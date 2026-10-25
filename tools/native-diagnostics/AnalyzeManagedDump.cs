using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Diagnostics.Runtime;

// Offline dump reader. No live-process attachment, symbol upload or account IO.
internal static class AnalyzeManagedDump
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Usage: AnalyzeManagedDump.exe <dump> <output-text>");
            string dump = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
            string dac = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "mscordacwks.dll");
            using (var log = new StreamWriter(output, false, new UTF8Encoding(false)))
            using (var rawTarget = DataTarget.LoadCrashDump(dump, CrashDumpReader.ClrMD))
            using (var target = DataTarget.CreateFromDataReader(new Win64ContextReader(rawTarget.DataReader, dump)))
            {
                log.AutoFlush = true;
                log.WriteLine("Dump=" + dump + "; DAC=" + dac + "; analyzer64=" + Environment.Is64BitProcess);
                log.WriteLine("Compatibility reader: bounded copy of standard 1232-byte AMD64 CONTEXT; original dump and XSTATE bytes unchanged.");
                foreach (var info in target.ClrVersions)
                {
                    log.WriteLine("CLR=" + info.Version);
                    ClrRuntime runtime = info.CreateRuntime(dac, false);
                    foreach (var thread in runtime.Threads.OrderBy(item => item.OSThreadId))
                    {
                        log.WriteLine("THREAD OS=" + thread.OSThreadId + " managed=" + thread.ManagedThreadId + " alive=" + thread.IsAlive + " background=" + thread.IsBackground + " finalizer=" + thread.IsFinalizer);
                        try
                        {
                            byte[] context = new byte[1232];
                            IntPtr contextPointer = Marshal.AllocHGlobal(context.Length);
                            bool readContext;
                            try { readContext = target.DataReader.GetThreadContext(thread.OSThreadId, 0x0010000B, (uint)context.Length, contextPointer); if (readContext) Marshal.Copy(contextPointer, context, 0, context.Length); }
                            finally { Marshal.FreeHGlobal(contextPointer); }
                            log.WriteLine("  DATAREADER_CONTEXT=" + readContext + "; RIP=" + BitConverter.ToUInt64(context, 0xF8).ToString("X16") + "; RSP=" + BitConverter.ToUInt64(context, 0x98).ToString("X16"));
                            foreach (var frame in thread.EnumerateStackTrace().Take(200))
                                log.WriteLine("  SP=" + frame.StackPointer.ToString("X16", CultureInfo.InvariantCulture) + " IP=" + frame.InstructionPointer.ToString("X16", CultureInfo.InvariantCulture) + " " + frame.DisplayString);
                            if (readContext)
                            {
                                ulong stackPointer = BitConverter.ToUInt64(context, 0x98); byte[] raw = new byte[2048]; int read;
                                if (target.DataReader.ReadMemory(stackPointer, raw, raw.Length, out read))
                                {
                                    log.WriteLine("  RAW_STACK_METHOD_CANDIDATES (not an unwound call chain):");
                                    for (int offset = 0; offset + 8 <= read; offset += 8)
                                    {
                                        ulong candidate = BitConverter.ToUInt64(raw, offset);
                                        if (candidate < 0x10000) continue;
                                        try { var method = runtime.GetMethodByAddress(candidate); if (method != null) log.WriteLine("    [RSP+0x" + offset.ToString("X") + "] " + candidate.ToString("X16") + " " + method.GetFullSignature()); } catch { }
                                    }
                                }
                            }
                            var exception = thread.CurrentException;
                            if (exception != null)
                            {
                                log.WriteLine("  EXCEPTION " + (exception.Type == null ? "<unknown>" : exception.Type.Name) + " HResult=0x" + exception.HResult.ToString("X8") + " " + exception.Message);
                                foreach (var frame in exception.StackTrace.Take(100)) log.WriteLine("    " + frame.DisplayString);
                            }
                        }
                        catch (Exception error) { log.WriteLine("  Partial thread read: " + error); }
                    }
                }
            }
            Console.WriteLine(output); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    // Windows 11 minidumps can contain a CONTEXT plus an XSTATE tail (for
    // example 1663 bytes). ClrMD 1.1's reader rejects anything other than 1232
    // bytes. Adapt only context requests; all other dump reads are delegated.
    // The original dump is never patched, rewritten or truncated.
    private sealed class Win64ContextReader : IDataReader
    {
        private readonly IDataReader inner;
        private readonly Dictionary<uint, byte[]> contexts = new Dictionary<uint, byte[]>();
        internal Win64ContextReader(IDataReader reader, string dump)
        {
            inner = reader;
            if (inner.GetPointerSize() != 8) throw new InvalidOperationException("This compatibility reader supports Win64 dumps only.");
            using (var file = new BinaryReader(File.OpenRead(dump)))
            {
                file.BaseStream.Position = 8; uint count = file.ReadUInt32(), directory = file.ReadUInt32();
                if (count > 4096 || (ulong)directory + count * 12UL > (ulong)file.BaseStream.Length) throw new InvalidDataException("Invalid dump stream directory.");
                for (uint index = 0; index < count; index++)
                {
                    file.BaseStream.Position = directory + index * 12L; uint kind = file.ReadUInt32(), size = file.ReadUInt32(), rva = file.ReadUInt32();
                    if (kind != 3) continue;
                    file.BaseStream.Position = rva; uint threads = file.ReadUInt32();
                    if (threads > 65536 || 4UL + threads * 48UL > size) throw new InvalidDataException("Invalid dump thread directory.");
                    for (uint thread = 0; thread < threads; thread++)
                    {
                        long record = rva + 4L + thread * 48L; file.BaseStream.Position = record; uint id = file.ReadUInt32();
                        file.BaseStream.Position = record + 40; uint bytes = file.ReadUInt32(), contextRva = file.ReadUInt32();
                        if (bytes < 1232 || (ulong)contextRva + bytes > (ulong)file.BaseStream.Length) throw new InvalidDataException("Invalid Win64 context for thread " + id);
                        file.BaseStream.Position = contextRva; byte[] context = file.ReadBytes(1232);
                        uint flags = BitConverter.ToUInt32(context, 0x30) & ~0x40U; // XSTATE tail not returned to the legacy DAC client.
                        Array.Copy(BitConverter.GetBytes(flags), 0, context, 0x30, 4); contexts[id] = context;
                    }
                }
            }
        }
        public bool GetThreadContext(uint thread, uint flags, uint size, IntPtr output)
        {
            byte[] context; if (!contexts.TryGetValue(thread, out context) || size < context.Length) return false;
            Marshal.Copy(context, 0, output, context.Length); return true;
        }
        public bool GetThreadContext(uint thread, uint flags, uint size, byte[] output)
        {
            byte[] context; if (!contexts.TryGetValue(thread, out context) || size < context.Length || output.Length < context.Length) return false;
            Array.Copy(context, output, context.Length); return true;
        }
        public void Close() { } // rawTarget owns the underlying reader and closes it after this wrapper.
        public void Flush() { inner.Flush(); }
        public Microsoft.Diagnostics.Runtime.Architecture GetArchitecture() { return inner.GetArchitecture(); }
        public uint GetPointerSize() { return inner.GetPointerSize(); }
        public IList<ModuleInfo> EnumerateModules() { return inner.EnumerateModules(); }
        public void GetVersionInfo(ulong address, out Microsoft.Diagnostics.Runtime.VersionInfo version) { inner.GetVersionInfo(address, out version); }
        public bool ReadMemory(ulong address, byte[] buffer, int bytes, out int read) { return inner.ReadMemory(address, buffer, bytes, out read); }
        public bool ReadMemory(ulong address, IntPtr buffer, int bytes, out int read) { return inner.ReadMemory(address, buffer, bytes, out read); }
        public bool IsMinidump { get { return inner.IsMinidump; } }
        public ulong GetThreadTeb(uint thread) { return inner.GetThreadTeb(thread); }
        public IEnumerable<uint> EnumerateAllThreads() { return inner.EnumerateAllThreads(); }
        public bool VirtualQuery(ulong address, out VirtualQueryData data) { return inner.VirtualQuery(address, out data); }
        public ulong ReadPointerUnsafe(ulong address) { return inner.ReadPointerUnsafe(address); }
        public uint ReadDwordUnsafe(ulong address) { return inner.ReadDwordUnsafe(address); }
    }
}
