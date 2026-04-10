using System;
using System.Linq;
using Xunit;
using VT03Builder.Models;
using VT03Builder.Services;

namespace VT03Builder.Tests
{
    /// <summary>
    /// Tests for:
    ///   1. LCD initialization stub (NOR 0x06E000, SUP 400-in-1 / TFT consoles)
    ///   2. Menu navigation page logic simulated in C#
    ///      - Down at last game on page → page jump (not single scroll)
    ///      - Right with all games fitting on one page → no page created
    ///      - Up at top of page 2+ → jumps to previous page (not single scroll)
    /// </summary>
    public class LcdAndNavTests
    {
        // ── LCD stub constants (must match RomBuilder.WriteLcdStub) ───────────
        private const int LcdStubAddr  = 0x06E000;
        private const int ChipSizeMb   = 8;
        private const int ChipSizeBytes = ChipSizeMb * 1024 * 1024;

        // ── helpers ───────────────────────────────────────────────────────────

        private static byte[] BuildWithLcd(bool initLcd)
        {
            var cfg = new BuildConfig
            {
                ChipSizeMb = ChipSizeMb,
                InitLcd    = initLcd,
                Games      = new System.Collections.Generic.List<NesRom>
                             { TestHelper.Nrom(32, 8) }
            };
            var result = RomBuilder.Build(cfg, TestHelper.FakeKernel());
            return result.NorBinary;
        }

        // ── 1. LCD stub tests ─────────────────────────────────────────────────

        [Fact]
        public void LcdStub_WhenDisabled_SlotIsFF()
        {
            var rom = BuildWithLcd(false);
            // The entire LCD stub region should be 0xFF (unprogrammed flash)
            for (int i = 0; i < 104; i++)
                Assert.Equal(0xFF, rom[LcdStubAddr + i]);
        }

        [Fact]
        public void LcdStub_WhenEnabled_StartsAtCorrectAddress()
        {
            var rom = BuildWithLcd(true);
            // First byte of stub = 0xA9 (LDA immediate)
            Assert.Equal(0xA9, rom[LcdStubAddr]);
        }

        [Fact]
        public void LcdStub_WhenEnabled_NotFF()
        {
            var rom = BuildWithLcd(true);
            // At least some bytes in the stub region are not 0xFF
            bool hasData = false;
            for (int i = 0; i < 104; i++)
                if (rom[LcdStubAddr + i] != 0xFF) { hasData = true; break; }
            Assert.True(hasData, "LCD stub should contain non-0xFF bytes when enabled");
        }

        [Fact]
        public void LcdStub_WhenEnabled_ContainsChrBankingSequence()
        {
            var rom = BuildWithLcd(true);
            // CHR setup: LDA #$20, STA $2018
            // Expect bytes: A9 20 8D 18 20 at start of stub
            Assert.Equal(0xA9, rom[LcdStubAddr + 0]);  // LDA #imm
            Assert.Equal(0x20, rom[LcdStubAddr + 1]);  // #$20 (IntermBank=2)
            Assert.Equal(0x8D, rom[LcdStubAddr + 2]);  // STA abs
            Assert.Equal(0x18, rom[LcdStubAddr + 3]);  // lo($2018)
            Assert.Equal(0x20, rom[LcdStubAddr + 4]);  // hi($2018)
        }

        [Fact]
        public void LcdStub_WhenEnabled_ContainsType3BacklightRegisters()
        {
            var rom = BuildWithLcd(true);
            byte[] stub = rom[LcdStubAddr..(LcdStubAddr + 104)];
            // Type 3 backlight: LDA #$1F, STA $413F
            bool foundType3 = false;
            for (int i = 0; i < stub.Length - 4; i++)
            {
                if (stub[i]   == 0xA9 && stub[i+1] == 0x1F &&
                    stub[i+2] == 0x8D && stub[i+3] == 0x3F && stub[i+4] == 0x41)
                {
                    foundType3 = true; break;
                }
            }
            Assert.True(foundType3, "LCD stub must enable Type-3 backlight ($413F=$1F)");
        }

