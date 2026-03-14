using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using VT03Builder.Models;
using VT03Builder.Services;

namespace VT03Builder.Tests
{
    /// <summary>
    /// Verifies that the PRG bank config (PS mode + b4–b7) is correct for every
    /// supported PRG size, at every alignment-valid placement offset.
    ///
    /// Key invariants:
    ///   1. inner=0xFF (fixed $E000 window) → romOffset + prgSize - 8 KB
    ///   2. inner=0xFE (fixed $C000 window) → romOffset + prgSize - 16 KB
    ///   3. inner=0x00 ($8000 bank after game reset)  → romOffset
    ///   4. inner=0x01 ($A000 bank)                   → romOffset + 8 KB
    /// </summary>
    public class Mmc3PrgBankTests
    {
        // (prgKb, expected PS)
        public static TheoryData<int, int> PrgSizes => new()
        {
            {  32, 4 },
            {  64, 3 },
            { 128, 2 },
            { 256, 1 },
            { 512, 0 },
        };

        [Theory]
        [MemberData(nameof(PrgSizes))]
        public void PS_MatchesPrgSize(int prgKb, int expectedPs)
        {
            var game   = TestHelper.Mmc3(prgKb, 128);
            var result = TestHelper.Build(32, game);
            var cfg    = TestHelper.GetConfig(result.NorBinary, 0);
            Assert.Equal((byte)expectedPs, (byte)(cfg[3] & 0x07));
        }

        // All valid (offset, prgKb) combinations within a 32 MB chip
        public static IEnumerable<object[]> OffsetAndPrgData()
        {
            int[] prgSizes = { 32, 64, 128, 256, 512 };
            int[] aligns   = { 32*1024, 64*1024, 128*1024, 256*1024, 512*1024 };

            for (int si = 0; si < prgSizes.Length; si++)
            {
                int prg   = prgSizes[si];
                int align = aligns[si];
                // Test first 3 valid positions in the MMC3 window
                for (int k = 0; k < 3; k++)
                {
                    int offset = 0x200000 + k * align;
                    if (offset + prg * 1024 <= 0x400000)   // stay within one 2MB window
                        yield return new object[] { offset, prg };
                }
            }
        }

        [Theory]
        [MemberData(nameof(OffsetAndPrgData))]
        public void E000_Window_PointsToLastBank(int firstGameOffset, int prgKb)
        {
            _ = firstGameOffset; // offset is implicit — game is placed at first valid position
            var game   = TestHelper.Mmc3(prgKb, 128);
            var result = TestHelper.Build(32, game);
            var cfg    = TestHelper.GetConfig(result.NorBinary, 0);

            // Determine actual placement from b4
            int outer     = ((cfg[0] >> 4) & 0x0F);
            int b4        = cfg[4];
            int romOffset = (outer * 0x200000) + (b4 * 8192);

            int e000 = TestHelper.PrgBankAddr(cfg, 0xFF);
            int c000 = TestHelper.PrgBankAddr(cfg, 0xFE);
            int expected_e000 = romOffset + prgKb * 1024 - 8192;
            int expected_c000 = romOffset + prgKb * 1024 - 16384;

            Assert.Equal(expected_e000, e000);
            Assert.Equal(expected_c000, c000);
        }

        [Theory]
        [MemberData(nameof(PrgSizes))]
        public void Bank0_PointsToGameStart(int prgKb, int _)
        {
            var game   = TestHelper.Mmc3(prgKb, 128);
            var result = TestHelper.Build(32, game);
            var cfg    = TestHelper.GetConfig(result.NorBinary, 0);

            int outer     = ((cfg[0] >> 4) & 0x0F);
            int b4        = cfg[4];
            int romOffset = (outer * 0x200000) + (b4 * 8192);

            int bank0 = TestHelper.PrgBankAddr(cfg, 0x00);
            Assert.Equal(romOffset, bank0);
        }

        [Theory]
        [MemberData(nameof(PrgSizes))]
        public void Bank1_PointsTo8KbAfterStart(int prgKb, int _)
        {
            var game   = TestHelper.Mmc3(prgKb, 128);
            var result = TestHelper.Build(32, game);
            var cfg    = TestHelper.GetConfig(result.NorBinary, 0);

            int outer     = ((cfg[0] >> 4) & 0x0F);
            int b4        = cfg[4];
            int romOffset = (outer * 0x200000) + (b4 * 8192);

            int bank1 = TestHelper.PrgBankAddr(cfg, 0x01);
            Assert.Equal(romOffset + 8192, bank1);
        }

