using System;
using System.Collections.Generic;
using System.Linq;
using VT03Builder.Models;
using VT03Builder.Services.Hardware;
using VT03Builder.Services.Targets;

namespace VT03Builder.Services.SourceMappers
{
    /// <summary>
    /// Handles NES mapper 4 (MMC3) games.
    ///
    /// MMC3 is the most common advanced NES mapper: 512 KB PRG max per inner window,
    /// separate CHR banking, IRQ counter for raster effects.
    ///
    /// On VTxx OneBus: window-aligned linear packing after NROM high-water mark.
    /// CHR must be placed immediately after (padded) PRG so the CHR inner bank
    /// index 0 maps to the start of CHR data.
    ///
    /// Submapper variants (mapper 256 submapper field):
    ///   0  Normal          — standard OneBus MMC3 register layout
    ///   2  Power Joy       — PQ0/PQ1 bytes swapped ($4107/$4108 order reversed)
    ///   3  Zechess/Hummer  — (to be documented)
    ///   4  Sports Game     — (to be documented)
    ///   5  Waixing VT02    — (to be documented)
    ///  11  Vibes           — hardware opcode D7↔D6,D2↔D1,D5↔D4 swap
    ///  12  Cheertone       — hardware opcode D7↔D6,D2↔D1 swap
    ///  13  Cube Tech       — hardware opcode D4↔D1 swap
    ///  14  Karaoto         — hardware opcode D7↔D6 swap
    ///  15  Jungletac       — hardware opcode D6↔D5 swap
    ///
    /// Submappers 11-15 use hardware opcode bit-swapping. The hardware unscrambles
    /// only CPU opcode fetches, not data reads. Standard NES ROMs cannot be adapted
    /// by post-processing the whole ROM — they require purpose-built code.
    /// </summary>
    public sealed class Mmc3Handler : ISourceMapperHandler
    {
        public IReadOnlyList<int> SupportedMappers { get; } = new[] { 4 };
        public string DisplayName => "MMC3";

        // ── Compatibility ─────────────────────────────────────────────────────

        public string? CompatibilityWarning(NesRom rom, IHardwareTarget target)
        {
            if (!target.SupportedSourceMappers.Contains(4))
                return "MMC3 not supported by this hardware target";

            if (rom.HasChrRam && !target.AlwaysChrRam)
                return "CHR-RAM — will grey screen (hardware needs CHR-ROM)";

            if (rom.PrgSize > 256 * 1024)
                return "PRG > 256 KB — may grey screen if game uses MMC3 IRQ";

            return null;
        }

        public bool IsCompatible(NesRom rom, IHardwareTarget target)
        {
            if (!target.SupportedSourceMappers.Contains(4)) return false;
            if (rom.HasChrRam && !target.AlwaysChrRam)      return false;
            return true;
        }

        // ── Flash placement ───────────────────────────────────────────────────

        public int PlacementAlignment(NesRom rom, IHardwareTarget target) =>
            OneBusBanking.PrgAlignFor(rom.PrgSize);

        public int PhysicalSize(NesRom rom, IHardwareTarget target)
        {
            int effPrg = OneBusBanking.EffectivePrgForChr(rom.PrgSize, rom.ChrSize);
            return effPrg + rom.ChrSize;
        }

        // ── Data + config ─────────────────────────────────────────────────────

        public void PlaceInFlash(NesRom rom, byte[] flash, int norOffset,
                                 IHardwareTarget target)
        {
            int effPrg = OneBusBanking.EffectivePrgForChr(rom.PrgSize, rom.ChrSize);
            // PRG at norOffset, CHR after the effective-PRG padding gap.
            System.Array.Copy(rom.PrgData, 0, flash, norOffset, rom.PrgSize);
            System.Array.Copy(rom.ChrData, 0, flash, norOffset + effPrg, rom.ChrSize);
        }

        public GameBankingInfo GetBankingInfo(NesRom rom, int norOffset,
                                              IHardwareTarget target)
        {
            int effPrg = OneBusBanking.EffectivePrgForChr(rom.PrgSize, rom.ChrSize);
            return new GameBankingInfo
            {
                PrgFlashOffset   = norOffset,
                ChrFlashOffset   = norOffset + effPrg,
                EffectivePrgSize = effPrg,
                NativePrgSize    = rom.PrgSize,
                ChrSize          = rom.ChrSize,
                Vertical         = rom.Vertical,
                SourceMapper     = 4,
            };
        }

        /// <summary>
        /// Apply submapper-specific fixups to a VTxx OneBus 9-byte MMC3 config.
        ///
        /// Submapper 2 (Power Joy Supermax):
        ///   Swap PQ0 and PQ1 bytes (config[4] and config[5]).
        ///   The hardware reads PRG bank registers in reversed order.
        ///
        /// Submappers 11-15 (opcode hardware encryption):
        ///   No change to the config record itself — the encryption is a property
        ///   of the console's CPU fetch hardware, not the bank register values.
        ///   The submapper number is written to the .nes header for emulator use.
        ///   Standard game ROMs cannot be adapted by ROM post-processing.
        ///
        /// Other submappers (3, 4, 5):
        ///   Register value reordering to be added when hardware documentation
        ///   and test cartridges confirm the exact byte layout.
        /// </summary>
        public void ApplySubmapper(byte[] configRecord, int submapper)
        {
            switch (submapper)
            {
                case 0:
                    // Normal — no change.
                    break;

                case 2:
                    // Power Joy Supermax: swap PQ0/PQ1 (bytes 4 and 5).
                    (configRecord[4], configRecord[5]) = (configRecord[5], configRecord[4]);
                    break;

                case 3:
                    // Zechess / Hummer Team: documented as register reorder.
                    // TODO: confirm exact byte layout with hardware test.
                    break;

                case 4:
                    // Sports Game 69-in-1: register reorder variant.
                    // TODO: confirm exact byte layout with hardware test.
                    break;

                case 5:
                    // Waixing VT02: register reorder variant.
                    // TODO: confirm exact byte layout with hardware test.
                    break;

                case 11:
                case 12:
                case 13:
                case 14:
                case 15:
                    // Hardware opcode encryption submappers.
                    // The config record registers are at standard addresses — no byte change.
                    // The ROM itself must have been compiled for this console type;
                    // it cannot be adapted by post-processing here.
                    break;

                // Any unknown submapper: silently leave config record unchanged.
            }
        }
    }
}