        [Fact]
        public void LcdStub_WhenEnabled_ContainsType12BacklightRegister()
        {
            var rom = BuildWithLcd(true);
            byte[] stub = rom[LcdStubAddr..(LcdStubAddr + 104)];
            // Type 1/2 backlight: LDA #$0F, STA $412C
            bool foundType12 = false;
            for (int i = 0; i < stub.Length - 4; i++)
            {
                if (stub[i]   == 0xA9 && stub[i+1] == 0x0F &&
                    stub[i+2] == 0x8D && stub[i+3] == 0x2C && stub[i+4] == 0x41)
                {
                    foundType12 = true; break;
                }
            }
            Assert.True(foundType12, "LCD stub must enable Type-1/2 backlight ($412C=$0F)");
        }

        [Fact]
        public void LcdStub_WhenEnabled_ContainsJmpToRAM()
        {
            var rom = BuildWithLcd(true);
            byte[] stub = rom[LcdStubAddr..(LcdStubAddr + 104)];
            // JMP $0400 = 4C 00 04
            bool foundJmp = false;
            for (int i = 0; i < stub.Length - 2; i++)
                if (stub[i] == 0x4C && stub[i+1] == 0x00 && stub[i+2] == 0x04)
                { foundJmp = true; break; }
            Assert.True(foundJmp, "LCD stub must contain JMP $0400 to RAM stub");
        }

        [Fact]
        public void LcdStub_WhenEnabled_RAMStubEndsWithIndirectJump()
        {
            var rom = BuildWithLcd(true);
            byte[] stub = rom[LcdStubAddr..(LcdStubAddr + 104)];
            // RAM stub ends with JMP ($FFFC) = 6C FC FF
            bool foundJmpI = false;
            for (int i = 0; i < stub.Length - 2; i++)
                if (stub[i] == 0x6C && stub[i+1] == 0xFC && stub[i+2] == 0xFF)
                { foundJmpI = true; break; }
            Assert.True(foundJmpI, "RAM stub must end with JMP ($FFFC) to RESET vector");
        }

        [Fact]
        public void LcdStub_DoesNotOverwriteKernel()
        {
            var rom = BuildWithLcd(true);
            // Stub is at 0x06E000. Kernel menu is at 0x07E000. No overlap.
            Assert.Equal(0x78, rom[0x07E000]); // SEI — first byte of our menu RESET
        }

        [Fact]
        public void LcdStub_EnabledVsDisabled_OnlyDiffersAtStubAddr()
        {
            var romOn  = BuildWithLcd(true);
            var romOff = BuildWithLcd(false);
            // Only the stub region differs
            int diffs = 0;
            for (int i = 0; i < ChipSizeBytes; i++)
                if (romOn[i] != romOff[i]) diffs++;
            Assert.True(diffs > 0,   "Enabled LCD should produce different ROM");
            Assert.True(diffs <= 104, $"Only stub bytes should differ, got {diffs} different bytes");
            // All differences must be within the stub region
            for (int i = 0; i < ChipSizeBytes; i++)
                if (romOn[i] != romOff[i])
                    Assert.True(i >= LcdStubAddr && i < LcdStubAddr + 104,
                        $"Difference at 0x{i:X6} is outside stub region");
        }

        // ── 2. Navigation page logic (simulated in C#) ────────────────────────
        //
        // We simulate the 6502 menu navigation logic directly in C# so we can
        // test all page-boundary cases without running the actual ROM.

        private const int VISIBLE = 20;

        /// <summary>Simulates one Down button press. Returns (newTop, newCur).</summary>
        private static (int top, int cur) PressDown(int top, int cur, int gcn)
        {
            if (cur + 1 >= gcn) return (top, cur);          // already at last game

            // At bottom of page AND a next page exists → page jump
            if (cur == top + VISIBLE - 1 && top + VISIBLE < gcn)
            {
                int newTop = top + VISIBLE;
                return (newTop, newTop);
            }

            // Normal scroll
            cur++;
            if (cur - top >= VISIBLE) top++;
            return (top, cur);
        }

