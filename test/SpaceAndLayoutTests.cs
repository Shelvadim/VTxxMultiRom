using System;
using System.Linq;
using Xunit;
using VT03Builder.Models;
using VT03Builder.Services;

namespace VT03Builder.Tests
{
    /// <summary>
    /// Tests for:
    ///   1. Flash-used bar (SpaceCalculator) changes correctly with chip size
    ///   2. MMC3 games with 256KB, 64KB PRG produce correct bank configs
    ///   3. MMC3 games fill beyond first 2MB window (PA=1+) on large chips
    ///   4. Games are not skipped when chip has enough space
    /// </summary>
    public class SpaceAndLayoutTests
    {
        // ── 1. Flash bar / SpaceCalculator changes with chip size ─────────────

        [Theory]
        [InlineData( 2)]
        [InlineData( 4)]
        [InlineData( 8)]
        [InlineData(16)]
        [InlineData(32)]
        public void SpaceCalculator_UsableBytes_ScalesWithChipSize(int chipMb)
        {
            var cfg = new BuildConfig { ChipSizeMb = chipMb };
            // No games — check usable space only
            var info = SpaceCalculator.Calculate(cfg);

            // Usable = 480KB kernel gaps + (chipMb*1MB - 512KB) after kernel
            long expected = (0x040000 - 0x000000) + (0x079000 - 0x041000)   // 480 KB gaps
                          + ((long)chipMb * 1024 * 1024 - 0x080000);         // after kernel
            Assert.Equal(expected, info.UsableBytes);
        }

        [Fact]
        public void SpaceCalculator_UsableBytes_DiffersAcrossChipSizes()
        {
            long prev = 0;
            foreach (int mb in new[] { 2, 4, 8, 16, 32 })
            {
                var cfg  = new BuildConfig { ChipSizeMb = mb };
                long cur = SpaceCalculator.Calculate(cfg).UsableBytes;
                Assert.True(cur > prev,
                    $"Chip {mb}MB usable ({cur}) should be greater than smaller chip ({prev})");
                prev = cur;
            }
        }

        [Fact]
        public void SpaceCalculator_UsedBytes_ReflectAddedGames()
        {
            var cfg = new BuildConfig { ChipSizeMb = 8 };
            var info0 = SpaceCalculator.Calculate(cfg);

            cfg.Games.Add(TestHelper.Mmc3(128, 128));
            var info1 = SpaceCalculator.Calculate(cfg);

            Assert.True(info1.UsedBytes > info0.UsedBytes,
                "Adding a game must increase UsedBytes");
        }

        [Fact]
        public void SpaceCalculator_Percent_ChangesWithChipSize()
        {
            var cfgSmall = new BuildConfig { ChipSizeMb = 2 };
            var cfgLarge = new BuildConfig { ChipSizeMb = 8 };
            cfgSmall.Games.Add(TestHelper.Mmc3(128, 128));
            cfgLarge.Games.Add(TestHelper.Mmc3(128, 128));

            double pctSmall = SpaceCalculator.Calculate(cfgSmall).Percent;
            double pctLarge = SpaceCalculator.Calculate(cfgLarge).Percent;

            Assert.True(pctSmall > pctLarge,
                $"Same game should occupy higher % on smaller chip ({pctSmall:F1}% vs {pctLarge:F1}%)");
        }

        // ── 2. MMC3 config for 64KB PRG games ────────────────────────────────

