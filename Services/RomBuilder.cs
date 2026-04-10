using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using VT03Builder.Models;
using VT03Builder.Services.SourceMappers;
using VT03Builder.Services.Targets;

namespace VT03Builder.Services
{
    /// <summary>
    /// Orchestrates building a VT03 OneBus NOR flash multicart binary.
    ///
    /// ROM layout (flat binary, filled with 0xFF):
    ///
    ///   Kernel-required addresses (from original_menu_patched.rom):
    ///     0x040000–0x040FFF   CHR font (4 KB)
    ///     0x079000–0x079FFF   GAMELIST  (written by builder)
    ///     0x07C000–0x07C1FF   CFGTABLE  (9 bytes × game count, written by builder)
    ///     0x07E000–0x07FFFF   CPU code  (8 KB)
    ///
    ///   Free kernel regions used for NROM game packing:
    ///     0x000000–0x03FFFF   256 KB  (before CHR font)
    ///     0x041000–0x078FFF   224 KB  (between CHR and GAMELIST)
    ///
    ///   Game overflow (NROM):   0x080000–0x1FFFFF
    ///   MMC3 games:             0x200000+  (always in 2 MB windows)
    ///
    ///   On a 2 MB chip (sharedWindow): MMC3 games follow NROM in the same window.
    ///
    ///   Output size = ChipSizeMb × 1048576, pre-filled with 0xFF.
    /// </summary>
    public static class RomBuilder
    {
        // ── Layout constants (mirror VtxxOneBusTarget — used by internal Build() overload) ──
        private const int KernelEnd  = VtxxOneBusTarget.KernelEnd;
        private const int NromEnd    = VtxxOneBusTarget.NromEnd;
        private const int WindowSize = VtxxOneBusTarget.WindowSize;

        private static readonly (int Start, int End)[] KernelFreeRegions =
            VtxxOneBusTarget.KernelFreeRegions;

        // ── Public entry point ────────────────────────────────────────────────