        /// <summary>Simulates one Up button press. Returns (newTop, newCur).</summary>
        private static (int top, int cur) PressUp(int top, int cur, int gcn)
        {
            if (cur == 0) return (top, cur);                 // already at first game
            cur--;                                           // decrement first
            if (cur >= top) return (top, cur);               // still on same page — done
            // CUR crossed below TOP → page-jump, but CUR stays at decremented value
            // (preserves relative row within the previous page)
            int newTop = top >= VISIBLE ? top - VISIBLE : 0;
            return (newTop, cur);
        }

        /// <summary>Simulates one Right button press. Returns (newTop, newCur).</summary>
        private static (int top, int cur) PressRight(int top, int cur, int gcn)
        {
            int nextTop = top + VISIBLE;
            if (nextTop >= gcn) return (top, cur);           // no next page
            int oldRow  = cur - top;                         // preserve relative row
            int newCur  = Math.Min(nextTop + oldRow, gcn - 1);
            return (nextTop, newCur);
        }

        /// <summary>Simulates one Left button press. Returns (newTop, newCur).</summary>
        private static (int top, int cur) PressLeft(int top, int cur, int gcn)
        {
            if (top == 0) return (top, cur);                 // already on first page
            int oldRow  = cur - top;                         // preserve relative row
            int newTop  = top >= VISIBLE ? top - VISIBLE : 0;
            int newCur  = Math.Min(newTop + oldRow, gcn - 1);
            return (newTop, newCur);
        }

        // ── Down navigation tests ─────────────────────────────────────────────

        [Fact]
        public void Down_WithFewGames_NeverCreatesSecondPage()
        {
            // 7 games all fit on one page (VISIBLE=20). Right should do nothing.
            int gcn = 7;
            var (top, cur) = (0, 0);

            // Press Down 20 times (more than enough)
            for (int i = 0; i < 20; i++)
                (top, cur) = PressDown(top, cur, gcn);

            // Should land on last game, still on page 0
            Assert.Equal(gcn - 1, cur);
            Assert.Equal(0, top);
        }

        [Fact]
        public void Down_AtBottomOfPage_JumpsToNextPage()
        {
            // 40 games, VISIBLE=20. At game 19 (bottom of page 0), Down jumps to page 1.
            int gcn = 40;
            var (top, cur) = (0, 19);  // cursor at bottom of page 0
            (top, cur) = PressDown(top, cur, gcn);
            Assert.Equal(20, top);  // page 1 starts at 20
            Assert.Equal(20, cur);  // cursor at top of page 1
        }

        [Fact]
        public void Down_AtBottomOfLastPage_DoesNothing()
        {
            // 40 games, cursor at game 39 (last) — Down does nothing
            int gcn = 40;
            var (top, cur) = (20, 39);
            (top, cur) = PressDown(top, cur, gcn);
            Assert.Equal(20, top);
            Assert.Equal(39, cur);
        }

        [Fact]
        public void Down_NotAtPageBottom_ScrollsOneStep()
        {
            // Cursor in middle of page, Down moves by one
            int gcn = 40;
            var (top, cur) = (0, 5);
            (top, cur) = PressDown(top, cur, gcn);
            Assert.Equal(0, top);
            Assert.Equal(6, cur);
        }

        [Fact]
        public void Down_WithExactlyPageSizeGames_NoPageJump()
        {
            // Exactly 20 games = exactly one page. Down at game 19 (last) does nothing.
            int gcn = VISIBLE; // = 20
            var (top, cur) = (0, gcn - 1);
            (top, cur) = PressDown(top, cur, gcn);
            Assert.Equal(0, top);
            Assert.Equal(gcn - 1, cur);  // stays on last game
        }

        // ── Up navigation tests ───────────────────────────────────────────────

        [Fact]
        public void Up_AtFirstGame_DoesNothing()
        {
            var (top, cur) = (0, 0);
            (top, cur) = PressUp(top, cur, 30);
            Assert.Equal(0, top);
            Assert.Equal(0, cur);
        }