        [Theory]
        [InlineData(64,   8)]
        [InlineData(64,  16)]
        [InlineData(64,  32)]
        [InlineData(64,  64)]
        [InlineData(64, 128)]
        public void Mmc3_64KB_Prg_E000Window_CorrectlyMapped(int prgKb, int chrKb)
        {
            var game   = TestHelper.Mmc3(prgKb, chrKb);
            var result = TestHelper.Build(8, game);
            Assert.Equal(1, result.GameCount);

            var cfg    = TestHelper.GetConfig(result.NorBinary, 0);
            int ps     = cfg[3];
            Assert.Equal(3, ps); // PS=3 → InnerMask=0x07 for 64KB PRG

            // Verify $E000 fixed window formula: BankNum = (0xFF & im) | (b7 & ~im)
            int b7        = cfg[7];
            int innerMask = 0x07;
            int bankNum   = (0xFF & innerMask) | (b7 & (~innerMask & 0xFF));
            int norAddr   = bankNum * 8192;

            // $E000 should map to last 8KB of PRG
            int outer    = (cfg[0] >> 4) & 0x0F;
            int baseAddr = outer * 0x200000 + (b7 - (b7 & innerMask)) * 8192;
            int lastPrg  = baseAddr + (b7 & innerMask) * 8192; // reconstruct
            // Simpler: derive from b4 (first bank) + PRG size
            int b4       = cfg[4];
            int startNor = outer * 0x200000 + b4 * 8192;
            int expectedE000 = startNor + prgKb * 1024 - 8192;
            Assert.Equal(expectedE000, norAddr,
                $"$E000 should map to NOR 0x{expectedE000:X6} for {prgKb}KB PRG, got 0x{norAddr:X6}");
        }

        // ── 3. MMC3 config for 256KB PRG games ───────────────────────────────

        [Theory]
        [InlineData(256,  16)]
        [InlineData(256,  64)]
        [InlineData(256, 128)]
        [InlineData(256, 256)]
        public void Mmc3_256KB_Prg_E000Window_CorrectlyMapped(int prgKb, int chrKb)
        {
            var game   = TestHelper.Mmc3(prgKb, chrKb);
            var result = TestHelper.Build(8, game);
            Assert.Equal(1, result.GameCount);

            var cfg = TestHelper.GetConfig(result.NorBinary, 0);
            Assert.Equal(1, cfg[3]); // PS=1 → InnerMask=0x1F for 256KB PRG

            int b7        = cfg[7];
            int b4        = cfg[4];
            int outer     = (cfg[0] >> 4) & 0x0F;
            int innerMask = 0x1F;
            int bankNum   = (0xFF & innerMask) | (b7 & (~innerMask & 0xFF));
            int norAddr   = bankNum * 8192;
            int startNor  = outer * 0x200000 + b4 * 8192;
            int expectedE000 = startNor + prgKb * 1024 - 8192;
            Assert.Equal(expectedE000, norAddr,
                $"$E000 NOR 0x{norAddr:X6} != expected 0x{expectedE000:X6}");
        }

        // ── 4. MMC3 covers all PRG sizes ─────────────────────────────────────

        [Theory]
        [InlineData( 32,  16)]
        [InlineData( 64,  64)]
        [InlineData(128, 128)]
        [InlineData(256, 256)]
        [InlineData(512, 256)]
        public void Mmc3_AllPrgSizes_Placed_NotSkipped(int prgKb, int chrKb)
        {
            var game   = TestHelper.Mmc3(prgKb, chrKb);
            var result = TestHelper.Build(8, game);
            Assert.Equal(1, result.GameCount);
        }

        // ── 5. MMC3 can fill beyond first 2MB window on large chips ──────────

        [Fact]
        public void Mmc3_BeyondFirstWindow_PlacedOnLargeChip()
        {
            // 8 × 256KB PRG + 128KB CHR = 8 × 384KB phys (aligned to 256KB = 512KB each)
            // = 4MB total. Requires going past 0x200000 on an 8MB chip.
            var games = Enumerable.Range(0, 8).Select(_ => TestHelper.Mmc3(256, 128)).ToArray();
            var result = TestHelper.Build(8, games);

            // All 8 should be placed (4MB of games fits in 7.5MB available on 8MB chip)
            Assert.Equal(8, result.GameCount);

            // At least one game should have PA=1 (placed beyond first 2MB window)
            bool hasPA1 = false;
            for (int i = 0; i < result.GameCount; i++)
            {
                var cfg = TestHelper.GetConfig(result.NorBinary, i);
                int pa  = (cfg[0] >> 4) & 0x0F;
                if (pa >= 1) hasPA1 = true;
            }
            Assert.True(hasPA1, "At least one game should be placed with PA=1 (beyond 2MB)");
        }