        /// <summary>
        /// Build a complete multicart flash image.
        /// Delegates all hardware-specific logic to the registered IHardwareTarget
        /// and ISourceMapperHandler objects — Build() itself is mapper-agnostic.
        /// </summary>
        public static BuildResult Build(BuildConfig cfg, IProgress<string>? log = null)
        {
            var target = TargetRegistry.GetRequired(cfg.TargetId);

            int    romSize = cfg.ChipSizeMb * 1024 * 1024;
            byte[] rom     = new byte[romSize];
            Array.Fill(rom, (byte)0xFF);

            // Let the target write its kernel/menu into the flash buffer.
            target.InitialiseFlash(rom, cfg);
            log?.Report($"Kernel: {target.KernelAreaSize} bytes ({target.DisplayName})");

            // ── Derive packing parameters from the target ─────────────────────
            // gameStart: first byte the packer may use (after kernel, or 0 for CoolBoy).
            // For VTxx OneBus, games also fill kernel free gaps; for other targets
            // the free list is simply (gameStart … romSize).
            // gameStart: first byte the packer may use (after kernel/menu window).
            // For VTxx OneBus = 0x080000; for CoolBoy = 0x080000 (menu window);
            // future targets may differ.
            int gameStart = target.KernelAreaSize;

            var entries        = new List<Models.GameEntry>();
            int nromHighWater  = gameStart;
            int mmc3Floor      = gameStart;
            int curMmc3        = gameStart;
            bool mmc3LoggedNote = false;

            // ── NROM free-list packer ─────────────────────────────────────────
            // VTxx OneBus: pack into kernel gaps first, then overflow.
            // Other targets: linear from gameStart.
            int nromWindowEnd = Math.Min(romSize, NromEnd);
            var freeList = new List<(int Start, int End)>();
            if (target.HasKernelArea)
            {
                foreach (var r in KernelFreeRegions)
                    freeList.Add(r);
                freeList.Add((gameStart, nromWindowEnd));
            }
            else
            {
                freeList.Add((gameStart, romSize));
            }

            var nromHandler = SourceMapperRegistry.Get(0)!;
            foreach (var game in cfg.Games.Where(g => g.Mapper == 0))
            {
                int gameSize = game.PrgSize + game.ChrSize;
                int align    = nromHandler.PlacementAlignment(game, target);
                int placed   = -1;

                for (int fi = 0; fi < freeList.Count; fi++)
                {
                    var (fs, fe) = freeList[fi];
                    int slotStart = ((fs + align - 1) / align) * align;
                    if (slotStart + gameSize > fe) continue;

                    placed = slotStart;
                    freeList.RemoveAt(fi);
                    int slotEnd = slotStart + ((gameSize + align - 1) / align) * align;
                    if (slotStart > fs)
                        freeList.Insert(fi, (fs, slotStart));
                    if (slotEnd < fe)
                        freeList.Insert(fi + (slotStart > fs ? 1 : 0), (slotEnd, fe));
                    break;
                }

                if (placed < 0)
                {
                    log?.Report($"SKIP  {game.FileName}: no NROM space left");
                    continue;
                }

                nromHandler.PlaceInFlash(game, rom, placed, target);
                var info   = nromHandler.GetBankingInfo(game, placed, target);
                var config = target.BuildConfigRecord(game, info, cfg);
                nromHandler.ApplySubmapper(config, cfg.Submapper);

                string name = CleanName(game.FileName);
                entries.Add(new Models.GameEntry(game, name, placed, config));

                bool inOverflow = placed >= gameStart;
                log?.Report($"NROM  {game.FileName,-32} @ 0x{placed:X6}" +
                            $"{(!inOverflow ? " [kernel gap]" : "")}  cfg={Hex(config)}");

                if (inOverflow)
                    nromHighWater = Math.Max(nromHighWater,
                        placed + ((gameSize + align - 1) / align) * align);
            }

            // ── MMC3 linear packer ────────────────────────────────────────────
            mmc3Floor = Math.Max(gameStart, nromHighWater);
            curMmc3   = mmc3Floor;

            var mmc3Handler = SourceMapperRegistry.Get(4)!;
            foreach (var game in cfg.Games.Where(g => g.Mapper == 4)
                                          .Where(g => !g.HasChrRam || cfg.AllowChrRam))
            {
                if (!mmc3LoggedNote)
                {
                    log?.Report($"NOTE  MMC3 games start at 0x{mmc3Floor:X6}");
                    mmc3LoggedNote = true;
                }

                int align    = mmc3Handler.PlacementAlignment(game, target);
                int physSize = mmc3Handler.PhysicalSize(game, target);

                if (curMmc3 % align != 0)
                    curMmc3 = ((curMmc3 + align - 1) / align) * align;

                // Never straddle a 2 MB window boundary.
                int wb = (curMmc3 / WindowSize) * WindowSize;
                if (curMmc3 + physSize > wb + WindowSize)
                    curMmc3 = wb + WindowSize;

                if (curMmc3 + physSize > romSize)
                {
                    log?.Report($"SKIP  {game.FileName}: no MMC3 space left (chip full)");
                    continue;
                }

                mmc3Handler.PlaceInFlash(game, rom, curMmc3, target);
                var info   = mmc3Handler.GetBankingInfo(game, curMmc3, target);
                var config = target.BuildConfigRecord(game, info, cfg);
                mmc3Handler.ApplySubmapper(config, cfg.Submapper);

                string name = CleanName(game.FileName);
                entries.Add(new Models.GameEntry(game, name, curMmc3, config));

                int effPrg = info.EffectivePrgSize;
                int padKB  = (effPrg - game.PrgSize) / 1024;
                log?.Report($"MMC3  {game.FileName,-32} @ 0x{curMmc3:X6}  cfg={Hex(config)}" +
                            (padKB > 0 ? $" [+{padKB}KB CHR-align pad]" : ""));
                curMmc3 += physSize;
            }

            // ── Write game table (names + config records) ─────────────────────
            target.WriteGameTable(rom, entries, cfg);

            if (cfg.Submapper >= 11 && cfg.Submapper <= 15)
                log?.Report($"NOTE  Submapper {cfg.Submapper}: hardware opcode bit-swap — " +
                             "ensure your ROMs were built for this console type.");

            int mmc3Used = Math.Max(0, curMmc3 - mmc3Floor);
            log?.Report($"Done  {entries.Count} games  |  chip {cfg.ChipSizeMb} MB  |  " +
                        $"NROM overflow {Math.Max(0, nromHighWater - gameStart) / 1024} KB  |  " +
                        $"MMC3 {mmc3Used / 1024} KB");

            if (cfg.PinSwap == 1)
                log?.Report("NOTE  Pin swap D1\u2194D9, D2\u2194D10 applied to .bin");

            var result = target.BuildOutputFiles(rom, cfg);
            result.GameCount = entries.Count;
            result.NromUsed  = Math.Max(0, nromHighWater - gameStart);
            result.Mmc3Used  = mmc3Used;
            return result;
        }

