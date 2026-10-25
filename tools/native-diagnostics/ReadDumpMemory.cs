using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Diagnostics.Runtime;

// Offline own-dump inspection. Reads no live process and mutates no dump.
internal static class ReadDumpMemory
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 4 || args.Length % 2 != 0) throw new ArgumentException("Usage: ReadDumpMemory.exe <dump> <output> <address-hex> <length-decimal> [<address> <length> ...]");
            using (var target = DataTarget.LoadCrashDump(Path.GetFullPath(args[0]), CrashDumpReader.ClrMD))
            using (var log = new StreamWriter(Path.GetFullPath(args[1]), false, new UTF8Encoding(false)))
            {
                log.WriteLine("Offline raw-memory view; original dump unchanged. Dump=" + Path.GetFullPath(args[0]));
                foreach (var module in target.DataReader.EnumerateModules()) log.WriteLine("MODULE " + module.ImageBase.ToString("X16") + " size=" + module.FileSize.ToString("X") + " " + module.FileName);
                for (int arg = 2; arg < args.Length; arg += 2)
                {
                    ulong address = UInt64.Parse(args[arg].Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    int count = Int32.Parse(args[arg + 1], CultureInfo.InvariantCulture); if (count < 1 || count > 1048576) throw new ArgumentException("Read length must be 1..1048576 bytes.");
                    byte[] bytes = new byte[count]; int read;
                    bool success = target.DataReader.ReadMemory(address, bytes, bytes.Length, out read);
                    log.WriteLine("MEMORY address=" + address.ToString("X16") + " requested=" + count + " read=" + read + " success=" + success);
                    for (int offset = 0; offset < read; offset += 16)
                    {
                        int length = Math.Min(16, read - offset);
                        log.Write((address + (ulong)offset).ToString("X16") + "  " + BitConverter.ToString(bytes, offset, length).Replace('-', ' ').PadRight(47) + "  ");
                        for (int index = 0; index < length; index++) log.Write(bytes[offset + index] >= 32 && bytes[offset + index] <= 126 ? (char)bytes[offset + index] : '.');
                        log.WriteLine();
                    }
                    for (int offset = 0; offset + 8 <= read; offset += 8)
                    {
                        ulong pointer = BitConverter.ToUInt64(bytes, offset);
                        var module = target.DataReader.EnumerateModules().FirstOrDefault(item => pointer >= item.ImageBase && pointer - item.ImageBase < item.FileSize);
                        if (module != null) log.WriteLine("PTR [" + (address + (ulong)offset).ToString("X16") + "]=" + pointer.ToString("X16") + " " + Path.GetFileName(module.FileName) + "+0x" + (pointer - module.ImageBase).ToString("X"));
                    }
                }
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
