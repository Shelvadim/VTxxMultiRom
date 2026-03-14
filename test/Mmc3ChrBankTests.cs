using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using VT03Builder.Models;
using VT03Builder.Services;

namespace VT03Builder.Tests
{
    /// <summary>
    /// Verifies that CHR bank 0 maps to the start of CHR data, and that the
    /// last accessible bank maps to the end, for every supported CHR size.
    ///
    /// For CHR >= 256 KB: PRG is padded to a 256 KB boundary so that
    /// VB0S=0 (InnerMask=0xFF) can encode the full 256 KB window without a Middle offset.
    /// </summary>
    public class Mmc3ChrBankTests
    {
        // (prgKb, chrKb) combinations to test
        public static IEnumerable<object[]> GameSizes()
        {
            int[] prgs = { 32, 64, 128, 256 };
            int[] chrs = { 8, 16, 32, 64, 128, 256 };
            foreach (var prg in prgs)
                foreach (var chr in chrs)
                    yield return new object[] { prg, chr };
        }

        [Theory]
        [MemberData(nameof(GameSizes))]
        public void ChrBank0_PointsToStartOfChrData(int prgKb, int chrKb)
        {
            var game   = TestHelper.Mmc3(prgKb, chrKb);
            var result = TestHelper.Build(32, game);
            var cfg    = TestHelper.GetConfig(result.NorBinary, 0);

            // Effective PRG (may be padded for large CHR)
            int effPrg   = RomBuilder.EffectivePrgForChr(prgKb * 1024, chrKb * 1024);
            int outer    = ((cfg[0] >> 4) & 0x0F);
            int b4       = cfg[4];
            int romOffset = outer * 0x200000 + b4 * 8192;
            int chrStart  = romOffset + effPrg;

            int bank0 = TestHelper.ChrBankAddr(cfg, 0x00);
            Assert.Equal(chrStart, bank0);
        }

        [Theory]
        [MemberData(nameof(GameSizes))]
        public void ChrLastBank_PointsToEndOfChrData(int prgKb, int chrKb)
        {
            var game     = TestHelper.Mmc3(prgKb, chrKb);
            var result   = TestHelper.Build(32, game);
            var cfg      = TestHelper.GetConfig(result.NorBinary, 0);

            int effPrg    = RomBuilder.EffectivePrgForChr(prgKb * 1024, chrKb * 1024);
            int outer     = ((cfg[0] >> 4) & 0x0F);
            int b4        = cfg[4];
            int romOffset = outer * 0x200000 + b4 * 8192;
            int chrStart  = romOffset + effPrg;

            // The inner window size (in 1KB units) limits how many CHR banks are accessible.
            int vb0s     = cfg[2] & 0x07;
            int[] imTab  = { 0xFF, 0x7F, 0x3F, 0x00, 0x1F, 0x0F, 0x07, 0xFF };
            int im       = imTab[vb0s];
            int innerKb  = im + 1;   // 1KB banks reachable via inner
            int accessKb = Math.Min(chrKb, innerKb);

            int lastBank = TestHelper.ChrBankAddr(cfg, accessKb - 1);
            int expected = chrStart + (accessKb - 1) * 1024;
            Assert.Equal(expected, lastBank);
        }

        [Theory]
        [MemberData(nameof(GameSizes))]
        public void Vb0s_IsCorrectForChrSize(int prgKb, int chrKb)
        {
            var game   = TestHelper.Mmc3(prgKb, chrKb);
            var result = TestHelper.Build(32, game);
            var cfg    = TestHelper.GetConfig(result.NorBinary, 0);

            int vb0s = cfg[2] & 0x07;
            int[] imTab  = { 0xFF, 0x7F, 0x3F, 0x00, 0x1F, 0x0F, 0x07, 0xFF };
            int im      = imTab[vb0s];
            int innerKb = im + 1;

            Assert.True(innerKb >= Math.Min(chrKb, 256),
                $"PRG={prgKb}KB CHR={chrKb}KB: inner window {innerKb}KB < accessible CHR {Math.Min(chrKb,256)}KB (VB0S={vb0s})");
        }

        [Theory]
        [InlineData(128, 256)]
        [InlineData(256, 256)]
        [InlineData( 64, 256)]
        public void LargeChr_256KB_FullyAccessible(int prgKb, int chrKb)
        {
            // For 256 KB CHR, PRG is padded to 256 KB alignment, VB0S=0, inner=256KB.
            // Verify all 256 banks (0–255) map contiguously from chrStart.
            var game   = TestHelper.Mmc3(prgKb, chrKb);
            var result = TestHelper.Build(32, game);
            var cfg    = TestHelper.GetConfig(result.NorBinary, 0);

            Assert.Equal(0, cfg[2] & 0x07);

            int effPrg    = RomBuilder.EffectivePrgForChr(prgKb * 1024, chrKb * 1024);
            int outer     = ((cfg[0] >> 4) & 0x0F);
            int b4        = cfg[4];
            int romOffset = outer * 0x200000 + b4 * 8192;
            int chrStart  = romOffset + effPrg;

            for (int bank = 0; bank < 256; bank++)
            {
                int addr = TestHelper.ChrBankAddr(cfg, bank);
                int exp  = chrStart + bank * 1024;
                Assert.Equal(exp, addr);
            }
        }

        [Theory]
        [InlineData( 32,  64,  64)]   // PRG < CHR inner window → must pad
        [InlineData( 32, 128, 128)]
        [InlineData( 64, 128, 128)]
        [InlineData(128, 256, 256)]
        [InlineData(256, 256, 256)]
        [InlineData( 64,  64,   0)]   // PRG == inner window → no pad needed
        [InlineData(128, 128,   0)]   // PRG == inner window → no pad needed
        public void EffectivePrg_IsAlignedToChrInnerWindow(int prgKb, int chrKb, int expectedPadKb)
        {
            int effPrg   = RomBuilder.EffectivePrgForChr(prgKb * 1024, chrKb * 1024);
            int padKb    = (effPrg - prgKb * 1024) / 1024;
            Assert.Equal(expectedPadKb, padKb);

            // Result must be a multiple of the CHR inner window
            // (the window size equals the inner-mask+1 in 1KB units × 1024)
            int vb0s      = chrKb <= 8 ? 6 : chrKb <= 16 ? 5 : chrKb <= 32 ? 4 :
                            chrKb <= 64 ? 2 : chrKb <= 128 ? 1 : 0;
            int[] imTab   = { 0xFF, 0x7F, 0x3F, 0x00, 0x1F, 0x0F, 0x07, 0xFF };
            int innerKb   = imTab[vb0s] + 1;
            Assert.Equal(0, effPrg % (innerKb * 1024));
        }

        [Fact]
        public void LargeFlag_SetFor_ChrOver128KB()
        {
            foreach (var (prg, chr, expectFlag) in new[] {
                (128,  64, false),
                (128, 128, false),
                (128, 256, true),
                (256, 256, true),
            })
            {
                var game   = TestHelper.Mmc3(prg, chr);
                var result = TestHelper.Build(32, game);
                var cfg    = TestHelper.GetConfig(result.NorBinary, 0);
                bool flag  = (cfg[8] & 0x80) != 0;
                Assert.Equal(expectFlag, flag);
            }
        }
    }
}