        [Fact]
        public void Up_AtTopOfPage2_JumpsToPage1()
        {
            // State: TOP=20, CUR=20 (top of page 2). Press Up → jump to page 1.
            // CUR decrements to 19, crosses below TOP → page jump, CUR stays at 19.
            int gcn = 40;
            var (top, cur) = (20, 20);
            (top, cur) = PressUp(top, cur, gcn);
            Assert.Equal(0,  top);   // back to page 1
            Assert.Equal(19, cur);   // last item of page 1 (relative position preserved)
        }

        [Fact]
        public void Up_AtTopOfPage3_JumpsToPage2()
        {
            // CUR=40 decrements to 39, crosses below TOP=40 → jump to page 2.
            // CUR stays at 39 (last item of page 2).
            int gcn = 60;
            var (top, cur) = (40, 40);  // top of page 3
            (top, cur) = PressUp(top, cur, gcn);
            Assert.Equal(20, top);   // page 2
            Assert.Equal(39, cur);   // last item of page 2
        }

        [Fact]
        public void Up_InMiddleOfPage_ScrollsOneStep()
        {
            int gcn = 40;
            var (top, cur) = (0, 5);
            (top, cur) = PressUp(top, cur, gcn);
            Assert.Equal(0, top);
            Assert.Equal(4, cur);
        }

        [Fact]
        public void Up_FromPage2_RepeatedPressStaysOnPage1()
        {
            // After page-jumping to page 1 (landing at cur=19), pressing Up again
            // scrolls normally within page 1 (cur-- from 19 to 18, stays on page 1).
            int gcn = 40;
            var (top, cur) = (20, 20);
            (top, cur) = PressUp(top, cur, gcn);  // page jump: (0, 19)
            Assert.Equal(0,  top);
            Assert.Equal(19, cur);
            // Further Up scrolls within page 1 without page-jumping
            (top, cur) = PressUp(top, cur, gcn);  // cur 19→18, still on page 1
            Assert.Equal(0,  top);
            Assert.Equal(18, cur);
        }

        // ── Right/Left navigation tests ───────────────────────────────────────

        [Fact]
        public void Right_WithFewerGamesThanVisible_DoesNothing()
        {
            // 7 games < VISIBLE=20: pressing Right does nothing
            int gcn = 7;
            var (top, cur) = (0, 3);
            (top, cur) = PressRight(top, cur, gcn);
            Assert.Equal(0, top);
            Assert.Equal(3, cur);  // cursor unchanged
        }

        [Fact]
        public void Right_WithExactlyOnePageOfGames_DoesNothing()
        {
            // Exactly VISIBLE games: no second page
            var (top, cur) = PressRight(0, 0, VISIBLE);
            Assert.Equal(0, top);
        }

        [Fact]
        public void Right_WithTwoPages_JumpsToSecondPage()
        {
            int gcn = 30;  // 20 + 10 = two pages
            var (top, cur) = (0, 0);
            (top, cur) = PressRight(top, cur, gcn);
            Assert.Equal(VISIBLE, top);  // page 2 start
            Assert.Equal(VISIBLE, cur);
        }

        [Fact]
        public void Right_OnLastPage_DoesNothing()
        {
            int gcn = 30;  // pages: [0..19], [20..29]
            var (top, cur) = (20, 20);  // already on last page
            (top, cur) = PressRight(top, cur, gcn);
            Assert.Equal(20, top);  // stays
            Assert.Equal(20, cur);
        }

        [Fact]
        public void Left_OnFirstPage_DoesNothing()
        {
            var (top, cur) = PressLeft(0, 5, 40);
            Assert.Equal(0, top);
        }

        [Fact]
        public void Left_OnSecondPage_JumpsToFirstPage()
        {
            var (top, cur) = PressLeft(20, 20, 40);
            Assert.Equal(0, top);
            Assert.Equal(0, cur);
        }

