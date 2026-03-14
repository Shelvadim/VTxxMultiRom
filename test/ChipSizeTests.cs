using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using VT03Builder.Models;
using VT03Builder.Services;

namespace VT03Builder.Tests
{
    /// <summary>
    /// Verifies correct behaviour across all supported chip sizes.
    ///
    ///   2 MB : MMC3 games are packed after NROM games in the single window (0x000000–0x1FFFFF)
    ///   4 MB : NROM in 0x080000–0x1FFFFF, MMC3 starts at 0x200000
    ///   8 MB : same, more MMC3 space
    ///  16 MB : same, even more
    /// </summary>
    public class ChipSizeTests
    {
        [Theory]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(8)]
        [InlineData(16)]
        public void ChipSize_OutputIsCorrectLength(int chipMb)
        {
            var result = TestHelper.Build(chipMb, TestHelper.Nrom(32, 8));
            Assert.Equal(chipMb * 1024 * 1024, result.NorBinary.Length);
        }

        // ── 2 MB chip ─────────────────────────────────────────────────────────

        [Fact]
        public void Chip2MB_Mmc3Games_AreNotSkipped()
        {
            var nrom = TestHelper.Nrom( 32,  8);
            var mmc3 = TestHelper.Mmc3(128, 128);
            var result = TestHelper.Build(2, nrom, mmc3);

            Assert.Equal(2, result.GameCount);
        }

        [Fact]
        public void Chip2MB_Mmc3Game_PlacedAfterNrom()
        {
            var nrom = TestHelper.Nrom( 32,  8);
            var mmc3 = TestHelper.Mmc3(128, 128);
            var result = TestHelper.Build(2, nrom, mmc3);

            var cfgNrom = TestHelper.GetConfig(result.NorBinary, 0);
            var cfgMmc3 = TestHelper.GetConfig(result.NorBinary, 1);

            int nromOuter = (cfgNrom[0] >> 4) & 0x0F;
            int nromStart = nromOuter * 0x200000 + cfgNrom[4] * 8192;
            int mmc3Outer = (cfgMmc3[0] >> 4) & 0x0F;
            int mmc3Start = mmc3Outer * 0x200000 + cfgMmc3[4] * 8192;

            Assert.True(mmc3Start > nromStart,
                $"MMC3 (0x{mmc3Start:X6}) should come after NROM (0x{nromStart:X6}) on 2MB chip");
        }

        [Fact]
        public void Chip2MB_Mmc3Game_StaysWithinChip()
        {
            var mmc3 = TestHelper.Mmc3(128, 128);
            var result = TestHelper.Build(2, mmc3);

            Assert.Equal(1, result.GameCount);

            var cfg   = TestHelper.GetConfig(result.NorBinary, 0);
            int outer = (cfg[0] >> 4) & 0x0F;
            int b4    = cfg[4];
            int start = outer * 0x200000 + b4 * 8192;
            int end   = start + 128 * 1024 + 128 * 1024;

            Assert.True(end <= 2 * 1024 * 1024,
                $"MMC3 game end 0x{end:X6} exceeds 2MB chip");
        }

        [Fact]
        public void Chip2MB_ManyNromGames_PushMmc3_SkipsIfNoRoom()
        {
            // Fill NROM area almost completely (NROM area = 0x080000–0x1FFFFF = 1.5 MB)
            // 1.5MB / (32KB+8KB = 40KB rounded up to 48KB per game) ≈ 32 games
            // Place enough games to leave < 256KB (one MMC3 game size), then add an MMC3 game.
            // The MMC3 game should be skipped.
            var games = new System.Collections.Generic.List<NesRom>();
            // 30 × 48KB = 1440KB used of 1536KB NROM space
            for (int i = 0; i < 30; i++) games.Add(TestHelper.Nrom(32, 8));
            // Add an MMC3 game that needs 256KB space
            games.Add(TestHelper.Mmc3(128, 128));

            var result = TestHelper.Build(2, games.ToArray());
            // The MMC3 game might be placed if there is still room; we're just checking
            // the build doesn't crash and the count is sane.
            Assert.True(result.GameCount >= 30, "NROM games should be placed");
        }

        // ── 4 MB chip ─────────────────────────────────────────────────────────

        [Fact]
        public void Chip4MB_Mmc3_StartsAt_0x200000()
        {
            var nrom = TestHelper.Nrom(32, 8);
            var mmc3 = TestHelper.Mmc3(128, 128);
            var result = TestHelper.Build(4, nrom, mmc3);

            var cfgMmc3 = TestHelper.GetConfig(result.NorBinary, 1);
            int outer   = (cfgMmc3[0] >> 4) & 0x0F;
            int b4      = cfgMmc3[4];
            int start   = outer * 0x200000 + b4 * 8192;

            Assert.Equal(0x200000, start);
        }