        /// <summary>
        /// Testable overload — caller supplies kernel bytes directly.
        /// Kept for backward compatibility with existing tests.
        /// Bypasses VtxxOneBusTarget.InitialiseFlash() and copies the supplied
        /// kernel bytes directly, then proceeds with the registry-based build.
        /// </summary>
        internal static BuildResult Build(BuildConfig cfg, byte[] kernelData,
                                          IProgress<string>? log = null)
        {
            var target = TargetRegistry.GetRequired(cfg.TargetId);

            int    romSize = cfg.ChipSizeMb * 1024 * 1024;
            byte[] rom     = new byte[romSize];
            Array.Fill(rom, (byte)0xFF);
            Array.Copy(kernelData, rom, Math.Min(kernelData.Length, rom.Length));
            log?.Report($"Kernel: {kernelData.Length} bytes");

            // gameStart: first byte the packer may use (after kernel/menu window).
            // For VTxx OneBus = 0x080000; for CoolBoy = 0x080000 (menu window);
            // future targets may differ.
            int gameStart = target.KernelAreaSize;

            var entries       = new List<Models.GameEntry>();
            int nromHighWater = gameStart;
            int mmc3Floor     = gameStart;
            int curMmc3       = gameStart;
            bool mmc3Note     = false;

            int nromWindowEnd = Math.Min(romSize, NromEnd);
            var freeList = new List<(int Start, int End)>();
            if (target.HasKernelArea)
            {
                foreach (var r in KernelFreeRegions) freeList.Add(r);
                freeList.Add((gameStart, nromWindowEnd));
            }
            else
            {
                freeList.Add((gameStart, romSize));
            }

            var nromHandler = SourceMapperRegistry.Get(0)!;
            foreach (var game in cfg.Games.Where(g => g.Mapper == 0))
            {
                int gameSize = game.PrgSize + game.ChrSize;
                int align    = nromHandler.PlacementAlignment(game, target);
                int placed   = -1;

                for (int fi = 0; fi < freeList.Count; fi++)
                {
                    var (fs, fe) = freeList[fi];
                    int ss = ((fs + align - 1) / align) * align;
                    if (ss + gameSize > fe) continue;
                    placed = ss;
                    freeList.RemoveAt(fi);
                    int se = ss + ((gameSize + align - 1) / align) * align;
                    if (ss > fs) freeList.Insert(fi, (fs, ss));
                    if (se < fe) freeList.Insert(fi + (ss > fs ? 1 : 0), (se, fe));
                    break;
                }
                if (placed < 0) { log?.Report($"SKIP  {game.FileName}: no NROM space"); continue; }

                nromHandler.PlaceInFlash(game, rom, placed, target);
                var info   = nromHandler.GetBankingInfo(game, placed, target);
                var config = target.BuildConfigRecord(game, info, cfg);
                nromHandler.ApplySubmapper(config, cfg.Submapper);
                string name = CleanName(game.FileName);
                entries.Add(new Models.GameEntry(game, name, placed, config));
                bool inOverflow = placed >= gameStart;
                log?.Report($"NROM  {game.FileName,-32} @ 0x{placed:X6}" +
                            (!inOverflow ? " [kernel gap]" : "") + $"  cfg={Hex(config)}");
                if (inOverflow)
                    nromHighWater = Math.Max(nromHighWater,
                        placed + ((gameSize + align - 1) / align) * align);
            }

            mmc3Floor = Math.Max(gameStart, nromHighWater);
            curMmc3   = mmc3Floor;
            var mmc3Handler = SourceMapperRegistry.Get(4)!;
            foreach (var game in cfg.Games.Where(g => g.Mapper == 4)
                                          .Where(g => !g.HasChrRam || cfg.AllowChrRam))
            {
                if (!mmc3Note) { log?.Report($"NOTE  MMC3 games start at 0x{mmc3Floor:X6}"); mmc3Note = true; }
                int align    = mmc3Handler.PlacementAlignment(game, target);
                int physSize = mmc3Handler.PhysicalSize(game, target);
                if (curMmc3 % align != 0)
                    curMmc3 = ((curMmc3 + align - 1) / align) * align;
                int wb = (curMmc3 / WindowSize) * WindowSize;
                if (curMmc3 + physSize > wb + WindowSize) curMmc3 = wb + WindowSize;
                if (curMmc3 + physSize > romSize)
                { log?.Report($"SKIP  {game.FileName}: no MMC3 space (chip full)"); continue; }

                mmc3Handler.PlaceInFlash(game, rom, curMmc3, target);
                var info   = mmc3Handler.GetBankingInfo(game, curMmc3, target);
                var config = target.BuildConfigRecord(game, info, cfg);
                mmc3Handler.ApplySubmapper(config, cfg.Submapper);
                string name = CleanName(game.FileName);
                entries.Add(new Models.GameEntry(game, name, curMmc3, config));
                int effPrg = info.EffectivePrgSize;
                int padKB  = (effPrg - game.PrgSize) / 1024;
                log?.Report($"MMC3  {game.FileName,-32} @ 0x{curMmc3:X6}  cfg={Hex(config)}" +
                            (padKB > 0 ? $" [+{padKB}KB pad]" : ""));
                curMmc3 += physSize;
            }

            target.WriteGameTable(rom, entries, cfg);

            // Write LCD stub directly — avoids calling InitialiseFlash (which
            // would try to load the embedded kernel ROM and crash in test context).
            if (cfg.InitLcd)
                WriteLcdStubDirect(rom);

            if (cfg.Submapper >= 11 && cfg.Submapper <= 15)
                log?.Report($"NOTE  Submapper {cfg.Submapper}: hardware opcode bit-swap.");

            int mmc3Used = Math.Max(0, curMmc3 - mmc3Floor);
            log?.Report($"Done  {entries.Count} games  |  chip {cfg.ChipSizeMb} MB  |  " +
                        $"NROM overflow {Math.Max(0, nromHighWater - gameStart) / 1024} KB  |  " +
                        $"MMC3 {mmc3Used / 1024} KB");

            byte[] nesFile  = Mapper256Builder.MakeNes2Rom(rom, cfg.Submapper);
            byte[] unifFile = Mapper256Builder.MakeUnifRom(rom);

            if (cfg.PinSwap == 1)
            {
                ApplyPinSwap(rom);
                log?.Report("NOTE  Pin swap D1\u2194D9, D2\u2194D10 applied to .bin");
            }

            return new BuildResult
            {
                NorBinary = rom,
                NesFile   = nesFile,
                UnifFile  = unifFile,
                GameCount = entries.Count,
                NromUsed  = Math.Max(0, nromHighWater - gameStart),
                Mmc3Used  = mmc3Used,
            };
        }

