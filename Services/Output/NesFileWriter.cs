using System;
using System.Text;

namespace VT03Builder.Services.Output
{
    /// <summary>
    /// Generates NES 2.0 (.nes) and UNIF (.unf) wrapper files from a flash binary.
    ///
    /// These files are for emulator testing only. Real hardware uses the raw .bin.
    /// </summary>
    public static class NesFileWriter
    {
        // ── NES 2.0 ───────────────────────────────────────────────────────────

        /// <summary>
        /// Wrap the flash binary in a NES 2.0 header for mapper 256 / submapper N.
        /// The full flash contents are included — emulators that only support 2 MB
        /// will read the first 2 MB window; the header encodes the actual chip size.
        /// NROM games work in FCEUX; MMC3 games need NintendulatorNRS (.unf).
        /// </summary>
        public static byte[] MakeNes2Rom(byte[] prg, int submapper = 0)
        {
            byte[] hdr    = MakeNes2Header(prg.Length / 16384, submapper);
            byte[] result = new byte[16 + prg.Length];
            Array.Copy(hdr,  result, 16);
            Array.Copy(prg, 0, result, 16, prg.Length);
            return result;
        }

        /// <summary>Produces a 16-byte NES 2.0 header for mapper 256.</summary>
        public static byte[] MakeNes2Header(int prg16kBanks, int submapper = 0)
        {
            // NES 2.0 mapper 256, submapper N:
            //   byte[6] bits 7-4 = mapper bits 3-0  = 256 & 0x0F         = 0x00
            //   byte[7] bits 7-4 = mapper bits 7-4  = (256>>4) & 0x0F    = 0x00
            //           bits 3-2 = 0x08 (NES 2.0 identifier)
            //   byte[8] bits 3-0 = mapper bits 11-8 = (256>>8) & 0x0F    = 0x01
            //           bits 7-4 = submapper
            const int Mapper = 256;
            byte[] hdr = new byte[16];
            hdr[0] = (byte)'N'; hdr[1] = (byte)'E';
            hdr[2] = (byte)'S'; hdr[3] = 0x1A;
            hdr[4] = (byte)(prg16kBanks & 0xFF);
            hdr[6] = (byte)((Mapper & 0x0F) << 4);
            hdr[7] = (byte)(((Mapper >> 4) & 0x0F) << 4 | 0x08);
            hdr[8] = (byte)(((submapper & 0x0F) << 4) | ((Mapper >> 8) & 0x0F));
            hdr[9] = (byte)((prg16kBanks >> 8) & 0x0F);
            return hdr;
        }

        // ── UNIF ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Wrap the full flash binary in a UNIF file (board = UNL-OneBus).
        /// Splits into 2 MB PRG chunks (PRG0, PRG1, …).
        /// Use with NintendulatorNRS for MMC3 game testing.
        /// </summary>
        public static byte[] MakeUnifRom(byte[] prg, string boardName = "UNL-OneBus")
        {
            const int Window = 0x200000;
            var chunks = new System.Collections.Generic.List<(byte[] id, byte[] data)>();
            chunks.Add((Ascii("MAPR"), Ascii(boardName + "\0")));
            chunks.Add((Ascii("MIRR"), new byte[] { 0x05 }));

            int numWindows = (prg.Length + Window - 1) / Window;
            for (int w = 0; w < numWindows; w++)
            {
                int start = w * Window;
                int len   = Math.Min(Window, prg.Length - start);
                byte[] chunk = new byte[Window];
                Array.Fill(chunk, (byte)0xFF);
                Array.Copy(prg, start, chunk, 0, len);
                chunks.Add((Ascii($"PRG{w}"), chunk));
            }

            int totalLen = 32;
            foreach (var (id, data) in chunks)
                totalLen += 4 + 4 + data.Length;

            byte[] unif = new byte[totalLen];
            Array.Copy(Ascii("UNIF"), 0, unif, 0, 4);
            unif[4] = 4;

            int pos = 32;
            foreach (var (id, data) in chunks)
            {
                Array.Copy(id, 0, unif, pos, id.Length); pos += 4;
                unif[pos+0] = (byte)( data.Length        & 0xFF);
                unif[pos+1] = (byte)((data.Length >>  8) & 0xFF);
                unif[pos+2] = (byte)((data.Length >> 16) & 0xFF);
                unif[pos+3] = (byte)((data.Length >> 24) & 0xFF);
                pos += 4;
                Array.Copy(data, 0, unif, pos, data.Length);
                pos += data.Length;
            }
            return unif;
        }

