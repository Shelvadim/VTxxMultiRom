using System;
using System.Linq;
using Xunit;
using VT03Builder.Models;
using VT03Builder.Services;
using VT03Builder.Services.SourceMappers;
using VT03Builder.Services.Targets;

namespace VT03Builder.Tests
{
    /// <summary>
    /// Tests for CoolBoyTarget (mapper 268) and CoolBoyBanking.
    ///
    /// Covers:
    ///   1. Target registration and identity
    ///   2. Hardware limits and capabilities
    ///   3. Config record format for NROM and MMC3 games
    ///   4. Flash placement (no kernel area, sequential packing)
    ///   5. Outer bank register values at key offsets
    ///   6. Submapper 0 (CoolBoy $6000) vs submapper 1 (Mindkids $5000)
    ///   7. CHR-RAM always required
    ///   8. Output file format (UNIF board name)
    /// </summary>
    public class CoolBoyTests
    {
        // ── Helpers ───────────────────────────────────────────────────────────

        private static CoolBoyTarget Target => new CoolBoyTarget();

        private static BuildResult Build(int chipMb, int submapper = 0,
                                         params NesRom[] games)
        {
            var cfg = new BuildConfig
            {
                TargetId   = "coolboy",
                ChipSizeMb = chipMb,
                Submapper  = submapper,
                AllowChrRam = true,   // CoolBoy always needs CHR-RAM
                GenerateNes = true,
            };
            cfg.Games.AddRange(games);
            // Use real public Build() so it goes through the registry
            return RomBuilder.Build(cfg);
        }

        private static byte[] GetConfig(byte[] flash, int gameIndex)
        {
            const int TableBase = 0x7F00;
            const int CfgBase   = TableBase + 2;
            const int RecSize   = 8;
            var rec = new byte[RecSize];
            Array.Copy(flash, CfgBase + gameIndex * RecSize, rec, 0, RecSize);
            return rec;
        }

        // ── 1. Registration ───────────────────────────────────────────────────

        [Fact]
        public void CoolBoy_IsRegistered()
        {
            var t = TargetRegistry.Get("coolboy");
            Assert.NotNull(t);
        }

        [Fact]
        public void CoolBoy_Identity()
        {
            var t = Target;
            Assert.Equal("coolboy",                    t.Id);
            Assert.Equal(268,                           t.OutputMapper);
            Assert.Contains("CoolBoy",                 t.DisplayName);
            Assert.Contains("268",                     t.DisplayName);
        }

        // ── 2. Hardware limits ────────────────────────────────────────────────

        [Fact]
        public void CoolBoy_MaxFlash_32MB()
        {
            Assert.Equal(32L * 1024 * 1024, Target.MaxFlashBytes);
        }

        [Fact]
        public void CoolBoy_ConfigRecord_8Bytes()
        {
            Assert.Equal(8, Target.ConfigRecordBytes);
        }

        [Fact]
        public void CoolBoy_AlwaysChrRam()
        {
            Assert.True(Target.AlwaysChrRam);
        }

        [Fact]
        public void CoolBoy_NoKernelArea()
        {
            // CoolBoy has no embedded kernel — HasKernelArea = false.
            // KernelAreaSize = 0x080000 (512 KB menu window reserved for user-supplied menu ROM).
            Assert.False(Target.HasKernelArea);
            Assert.Equal(CoolBoyTarget.MenuWindowSize, Target.KernelAreaSize);
        }

        [Fact]
        public void CoolBoy_SupportsNromAndMmc3()
        {
            var mappers = Target.SupportedSourceMappers;
            Assert.Contains(0, mappers);   // NROM
            Assert.Contains(4, mappers);   // MMC3
        }

        [Fact]
        public void CoolBoy_HasSubmapper0And1()
        {
            var nums = Target.Submappers.Select(s => s.Number).ToList();
            Assert.Contains(0, nums);   // CoolBoy ($6000)
            Assert.Contains(1, nums);   // Mindkids ($5000)
        }

        // ── 3. Config record — outer bank register values ─────────────────────

        [Fact]
        public void CoolBoy_Config_NormFirstGame_OuterBankZero()
        {
            // A game at flash offset 0x080000 (first game slot, outer window 1)
            // Outer PRG = 0x080000 / 512KB = 1
            // Reg0 bits 7-3 = 1 (outer A20-A16) → 0b00001000 = 0x08
            var result = Build(8, 0, TestHelper.Nrom(32, 8));
            var cfg    = GetConfig(result.NorBinary, 0);
            // For an NROM game the outer bank selects the 512KB window.
            // Game goes at 0x080000, outer = 1 → reg0 = 0x08
            Assert.Equal(8, cfg.Length);
            // Reg0 bits 7-3 should encode outer bank 1 → 0x08
            Assert.Equal(0x08, cfg[0] & 0xF8);
        }

        [Fact]
        public void CoolBoy_Config_Reg3_IsZero_BeforeLockout()
        {
            // Config record reg3 is stored as 0x00 — menu writes 0x80 at runtime
            var result = Build(8, 0, TestHelper.Nrom(32, 8));
            var cfg    = GetConfig(result.NorBinary, 0);
            Assert.Equal(0x00, cfg[3]);
        }

        [Fact]
        public void CoolBoy_Config_Mirror_Horizontal()
        {
            var result = Build(8, 0, TestHelper.Nrom(32, 8, vertical: false));
            var cfg    = GetConfig(result.NorBinary, 0);
            Assert.Equal(0x01, cfg[7]);   // horizontal = 1
        }

        [Fact]
        public void CoolBoy_Config_Mirror_Vertical()
        {
            var result = Build(8, 0, TestHelper.Nrom(32, 8, vertical: true));
            var cfg    = GetConfig(result.NorBinary, 0);
            Assert.Equal(0x00, cfg[7]);   // vertical = 0
        }

