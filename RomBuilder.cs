using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using VT03Builder.Models;

namespace VT03Builder.Services
{
    /// <summary>
    /// VT03 OneBus Multicart Builder — C# port of Wassermann1/sup400 rom_tool.
    ///
    /// ROM Layout (NES 2.0, Mapper 256):
    ///   [0x000000-0x07FFFF]  original_menu_patched.rom (512KB kernel)
    ///     [0x079000]         Game list: count(2B LE) + 14 constants + null-terminated names
    ///     [0x07C000]         Config table: 9 bytes × game count
    ///   [0x080000-0x1FFFFF]  NROM/CNROM game data
    ///   [0x200000+]          MMC3 game data
    ///   (rest = 0xFF padding)
    ///
    /// 9-byte config per game:
    ///   [0] $4100 = (pa24_21 << 4) | (va24_21 & 0x0F)
    ///   [1] $2018 (CHR bank intermediate)
    ///   [2] $201A (CHR bank flags)
    ///   [3] mode  (0x04/0x05=NROM, 0x02=MMC3)
    ///   [4] PQ0   (CPU $8000-$9FFF bank)
    ///   [5] PQ1   (CPU $A000-$BFFF bank)
    ///   [6] PQ2   (CPU $C000-$DFFF bank)
    ///   [7] PQ3   (CPU $E000-$FFFF bank)
    ///   [8] flags (0x00=V-mirror, 0x01=H-mirror, 0x80=large CHR)
    /// </summary>
    public static class RomBuilder
    {
        private const int MenuStart       = 0x079000;
        private const int MenuEnd         = 0x079FFF;
        private const int MenuHeaderEnd   = 0x079010;
        private const int ConfigTableAddr = 0x07C000;
        private const int NromStart       = 0x080000;
        private const int Mmc3Start       = 0x200000;
        private const int WindowSize      = 0x200000;

        private static readonly byte[] MenuConstants =
        {
            0x14, 0x00, 0x08, 0x08, 0x08, 0x04, 0x04, 0x08,
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00,
        };

        // ── Public entry point ────────────────────────────────────────────────
        public static BuildResult Build(BuildConfig cfg, IProgress<string>? log = null)
            => Build(cfg, LoadKernel(), log);

