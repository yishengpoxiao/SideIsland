using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Diagnostics.Runtime;

// Offline Win64 native unwinder for dumps captured from this project's test
// helpers. The dump is read-only. No process is opened or attached, and symbol
// server/environment paths and embedded CodeView paths are not used.
internal static class AnalyzeNativeDump
{
    private const uint MachineAmd64 = 0x8664;
    private static readonly IntPtr SymbolKey = new IntPtr(0x43494C41);
    private static readonly ReadMemoryCallback ReadCallback = ReadDumpMemory;
    private static readonly FunctionTableCallback TableCallback = FindRuntimeFunction;
    private static readonly ModuleBaseCallback BaseCallback = FindModuleBase;
    private static IDataReader Reader;
    private static DumpMetadata Metadata;
    private static StreamWriter Log;
    private static string CallbackFailure;
    private static bool SymbolsInitialized;
    private static int ImageReadCount, FunctionReadCount;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--self-test")
            {
                ValidateAbi(); Console.WriteLine("PASS: offline native analyzer Win64 ABI; no dump/process/UI accessed."); return 0;
            }
            if (args.Length < 2 || args.Length > 3) throw new ArgumentException("Usage: AnalyzeNativeDump.exe <project-dump> <project-output.txt> [project-local-validated-PDB-directory]");
            ValidateAbi();
            string root = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string dump = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
            string extension = Path.GetExtension(dump);
            if (!dump.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !output.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                String.Equals(dump, output, StringComparison.OrdinalIgnoreCase) || !(String.Equals(extension, ".dmp", StringComparison.OrdinalIgnoreCase) || String.Equals(extension, ".mdmp", StringComparison.OrdinalIgnoreCase) || String.Equals(extension, ".hdmp", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Read-only input dump and separate output must both be inside the project.");
            string hash = FileHash(dump);
            string pdbDirectory = args.Length == 3 ? Path.GetFullPath(args[2]).TrimEnd(Path.DirectorySeparatorChar) : null;
            if (pdbDirectory != null && (!pdbDirectory.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(pdbDirectory))) throw new ArgumentException("Optional symbols must be an existing local project directory.");
            Metadata = new DumpMetadata(dump);
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            string localSymbols = Path.Combine(Path.GetDirectoryName(output), "offline-symbols"); Directory.CreateDirectory(localSymbols);
            using (Log = new StreamWriter(output, false, new UTF8Encoding(false)))
            using (var target = DataTarget.LoadCrashDump(dump, CrashDumpReader.ClrMD))
            {
                Log.AutoFlush = true; Reader = target.DataReader;
                if (Reader.GetPointerSize() != 8) throw new InvalidOperationException("Only AMD64 dumps are supported.");
                Log.WriteLine("Dump=" + dump + "; SHA256=" + hash + "; bytes=" + new FileInfo(dump).Length);
                Log.WriteLine("OFFLINE ONLY: no live process/thread handles, no remote reads, no network symbols, no dump modifications.");
                Log.WriteLine("CONTEXT: preserve first 1232 bytes of AMD64 CONTEXT; ignore only the XSTATE tail/flag for legacy readers.");
                Log.WriteLine("Unwinding stops at unknown/JIT module boundaries, missing or mismatched images, invalid/non-executable PCs, unreadable or non-monotonic stacks. Raw stack values are never reported as frames.");
                // Explicit local search path plus these options prevent image
                // CodeView paths or ambient symbol-server configuration usage.
                uint options = 0x2 | 0x4 | 0x80 | 0x200 | 0x1000 | 0x20000 | 0x80000 | 0x200000 | 0x2000000;
                SymSetOptions(options);
                SymbolsInitialized = SymInitialize(SymbolKey, localSymbols, false);
                if (!SymbolsInitialized) throw new Win32Exception(Marshal.GetLastWin32Error(), "SymInitialize offline key failed");
                Log.WriteLine("DbgHelp=" + Path.Combine(Environment.SystemDirectory, "dbghelp.dll") + "; options=0x" + options.ToString("X8") + "; search=" + localSymbols);
                foreach (var module in Metadata.Modules)
                {
                    module.LoadImage();
                    if (module.Image != null && module.IdentityMatches)
                    {
                        string symbolImage = module.Path;
                        if (pdbDirectory != null && module.Image.PdbName != null)
                        {
                            string localPdb = Path.Combine(pdbDirectory, module.Image.PdbName);
                            if (File.Exists(localPdb))
                            {
                                PdbIdentity identity = ReadPdbIdentity(localPdb);
                                // Microsoft's PDB1::OpenValidate4 requires GUID equality,
                                // PDB age >= image age, and DBI age == image age.
                                // Public stripping can advance only the PDB info age.
                                if (identity.Guid != module.Image.PdbGuid || identity.Age < module.Image.PdbAge || identity.DbiAge != module.Image.PdbAge) throw new InvalidDataException("PDB GUID/Age does not match verified PE: " + localPdb + "; PDB=" + identity.Guid + "/" + identity.Age + "; DBI=" + identity.DbiAge + "; PE=" + module.Image.PdbGuid + "/" + module.Image.PdbAge);
                                symbolImage = localPdb; module.PdbLoaded = true;
                                Log.WriteLine("PDB " + localPdb + "; PE-RSDS-GUID=" + identity.Guid + "; PE-Age=" + module.Image.PdbAge + "; PDB-Info-Age=" + identity.Age + "; PDB-DBI-Age=" + identity.DbiAge + "; SHA256=" + FileHash(localPdb));
                            }
                        }
                        ulong loaded = SymLoadModuleEx(SymbolKey, IntPtr.Zero, symbolImage, Path.GetFileNameWithoutExtension(module.Path), module.Base, module.Size, IntPtr.Zero, 0);
                        module.PdbLoaded = module.PdbLoaded && loaded == module.Base;
                        module.UnwindLoaded = true; // Direct, verified .pdata lookup below; no dependence on deferred symbols.
                        if (loaded != module.Base) module.ImageStatus += "; SymLoadModuleEx error=" + Marshal.GetLastWin32Error();
                    }
                    Log.WriteLine("MODULE " + module.Base.ToString("X16") + " size=0x" + module.Size.ToString("X") + " timestamp=0x" + module.Timestamp.ToString("X8") + " image=" + module.ImageStatus + " unwind=" + module.UnwindLoaded + (module.Image == null ? "" : "; verified-PE-function-count=" + module.Image.FunctionCount) + " " + module.Path);
                }
                if (Metadata.Exception != null)
                {
                    var exception = Metadata.Exception;
                    Log.WriteLine("EXCEPTION STREAM: TID=" + exception.ThreadId + "; code=0x" + exception.Code.ToString("X8") + "; address=" + Resolve(exception.Address));
                    for (int index = 0; index < exception.Parameters.Length; index++) Log.WriteLine("  PARAM[" + index + "]=0x" + exception.Parameters[index].ToString("X16"));
                    ThreadRecord thread; Metadata.Threads.TryGetValue(exception.ThreadId, out thread);
                    Log.WriteLine("FAULT THREAD (exception stream context preferred)");
                    Walk(exception.ThreadId, exception.Context, thread);
                }
                else Log.WriteLine("NO EXCEPTION STREAM: no true fault thread can be identified from this dump.");
                Log.WriteLine("ALL THREADS (thread-list capture contexts)");
                foreach (var pair in Metadata.Threads.OrderBy(item => item.Key)) Walk(pair.Key, pair.Value.Context, pair.Value);
                Log.WriteLine("END: native-only partial chains are expected at managed/JIT boundaries; use the separate managed dump analyzer there.");
            }
            Reader = null;
            if (!String.Equals(hash, FileHash(dump), StringComparison.Ordinal)) throw new IOException("Input dump hash changed during analysis.");
            Console.WriteLine(output); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (SymbolsInitialized) SymCleanup(SymbolKey); if (Metadata != null) foreach (var module in Metadata.Modules) if (module.Image != null) module.Image.Dispose(); SymbolsInitialized = false; Reader = null; Log = null; Metadata = null; }
    }

    private static void Walk(uint threadId, byte[] rawContext, ThreadRecord thread)
    {
        if (rawContext == null || rawContext.Length < 1232) { Log.WriteLine("THREAD " + threadId + " PARTIAL: no standard AMD64 context."); return; }
        byte[] context = (byte[])rawContext.Clone();
        uint flags = BitConverter.ToUInt32(context, 0x30) & ~0x40U;
        if ((flags & 0x100000) == 0) { Log.WriteLine("THREAD " + threadId + " PARTIAL: context is not AMD64."); return; }
        Array.Copy(BitConverter.GetBytes(flags), 0, context, 0x30, 4);
        ulong rip = BitConverter.ToUInt64(context, 0xF8), rsp = BitConverter.ToUInt64(context, 0x98), rbp = BitConverter.ToUInt64(context, 0xA0);
        ulong low = thread == null ? 0 : thread.StackStart, high = thread == null ? 0 : thread.StackStart + thread.StackSize;
        if (thread != null && thread.Teb != 0)
        {
            byte[] tib = new byte[24]; int got;
            if (Reader.ReadMemory(thread.Teb, tib, tib.Length, out got) && got == tib.Length)
            {
                ulong tebHigh = BitConverter.ToUInt64(tib, 8), tebLow = BitConverter.ToUInt64(tib, 16);
                if (tebLow < tebHigh && rsp >= tebLow && rsp < tebHigh) { low = tebLow; high = tebHigh; }
            }
        }
        Log.WriteLine("THREAD " + threadId + (Metadata.Exception != null && threadId == Metadata.Exception.ThreadId ? " FAULT" : "") + "; RIP=" + Resolve(rip) + "; RSP=0x" + rsp.ToString("X16") + "; RBP=0x" + rbp.ToString("X16") + "; stack=[0x" + low.ToString("X16") + ",0x" + high.ToString("X16") + ")");
        using (var native = new ContextBuffer(context))
        {
            var frame = new StackFrame64 {
                PC = new Address64 { Offset = rip, Mode = 3 }, Stack = new Address64 { Offset = rsp, Mode = 3 }, Frame = new Address64 { Offset = rbp, Mode = 3 },
                Parameters = new ulong[4], Reserved = new ulong[3], Helper = new KdHelp64 { Reserved = new ulong[2] }
            };
            string reason; ModuleRecord module;
            if (!ValidFrame(rip, rsp, low, high, out module, out reason)) { Log.WriteLine("  PARTIAL at initial context: " + reason); return; }
            Log.WriteLine("  000 SP=" + rsp.ToString("X16") + " " + Resolve(rip));
            if (!module.UnwindLoaded) { Log.WriteLine("  PARTIAL: image unwind metadata was not loaded."); return; }
            var visited = new HashSet<string>(); visited.Add(rip.ToString("X") + ":" + rsp.ToString("X"));
            ulong lastPc = rip, lastSp = rsp; int printed = 1;
            for (int step = 0; step < 200; step++)
            {
                CallbackFailure = null;
                ImageReadCount = 0; FunctionReadCount = 0;
                bool success = StackWalk64(MachineAmd64, SymbolKey, new IntPtr(unchecked((long)threadId)), ref frame, native.Pointer, ReadCallback, TableCallback, BaseCallback, IntPtr.Zero);
                if (!success) { Log.WriteLine("  PARTIAL: StackWalk64 stopped; error=" + Marshal.GetLastWin32Error() + (CallbackFailure == null ? "" : "; memory=" + CallbackFailure)); return; }
                if (CallbackFailure != null) { Log.WriteLine("  PARTIAL: callback could not supply verified data: " + CallbackFailure + "; candidate not emitted."); return; }
                if (step == 0 && frame.PC.Offset == lastPc && frame.Stack.Offset == lastSp) continue;
                if (frame.PC.Offset == 0) { Log.WriteLine("  END: zero return PC."); return; }
                if (frame.Stack.Offset < lastSp) { Log.WriteLine("  PARTIAL: stack pointer moved backwards; candidate not emitted."); return; }
                string key = frame.PC.Offset.ToString("X") + ":" + frame.Stack.Offset.ToString("X");
                if (!visited.Add(key)) { Log.WriteLine("  PARTIAL: repeated PC/SP; candidate not emitted."); return; }
                if (!ValidFrame(frame.PC.Offset, frame.Stack.Offset, low, high, out module, out reason))
                { Log.WriteLine("  PARTIAL: " + reason + "; candidate PC=0x" + frame.PC.Offset.ToString("X16") + " SP=0x" + frame.Stack.Offset.ToString("X16") + " not emitted as a frame."); return; }
                Log.WriteLine("  " + printed.ToString("000") + " SP=" + frame.Stack.Offset.ToString("X16") + " " + Resolve(frame.PC.Offset) + "; verified-function-lookups=" + FunctionReadCount + "; local-image-fallback-reads=" + ImageReadCount);
                printed++; lastPc = frame.PC.Offset; lastSp = frame.Stack.Offset;
                if (!module.UnwindLoaded) { Log.WriteLine("  PARTIAL: matching image unwind metadata unavailable at this frame."); return; }
            }
            Log.WriteLine("  PARTIAL: 200-step bounded unwind limit reached.");
        }
    }

    private static bool ValidFrame(ulong pc, ulong sp, ulong low, ulong high, out ModuleRecord module, out string reason)
    {
        module = Metadata.FindModule(pc); reason = null;
        if (module == null) { reason = "unknown/JIT PC boundary (native module absent)"; return false; }
        if (!module.IdentityMatches || module.Image == null) { reason = "missing/mismatched image for " + module.Path; return false; }
        if (!module.Image.IsExecutable(pc - module.Base)) { reason = "PC is outside executable PE sections"; return false; }
        MemoryRegion region = Metadata.FindRegion(pc);
        if (region != null && (region.State != 0x1000 || (region.Protect & 0xF0) == 0 || (region.Protect & 0x100) != 0))
        { reason = "PC is non-executable/guard/uncommitted in dump memory map"; return false; }
        if (sp == 0 || (sp & 7) != 0 || (high > low && (sp < low || sp >= high))) { reason = "SP outside captured thread stack bounds or unaligned"; return false; }
        byte[] value = new byte[8]; int got;
        if (!Reader.ReadMemory(sp, value, value.Length, out got) || got != value.Length) { reason = "stack memory unavailable"; return false; }
        return true;
    }

    private static bool ReadDumpMemory(IntPtr key, ulong address, IntPtr output, uint size, out uint read)
    {
        read = 0;
        try
        {
            if (key != SymbolKey || size > 16 * 1024 * 1024 || Reader == null) return false;
            int got; bool success = Reader.ReadMemory(address, output, (int)size, out got);
            read = (uint)Math.Max(0, got);
            // Minidumps need not contain every read-only image page. Unwind
            // data/code may come from the exact timestamp/size/checksum image;
            // captured stack/heap bytes are never synthesized from local files.
            if (!success || read != size)
            {
                var module = Metadata.FindModule(address);
                if (module != null && module.Image != null && module.IdentityMatches)
                {
                    byte[] imageBytes;
                    if (module.Image.ReadVirtual(address - module.Base, (int)size, out imageBytes))
                    { Marshal.Copy(imageBytes, 0, output, imageBytes.Length); read = size; ImageReadCount++; return true; }
                }
            }
            if (!success || read != size) CallbackFailure = "0x" + address.ToString("X16") + " requested=" + size + " got=" + read;
            return success && read == size;
        }
        catch (Exception error) { CallbackFailure = error.GetType().Name + ": " + error.Message; return false; }
    }

    private static IntPtr FindRuntimeFunction(IntPtr key, ulong address)
    {
        try
        {
            if (key != SymbolKey || Metadata == null) return IntPtr.Zero;
            var module = Metadata.FindModule(address);
            if (module == null || module.Image == null || !module.IdentityMatches) return IntPtr.Zero;
            IntPtr entry = module.Image.RuntimeFunction(address - module.Base);
            if (entry != IntPtr.Zero) FunctionReadCount++;
            return entry;
        }
        catch (Exception error) { CallbackFailure = "function-table " + error.GetType().Name + ": " + error.Message; return IntPtr.Zero; }
    }

    private static ulong FindModuleBase(IntPtr key, ulong address)
    {
        if (key != SymbolKey || Metadata == null) return 0;
        var module = Metadata.FindModule(address); return module == null ? 0 : module.Base;
    }

    private static string Resolve(ulong address)
    {
        if (address == 0) return "0";
        var module = Metadata.FindModule(address);
        if (module == null) return "0x" + address.ToString("X16") + " <unknown/JIT>";
        string result = Path.GetFileName(module.Path) + "+0x" + (address - module.Base).ToString("X") + " (0x" + address.ToString("X16") + ")";
        if (module.PdbLoaded)
        {
            IntPtr symbol = Marshal.AllocHGlobal(88 + 2048);
            try
            {
                for (int index = 0; index < 88 + 2048; index++) Marshal.WriteByte(symbol, index, 0);
                Marshal.WriteInt32(symbol, 0, 88); Marshal.WriteInt32(symbol, 80, 2048);
                ulong displacement;
                if (SymFromAddr(SymbolKey, address, out displacement, symbol))
                {
                    int length = Marshal.ReadInt32(symbol, 76);
                    if (length >= 0 && length < 2048) return result + " [PDB=" + Marshal.PtrToStringAnsi(IntPtr.Add(symbol, 84), length) + "+0x" + displacement.ToString("X") + "]";
                }
            }
            finally { Marshal.FreeHGlobal(symbol); }
        }
        if (module.Image != null && module.IdentityMatches)
        {
            var nearest = module.Image.NearestExport(address - module.Base);
            if (nearest != null) result += " [nearest-export=" + nearest.Name + "+0x" + (address - module.Base - nearest.Rva).ToString("X") + "; not a private-symbol attribution]";
        }
        return result;
    }

    private static string FileHash(string path)
    { using (var input = File.OpenRead(path)) using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(input)).Replace("-", ""); }
    private static void ValidateAbi()
    {
        if (!Environment.Is64BitProcess || Marshal.SizeOf(typeof(StackFrame64)) != 264 || Marshal.SizeOf(typeof(KdHelp64)) != 112 || Marshal.SizeOf(typeof(Address64)) != 16)
            throw new InvalidOperationException("Expected AMD64 ADDRESS64=16, STACKFRAME64=264, KDHELP64=112.");
    }

