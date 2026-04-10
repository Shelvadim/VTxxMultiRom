using System;
using System.Collections.Generic;
using System.Linq;
using VT03Builder.Models;
using VT03Builder.Services.Hardware;
using VT03Builder.Services.Targets;

namespace VT03Builder.Services.SourceMappers
{
    /// <summary>
    /// Handles NES mapper 0 (NROM) games.
    ///
    /// NROM games have fixed PRG and CHR banks — no runtime bank switching.
    /// PRG is 16 KB or 32 KB. CHR-ROM is typically 8 KB.
    ///
    /// On VTxx OneBus: packed into kernel free gaps first, then overflow area.
    /// On other targets: packed linearly after the kernel area.
    /// </summary>
    public sealed class NromHandler : ISourceMapperHandler
    {
        public IReadOnlyList<int> SupportedMappers { get; } = new[] { 0 };
        public string DisplayName => "NROM";

        // ── Compatibility ─────────────────────────────────────────────────────

        public string? CompatibilityWarning(NesRom rom, IHardwareTarget target)
        {
            // NROM is compatible with all current targets — no known issues.
            return null;
        }

        public bool IsCompatible(NesRom rom, IHardwareTarget target) =>
            target.SupportedSourceMappers.Contains(0);

        // ── Flash placement ───────────────────────────────────────────────────

        public int PlacementAlignment(NesRom rom, IHardwareTarget target) =>
            OneBusBanking.PrgAlignFor(rom.PrgSize);

        public int PhysicalSize(NesRom rom, IHardwareTarget target) =>
            rom.PrgSize + rom.ChrSize;

        // ── Data + config ─────────────────────────────────────────────────────

        public void PlaceInFlash(NesRom rom, byte[] flash, int norOffset,
                                 IHardwareTarget target)
        {
            Array.Copy(rom.RawData, 0, flash, norOffset, rom.PrgSize + rom.ChrSize);
        }

        public GameBankingInfo GetBankingInfo(NesRom rom, int norOffset,
                                              IHardwareTarget target) =>
            new GameBankingInfo
            {
                PrgFlashOffset   = norOffset,
                ChrFlashOffset   = norOffset + rom.PrgSize,
                EffectivePrgSize = rom.PrgSize,
                NativePrgSize    = rom.PrgSize,
                ChrSize          = rom.ChrSize,
                Vertical         = rom.Vertical,
                SourceMapper     = 0,
            };

        public void ApplySubmapper(byte[] configRecord, int submapper)
        {
            // NROM is unaffected by any submapper variant.
        }
    }
}
