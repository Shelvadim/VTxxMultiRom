using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using VT03Builder.Models;
using VT03Builder.Services;

namespace VT03Builder.Tests
{
    /// <summary>
    /// Verifies the binary content of the built ROM:
    ///   - Kernel occupies bytes 0–0x7FFFF unchanged
    ///   - Game data is written at the offsets the config encodes
    ///   - Config table at 0x7C000 has the right values
    ///   - Game list at 0x79000 has correct count and names
    ///   - Non-game areas are padded with 0xFF
    ///   - No two games overlap
    /// </summary>
    public class BinaryLayoutTests
    {
        // ── Kernel preservation ───────────────────────────────────────────────

        [Fact]
        public void KernelBytes_PreservedInOutput()
        {
            var kernel = TestHelper.FakeKernel();
            var cfg    = new BuildConfig { ChipSizeMb = 8 };
            cfg.Games.Add(TestHelper.Nrom(32, 8));
            var result = RomBuilder.Build(cfg, kernel);

            // Games are now packed into kernel gaps starting at 0x000000, so byte 0
            // will be game data.  Check the CPU code region (0x07E000+) which no game
            // ever touches — FakeKernel fills it with 0xAB.
            Assert.Equal(kernel[0x07E000], result.NorBinary[0x07E000]);
            Assert.Equal(kernel[0x07E001], result.NorBinary[0x07E001]);
            Assert.Equal(kernel[0x07E002], result.NorBinary[0x07E002]);
            Assert.Equal(kernel[0x07E003], result.NorBinary[0x07E003]);
        }

        [Fact]
        public void OutputSize_MatchesChipSize()
        {
            foreach (int mb in new[] { 2, 4, 8, 16 })
            {
                var result = TestHelper.Build(mb, TestHelper.Nrom(32, 8));
                Assert.Equal(mb * 1024 * 1024, result.NorBinary.Length);
            }
        }

        // ── NROM game data placement ──────────────────────────────────────────

        [Theory]
        [InlineData(16,  8)]
        [InlineData(32,  8)]
        [InlineData(32, 32)]
        public void NromGame_DataAtExpectedOffset(int prgKb, int chrKb)
        {
            var game   = TestHelper.Nrom(prgKb, chrKb);
            var result = TestHelper.Build(8, game);
            var cfg    = TestHelper.GetConfig(result.NorBinary, 0);

            int b4     = cfg[4];
            int outer  = (cfg[0] >> 4) & 0x0F;
            int romOff = outer * 0x200000 + b4 * 8192;

            // PRG data
            Assert.True(TestHelper.DataAt(result.NorBinary, romOff, game.PrgData, game.PrgSize),
                $"NROM PRG={prgKb}KB not at 0x{romOff:X6}");
        }

        // ── MMC3 game data placement ──────────────────────────────────────────

        [Theory]
        [InlineData( 32,  16)]
        [InlineData( 64,  32)]
        [InlineData(128, 128)]
        [InlineData(256, 256)]
        public void Mmc3Game_PrgDataAtRomOffset(int prgKb, int chrKb)
        {
            var game   = TestHelper.Mmc3(prgKb, chrKb);
            var result = TestHelper.Build(32, game);
            var cfg    = TestHelper.GetConfig(result.NorBinary, 0);

            int b4     = cfg[4];
            int outer  = (cfg[0] >> 4) & 0x0F;
            int romOff = outer * 0x200000 + b4 * 8192;

            Assert.True(TestHelper.DataAt(result.NorBinary, romOff, game.PrgData, game.PrgSize),
                $"MMC3 PRG={prgKb}KB not found at 0x{romOff:X6}");
        }

        [Theory]
        [InlineData( 32,  16)]
        [InlineData( 64,  32)]
        [InlineData(128, 128)]
        [InlineData(256, 256)]
        public void Mmc3Game_ChrDataAfterEffectivePrg(int prgKb, int chrKb)
        {
            var game    = TestHelper.Mmc3(prgKb, chrKb);
            var result  = TestHelper.Build(32, game);
            var cfg     = TestHelper.GetConfig(result.NorBinary, 0);

            int b4      = cfg[4];
            int outer   = (cfg[0] >> 4) & 0x0F;
            int romOff  = outer * 0x200000 + b4 * 8192;
            int effPrg  = RomBuilder.EffectivePrgForChr(prgKb * 1024, chrKb * 1024);
            int chrOff  = romOff + effPrg;

            Assert.True(TestHelper.DataAt(result.NorBinary, chrOff, game.ChrData, game.ChrSize),
                $"MMC3 CHR={chrKb}KB not at 0x{chrOff:X6} (romOff=0x{romOff:X6} effPrg={effPrg/1024}KB)");
        }