        /// <summary>Testable overload — caller supplies kernel bytes directly.</summary>
        internal static BuildResult Build(BuildConfig cfg, byte[] kernelData, IProgress<string>? log = null)
        {
            log?.Report($"Kernel: {kernelData.Length} bytes");

            int    romSize = cfg.ChipSizeMb * 1024 * 1024;
            byte[] rom     = new byte[romSize];
            Array.Fill(rom, (byte)0xFF);
            Array.Copy(kernelData, rom, kernelData.Length);

            var nromGames = cfg.Games.Where(g => g.Mapper == 0 || g.Mapper == 3).ToList();
            var mmc3Games = cfg.Games.Where(g => g.Mapper == 4).ToList();

            var names   = new List<string>();
            var configs = new List<byte[]>();

            // For chips > 2 MB, MMC3 games start in the second 2 MB window (0x200000).
            // For a 2 MB chip that window doesn't exist, so MMC3 games are packed
            // immediately after the NROM games in the same (only) window.
            bool sharedWindow = romSize <= WindowSize;
            int  nromLimit    = sharedWindow ? romSize : Mmc3Start;

            int curNrom = NromStart;

            // ── NROM / CNROM games ────────────────────────────────────────────
            foreach (var game in nromGames)
            {
                int gameSize = game.PrgSize + game.ChrSize;
                if (curNrom + gameSize >= nromLimit)
                {
                    log?.Report($"SKIP  {game.FileName}: no NROM space left");
                    continue;
                }
                Array.Copy(game.RawData, 0, rom, curNrom, gameSize);
                byte[] c = BuildNromConfig(game, curNrom);
                names.Add(CleanName(game.FileName));
                configs.Add(c);
                log?.Report($"NROM  {game.FileName,-32} @ 0x{curNrom:X6}  cfg={Hex(c)}");
                int align = 0x4000;
                curNrom += ((gameSize + align - 1) / align) * align;
            }

            // MMC3 floor: second window start for large chips, right after NROM for 2 MB.
            int mmc3Floor = sharedWindow ? curNrom : Mmc3Start;
            int curMmc3   = mmc3Floor;

            if (sharedWindow && mmc3Games.Count > 0)
                log?.Report("NOTE  2 MB chip — MMC3 games packed after NROM in the same window");

            // ── MMC3 games ────────────────────────────────────────────────────
            foreach (var game in mmc3Games)
            {
                var (_, _, prgAlign) = PsForPrg(game.PrgSize);
                int effPrg   = RomBuilder.EffectivePrgForChr(game.PrgSize, game.ChrSize);
                int physSize = effPrg + game.ChrSize;

                // Align to PRG inner-window boundary (128/256/512 KB depending on PRG size).
                if (curMmc3 % prgAlign != 0)
                    curMmc3 = ((curMmc3 + prgAlign - 1) / prgAlign) * prgAlign;

                // Never straddle a 2 MB window boundary
                int wb = (curMmc3 / WindowSize) * WindowSize;
                if (curMmc3 + physSize > wb + WindowSize)
                    curMmc3 = wb + WindowSize;

                if (curMmc3 + physSize > romSize)
                {
                    log?.Report($"SKIP  {game.FileName}: no MMC3 space left");
                    continue;
                }
                // Write real PRG; padding bytes (if any) stay 0xFF (rom pre-filled).
                Array.Copy(game.RawData, 0, rom, curMmc3, game.PrgSize);
                // Write CHR after effective PRG (past any padding).
                Array.Copy(game.RawData, game.PrgSize, rom, curMmc3 + effPrg, game.ChrSize);
                byte[] c = BuildMmc3Config(game, curMmc3);
                ApplySubmapperToMmc3Config(c, cfg.Submapper);
                names.Add(CleanName(game.FileName));
                configs.Add(c);
                int padKB = (effPrg - game.PrgSize) / 1024;
                string padNote = padKB > 0 ? $" [+{padKB}KB CHR-align pad]" : "";
                log?.Report($"MMC3  {game.FileName,-32} @ 0x{curMmc3:X6}  cfg={Hex(c)}{padNote}");
                curMmc3 += physSize;
            }

            WriteGameList(rom, names);
            for (int i = 0; i < configs.Count; i++)
                Array.Copy(configs[i], 0, rom, ConfigTableAddr + i * 9, 9);

            return new BuildResult
            {
                NorBinary = rom,
                NesFile   = MakeNes2Rom(rom, cfg.Submapper),
                UnifFile  = MakeUnifRom(rom),
                GameCount = names.Count,
                NromUsed  = curNrom - NromStart,
                Mmc3Used  = curMmc3 - mmc3Floor,
            };
        }

        // ── How much space is available for games in a given chip ────────────
        public static long UsableBytes(int chipSizeMb)
        {
            // NROM area: 0x080000 – 0x1FFFFF  (1.5 MB)
            // MMC3 area: 0x200000 – end
            long total = (long)chipSizeMb * 1024L * 1024L;
            return total - NromStart;   // everything after the 512 KB kernel
        }