        // ── Header description (for UI "Generate Header" button) ─────────────

        /// <summary>
        /// Multi-line human-readable description of the NES 2.0 header
        /// for mapper 256 with the given submapper and chip size.
        /// </summary>
        public static string DescribeHeader(int submapper, int chipMb)
        {
            int prg16k = (chipMb * 1024 * 1024) / (16 * 1024);
            byte[] hdr = MakeNes2Header(prg16k, submapper);

            int mapperNum = ((hdr[6] >> 4) & 0xF)
                          | (hdr[7] & 0xF0)
                          | ((hdr[8] & 0x0F) << 8);
            int smNum    = (hdr[8] >> 4) & 0x0F;
            int prg12bit = ((hdr[9] & 0x0F) << 8) | hdr[4];

            var sb = new StringBuilder();
            sb.AppendLine("── NES 2.0 Header (Mapper 256 / OneBus / VT03) ─────────────────────");
            sb.AppendLine();
            sb.Append("  Hex:  ");
            for (int i = 0; i < 16; i++)
            {
                sb.Append($"{hdr[i]:X2}");
                if (i == 7) sb.Append("  ");
                else if (i < 15) sb.Append(' ');
            }
            sb.AppendLine(); sb.AppendLine();
            sb.AppendLine($"  [0-3]  Magic       : NES 1A");
            sb.AppendLine($"  [4]    PRG ROM lo  : {hdr[4]:X2}h  ┐ 12-bit PRG size:");
            sb.AppendLine($"  [9]    PRG ROM hi  : {hdr[9]:X2}h  ┘ = 0x{prg12bit:X3} = {prg12bit} × 16KB = {chipMb} MB");
            sb.AppendLine($"  [5]    CHR ROM     : 00h  (CHR banked from PRG flash via $2018/$201A)");
            sb.AppendLine($"  [6]    Flags 6     : {hdr[6]:X2}h  mapper bits 3-0 = {(hdr[6]>>4)&0xF:X1}");
            sb.AppendLine($"  [7]    Flags 7     : {hdr[7]:X2}h  mapper bits 7-4 = {(hdr[7]>>4)&0xF:X1},  NES 2.0 id = 2");
            sb.AppendLine($"  [8]    Mapper/Sub  : {hdr[8]:X2}h  mapper bits 11-8 = {hdr[8]&0xF:X1},  submapper = {smNum}");
            sb.AppendLine($"  [10-15] Unused     : 00 00 00 00 00 00");
            sb.AppendLine();
            sb.AppendLine($"  Mapper: {mapperNum}  Submapper: {smNum}");
            sb.AppendLine($"  PRG:    {prg12bit} × 16KB = {chipMb} MB");
            sb.Append(    $"  Flash:  {chipMb * 1024} KB output image");

            if (submapper >= 11 && submapper <= 15)
            {
                string swapDesc = submapper switch {
                    11 => "D7↔D6, D2↔D1 (via $411C.6) + D5↔D4 (via $411C.1)",
                    12 => "D7↔D6, D2↔D1 (via $411C.6)",
                    13 => "D4↔D1 (via $4169)",
                    14 => "D7↔D6 (via $411C)",
                    15 => "D6↔D5 (via $4169)",
                    _  => ""
                };
                sb.AppendLine(); sb.AppendLine();
                sb.AppendLine($"  ⚠ Submapper {submapper}: CPU opcode bit-swap active at power-on");
                sb.AppendLine($"     Swap: {swapDesc}");
                sb.AppendLine($"     Hardware unscrambles opcode fetches only — not data reads.");
                sb.Append(   $"     Use ROMs compiled specifically for this console type.");
            }
            return sb.ToString();
        }

        private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
    }
}
