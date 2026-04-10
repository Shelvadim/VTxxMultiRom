using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using VT03Builder.Models;
using VT03Builder.Services;

namespace VT03Builder.Tests
{
    /// <summary>
    /// Helpers shared across all test classes.
    /// </summary>
    internal static class TestHelper
    {
        public const int KernelSize   = 0x080000;   // 512 KB
        public const int NromStart    = 0x080000;
        public const int Mmc3Start    = 0x200000;
        public const int WindowSize   = 0x200000;
        public const int ConfigTable  = 0x07C000;   // 9 bytes × game index

        // ── Kernel ────────────────────────────────────────────────────────────

        /// <summary>
        /// Synthetic 512 KB kernel: first 4 bytes = magic, rest filled with 0xAB.
        /// Enough for RomBuilder to copy in and write the config/name tables over.
        /// </summary>
        public static byte[] FakeKernel()
        {
            var k = new byte[0x80000];
            Array.Fill(k, (byte)0xAB);
            k[0] = 0x4B; k[1] = 0x45; k[2] = 0x52; k[3] = 0x4E;  // "KERN"
            // Simulate the real kernel's RESET vector at 0x07E000 (CPU $E000).
            // The LCD stub test checks this byte is untouched after stub is written.
            k[0x07E000] = 0x78;   // SEI — first byte of real menu RESET handler
            // Leave the LCD stub region (0x06E000-0x06E0FF) as 0xFF to match
            // real unprogrammed flash — the stub tests check this region.
            for (int i = 0x06E000; i < 0x06E100 && i < k.Length; i++) k[i] = 0xFF;
            return k;
        }

        // ── Build helpers ─────────────────────────────────────────────────────

        /// <summary>Run Build() with a fake kernel; no disk access.</summary>
        public static BuildResult Build(int chipMb, params NesRom[] games)
        {
            var cfg = new BuildConfig { ChipSizeMb = chipMb };
            cfg.Games.AddRange(games);
            return RomBuilder.Build(cfg, FakeKernel());
        }

        // ── Config byte extractors ────────────────────────────────────────────

        public static byte[] GetConfig(byte[] bin, int gameIndex)
        {
            var c = new byte[9];
            Array.Copy(bin, ConfigTable + gameIndex * 9, c, 0, 9);
            return c;
        }

        // ── PRG bank formula (mirrors the hardware) ───────────────────────────

        /// <summary>
        /// Resolve a PRG 8 KB bank number to a physical address.
        ///   BankNum = (inner & InnerMask) | (middle & ~InnerMask) | (outer << 8)
        /// </summary>
        public static int PrgBankAddr(byte[] config, int inner)
        {
            int ps      = config[3] & 0x07;
            int[] imTab = { 0x3F, 0x1F, 0x0F, 0x07, 0x03, 0x01, 0x00, 0xFF };
            int im      = imTab[ps];
            int outer   = (config[0] >> 4) & 0x0F;
            int b7      = config[7];
            int mid     = b7 & ((~im) & 0xFF);
            int bankNum = ((inner & im) | mid | (outer << 8));
            return bankNum * 8192;
        }

        // ── CHR bank formula ──────────────────────────────────────────────────

        /// <summary>Resolve a 1 KB CHR bank number to a physical address.</summary>
        public static int ChrBankAddr(byte[] config, int inner)
        {
            int outer  = config[0] & 0x0F;
            int interm = (config[1] >> 4) & 0x07;
            int vb0s   = config[2] & 0x07;
            int[] imT  = { 0xFF, 0x7F, 0x3F, 0x00, 0x1F, 0x0F, 0x07, 0xFF };
            int im     = imT[vb0s];   // indexed by VB0S value
            int mid    = config[2] & ((~im) & 0xFF);
            int bankNum = ((inner & im) | mid | (interm << 8) | (outer << 11));
            return bankNum * 1024;
        }

        // ── Game data verification ────────────────────────────────────────────

        /// <summary>True if bin[offset..offset+len] matches src[0..len].</summary>
        public static bool DataAt(byte[] bin, int offset, byte[] src, int len)
        {
            for (int i = 0; i < len; i++)
                if (bin[offset + i] != src[i]) return false;
            return true;
        }

        /// <summary>Returns the first offset in bin where src appears, or -1.</summary>
        public static int FindData(byte[] bin, byte[] src)
        {
            for (int i = 0; i <= bin.Length - src.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < src.Length; j++)
                    if (bin[i + j] != src[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }

        // ── Fake NesRom factory ───────────────────────────────────────────────

        public static NesRom Nrom(int prgKb = 32, int chrKb = 8,  bool vertical = false)
            => NesRom.CreateForTest(0, prgKb, chrKb, vertical, $"nrom_{prgKb}_{chrKb}.nes");

        public static NesRom Cnrom(int prgKb = 32, int chrKb = 32, bool vertical = false)
            => NesRom.CreateForTest(3, prgKb, chrKb, vertical, $"cnrom_{prgKb}_{chrKb}.nes");

        public static NesRom Mmc3(int prgKb, int chrKb, bool vertical = false)
            => NesRom.CreateForTest(4, prgKb, chrKb, vertical, $"mmc3_{prgKb}_{chrKb}.nes");
    }
}