    private sealed class ThreadRecord { internal uint Id; internal ulong Teb, StackStart, StackSize; internal byte[] Context; }
    private sealed class ExceptionInfo { internal uint ThreadId, Code; internal ulong Address; internal ulong[] Parameters; internal byte[] Context; }
    private sealed class MemoryRegion { internal ulong Start, Size; internal uint State, Protect; }
    private sealed class ModuleRecord
    {
        internal ulong Base; internal uint Size, Timestamp, Checksum; internal string Path, ImageStatus; internal PeImage Image; internal bool IdentityMatches, UnwindLoaded, PdbLoaded;
        internal void LoadImage()
        {
            try
            {
                if (String.IsNullOrEmpty(Path) || Path.StartsWith(@"\\", StringComparison.Ordinal) || !System.IO.Path.IsPathRooted(Path)) { ImageStatus = "local image path unavailable"; return; }
                Image = new PeImage(Path);
                IdentityMatches = Image.Timestamp == Timestamp && Image.Size == Size && (Checksum == 0 || Image.Checksum == Checksum);
                ImageStatus = IdentityMatches ? "matching timestamp/size/checksum" : "MISMATCH; symbols/unwind rejected";
            }
            catch (Exception error) { ImageStatus = error.GetType().Name + ": " + error.Message; }
        }
    }