        /// <summary>
        /// Write the LCD init stub bytes directly to the flash buffer at 0x06E000.
        /// This is used by the internal (test) Build() overload where we must NOT
        /// call VtxxOneBusTarget.InitialiseFlash() because that would try to load
        /// the embedded kernel ROM (which doesn't exist in test context).
        /// The stub bytes are identical to those in VtxxOneBusTarget.WriteLcdStub.
        /// </summary>
        private static void WriteLcdStubDirect(byte[] rom)
        {
            const int LcdStubAddr = 0x06E000;
            byte[] stub =
            {
                0xA9, 0x20, 0x8D, 0x18, 0x20,   // LDA #$20, STA $2018
                0xA9, 0x00, 0x8D, 0x1A, 0x20,   // LDA #$00, STA $201A
                0xA9, 0x00, 0x8D, 0x16, 0x20,   // LDA #$00, STA $2016
                0xA9, 0x02, 0x8D, 0x17, 0x20,   // LDA #$02, STA $2017
                0xA0, 0x04, 0x8C, 0x12, 0x20,   // LDY #$04, STY $2012
                0xC8,       0x8C, 0x13, 0x20,   // INY,      STY $2013
                0xC8,       0x8C, 0x14, 0x20,   // INY,      STY $2014
                0xC8,       0x8C, 0x15, 0x20,   // INY,      STY $2015
                0xA9, 0x1F, 0x8D, 0x3F, 0x41,   // LDA #$1F, STA $413F  (Type 3 backlight)
                0xA9, 0x0B, 0x8D, 0x38, 0x41,   // LDA #$0B, STA $4138
                0xA9, 0x0F, 0x8D, 0x39, 0x41,   // LDA #$0F, STA $4139
                0xA9, 0x0F, 0x8D, 0x2C, 0x41,   // LDA #$0F, STA $412C  (Type 1/2 backlight)
                0xA2, 0x20,                      // LDX #$20
                0xBD, 0x47, 0x80,               // LDA $8047,X  [loop]
                0x9D, 0x00, 0x04,               // STA $0400,X
                0xCA,                            // DEX
                0x10, 0xF9,                      // BPL loop
                0x4C, 0x00, 0x04,               // JMP $0400
                0xA9, 0x3C, 0x8D, 0x0A, 0x41,   // LDA #$3C, STA $410A  (RAM stub)
                0xA9, 0x00, 0x8D, 0x0B, 0x41,   // LDA #$00, STA $410B
                0xA9, 0x00, 0x8D, 0x00, 0x41,   // LDA #$00, STA $4100
                0xA9, 0x3C, 0x8D, 0x07, 0x41,   // LDA #$3C, STA $4107
                0xA9, 0x3D, 0x8D, 0x08, 0x41,   // LDA #$3D, STA $4108
                0xA9, 0x00, 0x8D, 0x09, 0x41,   // LDA #$00, STA $4109
                0x6C, 0xFC, 0xFF,               // JMP ($FFFC)
            };
            if (LcdStubAddr + stub.Length <= rom.Length)
                Array.Copy(stub, 0, rom, LcdStubAddr, stub.Length);
        }

