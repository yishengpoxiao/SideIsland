using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Globalization;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

// Offline only. Exact queue-generation checks, no window/process/ETW APIs.
internal static class AnalyzePostedCalibration
{
    private static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=8*1024*1024,RecursionLimit=100};
    private static string Project;
    private static Dictionary<string,object> O(object value){return (Dictionary<string,object>)value;}
    private static object G(Dictionary<string,object> row,string key){object value;return row.TryGetValue(key,out value)?value:null;}
    private static string S(Dictionary<string,object> row,string key){object value=G(row,key);return value==null?"":Convert.ToString(value,CultureInfo.InvariantCulture);}
    private static bool B(Dictionary<string,object> row,string key){object value=G(row,key);return value!=null&&Convert.ToBoolean(value,CultureInfo.InvariantCulture);}
    private static ulong U(object value)
    {
        if(value==null||value is double||value is float)throw new InvalidDataException("Missing/floating integer rejected");
        if(value is string){string text=(string)value;return text.StartsWith("0x",StringComparison.OrdinalIgnoreCase)?UInt64.Parse(text.Substring(2),NumberStyles.HexNumber,CultureInfo.InvariantCulture):UInt64.Parse(text,CultureInfo.InvariantCulture);}
        decimal number=Convert.ToDecimal(value,CultureInfo.InvariantCulture);if(number<0||number>UInt64.MaxValue||Decimal.Truncate(number)!=number)throw new InvalidDataException("Non-integral integer");return (ulong)number;
    }
    private static long N(Dictionary<string,object> row,string key){return checked((long)U(G(row,key)));}
    private static ulong P(Dictionary<string,object> row,string key){return U(G(row,key));}
    private static Dictionary<string,object> D(Dictionary<string,object> row){return O(G(row,"Detail"));}
    private static Dictionary<string,object> Payload(Dictionary<string,object> row){return O(G(row,"Payload"));}
    private static void R(bool condition,string why){if(!condition)throw new InvalidDataException(why);}
    private static string Inside(string path){string full=Path.GetFullPath(path);R(full.StartsWith(Project+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase),"Only project paths allowed");return full;}
    private static List<Dictionary<string,object>> Lines(string path){return File.ReadLines(path).Where(x=>!String.IsNullOrWhiteSpace(x)).Select(x=>O(Json.DeserializeObject(x))).ToList();}
    private static Dictionary<string,object> Read(string path){return O(Json.DeserializeObject(File.ReadAllText(path)));}
    private static string Hash(string path){using(var stream=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");}
    private static string H(ulong value){return "0x"+value.ToString("X",CultureInfo.InvariantCulture);}
    private static Dictionary<string,object> One(IEnumerable<Dictionary<string,object>> source,string why){var rows=source.ToArray();R(rows.Length==1,why+"; count="+rows.Length);return rows[0];}
    private static bool Queue(Dictionary<string,object> row){return S(row,"Event")=="OWN_THREAD_MESSAGE_ORIGIN"&&S(D(row),"Event")=="OWN_THREAD_GETMESSAGE";}
    private static bool SameTarget(Dictionary<string,object> row,ulong hwnd,long pid,long tid){return P(row,"TargetHwnd")==hwnd&&N(row,"TargetProcessId")==pid&&N(row,"TargetThreadId")==tid;}
    private static object Route(Dictionary<string,object> row){var p=Payload(row);return new {EventId=N(row,"EventId"),Qpc=N(row,"Qpc"),HeaderProcessId=N(row,"HeaderProcessId"),HeaderThreadId=N(row,"HeaderThreadId"),TargetHwnd=S(row,"TargetHwnd"),PqmsgHex=H(P(p,"pqmsg")),PqmsgDecimal=P(p,"pqmsg").ToString(CultureInfo.InvariantCulture),InputReadyTimeMs=N(p,"inputReadyTimeMs"),CallbackCount=N(p,"CallbackCount"),Flags=G(p,"flags")};}
    private static int Main(string[] args)
    {
        try
        {
            Project=Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if(args.Length==1&&args[0]=="--self-check"){R(U("18446705635987972800")==18446705635987972800UL,"exact UInt64");bool rejected=false;try{U((double)1);}catch(InvalidDataException){rejected=true;}R(rejected,"reject floating pointer");Console.WriteLine("SELF-CHECK PASS offline exact integers; no UI/process/hooks/session APIs");return 0;}
            R(args.Length==2,"Usage: AnalyzePostedCalibration.exe <project calibration run> <fresh output basename>");
            string run=Inside(args[0]),output=Inside(args[1]);R(!File.Exists(output+".json")&&!File.Exists(output+".md"),"Fresh output required");
            string nativePath=Path.Combine(run,"receiver","events.jsonl"),etwPath=Path.Combine(run,"etw","events.jsonl"),qualityPath=Path.Combine(run,"etw","summary.json"),senderPath=Path.Combine(run,"receiver","sender-post-sc.json");
            var native=Lines(nativePath);var etw=Lines(etwPath);var quality=Read(qualityPath);var expected=Read(senderPath);
            foreach(string field in new[]{"EventsLost","CallbackErrors","WatchParseErrors","ExpiredSpans","AmbiguousSpanStops"})R(N(quality,field)==0,"ETW quality "+field);
            R(B(quality,"CleanupConfirmed")&&!B(quality,"DataLossDetected")&&!B(quality,"LogLimitReached"),"ETW cleanup/bounds");
            var summary=One(native.Where(x=>S(x,"Event")=="MESSAGE_ORIGIN_OBSERVER_SUMMARY"),"observer summary");
            foreach(string field in new[]{"CallbackErrors","CleanupErrors"})R(N(D(summary),field)==0,"Observer "+field);
            R(B(D(summary),"OnlyOwnThread")&&B(D(summary),"OwnHwndPredicate")&&B(D(summary),"NoMessagesChanged"),"observer scope contract");
            var queues=native.Where(Queue).OrderBy(x=>N(x,"Qpc")).ToArray();R(queues.Length==N(D(summary),"QueueRecords"),"queue saved count");
            long previousQpc=-1,previousOrdinal=0;
            foreach(var row in queues){var d=D(row);R(N(row,"Qpc")==N(d,"Qpc")&&N(row,"ReceiverProcessId")==N(d,"ReceiverProcessId")&&N(row,"CurrentThreadId")==N(d,"CurrentThreadId"),"wrapper identity/QPC drift");R(N(d,"QueueOrdinal")==previousOrdinal+1&&N(row,"Qpc")>previousQpc,"queue ordinal/QPC gap or order");previousOrdinal=N(d,"QueueOrdinal");previousQpc=N(row,"Qpc");}
            var sc=One(native.Where(x=>S(x,"Event")=="WM"&&S(x,"Context")=="CONTROLLED/post-sc"&&N(D(x),"Message")==0x112&&P(D(x),"W")==0xF020),"native PostSC entry");
            ulong hwnd=P(D(sc),"Hwnd");long receiver=N(sc,"ReceiverProcessId"),thread=N(sc,"CurrentThreadId"),scQpc=N(sc,"Qpc");
            var q=One(queues.Where(x=>S(x,"Context")=="CONTROLLED/post-sc"&&G(D(x),"Message")!=null&&N(D(x),"Message")==0x112&&P(D(x),"Hwnd")==hwnd&&P(D(x),"W")==P(D(sc),"W")&&P(D(x),"L")==P(D(sc),"L")),"actual queued SC tuple");
            long qOrdinal=N(D(q),"QueueOrdinal"),qQpc=N(q,"Qpc"),msgTime=N(D(q),"MessageTimeMs");
            var previous=One(queues.Where(x=>N(D(x),"QueueOrdinal")==qOrdinal-1),"previous queue boundary");var next=One(queues.Where(x=>N(D(x),"QueueOrdinal")==qOrdinal+1),"next queue boundary");
            R(N(D(q),"QueueDisposition")==1&&N(q,"ReceiverProcessId")==receiver&&N(q,"CurrentThreadId")==thread,"actual SC remove/thread");
            R(N(previous,"Qpc")<qQpc&&qQpc<scQpc&&scQpc<N(next,"Qpc"),"native SC not between dequeued SC and next owned dequeue");
            var routes=etw.Where(x=>S(x,"Kind")=="owned-queue-routing"&&SameTarget(x,hwnd,receiver,thread)).ToArray();
            var dequeue=One(routes.Where(x=>N(x,"EventId")==61&&N(x,"HeaderProcessId")==receiver&&N(x,"HeaderThreadId")==thread&&N(x,"Qpc")>N(previous,"Qpc")&&N(x,"Qpc")<=qQpc),"sole retrieve in adjacent own callback interval");
            var dp=Payload(dequeue);R(N(dp,"inputReadyTimeMs")==msgTime&&N(dp,"flags")==1,"actual MSG.time/remove does not match unique retrieve");ulong pointer=P(dp,"pqmsg");
            long consumeBoundary=routes.Where(x=>N(x,"EventId")==61&&P(Payload(x),"pqmsg")==pointer&&N(Payload(x),"flags")==1&&N(x,"Qpc")<N(dequeue,"Qpc")).Select(x=>N(x,"Qpc")).DefaultIfEmpty(0).Max();
            var post=One(routes.Where(x=>N(x,"EventId")==59&&P(Payload(x),"pqmsg")==pointer&&N(x,"Qpc")>consumeBoundary&&N(x,"Qpc")<=N(dequeue,"Qpc")),"unique unconsumed post generation");
            R(N(Payload(post),"inputReadyTimeMs")==msgTime,"post timestamp generation mismatch");
            R(B(expected,"Controlled")&&S(expected,"Command")=="post-sc"&&N(expected,"NativeReturn")==1&&N(expected,"ReceiverProcessId")==receiver&&P(expected,"ReceiverHwnd")==hwnd,"known sender target/control contract");
            R(N(post,"HeaderProcessId")==N(expected,"SenderProcessId")&&N(post,"HeaderThreadId")==N(expected,"SenderThreadId"),"queue source differs from known child sender");
            var dispatchStarts=etw.Where(x=>S(x,"Kind")=="owned-receiver-thread-dispatch-start"&&N(x,"HeaderProcessId")==receiver&&N(x,"HeaderThreadId")==thread).ToArray();
            var bracketed=new List<object>();
            foreach(var start in dispatchStarts)
            {
                var stops=etw.Where(x=>S(x,"Kind")=="owned-receiver-thread-dispatch-stop"&&N(x,"DispatchSpanId")==N(start,"DispatchSpanId")&&N(x,"HeaderProcessId")==receiver&&N(x,"HeaderThreadId")==thread&&N(start,"Qpc")<=scQpc&&N(x,"Qpc")>=scQpc).ToArray();
                if(stops.Length==1)bracketed.Add(new {SpanId=N(start,"DispatchSpanId"),StartQpc=N(start,"Qpc"),EndQpc=N(stops[0],"Qpc")});
            }
            long futureReuse=routes.Count(x=>N(x,"EventId")==59&&P(Payload(x),"pqmsg")==pointer&&N(x,"Qpc")>scQpc);
            var files=new[]{nativePath,etwPath,qualityPath,senderPath};
            long? callErrors=G(D(summary),"CallPairingErrors")==null?(long?)null:N(D(summary),"CallPairingErrors"),activeCalls=G(D(summary),"ActiveCallSpans")==null?(long?)null:N(D(summary),"ActiveCallSpans");
            var report=new {PassedControlledQueueJoin=true,HistoricalSenderProven=false,UncontrolledNativeCausalityProven=false,KnownSenderVerified=true,ReceiverProcessId=receiver,ReceiverThreadId=thread,TargetHwnd=H(hwnd),SenderProcessId=N(post,"HeaderProcessId"),SenderThreadId=N(post,"HeaderThreadId"),NativeSCQpc=scQpc,QueuedSC=q,PreviousOwnCallback=previous,NextOwnCallback=next,ExactPost=Route(post),ExactRetrieve=Route(dequeue),PriorConsumedPointerBoundaryQpc=consumeBoundary,RejectedLaterPointerReuseGenerations=futureReuse,QueueRecords=queues.Length,ObserverCallPairingErrors=callErrors,ObserverActiveCallSpans=activeCalls,ObserverCallLifoEvidenceUsable=callErrors.HasValue&&activeCalls.HasValue&&callErrors.Value==0&&activeCalls.Value==0,DispatchNativeSCBracketConfirmed=bracketed.Count==1,DispatchNativeSCBrackets=bracketed,DispatchProviderStarts=N(O(G(quality,"EventCounts")),"92"),DispatchProviderStops=N(O(G(quality,"EventCounts")),"93"),OwnedDispatchStarts=dispatchStarts.Length,ScopeDiagnostics=etw.Where(x=>S(x,"Kind")=="owned-dispatch-scope-diagnostic").ToArray(),InputHashes=files.Select(path=>new {Path=path,SHA256=Hash(path)}).ToArray(),Meaning="Known PostSC control uniquely joins post generation to retrieve, actual WH_GETMESSAGE SC tuple/time/remove, and matching later native SC before next dequeue. A missing dispatch bracket is not invented; this does not identify the historical sender or prove arbitrary nested-call causality. Call-hook LIFO quality is reported independently and never implied by queue-join success."};
            File.WriteAllText(output+".json",Json.Serialize(report),new UTF8Encoding(false));
            File.WriteAllText(output+".md","# Controlled posted-message queue join\r\n\r\nKnown sender "+N(post,"HeaderProcessId")+"/"+N(post,"HeaderThreadId")+" uniquely matches post "+N(post,"Qpc")+" → retrieve "+N(dequeue,"Qpc")+" → actual queued SC "+qQpc+" (ordinal "+qOrdinal+", MSG.time "+msgTime+") → native SC "+scQpc+" before next dequeue "+N(next,"Qpc")+". Exact pointer "+H(pointer)+"; "+futureReuse+" later reuse generations excluded.\r\n\r\nDispatch bracket confirmed: "+(bracketed.Count==1)+". Provider start/stop counts do not establish receiver coverage. Historical sender remains unknown; uncontrolled nested-call causality is not proven by this control. See JSON for strict checks and input hashes.\r\n",new UTF8Encoding(false));
            Console.WriteLine("PASS controlled exact queue-generation join; sender="+N(post,"HeaderProcessId")+"; dispatch-bracket="+(bracketed.Count==1)+"; historical-sender=false");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