    private sealed class PdbIdentity { internal Guid Guid; internal uint Age, DbiAge; }
    private static PdbIdentity ReadPdbIdentity(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 56 || bytes.Length > 128 * 1024 * 1024 || Encoding.ASCII.GetString(bytes, 0, 26) != "Microsoft C/C++ MSF 7.00\r\n") throw new InvalidDataException("Expected bounded MSF7 PDB: " + path);
        int page = checked((int)BitConverter.ToUInt32(bytes, 32)), directorySize = checked((int)BitConverter.ToUInt32(bytes, 44));
        uint map = BitConverter.ToUInt32(bytes, 52);
        if (page < 512 || page > 65536 || (page & (page - 1)) != 0 || directorySize < 12 || directorySize > 16 * 1024 * 1024) throw new InvalidDataException("Invalid MSF superblock.");
        byte[] directory = new byte[directorySize]; int pages = (directorySize + page - 1) / page;
        for (int index = 0; index < pages; index++)
        {
            long blockEntry = (long)map * page + index * 4L;
            if (blockEntry < 0 || blockEntry > bytes.Length - 4) throw new InvalidDataException("Invalid PDB directory block map.");
            long block = (long)BitConverter.ToUInt32(bytes, (int)blockEntry) * page; int amount = Math.Min(page, directorySize - index * page);
            if (block < 0 || block > bytes.Length - amount) throw new InvalidDataException("Invalid PDB directory page."); Array.Copy(bytes, block, directory, index * page, amount);
        }
        int streams = checked((int)BitConverter.ToUInt32(directory, 0)); if (streams < 2 || streams > 65536 || 4L + streams * 4L > directory.Length) throw new InvalidDataException("Invalid PDB stream directory.");
        int blockList = 4 + streams * 4;
        uint streamZeroSize = BitConverter.ToUInt32(directory, 4), streamOneSize = BitConverter.ToUInt32(directory, 8);
        if (streamZeroSize != UInt32.MaxValue) blockList = checked(blockList + (int)((streamZeroSize + (ulong)page - 1) / (uint)page) * 4);
        if (streamOneSize < 28 || streamOneSize == UInt32.MaxValue || blockList > directory.Length - 4) throw new InvalidDataException("Missing PDB identity stream.");
        long identityBlock = (long)BitConverter.ToUInt32(directory, blockList) * page;
        if (identityBlock < 0 || identityBlock > bytes.Length - 28) throw new InvalidDataException("Invalid PDB identity page.");
        byte[] guid = new byte[16]; Array.Copy(bytes, identityBlock + 12, guid, 0, 16);
        int dbiBlockList = 4 + streams * 4;
        for (int index = 0; index < 3; index++)
        {
            uint length = BitConverter.ToUInt32(directory, 4 + index * 4);
            if (length != UInt32.MaxValue) dbiBlockList = checked(dbiBlockList + (int)((length + (ulong)page - 1) / (uint)page) * 4);
        }
        if (streams < 4 || BitConverter.ToUInt32(directory, 16) < 12 || dbiBlockList > directory.Length - 4) throw new InvalidDataException("Missing DBI identity stream.");
        long dbiBlock = (long)BitConverter.ToUInt32(directory, dbiBlockList) * page;
        if (dbiBlock < 0 || dbiBlock > bytes.Length - 12 || BitConverter.ToUInt32(bytes, (int)dbiBlock) != UInt32.MaxValue) throw new InvalidDataException("Invalid DBI identity page.");
        return new PdbIdentity { Age = BitConverter.ToUInt32(bytes, (int)identityBlock + 8), Guid = new Guid(guid), DbiAge = BitConverter.ToUInt32(bytes, (int)dbiBlock + 8) };
    }

    private sealed class DumpMetadata
    {
        internal readonly Dictionary<uint, ThreadRecord> Threads = new Dictionary<uint, ThreadRecord>();
        internal readonly List<ModuleRecord> Modules = new List<ModuleRecord>();
        private readonly List<MemoryRegion> regions = new List<MemoryRegion>();
        internal ExceptionInfo Exception;
        internal DumpMetadata(string path)
        {
            using (var file = new BinaryReader(File.OpenRead(path)))
            {
                if (file.ReadUInt32() != 0x504D444D) throw new InvalidDataException("Not a minidump.");
                file.BaseStream.Position = 8; uint count = file.ReadUInt32(), directory = file.ReadUInt32();
                Require(file, directory, count * 12UL); if (count > 4096) throw new InvalidDataException("Too many dump streams.");
                var streams = new List<uint[]>();
                for (uint index = 0; index < count; index++) { file.BaseStream.Position = directory + index * 12L; streams.Add(new[] { file.ReadUInt32(), file.ReadUInt32(), file.ReadUInt32() }); }
                foreach (var stream in streams)
                {
                    uint kind = stream[0], size = stream[1], rva = stream[2]; Require(file, rva, size);
                    if (kind == 3)
                    {
                        file.BaseStream.Position = rva; uint number = file.ReadUInt32();
                        if (number > 65536 || 4UL + number * 48UL > size) throw new InvalidDataException("Invalid thread list.");
                        for (uint index = 0; index < number; index++)
                        {
                            long record = rva + 4L + index * 48L; file.BaseStream.Position = record;
                            var thread = new ThreadRecord { Id = file.ReadUInt32() };
                            file.BaseStream.Position = record + 16; thread.Teb = file.ReadUInt64(); thread.StackStart = file.ReadUInt64(); thread.StackSize = file.ReadUInt32();
                            file.BaseStream.Position = record + 40; uint contextSize = file.ReadUInt32(), contextRva = file.ReadUInt32(); thread.Context = ReadContext(file, contextRva, contextSize);
                            Threads.Add(thread.Id, thread);
                        }
                    }
                    else if (kind == 4)
                    {
                        file.BaseStream.Position = rva; uint number = file.ReadUInt32();
                        if (number > 16384 || 4UL + number * 108UL > size) throw new InvalidDataException("Invalid module list.");
                        for (uint index = 0; index < number; index++)
                        {
                            file.BaseStream.Position = rva + 4L + index * 108L;
                            var module = new ModuleRecord { Base = file.ReadUInt64(), Size = file.ReadUInt32(), Checksum = file.ReadUInt32(), Timestamp = file.ReadUInt32() };
                            uint nameRva = file.ReadUInt32(); file.BaseStream.Position = nameRva; uint bytes = file.ReadUInt32();
                            if (bytes > 65536 || (bytes & 1) != 0) throw new InvalidDataException("Invalid module name."); Require(file, (ulong)nameRva + 4, bytes);
                            module.Path = Encoding.Unicode.GetString(file.ReadBytes((int)bytes)); Modules.Add(module);
                        }
                    }
                    else if (kind == 6)
                    {
                        if (size < 168) throw new InvalidDataException("Invalid exception stream.");
                        file.BaseStream.Position = rva; var exception = new ExceptionInfo { ThreadId = file.ReadUInt32() };
                        file.BaseStream.Position = rva + 8; exception.Code = file.ReadUInt32();
                        file.BaseStream.Position = rva + 24; exception.Address = file.ReadUInt64(); uint parameters = file.ReadUInt32(); file.ReadUInt32();
                        if (parameters > 15) throw new InvalidDataException("Too many exception parameters.");
                        exception.Parameters = new ulong[parameters]; for (int index = 0; index < parameters; index++) exception.Parameters[index] = file.ReadUInt64();
                        file.BaseStream.Position = rva + 160; uint contextSize = file.ReadUInt32(), contextRva = file.ReadUInt32(); exception.Context = ReadContext(file, contextRva, contextSize); Exception = exception;
                    }
                    else if (kind == 7)
                    { file.BaseStream.Position = rva; if (file.ReadUInt16() != 9) throw new InvalidDataException("Only AMD64 system-info streams are supported."); }
                    else if (kind == 16)
                    {
                        file.BaseStream.Position = rva; uint header = file.ReadUInt32(), entry = file.ReadUInt32(); ulong number = file.ReadUInt64();
                        if (header < 16 || entry < 48 || number > 1048576 || header + number * entry > size) throw new InvalidDataException("Invalid memory-info list.");
                        for (ulong index = 0; index < number; index++)
                        {
                            long record = checked((long)((ulong)rva + header + index * entry)); file.BaseStream.Position = record;
                            var region = new MemoryRegion { Start = file.ReadUInt64() }; file.BaseStream.Position = record + 24; region.Size = file.ReadUInt64(); region.State = file.ReadUInt32(); region.Protect = file.ReadUInt32(); regions.Add(region);
                        }
                    }
                }
            }
            Modules.Sort((left, right) => left.Base.CompareTo(right.Base)); regions.Sort((left, right) => left.Start.CompareTo(right.Start));
        }
        internal ModuleRecord FindModule(ulong address) { return Modules.LastOrDefault(item => item.Base <= address && address - item.Base < item.Size); }
        internal MemoryRegion FindRegion(ulong address) { return regions.LastOrDefault(item => item.Start <= address && address - item.Start < item.Size); }
        private static byte[] ReadContext(BinaryReader file, uint rva, uint bytes)
        {
            if (bytes < 1232) return null; Require(file, rva, bytes); file.BaseStream.Position = rva; return file.ReadBytes(1232);
        }
        private static void Require(BinaryReader file, ulong offset, ulong bytes)
        { if (offset > (ulong)file.BaseStream.Length || bytes > (ulong)file.BaseStream.Length - offset) throw new InvalidDataException("Dump stream extends beyond input file."); }
    }

    private sealed class PeImage : IDisposable
    {
        private sealed class Section { internal uint Rva, Size, Raw, RawSize, Characteristics; }
        internal sealed class Export { internal uint Rva; internal string Name; }
        internal readonly uint Timestamp, Size, Checksum;
        internal string PdbName; internal Guid PdbGuid; internal uint PdbAge;
        private readonly List<Section> sections = new List<Section>(); private readonly List<Export> exports = new List<Export>();
        private readonly byte[] imageBytes, functionBytes;
        private IntPtr functionBuffer;
        internal int FunctionCount { get { return functionBytes.Length / 12; } }
        internal PeImage(string path)
        {
            var info = new FileInfo(path); if (!info.Exists || info.Length > 128 * 1024 * 1024) throw new InvalidDataException("Local image missing or exceeds bounded PE-reader limit.");
            byte[] bytes = imageBytes = File.ReadAllBytes(path); if (bytes.Length < 256 || BitConverter.ToUInt16(bytes, 0) != 0x5A4D) throw new InvalidDataException("Not a PE image.");
            int pe = BitConverter.ToInt32(bytes, 0x3C); Need(bytes, pe, 24); if (BitConverter.ToUInt32(bytes, pe) != 0x4550) throw new InvalidDataException("Invalid PE signature.");
            if (BitConverter.ToUInt16(bytes, pe + 4) != MachineAmd64) throw new InvalidDataException("Image is not AMD64.");
            int count = BitConverter.ToUInt16(bytes, pe + 6), optional = pe + 24, optionalSize = BitConverter.ToUInt16(bytes, pe + 20);
            Need(bytes, optional, optionalSize); if (optionalSize < 120 || BitConverter.ToUInt16(bytes, optional) != 0x20B) throw new InvalidDataException("Expected PE32+ optional header.");
            Timestamp = BitConverter.ToUInt32(bytes, pe + 8); Size = BitConverter.ToUInt32(bytes, optional + 56); Checksum = BitConverter.ToUInt32(bytes, optional + 64);
            if (count > 4096) throw new InvalidDataException("Too many PE sections.");
            for (int index = 0; index < count; index++)
            {
                int record = optional + optionalSize + index * 40; Need(bytes, record, 40);
                sections.Add(new Section { Rva = BitConverter.ToUInt32(bytes, record + 12), Size = Math.Max(BitConverter.ToUInt32(bytes, record + 8), BitConverter.ToUInt32(bytes, record + 16)), RawSize = BitConverter.ToUInt32(bytes, record + 16), Raw = BitConverter.ToUInt32(bytes, record + 20), Characteristics = BitConverter.ToUInt32(bytes, record + 36) });
            }
            if (optionalSize >= 168)
            {
                uint debugRva = BitConverter.ToUInt32(bytes, optional + 160), debugSize = BitConverter.ToUInt32(bytes, optional + 164);
                if (debugRva != 0 && debugSize <= 65536 && debugSize % 28 == 0)
                {
                    int debug = Offset(bytes, debugRva, (int)debugSize);
                    for (int index = 0; index < debugSize / 28; index++)
                    {
                        int record = debug + index * 28; if (BitConverter.ToUInt32(bytes, record + 12) != 2) continue;
                        int cv = checked((int)BitConverter.ToUInt32(bytes, record + 24)), length = checked((int)BitConverter.ToUInt32(bytes, record + 16)); Need(bytes, cv, length);
                        if (length < 25 || BitConverter.ToUInt32(bytes, cv) != 0x53445352) continue;
                        byte[] guid = new byte[16]; Array.Copy(bytes, cv + 4, guid, 0, 16); PdbGuid = new Guid(guid); PdbAge = BitConverter.ToUInt32(bytes, cv + 20);
                        int end = cv + 24; while (end < cv + length && bytes[end] != 0) end++;
                        if (end == cv + length) throw new InvalidDataException("Invalid CodeView PDB name."); PdbName = Path.GetFileName(Encoding.ASCII.GetString(bytes, cv + 24, end - cv - 24));
                    }
                }
            }
            uint exceptionRva = optionalSize >= 144 ? BitConverter.ToUInt32(bytes, optional + 136) : 0, exceptionSize = optionalSize >= 144 ? BitConverter.ToUInt32(bytes, optional + 140) : 0;
            if (exceptionSize > 16 * 1024 * 1024 || exceptionSize % 12 != 0) throw new InvalidDataException("Invalid AMD64 exception table size.");
            functionBytes = new byte[exceptionSize];
            if (exceptionSize > 0)
            {
                int tableOffset = Offset(bytes, exceptionRva, (int)exceptionSize); Array.Copy(bytes, tableOffset, functionBytes, 0, (int)exceptionSize);
                uint previous = 0;
                for (int index = 0; index < FunctionCount; index++)
                {
                    uint begin = BitConverter.ToUInt32(functionBytes, index * 12), end = BitConverter.ToUInt32(functionBytes, index * 12 + 4), unwind = BitConverter.ToUInt32(functionBytes, index * 12 + 8);
                    if (begin < previous || begin >= end || end > Size || unwind >= Size) throw new InvalidDataException("Invalid/unsorted AMD64 runtime function entry."); previous = begin;
                }
            }
            uint exportRva = BitConverter.ToUInt32(bytes, optional + 112), exportSize = BitConverter.ToUInt32(bytes, optional + 116);
            if (exportRva == 0) return;
            int directory = Offset(bytes, exportRva, 40); uint functions = BitConverter.ToUInt32(bytes, directory + 20), names = BitConverter.ToUInt32(bytes, directory + 24);
            uint functionTable = BitConverter.ToUInt32(bytes, directory + 28), nameTable = BitConverter.ToUInt32(bytes, directory + 32), ordinalTable = BitConverter.ToUInt32(bytes, directory + 36);
            if (names > 1048576 || functions > 1048576) throw new InvalidDataException("Invalid PE export counts.");
            for (uint index = 0; index < names; index++)
            {
                uint textRva = BitConverter.ToUInt32(bytes, Offset(bytes, checked(nameTable + index * 4), 4));
                ushort ordinal = BitConverter.ToUInt16(bytes, Offset(bytes, checked(ordinalTable + index * 2), 2)); if (ordinal >= functions) continue;
                uint functionRva = BitConverter.ToUInt32(bytes, Offset(bytes, checked(functionTable + ordinal * 4U), 4));
                if (functionRva >= exportRva && functionRva - exportRva < exportSize) continue; // Forwarder string, not native code.
                int text = Offset(bytes, textRva, 1), end = text; while (end < bytes.Length && end - text < 4096 && bytes[end] != 0) end++;
                if (end == bytes.Length || end - text >= 4096) continue;
                exports.Add(new Export { Rva = functionRva, Name = Encoding.ASCII.GetString(bytes, text, end - text) });
            }
            exports.Sort((left, right) => left.Rva.CompareTo(right.Rva));
        }
        internal bool IsExecutable(ulong rva) { return sections.Any(section => rva >= section.Rva && rva - section.Rva < section.Size && (section.Characteristics & 0x20000000) != 0); }
        internal Export NearestExport(ulong rva) { return exports.LastOrDefault(item => item.Rva <= rva); }
        internal IntPtr RuntimeFunction(ulong rva)
        {
            int low = 0, high = FunctionCount - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2, offset = middle * 12;
                uint begin = BitConverter.ToUInt32(functionBytes, offset), end = BitConverter.ToUInt32(functionBytes, offset + 4);
                if (rva < begin) high = middle - 1;
                else if (rva >= end) low = middle + 1;
                else
                {
                    if (functionBuffer == IntPtr.Zero) { functionBuffer = Marshal.AllocHGlobal(functionBytes.Length); Marshal.Copy(functionBytes, 0, functionBuffer, functionBytes.Length); }
                    return IntPtr.Add(functionBuffer, offset);
                }
            }
            return IntPtr.Zero; // Valid PE leaf functions have no runtime-function entry.
        }
        internal bool ReadVirtual(ulong rva, int length, out byte[] bytes)
        {
            bytes = null; if (rva > UInt32.MaxValue || length < 0 || rva >= Size || (ulong)length > Size - rva) return false;
            foreach (var section in sections)
                if ((section.Characteristics & 0x80000000) == 0 && rva >= section.Rva && rva - section.Rva < section.RawSize && (ulong)length <= section.RawSize - (rva - section.Rva))
                { int offset = checked((int)(rva - section.Rva + section.Raw)); Need(imageBytes, offset, length); bytes = new byte[length]; Array.Copy(imageBytes, offset, bytes, 0, length); return true; }
            return false;
        }
        public void Dispose() { if (functionBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(functionBuffer); functionBuffer = IntPtr.Zero; } }
        private int Offset(byte[] bytes, uint rva, int length)
        {
            foreach (var section in sections) if (rva >= section.Rva && rva - section.Rva < section.RawSize) { int offset = checked((int)(rva - section.Rva + section.Raw)); Need(bytes, offset, length); return offset; }
            int header = checked((int)rva); Need(bytes, header, length); return header;
        }
        private static void Need(byte[] bytes, int offset, int size) { if (offset < 0 || size < 0 || offset > bytes.Length - size) throw new InvalidDataException("PE record extends beyond local image."); }
    }

    private sealed class ContextBuffer : IDisposable
    {
        private IntPtr allocation; internal IntPtr Pointer;
        internal ContextBuffer(byte[] bytes) { allocation = Marshal.AllocHGlobal(1248); Pointer = new IntPtr((allocation.ToInt64() + 15) & ~15L); Marshal.Copy(bytes, 0, Pointer, 1232); }
        public void Dispose() { if (allocation != IntPtr.Zero) { Marshal.FreeHGlobal(allocation); allocation = IntPtr.Zero; } }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Address64 { internal ulong Offset; internal ushort Segment; internal uint Mode; }
    [StructLayout(LayoutKind.Sequential)] private struct KdHelp64 { internal ulong Thread; internal uint CallbackStack, CallbackBStore, NextCallback, FramePointer; internal ulong KiCallUserMode, KeUserCallbackDispatcher, SystemRangeStart, KiUserExceptionDispatcher, StackBase, StackLimit; internal uint BuildVersion, RetpolineTableSize; internal ulong RetpolineTable; internal uint RetpolineOffset, RetpolineSize; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] internal ulong[] Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct StackFrame64 { internal Address64 PC, Return, Frame, Stack, BackingStore; internal IntPtr FunctionTable; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] internal ulong[] Parameters; internal int Far, Virtual; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] internal ulong[] Reserved; internal KdHelp64 Helper; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool ReadMemoryCallback(IntPtr process, ulong address, IntPtr output, uint size, out uint read);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr FunctionTableCallback(IntPtr process, ulong address);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate ulong ModuleBaseCallback(IntPtr process, ulong address);
    [DllImport("dbghelp.dll", CharSet = CharSet.Ansi, SetLastError = true)] private static extern bool SymInitialize(IntPtr process, string searchPath, bool invade);
    [DllImport("dbghelp.dll", SetLastError = true)] private static extern bool SymCleanup(IntPtr process);
    [DllImport("dbghelp.dll")] private static extern uint SymSetOptions(uint options);
    [DllImport("dbghelp.dll", CharSet = CharSet.Ansi, SetLastError = true)] private static extern ulong SymLoadModuleEx(IntPtr process, IntPtr file, string image, string module, ulong baseAddress, uint size, IntPtr data, uint flags);
    [DllImport("dbghelp.dll", CharSet = CharSet.Ansi, SetLastError = true)] private static extern bool SymFromAddr(IntPtr process, ulong address, out ulong displacement, IntPtr symbol);
    [DllImport("dbghelp.dll", SetLastError = true)] private static extern bool StackWalk64(uint machine, IntPtr process, IntPtr thread, ref StackFrame64 frame, IntPtr context, ReadMemoryCallback readMemory, FunctionTableCallback functionTable, ModuleBaseCallback moduleBase, IntPtr translate);
}