        // ── Public helpers (used by tests / SpaceCalculator) ──────────────────

        /// <summary>Forwards to Mapper256Builder for backwards compatibility.</summary>
        public static int PrgAlignFor(int prgSize)
            => Mapper256Builder.PrgAlignFor(prgSize);

        /// <summary>Forwards to Mapper256Builder for backwards compatibility.</summary>
        public static int EffectivePrgForChr(int prgSize, int chrSize)
            => Mapper256Builder.EffectivePrgForChr(prgSize, chrSize);

        // ── How much space is available for games ─────────────────────────────
        public static long UsableBytes(int chipSizeMb)
        {
            long kernelFree  = KernelFreeRegions.Sum(r => (long)(r.End - r.Start));
            long chipBytes   = (long)chipSizeMb * 1024L * 1024L;
            long afterKernel = Math.Max(0L, chipBytes - KernelEnd);
            return kernelFree + afterKernel;
        }

        // ── Forwarded from VtxxOneBusTarget (kept for test/LCD backwards compat) ──
        private static void WriteLcdStub(byte[] rom)
        {
            // Stub bytes live in VtxxOneBusTarget — call via InitialiseFlash trick.
            // Only the LCD bytes are written; kernel copy is a no-op since rom is
            // already populated.
            var stub = new VtxxOneBusTarget();
            // Write LCD stub bytes directly by re-using the target's private method
            // via a minimal BuildConfig with InitLcd=true.
            // The target's InitialiseFlash would also re-copy the kernel — we avoid
            // that by calling the stub writer through the internal route.
            // Simplest correct approach: duplicate the 5-line stub call here.
            const int LcdStubAddr = 0x06E000;
            byte[] stubBytes =
            {
                0xA9, 0x20, 0x8D, 0x18, 0x20, 0xA9, 0x00, 0x8D, 0x1A, 0x20,
                0xA9, 0x00, 0x8D, 0x16, 0x20, 0xA9, 0x02, 0x8D, 0x17, 0x20,
                0xA0, 0x04, 0x8C, 0x12, 0x20, 0xC8, 0x8C, 0x13, 0x20,
                0xC8, 0x8C, 0x14, 0x20, 0xC8, 0x8C, 0x15, 0x20,
                0xA9, 0x1F, 0x8D, 0x3F, 0x41, 0xA9, 0x0B, 0x8D, 0x38, 0x41,
                0xA9, 0x0F, 0x8D, 0x39, 0x41, 0xA9, 0x0F, 0x8D, 0x2C, 0x41,
                0xA2, 0x20, 0xBD, 0x47, 0x80, 0x9D, 0x00, 0x04, 0xCA, 0x10, 0xF9,
                0x4C, 0x00, 0x04,
                0xA9, 0x3C, 0x8D, 0x0A, 0x41, 0xA9, 0x00, 0x8D, 0x0B, 0x41,
                0xA9, 0x00, 0x8D, 0x00, 0x41, 0xA9, 0x3C, 0x8D, 0x07, 0x41,
                0xA9, 0x3D, 0x8D, 0x08, 0x41, 0xA9, 0x00, 0x8D, 0x09, 0x41,
                0x6C, 0xFC, 0xFF,
            };
            if (LcdStubAddr + stubBytes.Length <= rom.Length)
                Array.Copy(stubBytes, 0, rom, LcdStubAddr, stubBytes.Length);
        }

        // LoadKernel now delegates to VtxxOneBusTarget.
        private static byte[] LoadKernel() =>
            VtxxOneBusTarget.LoadKernel();