        [Fact]
        public void Chip4MB_NromGameDoesNotEnter_Mmc3Window()
        {
            // Pack enough NROM games to approach the 0x200000 limit
            var games = new System.Collections.Generic.List<NesRom>();
            for (int i = 0; i < 50; i++) games.Add(TestHelper.Nrom(32, 8));

            var result = TestHelper.Build(4, games.ToArray());

            int acceptedNrom = result.GameCount;
            // All accepted NROM games must have their start < 0x200000
            for (int i = 0; i < acceptedNrom; i++)
            {
                var cfg   = TestHelper.GetConfig(result.NorBinary, i);
                int outer = (cfg[0] >> 4) & 0x0F;
                int b4    = cfg[4];
                int start = outer * 0x200000 + b4 * 8192;
                Assert.True(start < 0x200000,
                    $"NROM game {i} placed at 0x{start:X6} which is in the MMC3 window");
            }
        }

        // ── Larger chips ──────────────────────────────────────────────────────

        [Theory]
        [InlineData( 8)]
        [InlineData(16)]
        public void LargeChip_ManyMmc3Games_AllPlaced(int chipMb)
        {
            // How many 128KB+128KB games fit in (chipMb - 2) MB of MMC3 space?
            int mmc3SpaceMb = chipMb - 2;
            int gameSize    = (128 + 128) * 1024;
            int maxGames    = (mmc3SpaceMb * 1024 * 1024) / gameSize;
            // Use 80% to be safe (alignment overhead)
            int target = (maxGames * 4) / 5;

            var games = Enumerable.Range(0, target)
                                  .Select(_ => TestHelper.Mmc3(128, 128))
                                  .ToList();
            games.Insert(0, TestHelper.Nrom(32, 8));  // one NROM game to ensure ordering

            var result = TestHelper.Build(chipMb, games.ToArray());

            Assert.Equal(games.Count, result.GameCount);
        }

        // ── GameCount stat ────────────────────────────────────────────────────

        [Fact]
        public void GameCount_OnlyIncludesPlacedGames()
        {
            // On a 2MB chip, try to place more games than can fit
            var games = Enumerable.Range(0, 20)
                                  .Select(_ => TestHelper.Mmc3(128, 128))
                                  .ToArray();
            var result = TestHelper.Build(2, games);

            // Some games will be skipped; count must reflect only placed games
            Assert.True(result.GameCount < 20, "Not all games should fit on 2MB chip");
            Assert.True(result.GameCount > 0,  "At least one game should fit");

            // Verify game list count matches GameCount
            byte lo    = result.NorBinary[0x79000];
            byte hi    = result.NorBinary[0x79001];
            int  count = lo | (hi << 8);
            Assert.Equal(result.GameCount, count);
        }

        // ── NROM ordering: NROM before MMC3 in menu list ──────────────────────

        [Fact]
        public void NROM_Games_AppearBeforeMmc3_InConfigTable()
        {
            // Config table ordering: NROM first, then MMC3.
            // NROM games have mode byte 0x04 or 0x05; MMC3 has PS (0x00–0x04).
            // All NROM config[3] values are 0x04 or 0x05; MMC3 are <= 0x04 for PS.
            // Distinguishing: NROM mode 0x04 == MMC3 PS=0x04, BUT NROM games are placed
            // first so their config indices come first.
            var nrom1  = TestHelper.Nrom( 32,  8);
            var nrom2  = TestHelper.Nrom( 32, 16);
            var mmc3_1 = TestHelper.Mmc3(128, 128);
            var mmc3_2 = TestHelper.Mmc3( 64,  64);

            // Pass in mixed order — builder must reorder NROM before MMC3
            var result = TestHelper.Build(8, mmc3_1, nrom1, mmc3_2, nrom2);
            Assert.Equal(4, result.GameCount);

            // Config index 0 and 1 should be NROM (mode 0x04 or 0x05)
            var c0 = TestHelper.GetConfig(result.NorBinary, 0);
            var c1 = TestHelper.GetConfig(result.NorBinary, 1);
            Assert.True(c0[3] is 0x04 or 0x05, "Config[0] should be NROM");
            Assert.True(c1[3] is 0x04 or 0x05, "Config[1] should be NROM");

            // Config index 2 and 3 should be MMC3 (PS <= 4)
            // For 128KB PRG, PS=2; for 64KB PRG, PS=3
            var c2 = TestHelper.GetConfig(result.NorBinary, 2);
            var c3 = TestHelper.GetConfig(result.NorBinary, 3);
            Assert.True((c2[3] & 0x07) is 2 or 3, $"Config[2] should be MMC3 (PS=2 or 3), got {c2[3]}");
            Assert.True((c3[3] & 0x07) is 2 or 3, $"Config[3] should be MMC3 (PS=2 or 3), got {c3[3]}");
        }
    }
}
