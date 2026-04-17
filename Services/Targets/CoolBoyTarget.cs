using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using VT03Builder.Models;
using VT03Builder.Services.Hardware;
using VT03Builder.Services.Output;

namespace VT03Builder.Services.Targets
{
    /// <summary>
    /// Hardware target: CoolBoy / Mindkids AA6023 multicart (NES 2.0 mapper 268).
    ///
    /// Cheap Famicom cartridges using the AA6023 ASIC (labelled SMD132/SMD133).
    /// Supports NROM and MMC3 games. Always uses CHR-RAM — CHR data from the NES
    /// file is copied from flash to CHR-RAM by the menu loader before the game starts.
    ///
    /// Flash layout:
    ///   0x000000–end   Game data packed sequentially in 512 KB MMC3 outer windows.
    ///                  No fixed kernel area — the menu ROM is an ordinary NES file
    ///                  loaded into the first outer window.
    ///
    /// Submappers:
    ///   0  CoolBoy  — outer bank regs at $6000–$6003
    ///   1  Mindkids — outer bank regs at $5000–$5003
    ///   (hardware is identical; a solder pad selects the register address range)
    ///
    /// Key differences from VTxx OneBus:
    ///   • No kernel area — entire flash is available for games
    ///   • Always CHR-RAM (256 KB max; some boards only 128 KB)
    ///   • Config record is 8 bytes (not 9)
    ///   • Output is .nes / .unf with UNIF board "COOLBOY" or "MINDKIDS"
    ///   • Menu is a standard NES ROM in MMC3 format (not embedded in flash kernel)
    ///   • After init, lockout bit ($6003/$5003 bit 7) must be set to prevent
    ///     the running game from accidentally overwriting outer bank registers
    /// </summary>
    public sealed class CoolBoyTarget : IHardwareTarget
    {
        // ── Identity ──────────────────────────────────────────────────────────

        public string Id           => "coolboy";
        public string DisplayName  => "CoolBoy / Mindkids (Mapper 268)";
        public int    OutputMapper => 268;

        // ── Hardware limits ───────────────────────────────────────────────────

        public long MaxFlashBytes     => 32L * 1024 * 1024;   // 32 MB
        public int  ConfigRecordBytes => CoolBoyBanking.ConfigBytes;  // 10 bytes (indices 0-9)
        public bool AlwaysChrRam      => true;    // AA6023 has no CHR-ROM mode

        /// <summary>
        /// CoolBoy has no embedded kernel — the menu is a user-supplied NES ROM.
        /// However, the first 512 KB of flash is reserved for the menu window and
        /// must not be overwritten by the game packer.
        /// HasKernelArea = false means we don't embed a kernel ROM ourselves.
        /// KernelAreaSize = 0x080000 tells the packer where games may start.
        /// </summary>
        public bool HasKernelArea  => false;
        public int  KernelAreaSize => MenuWindowSize;   // 0x080000 = 512 KB

        public IReadOnlyList<int> SupportedSourceMappers { get; } =
            new[] { 0, 4 };   // NROM and MMC3 only

        // ── Submappers ────────────────────────────────────────────────────────

        public IReadOnlyList<SubmapperInfo> Submappers { get; } = new[]
        {
            new SubmapperInfo { Number = 0, Name = "CoolBoy  (outer regs at $6000–$6003)" },
            new SubmapperInfo { Number = 1, Name = "Mindkids (outer regs at $5000–$5003)" },
            new SubmapperInfo { Number = 2, Name = "Variant 2" },
            new SubmapperInfo { Number = 3, Name = "Variant 3" },
            new SubmapperInfo { Number = 4, Name = "Variant 4" },
            new SubmapperInfo { Number = 5, Name = "Variant 5" },
        };

        // ── Flash layout constants ────────────────────────────────────────────

        // CoolBoy has no kernel area. Games are packed sequentially from offset 0.
        // The menu occupies the first 512KB outer window (the "menu window").
        // Game data starts at 0x080000 (after the first two 256KB MMC3 windows).
        // This mirrors how ClusterM's toolset lays out the flash.
        public const int MenuWindowSize = 512 * 1024;   // 512 KB menu slot
        internal const int GameAreaStart  = MenuWindowSize; // 0x080000

        // ── Build steps ───────────────────────────────────────────────────────

        /// <summary>
        /// CoolBoy has no embedded kernel — the menu is a separate NES ROM.
        /// We fill the flash with 0xFF here; the caller (Build()) places games.
        /// The menu ROM itself is NOT embedded in the flash by this tool —
        /// it must be written to the first outer window separately by the user.
        /// </summary>
        public void InitialiseFlash(byte[] flash, BuildConfig cfg)
        {
            // Flash is already pre-filled with 0xFF by RomBuilder.Build().
            // Copy the embedded 128 KB menu ROM into the start of flash.
            byte[] menu = LoadMenuRom();
            // Menu ROM is 128 KB; the window is 512 KB — zero-pad the rest with 0xFF.
            Array.Copy(menu, 0, flash, 0, menu.Length);
        }