        private static void WriteGameList(byte[] rom, List<string> names)
        {
            // Kept for the internal Build() overload — delegates to target constants.
            const int MenuStart    = VtxxOneBusTarget.MenuStart;
            const int MenuEnd      = VtxxOneBusTarget.MenuEnd;
            const int MenuHdrEnd   = VtxxOneBusTarget.MenuHeaderEnd;
            byte[] consts = { 0x14,0x00,0x08,0x08,0x08,0x04,0x04,0x08,0x01,0x00,0x00,0x00,0x00,0x00 };
            byte[] area   = new byte[MenuEnd - MenuStart + 1];
            Array.Fill(area, (byte)0xFF);
            area[0] = (byte)(names.Count & 0xFF);
            area[1] = (byte)((names.Count >> 8) & 0xFF);
            Array.Copy(consts, 0, area, 2, consts.Length);
            int off = MenuHdrEnd - MenuStart;
            foreach (string n in names)
            {
                byte[] nb = Encoding.ASCII.GetBytes(n);
                if (off + nb.Length + 1 > area.Length) break;
                Array.Copy(nb, 0, area, off, nb.Length);
                area[off + nb.Length] = 0;
                off += nb.Length + 1;
            }
            Array.Copy(area, 0, rom, MenuStart, area.Length);
        }
        //
        // For SUP 400-in-1 and similar VT03 handhelds that have their own hardware
        // startup code (TFT init etc.) in NOR 0x60000-0x6DFFF but need a bridge
        // at NOR 0x06E000 (where their original menu lived) that:
        //   1. Completes CHR banking setup ($2018/$201A/$2012-$2015)
        //   2. Enables backlight — both Type 1/2 ($412C=$0F) and
        //                          Type 3 ($413F=$1F, $4138=$0B, $4139=$0F)
        //   3. Copies a 33-byte bank-switch stub to CPU RAM $0400
        //   4. Jumps to RAM stub, which configures PRG banking and
        //      executes JMP($FFFC) → our RESET vector at CPU $E000 = NOR 0x07E000
        //
        // The console's own startup code (TFT init) is assumed to be in
        // NOR 0x60000-0x6DFFF, which we leave as 0xFF (the user must flash
        // their original kernel's startup block there separately if needed).
        //
        // CPU mapping assumed when stub runs (after their HW init):
        //   $8000-$9FFF = NOR 0x06E000  ($4107 = 0x37 → bank 0x37)
        //   so stub runs with CPU base $8000 = NOR 0x06E000
        // ── Pin swap (D1↔D9, D2↔D10) ─────────────────────────────────────────
        private static bool GetBit(byte b, int bit) => ((b >> bit) & 0x01) == 1;
        private static void SetBit(ref byte b, int bit, bool value)
            => b = (byte)(value ? b | (1 << bit) : b & ~(1 << bit));

        /// <summary>
        /// Swap bits 1 and 2 between each adjacent even/odd byte pair in the .bin.
        /// Compensates for boards where D1↔D9 and D2↔D10 are physically crossed.
        /// Applied AFTER generating .nes/.unf (those files need unswapped data).
        /// </summary>
        public static void ApplyPinSwap(byte[] data)
        {
            for (int i = 0; i < data.Length / 2; i++)
            {
                byte b1 = data[i * 2];
                byte b2 = data[i * 2 + 1];
                byte tmp = b1;
                SetBit(ref b1, 1, GetBit(b2, 1));
                SetBit(ref b1, 2, GetBit(b2, 2));
                SetBit(ref b2, 1, GetBit(tmp, 1));
                SetBit(ref b2, 2, GetBit(tmp, 2));
                data[i * 2]     = b1;
                data[i * 2 + 1] = b2;
            }
        }

        // ── Private helpers ───────────────────────────────────────────────────
        private static string CleanName(string fn)
        {
            string n = Path.GetFileNameWithoutExtension(fn);
            int p = n.IndexOf('(');
            if (p >= 0) n = n.Substring(0, p);
            n = n.ToUpperInvariant().Trim();
            var sb = new StringBuilder();
            foreach (char c in n)
                if (char.IsLetterOrDigit(c) || c == ' ') sb.Append(c);
            string r = string.Join(" ", sb.ToString()
                           .Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            return r.Length > 20 ? r.Substring(0, 20) : r;
        }

        private static string Hex(byte[] b) =>
            BitConverter.ToString(b).Replace("-", "").ToLower();
    }

        public static class Mapper256Builder
        {
            // ── Delegates to OneBusBanking (no longer duplicate) ──────────────────