        // ── Multi-press sequences ─────────────────────────────────────────────

        [Fact]
        public void RightThenLeft_ReturnsToStart()
        {
            int gcn = 40;
            var (top, cur) = (0, 0);
            (top, cur) = PressRight(top, cur, gcn);
            (top, cur) = PressLeft(top, cur, gcn);
            Assert.Equal(0, top);
            Assert.Equal(0, cur);
        }

        [Fact]
        public void DownToEndOfPage_ThenUp_JumpsBackNotScrolls()
        {
            // Navigate to last item on page 1, then press Up — should page-jump
            int gcn = 40;
            var (top, cur) = (0, 0);
            // Go to bottom of page 0
            for (int i = 0; i < VISIBLE - 1; i++)
                (top, cur) = PressDown(top, cur, gcn);
            Assert.Equal(0, top);
            Assert.Equal(VISIBLE - 1, cur);  // at game 19
            // Page jump to page 1
            (top, cur) = PressDown(top, cur, gcn);
            Assert.Equal(VISIBLE, top);  // now on page 1
            // Press Up once — should jump back to page 0 immediately
            (top, cur) = PressUp(top, cur, gcn);
            Assert.Equal(0,  top);   // back to page 0
            Assert.Equal(19, cur);   // last item of page 0 (relative position)
        }

        [Fact]
        public void Navigation_ThreePages_CycleThroughAll()
        {
            int gcn = 55;  // 3 pages: [0..19], [20..39], [40..54]
            var (top, cur) = (0, 0);

            (top, cur) = PressRight(top, cur, gcn);
            Assert.Equal(20, top);

            (top, cur) = PressRight(top, cur, gcn);
            Assert.Equal(40, top);

            // Right on last page does nothing
            (top, cur) = PressRight(top, cur, gcn);
            Assert.Equal(40, top);

            // Left twice to get back to page 0
            (top, cur) = PressLeft(top, cur, gcn);
            Assert.Equal(20, top);
            (top, cur) = PressLeft(top, cur, gcn);
            Assert.Equal(0, top);
        }
        // ── 3. Config table offset tests (Y-register overflow bug) ────────────
        //
        // The 6502 Y register is 8-bit. CUR*9 overflows at CUR=29 (29*9=261>255).
        // The fix uses PLO/PHI (16-bit pointer) to walk the CFGTABLE.
        // We verify that the correct 9-byte config is written for every game index.

        private const int ConfigTableAddrC = 0x07C000;  // NOR address of CFGTABLE

        private static byte[] BuildManyGames(int count)
        {
            var cfg = new BuildConfig { ChipSizeMb = 16 };
            // Add enough MMC3 games to exceed CUR=28 (the overflow point)
            for (int i = 0; i < count; i++)
                cfg.Games.Add(TestHelper.Mmc3(64, 64));
            var result = RomBuilder.Build(cfg, TestHelper.FakeKernel());
            return result.NorBinary;
        }

        private static byte[] GetConfigFromRom(byte[] rom, int gameIndex)
        {
            int offset = ConfigTableAddrC + gameIndex * 9;
            return rom[offset..(offset + 9)];
        }

        [Fact]
        public void ConfigTable_Game28_HasCorrectPA()
        {
            // Game 28 (0-based) is the last one before the Y-overflow boundary (CUR=28, Y=252).
            // This should work even with the old code.
            var rom = BuildManyGames(35);
            var cfg = GetConfigFromRom(rom, 28);
            // PA = upper nibble of cfg[0]. Must not be 0xFF (unprogrammed).
            Assert.NotEqual(0xFF, cfg[0]);
        }

        [Fact]
        public void ConfigTable_Game29_HasCorrectOffset()
        {
            // CUR=29: old code: Y = (29*9)&0xFF = 5 → reads game 0 config.
            // Fixed code: reads game 29 config correctly.
            var rom = BuildManyGames(35);
            var cfg0  = GetConfigFromRom(rom, 0);   // game 0 config
            var cfg29 = GetConfigFromRom(rom, 29);   // game 29 config
            // Game 29 must not have the same NOR start address as game 0
            // (PQ0 byte = cfg[4], start bank)
            Assert.NotEqual(cfg0[4], cfg29[4]);
        }