        // ── NROM / CNROM ──────────────────────────────────────────────────────
        private static byte[] BuildNromConfig(NesRom game, int romOffset)
        {
            CalcNromVideo(romOffset + game.PrgSize, game.Mapper,
                          out byte v4100, out byte r2018, out byte r201A);
            byte pa    = (byte)((romOffset >> 21) & 0x0F);
            byte r4100 = (byte)((pa << 4) | (v4100 & 0x0F));
            byte b4    = Bnk(romOffset);
            byte b5    = Bnk(romOffset + 0x2000);
            byte b6    = Bnk(romOffset + game.PrgSize - 0x4000);
            byte b7    = Bnk(romOffset + game.PrgSize - 0x2000);
            byte mir   = game.Vertical ? (byte)0x00 : (byte)0x01;
            byte mode  = game.PrgSize >= 32768 ? (byte)0x04 : (byte)0x05;
            return new byte[] { r4100, r2018, r201A, mode, b4, b5, b6, b7, mir };
        }

        private static void CalcNromVideo(int addr, int mapper,
                                          out byte v4100, out byte r2018, out byte r201A)
        {
            addr += 0x1800;
            if (mapper == 3) addr += 0x4000;
            v4100 = (byte)((addr >> 21) & 0x0F);
            r2018 = (byte)(((addr & 0x1FFFFF) / 0x40000) << 4);
            r201A = (byte)((addr >> 10) & 0xFF);
        }

        // ── MMC3 ──────────────────────────────────────────────────────────────
        //
        // CHR bank addressing formula per NESdev "VT02+ CHR-ROM Bankswitching":
        //   https://www.nesdev.org/wiki/VT02%2B_CHR-ROM_Bankswitching
        //
        // Final 1KB CHR bank# = (InnerBank & InnerMask) | (Middle & ~InnerMask)
        //                     | (IntermBank << 8) | (OuterBank << 11)
        //
        //   $4100 bits 0-3  OuterBank  = (chrAddr >> 21) & 0x0F
        //   $2018 bits 4-6  IntermBank = (chrAddr >> 14) & 0x70
        //   $201A bits 0-2  VB0S       selects InnerMask (see below)
        //   $201A bits 3-7  Middle     = (chrAddr >> 10) & 0xFF & ~InnerMask
        //
        //   VB0S  InnerMask  inner window
        //     6     0x07       8 KB
        //     5     0x0F      16 KB
        //     4     0x1F      32 KB
        //     2     0x3F      64 KB
        //     1     0x7F     128 KB
        //     0     0xFF     256 KB  ← used for CHR >= 256 KB; requires CHR at 256 KB-aligned offset
        //
        // For CHR >= 256 KB the Inner window must be 256 KB (VB0S=0, InnerMask=0xFF).
        // That means the Middle field is zero (anti-mask=0), so the CHR base offset
        // must come entirely from the Intermediate + Outer banks.  Those only resolve
        // cleanly when chrAddr is 256 KB-aligned.  We enforce this by padding the PRG
        // to the next 256 KB multiple before placing CHR.  PRG bank registers (b4–b7)
        // still point into the real (unpadded) PRG data.
        //
        // When InnerBank=0, BankNum resolves to chrAddr exactly →
        // game CHR bank 0 always lands on the start of CHR data.
        private static (byte vb0s, byte innerMask) Vb0sForChr(int chrSize)
        {
            int kb = chrSize / 1024;
            if      (kb <=  8) return (6, 0x07);
            else if (kb <= 16) return (5, 0x0F);
            else if (kb <= 32) return (4, 0x1F);
            else if (kb <= 64) return (2, 0x3F);
            else if (kb <=128) return (1, 0x7F);
            else               return (0, 0xFF);   // 256 KB and above
        }

        // Returns the effective PRG size used for CHR placement (may be padded to alignment).
        //
        // The CHR bankswitching formula requires chrAddr to be aligned to the CHR inner window
        // size so that InnerBank=0 maps exactly to the start of CHR data.  The inner window
        // is determined by VB0S / CHR size:
        //
        //   CHR <=   8 KB  inner =   8 KB
        //   CHR <=  16 KB  inner =  16 KB
        //   CHR <=  32 KB  inner =  32 KB
        //   CHR <=  64 KB  inner =  64 KB
        //   CHR <= 128 KB  inner = 128 KB
        //   CHR >= 256 KB  inner = 256 KB
        //
        // We ensure alignment by rounding PRG up to the next multiple of the inner window.
        // For the common case where PRG >= CHR this is a no-op.
        public static int EffectivePrgForChr(int prgSize, int chrSize)
        {
            int innerWindow = ChrInnerWindow(chrSize);
            if (prgSize % innerWindow != 0)
                return ((prgSize + innerWindow - 1) / innerWindow) * innerWindow;
            return prgSize;
        }

