using System.Collections.Generic;
using System.Linq;
using VT03Builder.Models;
using VT03Builder.Services.Targets;

namespace VT03Builder.Services.SourceMappers
{
    /// <summary>
    /// Knows everything about one NES source mapper (e.g. NROM, MMC3).
    ///
    /// There is one implementation per NES mapper number that the tool supports.
    /// Handlers are registered in SourceMapperRegistry and retrieved by mapper number.
    ///
    /// The handler is responsible for:
    ///   1. Reporting whether a game is compatible with a given hardware target
    ///   2. Computing flash placement parameters (alignment, physical size)
    ///   3. Copying game data into the flash buffer at the chosen offset
    ///   4. Producing a hardware-neutral GameBankingInfo for the target to
    ///      translate into its own config record byte format
    ///   5. Applying any submapper-specific post-processing to the config record
    /// </summary>
    public interface ISourceMapperHandler
    {
        /// <summary>NES mapper numbers this handler accepts (usually just one).</summary>
        IReadOnlyList<int> SupportedMappers { get; }

        /// <summary>Short label shown in the game list (e.g. "NROM", "MMC3").</summary>
        string DisplayName { get; }

        // ── Compatibility ─────────────────────────────────────────────────────

        /// <summary>
        /// Returns null if the game is fully compatible with the target,
        /// or a short human-readable warning string otherwise.
        /// The caller decides whether to reject or warn; the handler only judges.
        /// </summary>
        string? CompatibilityWarning(NesRom rom, IHardwareTarget target);

        /// <summary>True if the game can be built for the target without errors.</summary>
        bool IsCompatible(NesRom rom, IHardwareTarget target);

        // ── Flash placement ───────────────────────────────────────────────────

        /// <summary>
        /// Required alignment in bytes for this game's PRG data in flash.
        /// The packer will round up the current write pointer to this boundary.
        /// </summary>
        int PlacementAlignment(NesRom rom, IHardwareTarget target);

        /// <summary>
        /// Total bytes this game will occupy in flash, including PRG, CHR, and
        /// any padding added between them for alignment.
        /// Used by the packer to check that a candidate slot is large enough.
        /// </summary>
        int PhysicalSize(NesRom rom, IHardwareTarget target);

        // ── Data + config ─────────────────────────────────────────────────────

        /// <summary>
        /// Copy the game's PRG and CHR bytes into flash[] at norOffset.
        /// The handler decides exactly where inside that slot CHR goes
        /// (after any alignment padding following PRG).
        /// </summary>
        void PlaceInFlash(NesRom rom, byte[] flash, int norOffset,
                          IHardwareTarget target);

        /// <summary>
        /// Return a hardware-neutral description of how this game's banks are laid
        /// out in flash.  The IHardwareTarget will translate this into its own
        /// target-specific config record byte format.
        /// </summary>
        GameBankingInfo GetBankingInfo(NesRom rom, int norOffset,
                                       IHardwareTarget target);

        /// <summary>
        /// Apply any submapper-specific fixups to the already-built config record.
        /// For most handlers and most submappers this is a no-op.
        /// Example: MMC3 submapper 2 swaps two PRG bank bytes.
        /// </summary>
        void ApplySubmapper(byte[] configRecord, int submapper);
    }
}