        [Fact]
        public void CoolBoy_Config_ChrHint_256KB_IsZero()
        {
            // CHR-RAM 256KB hint = 0x00
            var mmc3 = TestHelper.Mmc3(256, 256);   // large game
            var result = Build(32, 0, mmc3);
            var cfg    = GetConfig(result.NorBinary, 0);
            Assert.Equal(0x00, cfg[6]);   // 256KB CHR-RAM hint
        }

        [Fact]
        public void CoolBoy_Config_ChrHint_128KB_IsOne()
        {
            // CHR-RAM 128KB hint = 0x01
            var mmc3 = TestHelper.Mmc3(64, 64);   // small game
            var result = Build(8, 0, mmc3);
            var cfg    = GetConfig(result.NorBinary, 0);
            Assert.Equal(0x01, cfg[6]);   // 128KB CHR-RAM hint
        }

        // ── 4. Flash placement ────────────────────────────────────────────────

        [Fact]
        public void CoolBoy_GameData_NotAtFlashStart()
        {
            // First 512KB is the menu window — game data starts at 0x080000.
            // PRG bank 0 fills with byte value 1. Offset 0 should still be 0xFF.
            var result = Build(8, 0, TestHelper.Nrom(32, 8));
            var bin    = result.NorBinary;
            Assert.Equal(0xFF, bin[0]);                           // menu window = unprogrammed
            Assert.Equal(0x01, bin[0x080000]);                    // game data starts here
        }

        [Fact]
        public void CoolBoy_TwoGames_PlacedSequentially()
        {
            // Two NROM games fit in the same 512KB outer window, so reg0 (outer bank)
            // is the same for both. What differs is cfg[4] (inner PRG bank 0), which
            // encodes the game's offset within the outer window.
            // Game 1: offset 0x080000 → inner bank = (0x080000 % 0x80000)/8192 = 0
            // Game 2: offset 0x090000 → inner bank = (0x010000)/8192 = 8
            var result = Build(16, 0,
                TestHelper.Nrom(32, 8),
                TestHelper.Nrom(32, 8));
            var cfg0 = GetConfig(result.NorBinary, 0);
            var cfg1 = GetConfig(result.NorBinary, 1);
            Assert.NotEqual(cfg0[4], cfg1[4]);   // inner PRG bank differs
        }

        [Fact]
        public void CoolBoy_GameCount_WrittenToFlash()
        {
            const int TableBase = 0x7F00;
            var result = Build(8, 0,
                TestHelper.Nrom(32, 8),
                TestHelper.Nrom(32, 8));
            var bin   = result.NorBinary;
            int count = bin[TableBase] | (bin[TableBase + 1] << 8);
            Assert.Equal(2, count);
        }

        // ── 5. Output format ──────────────────────────────────────────────────

        [Fact]
        public void CoolBoy_NesFile_HasNes20Header()
        {
            var result = Build(8, 0, TestHelper.Nrom(32, 8));
            var nes    = result.NesFile;
            Assert.True(nes.Length >= 16);
            Assert.Equal((byte)'N', nes[0]);
            Assert.Equal((byte)'E', nes[1]);
            Assert.Equal((byte)'S', nes[2]);
            Assert.Equal(0x1A,       nes[3]);
            // NES 2.0 identifier: byte[7] bits 3-2 = 10b
            Assert.Equal(2, (nes[7] >> 2) & 0x03);
        }

        [Fact]
        public void CoolBoy_NesFile_Mapper268()
        {
            var result = Build(8, 0, TestHelper.Nrom(32, 8));
            var nes    = result.NesFile;
            int mapper = ((nes[6] >> 4) & 0x0F)
                       | (nes[7] & 0xF0)
                       | ((nes[8] & 0x0F) << 8);
            Assert.Equal(268, mapper);
        }

        [Fact]
        public void CoolBoy_UnifFile_BoardIsCOOLBOY_Submapper0()
        {
            var result = Build(8, 0, TestHelper.Nrom(32, 8));
            var unif   = System.Text.Encoding.ASCII.GetString(result.UnifFile);
            Assert.Contains("COOLBOY", unif);
        }

        [Fact]
        public void CoolBoy_UnifFile_BoardIsMINDKIDS_Submapper1()
        {
            var result = Build(8, 1, TestHelper.Nrom(32, 8));
            var unif   = System.Text.Encoding.ASCII.GetString(result.UnifFile);
            Assert.Contains("MINDKIDS", unif);
        }

        // ── 6. Compatibility ──────────────────────────────────────────────────

        [Fact]
        public void CoolBoy_ChrRam_Game_IsCompatible()
        {
            var mmc3    = TestHelper.Mmc3(64, 0);   // CHR-RAM game (ChrSize=0)
            var handler = SourceMapperRegistry.Get(4)!;
            var warn    = handler.CompatibilityWarning(mmc3, Target);
            // On CoolBoy (AlwaysChrRam=true) CHR-RAM games ARE compatible
            Assert.Null(warn);
        }

        [Fact]
        public void CoolBoy_SpaceCalculator_NoKernelOverhead()
        {
            // CoolBoy reserves 512 KB for the menu window (0x000000–0x07FFFF).
            // Usable for games = chip size - 512 KB.
            const long MenuWindow = 512L * 1024;
            var cfg = new BuildConfig
            {
                TargetId   = "coolboy",
                ChipSizeMb = 8,
            };
            var info = SpaceCalculator.Calculate(cfg);
            Assert.Equal(8L * 1024 * 1024 - MenuWindow, info.UsableBytes);
        }
    }
}