        private static int ChrInnerWindow(int chrSize)
        {
            int kb = chrSize / 1024;
            if (kb <=   8) return   8 * 1024;
            if (kb <=  16) return  16 * 1024;
            if (kb <=  32) return  32 * 1024;
            if (kb <=  64) return  64 * 1024;
            if (kb <= 128) return 128 * 1024;
            return                256 * 1024;
        }

        // Returns the required placement alignment for an MMC3 game with the given PRG size.
        public static int PrgAlignFor(int prgSize) => PsForPrg(prgSize).align;

        // PRG inner-bank PS field ($410B bits 0-2) and required placement alignment.
        //
        // PS selects the AND mask applied to the game's inner PRG bank number.
        // The bits that survive are kept; the rest come from the Middle bank (PQ3/$410A).
        // PS must be chosen so that inner=0xFF (fixed $E000 window) and inner=0xFE
        // ($C000 window) resolve to the last and second-to-last 8KB of PRG respectively.
        //
        //   PS  InnerMask  inner window  PRG size   romOffset alignment
        //    5    0x01       16 KB         16 KB        16 KB
        //    4    0x03       32 KB         32 KB        32 KB
        //    3    0x07       64 KB         64 KB        64 KB
        //    2    0x0F      128 KB        128 KB       128 KB  ← all ref games
        //    1    0x1F      256 KB        256 KB       256 KB
        //    0    0x3F      512 KB        512 KB+      512 KB
        //
        // Using PS=2 for all games only worked for 128 KB PRG.  For 32/64 KB PRG,
        // inner=0xFF masked to 0x0F = bank 15 is past the end of the game, so the
        // fixed $E000 window (which holds the reset vector) read garbage → grey screen.
        // For 64 KB PRG the bug was intermittent: PS=2 accidentally produced the right
        // answer at offset 0x230000 but broke at 128 KB-aligned offsets like 0x240000,
        // which is exactly where the new alignment logic places them.
        private static (byte ps, byte innerMask, int align) PsForPrg(int prgSize)
        {
            int banks = prgSize / 8192;
            if (banks <=  2) return (5, 0x01,  16 * 1024);   //  16 KB
            if (banks <=  4) return (4, 0x03,  32 * 1024);   //  32 KB
            if (banks <=  8) return (3, 0x07,  64 * 1024);   //  64 KB
            if (banks <= 16) return (2, 0x0F, 128 * 1024);   // 128 KB
            if (banks <= 32) return (1, 0x1F, 256 * 1024);   // 256 KB
            return                  (0, 0x3F, 512 * 1024);   // 512 KB+
        }