        [Fact]
        public void Mmc3_ManyGames_CorrectCountOnLargeChip()
        {
            // Verify a realistic multicart scenario: mixed sizes, large chip
            var games = new[]
            {
                TestHelper.Mmc3(128, 128),
                TestHelper.Mmc3(128, 128),
                TestHelper.Mmc3(256, 128),
                TestHelper.Mmc3(256, 256),
                TestHelper.Mmc3(128, 128),
                TestHelper.Mmc3(256, 128),
            };
            var result = TestHelper.Build(8, games);
            Assert.Equal(games.Length, result.GameCount);
        }

        // ── 6. Games do not overlap regardless of size mix ───────────────────

        [Fact]
        public void MixedSizeGames_DoNotOverlap()
        {
            var games = new[]
            {
                TestHelper.Nrom( 32,   8),
                TestHelper.Mmc3( 64,  64),
                TestHelper.Mmc3(128, 128),
                TestHelper.Mmc3(256, 128),
                TestHelper.Mmc3(256, 256),
            };
            var result = TestHelper.Build(8, games);
            Assert.Equal(games.Length, result.GameCount);

            var regions = new (int start, int end)[games.Length];
            for (int i = 0; i < games.Length; i++)
            {
                var cfg     = TestHelper.GetConfig(result.NorBinary, i);
                int outer   = (cfg[0] >> 4) & 0x0F;
                int b4      = cfg[4];
                int start   = outer * 0x200000 + b4 * 8192;
                int effPrg  = games[i].Mapper == 4
                    ? RomBuilder.EffectivePrgForChr(games[i].PrgSize, games[i].ChrSize)
                    : games[i].PrgSize;
                regions[i]  = (start, start + effPrg + games[i].ChrSize);
            }

            for (int a = 0; a < regions.Length; a++)
            for (int b = a + 1; b < regions.Length; b++)
            {
                bool overlap = regions[a].start < regions[b].end
                            && regions[b].start < regions[a].end;
                Assert.False(overlap,
                    $"Games {a} [{regions[a].start:X6}-{regions[a].end:X6}) and " +
                    $"{b} [{regions[b].start:X6}-{regions[b].end:X6}) overlap");
            }
        }

        // ── 7. SpaceCalculator matches actual build capacity ──────────────────

        [Fact]
        public void SpaceCalculator_UsableBytes_MatchesBuildCapacity_8MB()
        {
            // The usable space reported by SpaceCalculator should match what
            // actually fits in a build for an 8MB chip.
            // 8MB chip: usable = 480KB gaps + (8MB - 512KB) = 480KB + 7680KB = 8160KB
            var cfg  = new BuildConfig { ChipSizeMb = 8 };
            var info = SpaceCalculator.Calculate(cfg);
            long expected = (480 + 7680) * 1024L;
            Assert.Equal(expected, info.UsableBytes);
        }

        [Theory]
        [InlineData(2,  (480 + 1536))]   // 480KB gaps + 1.5MB after kernel
        [InlineData(4,  (480 + 3584))]   // 480KB gaps + 3.5MB after kernel
        [InlineData(8,  (480 + 7680))]   // 480KB gaps + 7.5MB after kernel
        [InlineData(16, (480 + 15872))]  // 480KB gaps + 15.5MB after kernel
        public void SpaceCalculator_UsableBytes_ExactValues(int chipMb, int expectedKb)
        {
            var cfg  = new BuildConfig { ChipSizeMb = chipMb };
            var info = SpaceCalculator.Calculate(cfg);
            Assert.Equal((long)expectedKb * 1024, info.UsableBytes);
        }
    }
}
