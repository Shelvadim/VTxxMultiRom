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
    /// Orchestrates building a VT03 OneBus NOR flash multicart binary.
    ///
    /// ROM layout (flat binary, filled with 0xFF):
    ///
    ///   Kernel-required addresses (from original_menu_patched.rom):
    ///     0x040000–0x040FFF   CHR font (4 KB)
    ///     0x079000–0x079FFF   GAMELIST  (written by builder)
    ///     0x07C000–0x07C1FF   CFGTABLE  (9 bytes × game count, written by builder)
    ///     0x07E000–0x07FFFF   CPU code  (8 KB)
    ///
    ///   Free kernel regions used for NROM/CNROM game packing:
    ///     0x000000–0x03FFFF   256 KB  (before CHR font)
    ///     0x041000–0x078FFF   224 KB  (between CHR and GAMELIST)
    ///
    ///   Game overflow (NROM):   0x080000–0x1FFFFF
    ///   MMC3 games:             0x200000+  (always in 2 MB windows)
    ///
    ///   On a 2 MB chip (sharedWindow): MMC3 games follow NROM in the same window.
    ///
    ///   Output size = ChipSizeMb × 1048576, pre-filled with 0xFF.
    /// </summary>
    public static class RomBuilder
    {
        // ── Layout constants ──────────────────────────────────────────────────
        private const int MenuStart       = 0x079000;
        private const int MenuEnd         = 0x079FFF;
        private const int MenuHeaderEnd   = 0x079010;
        private const int ConfigTableAddr = 0x07C000;
        private const int KernelEnd       = 0x080000;   // kernel spans 0x000000–0x07FFFF
        private const int Mmc3Start       = 0x200000;   // MMC3 starts here on chips > 2 MB
        private const int WindowSize      = 0x200000;   // 2 MB PRG window boundary

        // Free regions inside the kernel that NROM/CNROM games can safely use.
        private static readonly (int Start, int End)[] KernelFreeRegions =
        {
            (0x000000, 0x040000),  // 256 KB — before CHR font
            (0x041000, 0x079000),  // 224 KB — between CHR and GAMELIST
        };

        private static readonly byte[] MenuConstants =
        {
            0x14, 0x00, 0x08, 0x08, 0x08, 0x04, 0x04, 0x08,
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00,
        };

        // ── Public entry point ────────────────────────────────────────────────
        public static BuildResult Build(BuildConfig cfg, IProgress<string>? log = null)
            => Build(cfg, LoadKernel(), log);

        /// <summary>Testable overload — caller supplies kernel bytes directly.</summary>
        internal static BuildResult Build(BuildConfig cfg, byte[] kernelData,
                                          IProgress<string>? log = null)
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

            // On a 2 MB chip the single 2 MB window is shared by NROM and MMC3.
            bool sharedWindow = romSize <= WindowSize;
            int  nromLimit    = sharedWindow ? romSize : Mmc3Start;

            // ── NROM / CNROM — free-list packer ──────────────────────────────
            // Priority 1: kernel free gaps (480 KB)
            // Priority 2: 0x080000–nromLimit overflow
            var freeList = new List<(int Start, int End)>(KernelFreeRegions)
            {
                (KernelEnd, nromLimit)
            };
            int nromHighWater = KernelEnd;

            foreach (var game in nromGames)
            {
                int gameSize = game.PrgSize + game.ChrSize;
                int align    = Mapper256Builder.PrgAlignFor(game.PrgSize);
                int placed   = -1;

                for (int fi = 0; fi < freeList.Count; fi++)
                {
                    var (fs, fe) = freeList[fi];
                    int slotStart = ((fs + align - 1) / align) * align;
                    if (slotStart + gameSize > fe) continue;

                    placed = slotStart;
                    freeList.RemoveAt(fi);
                    int slotEnd = slotStart + ((gameSize + align - 1) / align) * align;
                    if (slotStart > fs)
                        freeList.Insert(fi, (fs, slotStart));
                    if (slotEnd < fe)
                        freeList.Insert(fi + (slotStart > fs ? 1 : 0), (slotEnd, fe));
                    break;
                }

                if (placed < 0)
                {
                    log?.Report($"SKIP  {game.FileName}: no NROM space left");
                    continue;
                }

                Array.Copy(game.RawData, 0, rom, placed, gameSize);
                byte[] c = Mapper256Builder.BuildNromConfig(
                    placed, game.PrgSize, game.ChrSize, game.Mapper, game.Vertical);
                names.Add(CleanName(game.FileName));
                configs.Add(c);
                bool inGap = placed < KernelEnd;
                log?.Report($"NROM  {game.FileName,-32} @ 0x{placed:X6}" +
                            $"{(inGap ? " [kernel gap]" : "")}  cfg={Hex(c)}");
                if (!inGap)
                    nromHighWater = Math.Max(nromHighWater,
                        placed + ((gameSize + align - 1) / align) * align);
            }

            // ── MMC3 ─────────────────────────────────────────────────────────
            // On 2 MB chip: start right after NROM (or at KernelEnd if all fit in gaps).
            // On larger chips: always at Mmc3Start = 0x200000.
            int mmc3Floor = sharedWindow ? Math.Max(KernelEnd, nromHighWater) : Mmc3Start;
            int curMmc3   = mmc3Floor;

            if (sharedWindow && mmc3Games.Count > 0)
                log?.Report("NOTE  2 MB chip — MMC3 games packed after NROM in the same window");

            foreach (var game in mmc3Games)
            {
                (byte psVal, byte innerMaskVal, int prgAlign) = Mapper256Builder.PsForPrg(game.PrgSize);
                int effPrg   = Mapper256Builder.EffectivePrgForChr(game.PrgSize, game.ChrSize);
                int physSize = effPrg + game.ChrSize;

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

                Array.Copy(game.RawData, 0, rom, curMmc3, game.PrgSize);
                Array.Copy(game.RawData, game.PrgSize, rom, curMmc3 + effPrg, game.ChrSize);
                byte[] c = Mapper256Builder.BuildMmc3Config(
                    curMmc3, game.PrgSize, game.ChrSize);
                Mapper256Builder.ApplySubmapperToMmc3Config(c, cfg.Submapper);
                names.Add(CleanName(game.FileName));
                configs.Add(c);
                int padKB = (effPrg - game.PrgSize) / 1024;
                string pad = padKB > 0 ? $" [+{padKB}KB CHR-align pad]" : "";
                log?.Report($"MMC3  {game.FileName,-32} @ 0x{curMmc3:X6}  cfg={Hex(c)}{pad}");
                curMmc3 += physSize;
            }

            WriteGameList(rom, names);
            for (int i = 0; i < configs.Count; i++)
                Array.Copy(configs[i], 0, rom, ConfigTableAddr + i * 9, 9);

            int mmc3Used = Math.Max(0, curMmc3 - mmc3Floor);
            log?.Report($"Done  {names.Count} games  |  chip {cfg.ChipSizeMb} MB  |  " +
                        $"NROM overflow {Math.Max(0, nromHighWater - KernelEnd) / 1024} KB  |  " +
                        $"MMC3 {mmc3Used / 1024} KB");

            return new BuildResult
            {
                NorBinary = rom,
                NesFile   = Mapper256Builder.MakeNes2Rom(rom, cfg.Submapper),
                UnifFile  = Mapper256Builder.MakeUnifRom(rom),
                GameCount = names.Count,
                NromUsed  = Math.Max(0, nromHighWater - KernelEnd),
                Mmc3Used  = mmc3Used,
            };
        }

        // ── Public helpers (used by tests / SpaceCalculator) ──────────────────

        /// <summary>Forwards to Mapper256Builder for backwards compatibility.</summary>
        public static int PrgAlignFor(int prgSize)
            => Mapper256Builder.PrgAlignFor(prgSize);

        /// <summary>Forwards to Mapper256Builder for backwards compatibility.</summary>
        public static int EffectivePrgForChr(int prgSize, int chrSize)
            => Mapper256Builder.EffectivePrgForChr(prgSize, chrSize);

        // ── How much space is available for games ─────────────────────────────
        public static long UsableBytes(int chipSizeMb)
        {
            long kernelFree   = KernelFreeRegions.Sum(r => (long)(r.End - r.Start)); // 480 KB
            long nromOverflow = Mmc3Start - KernelEnd;    // 1.5 MB
            long mmc3Space    = Math.Max(0L, (long)chipSizeMb * 1024L * 1024L - Mmc3Start);
            return kernelFree + nromOverflow + mmc3Space;
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

        // ── Kernel loader ─────────────────────────────────────────────────────
        private static byte[] LoadKernel()
        {
            var asm = Assembly.GetExecutingAssembly();
            string[] candidates =
            {
                "original_menu_patched.rom",
                "VT03Builder.original_menu_patched.rom",
            };
            foreach (string name in candidates)
            {
                using var stream = asm.GetManifestResourceStream(name);
                if (stream == null) continue;
                return ReadFlatKernel(stream, name);
            }
            string[] diskPaths =
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "original_menu_patched.rom"),
                Path.Combine(Directory.GetCurrentDirectory(), "original_menu_patched.rom"),
            };
            foreach (string path in diskPaths)
            {
                if (!File.Exists(path)) continue;
                using var fs = File.OpenRead(path);
                return ReadFlatKernel(fs, path);
            }
            string[] rn = asm.GetManifestResourceNames();
            throw new InvalidOperationException(
                "Kernel ROM not found. Embed Services\\original_menu_patched.rom and rebuild.\n" +
                $"Resources: {string.Join(", ", rn.DefaultIfEmpty("(none)"))}");
        }

        private static byte[] ReadFlatKernel(Stream stream, string source)
        {
            if (stream.Length != 0x80000)
                throw new InvalidDataException(
                    $"Kernel '{source}' is {stream.Length} bytes — expected 524288 (512 KB).");
            byte[] data = new byte[stream.Length];
            _ = stream.Read(data, 0, data.Length);
            return data;
        }

        // ── Private helpers ───────────────────────────────────────────────────
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
                           .Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            return r.Length > 20 ? r.Substring(0, 20) : r;
        }

        private static string Hex(byte[] b) =>
            BitConverter.ToString(b).Replace("-", "").ToLower();
    }

        public static class Mapper256Builder
        {
            // ── NROM / CNROM ──────────────────────────────────────────────────────

            public static byte[] BuildNromConfig(
                int romOffset, int prgSize, int chrSize, int mapper, bool vertical)
            {
                CalcNromVideo(romOffset + prgSize, mapper,
                              out byte v4100, out byte r2018, out byte r201A);
                byte pa    = (byte)((romOffset >> 21) & 0x0F);
                byte r4100 = (byte)((pa << 4) | (v4100 & 0x0F));
                byte b4    = Bnk(romOffset);
                byte b5    = Bnk(romOffset + 0x2000);
                byte b6    = Bnk(romOffset + prgSize - 0x4000);
                byte b7    = Bnk(romOffset + prgSize - 0x2000);
                byte mir   = vertical ? (byte)0x00 : (byte)0x01;
                byte mode  = prgSize >= 32768 ? (byte)0x04 : (byte)0x05;
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
            // CHR bank addressing — NESdev "VT02+ CHR-ROM Bankswitching":
            //   BankNum = (InnerBank & InnerMask) | (Middle & ~InnerMask)
            //           | (IntermBank << 8) | (OuterBank << 11)
            //
            //   $4100 bits 0-3  OuterBank  = (chrAddr >> 21) & 0x0F
            //   $2018 bits 4-6  IntermBank = (chrAddr >> 14) & 0x70
            //   $201A bits 0-2  VB0S       → InnerMask (table below)
            //   $201A bits 3-7  Middle     = (chrAddr >> 10) & 0xFF & ~InnerMask
            //
            //   VB0S   InnerMask   inner window
            //     6      0x07        8 KB
            //     5      0x0F       16 KB
            //     4      0x1F       32 KB
            //     2      0x3F       64 KB
            //     1      0x7F      128 KB
            //     0      0xFF      256 KB

            public static byte[] BuildMmc3Config(
                int romOffset, int prgSize, int chrSize)
            {
                var (ps, _, _) = PsForPrg(prgSize);
                int effPrg  = EffectivePrgForChr(prgSize, chrSize);
                int chrAddr = romOffset + effPrg;

                byte va    = (byte)((chrAddr >> 21) & 0x0F);
                byte r2018 = (byte)((chrAddr >> 14) & 0x70);
                var (vb0s, chrInnerMask) = Vb0sForChr(chrSize);
                byte antiMask = (byte)(~chrInnerMask & 0xFF);
                byte middle   = (byte)(((chrAddr >> 10) & 0xFF) & antiMask);
                byte r201A    = (byte)(vb0s | middle);

                byte pa    = (byte)((romOffset >> 21) & 0x0F);
                byte r4100 = (byte)((pa << 4) | va);

                const int Window = 0x200000;
                int  wb  = (romOffset / Window) * Window;
                int  oin = romOffset - wb;
                byte b4  = Bnk(oin);
                byte b5  = Bnk(oin + 0x2000);
                byte b7  = Bnk(oin + prgSize - 8192);
                byte b6  = Bnk(oin + prgSize - 16384);

                byte f = chrSize > 128 * 1024 ? (byte)0x80 : (byte)0x00;
                return new byte[] { r4100, r2018, r201A, ps, b4, b5, b6, b7, f };
            }

            // ── PRG bank helpers ──────────────────────────────────────────────────

            /// <summary>Returns the required placement alignment for an MMC3 game.</summary>
            public static int PrgAlignFor(int prgSize) => PsForPrg(prgSize).align;

            /// <summary>
            /// PS field ($410B bits 0-2), InnerMask, and required placement alignment.
            ///
            ///   PS  InnerMask  inner window  PRG size   alignment
            ///    5    0x01       16 KB         16 KB      16 KB
            ///    4    0x03       32 KB         32 KB      32 KB
            ///    3    0x07       64 KB         64 KB      64 KB
            ///    2    0x0F      128 KB        128 KB     128 KB
            ///    1    0x1F      256 KB        256 KB     256 KB
            ///    0    0x3F      512 KB        512 KB+    512 KB
            /// </summary>
            public static (byte ps, byte innerMask, int align) PsForPrg(int prgSize)
            {
                int banks = prgSize / 8192;
                if (banks <=  2) return (5, 0x01,  16 * 1024);
                if (banks <=  4) return (4, 0x03,  32 * 1024);
                if (banks <=  8) return (3, 0x07,  64 * 1024);
                if (banks <= 16) return (2, 0x0F, 128 * 1024);
                if (banks <= 32) return (1, 0x1F, 256 * 1024);
                return                  (0, 0x3F, 512 * 1024);
            }

            // ── CHR bank helpers ──────────────────────────────────────────────────

            private static (byte vb0s, byte innerMask) Vb0sForChr(int chrSize)
            {
                int kb = chrSize / 1024;
                if      (kb <=   8) return (6, 0x07);
                else if (kb <=  16) return (5, 0x0F);
                else if (kb <=  32) return (4, 0x1F);
                else if (kb <=  64) return (2, 0x3F);
                else if (kb <= 128) return (1, 0x7F);
                else                return (0, 0xFF);
            }

            /// <summary>
            /// Effective PRG size used for CHR placement — pads PRG to CHR inner-window
            /// boundary so that InnerBank=0 maps exactly to the start of CHR data.
            /// </summary>
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

            // ── Submapper ─────────────────────────────────────────────────────────

            /// <summary>
            /// Apply submapper-specific fixups to a 9-byte MMC3 config.
            ///   Submapper 2 (Power Joy Supermax): swap PQ0/PQ1 bytes (4 and 5).
            ///   All other submappers: no change.
            /// </summary>
            public static void ApplySubmapperToMmc3Config(byte[] cfg, int submapper)
            {
                if (submapper == 2)
                    (cfg[4], cfg[5]) = (cfg[5], cfg[4]);
            }

            // ── NES 2.0 file wrapper ──────────────────────────────────────────────

            public static byte[] MakeNes2Rom(byte[] prg, int submapper = 0)
            {
                byte[] hdr    = MakeNes2Header(prg.Length / 16384, submapper);
                byte[] result = new byte[16 + prg.Length];
                Array.Copy(hdr, result, 16);
                Array.Copy(prg, 0, result, 16, prg.Length);
                return result;
            }

            /// <summary>Produces a 16-byte NES 2.0 header for mapper 256, given PRG bank count.</summary>
            public static byte[] MakeNes2Header(int prg16kBanks, int submapper = 0)
            {
                // NES 2.0 mapper 256, submapper N:
                //   byte[6] bits 7-4 = mapper bits 3-0  = 256 & 0x0F         = 0x00
                //   byte[7] bits 7-4 = mapper bits 7-4  = (256>>4) & 0x0F    = 0x00
                //           bits 3-2 = 0x08 (NES 2.0 identifier)
                //   byte[8] bits 3-0 = mapper bits 11-8 = (256>>8) & 0x0F    = 0x01
                //           bits 7-4 = submapper                               = submapper<<4
                byte[] hdr = new byte[16];
                hdr[0] = (byte)'N';
                hdr[1] = (byte)'E';
                hdr[2] = (byte)'S';
                hdr[3] = 0x1A;
                hdr[4] = (byte)(prg16kBanks & 0xFF);
                hdr[6] = (byte)((256 & 0x0F) << 4);
                hdr[7] = (byte)(((256 >> 4) & 0x0F) << 4 | 0x08);
                hdr[8] = (byte)(((submapper & 0x0F) << 4) | ((256 >> 8) & 0x0F));
                hdr[9] = (byte)((prg16kBanks >> 8) & 0x0F);
                return hdr;
            }

            // ── UNIF wrapper ──────────────────────────────────────────────────────

            public static byte[] MakeUnifRom(byte[] prg)
            {
                const int Window = 0x200000;
                var chunks = new List<(byte[] id, byte[] data)>();
                chunks.Add((Encoding.ASCII.GetBytes("MAPR"), Encoding.ASCII.GetBytes("UNL-OneBus\0")));
                chunks.Add((Encoding.ASCII.GetBytes("MIRR"), new byte[] { 0x05 }));
                int numWindows = (prg.Length + Window - 1) / Window;
                for (int w = 0; w < numWindows; w++)
                {
                    int start = w * Window;
                    int len   = Math.Min(Window, prg.Length - start);
                    byte[] chunk = new byte[Window];
                    Array.Fill(chunk, (byte)0xFF);
                    Array.Copy(prg, start, chunk, 0, len);
                    chunks.Add((Encoding.ASCII.GetBytes($"PRG{w}"), chunk));
                }
                int totalLen = 32;
                foreach (var (id, data) in chunks)
                    totalLen += 4 + 4 + data.Length;
                byte[] unif = new byte[totalLen];
                Array.Copy(Encoding.ASCII.GetBytes("UNIF"), 0, unif, 0, 4);
                unif[4] = 4;
                int pos = 32;
                foreach (var (id, data) in chunks)
                {
                    Array.Copy(id, 0, unif, pos, id.Length); pos += 4;
                    unif[pos+0] = (byte)(data.Length & 0xFF);
                    unif[pos+1] = (byte)((data.Length >> 8) & 0xFF);
                    unif[pos+2] = (byte)((data.Length >> 16) & 0xFF);
                    unif[pos+3] = (byte)((data.Length >> 24) & 0xFF);
                    pos += 4;
                    Array.Copy(data, 0, unif, pos, data.Length); pos += data.Length;
                }
                return unif;
            }

            // ── Header description (for UI "Generate Header" button) ─────────────

            /// <summary>
            /// Returns a multi-line human-readable description of the NES 2.0 header
            /// for mapper 256 with the given submapper and chip size.
            /// </summary>
            public static string DescribeHeader(int submapper, int chipMb)
            {
                int prg16k = (chipMb * 1024 * 1024) / (16 * 1024);
                byte[] hdr = MakeNes2Header(prg16k, submapper);

                var sb = new StringBuilder();
                sb.AppendLine("── NES 2.0 Header (Mapper 256 / OneBus / VT03) ─────────────────────");
                sb.AppendLine();

                // Hex dump
                sb.Append("  Hex:  ");
                for (int i = 0; i < 16; i++)
                {
                    sb.Append($"{hdr[i]:X2}");
                    if (i == 7) sb.Append("  ");
                    else if (i < 15) sb.Append(' ');
                }
                sb.AppendLine();
                sb.AppendLine();

                // Field breakdown
                sb.AppendLine($"  [0-3]  Magic     : {(char)hdr[0]}{(char)hdr[1]}{(char)hdr[2]} 1A");
                sb.AppendLine($"  [4]    PRG size  : {hdr[4]:X2}h  ({prg16k} × 16KB = {chipMb} MB)");
                sb.AppendLine($"  [5]    CHR size  : 00h  (no CHR-ROM in header — all in PRG flash)");
                sb.AppendLine($"  [6]    Flags6    : {hdr[6]:X2}h  mapper bits 3-0 = {hdr[6] >> 4:X1}");
                sb.AppendLine($"  [7]    Flags7    : {hdr[7]:X2}h  mapper bits 7-4 = {(hdr[7] >> 4) & 0xF:X1},  NES2 id = {(hdr[7] >> 2) & 3:X1}");
                sb.AppendLine($"  [8]    Mapper hi : {hdr[8]:X2}h  submapper = {(hdr[8] >> 4) & 0xF},  mapper bits 11-8 = {hdr[8] & 0xF:X1}");
                sb.AppendLine($"  [9]    PRG hi    : {hdr[9]:X2}h  PRG size MSB");
                sb.AppendLine($"  [10-15] Unused   : 00 00 00 00 00 00");
                sb.AppendLine();

                // Summary
                int mapperNum = ((hdr[6] >> 4) & 0xF) | ((hdr[7] & 0xF0)) | (((hdr[8] & 0x0F)) << 8);
                int smNum     = (hdr[8] >> 4) & 0x0F;
                sb.AppendLine($"  Mapper: {mapperNum}  Submapper: {smNum}  PRG: {chipMb} MB");
                sb.Append(    $"  Output: {chipMb * 1024 * 1024 / 1024} KB flash image");

                return sb.ToString();
            }

            // ── Private helpers ───────────────────────────────────────────────────

            private static byte Bnk(int addr) => (byte)((addr / 8192) & 0xFF);
        }

    // ── Build result ──────────────────────────────────────────────────────────
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
