using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using VT03Builder.Models;
using VT03Builder.Services;

namespace VT03Builder.Tests
{
    /// <summary>    
    /// The 7 contiguous reference games (Batman through Felix) are packed
    /// sequentially at 0x200000 with no gaps and can be verified exactly.
    /// Games 7–9 (Little Mermaid, Ninja Gaiden, SMB2) sit at different absolute
    /// offsets in the reference due to Flintstones/Spiderman filling the gap, so
    /// we test their formula invariants rather than exact hex.
    /// </summary>
    public class ReferenceGameTests
    {
        private static readonly (int prg, int chr, string hex)[] Contiguous7 =
        {
            (128, 128, "1100810200010e0f00"),
            (128, 128, "1110810220212e2f00"),
            (128, 128, "1120810240414e4f00"),
            (128, 128, "1130810260616e6f00"),
            (128, 128, "1140810280818e8f00"),
            (128, 128, "11508102a0a1aeaf00"),
            (128, 128, "11608102c0c1cecf00"),
        };

        private static readonly string[] Names7 =
            { "Batman", "Captain America", "Chip N Dale 2", "Chip N Dale",
              "Darkwing Duck", "Duck Tales", "Felix the Cat" };

        [Fact]
        public void Contiguous7_PackedSequentially_AllMatch()
        {
            var games  = Contiguous7.Select(g => TestHelper.Mmc3(g.prg, g.chr)).ToArray();
            var result = TestHelper.Build(16, games);
            Assert.Equal(7, result.GameCount);
            for (int i = 0; i < Contiguous7.Length; i++)
            {
                var cfg = TestHelper.GetConfig(result.NorBinary, i);
                Assert.Equal(Contiguous7[i].hex,
                             Convert.ToHexString(cfg).ToLower());
            }
        }

        [Theory]
        [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
        [InlineData(4)][InlineData(5)][InlineData(6)]
        public void Contiguous7_Individual_MatchesRef(int i)
        {
            var games  = Enumerable.Range(0, i + 1)
                                   .Select(_ => TestHelper.Mmc3(128, 128))
                                   .ToArray();
            var result = TestHelper.Build(16, games);
            var cfg    = TestHelper.GetConfig(result.NorBinary, i);
            Assert.Equal(Contiguous7[i].hex,
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