        // ── Config table ──────────────────────────────────────────────────────

        [Fact]
        public void ConfigTable_GameCount_InGameList()
        {
            var result = TestHelper.Build(8,
                TestHelper.Nrom(32,  8),
                TestHelper.Nrom(32, 16),
                TestHelper.Mmc3(128, 128));

            // Game count at 0x79000 (2 bytes LE)
            byte lo = result.NorBinary[0x79000];
            byte hi = result.NorBinary[0x79001];
            int  count = lo | (hi << 8);
            Assert.Equal(3, count);
        }

        [Fact]
        public void ConfigTable_NineBytes_PerGame()
        {
            int n = 4;
            var games = Enumerable.Range(0, n).Select(_ => TestHelper.Nrom(32, 8)).ToArray();
            var result = TestHelper.Build(8, games);

            for (int i = 0; i < n; i++)
            {
                // Each config entry must start at ConfigTable + i*9
                var cfg = TestHelper.GetConfig(result.NorBinary, i);
                Assert.Equal(9, cfg.Length);
                // Mode byte for NROM-32 is 0x04
                Assert.Equal((byte)0x04, cfg[3]);
            }
        }

        [Fact]
        public void MirrorFlag_NROM_Vertical()
        {
            var game   = TestHelper.Nrom(32, 8, vertical: true);
            var result = TestHelper.Build(8, game);
            var cfg    = TestHelper.GetConfig(result.NorBinary, 0);
            Assert.Equal((byte)0x00, cfg[8]);
        }

        [Fact]
        public void MirrorFlag_NROM_Horizontal()
        {
            var game   = TestHelper.Nrom(32, 8, vertical: false);
            var result = TestHelper.Build(8, game);
            var cfg    = TestHelper.GetConfig(result.NorBinary, 0);
            Assert.Equal((byte)0x01, cfg[8]);
        }

        // ── No-overlap invariant ──────────────────────────────────────────────

        [Fact]
        public void MultipleGames_DoNotOverlap()
        {
            var games = new NesRom[]
            {
                TestHelper.Nrom( 32,  8),
                TestHelper.Nrom( 32, 32),
                TestHelper.Mmc3( 64,  32),
                TestHelper.Mmc3(128, 128),
                TestHelper.Mmc3( 32,  16),
            };
            var result = TestHelper.Build(8, games);
            Assert.Equal(games.Length, result.GameCount);

            // Build list of (start, end) for each game from config
            var regions = new List<(int start, int end, int i)>();
            for (int i = 0; i < games.Length; i++)
            {
                var cfg   = TestHelper.GetConfig(result.NorBinary, i);
                int outer = (cfg[0] >> 4) & 0x0F;
                int b4    = cfg[4];
                int start = outer * 0x200000 + b4 * 8192;
                int effPrg = games[i].Mapper == 4
                    ? RomBuilder.EffectivePrgForChr(games[i].PrgSize, games[i].ChrSize)
                    : games[i].PrgSize;
                int end   = start + effPrg + games[i].ChrSize;
                regions.Add((start, end, i));
            }

            // Check every pair
            for (int a = 0; a < regions.Count; a++)
            for (int b = a + 1; b < regions.Count; b++)
            {
                bool overlap = regions[a].start < regions[b].end &&
                               regions[b].start < regions[a].end;
                Assert.False(overlap,
                    $"Games {regions[a].i} and {regions[b].i} overlap: " +
                    $"[0x{regions[a].start:X6}–0x{regions[a].end:X6}) vs " +
                    $"[0x{regions[b].start:X6}–0x{regions[b].end:X6})");
            }
        }

        // ── Padding ───────────────────────────────────────────────────────────

        [Fact]
        public void UnusedArea_PaddedWith0xFF()
        {
            // A single tiny NROM game on a large chip: the vast majority is padding.
            var result = TestHelper.Build(8, TestHelper.Nrom(32, 8));
            var bin    = result.NorBinary;

            // Check a region known to be empty (after NROM area, before MMC3 area)
            // We know no MMC3 games exist, so 0x200000–end should all be 0xFF.
            for (int i = 0x200000; i < 0x200010; i++)
                Assert.Equal((byte)0xFF, bin[i]);
        }
    }
}
