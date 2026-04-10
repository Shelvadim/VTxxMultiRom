namespace VT03Builder.Models
{
    /// <summary>
    /// Hardware-neutral description of a game's placement in flash.
    /// Produced by ISourceMapperHandler.GetBankingInfo().
    /// Consumed by IHardwareTarget.BuildConfigRecord() to produce
    /// the target-specific byte sequence the menu loader writes.
    /// </summary>
    public class GameBankingInfo
    {
        /// <summary>Byte offset in flash where PRG data begins.</summary>
        public int PrgFlashOffset  { get; init; }

        /// <summary>Byte offset in flash where CHR data begins (= PrgFlashOffset + EffectivePrg).</summary>
        public int ChrFlashOffset  { get; init; }

        /// <summary>PRG size as stored in flash (may be padded for CHR alignment).</summary>
        public int EffectivePrgSize { get; init; }

        /// <summary>Original PRG size from the iNES file (before any padding).</summary>
        public int NativePrgSize   { get; init; }

        /// <summary>CHR data size in bytes. 0 = no CHR-ROM (CHR-RAM game).</summary>
        public int ChrSize         { get; init; }

        /// <summary>True = vertical mirroring, false = horizontal.</summary>
        public bool Vertical       { get; init; }

        /// <summary>NES source mapper number (0=NROM, 4=MMC3, etc.).</summary>
        public int SourceMapper    { get; init; }
    }
}
