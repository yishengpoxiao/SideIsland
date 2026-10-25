using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

// Non-UI filesystem contract test; no threads, windows, processes or ETW.
internal static class WatchSnapshotSharingProof
{
    private static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    private static object Case(string directory,bool deleteSharing)
    {
        Directory.CreateDirectory(directory);
        string target=Path.Combine(directory,"targets.json"),next=Path.Combine(directory,"targets.next.json");
        File.WriteAllText(target,"{\"Generation\":1}",new UTF8Encoding(false));File.WriteAllText(next,"{\"Generation\":2}",new UTF8Encoding(false));
        FileShare flags=deleteSharing?FileShare.ReadWrite|FileShare.Delete:FileShare.Read;
        bool replaced=false;int nativeError=0;string errorType=null,heldSnapshot;
        using(var file=new FileStream(target,FileMode.Open,FileAccess.Read,flags))
        {
            try{File.Replace(next,target,null);replaced=true;}
            catch(IOException error){nativeError=error.HResult&0xFFFF;errorType=error.GetType().FullName;}
            using(var reader=new StreamReader(file,Encoding.UTF8,true,4096))heldSnapshot=reader.ReadToEnd();
        }
        string namedSnapshot=File.ReadAllText(target);
        bool expected=deleteSharing?replaced&&nativeError==0&&heldSnapshot=="{\"Generation\":1}"&&namedSnapshot=="{\"Generation\":2}":!replaced&&nativeError==32&&heldSnapshot=="{\"Generation\":1}"&&namedSnapshot=="{\"Generation\":1}";
        return new {DeleteSharing=deleteSharing,FileShare=flags.ToString(),AtomicReplaceSucceeded=replaced,NativeError=nativeError,ErrorType=errorType,HeldHandleSnapshot=heldSnapshot,NamedPathSnapshot=namedSnapshot,ExpectedContractObserved=expected};
    }
    private static int Main(string[] args)
    {
        if(args.Length!=1)return 2;
        string project=Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar),output=Path.GetFullPath(args[0]);
        if(!output.StartsWith(project+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||Directory.Exists(output)||File.Exists(output))return 3;
        Directory.CreateDirectory(output);
        object oldCase=Case(Path.Combine(output,"default-share"),false),fixedCase=Case(Path.Combine(output,"delete-share"),true);
        var results=new {UiExecuted=false,EtwExecuted=false,NoOtherFilesModified=true,AutomaticRetries=0,OldReader=oldCase,FixedReader=fixedCase,HistoricalToolErrorCauseProven=false};
        string json=Json.Serialize(results);File.WriteAllText(Path.Combine(output,"proof.json"),json,new UTF8Encoding(false));Console.WriteLine(json);
        var oldMap=(System.Collections.Generic.Dictionary<string,object>)Json.DeserializeObject(Json.Serialize(oldCase));var newMap=(System.Collections.Generic.Dictionary<string,object>)Json.DeserializeObject(Json.Serialize(fixedCase));
        return (bool)oldMap["ExpectedContractObserved"]&&(bool)newMap["ExpectedContractObserved"]?0:1;
    }
}
