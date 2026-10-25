using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

// Non-UI native algorithm proof. Loads only the exact matched local framework
// DLL and uses self-owned 16-byte ptrset storage. Fake pointer values are only
// compared by these internal methods and are never dereferenced or executed.
internal static class NativePtrMultisetProof
{
    private const string ExpectedHash = "35C31A7E813D042CC82207D9264E3C3FFA5A95D548BAC6762422FEE58FEDBFBC";
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int AddFunction(IntPtr self, UIntPtr pointer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool RemoveFunction(IntPtr self, UIntPtr pointer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ClearFunction(IntPtr self);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static StreamWriter Log;
    private static int Step;

    private static int Main(string[] args)
    {
        IntPtr module = IntPtr.Zero, self = IntPtr.Zero; ClearFunction clear = null;
        try
        {
            if (args.Length < 1 || args.Length > 2 || !Environment.Is64BitProcess || (args.Length == 2 && args[1] != "--clearflag-control")) throw new ArgumentException("Usage: x64 NativePtrMultisetProof.exe <project-output-directory> [--clearflag-control]");
            bool control = args.Length == 2;
            string root = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd('\\') + "\\", output = Path.GetFullPath(args[0]);
            if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Evidence must remain inside project.");
            Directory.CreateDirectory(output);
            string image = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "WPF", "wpfgfx_v0400.dll");
            string hash; using (var sha=SHA256.Create()) using (var file=File.OpenRead(image)) hash=BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
            if (hash != ExpectedHash) throw new InvalidOperationException("Image does not match PDB/PE-validated RVA identity; no native calls made.");
            module=LoadLibrary(image); if (module==IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            var add=(AddFunction)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(module,0xFD3F8),typeof(AddFunction));
            var remove=(RemoveFunction)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(module,0xFD6C0),typeof(RemoveFunction));
            clear=(ClearFunction)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(module,0xB42A0),typeof(ClearFunction));
            self=Marshal.AllocHGlobal(16); for(int offset=0;offset<16;offset++) Marshal.WriteByte(self,offset,0);
            using(Log=new StreamWriter(Path.Combine(output,"native-ptrmultiset-sequence.jsonl"),false,new UTF8Encoding(false)))
            {
                Snapshot(self,"initial",0,null);
                for(uint value=4;value<=860;value+=4) { int hr=add(self,new UIntPtr(value)); Snapshot(self,"Add",value,hr); if(hr<0) throw new InvalidOperationException("Native Add failed."); }
                for(uint value=4;value<=260;value+=4) { bool result=remove(self,new UIntPtr(value)); Snapshot(self,"Remove",value,result); if(!result) throw new InvalidOperationException("Initial legal native removals failed."); }
                var beforeSwap=Snapshot(self,"before-linear-swap",0,null);
                bool swapRemoval=remove(self,new UIntPtr(264)); var afterSwap=Snapshot(self,"Remove-linear-swap",264,swapRemoval);
                if (control)
                {
                    // Counterfactual intervention only in this proof's own
                    // allocated collection state; DLL code is never changed.
                    ulong ownData=unchecked((ulong)Marshal.ReadInt64(self));
                    IntPtr ownArray=new IntPtr(unchecked((long)(ownData&~3UL)));
                    ulong ownMetadata=unchecked((ulong)Marshal.ReadInt64(ownArray,16));
                    Marshal.WriteInt64(ownArray,16,unchecked((long)(ownMetadata&~0x80000000UL)));
                    Snapshot(self,"CONTROL-clear-owned-sorted-flag",0,null);
                }
                int firstAdd=add(self,new UIntPtr(864)); Snapshot(self,"Add-after-swap",864,firstAdd);
                int secondAdd=add(self,new UIntPtr(868)); var beforeFinal=Snapshot(self,"Add-after-swap",868,secondAdd);
                bool finalRemoval=remove(self,new UIntPtr(860)); var afterFinal=Snapshot(self,"Remove-final-real-native-call",860,finalRemoval);
                bool residual=(bool)afterFinal["Contains860"], incorrectSorted=(bool)beforeFinal["SortedFlag"] && !(bool)beforeFinal["ActuallySorted"];
                var summary=new Dictionary<string,object> {
                    {"Kind","actual-local-framework-native-algorithm-proof"},{"ControlledAlgorithmInput",true},{"ProductCrashReproduction",false},{"UsesProductionFrameworkImplementation",true},{"UIAccessed",false},{"DebuggerAttached",false},{"SystemPolicyChanged",false},{"ControlClearedOwnedSortedFlag",control},{"DLLModified",false},
                    {"Image",image},{"ImageSHA256",hash},{"AddRva","0xFD3F8"},{"RemoveRva","0xFD6C0"},{"ClearRva","0xB42A0"},{"SelfOwnedBytes",16},{"FakePointerValuesNeverDereferenced",true},
                    {"BeforeSwap",beforeSwap},{"AfterSwap",afterSwap},{"BeforeFinalRemoval",beforeFinal},{"AfterFinalRemoval",afterFinal},
                    {"FinalRemoveReturned",finalRemoval},{"ResidualTargetStillInSet",residual},{"StaleSortedFlagBeforeFinalRemoval",incorrectSorted},{"NativeAlgorithmDefectReproduced",!finalRemoval && residual && incorrectSorted},{"OwnedFlagControlRecoveredRemoval",control && finalRemoval && !residual},
                    {"Meaning","Legal Add/Remove sequence on the exact installed framework implementation proves stale sorted-flag can make Remove return false while a matching pointer remains. This separate non-UI proof does not itself reproduce the WPF product crash."}
                };
                File.WriteAllText(Path.Combine(output,"native-ptrmultiset-proof.json"),Json.Serialize(summary),new UTF8Encoding(false));
                Console.WriteLine("NativeAlgorithmDefectReproduced="+(!finalRemoval && residual && incorrectSorted)+"; control="+control+"; finalRemove="+finalRemoval+"; residual860="+residual);
                return control ? (finalRemoval && !residual ? 0 : 1) : (!finalRemoval && residual && incorrectSorted ? 0 : 1);
            }
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 2; }
        finally
        {
            if(self!=IntPtr.Zero) { if(clear!=null) clear(self); Marshal.FreeHGlobal(self); }
            if(module!=IntPtr.Zero) FreeLibrary(module);
        }
    }

    private static Dictionary<string,object> Snapshot(IntPtr self,string operation,uint argument,object result)
    {
        ulong data=unchecked((ulong)Marshal.ReadInt64(self)); ulong count=data&1,capacity=count,metadata=0; var values=new List<ulong>();
        if((data&2)!=0) {
            IntPtr array=new IntPtr(unchecked((long)(data&~3UL))); count=unchecked((ulong)Marshal.ReadInt64(array)); capacity=unchecked((ulong)Marshal.ReadInt64(array,8)); metadata=unchecked((ulong)Marshal.ReadInt64(array,16));
            if(count>10000 || capacity<count) throw new InvalidDataException("Unexpected native array bounds.");
            for(int index=0;index<(int)count;index++) values.Add(unchecked((ulong)Marshal.ReadInt64(array,24+index*8)));
        } else if(count==1) values.Add(data&~3UL);
        bool sorted=true; for(int index=1;index<values.Count;index++) if(values[index]<values[index-1]) sorted=false;
        var record=new Dictionary<string,object> { {"Step",++Step},{"Operation",operation},{"Argument",argument},{"Result",result},{"DataPointer","0x"+data.ToString("X16")},{"UnsortedNewElements",Marshal.ReadInt32(self,8)},{"Count",count},{"Capacity",capacity},{"Metadata","0x"+metadata.ToString("X16")},{"SortedFlag",(metadata&0x80000000)!=0},{"ActuallySorted",sorted},{"RemovedCount",metadata&0x7fffffff},{"Contains860",values.Any(value=>value==860)},{"Elements",values.ToArray()} };
        Log.WriteLine(Json.Serialize(record)); Log.Flush(); return record;
    }
}