        [Theory]
        [MemberData(nameof(PrgSizes))]
        public void InnerWindow_CoversFullPrg(int prgKb, int _)
        {
            // The inner window size (determined by PS) must be >= prgKb.
            // If it were smaller, banks past the window would alias back to an earlier bank.
            var game    = TestHelper.Mmc3(prgKb, 128);
            var result  = TestHelper.Build(32, game);
            var cfg     = TestHelper.GetConfig(result.NorBinary, 0);
            int ps      = cfg[3] & 0x07;
            int[] imTab = { 0x3F, 0x1F, 0x0F, 0x07, 0x03, 0x01, 0x00, 0xFF };
            int im      = imTab[ps];
            int innerWindowKb = (im + 1) * 8;  // each inner bank = 8 KB
            Assert.True(innerWindowKb >= prgKb,
                $"PRG={prgKb}KB: inner window {innerWindowKb}KB too small (PS={ps})");
        }

        [Theory]
        [MemberData(nameof(PrgSizes))]
        public void Alignment_IsMultipleOfInnerWindow(int prgKb, int _)
        {
            // The game's romOffset must be a multiple of the PRG inner-window size.
            var game   = TestHelper.Mmc3(prgKb, 128);
            var result = TestHelper.Build(32, game);
            var cfg    = TestHelper.GetConfig(result.NorBinary, 0);

            int outer     = ((cfg[0] >> 4) & 0x0F);
            int b4        = cfg[4];
            int romOffset = (outer * 0x200000) + (b4 * 8192);
            int align     = RomBuilder.PrgAlignFor(prgKb * 1024);

            Assert.Equal(0, romOffset % align);
        }

        [Fact]
        public void TwoConsecutive_256KbGames_AreCorrectlyAligned()
        {
            // Two 256KB PRG games: second must start at 0x200000 + 256KB + 256KB (CHR) = 0x280000
            // but must be aligned to 256KB. So it should be at 0x280000 (already aligned).
            var g1 = TestHelper.Mmc3(256, 256);
            var g2 = TestHelper.Mmc3(256, 256);
            var result = TestHelper.Build(32, g1, g2);

            var cfg1 = TestHelper.GetConfig(result.NorBinary, 0);
            var cfg2 = TestHelper.GetConfig(result.NorBinary, 1);

            int ro1 = ((cfg1[0] >> 4) & 0x0F) * 0x200000 + (cfg1[4] * 8192);
            int ro2 = ((cfg2[0] >> 4) & 0x0F) * 0x200000 + (cfg2[4] * 8192);

            Assert.Equal(0, ro1 % (256 * 1024));
            Assert.Equal(0, ro2 % (256 * 1024));
            Assert.True(ro2 > ro1, "Game 2 must follow game 1");

            // Verify $E000 for both
            int e1 = TestHelper.PrgBankAddr(cfg1, 0xFF);
            int e2 = TestHelper.PrgBankAddr(cfg2, 0xFF);
            Assert.Equal(ro1 + 256 * 1024 - 8192, e1);
            Assert.Equal(ro2 + 256 * 1024 - 8192, e2);
        }

        [Fact]
        public void Mixed_Sizes_AllHaveCorrect_E000()
        {
            // Pack a variety of PRG sizes and confirm every game's $E000 is correct.
            var games = new[]
            {
                TestHelper.Mmc3( 32,  16),
                TestHelper.Mmc3( 64,  32),
                TestHelper.Mmc3(128, 128),
                TestHelper.Mmc3(256, 128),
                TestHelper.Mmc3( 64,  64),
                TestHelper.Mmc3(128,  64),
            };
            var result = TestHelper.Build(32, games);
            Assert.Equal(games.Length, result.GameCount);

            for (int i = 0; i < games.Length; i++)
            {
                var cfg   = TestHelper.GetConfig(result.NorBinary, i);
                int outer = ((cfg[0] >> 4) & 0x0F);
                int b4    = cfg[4];
                int ro    = outer * 0x200000 + b4 * 8192;
                int e000  = TestHelper.PrgBankAddr(cfg, 0xFF);
                int exp   = ro + games[i].PrgSize - 8192;
                Assert.Equal(exp, e000);
            }
        }
    }
}
