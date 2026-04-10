using System.Collections.Generic;
using VT03Builder.Models;

namespace VT03Builder.Services.Targets
{
    /// <summary>
    /// Knows everything about one physical multicart hardware target
    /// (e.g. VTxx OneBus, CoolBoy, CoolGirl).
    ///
    /// There is one implementation per supported cartridge type.
    /// Targets are registered in TargetRegistry and retrieved by Id.
    ///
    /// The target is responsible for:
    ///   1. Describing its hardware limits (flash size, config record size, …)
    ///   2. Placing its kernel / menu ROM into the flash buffer
    ///   3. Translating hardware-neutral GameBankingInfo into its own
    ///      target-specific config record byte sequence
    ///   4. Writing the game name table and config table to flash
    ///   5. Producing the final output files (.bin / .nes / .unf)
    /// </summary>
    public interface IHardwareTarget
    {
        // ── Identity ──────────────────────────────────────────────────────────

        /// <summary>Stable key used in BuildConfig.TargetId ("vtxx", "coolboy", …).</summary>
        string Id          { get; }

        /// <summary>Human-readable name shown in the UI target dropdown.</summary>
        string DisplayName { get; }

        /// <summary>NES 2.0 output mapper number (256, 268, 342, …).</summary>
        int OutputMapper   { get; }

        // ── Hardware limits ───────────────────────────────────────────────────

        /// <summary>Maximum supported flash chip size in bytes.</summary>
        long MaxFlashBytes { get; }

        /// <summary>Number of bytes in each game's config record.</summary>
        int ConfigRecordBytes { get; }

        /// <summary>
        /// True if the hardware always uses CHR-RAM (CoolBoy, CoolGirl).
        /// False if CHR-ROM is possible (VTxx OneBus).
        /// </summary>
        bool AlwaysChrRam { get; }

        /// <summary>
        /// True if the target has a reserved kernel area at the start of flash
        /// that the packer must not place game data in (VTxx OneBus = true).
        /// </summary>
        bool HasKernelArea { get; }

        /// <summary>
        /// Byte count of the reserved kernel area.
        /// 0 if HasKernelArea is false.
        /// </summary>
        int KernelAreaSize { get; }

        // ── Supported source mappers ───────────────────────────────────────────

        /// <summary>
        /// NES mapper numbers that this hardware target can run.
        /// Used by the UI to flag incompatible games before the build starts.
        /// </summary>
        IReadOnlyList<int> SupportedSourceMappers { get; }

        // ── Submappers ────────────────────────────────────────────────────────

        /// <summary>All submapper variants for this hardware, shown in the UI.</summary>
        IReadOnlyList<SubmapperInfo> Submappers { get; }

        // ── Build steps ───────────────────────────────────────────────────────

        /// <summary>
        /// Initialise the flash buffer: copy the target's kernel / menu ROM
        /// and any fixed data that must be present before games are packed.
        /// Called once at the start of Build() before any game is placed.
        /// </summary>
        void InitialiseFlash(byte[] flash, BuildConfig cfg);

        /// <summary>
        /// Translate a hardware-neutral GameBankingInfo into this target's
        /// own config record byte format (9 bytes for VTxx, 8 bytes for CoolGirl, …).
        /// </summary>
        byte[] BuildConfigRecord(NesRom game, GameBankingInfo info, BuildConfig cfg);

        /// <summary>
        /// Write the game name table and config record table into the flash buffer.
        /// Called once after all games have been placed.
        /// </summary>
        void WriteGameTable(byte[] flash, IReadOnlyList<GameEntry> entries,
                            BuildConfig cfg);

        /// <summary>
        /// Produce the final output files (NorBinary, NesFile, UnifFile).
        /// The flash buffer has been fully populated at this point.
        /// </summary>
        BuildResult BuildOutputFiles(byte[] flash, BuildConfig cfg);
    }
}