            /// <summary>Alignment for an MMC3 game.</summary>
            public static int PrgAlignFor(int prgSize)
                => Hardware.OneBusBanking.PrgAlignFor(prgSize);

            /// <summary>Effective PRG size (PRG padded for CHR placement).</summary>
            public static int EffectivePrgForChr(int prgSize, int chrSize)
                => Hardware.OneBusBanking.EffectivePrgForChr(prgSize, chrSize);

            /// <summary>PS field, InnerMask, and placement alignment for a given PRG size.</summary>
            public static (byte ps, byte innerMask, int align) PsForPrg(int prgSize)
                => Hardware.OneBusBanking.PsForPrg(prgSize);

            // ── Delegates to NesFileWriter ────────────────────────────────────────

            /// <summary>Wraps the flash binary in a NES 2.0 header for mapper 256.</summary>
            public static byte[] MakeNes2Rom(byte[] prg, int submapper = 0)
                => Output.NesFileWriter.MakeNes2Rom(prg, submapper);

            /// <summary>Produces a 16-byte NES 2.0 header for mapper 256.</summary>
            public static byte[] MakeNes2Header(int prg16kBanks, int submapper = 0)
                => Output.NesFileWriter.MakeNes2Header(prg16kBanks, submapper);

            /// <summary>Wraps the flash binary in a UNIF file (board = UNL-OneBus).</summary>
            public static byte[] MakeUnifRom(byte[] prg)
                => Output.NesFileWriter.MakeUnifRom(prg);

            /// <summary>Multi-line NES 2.0 header description for the UI.</summary>
            public static string DescribeHeader(int submapper, int chipMb)
                => Output.NesFileWriter.DescribeHeader(submapper, chipMb);

            /// <summary>
            /// Kept for backward compatibility — submapper 2 byte-swap.
            /// New code should use Mmc3Handler.ApplySubmapper() instead.
            /// </summary>
            public static void ApplySubmapperToMmc3Config(byte[] cfg, int submapper)
            {
                if (submapper == 2)
                    (cfg[4], cfg[5]) = (cfg[5], cfg[4]);
            }

            // ── Submapper 11-15: CPU opcode bit-swap ──────────────────────────────
            //
            // These consoles have hardware that swaps specific bits of every byte
            // fetched as a CPU opcode from flash. Games must have been compiled with
            // the swap pre-applied. These methods let callers pre-scramble game data
            // for consoles that require it.
            //
            // Bit-swapping is self-inverse: applying it twice restores the original.

            /// <summary>Pre-scramble a single byte for the given submapper (11-15).</summary>
            public static byte ScrambleByte(byte b, int submapper)
            {
                switch (submapper)
                {
                    case 11:  // D7↔D6, D2↔D1, D5↔D4
                        return (byte)(
                            ((b & 0x80) >> 1) | ((b & 0x40) << 1) |
                            ((b & 0x20) >> 1) | ((b & 0x10) << 1) |
                            ((b & 0x08)     ) |
                            ((b & 0x04) >> 1) | ((b & 0x02) << 1) |
                            ((b & 0x01)     ));
                    case 12:  // D7↔D6, D2↔D1
                        return (byte)(
                            ((b & 0x80) >> 1) | ((b & 0x40) << 1) |
                            ((b & 0x38)     ) |
                            ((b & 0x04) >> 1) | ((b & 0x02) << 1) |
                            ((b & 0x01)     ));
                    case 13:  // D4↔D1
                        return (byte)(
                            ((b & 0xE0)     ) |
                            ((b & 0x10) >> 3) | ((b & 0x0C)     ) |
                            ((b & 0x02) << 3) | ((b & 0x01)     ));
                    case 14:  // D7↔D6
                        return (byte)(
                            ((b & 0x80) >> 1) | ((b & 0x40) << 1) |
                            ((b & 0x3F)     ));
                    case 15:  // D6↔D5
                        return (byte)(
                            ((b & 0x80)     ) |
                            ((b & 0x40) >> 1) | ((b & 0x20) << 1) |
                            ((b & 0x1F)     ));
                    default:
                        return b;
                }
            }

            /// <summary>Pre-scramble every byte in the ROM for submappers 11-15.</summary>
            public static void ScrambleRom(byte[] rom, int submapper)
            {
                if (submapper < 11 || submapper > 15) return;
                for (int i = 0; i < rom.Length; i++)
                    rom[i] = ScrambleByte(rom[i], submapper);
            }

            // ── Config builders (kept for tests that call them directly) ──────────