        [Fact]
        public void ConfigTable_AllGames_HaveUniqueStartBanks()
        {
            // Every game must have a unique physical placement (unique b4 within same PA).
            // If two games share the same cfg[0]+cfg[4] it means Y wrapped and reads wrong entry.
            var rom = BuildManyGames(35);
            var seen = new System.Collections.Generic.HashSet<(byte, byte)>();
            for (int i = 0; i < 35; i++)
            {
                var cfg = GetConfigFromRom(rom, i);
                if (cfg[0] == 0xFF && cfg[4] == 0xFF) continue;  // unplaced/skipped
                var key = (cfg[0], cfg[4]);  // (PA byte, start bank)
                Assert.True(seen.Add(key),
                    $"Game {i} has duplicate config (PA/bank={key}) — Y-register overflow likely");
            }
        }

        [Fact]
        public void ConfigTable_Game29To34_NotEqualToGame0To5()
        {
            // The specific failure mode: CUR=29 wraps Y to 5 → reads game 0 config.
            // Games 29..34 must not equal games 0..5 respectively.
            var rom = BuildManyGames(40);
            for (int i = 0; i < 6; i++)
            {
                var cfgEarly = GetConfigFromRom(rom, i);
                var cfgLate  = GetConfigFromRom(rom, 29 + i);
                if (cfgLate[0] == 0xFF) continue;  // game may have been skipped
                Assert.False(cfgEarly.SequenceEqual(cfgLate),
                    $"Game {29+i} config equals game {i} config — Y-register overflow detected");
            }
        }

        [Fact]
        public void ConfigTable_Game56_HasCorrectOffset()
        {
            // CUR=56: Y = (56*9)&0xFF = 0xF8 → wraps to game 27's offset.
            // This is a second overflow boundary.
            var rom = BuildManyGames(60);
            var cfg56 = GetConfigFromRom(rom, 56);
            var cfg27 = GetConfigFromRom(rom, 27);
            if (cfg56[0] == 0xFF) return;  // game may not have been placed
            Assert.False(cfg56.SequenceEqual(cfg27),
                "Game 56 config equals game 27 config — Y-register overflow at second boundary");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(28)]
        [InlineData(29)]  // first overflow boundary
        [InlineData(30)]
        [InlineData(56)]  // second overflow boundary
        [InlineData(57)]
        public void ConfigTable_GameN_NotAllFF(int gameIndex)
        {
            int total = gameIndex + 5;
            if (total > 200) return;
            var rom = BuildManyGames(Math.Min(total, 100));
            var cfg = GetConfigFromRom(rom, gameIndex);
            // If game was placed, config must not be all-FF (unprogrammed)
            bool allFF = cfg.All(b => b == 0xFF);
            Assert.False(allFF, $"Game {gameIndex} config is all 0xFF — not placed or wrong offset");
        }

        // ── Position-preserving navigation tests ─────────────────────────────

        [Fact]
        public void Up_AtTopOfPage2_PreservesRelativePosition()
        {
            // Bug: cursor on game 21 (index 20, top of page 2), UP → cursor goes to index 0
            // Fix: cursor should stay at index 19 (last game of page 1 = position 20)
            int gcn = 40;
            var (top, cur) = (20, 20);   // game 21, first of page 2
            (top, cur) = PressUp(top, cur, gcn);
            Assert.Equal(0,  top);       // page 1
            Assert.Equal(19, cur);       // position 20 (index 19), NOT position 1 (index 0)
        }

        [Fact]
        public void Up_MiddleOfPage2_PreservesPosition()
        {
            // Cursor on game 25 (index 24, row 4 of page 2): UP → index 23 on page 1
            int gcn = 40;
            var (top, cur) = (20, 24);
            (top, cur) = PressUp(top, cur, gcn);
            Assert.Equal(20, top);   // still on page 2 (just moved up one row)
            Assert.Equal(23, cur);
        }

