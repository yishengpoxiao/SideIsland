using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

internal static class Win32kAttributionCollector
{
    private static readonly Guid Provider = new Guid("8C416C79-D49B-4F01-A467-E56D3AA8234C");
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
    private static readonly int[] EventIds = { 1, 2, 59, 60, 61, 62, 63, 70, 83, 88, 92, 93, 203, 204 };
    private static readonly Dictionary<string,List<Span>> ActiveSpans = new Dictionary<string,List<Span>>();
    private static string Project, Output;
    private static readonly object Gate = new object();
    private static readonly Stopwatch Clock = new Stopwatch();
    private static readonly Dictionary<int,long> SeenIds = new Dictionary<int,long>();
    private static readonly HashSet<string> Schemas = new HashSet<string>();
    private static readonly Dictionary<ulong,Target> Known = new Dictionary<ulong,Target>();
    private static volatile Target[] Targets = new Target[0];
    private static TraceEventSession Session;
    private static StreamWriter Log;
    private static Timer WatchTimer, DeadlineTimer;
    private static string WatchPath, OwnSession, StopReason, CleanupError;
    private static DateTime WatchWrite;
    private static bool StopByWatch, LogLimit, DiagnosticOwnedRaw;
    private static int Stopping, Reloading, Seconds, EventsLostBeforeStop, SourceReportedEventsLost;
    private static string LostCounterReadError;
    private static long Seen, Matched, NoTarget, NonMinimize, MissingHwnd, MissingMessage, RejectedOwner, BufferedOwner, CallbackErrors, WatchErrors, TargetUpdates, Lines, Bytes, IdentityLookups, Unhandled;
    private static long MissingMessageDiagnostics, MinimizeMessageRecords;
    private static long SpanSequence, SendSpanStarts, ReceiveSpanStarts, MappedSpanStops, UnmappedSpanStops, AmbiguousSpanStops, ExpiredSpans;
    private static int ActiveSpanCount;
    private static readonly Dictionary<string,List<DispatchSpan>> DispatchSpans = new Dictionary<string,List<DispatchSpan>>();
    private static long DispatchSequence,DispatchStarts,DispatchStops,UnmatchedDispatchStops;
    private static long DispatchOwnPidHeaders,DispatchOwnTidHeaders,DispatchZeroPidHeaders,DispatchZeroTidHeaders,DispatchFilteredOwnStarts,DispatchScopeDiagnostics;
    private static int ActiveDispatchCount;
    private const long MaximumLogBytes = 64L * 1024 * 1024;
    private const int MaximumLogLines = 100000;
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            Project = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (args.Length == 1 && args[0] == "--self-check")
            {
                if (EventIds.Length != 14 || typeof(TraceEventProviderOptions).GetProperty("EventIDsToEnable") == null || !AllowedMessage(0x112,0xF020) || AllowedMessage(0x102,65)) throw new InvalidOperationException("ETW contract missing.");
                Console.WriteLine("SELF-CHECK PASS UI=false SessionCreated=false EventIDs=1,2,59,60,61,62,63,70,83,88,92,93,203,204; WM_CHAR rejected; owned starts map numeric stop spans"); return 0;
            }
            if(args.Length==3 && args[0]=="--log-byte-self-check" && args[1]=="--output")
            { Output=Fresh(args[2]);return LogByteSelfCheck(); }
            bool preflight = args.Length > 0 && args[0] == "--preflight";
            if (!preflight) return Watch(args);
            if (args.Length != 1 && !(args.Length == 3 && args[1] == "--output")) throw new ArgumentException("Use --preflight [--output fresh-project-directory].");
            Output = Fresh(args.Length == 3 ? args[2] : Path.Combine(Project, "artifacts", "minimize-attribution", "preflight-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")));
            return Preflight();
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 3; }
    }

    private static int LogByteSelfCheck()
    {
        string path=Path.Combine(Output,"synthetic-log.jsonl");
        Log=new StreamWriter(new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.ReadWrite),new UTF8Encoding(false)){AutoFlush=true};
        try
        {
            // Exercise the same Write()/serializer/counter as the collector.
            Write(new {Kind="synthetic-byte-positive",Text="ASCII and Chinese \u4e2d\u6587 plus UTF8 \ud83d\ude00"});
            Write(new {Kind="synthetic-byte-positive",Text="escaped\r\ncontent\tand slash\\"});
        }
        finally{Log.Dispose();Log=null;}
        long actual=new FileInfo(path).Length;int actualLines=File.ReadAllLines(path).Length;
        bool passed=actual==Bytes && actualLines==Lines && Lines==2;
        var proof=new {Passed=passed,UiExecuted=false,EtwSessionCreated=false,NativeInputOrWindowApisUsed=false,SyntheticContentOnly=true,SameProductionWriteMethod=true,ReportedBytes=Bytes,ActualFileBytes=actual,ReportedLines=Lines,ActualLines=actualLines,EnvironmentNewLineBytes=Encoding.UTF8.GetByteCount(Environment.NewLine),Utf8NoBom=true,HistoricalMinimizeCauseProven=false};
        Save("proof.json",proof);Console.WriteLine(Json.Serialize(proof));return passed?0:3;
    }
    private static int Preflight()
    {
        string name = "CodexIsland.MinimizeAttrib." + Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N");
        var record = new PreflightResult { SessionName = name, StartedUtc = DateTime.UtcNow.ToString("o"), Elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator) };
        TraceEventSession session = null;
        try
        {
            session = new TraceEventSession(name, TraceEventSessionOptions.Create); session.StopOnDispose = true;
            bool restarted = session.EnableProvider(Provider, TraceEventLevel.Informational, 0x401000UL, new TraceEventProviderOptions { EventIDsToEnable = EventIds });
            if (restarted) throw new InvalidOperationException("GUID session unexpectedly reused an existing name.");
            record.Started = session.IsActive; record.RealTime = session.IsRealTime;
        }
        catch (Exception error) { record.ErrorType = error.GetType().FullName; record.HResult = "0x" + error.HResult.ToString("X8"); record.NativeError = NativeError(error); record.AccessDenied = record.NativeError == 5 || error.HResult == unchecked((int)0x80070005); }
        finally
        {
            if (session != null) { try { session.Dispose(); record.DisposeCompleted = true; } catch (Exception error) { record.CleanupError = error.GetType().FullName + ":0x" + error.HResult.ToString("X8"); } }
            record.EndedUtc = DateTime.UtcNow.ToString("o");
            File.WriteAllText(Path.Combine(Output, "preflight.json"), Json.Serialize(record), new UTF8Encoding(false));
            Console.WriteLine(Json.Serialize(record));
        }
        return record.Started && record.DisposeCompleted && record.CleanupError == null ? 0 : record.AccessDenied ? 2 : 3;
    }
    private static int NativeError(Exception error) { while (error != null) { var native = error as Win32Exception; if (native != null) return native.NativeErrorCode; error = error.InnerException; } return 0; }
    private static string Fresh(string value)
    {
        string path = Path.GetFullPath(value);
        if (!path.StartsWith(Project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must be inside the current project.");
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Fresh output directory required; nothing overwritten.");
        Directory.CreateDirectory(path); return path;
    }
    private static int Watch(string[] args)
    {
        string requestedOutput = null; Seconds = 120;
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new ArgumentException("Every watch option requires a value.");
            if (args[i] == "--watch") WatchPath = Inside(args[i+1]);
            else if (args[i] == "--output") requestedOutput = args[i+1];
            else if (args[i] == "--seconds") Seconds = Int32.Parse(args[i+1]);
            else if (args[i] == "--diagnostic-owned-raw") DiagnosticOwnedRaw = args[i+1] == "1";
            else throw new ArgumentException("Use --watch project-targets.json --output fresh-project-directory --seconds 1..900.");
        }
        if (WatchPath == null || requestedOutput == null || Seconds < 1 || Seconds > 900) throw new ArgumentException("Watch/output and seconds 1..900 are required.");
        Output = Fresh(requestedOutput); OwnSession = "CodexIsland.MinimizeAttrib." + Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N");
        string errorType = null, errorCode = null; bool started = false, processingReturned = false, cleanupConfirmed = false;
        ConsoleCancelEventHandler cancel = delegate(object sender, ConsoleCancelEventArgs value) { value.Cancel = true; StopOwn("console-cancel"); };
        Console.CancelKeyPress += cancel; Clock.Start();
        try
        {
            Log = new StreamWriter(Path.Combine(Output,"events.jsonl"),false,new UTF8Encoding(false),65536) { AutoFlush = true };
            Session = new TraceEventSession(OwnSession,TraceEventSessionOptions.Create); Session.StopOnDispose = true;
            Session.BufferSizeMB = 32;
            bool restarted = Session.EnableProvider(Provider,TraceEventLevel.Informational,0xC03000UL,new TraceEventProviderOptions { EventIDsToEnable = EventIds });
            if (restarted) throw new InvalidOperationException("Unique session was unexpectedly restarted.");
            started = Session.IsActive;
            var source = Session.Source;
            source.Registered.All += OnEvent;
            source.UnhandledEvents += delegate(TraceEvent e) { if (e.ProviderGuid == Provider) Interlocked.Increment(ref Unhandled); };
            ReloadTargets(null);
            WatchTimer = new Timer(ReloadTargets,null,100,100);
            DeadlineTimer = new Timer(delegate { StopOwn("bounded-deadline"); },null,Seconds * 1000,Timeout.Infinite);
            Save("ready.json",new { StartedUtc=DateTime.UtcNow.ToString("o"),SessionName=OwnSession,ProcessId=Process.GetCurrentProcess().Id,RealTime=Session.IsRealTime,
                EtlWritten=false,WatchFile=WatchPath,EventIDs=EventIds,Keywords="0xC03000",TargetCount=Targets.Length,Seconds=Seconds,
                Interpretation="Header PID/TID is event-emission context, not an assumed sender; receiver fields remain separately named." });
            Console.WriteLine("READY session="+OwnSession+" no-ETL=true output="+Output); Console.Out.Flush();
            source.Process(); processingReturned = true;
            try { SourceReportedEventsLost=source.EventsLost; } catch(Exception error) { LostCounterReadError=error.GetType().Name; }
            if (Stopping == 0) StopOwn("consumer-returned");
        }
        catch (Exception error)
        {
            errorType = error.GetType().FullName; errorCode = "0x"+error.HResult.ToString("X8");
            Console.Error.WriteLine(errorType+" "+errorCode+" NativeError="+NativeError(error));
        }
        finally
        {
            if (WatchTimer != null) WatchTimer.Dispose(); if (DeadlineTimer != null) DeadlineTimer.Dispose();
            StopOwn(StopReason ?? "finally");
            // Dispose is idempotent and only concerns this exact GUID-named session.
            if (Session != null) { try { Session.Dispose(); } catch (Exception error) { CleanupError = error.GetType().FullName+":0x"+error.HResult.ToString("X8"); } }
            try { cleanupConfirmed = !TraceEventSession.GetActiveSessionNames().Contains(OwnSession); } catch (Exception error) { CleanupError = (CleanupError ?? "")+" query:"+error.GetType().Name; }
            if (!cleanupConfirmed && started) cleanupConfirmed = FallbackStopOwn();
            Console.CancelKeyPress -= cancel;
            if (Log != null) { lock(Gate) { Log.Dispose(); Log=null; } }
            Save("summary.json",new { Kind="bounded-realtime-owned-HWND-win32k-attribution",SessionName=OwnSession,Started=started,ProcessingReturned=processingReturned,
                CleanupConfirmed=cleanupConfirmed,CleanupError=CleanupError,ErrorType=errorType,HResult=errorCode,StopReason=StopReason,StopRequestedByWatch=StopByWatch,
                EtlWritten=false,UiExecuted=false,GlobalKeyboardHook=false,OtherSessionStopped=false,EventIDs=EventIds,SecondsRequested=Seconds,ElapsedMilliseconds=Clock.ElapsedMilliseconds,
                EventsSeen=Seen,MatchedRecords=Matched,EventsWithoutWatchedTarget=NoTarget,IgnoredNonMinimizeMessages=NonMinimize,MissingHwnd=MissingHwnd,
                MissingOrZeroMessage=MissingMessage,RejectedOwner=RejectedOwner,BufferedOwnershipMatches=BufferedOwner,CallbackErrors=CallbackErrors,WatchParseErrors=WatchErrors,
                TargetUpdates=TargetUpdates,UnhandledProviderEvents=Unhandled,EventsLost=Math.Max(EventsLostBeforeStop,SourceReportedEventsLost),SessionEventsLostBeforeStop=EventsLostBeforeStop,SourceReportedEventsLost=SourceReportedEventsLost,LostCounterReadError=LostCounterReadError,LogLimitReached=LogLimit,LogLines=Lines,LogBytes=Bytes,IdentityLookups=IdentityLookups,
                DiagnosticOwnedRawEnabled=DiagnosticOwnedRaw,MissingMessageDiagnosticRecords=MissingMessageDiagnostics,WhitelistedMessageRecords=MinimizeMessageRecords,
                SendSpanStarts=SendSpanStarts,ReceiveSpanStarts=ReceiveSpanStarts,MappedSpanStops=MappedSpanStops,UnmappedSpanStops=UnmappedSpanStops,AmbiguousSpanStops=AmbiguousSpanStops,ActiveSpansAtStop=ActiveSpanCount,ExpiredSpans=ExpiredSpans,
                DispatchSpanStarts=DispatchStarts,DispatchSpanStops=DispatchStops,UnmatchedDispatchStops=UnmatchedDispatchStops,ActiveDispatchSpansAtStop=ActiveDispatchCount,DispatchCoverageObserved=DispatchStarts>0 && DispatchStops>0,DispatchCoverageIncomplete=DispatchStarts==0 || ActiveDispatchCount!=0 || UnmatchedDispatchStops!=0,QueueRoutingRecordsKeptWhenMessageRedacted=true,
                DispatchOwnPidHeaders=DispatchOwnPidHeaders,DispatchOwnTidHeaders=DispatchOwnTidHeaders,DispatchZeroPidHeaders=DispatchZeroPidHeaders,DispatchZeroTidHeaders=DispatchZeroTidHeaders,DispatchFilteredOwnStarts=DispatchFilteredOwnStarts,DispatchScopeDiagnosticRecords=DispatchScopeDiagnostics,DispatchMessageIdentifiersOnly=true,DispatchInputParametersLogged=false,
                EventCounts=SeenIds.ToDictionary(pair=>pair.Key.ToString(),pair=>pair.Value),SchemaCount=Schemas.Count,DataLossDetected=EventsLostBeforeStop!=0 || SourceReportedEventsLost!=0 || CallbackErrors!=0 || LogLimit || LostCounterReadError!=null || ExpiredSpans!=0,
                WatchContract="Version1, Targets:[{Hwnd:hex,ProcessId,ProcessStartUtc:ISO,Role,ThreadId optional}]; atomic replace; Stop:true ends this collector gracefully.",
                AttributionNotAutomaticallyProven=true });
            Console.WriteLine("STOPPED cleanup-confirmed="+cleanupConfirmed+" matched="+Matched+" lost="+EventsLostBeforeStop+" output="+Output);
        }
        return errorType == null && cleanupConfirmed && EventsLostBeforeStop == 0 && SourceReportedEventsLost==0 && LostCounterReadError==null && CallbackErrors == 0 && !LogLimit && ExpiredSpans==0 ? 0 : 3;
    }

    private static void OnEvent(TraceEvent e)
    {
        // StopTrace flushes buffered events. Continue consuming until Process()
        // actually returns; a stop request is not permission to discard a tail.
        if (e.ProviderGuid != Provider) return;
        Interlocked.Increment(ref Seen); int id = (int)e.ID;
        lock(Gate)
        {
            long count; SeenIds.TryGetValue(id,out count); SeenIds[id]=count+1;
            string schema=id+"/"+e.Version+"/"+String.Join(",",e.PayloadNames ?? new string[0]);
            if (Schemas.Add(schema)) Write(new {Kind="schema",EventId=id,Version=e.Version,PayloadNames=e.PayloadNames});
        }
        try
        {
            if(id==92 || id==93) {TrackDispatch(e,id);return;}
            if(id==70 || id==83) {TrackStop(e,id);return;}
            Target match=null; ulong hwnd;
            if (id == 2)
            {
                ulong oldThread,newThread; if (!Number(e,"OldThreadId",out oldThread) || !Number(e,"NewThreadId",out newThread)) return;
                match=Targets.FirstOrDefault(t=>t.ThreadId==oldThread || t.ThreadId==newThread);
                if (match==null || !OwnedAtEvent(match,e.TimeStamp.ToUniversalTime())) { Interlocked.Increment(ref NoTarget); return; }
                Record(e,match,new Dictionary<string,object>{{"OldThreadId",oldThread},{"NewThreadId",newThread}},"owned-focus"); return;
            }
            if (!Number(e,"hwnd",out hwnd) && !Number(e,"Hwnd",out hwnd) && !Number(e,"hWnd",out hwnd)) {Interlocked.Increment(ref MissingHwnd);return;}
            match=Targets.FirstOrDefault(t=>t.Hwnd==hwnd);
            if (match==null) {Interlocked.Increment(ref NoTarget);return;}
            if (!OwnedAtEvent(match,e.TimeStamp.ToUniversalTime())) {Interlocked.Increment(ref RejectedOwner);return;}
            if(id==60 || id==62)TrackStart(e,match,id);
            if(id==59 || id==61 || id==63 || id==88)RecordQueueRoute(e,match,id);
            var fields=new Dictionary<string,object>();
            if (id==1) { Add(e,fields,"Type"); Record(e,match,fields,"owned-window-update");return; }
            if (id==203 || id==204) {foreach(string name in new[]{"pti","pqmsg","dwQEvent","flags"})Add(e,fields,name);Record(e,match,fields,"owned-queue-event");return;}
            ulong message,wp;
            if(!Number(e,"message",out message) || message==0) {Interlocked.Increment(ref MissingMessage);if(DiagnosticOwnedRaw)MissingMessageDiagnostic(e,match);return;}
            Number(e,"WParam",out wp);
            if(!AllowedMessage(message,wp)) {Interlocked.Increment(ref NonMinimize);return;}
            foreach(string name in new[]{"message","WParam","LParam","pqmsg","pidReceiver","tidReceiver","flags","inputReadyTimeMs","CallbackCount"})Add(e,fields,name);
            Interlocked.Increment(ref MinimizeMessageRecords);
            Record(e,match,fields,"owned-minimize-related-message");
        }
        catch {Interlocked.Increment(ref CallbackErrors);}
    }
    private static void RecordQueueRoute(TraceEvent e,Target target,int id)
    {
        // Keep routing even when Win32k deliberately redacts the message. No
        // WParam/LParam, text or unknown message IDs are written in this record.
        var fields=new Dictionary<string,object>();
        foreach(string name in new[]{"CallbackCount","inputReadyTimeMs","flags"})Add(e,fields,name);
        ulong pointer;if(Number(e,"pqmsg",out pointer))fields["pqmsg"]="0x"+pointer.ToString("X");
        fields["QueueStage"]=id==59?"post":id==61?"retrieve-post":id==88?"queue-input":"retrieve-input";
        fields["MessageIdentityKnown"]=false;
        fields["Meaning"]="Routing only; pair generation/lifetime and actual own-thread queue observations before attributing a message or sender.";
        Record(e,target,fields,"owned-queue-routing");
    }
    private static void TrackDispatch(TraceEvent e,int id)
    {
        ulong count,message;Number(e,"CallbackCount",out count);Number(e,"message",out message);
        bool ownPid=Targets.Any(t=>t.ProcessId==e.ProcessID),ownTid=Targets.Any(t=>t.ThreadId==e.ThreadID);
        if(ownPid)DispatchOwnPidHeaders++;if(ownTid)DispatchOwnTidHeaders++;if(e.ProcessID==0)DispatchZeroPidHeaders++;if(e.ThreadID==0)DispatchZeroTidHeaders++;
        if((ownPid || ownTid) && DispatchScopeDiagnostics<32)
        {
            DispatchScopeDiagnostics++;
            Write(new {Kind="owned-dispatch-scope-diagnostic",EventId=id,Qpc=e.TimeStampQPC,HeaderProcessId=e.ProcessID,HeaderThreadId=e.ThreadID,OwnedPidMatches=ownPid,OwnedTidMatches=ownTid,
                MessageTypeId=message,MessageTypeIdentifierOnly=true,NoMessageParameters=true,
                Interpretation="Scope diagnostic for own PID or TID only. The message type ID is metadata; no WParam/LParam, key values or content are serialized. A header match is not window dispatch proof."});
        }
        // Dispatch schemas have no HWND. Only receiver threads which currently
        // own a registered project HWND are eligible, and no key/text payload
        // is serialized. A thread match is not a target-window match.
        string key=e.ProcessID+"/"+e.ThreadID+"/"+count;
        List<DispatchSpan> stack;DispatchSpan span=null;
        if(id==92)
        {
            var candidates=Targets.Where(t=>t.ProcessId==e.ProcessID && t.ThreadId==e.ThreadID && OwnedAtEvent(t,e.TimeStamp.ToUniversalTime())).ToArray();
            if(candidates.Length==0){Interlocked.Increment(ref NoTarget);return;}
            // Own receiver-thread type identifiers are metadata, not WParam or
            // text/key content. Record all type IDs to avoid falsely dropping
            // relevant native dispatch paths before their meaning is calibrated.
            if(ActiveDispatchCount>=1024){LogLimit=true;ThreadPool.QueueUserWorkItem(delegate{StopOwn("bounded-dispatch-cache");});return;}
            span=new DispatchSpan{Id=++DispatchSequence,StartQpc=e.TimeStampQPC,CallbackCount=count,Message=message,Candidates=candidates};
            if(!DispatchSpans.TryGetValue(key,out stack)){stack=new List<DispatchSpan>();DispatchSpans.Add(key,stack);}
            stack.Add(span);ActiveDispatchCount++;DispatchStarts++;
        }
        else if(DispatchSpans.TryGetValue(key,out stack) && stack.Count>0)
        {
            span=stack[stack.Count-1];stack.RemoveAt(stack.Count-1);if(stack.Count==0)DispatchSpans.Remove(key);ActiveDispatchCount--;DispatchStops++;
        }
        if(span==null){if(Targets.Any(t=>t.ProcessId==e.ProcessID && t.ThreadId==e.ThreadID))UnmatchedDispatchStops++;return;}
        Write(new {Kind=id==92?"owned-receiver-thread-dispatch-start":"owned-receiver-thread-dispatch-stop",EventId=id,EventUtc=e.TimeStamp.ToUniversalTime().ToString("o"),Qpc=e.TimeStampQPC,
            HeaderProcessId=e.ProcessID,HeaderThreadId=e.ThreadID,DispatchSpanId=span.Id,CallbackCount=count,StartQpc=span.StartQpc,EndQpc=id==93?(long?)e.TimeStampQPC:null,
            MessageTypeId=message,StartMessage=span.Message,MessageTypeIdentifierOnly=true,NoMessageParameters=true,MessageChanged=message!=span.Message,TargetCandidates=span.Candidates.Select(t=>new {Hwnd="0x"+t.Hwnd.ToString("X"),ProcessId=t.ProcessId,ThreadId=t.ThreadId,ProcessStartUtc=t.StartUtc.ToString("o"),Role=t.Role}).ToArray(),
            Interpretation="Own receiver-thread dispatch interval only; HWND is absent from provider schema. Candidates are not asserted as the dispatched target. Native/queue entry must establish that identity."});
    }
    private static string SpanKey(string role,ulong pqmsg,int pid,int tid,ulong count)
    {return role+"/"+pqmsg.ToString("X")+"/"+pid+"/"+tid+"/"+count;}
    private static void TrackStart(TraceEvent e,Target target,int id)
    {
        ulong pqmsg,count; if(!Number(e,"pqmsg",out pqmsg) || pqmsg==0)return;Number(e,"CallbackCount",out count);
        // Routing metadata only. Never infer that a zero/redacted message was
        // SC_MINIMIZE; receiver WndProc QPC must prove that in later analysis.
        string role=id==60?"send":"receive";string key=SpanKey(role,pqmsg,e.ProcessID,e.ThreadID,count);
        foreach(var oldKey in ActiveSpans.Keys.ToArray())
        {
            var list=ActiveSpans[oldKey];int expired=list.RemoveAll(item=>Clock.ElapsedMilliseconds-item.AddedAtMilliseconds>60000);
            ExpiredSpans+=expired;ActiveSpanCount-=expired;if(list.Count==0)ActiveSpans.Remove(oldKey);
        }
        if(ActiveSpanCount>=2048){LogLimit=true;ThreadPool.QueueUserWorkItem(delegate{StopOwn("bounded-owned-span-cache");});return;}
        var span=new Span {Id=++SpanSequence,Role=role,Pqmsg=pqmsg,CallbackCount=count,HeaderPid=e.ProcessID,HeaderTid=e.ThreadID,StartQpc=e.TimeStampQPC,StartEventId=id,Target=target,AddedAtMilliseconds=Clock.ElapsedMilliseconds};
        List<Span> values;if(!ActiveSpans.TryGetValue(key,out values)){values=new List<Span>();ActiveSpans[key]=values;}values.Add(span);ActiveSpanCount++;
        if(id==60)SendSpanStarts++;else ReceiveSpanStarts++;
        var fields=new Dictionary<string,object>{{"SpanId",span.Id},{"SpanRole",role},{"pqmsg",pqmsg},{"CallbackCount",count},{"StartQpc",span.StartQpc}};
        Add(e,fields,"pidReceiver");Add(e,fields,"tidReceiver");
        Record(e,target,fields,"owned-message-span-start");
    }
    private static void TrackStop(TraceEvent e,int id)
    {
        ulong pqmsg,count;if(!Number(e,"pqmsg",out pqmsg) || pqmsg==0){UnmappedSpanStops++;return;}Number(e,"CallbackCount",out count);
        string role=id==70?"send":"receive";string key=SpanKey(role,pqmsg,e.ProcessID,e.ThreadID,count);
        List<Span> list;Span span=null;bool countMismatch=false;
        if(ActiveSpans.TryGetValue(key,out list) && list.Count>0){span=list[list.Count-1];list.RemoveAt(list.Count-1);if(list.Count==0)ActiveSpans.Remove(key);}
        else
        {
            var candidates=ActiveSpans.SelectMany(pair=>pair.Value.Select(value=>new {Key=pair.Key,Span=value})).Where(item=>item.Span.Role==role && item.Span.Pqmsg==pqmsg && item.Span.HeaderPid==e.ProcessID && item.Span.HeaderTid==e.ThreadID).ToArray();
            if(candidates.Length==1){span=candidates[0].Span;list=ActiveSpans[candidates[0].Key];list.Remove(span);if(list.Count==0)ActiveSpans.Remove(candidates[0].Key);countMismatch=true;}
            else{if(candidates.Length>1)AmbiguousSpanStops++;UnmappedSpanStops++;return;}
        }
        ActiveSpanCount--;MappedSpanStops++;
        var fields=new Dictionary<string,object>{{"PairedSpanId",span.Id},{"SpanRole",role},{"pqmsg",pqmsg},{"StartEventId",span.StartEventId},{"StartQpc",span.StartQpc},{"EndQpc",e.TimeStampQPC},
            {"StartHeaderPid",span.HeaderPid},{"StartHeaderTid",span.HeaderTid},{"StartCallbackCount",span.CallbackCount},{"StopCallbackCount",count},{"UniqueCountMismatchFallback",countMismatch},
            {"NegativeQpcDuration",e.TimeStampQPC<span.StartQpc},{"DurationMilliseconds",(e.TimeStampQPC-span.StartQpc)*1000.0/Stopwatch.Frequency}};
        Record(e,span.Target,fields,"owned-message-span-stop");
    }
    private static void MissingMessageDiagnostic(TraceEvent e,Target target)
    {
        if(MissingMessageDiagnostics>=512)return;
        uint pid;uint thread=GetWindowThreadProcessId(new IntPtr(unchecked((long)target.Hwnd)),out pid);
        if(pid!=target.ProcessId || thread!=target.ThreadId)return;
        var values=new List<object>();
        string[] names=e.PayloadNames ?? new string[0];
        for(int index=0;index<names.Length;index++)
        {
            object value=null;string error=null;
            try{value=e.PayloadValue(index);}catch(Exception failure){error=failure.GetType().Name;}
            Type type=value==null?null:value.GetType();string text=null;
            if(value is IntPtr)text="0x"+unchecked((ulong)((IntPtr)value).ToInt64()).ToString("X");
            else if(value is UIntPtr)text="0x"+((UIntPtr)value).ToUInt64().ToString("X");
            else if(type!=null && (type.IsPrimitive || type.IsEnum || value is Decimal))text=Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture);
            else if(value is string)
            {
                string candidate=(string)value;ulong parsed;
                if(UInt64.TryParse(candidate,out parsed) || candidate.StartsWith("0x",StringComparison.OrdinalIgnoreCase) && UInt64.TryParse(candidate.Substring(2),System.Globalization.NumberStyles.HexNumber,null,out parsed))text=candidate;
            }
            values.Add(new {Name=names[index],Type=type==null?null:type.FullName,NumericToString=text,NonNumericValueSuppressed=value!=null && text==null,ReadError=error});
        }
        byte[] raw=e.EventData();bool rawSaved=raw.Length<=256;
        var fields=new Dictionary<string,object>{{"PointerSize",e.PointerSize},{"EventDataLength",e.EventDataLength},{"DecodedFields",values},{"RawByteCount",raw.Length},{"RawBase64",rawSaved?Convert.ToBase64String(raw):null},{"RawSuppressedOver256Bytes",!rawSaved},{"LiveOwnedHwndVerified",true},{"Meaning","Diagnostic only: zero/missing decoded message cannot prove which command this event represented."}};
        Interlocked.Increment(ref MissingMessageDiagnostics);
        Record(e,target,fields,"owned-zero-message-diagnostic");
    }
    private static bool AllowedMessage(ulong message,ulong wp)
    {
        if(message==0x0005) return true;
        if(message>=0x00A1 && message<=0x00A9) return true;
        if(message==0x0112) {ulong command=wp & 0xFFF0;return command==0xF020 || command==0xF120 || command==0xF030;}
        return false;
    }
    private static void Record(TraceEvent e,Target target,Dictionary<string,object> fields,string kind)
    {
        Interlocked.Increment(ref Matched);
        var item=new {Kind=kind,EventUtc=e.TimeStamp.ToUniversalTime().ToString("o"),CaptureUtc=DateTime.UtcNow.ToString("o"),Qpc=e.TimeStampQPC,
            EventId=(int)e.ID,Version=e.Version,HeaderProcessId=e.ProcessID,HeaderThreadId=e.ThreadID,TargetHwnd="0x"+target.Hwnd.ToString("X"),
            TargetProcessId=target.ProcessId,TargetThreadId=target.ThreadId,TargetRole=target.Role,TargetProcessStartUtc=target.StartUtc.ToString("o"),
            HeaderIdentity=Identity(e.ProcessID,e.TimeStamp.ToUniversalTime()),Payload=fields,
            Interpretation="Header is event-emission context; pidReceiver/tidReceiver are never renamed to sender; calibrate before attribution."};
        Write(item);
    }
    private static object Identity(int pid,DateTime eventUtc)
    {
        Interlocked.Increment(ref IdentityLookups);
        if(pid<=0)return new {ProcessId=pid,Available=false,Reason="system-or-unavailable"};
        try
        {
            using(var p=Process.GetProcessById(pid))
            {
                DateTime start=p.StartTime.ToUniversalTime();
                if(start>eventUtc.AddMilliseconds(2))return new {ProcessId=pid,Available=false,Reason="PID-reused-after-event"};
                return new {ProcessId=pid,Available=true,StartUtc=start.ToString("o"),Image=p.MainModule.FileName};
            }
        }
        catch(Exception error){return new {ProcessId=pid,Available=false,Reason=error.GetType().Name};}
    }
    private static bool OwnedAtEvent(Target t,DateTime eventUtc)
    {
        if(t.RetiredUtc!=DateTime.MinValue && eventUtc>t.RetiredUtc)return false;
        uint pid;uint thread=GetWindowThreadProcessId(new IntPtr(unchecked((long)t.Hwnd)),out pid);
        if(pid==t.ProcessId && thread==t.ThreadId)return true;
        // ETW buffers can arrive after a failed test has closed its own window.
        // Retain only a very recent, previously live-verified identity; never
        // accept a handle now owned by another PID/thread, and never save input.
        if(pid==0 && eventUtc>=t.FirstVerifiedUtc.AddMilliseconds(-100) && eventUtc<=t.LastVerifiedUtc.AddMilliseconds(250) && DateTime.UtcNow<=t.LastVerifiedUtc.AddSeconds(8))
        {Interlocked.Increment(ref BufferedOwner);return true;}
        return false;
    }
    private static bool Number(TraceEvent e,string name,out ulong number)
    {
        number=0;string[] names=e.PayloadNames;if(names==null)return false;
        for(int i=0;i<names.Length;i++)if(String.Equals(names[i],name,StringComparison.OrdinalIgnoreCase))
        {object value=e.PayloadValue(i);if(value==null)return false;try{if(value is IntPtr){number=unchecked((ulong)((IntPtr)value).ToInt64());return true;}if(value is UIntPtr){number=((UIntPtr)value).ToUInt64();return true;}if(value is long){number=unchecked((ulong)(long)value);return true;}if(value is int){number=unchecked((uint)(int)value);return true;}number=Convert.ToUInt64(value);return true;}catch{return false;}}
        return false;
    }
    private static void Add(TraceEvent e,Dictionary<string,object> target,string name){ulong value;if(Number(e,name,out value))target[name]=value;}
    private static string ReadWatchSnapshot(string path)
    {
        // The publisher atomically replaces targets.json. Hold the old inode
        // with DELETE sharing so publication is never blocked by this reader.
        // This handle reads one complete old/new snapshot, not the changing name.
        using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
        {
            if(stream.Length>65536)throw new InvalidDataException("Watch snapshot exceeds64KiB.");
            using(var reader=new StreamReader(stream,Encoding.UTF8,true,4096))
            {
                string snapshot=reader.ReadToEnd();
                if(snapshot.Length>65536)throw new InvalidDataException("Watch snapshot text exceeds64KiB.");
                return snapshot;
            }
        }
    }
    private static void ReloadTargets(object unused)
    {
        if(Interlocked.Exchange(ref Reloading,1)!=0 || Stopping!=0)return;
        try
        {
            DateTime now=DateTime.UtcNow;
            if(File.Exists(WatchPath))
            {
                var info=new FileInfo(WatchPath);
                if(info.Length>65536)throw new InvalidDataException("Watch file exceeds64KiB.");
                if(info.LastWriteTimeUtc!=WatchWrite)
                {
                    var root=new JavaScriptSerializer {MaxJsonLength=65536}.DeserializeObject(ReadWatchSnapshot(WatchPath)) as Dictionary<string,object>;
                    if(root==null)throw new InvalidDataException("Watch object required.");object stop;
                    if(root.TryGetValue("Stop",out stop) && stop is bool && (bool)stop){StopByWatch=true;StopOwn("watch-stop-request");return;}
                    object raw;if(!root.TryGetValue("Targets",out raw) || !(raw is object[]))throw new InvalidDataException("Targets array required.");
                    var array=(object[])raw;if(array.Length>32)throw new InvalidDataException("At most32targets.");
                    var activeHandles=new HashSet<ulong>();
                    foreach(object value in array)
                    {
                        var row=value as Dictionary<string,object>;if(row==null)continue;
                        ulong hwnd=ParseHandle(Convert.ToString(row["Hwnd"]));uint pid=Convert.ToUInt32(row["ProcessId"]);uint actualPid;uint tid=GetWindowThreadProcessId(new IntPtr(unchecked((long)hwnd)),out actualPid);
                        if(tid==0 || actualPid!=pid)continue;
                        DateTime expected=DateTime.Parse(Convert.ToString(row["ProcessStartUtc"]),null,System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
                        string path;DateTime actual;
                        using(var process=Process.GetProcessById((int)pid)){actual=process.StartTime.ToUniversalTime();path=Path.GetFullPath(process.MainModule.FileName);}
                        if(Math.Abs((actual-expected).TotalMilliseconds)>2 || !path.StartsWith(Project+"\\.runtime\\",StringComparison.OrdinalIgnoreCase) && !String.Equals(path,Path.Combine(Project,"packaging","app","CodexIsland.exe"),StringComparison.OrdinalIgnoreCase) && !String.Equals(path,Path.Combine(Project,"dist","CodexIsland.exe"),StringComparison.OrdinalIgnoreCase))continue;
                        object thread;if(row.TryGetValue("ThreadId",out thread) && Convert.ToUInt32(thread)!=tid)continue;
                        Target previous;Known.TryGetValue(hwnd,out previous);string role=row.ContainsKey("Role")?Convert.ToString(row["Role"]):"window";
                        if(role.Length>48 || role.Any(c=>!Char.IsLetterOrDigit(c)&&c!='-'&&c!='_'&&c!='.'))role="window";
                        Known[hwnd]=new Target {Hwnd=hwnd,ProcessId=pid,ThreadId=tid,StartUtc=actual,Role=role,FirstVerifiedUtc=previous!=null && previous.ProcessId==pid && previous.StartUtc==actual?previous.FirstVerifiedUtc:now,LastVerifiedUtc=now};
                        activeHandles.Add(hwnd);
                    }
                    foreach(var t in Known.Values)if(!activeHandles.Contains(t.Hwnd) && t.RetiredUtc==DateTime.MinValue)t.RetiredUtc=now;
                    WatchWrite=info.LastWriteTimeUtc;Interlocked.Increment(ref TargetUpdates);
                }
            }
            // Refresh live ownership times without reading or saving any input.
            foreach(var t in Known.Values.ToArray())
            {
                uint pid;uint thread=GetWindowThreadProcessId(new IntPtr(unchecked((long)t.Hwnd)),out pid);
                if(t.RetiredUtc==DateTime.MinValue && pid==t.ProcessId && thread==t.ThreadId)t.LastVerifiedUtc=now;
                else if(now>t.LastVerifiedUtc.AddSeconds(8))Known.Remove(t.Hwnd);
            }
            Targets=Known.Values.ToArray();
        }
        catch{Interlocked.Increment(ref WatchErrors);}
        finally{Volatile.Write(ref Reloading,0);}
    }
    private static ulong ParseHandle(string value){return value.StartsWith("0x",StringComparison.OrdinalIgnoreCase)?UInt64.Parse(value.Substring(2),System.Globalization.NumberStyles.HexNumber):UInt64.Parse(value);}
    private static string Inside(string value){string path=Path.GetFullPath(value);if(!path.StartsWith(Project+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Path must be inside project.");return path;}
    private static void Write(object record)
    {
        lock(Gate)
        {
            if(Log==null || LogLimit)return;string line=Json.Serialize(record);long bytes=Encoding.UTF8.GetByteCount(line)+Encoding.UTF8.GetByteCount(Environment.NewLine);
            if(Lines>=MaximumLogLines || Bytes+bytes>MaximumLogBytes){LogLimit=true;ThreadPool.QueueUserWorkItem(delegate{StopOwn("bounded-log-limit");});return;}
            Log.WriteLine(line);Lines++;Bytes+=bytes;
        }
    }
    private static void Save(string name,object value){File.WriteAllText(Path.Combine(Output,name),new JavaScriptSerializer {MaxJsonLength=1024*1024}.Serialize(value),new UTF8Encoding(false));}
    private static void StopOwn(string reason)
    {
        if(Interlocked.Exchange(ref Stopping,1)!=0)return;StopReason=reason;
        if(Session!=null)
        {
            try{EventsLostBeforeStop=Session.EventsLost;}catch(Exception error){LostCounterReadError=error.GetType().Name;}
            try{Session.Dispose();}catch(Exception error){CleanupError=error.GetType().FullName+":0x"+error.HResult.ToString("X8");try{Session.Source.StopProcessing();}catch{}}
        }
    }
    private static bool FallbackStopOwn()
    {
        if(OwnSession==null || !OwnSession.StartsWith("CodexIsland.MinimizeAttrib.",StringComparison.Ordinal))return false;
        try
        {
            var info=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"logman.exe"),"stop "+OwnSession+" -ets") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            using(var process=Process.Start(info)){process.StandardOutput.ReadToEnd();process.StandardError.ReadToEnd();if(!process.WaitForExit(5000))return false;}
            return !TraceEventSession.GetActiveSessionNames().Contains(OwnSession);
        }
        catch{return false;}
    }
    private sealed class Target {internal ulong Hwnd;internal uint ProcessId,ThreadId;internal DateTime StartUtc,FirstVerifiedUtc,LastVerifiedUtc,RetiredUtc;internal string Role;}
    private sealed class Span {internal long Id,StartQpc,AddedAtMilliseconds;internal string Role;internal ulong Pqmsg,CallbackCount;internal int HeaderPid,HeaderTid,StartEventId;internal Target Target;}
    private sealed class DispatchSpan {internal long Id,StartQpc;internal ulong CallbackCount,Message;internal Target[] Candidates;}
    private sealed class PreflightResult
    {
        public string SessionName, StartedUtc, EndedUtc, ErrorType, HResult, CleanupError;
        public bool Elevated, Started, RealTime, DisposeCompleted, AccessDenied;
        public bool EtlWritten = false, UiExecuted = false, OtherSessionStopped = false;
        public int NativeError;
    }
}
