using System;
using System.Collections.Generic;
using System.Linq;
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
        public int  ConfigRecordBytes => 8;
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
            // Nothing else to do: CoolBoy has no fixed kernel in flash.
        }

        /// <summary>
        /// Translate a hardware-neutral GameBankingInfo into the 8-byte CoolBoy
        /// config record (outer bank regs + MMC3 inner banks + CHR/mirror hints).
        /// </summary>
        public byte[] BuildConfigRecord(NesRom game, GameBankingInfo info,
                                        BuildConfig cfg)
        {
            return CoolBoyBanking.BuildConfig(game, info.PrgFlashOffset,
                                              cfg.Submapper);
        }

        /// <summary>
        /// Write the game name table and config records into the flash.
        /// CoolBoy stores these at a fixed offset agreed with the menu ROM.
        /// The menu ROM must be compiled knowing these offsets.
        ///
        /// Layout (compatible with ClusterM's menu format):
        ///   Flash 0x000000+:  Menu NES ROM (user-supplied, not written here)
        ///   Flash 0x0007F00:  Game count (2 bytes, little-endian)
        ///   Flash 0x0007F02:  Config records (8 bytes × game count)
        ///   Flash 0x0007F02 + N*8:  ASCII names, null-terminated, packed
        /// </summary>
        public void WriteGameTable(byte[] flash,
                                   IReadOnlyList<GameEntry> entries,
                                   BuildConfig cfg)
        {
            const int TableBase = 0x7F00;  // within first 512KB menu window

            int count = entries.Count;
            flash[TableBase + 0] = (byte)(count & 0xFF);
            flash[TableBase + 1] = (byte)((count >> 8) & 0xFF);

            // Config records
            int cfgBase = TableBase + 2;
            for (int i = 0; i < count; i++)
                Array.Copy(entries[i].ConfigRecord, 0, flash,
                           cfgBase + i * ConfigRecordBytes, ConfigRecordBytes);

            // Game names (null-terminated ASCII)
            int nameBase = cfgBase + count * ConfigRecordBytes;
            int pos = nameBase;
            foreach (var e in entries)
            {
                byte[] nb = Encoding.ASCII.GetBytes(e.DisplayName);
                if (pos + nb.Length + 1 >= flash.Length) break;
                Array.Copy(nb, 0, flash, pos, nb.Length);
                flash[pos + nb.Length] = 0;
                pos += nb.Length + 1;
            }
        }

        /// <summary>
        /// Produce .nes and .unf output files.
        /// The UNIF board name is "COOLBOY" for submapper 0, "MINDKIDS" for submapper 1.
        /// </summary>
        public BuildResult BuildOutputFiles(byte[] flash, BuildConfig cfg)
        {
            string board = cfg.Submapper == 1 ? "MINDKIDS" : "COOLBOY";

            // NES 2.0 header for mapper 268
            byte[] hdr    = MakeNes2Header268(flash.Length / 16384, cfg.Submapper);
            byte[] nesFile = new byte[16 + flash.Length];
            Array.Copy(hdr,   nesFile, 16);
            Array.Copy(flash, 0, nesFile, 16, flash.Length);

            byte[] unifFile = cfg.GenerateNes
                ? NesFileWriter.MakeUnifRom(flash, board)
                : Array.Empty<byte>();

            if (!cfg.GenerateNes)
                nesFile = Array.Empty<byte>();

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
