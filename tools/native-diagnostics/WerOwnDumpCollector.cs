using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;

// Starts only an exact project-owned test executable, without debugger flags.
// WER files for other PIDs are never copied or inspected beyond header/MiscInfo.
internal static class WerOwnDumpCollector
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
    private static readonly List<Dictionary<string, object>> Captures = new List<Dictionary<string, object>>();
    private static readonly List<HeldDump> Held = new List<HeldDump>();
    private static readonly HashSet<string> OldFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> CopiedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> Failures = new List<string>();
    private static StreamWriter Log;
    private static IntPtr ProcessHandle;
    private static uint ProcessId, ChildExit;
    private static ulong CreationTime;
    private static string Root, Output, Target, WerDirectory;
    private static bool ChildStarted, ChildExited, DeadlineTerminated;
    private static long EventCursor;
    private static int OtherPidFiles, PendingHeaderReads;
    private static bool FullRegression;
    private static readonly Dictionary<uint, OwnedProcess> Owned = new Dictionary<uint, OwnedProcess>();

    [STAThread]
    private static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception error) { Failures.Add(error.ToString()); if (Log != null) Write("COLLECTOR ERROR: " + error); else Console.Error.WriteLine(error); return 2; }
        finally
        {
            if (ProcessHandle != IntPtr.Zero && !ChildExited)
            {
                uint exit; if (GetExitCodeProcess(ProcessHandle, out exit) && exit == 259) TerminateProcess(ProcessHandle, 0xDEAD0003);
            }
            foreach (var held in Held) held.Stream.Dispose();
            foreach (var owner in Owned.Values.Where(item => item.ProcessId != ProcessId))
            {
                uint exit; if (owner.Handle != IntPtr.Zero && GetExitCodeProcess(owner.Handle, out exit) && exit == 259) TerminateProcess(owner.Handle, 0xDEAD0003);
                if (owner.Handle != IntPtr.Zero) CloseHandle(owner.Handle);
            }
            if (ProcessHandle != IntPtr.Zero) CloseHandle(ProcessHandle);
            if (Output != null) SaveSummary();
            if (Log != null) Log.Dispose();
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test") return SelfTest();
        if (args.Length < 3 || args.Length > 4) throw new ArgumentException("Usage: WerOwnDumpCollector.exe <project .runtime NativeTests.exe> <suite|--full-regression> <project evidence directory> [timeout-seconds]");
        Root = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar);
        Target = Path.GetFullPath(args[0]); string suite = args[1]; string requestedOutput = Path.GetFullPath(args[2]);
        if (!Target.StartsWith(Root + "\\.runtime\\", StringComparison.OrdinalIgnoreCase) || !String.Equals(Path.GetFileName(Target), "NativeTests.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(Target)) throw new ArgumentException("Target must be an existing project .runtime NativeTests.exe.");
        if (!requestedOutput.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Evidence must stay inside the project.");
        FullRegression = suite == "--full-regression";
        if (!FullRegression && (suite.Length == 0 || suite.Any(value => !Char.IsLetterOrDigit(value) && value != '_'))) throw new ArgumentException("Invalid suite name.");
        int timeout = args.Length == 4 ? Int32.Parse(args[3], CultureInfo.InvariantCulture) : FullRegression ? 1800 : 240;
        if (timeout < 10 || timeout > (FullRegression ? 3600 : 1200)) throw new ArgumentException("Timeout outside supported bounded range.");
        // Publish the destination only after validation: Main's finally must
        // not save a summary to a rejected command-line path.
        Directory.CreateDirectory(requestedOutput); Output = requestedOutput;
        Log = new StreamWriter(Path.Combine(Output, "collector.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
        WerDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "WER", "Temp");
        foreach (string file in DumpFiles()) OldFiles.Add(file);
        EventCursor = LatestApplicationEvent();
        Write("Normal launch; no debugger/registry/policy change. target=" + Target + "; suite=" + suite);
        Write("WER directory=" + WerDirectory + "; excluded pre-existing dump files=" + OldFiles.Count + "; input desktop=" + DesktopName());
        File.WriteAllText(Path.Combine(Output, "target-sha256.txt"), Sha256(Target), new UTF8Encoding(false));
        using (var stdout = new FileStream(Path.Combine(Output, "stdout.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        using (var stderr = new FileStream(Path.Combine(Output, "stderr.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            SetHandleInformation(stdout.SafeFileHandle.DangerousGetHandle(), 1, 1); SetHandleInformation(stderr.SafeFileHandle.DangerousGetHandle(), 1, 1);
            var startup = new StartupInfo { Size = (uint)Marshal.SizeOf(typeof(StartupInfo)), Flags = 0x100,
                StdInput = GetStdHandle(-10), StdOutput = stdout.SafeFileHandle.DangerousGetHandle(), StdError = stderr.SafeFileHandle.DangerousGetHandle() };
            ProcessInformation process;
            // Normal process flags. In particular: no DEBUG_PROCESS, no
            // DEBUG_ONLY_THIS_PROCESS, no debugger attach at any later point.
            string command = "\"" + Target + "\"" + (FullRegression ? "" : " --suite " + suite);
            if (!CreateProcess(Target, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, true, 0x08000000, IntPtr.Zero, Root, ref startup, out process)) throw new Win32Exception();
            ProcessHandle = process.Process; ProcessId = process.ProcessId; CloseHandle(process.Thread); ChildStarted = true;
            FileTime created, ended, kernel, user;
            if (!GetProcessTimes(ProcessHandle, out created, out ended, out kernel, out user)) throw new Win32Exception();
            CreationTime = created.Value; Write("Owned PID=" + ProcessId + "; creation FILETIME=0x" + CreationTime.ToString("X"));
            if (FullRegression) { Owned.Add(ProcessId, new OwnedProcess { ProcessId=ProcessId, Creation=CreationTime, Handle=ProcessHandle, Image=Target, DiscoveredAt=Clock.ElapsedMilliseconds, Role="coordinator" }); SaveOwnedProcesses(); }
            long nextEventRead = 0; int exitPolls = 0;
            while (true)
            {
                if (!GetExitCodeProcess(ProcessHandle, out ChildExit)) throw new Win32Exception();
                ChildExited = ChildExit != 259;
                if (FullRegression) DiscoverAndPollOwnedProcesses();
                ScanNewDumpFiles();
                foreach (var held in Held.Where(item => !item.Copied).ToArray()) TryCopy(held, false);
                if (Clock.ElapsedMilliseconds >= nextEventRead) { ReadOwnEvents(); nextEventRead = Clock.ElapsedMilliseconds + 250; }
                if (ChildExited && (!FullRegression || Owned.Values.All(item => item.Exited)))
                {
                    if (++exitPolls >= 15) break; // bounded grace for WER finalization.
                }
                if (Clock.Elapsed.TotalSeconds > timeout && (!ChildExited || (FullRegression && Owned.Values.Any(item=>!item.Exited))))
                {
                    DeadlineTerminated = true; Write("Deadline: terminating only the child created by this collector.");
                    TerminateProcess(ProcessHandle, 0xDEAD0004);
                    if (FullRegression) foreach (var owner in Owned.Values.Where(item => !item.Exited && item.ProcessId != ProcessId)) TerminateProcess(owner.Handle,0xDEAD0004);
                }
                Thread.Sleep(FullRegression ? 20 : 100);
            }
            foreach (var held in Held.Where(item => !item.Copied).ToArray()) TryCopy(held, true);
            ReadOwnEvents();
            Write("Observed child exit=0x" + ChildExit.ToString("X8") + "; copied matching dumps=" + Captures.Count + "; complete=" + Captures.Count(item => (bool)item["StructurallyComplete"]));
            return ChildExit == 0 && !DeadlineTerminated && Failures.Count == 0 && (!FullRegression || Owned.Values.All(item=>item.Exited && item.ExitCode==0)) ? 0 : 1;
        }
    }

    private static OwnedProcess FindOwned(uint pid)
    {
        if (FullRegression) { OwnedProcess found; return Owned.TryGetValue(pid,out found) ? found : null; }
        return pid == ProcessId ? new OwnedProcess {ProcessId=ProcessId,Creation=CreationTime,Exited=ChildExited,Handle=ProcessHandle,Image=Target,Role="suite"} : null;
    }

    private static void DiscoverAndPollOwnedProcesses()
    {
        var candidates = new List<ProcessEntry>();
        IntPtr snapshot=CreateToolhelp32Snapshot(2,0);
        if (snapshot==new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(),"Owned-child process snapshot failed");
        try
        {
            var entry=new ProcessEntry {Size=(uint)Marshal.SizeOf(typeof(ProcessEntry))};
            if (Process32First(snapshot,ref entry)) do
            {
                if (!Owned.ContainsKey(entry.ProcessId) && String.Equals(entry.ExeName,Path.GetFileName(Target),StringComparison.OrdinalIgnoreCase)) candidates.Add(entry);
                entry.Size=(uint)Marshal.SizeOf(typeof(ProcessEntry));
            } while (Process32Next(snapshot,ref entry));
        }
        finally {CloseHandle(snapshot);}
        bool changed=false, progress;
        do
        {
            progress=false;
            foreach (var candidate in candidates.ToArray())
            {
                OwnedProcess parent; if (!Owned.TryGetValue(candidate.ParentProcessId,out parent)) continue;
                IntPtr handle=OpenProcess(0x1000|0x100000,false,candidate.ProcessId); if(handle==IntPtr.Zero) continue;
                bool accepted=false;
                try
                {
                    FileTime created,ended,kernel,user;
                    if(!GetProcessTimes(handle,out created,out ended,out kernel,out user)) continue;
                    if(created.Value<parent.Creation || (parent.ExitTime!=0 && created.Value>parent.ExitTime)) continue;
                    var image=new StringBuilder(32768); uint size=(uint)image.Capacity;
                    if(!QueryFullProcessImageName(handle,0,image,ref size) || !String.Equals(Path.GetFullPath(image.ToString()),Target,StringComparison.OrdinalIgnoreCase)) continue;
                    var child=new OwnedProcess {ProcessId=candidate.ProcessId,ParentId=candidate.ParentProcessId,Creation=created.Value,Handle=handle,Image=image.ToString(),Role="suite-descendant",DiscoveredAt=Clock.ElapsedMilliseconds};
                    Owned.Add(child.ProcessId,child); accepted=true; candidates.Remove(candidate); progress=changed=true;
                    Write("Verified owned suite descendant PID="+child.ProcessId+"; parent="+child.ParentId+"; creation=0x"+child.Creation.ToString("X")+"; exact image="+child.Image);
                }
                finally {if(!accepted) CloseHandle(handle);}
            }
        }while(progress);
        foreach(var owner in Owned.Values.Where(item=>!item.Exited))
        {
            uint exit; if(!GetExitCodeProcess(owner.Handle,out exit)) throw new Win32Exception(Marshal.GetLastWin32Error(),"Owned process exit query failed");
            if(exit!=259)
            {
                owner.Exited=true; owner.ExitCode=exit; owner.ExitedAt=Clock.ElapsedMilliseconds;
                FileTime created,ended,kernel,user; if(GetProcessTimes(owner.Handle,out created,out ended,out kernel,out user)) owner.ExitTime=ended.Value;
                Write("Owned process exited PID="+owner.ProcessId+"; role="+owner.Role+"; exit=0x"+exit.ToString("X8")); changed=true;
            }
        }
        if(changed) SaveOwnedProcesses();
    }

    private static object OwnedRecords()
    {
        return Owned.Values.OrderBy(item=>item.DiscoveredAt).Select(item=>new Dictionary<string,object> {
            {"ProcessId",item.ProcessId},{"ParentProcessId",item.ParentId},{"CreationFileTime","0x"+item.Creation.ToString("X")},{"Image",item.Image},{"Role",item.Role},
            {"DiscoveredAtMilliseconds",item.DiscoveredAt},{"Exited",item.Exited},{"ExitCode",item.Exited ? (object)item.ExitCode : null},{"ExitHex",item.Exited ? "0x"+item.ExitCode.ToString("X8") : null},
            {"ExitFileTime","0x"+item.ExitTime.ToString("X")},{"ExitedAtMilliseconds",item.Exited ? (object)item.ExitedAt : null}
        }).ToArray();
    }
    private static void SaveOwnedProcesses()
    {
        File.WriteAllText(Path.Combine(Output,"owned-processes.json"),Json.Serialize(new Dictionary<string,object> {
            {"Kind","verified-exact-test-executable-own-process-tree"},{"PollMilliseconds",20},{"DebuggerAttached",false},{"Retry",false},
            {"Verification","Toolhelp parent chain from created coordinator, GetProcessTimes creation bounds, retained process handle, QueryFullProcessImageName exact target"},
            {"Coverage","Records processes discovered by polling; short-lived normal children may complete between snapshots. Coordinator stdout remains authoritative for all suite results."},{"Processes",OwnedRecords()}
        }),new UTF8Encoding(false));
    }

    private static IEnumerable<string> DumpFiles()
    {
        if (!Directory.Exists(WerDirectory)) throw new DirectoryNotFoundException(WerDirectory);
        return Directory.GetFiles(WerDirectory).Where(path => path.EndsWith(".mdmp", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".hdmp", StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static void ScanNewDumpFiles()
    {
        foreach (string path in DumpFiles())
        {
            if (OldFiles.Contains(path) || CopiedFiles.Contains(path) || Held.Any(item => String.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            FileStream stream = null;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                DumpHeader header = ReadHeader(stream, false);
                if (header.ProcessId == 0) { PendingHeaderReads++; continue; }
                OwnedProcess owner = FindOwned(header.ProcessId);
                if (owner == null) { OtherPidFiles++; OldFiles.Add(path); continue; }
                uint creationSeconds = (uint)((DateTime.FromFileTimeUtc(unchecked((long)owner.Creation)) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
                bool timedIdentity = header.ProcessCreationSeconds.HasValue && header.ProcessCreationSeconds.Value == creationSeconds;
                if (header.ProcessCreationSeconds.HasValue && !timedIdentity) { OldFiles.Add(path); Write("Ignored PID reuse / creation-time mismatch (no dump copied)."); continue; }
                if (owner.Exited && !timedIdentity) { Write("Cannot establish ownership of post-exit dump without creation time; no dump copied."); OldFiles.Add(path); continue; }
                var held = new HeldDump { Path = path, Stream = stream, MatchedAt = Clock.ElapsedMilliseconds, CreationTimeMatched = timedIdentity, Owner=owner };
                Held.Add(held); stream = null;
                Write("Retained matching own-PID WER handle; PID=" + header.ProcessId + "; bytes=" + held.Stream.Length + "; creation matched=" + timedIdentity + "; file=" + path + "; desktop=" + DesktopName());
            }
            catch (IOException) { PendingHeaderReads++; }
            catch (UnauthorizedAccessException error) { Write("Read-only WER candidate access failed: " + error.Message); }
            finally { if (stream != null) stream.Dispose(); }
        }
    }

    private static void TryCopy(HeldDump held, bool final)
    {
        try
        {
            long length = held.Stream.Length;
            if (length != held.LastLength) { held.LastLength = length; held.StableSince = Clock.ElapsedMilliseconds; if (!final) return; }
            if (!final && Clock.ElapsedMilliseconds - held.StableSince < 300) return;
            DumpHeader header = ReadHeader(held.Stream, true);
            if (!header.Complete && !final) return;
            FileStream closedWriterProbe = null;
            try { closedWriterProbe = new FileStream(held.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
            catch (IOException) { if (!final) return; }
            // A successful read handle without ShareWrite proves there is no
            // current writer, and keeps the snapshot immutable while copied.
            using (closedWriterProbe)
            {
            string destination = Path.Combine(Output, "own-wer-" + (Captures.Count + 1).ToString("000") + Path.GetExtension(held.Path).ToLowerInvariant());
            held.Stream.Position = 0;
            using (var copy = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                byte[] buffer = new byte[1024 * 1024]; long remaining = length;
                while (remaining > 0) { int read = held.Stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining)); if (read <= 0) break; copy.Write(buffer, 0, read); remaining -= read; }
            }
            DumpHeader copied;
            using (var copy = File.OpenRead(destination)) copied = ReadHeader(copy, true);
            bool complete = copied.Complete && copied.ProcessId == held.Owner.ProcessId && new FileInfo(destination).Length == length && held.Stream.Length == length;
            if (held.Stream.Length != length && !final) { File.Delete(destination); held.LastLength = held.Stream.Length; held.StableSince = Clock.ElapsedMilliseconds; return; }
            Captures.Add(new Dictionary<string, object> {
                {"SourceWerPath",held.Path},{"File",destination},{"Bytes",new FileInfo(destination).Length},{"SHA256",Sha256(destination)},
                {"ProcessId",copied.ProcessId},{"CreationTimeMatched",held.CreationTimeMatched},{"StructurallyComplete",complete},{"OwnedRole",held.Owner.Role},{"OwnerCreationFileTime","0x"+held.Owner.Creation.ToString("X")},
                {"Validation",copied.Validation},{"WriterClosedVerified",closedWriterProbe!=null},{"PostExitFinalizationGrace",final},{"ExceptionCode",copied.ExceptionCode.HasValue ? "0x"+copied.ExceptionCode.Value.ToString("X8") : null},
                {"ExceptionThreadId",copied.ExceptionThread},{"RequiredDataEnd",copied.RequiredEnd},{"HeldAtMilliseconds",held.MatchedAt},{"CopiedAtMilliseconds",Clock.ElapsedMilliseconds}
            });
            held.Copied = true; CopiedFiles.Add(held.Path);
            Write("Copied matching WER dump: complete=" + complete + "; bytes=" + length + "; " + destination);
            SaveSummary();
            }
        }
        catch (IOException error) { if (final) Failures.Add("Final matching-dump copy: " + error.Message); }
        catch (InvalidDataException error) { if (final) Failures.Add("Final matching-dump validation: " + error.Message); }
    }

    private static DumpHeader ReadHeader(Stream stream, bool validateAll)
    {
        var result = new DumpHeader { Validation = "header incomplete" };
        long length = stream.Length; if (length < 32) return result;
        var reader = new BinaryReader(stream, Encoding.UTF8, true); stream.Position = 0;
        if (reader.ReadUInt32() != 0x504D444D) { result.Validation = "not MDMP"; return result; }
        reader.ReadUInt32(); uint count = reader.ReadUInt32(), directory = reader.ReadUInt32();
        if (count > 4096 || count == 0 || (ulong)directory + count * 12UL > (ulong)length) return result;
        bool allBounds = true, threads = false, modules = false, misc = false; ulong required = (ulong)directory + count * 12UL;
        var memory = new List<Tuple<uint, uint>>();
        for (uint index = 0; index < count; index++)
        {
            stream.Position = directory + index * 12L; uint kind = reader.ReadUInt32(), size = reader.ReadUInt32(), rva = reader.ReadUInt32();
            ulong end = (ulong)rva + size; required = Math.Max(required, end); if (end > (ulong)length) { allBounds = false; continue; }
            if (kind == 15 && size >= 12)
            {
                stream.Position = rva; uint infoSize = reader.ReadUInt32(), flags = reader.ReadUInt32();
                if (infoSize >= 12 && (flags & 1) != 0) { result.ProcessId = reader.ReadUInt32(); misc = true; if ((flags & 2) != 0 && size >= 24 && infoSize >= 24) result.ProcessCreationSeconds = reader.ReadUInt32(); }
            }
            else if (validateAll && kind == 3 && size >= 4)
            {
                stream.Position = rva; uint threadCount = reader.ReadUInt32();
                threads = threadCount > 0 && threadCount <= 65536 && 4UL + threadCount * 48UL <= size;
                if (!threads) allBounds = false;
                else for (uint thread = 0; thread < threadCount; thread++)
                {
                    stream.Position = rva + 4L + thread * 48L + 32; uint stackBytes = reader.ReadUInt32(), stackRva = reader.ReadUInt32(), contextBytes = reader.ReadUInt32(), contextRva = reader.ReadUInt32();
                    ulong stackEnd = (ulong)stackRva + stackBytes, contextEnd = (ulong)contextRva + contextBytes;
                    required = Math.Max(required, Math.Max(stackEnd, contextEnd)); if (stackEnd > (ulong)length || contextEnd > (ulong)length || contextBytes == 0) allBounds = false;
                }
            }
            else if (validateAll && kind == 4 && size >= 4)
            {
                stream.Position = rva; uint moduleCount = reader.ReadUInt32();
                modules = moduleCount > 0 && moduleCount <= 65536 && 4UL + moduleCount * 108UL <= size;
                if (!modules) allBounds = false;
                else for (uint module = 0; module < moduleCount; module++)
                {
                    long moduleOffset = rva + 4L + module * 108L; stream.Position = moduleOffset + 20; uint nameRva = reader.ReadUInt32();
                    if ((ulong)nameRva + 4 > (ulong)length) allBounds = false;
                    else { stream.Position = nameRva; uint nameBytes = reader.ReadUInt32(); ulong nameEnd = (ulong)nameRva + 4 + nameBytes; required = Math.Max(required, nameEnd); if (nameEnd > (ulong)length) allBounds = false; }
                    stream.Position = moduleOffset + 76;
                    for (int descriptor = 0; descriptor < 2; descriptor++) { uint bytes = reader.ReadUInt32(), dataRva = reader.ReadUInt32(); ulong dataEnd = (ulong)dataRva + bytes; required = Math.Max(required, dataEnd); if (dataEnd > (ulong)length) allBounds = false; }
                }
            }
            else if (validateAll && kind == 6 && size >= 40) { stream.Position = rva; result.ExceptionThread = reader.ReadUInt32(); reader.ReadUInt32(); result.ExceptionCode = reader.ReadUInt32(); }
            else if (validateAll && (kind == 9 || kind == 5)) memory.Add(Tuple.Create(kind, rva));
        }
        if (!validateAll) return result;
        foreach (var item in memory)
        {
            stream.Position = item.Item2;
            if (item.Item1 == 9)
            {
                if ((ulong)item.Item2 + 16 > (ulong)length) { allBounds = false; continue; }
                ulong ranges = reader.ReadUInt64(), data = reader.ReadUInt64();
                if (ranges > 10000000 || (ulong)item.Item2 + 16 + ranges * 16 > (ulong)length) { allBounds = false; continue; }
                for (ulong range = 0; range < ranges; range++) { reader.ReadUInt64(); ulong bytes = reader.ReadUInt64(); data = checked(data + bytes); }
                required = Math.Max(required, data); if (data > (ulong)length) allBounds = false;
            }
            else
            {
                uint ranges = reader.ReadUInt32();
                if (ranges > 10000000 || (ulong)item.Item2 + 4 + ranges * 16UL > (ulong)length) { allBounds = false; continue; }
                for (uint range = 0; range < ranges; range++) { reader.ReadUInt64(); uint bytes = reader.ReadUInt32(), rva = reader.ReadUInt32(); ulong end = (ulong)rva + bytes; required = Math.Max(required, end); if (end > (ulong)length) allBounds = false; }
            }
        }
        result.RequiredEnd = required; result.Complete = allBounds && threads && modules && misc;
        result.Validation = result.Complete ? "MDMP directory and thread/context/module/memory references within captured bytes; nonempty thread/module/misc streams present" : "partial or missing required streams/data";
        return result;
    }

    private static long LatestApplicationEvent()
    {
        using (var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, "*") { ReverseDirection = true }))
        using (var record = reader.ReadEvent()) return record == null ? 0 : record.RecordId ?? 0;
    }

    private static void ReadOwnEvents()
    {
        string query = "*[System[(EventID=1000 or EventID=1026) and EventRecordID > " + EventCursor.ToString(CultureInfo.InvariantCulture) + "]]";
        using (var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, query)))
        {
            EventRecord record;
            while ((record = reader.ReadEvent()) != null)
            using (record)
            {
                EventCursor = Math.Max(EventCursor, record.RecordId ?? EventCursor);
                var document = new XmlDocument { XmlResolver = null }; document.LoadXml(record.ToXml());
                var data = new Dictionary<string, string>(); foreach (XmlNode node in document.SelectNodes("/*[local-name()='Event']/*[local-name()='EventData']/*[local-name()='Data']")) if (node.Attributes["Name"] != null) data[node.Attributes["Name"].Value] = node.InnerText;
                string pid, path; ulong parsed;
                if (record.Id == 1000 && data.TryGetValue("ProcessId", out pid) && TryHex(pid, out parsed) && parsed<=UInt32.MaxValue && FindOwned((uint)parsed)!=null && data.TryGetValue("AppPath", out path) && String.Equals(Path.GetFullPath(path), Target, StringComparison.OrdinalIgnoreCase))
                {
                    OwnedProcess owner=FindOwned((uint)parsed);
                    string stamp; ulong creation;
                    if (data.TryGetValue("ProcessCreationTime", out stamp) && TryHex(stamp, out creation) && creation != owner.Creation) continue;
                    string destination = Path.Combine(Output, "application-1000-" + record.RecordId + ".xml"); File.WriteAllText(destination, record.ToXml(), new UTF8Encoding(false));
                    Write("Confirmed own Application1000; PID=" + owner.ProcessId + "; stillActive=" + !owner.Exited + "; exception=" + (data.ContainsKey("ExceptionCode") ? data["ExceptionCode"] : "unknown"));
                }
            }
        }
    }

    private static bool TryHex(string text, out ulong value) { return UInt64.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text.Substring(2) : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value); }
    private static string Sha256(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", ""); }
    private static void Write(string text) { string line = Clock.ElapsedMilliseconds + "ms " + text; Console.WriteLine(line); if (Log != null) Log.WriteLine(line); }
    private static void SaveSummary()
    {
        Directory.CreateDirectory(Output);
        var result = new Dictionary<string, object> {
            {"Kind","owned-test-normal-launch-WER-collector"},{"DebuggerAttached",false},{"SystemPolicyChanged",false},{"Target",Target},{"ProcessId",ProcessId},
            {"FullRegression",FullRegression},{"AutomaticRetry",false},{"OwnedProcesses",FullRegression ? OwnedRecords() : null},
            {"ProcessCreationFileTime","0x"+CreationTime.ToString("X")},{"ChildStarted",ChildStarted},{"ChildExited",ChildExited},{"ChildExitHex","0x"+ChildExit.ToString("X8")},
            {"DeadlineTerminated",DeadlineTerminated},{"CapturedFiles",Captures},{"CompleteDumps",Captures.Count(item=>(bool)item["StructurallyComplete"])},
            {"ExcludedOldFiles",OldFiles.Count},{"OtherPidHeadersIgnored",OtherPidFiles},{"PendingHeaderReads",PendingHeaderReads},{"Failures",Failures},{"ElapsedMilliseconds",Clock.ElapsedMilliseconds},
            {"Outcome",DeadlineTerminated ? "deadline" : Captures.Any(item=>(bool)item["StructurallyComplete"]) ? "captured_own_WER_evidence" : ChildExited && ChildExit==0 ? "normal_exit_no_matching_dump_NOT_a_fix" : "fault_or_incomplete_no_complete_dump"}
        };
        File.WriteAllText(Path.Combine(Output, "collector-result.json"), Json.Serialize(result), new UTF8Encoding(false));
    }
    private static int SelfTest()
    {
        if(Marshal.SizeOf(typeof(ProcessEntry))!=568 || Marshal.OffsetOf(typeof(ProcessEntry),"ParentProcessId").ToInt32()!=32 || Marshal.OffsetOf(typeof(ProcessEntry),"ExeName").ToInt32()!=44) throw new InvalidDataException("Toolhelp Win64 ABI mismatch");
        using (var stream = new MemoryStream(new byte[4096]))
        {
            var writer = new BinaryWriter(stream); writer.Write(0x504D444DU); writer.Write(42899U); writer.Write(3U); writer.Write(32U);
            stream.Position = 32; writer.Write(15U); writer.Write(24U); writer.Write(80U); writer.Write(3U); writer.Write(52U); writer.Write(128U); writer.Write(4U); writer.Write(112U); writer.Write(256U);
            stream.Position = 80; writer.Write(24U); writer.Write(3U); writer.Write(12345U); writer.Write(1000000U); writer.Write(0U); writer.Write(0U);
            stream.Position = 128; writer.Write(1U); writer.Write(123U); stream.Position = 132 + 32; writer.Write(128U); writer.Write(2048U); writer.Write(1232U); writer.Write(512U);
            stream.Position = 256; writer.Write(1U); writer.Write(0x10000000UL); writer.Write(0x10000U); stream.Position = 260 + 20; writer.Write(3072U);
            stream.Position = 3072; byte[] name = Encoding.Unicode.GetBytes("NativeTests.exe"); writer.Write((uint)name.Length); writer.Write(name);
            var header = ReadHeader(stream, true); if (header.ProcessId != 12345 || header.ProcessCreationSeconds != 1000000 || !header.Complete) throw new InvalidDataException("Synthetic minidump PID/bounds self-test failed.");
            stream.SetLength(1024); var partial = ReadHeader(stream, true); if (partial.Complete) throw new InvalidDataException("Truncated dump was incorrectly accepted as complete.");
            Console.WriteLine("Non-UI synthetic header self-test passed; PID/creation and complete-vs-truncated references validated.");
        }
        return 0;
    }
    private static string DesktopName() { IntPtr desktop = OpenInputDesktop(0,false,1); if (desktop==IntPtr.Zero) return "unavailable:"+Marshal.GetLastWin32Error(); try {var text=new StringBuilder(512);uint needed;return GetUserObjectInformation(desktop,2,text,1024,out needed)?text.ToString():"unavailable:"+Marshal.GetLastWin32Error();}finally{CloseDesktop(desktop);} }
    private sealed class HeldDump { internal string Path; internal FileStream Stream; internal bool Copied, CreationTimeMatched; internal long LastLength, StableSince, MatchedAt; internal OwnedProcess Owner; }
    private sealed class OwnedProcess {internal uint ProcessId,ParentId,ExitCode;internal ulong Creation,ExitTime;internal IntPtr Handle;internal string Image,Role;internal bool Exited;internal long DiscoveredAt,ExitedAt;}
    private sealed class DumpHeader { internal uint ProcessId; internal uint? ProcessCreationSeconds, ExceptionCode, ExceptionThread; internal bool Complete; internal ulong RequiredEnd; internal string Validation; }
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { internal uint Low,High; internal ulong Value {get{return ((ulong)High<<32)|Low;}} }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct StartupInfo { internal uint Size; internal string Reserved,Desktop,Title; internal uint X,Y,XSize,YSize,XChars,YChars,Fill,Flags; internal ushort ShowWindow,Reserved2; internal IntPtr ReservedPointer,StdInput,StdOutput,StdError; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { internal IntPtr Process,Thread; internal uint ProcessId,ThreadId; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct ProcessEntry {internal uint Size,Usage,ProcessId;internal UIntPtr DefaultHeap;internal uint ModuleId,Threads,ParentProcessId;internal int Priority;internal uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]internal string ExeName;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="CreateProcessW")] private static extern bool CreateProcess(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string directory,ref StartupInfo startup,out ProcessInformation process);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetExitCodeProcess(IntPtr process,out uint exit);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetProcessTimes(IntPtr process,out FileTime creation,out FileTime exit,out FileTime kernel,out FileTime user);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetHandleInformation(IntPtr handle,uint mask,uint flags);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int standard);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool TerminateProcess(IntPtr process,uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,EntryPoint="Process32FirstW",SetLastError=true)]private static extern bool Process32First(IntPtr snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,EntryPoint="Process32NextW",SetLastError=true)]private static extern bool Process32Next(IntPtr snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder path,ref uint size);
    [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="GetUserObjectInformationW")] private static extern bool GetUserObjectInformation(IntPtr handle,int index,StringBuilder text,uint length,out uint needed);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
}
