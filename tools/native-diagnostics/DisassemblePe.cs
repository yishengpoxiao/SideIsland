using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Iced.Intel;

// Local PE decoder only: no debugger/live-process access or binary changes.
internal static class DisassemblePe
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 4) throw new ArgumentException("Usage: DisassemblePe.exe <local-pe> <start-rva-hex> <byte-count> <output-text>");
            string image = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[3]);
            if (String.Equals(image, output, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must differ from input image.");
            uint rva = UInt32.Parse(args[1].Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int count = Int32.Parse(args[2], CultureInfo.InvariantCulture); if (count < 1 || count > 1048576) throw new ArgumentException("Count outside 1..1048576.");
            byte[] pe = File.ReadAllBytes(image); int nt = BitConverter.ToInt32(pe, 0x3C), optional = nt + 24;
            if (BitConverter.ToUInt16(pe, nt + 4) != 0x8664 || BitConverter.ToUInt16(pe, optional) != 0x20B) throw new InvalidDataException("Only AMD64 PE32+ images.");
            ulong imageBase = BitConverter.ToUInt64(pe, optional + 24); int sections = BitConverter.ToUInt16(pe, nt + 6), sectionStart = optional + BitConverter.ToUInt16(pe, nt + 20);
            int raw = -1;
            for (int index = 0; index < sections; index++)
            {
                int section = sectionStart + index * 40; uint address = BitConverter.ToUInt32(pe, section + 12), rawSize = BitConverter.ToUInt32(pe, section + 16);
                if (rva >= address && rva - address < rawSize && count <= rawSize - (rva - address)) { raw = checked((int)(BitConverter.ToUInt32(pe, section + 20) + rva - address)); break; }
            }
            if (raw < 0 || raw + count > pe.Length) throw new InvalidDataException("Requested bytes not present in one local PE section.");
            byte[] code = new byte[count]; Array.Copy(pe, raw, code, 0, count);
            var decoder = Iced.Intel.Decoder.Create(64, code); decoder.IP = imageBase + rva;
            using (var log = new StreamWriter(output, false, new UTF8Encoding(false)))
            using (var sha = SHA256.Create())
            {
                log.WriteLine("Offline local PE disassembly; no original file mutation; decoder=Iced 1.21.0.");
                log.WriteLine("Image=" + image + "; SHA256=" + BitConverter.ToString(sha.ComputeHash(pe)).Replace("-", "") + "; preferredBase=0x" + imageBase.ToString("X") + "; startRva=0x" + rva.ToString("X") + "; bytes=" + count);
                while (decoder.IP < imageBase + rva + (ulong)count)
                {
                    Instruction instruction = decoder.Decode(); int offset = checked((int)(instruction.IP - imageBase - rva));
                    if (instruction.Length <= 0 || offset + instruction.Length > code.Length) break;
                    log.WriteLine("RVA " + (instruction.IP - imageBase).ToString("X8") + "  " + BitConverter.ToString(code, offset, instruction.Length).Replace('-', ' ').PadRight(44) + "  " + instruction.ToString());
                }
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