        private static byte[] BuildMmc3Config(NesRom game, int romOffset)
        {
            var (ps, _, _) = PsForPrg(game.PrgSize);

            int effPrg  = EffectivePrgForChr(game.PrgSize, game.ChrSize);
            int chrAddr = romOffset + effPrg;

            byte va    = (byte)((chrAddr >> 21) & 0x0F);      // OuterBank for CHR → $4100 bits 0-3
            byte r2018 = (byte)((chrAddr >> 14) & 0x70);      // IntermBank VA18-20 → $2018 bits 4-6
            var (vb0s, chrInnerMask) = Vb0sForChr(game.ChrSize);
            byte antiMask = (byte)(~chrInnerMask & 0xFF);
            byte middle   = (byte)(((chrAddr >> 10) & 0xFF) & antiMask);
            byte r201A    = (byte)(vb0s | middle);             // VB0S + CHR Middle bank offset

            byte pa    = (byte)((romOffset >> 21) & 0x0F);    // PRG outer bank → $4100 bits 4-7
            byte r4100 = (byte)((pa << 4) | va);

            int  wb  = (romOffset / WindowSize) * WindowSize;
            int  oin = romOffset - wb;
            byte b4  = Bnk(oin);
            byte b5  = Bnk(oin + 0x2000);
            // b7 = last 8 KB of PRG (fixed $E000 window)
            // b6 = second-to-last 8 KB (fixed $C000 window)
            byte b7  = Bnk(oin + game.PrgSize - 8192);
            byte b6  = Bnk(oin + game.PrgSize - 16384);

            byte f = game.ChrSize > 128 * 1024 ? (byte)0x80 : (byte)0x00;
            return new byte[] { r4100, r2018, r201A, ps, b4, b5, b6, b7, f };
        }

        // ── Game list ─────────────────────────────────────────────────────────
        private static void WriteGameList(byte[] rom, List<string> names)
        {
            byte[] area = new byte[MenuEnd - MenuStart + 1];
            Array.Fill(area, (byte)0xFF);
            area[0] = (byte)(names.Count & 0xFF);
            area[1] = (byte)((names.Count >> 8) & 0xFF);
            Array.Copy(MenuConstants, 0, area, 2, MenuConstants.Length);
            int off = MenuHeaderEnd - MenuStart;
            foreach (string n in names)
            {
                byte[] nb = Encoding.ASCII.GetBytes(n);
                if (off + nb.Length + 1 > area.Length) break;
                Array.Copy(nb, 0, area, off, nb.Length);
                area[off + nb.Length] = 0;
                off += nb.Length + 1;
            }
            Array.Copy(area, 0, rom, MenuStart, area.Length);
        }

        // ── NES 2.0 wrapper (mapper 256) ──────────────────────────────────────
        private static byte[] MakeNes2Rom(byte[] prg, int submapper = 0)
        {
            // NES 2.0 header encoding for mapper 256, submapper N:
            //   byte[6] bits 7-4 = mapper bits 3-0  = 256 & 0x0F           = 0x00
            //   byte[7] bits 7-4 = mapper bits 7-4  = (256>>4) & 0x0F      = 0x00
            //           bits 3-2 = 0x08 (NES 2.0 identifier)
            //   byte[8] bits 3-0 = mapper bits 11-8 = (256>>8) & 0x0F      = 0x01
            //           bits 7-4 = submapper                                 = submapper<<4
            int    banks = prg.Length / 16384;
            byte[] hdr   = new byte[16];
            hdr[0] = (byte)'N';
            hdr[1] = (byte)'E';
            hdr[2] = (byte)'S';
            hdr[3] = 0x1A;
            hdr[4] = (byte)(banks & 0xFF);
            hdr[6] = (byte)((256 & 0x0F) << 4);                   // mapper bits 3-0  → 0x00
            hdr[7] = (byte)(((256 >> 4) & 0x0F) << 4 | 0x08);    // mapper bits 7-4 + NES2.0 → 0x08
            hdr[8] = (byte)(((submapper & 0x0F) << 4) | ((256 >> 8) & 0x0F)); // submapper + mapper bits 11-8
            hdr[9] = (byte)((banks >> 8) & 0x0F);
            byte[] result = new byte[16 + prg.Length];
            Array.Copy(hdr, result, 16);
            Array.Copy(prg, 0, result, 16, prg.Length);
            return result;
        }