            /// <summary>Build the 9-byte VTxx config record for an NROM game.</summary>
            public static byte[] BuildNromConfig(
                int romOffset, int prgSize, int chrSize, int mapper, bool vertical)
            {
                // Delegates to VtxxOneBusTarget's inline config builder.
                // Create a synthetic GameBankingInfo and call the target.
                var target = new Targets.VtxxOneBusTarget();
                var info   = new Models.GameBankingInfo
                {
                    PrgFlashOffset   = romOffset,
                    ChrFlashOffset   = romOffset + prgSize,
                    EffectivePrgSize = prgSize,
                    NativePrgSize    = prgSize,
                    ChrSize          = chrSize,
                    Vertical         = vertical,
                    SourceMapper     = 0,
                };
                // Create a minimal NesRom-like object — reuse existing build path.
                // Simplest: call the logic inline (copy from VtxxOneBusTarget).
                return BuildNromConfigInline(romOffset, prgSize, chrSize, mapper, vertical);
            }

            /// <summary>Build the 9-byte VTxx config record for an MMC3 game.</summary>
            public static byte[] BuildMmc3Config(int romOffset, int prgSize, int chrSize)
                => BuildMmc3ConfigInline(romOffset, prgSize, chrSize);

            // ── Private inline config builders (same logic as VtxxOneBusTarget) ───

            private static byte[] BuildNromConfigInline(
                int romOffset, int prgSize, int chrSize, int mapper, bool vertical)
            {
                int addr  = romOffset + prgSize + 0x1800;
                byte v4100 = (byte)((addr >> 21) & 0x0F);
                byte r2018 = (byte)(((addr & 0x1FFFFF) / 0x40000) << 4);
                byte r201A = (byte)((addr >> 10) & 0xFF);
                byte pa    = (byte)((romOffset >> 21) & 0x0F);
                byte r4100 = (byte)((pa << 4) | (v4100 & 0x0F));
                byte b4    = Bnk(romOffset);
                byte b5    = Bnk(romOffset + 0x2000);
                byte b6    = Bnk(romOffset + prgSize - 0x4000);
                byte b7    = Bnk(romOffset + prgSize - 0x2000);
                byte mir   = vertical ? (byte)0x00 : (byte)0x01;
                byte mode  = prgSize >= 32768 ? (byte)0x04 : (byte)0x05;
                return new byte[] { r4100, r2018, r201A, mode, b4, b5, b6, b7, mir };
            }

            private static byte[] BuildMmc3ConfigInline(
                int romOffset, int prgSize, int chrSize)
            {
                var (ps, _, _)           = Hardware.OneBusBanking.PsForPrg(prgSize);
                int  effPrg              = Hardware.OneBusBanking.EffectivePrgForChr(prgSize, chrSize);
                int  chrAddr             = romOffset + effPrg;
                byte va                  = (byte)((chrAddr >> 21) & 0x0F);
                byte r2018               = (byte)((chrAddr >> 14) & 0x70);
                var (vb0s, chrInnerMask) = Hardware.OneBusBanking.Vb0sForChr(chrSize);
                byte antiMask            = (byte)(~chrInnerMask & 0xFF);
                byte middle              = (byte)(((chrAddr >> 10) & 0xFF) & antiMask);
                byte r201A               = (byte)(vb0s | middle);
                byte pa                  = (byte)((romOffset >> 21) & 0x0F);
                byte r4100               = (byte)((pa << 4) | va);
                const int Window         = 0x200000;
                int  wb                  = (romOffset / Window) * Window;
                int  oin                 = romOffset - wb;
                byte b4 = Bnk(oin), b5 = Bnk(oin + 0x2000);
                byte b7 = Bnk(oin + prgSize - 8192);
                byte b6 = Bnk(oin + prgSize - 16384);
                byte f  = chrSize > 128 * 1024 ? (byte)0x80 : (byte)0x00;
                return new byte[] { r4100, r2018, r201A, ps, b4, b5, b6, b7, f };
            }

            private static byte Bnk(int addr) => (byte)((addr / 8192) & 0xFF);
        }

    // ── Build result ──────────────────────────────────────────────────────────
    public class BuildResult
    {
        public byte[] NorBinary { get; set; } = Array.Empty<byte>();
        public byte[] NesFile   { get; set; } = Array.Empty<byte>();
        public byte[] UnifFile  { get; set; } = Array.Empty<byte>();
        public int    GameCount { get; set; }
        public int    NromUsed  { get; set; }
        public int    Mmc3Used  { get; set; }
    }
}