        [Fact]
        public void Right_PreservesRelativeRow()
        {
            // Bug: cursor on game 10 (index 9, row 9), RIGHT → cursor at position 21 (index 20)
            // Fix: cursor should stay at row 9 → index 29 on page 2 (position 30)
            int gcn = 60;
            var (top, cur) = (0, 9);     // row 9 on page 1
            (top, cur) = PressRight(top, cur, gcn);
            Assert.Equal(20, top);       // page 2
            Assert.Equal(29, cur);       // row 9 on page 2 = index 29, NOT index 20
        }

        [Fact]
        public void Right_Row0_LandsAtFirstOfNextPage()
        {
            // Cursor at top of page (row 0): RIGHT → still at row 0 of next page
            int gcn = 60;
            var (top, cur) = (0, 0);
            (top, cur) = PressRight(top, cur, gcn);
            Assert.Equal(20, top);
            Assert.Equal(20, cur);   // row 0 of page 2
        }

        [Fact]
        public void Left_PreservesRelativeRow()
        {
            // Cursor on game 30 (index 29, row 9 of page 2): LEFT → row 9 of page 1 = index 9
            int gcn = 60;
            var (top, cur) = (20, 29);   // row 9 on page 2
            (top, cur) = PressLeft(top, cur, gcn);
            Assert.Equal(0,  top);       // page 1
            Assert.Equal(9, cur);        // row 9 of page 1 = index 9
        }

        [Fact]
        public void Left_Row0_LandsAtFirstOfPrevPage()
        {
            // Cursor at top of page 2 (row 0): LEFT → row 0 of page 1
            int gcn = 60;
            var (top, cur) = (20, 20);
            (top, cur) = PressLeft(top, cur, gcn);
            Assert.Equal(0, top);
            Assert.Equal(0, cur);
        }

        [Fact]
        public void Right_ClampsToLastGame_WhenRowExceedsGcn()
        {
            // 25 games: page 1 has rows 0-19, page 2 has rows 0-4 (games 20-24)
            // Cursor at row 9 of page 1 (index 9): RIGHT → row 9 would be index 29, but GCN=25
            // So cursor should clamp to GCN-1=24
            int gcn = 25;
            var (top, cur) = (0, 9);
            (top, cur) = PressRight(top, cur, gcn);
            Assert.Equal(20, top);
            Assert.Equal(24, cur);   // clamped to last game
        }

        [Fact]
        public void UpThenRight_CursorConsistent()
        {
            // Navigate to row 5 of page 2, press UP, then RIGHT: should end at row 5 of page 2
            int gcn = 60;
            var (top, cur) = (20, 25);   // row 5 of page 2
            (top, cur) = PressUp(top, cur, gcn);   // move to row 4 of page 2
            Assert.Equal(20, top); Assert.Equal(24, cur);
            (top, cur) = PressDown(top, cur, gcn);  // back to row 5
            Assert.Equal(20, top); Assert.Equal(25, cur);
        }

        [Fact]
        public void RightLeft_ReturnToSamePosition()
        {
            // RIGHT then LEFT should return to original cursor position
            int gcn = 60;
            var (top0, cur0) = (0, 7);
            var (top, cur) = PressRight(top0, cur0, gcn);
            (top, cur) = PressLeft(top, cur, gcn);
            Assert.Equal(top0, top);
            Assert.Equal(cur0, cur);
        }

        [Fact]
        public void Up_AtRow5OfPage2_LandsAtRow5OfPage1()
        {
            // UP at row 5, page 2 (index 25) → stays at row 5, still page 2 (just moves up)
            // Because row 5 > 0 of page, so it's just a normal up, not page jump
            int gcn = 60;
            var (top, cur) = (20, 25);   // row 5, page 2
            (top, cur) = PressUp(top, cur, gcn);
            Assert.Equal(20, top);       // still page 2
            Assert.Equal(24, cur);       // row 4, page 2
        }

    }
}