        // ── Submapper fixups on MMC3 9-byte config ────────────────────────────
        //
        // The 9-byte config layout: [0]=reg4100 [1]=$2018 [2]=$201A [3]=mode
        //                           [4]=PQ0($4107→$8000) [5]=PQ1($4108→$A000)
        //                           [6]=PQ2($4109→$C000) [7]=PQ3($410A→$E000) [8]=flags
        //
        // Per the NESdev submapper table:
        //   Submapper 2 (Power Joy Supermax): $4107→$A000, $4108→$8000  → swap bytes 4 and 5
        //   All others: standard routing, no change needed
        //
        private static void ApplySubmapperToMmc3Config(byte[] cfg, int submapper)
        {
            if (submapper == 2)
            {
                // Swap PQ0 ($4107) and PQ1 ($4108) — their CPU bank targets are exchanged
                (cfg[4], cfg[5]) = (cfg[5], cfg[4]);
            }
            // Submappers 1,3,4,5 differ only in PPU CHR bank routing (handled by hardware/emulator).
            // Submappers 11-15 differ only in CPU opcode bus scrambling (handled by hardware/emulator).
            // None of those require changes to the 9-byte config written by this builder.
        }

        // ── UNIF wrapper for NintendulatorNRS ("UNL-OneBus") ─────────────────
        //
        // Structure:
        //   "UNIF"  4 bytes
        //   rev=4   4 bytes LE
        //   24 bytes padding (zeros)
        //   MAPR chunk : "UNL-OneBus\0"  (11 bytes)
        //   MIRR chunk : 0x05             (1 byte  — mapper-controlled)
        //   PRG0 chunk : ROM[0x000000–0x1FFFFF]
        //   PRG1 chunk : ROM[0x200000–0x3FFFFF]  (if chip ≥ 4 MB)
        //   PRG2 chunk : ROM[0x400000–0x5FFFFF]  (if chip ≥ 6 MB)
        //   PRG3 chunk : ROM[0x600000–0x7FFFFF]  (if chip = 8 MB)
        //
        private static byte[] MakeUnifRom(byte[] prg)
        {
            const int Window = 0x200000;  // 2 MB per PRG chunk

            // Build chunks list
            var chunks = new List<(byte[] id, byte[] data)>();

            byte[] MaprStr = Encoding.ASCII.GetBytes("UNL-OneBus\0");
            chunks.Add((Encoding.ASCII.GetBytes("MAPR"), MaprStr));
            chunks.Add((Encoding.ASCII.GetBytes("MIRR"), new byte[] { 0x05 }));

            int numWindows = (prg.Length + Window - 1) / Window;
            for (int w = 0; w < numWindows; w++)
            {
                int start = w * Window;
                int len   = Math.Min(Window, prg.Length - start);
                byte[] chunk = new byte[Window];
                Array.Fill(chunk, (byte)0xFF);
                Array.Copy(prg, start, chunk, 0, len);
                string name = $"PRG{w}";
                chunks.Add((Encoding.ASCII.GetBytes(name), chunk));
            }

            // Compute total size
            int totalLen = 32; // UNIF header
            foreach (var (id, data) in chunks)
                totalLen += 4 + 4 + data.Length; // id + len + data

            byte[] unif = new byte[totalLen];
            int pos = 0;

            // UNIF header
            byte[] magic = Encoding.ASCII.GetBytes("UNIF");
            Array.Copy(magic, 0, unif, 0, 4);
            unif[4] = 4; unif[5] = 0; unif[6] = 0; unif[7] = 0; // revision = 4
            pos = 32;

            // Write chunks
            foreach (var (id, data) in chunks)
            {
                Array.Copy(id, 0, unif, pos, id.Length);
                pos += 4;
                unif[pos + 0] = (byte)(data.Length & 0xFF);
                unif[pos + 1] = (byte)((data.Length >> 8) & 0xFF);
                unif[pos + 2] = (byte)((data.Length >> 16) & 0xFF);
                unif[pos + 3] = (byte)((data.Length >> 24) & 0xFF);
                pos += 4;
                Array.Copy(data, 0, unif, pos, data.Length);
                pos += data.Length;
            }

            return unif;
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static byte Bnk(int addr) => (byte)((addr / 8192) & 0xFF);

        private static string CleanName(string fn)
        {
            string n = Path.GetFileNameWithoutExtension(fn);
            int p = n.IndexOf('(');
            if (p >= 0) n = n.Substring(0, p);
            n = n.ToUpperInvariant().Trim();
            var sb = new StringBuilder();
            foreach (char c in n)
                if (char.IsLetterOrDigit(c) || c == ' ') sb.Append(c);
            string r = string.Join(" ", sb.ToString()
                           .Split(new char[]{' '}, StringSplitOptions.RemoveEmptyEntries));
            return r.Length > 20 ? r.Substring(0, 20) : r;
        }

        private static string Hex(byte[] b) =>
            BitConverter.ToString(b).Replace("-", "").ToLower();

        // ── Kernel ROM search ─────────────────────────────────────────────────
        // ── Kernel ROM — embedded directly in the assembly ────────────────────
        //   Logical name set in .csproj: <LogicalName>original_menu_patched.rom</LogicalName>
        //   At runtime the manifest resource name is exactly "original_menu_patched.rom".
        //   If the embedded resource is missing (e.g. during development without a rebuild),
        //   we fall back to loading the file from disk next to the executable.
        private static byte[] LoadKernel()
        {
            var asm = Assembly.GetExecutingAssembly();

            // All plausible manifest resource name forms the SDK might generate
            string[] candidates =
            {
                "original_menu_patched.rom",
                "VT03Builder.original_menu_patched.rom",
                "VT03Builder.Services.original_menu_patched.rom",
            };

            foreach (string name in candidates)
            {
                using var stream = asm.GetManifestResourceStream(name);
                if (stream == null) continue;
                return ReadKernelStream(stream, name);
            }

            // ── Disk fallback (dev builds / missing embed) ────────────────────
            string[] diskPaths =
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "original_menu_patched.rom"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Services", "original_menu_patched.rom"),
                Path.Combine(Directory.GetCurrentDirectory(), "original_menu_patched.rom"),
                Path.Combine(Directory.GetCurrentDirectory(), "Services", "original_menu_patched.rom"),
                // Also try original (unpatched) as last resort
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "original_menu.rom"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Services", "original_menu.rom"),
            };

            foreach (string path in diskPaths)
            {
                if (!File.Exists(path)) continue;
                using var fs = File.OpenRead(path);
                return ReadKernelStream(fs, path);
            }

            // Nothing worked — give a useful diagnostic
            string[] resourceNames = asm.GetManifestResourceNames();
            string available = resourceNames.Length > 0
                ? string.Join(", ", resourceNames)
                : "(none)";
            throw new InvalidOperationException(
                "Kernel ROM not found.\n\n" +
                "The embedded resource was not baked into the assembly.\n" +
                "→ Make sure Services\\original_menu_patched.rom exists, then rebuild.\n\n" +
                "As a fallback, place original_menu_patched.rom (or original_menu.rom)\n" +
                "in the same folder as VT03Builder.exe.\n\n" +
                $"Embedded resources present: {available}");
        }

        private static byte[] ReadKernelStream(Stream stream, string source)
        {
            if (stream.Length != 0x80000)
                throw new InvalidDataException(
                    $"Kernel ROM '{source}' is {stream.Length} bytes — expected 524288 (512 KB).");
            byte[] data = new byte[stream.Length];
            _ = stream.Read(data, 0, data.Length);
            return data;
        }
    }

    // ── Result ────────────────────────────────────────────────────────────────
    public class BuildResult
    {
        public byte[] NorBinary { get; set; } = Array.Empty<byte>();
        public byte[] NesFile   { get; set; } = Array.Empty<byte>();
        public byte[] UnifFile  { get; set; } = Array.Empty<byte>();
        public int    GameCount { get; set; }
        public int    NromUsed  { get; set; }
        public int    Mmc3Used  { get; set; }
    }
}
