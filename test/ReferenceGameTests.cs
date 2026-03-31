using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using VT03Builder.Models;
using VT03Builder.Services;

namespace VT03Builder.Tests
{
    /// <summary>
    /// Verifies byte-for-byte match for MMC3 game configs packed in the
    /// first 2MB window (PA=0, NintendulatorNRS compatible).
    ///
    /// MMC3 games are placed sequentially starting at 0x080000 (right after
    /// the kernel). Six 128KB+128KB games fill exactly 0x080000–0x1FFFFF.
    /// PA=0 ensures NintendulatorNRS (UNL-OneBus) reads the correct banks
    /// without needing to handle the outer bank (PA) field in $4100.
    /// </summary>
    public class ReferenceGameTests
    {
        private static readonly (int prg, int chr, string hex)[] Contiguous6 =
        {
            (128, 128, "0020810240414e4f00"),   // NOR 0x080000
            (128, 128, "0030810260616e6f00"),   // NOR 0x0C0000
            (128, 128, "0040810280818e8f00"),   // NOR 0x100000
            (128, 128, "00508102a0a1aeaf00"),   // NOR 0x140000
            (128, 128, "00608102c0c1cecf00"),   // NOR 0x180000
            (128, 128, "00708102e0e1eeef00"),   // NOR 0x1C0000
        };

        private static readonly string[] Names6 =
            { "Batman", "Captain America", "Chip N Dale 2", "Chip N Dale",
              "Darkwing Duck", "Duck Tales" };

        [Fact]
        public void Contiguous6_PackedSequentially_AllMatch()
        {
            var games  = Contiguous6.Select(g => TestHelper.Mmc3(g.prg, g.chr)).ToArray();
            var result = TestHelper.Build(16, games);
            Assert.Equal(6, result.GameCount);
            for (int i = 0; i < Contiguous6.Length; i++)
            {
                var cfg = TestHelper.GetConfig(result.NorBinary, i);
                Assert.Equal(Contiguous6[i].hex,
                             Convert.ToHexString(cfg).ToLower());
            }
        }

        [Theory]
        [InlineData(0)][InlineData(1)][InlineData(2)]
        [InlineData(3)][InlineData(4)][InlineData(5)]
        public void Contiguous6_Individual_MatchesRef(int i)
        {
            var games  = Enumerable.Range(0, i + 1)
                                   .Select(_ => TestHelper.Mmc3(128, 128))
                                   .ToArray();
            var result = TestHelper.Build(16, games);
            var cfg    = TestHelper.GetConfig(result.NorBinary, i);
            Assert.Equal(Contiguous6[i].hex,
                         Convert.ToHexString(cfg).ToLower());
        }

        [Theory]
        [InlineData(128, 128)]
        public void OtherRefGames_FormulaCorrect(int prgKb, int chrKb)
        {
            var game   = TestHelper.Mmc3(prgKb, chrKb);
            var result = TestHelper.Build(16, game);
            var cfg    = TestHelper.GetConfig(result.NorBinary, 0);
            int outer  = (cfg[0] >> 4) & 0x0F;
            int ro     = outer * 0x200000 + cfg[4] * 8192;
            int effPrg = RomBuilder.EffectivePrgForChr(prgKb * 1024, chrKb * 1024);
            Assert.Equal(ro + prgKb * 1024 - 8192,  TestHelper.PrgBankAddr(cfg, 0xFF));
            Assert.Equal(ro + effPrg,                TestHelper.ChrBankAddr(cfg, 0x00));
        }
    }
}