        /// <summary>
        /// Load the CoolBoy menu ROM from the embedded resource.
        /// The ROM is a 128 KB raw PRG binary generated by coolboy_menu_assemble.py.
        /// Falls back to a file on disk (same directory as the executable) if the
        /// embedded resource is not found — useful during development.
        /// </summary>
        internal static byte[] LoadMenuRom()
        {
            const int ExpectedSize = 128 * 1024;   // 131072 bytes
            var asm = Assembly.GetExecutingAssembly();
            string[] candidates =
            {
                "coolboy_menu.rom",
                "VT03Builder.coolboy_menu.rom",
            };
            foreach (string name in candidates)
            {
                using var s = asm.GetManifestResourceStream(name);
                if (s != null) return ReadMenuStream(s, name, ExpectedSize);
            }
            // Fallback: load from disk (for development / running outside VS)
            string[] diskPaths =
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "coolboy_menu.rom"),
                Path.Combine(Directory.GetCurrentDirectory(),       "coolboy_menu.rom"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                             "Services", "coolboy_menu.rom"),
            };
            foreach (string path in diskPaths)
            {
                if (!File.Exists(path)) continue;
                using var fs = File.OpenRead(path);
                return ReadMenuStream(fs, path, ExpectedSize);
            }
            string[] rn = asm.GetManifestResourceNames();
            throw new InvalidOperationException(
                "CoolBoy menu ROM not found. " +
                "Embed Services\\coolboy_menu.rom and rebuild, or place coolboy_menu.rom " +
                "next to the executable.\n" +
                $"Resources: {string.Join(", ", rn.DefaultIfEmpty("(none)"))}");
        }

        private static byte[] ReadMenuStream(Stream s, string source, int expectedSize)
        {
            if (s.Length != expectedSize)
                throw new InvalidDataException(
                    $"CoolBoy menu ROM '{source}' is {s.Length} bytes — " +
                    $"expected {expectedSize} (128 KB).");
            byte[] data = new byte[s.Length];
            _ = s.Read(data, 0, data.Length);
            return data;
        }

        /// <summary>
        /// Translate a hardware-neutral GameBankingInfo into the 10-byte CoolBoy
        /// config record (outer bank regs + CHR source bank + size + mirror).
        /// CoolBoyBanking derives CHR parameters from game.PrgSize / game.ChrSize
        /// and the PRG flash offset.
        /// </summary>
        public byte[] BuildConfigRecord(NesRom game, GameBankingInfo info,
                                        BuildConfig cfg)
        {
            return CoolBoyBanking.BuildConfig(game, info.PrgFlashOffset);
        }

        /// <summary>
        /// Write the per-game config records into the flash game table.
        ///
        /// Layout (must match the addresses the menu ROM reads at boot):
        ///   Flash 0x004000:  Game count (2 bytes, little-endian)
        ///   Flash 0x004002+: Game records, 32 bytes each:
        ///     [0..9]   CoolBoy config record (outer regs + CHR params + mirror)
        ///     [10]     name_len (0–20)
        ///     [11..30] game name (ASCII, null-padded to 20 bytes)
        ///     [31]     pad (0)
        ///
        /// The menu ROM accesses this by switching MMC3 reg6=2, reg7=3 which
        /// maps flash 0x004000 to CPU $8000.  Game count is at CPU $8000,
        /// records start at CPU $8002.
        /// </summary>
        public void WriteGameTable(byte[] flash,
                                   IReadOnlyList<GameEntry> entries,
                                   BuildConfig cfg)
        {
            // Flash 0x004000 = PRG banks 2+3 of the menu ROM window.
            // The menu ROM reads this region via MMC3 register switching.
            const int TableBase  = 0x004000;
            const int TotalRec   = 32;    // bytes per record in the table
            const int NameOffset = 10;    // offset of name_len within each record
            const int NameMax    = 20;    // max name characters stored

            int count = Math.Min(entries.Count, (flash.Length - TableBase - 2) / TotalRec);

            // Write game count as uint16 LE
            flash[TableBase + 0] = (byte)(count & 0xFF);
            flash[TableBase + 1] = (byte)((count >> 8) & 0xFF);

            int recBase = TableBase + 2;
            for (int i = 0; i < count; i++)
            {
                int recOff = recBase + i * TotalRec;
                if (recOff + TotalRec > flash.Length) break;

                // [0..9] config record
                byte[] cfg_rec = entries[i].ConfigRecord;
                int copyLen = Math.Min(cfg_rec.Length, ConfigRecordBytes);
                Array.Copy(cfg_rec, 0, flash, recOff, copyLen);

                // [10] name_len  [11..30] name  [31] pad
                string name = entries[i].DisplayName ?? string.Empty;
                if (name.Length > NameMax) name = name[..NameMax];
                byte[] nb = Encoding.ASCII.GetBytes(name);
                flash[recOff + NameOffset] = (byte)nb.Length;
                for (int c = 0; c < nb.Length; c++)
                    flash[recOff + NameOffset + 1 + c] = nb[c];
                // remaining bytes in name area are already 0xFF from flash init —
                // the menu reads name_len bytes only, so 0xFF padding is harmless.
                flash[recOff + TotalRec - 1] = 0;   // pad byte [31]
            }
        }

        /// <summary>
        /// Produce .nes and .unf output files.
        /// The UNIF board name is "COOLBOY" for submapper 0, "MINDKIDS" for submapper 1.
        /// </summary>
        public BuildResult BuildOutputFiles(byte[] flash, BuildConfig cfg)
        {
            string board = cfg.Submapper == 1 ? "MINDKIDS" : "COOLBOY";

            // The .nes / .unf test files contain ONLY the 128 KB menu ROM as PRG.
            // This is critical for emulator compatibility:
            //   • At reset, mapper 268 with all outer regs = 0 maps the last 8 KB
            //     inner bank to CPU $E000-$FFFF.  For 128 KB PRG that is bank 15
            //     (flash 0x01E000) = our RESET handler.  Correct!
            //   • If the full flash (e.g. 8 MB) were declared as PRG, the last
            //     inner bank would be in the game-data area (0xFF bytes).  Grey screen.
            // The full flash .bin is written for the T48/Xgpro hardware programmer.
            // Games remain accessible via mapper 268 outer bank registers at runtime.
            const int MenuRomSize = 128 * 1024;   // first 128 KB of flash = menu window PRG
            byte[] menuPrg = flash[..Math.Min(MenuRomSize, flash.Length)];

            byte[] nesFile  = Array.Empty<byte>();
            byte[] unifFile = Array.Empty<byte>();

            if (cfg.GenerateNes)
            {
                // NES 2.0 header: mapper 268, PRG = 128 KB (8 × 16 KB banks), CHR-RAM
                byte[] hdr = MakeNes2Header268(menuPrg.Length / 16384, cfg.Submapper);
                nesFile = new byte[16 + menuPrg.Length];
                Array.Copy(hdr,    nesFile, 16);
                Array.Copy(menuPrg, 0, nesFile, 16, menuPrg.Length);

                unifFile = NesFileWriter.MakeUnifRom(menuPrg, board);
            }

            return new BuildResult
            {
                NorBinary = (byte[])flash.Clone(),
                NesFile   = nesFile,
                UnifFile  = unifFile,
            };
        }

        // ── NES 2.0 header for mapper 268 ────────────────────────────────────

        private static byte[] MakeNes2Header268(int prg16kBanks, int submapper)
        {
            // Mapper 268 = 0x10C
            // byte[6] bits 7-4 = mapper 3-0  = 268 & 0x0F          = 0x0C → 0xC0
            // byte[7] bits 7-4 = mapper 7-4  = (268>>4) & 0x0F     = 0x00 → 0x08 (NES2 id)
            // byte[8] bits 3-0 = mapper 11-8 = (268>>8) & 0x0F     = 0x01
            //         bits 7-4 = submapper
            const int Mapper = 268;
            byte[] hdr = new byte[16];
            hdr[0] = (byte)'N'; hdr[1] = (byte)'E';
            hdr[2] = (byte)'S'; hdr[3] = 0x1A;
            hdr[4] = (byte)(prg16kBanks & 0xFF);
            hdr[6] = (byte)((Mapper & 0x0F) << 4);            // = 0xC0
            hdr[7] = (byte)(((Mapper >> 4) & 0x0F) << 4 | 0x08);  // = 0x08
            hdr[8] = (byte)(((submapper & 0x0F) << 4) | ((Mapper >> 8) & 0x0F)); // = sub<<4 | 1
            hdr[9] = (byte)((prg16kBanks >> 8) & 0x0F);
            // CHR-RAM = 256 KB: NES 2.0 byte 11 bits 3-0 = shift count for 64<<N bytes
            // 256KB = 64 << 12... wait: 64<<N. 256KB=262144. 262144/64=4096=2^12. N=12? No.
            // Actually: size = 64 << shift. 256KB: 256*1024 = 262144. 262144/64 = 4096. log2(4096)=12.
            // So shift=12. But field is 4 bits (max 15), value 12 = 0x0C. That gives 64<<12 = 256KB ✓
            hdr[11] = 0x07;   // CHR-RAM: 64<<7 = 8192... hmm
            // Correct: 64 << shift = chrRamBytes. For 256KB: need shift such that 64<<shift=262144.
            // 262144 / 64 = 4096 = 2^12 → shift = 12. But 4-bit field max = 15, 12 fits. → 0x0C.
            // For 128KB: 131072/64=2048=2^11 → shift=11 → 0x0B
            hdr[11] = 0x0C;   // 256 KB CHR-RAM (volatile)
            return hdr;
        }
    }
}
