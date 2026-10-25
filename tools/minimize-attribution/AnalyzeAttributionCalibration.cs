using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Globalization;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

// Offline only: never opens a window, process, hook or ETW session.
internal static class AnalyzeAttributionCalibration
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024, RecursionLimit = 100 };
    static readonly string Project = FindProject();
    static string FindProject()
    {
        foreach (string start in new[] { Environment.CurrentDirectory, AppDomain.CurrentDomain.BaseDirectory })
        {
            var current = new DirectoryInfo(Path.GetFullPath(start));
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "build.ps1")) && Directory.Exists(Path.Combine(current.FullName, "src")) && Directory.Exists(Path.Combine(current.FullName, "tests"))) return current.FullName;
                current = current.Parent;
            }
        }
        throw new InvalidOperationException("Run this offline analyzer from a source checkout or its local diagnostic output directory.");
    }
    sealed class Span
    {
        public long Id, Start, End, HeaderPid, HeaderTid, TargetPid, TargetTid;
        public ulong Pointer, Hwnd;
        public string Role;
        public Dictionary<string, object> First, Last;
        public object Report()
        {
            return new { SpanId = Id, Role, StartQpc = Start, EndQpc = End, PqmsgDecimal = Pointer.ToString(CultureInfo.InvariantCulture), PqmsgHex = Hex(Pointer), Hwnd = Hex(Hwnd), HeaderProcessId = HeaderPid, HeaderThreadId = HeaderTid, TargetProcessId = TargetPid, TargetThreadId = TargetTid };
        }
    }
    static Dictionary<string, object> Obj(object o) { return (Dictionary<string, object>)o; }
    static object Get(Dictionary<string, object> o, string key) { object v; return o.TryGetValue(key, out v) ? v : null; }
    static string Str(Dictionary<string, object> o, string key) { var v = Get(o, key); return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture); }
    static bool Bool(Dictionary<string, object> o, string key) { var v = Get(o, key); return v != null && Convert.ToBoolean(v, CultureInfo.InvariantCulture); }
    // No binary float is accepted for pointers or QPC. JavaScriptSerializer uses Decimal for UInt64 above Int64.MaxValue.
    static ulong U64(object v)
    {
        if (v == null || v is double || v is float) throw new InvalidDataException("Missing/floating integer rejected: " + v);
        if (v is string)
        {
            string s = (string)v;
            return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ulong.Parse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : ulong.Parse(s, CultureInfo.InvariantCulture);
        }
        decimal d = Convert.ToDecimal(v, CultureInfo.InvariantCulture);
        if (decimal.Truncate(d) != d || d < 0 || d > ulong.MaxValue) throw new InvalidDataException("Non-integral/out-of-range integer");
        return (ulong)d;
    }
    static long I64(object v) { return checked((long)U64(v)); }
    static long N(Dictionary<string, object> o, string key) { return I64(Get(o, key)); }
    static ulong P(Dictionary<string, object> o, string key) { return U64(Get(o, key)); }
    static string Hex(ulong v) { return "0x" + v.ToString("X", CultureInfo.InvariantCulture); }
    static void Require(bool ok, string why) { if (!ok) throw new InvalidDataException(why); }
    static Dictionary<string, object> Read(string path) { return Obj(Json.DeserializeObject(File.ReadAllText(path))); }
    static List<Dictionary<string, object>> Lines(string path) { return File.ReadLines(path).Where(x => !String.IsNullOrWhiteSpace(x)).Select(x => Obj(Json.DeserializeObject(x))).ToList(); }
    static string Hash(string path) { using (var s = File.OpenRead(path)) using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(s)).Replace("-", ""); }
    static string InProject(string path)
    {
        string full = Path.GetFullPath(path);
        Require(full.StartsWith(Project + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Input must be within project");
        return full;
    }
    static List<Span> Pair(List<Dictionary<string, object>> etw)
    {
        var starts = new Dictionary<long, Dictionary<string, object>>();
        var pairs = new List<Span>();
        foreach (var row in etw)
        {
            string kind = Str(row, "Kind");
            if (kind != "owned-message-span-start" && kind != "owned-message-span-stop") continue;
            var p = Obj(Get(row, "Payload"));
            if (kind == "owned-message-span-start")
            {
                long id = N(p, "SpanId");
                Require(!starts.ContainsKey(id), "Duplicate span start " + id);
                starts.Add(id, row);
                continue;
            }
            long sid = N(p, "PairedSpanId");
            Require(starts.ContainsKey(sid), "Stop without start " + sid);
            var first = starts[sid]; var fp = Obj(Get(first, "Payload"));
            string role = Str(fp, "SpanRole");
            Require(role == "send" || role == "receive", "Unknown role");
            Require(N(first, "EventId") == (role == "send" ? 60 : 62) && N(row, "EventId") == (role == "send" ? 70 : 83), "Unexpected span event IDs");
            Require(Str(p, "SpanRole") == role && N(p, "StartEventId") == N(first, "EventId"), "Mismatched role/event");
            Require(P(fp, "pqmsg") == P(p, "pqmsg"), "Pointer changed within span");
            Require(N(first, "Qpc") == N(fp, "StartQpc") && N(first, "Qpc") == N(p, "StartQpc") && N(row, "Qpc") == N(p, "EndQpc"), "QPC pairing inconsistent");
            foreach (string field in new[] { "HeaderProcessId", "HeaderThreadId", "TargetProcessId", "TargetThreadId" }) Require(N(first, field) == N(row, field), "Identity changed within span: " + field);
            Require(P(first, "TargetHwnd") == P(row, "TargetHwnd"), "Target handle changed");
            Require(!Bool(p, "UniqueCountMismatchFallback") && !Bool(p, "NegativeQpcDuration") && N(row, "Qpc") >= N(first, "Qpc"), "Non-exact/negative span rejected");
            Require(N(fp, "CallbackCount") == N(p, "StartCallbackCount") && N(fp, "CallbackCount") == N(p, "StopCallbackCount"), "Callback nesting changed");
            var s = new Span { Id = sid, Role = role, Start = N(first, "Qpc"), End = N(row, "Qpc"), HeaderPid = N(first, "HeaderProcessId"), HeaderTid = N(first, "HeaderThreadId"), TargetPid = N(first, "TargetProcessId"), TargetTid = N(first, "TargetThreadId"), Pointer = P(fp, "pqmsg"), Hwnd = P(first, "TargetHwnd"), First = first, Last = row };
            if (role == "send") Require(N(fp, "pidReceiver") == s.TargetPid && N(fp, "tidReceiver") == s.TargetTid, "Explicit send receiver disagrees with target");
            if (role == "receive") Require(s.HeaderPid == s.TargetPid && s.HeaderTid == s.TargetTid, "Receive header not target thread");
            pairs.Add(s); starts.Remove(sid);
        }
        Require(starts.Count == 0, "Unclosed starts in calibration");
        return pairs;
    }
    static Dictionary<string, object> Expected(string folder, string command)
    {
        string path = Path.Combine(folder, "receiver", "sender-" + command + ".json");
        return File.Exists(path) ? Read(path) : null;
    }
    static string Command(Dictionary<string, object> row) { return Str(row, "Context").Replace("CONTROLLED/", ""); }
    static void WriteFresh(string path, string value)
    {
        using (var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var w = new StreamWriter(f, new UTF8Encoding(false))) w.Write(value);
    }
    public static int Main(string[] args)
    {
        try
        {
            var exact = Obj(Json.DeserializeObject("{\"p\":18446705635115530752,\"q\":891741865409}"));
            Require(P(exact, "p") == ulong.Parse("18446705635115530752", CultureInfo.InvariantCulture), "UInt64 self-check");
            Require(N(exact, "q") == 891741865409L, "QPC self-check");
            bool rejected = false; try { U64(18446705635115530752d); } catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Floating pointer must be rejected");
            if (args.Length == 1 && args[0] == "--self-check") { Console.WriteLine("exact UInt64/QPC parser self-check passed; no UI/session"); return 0; }
            Require(args.Length == 1, "Usage: AnalyzeAttributionCalibration.exe <project calibration folder> | --self-check");
            string dir = InProject(args[0]);
            var paths = new[] { Path.Combine(dir, "etw", "events.jsonl"), Path.Combine(dir, "etw", "summary.json"), Path.Combine(dir, "receiver", "events.jsonl") };
            var etw = Lines(paths[0]); var summary = Read(paths[1]); var native = Lines(paths[2]);
            Require(!Bool(summary, "DataLossDetected") && N(summary, "EventsLost") == 0 && Bool(summary, "CleanupConfirmed") && Bool(summary, "ProcessingReturned"), "Collector loss/cleanup quality gate");
            var spans = Pair(etw);
            var scs = native.Where(r => Str(r, "Event") == "WM" && Get(r, "Detail") is Dictionary<string, object> && N(Obj(Get(r, "Detail")), "Message") == 274 && (P(Obj(Get(r, "Detail")), "W") & 0xFFF0UL) == 0xF020UL).ToList();
            var scResults = new List<object>(); var scMd = new List<string>(); int directMatched = 0, asyncUnproven = 0;
            foreach (var row in scs)
            {
                var d = Obj(Get(row, "Detail")); string command = Command(row); long qpc = N(row, "Qpc");
                var enclosing = spans.Where(s => s.Role == "receive" && s.Hwnd == P(d, "Hwnd") && s.TargetPid == N(row, "ReceiverProcessId") && s.TargetTid == N(row, "CurrentThreadId") && s.Start <= qpc && qpc <= s.End).ToList();
                // Unique minimal interval under strict containment. Crossing intervals or equal duplicate bounds are ambiguous.
                var tight = enclosing.Where(s => !enclosing.Any(t => t.Id != s.Id && t.Start >= s.Start && t.End <= s.End && (t.Start > s.Start || t.End < s.End))).ToList();
                Span receive = tight.Count == 1 ? tight[0] : null;
                var sends = receive == null ? new List<Span>() : spans.Where(s => s.Role == "send" && s.Pointer == receive.Pointer && s.Hwnd == receive.Hwnd && s.TargetPid == receive.TargetPid && s.TargetTid == receive.TargetTid && s.Start <= receive.Start && receive.End <= s.End).ToList();
                Span send = sends.Count == 1 ? sends[0] : null; var expected = Expected(dir, command);
                bool matches = send != null && expected != null && Bool(expected, "Controlled") && Str(expected, "Command") == command && send.HeaderPid == N(expected, "SenderProcessId") && send.HeaderTid == N(expected, "SenderThreadId") && send.TargetPid == N(expected, "ReceiverProcessId") && send.Hwnd == P(expected, "ReceiverHwnd");
                bool direct = command == "send-sc" || command == "timeout-sc";
                if (direct) { Require(Bool(row, "Controlled") && N(d, "ISMEX") == 1 && matches, "Direct SC positive control lacks exact expected sender chain: " + command); directMatched++; }
                if (command == "post-sc") { Require(N(d, "ISMEX") == 0 && receive == null && send == null, "Post control unexpectedly has synchronous chain"); asyncUnproven++; }
                string status = direct && matches ? "controlled-direct-SC-sender-confirmed" : command == "post-sc" ? "async-post-no-synchronous-sender-proof" : "same-thread-control-no-remote-chain";
                scResults.Add(new { Command = command, NativeAbsoluteQpc = qpc, NativeReceiverProcessId = N(row, "ReceiverProcessId"), NativeReceiverThreadId = N(row, "CurrentThreadId"), NativeHwnd = Str(d, "Hwnd"), NativeMessage = 274, NativeW = Str(d, "W"), NativeISMEX = N(d, "ISMEX"), EnclosingReceiveCandidateCount = enclosing.Count, TightestReceiveCandidateCount = tight.Count, ReceiveSpan = receive == null ? null : receive.Report(), EnclosingSendCandidateCount = sends.Count, SendSpan = send == null ? null : send.Report(), ExpectedSenderProcessId = expected == null ? (long?)null : N(expected, "SenderProcessId"), ExpectedSenderThreadId = expected == null ? (long?)null : N(expected, "SenderThreadId"), ExactExpectedSenderMatch = matches, Status = status, HistoricalSenderProven = false });
                scMd.Add("| " + command + " | " + qpc + " | " + (receive == null ? "none" : receive.Id + ": [" + receive.Start + ", " + receive.End + "]") + " | " + (send == null ? "none" : send.Id + ": [" + send.Start + ", " + send.End + "]") + " | " + (send == null ? "unproven" : send.HeaderPid + "/" + send.HeaderTid) + " | " + status + " |");
            }
            Require(directMatched == 2 && asyncUnproven == 1 && scs.Count == 4, "Unexpected direct/post/own calibration control count");
            var apiResults = new List<object>(); var apiMd = new List<string>();
            foreach (string command in new[] { "show-window", "show-window-async", "placement" })
            {
                var expected = Expected(dir, command); Require(expected != null && Bool(expected, "Controlled"), "Missing controlled sender " + command);
                var before = native.Single(r => Str(r, "Event") == "action-before" && Command(r) == command);
                var after = native.Single(r => Str(r, "Event") == "action-minimized" && Command(r) == command);
                long start = N(before, "Qpc"), end = N(after, "Qpc");
                var candidates = etw.Where(r => (Str(r, "Kind") == (command == "show-window-async" ? "owned-queue-event" : "owned-window-update")) && N(r, "EventId") == (command == "show-window-async" ? 203 : 1) && P(r, "TargetHwnd") == P(expected, "ReceiverHwnd") && N(r, "TargetProcessId") == N(expected, "ReceiverProcessId") && N(r, "HeaderProcessId") == N(expected, "SenderProcessId") && N(r, "HeaderThreadId") == N(expected, "SenderThreadId") && start <= N(r, "Qpc") && N(r, "Qpc") <= end && N(Obj(Get(r, "Payload")), command == "show-window-async" ? "dwQEvent" : "Type") == (command == "show-window-async" ? 1 : 1073741845)).ToList();
                Require(candidates.Count == 1, "Known API control lacks unique matching source header: " + command);
                var c = candidates[0]; apiResults.Add(new { Command = command, Controlled = true, PhaseStartQpc = start, PhaseEndQpc = end, ExpectedSenderProcessId = N(expected, "SenderProcessId"), ExpectedSenderThreadId = N(expected, "SenderThreadId"), ExactSourceHeaderMatchCount = candidates.Count, MatchedEventId = N(c, "EventId"), EventQpc = N(c, "Qpc"), SourceHeaderProcessId = N(c, "HeaderProcessId"), SourceHeaderThreadId = N(c, "HeaderThreadId"), Hwnd = Str(c, "TargetHwnd"), Payload = Get(c, "Payload"), Interpretation = "Controlled positive source-context calibration only; event type does not independently decode a particular API or command.", HistoricalSenderProven = false });
                apiMd.Add("| " + command + " | " + N(c, "EventId") + " | " + N(c, "Qpc") + " | " + N(c, "HeaderProcessId") + "/" + N(c, "HeaderThreadId") + " | yes |");
            }
            var inputs = paths.Concat(Directory.GetFiles(Path.Combine(dir, "receiver"), "sender-*.json")).Select(p => new { Path = p, Sha256 = Hash(p), Bytes = new FileInfo(p).Length }).ToArray();
            string outputJson = Path.Combine(dir, "attribution-calibration.json"), outputMd = Path.Combine(dir, "attribution-calibration.md");
            Require(!File.Exists(outputJson) && !File.Exists(outputMd), "Outputs must not already exist");
            var report = new { Version = 1, Kind = "offline-exact-span-controlled-attribution-calibration", GeneratedUtc = DateTime.UtcNow.ToString("o"), NoUiOrSessionExecuted = true, CalibrationDirectory = dir, AnalyzerSource = Path.Combine(Project, "tools", "minimize-attribution", "AnalyzeAttributionCalibration.cs"), AnalyzerSourceSha256 = Hash(Path.Combine(Project, "tools", "minimize-attribution", "AnalyzeAttributionCalibration.cs")), Inputs = inputs, IntegerParsing = new { Qpc = "exact Int64; double/float rejected", Pqmsg = "exact UInt64 using Decimal; double/float rejected; output pointer strings prevent JavaScript precision loss", SelfCheckPointer = "18446705635115530752", SelfCheckPassed = true }, CollectorQuality = new { EventsLost = N(summary, "EventsLost"), DataLossDetected = Bool(summary, "DataLossDetected"), CleanupConfirmed = Bool(summary, "CleanupConfirmed"), ProcessingReturned = Bool(summary, "ProcessingReturned"), UnmappedOtherWindowStops = N(summary, "UnmappedSpanStops"), AmbiguousSpanStops = N(summary, "AmbiguousSpanStops"), ActiveSpansAtStop = N(summary, "ActiveSpansAtStop"), RawOwnedDiagnosticRecords = N(summary, "MissingMessageDiagnosticRecords"), WhitelistedMessageRecords = N(summary, "WhitelistedMessageRecords") }, SpanPairCount = spans.Count, SendSpanPairCount = spans.Count(s => s.Role == "send"), ReceiveSpanPairCount = spans.Count(s => s.Role == "receive"), ExactControlledDirectScMatches = directMatched, AsyncPostWithoutSynchronousProof = asyncUnproven, NativeScResults = scResults, ControlledApiSourceHeaderResults = apiResults, PairedSpans = spans.Select(s => s.Report()).ToArray(), HistoricalSenderProven = false, Limitations = new[] { "This calibration establishes source-header interpretation for these controlled commands on this OS build; it does not identify the historical first-run minimization trigger.", "A native SC_MINIMIZE uniquely inside a receive span and enclosing send span establishes synchronous dispatch context. Outside a controlled direct-command test, nested self-generated SC during another external dispatch must be excluded with entry stack or additional code-path evidence before naming the originating command.", "PostMessage SC_MINIMIZE is asynchronous and has no synchronous send/receive span proof here; pointer equality or near-time correlation alone is not sufficient.", "WindowUpdate type 0x40000015 and QueueEvent dwQEvent=1 source contexts match the known external ShowWindow/SetWindowPlacement/ShowWindowAsync controls; neither numeric event alone uniquely specifies that API.", "Message/WParam/LParam are zero in this build's matched ETW message records. No zero payload is re-labelled as SC_MINIMIZE; native receiver logs identify that command." } };
            var md = new StringBuilder(); md.AppendLine("# Controlled attribution calibration 002").AppendLine().AppendLine("Offline analysis only. All exact-integer and span integrity checks passed. Collector reports zero lost events, completed processing and confirmed session cleanup. This report does **not** identify the historical first-run sender.").AppendLine();
            md.AppendLine("The analyzer pairs all 24 start/stop spans (12 send, 12 receive). It rejects floating pointers, callback-count fallbacks, inconsistent identities, negative durations, unclosed spans and ambiguous enclosing intervals. A native SC_MINIMIZE must match HWND and receiver PID/TID, fall inside the unique tightest receive [62,83] interval, and that interval must fall fully inside one send [60,70] interval with exact UInt64 pqmsg and receiver fields. The known child sender PID/TID must also match.").AppendLine();
            md.AppendLine("| Native control | Native QPC | Receive span | Send span | Source PID/TID | Result |").AppendLine("|---|---:|---|---|---|---|"); foreach (var line in scMd) md.AppendLine(line); md.AppendLine();
            md.AppendLine("Both external direct SC controls have exact matching sender chains. Same-thread own-send-sc has no remote dispatch chain. PostMessage SC has no synchronous chain and remains explicitly unproven by this technique. The two direct controls reuse the same UInt64 pqmsg; time containment prevents cross-command pointer reuse from causing a false join.").AppendLine();
            md.AppendLine("| Known API control | ETW ID | QPC | Source PID/TID | Expected child exact match |").AppendLine("|---|---:|---:|---|---|"); foreach (var line in apiMd) md.AppendLine(line); md.AppendLine();
            md.AppendLine("ShowWindow/SetWindowPlacement match WindowUpdate Type=0x40000015; ShowWindowAsync matches QueueEvent dwQEvent=1. These positive controls calibrate event emission context. They do not turn a shared event type into a unique API decoder. A future uncontrolled observation must preserve the native transition and bounded owned-HWND dispatch evidence before attributing a trigger.").AppendLine();
            md.AppendLine("For uncontrolled synchronous SC, an enclosing remote span may also surround a nested self-generated command. Exclude that case through the SC entry stack and application call paths before saying a remote process sent SC_MINIMIZE. No near-time-only attribution is accepted.").AppendLine();
            md.AppendLine("The exact pointer parser self-check is 18446705635115530752 (larger than Int64 and JavaScript's exact integer range); JSON output uses pointer strings. Full hashes, all paired spans, QPC bounds, expected identities, collector quality and limitations are in attribution-calibration.json. Original input files are unchanged.");
            WriteFresh(outputJson, Json.Serialize(report) + Environment.NewLine); WriteFresh(outputMd, md.ToString());
            Console.WriteLine("PASS: 24 exact paired spans; 2 controlled direct SC sender chains; 3 API source header controls; async Post explicitly unproven. HistoricalSenderProven=false.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); return 1; }
    }
}
