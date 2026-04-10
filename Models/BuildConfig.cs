using System.Collections.Generic;
using VT03Builder.Services.Targets;

namespace VT03Builder.Models
{
    public class BuildConfig
    {
        /// <summary>
        /// Hardware target identifier — key into TargetRegistry.
        /// Defaults to VTxx OneBus. Future values: "coolboy", "coolgirl", etc.
        /// </summary>
        public string TargetId { get; set; } = TargetRegistry.DefaultId;

        /// <summary>NOR chip size in megabytes (e.g. 8 = 8 MB = 64 Mbit).</summary>
        public int          ChipSizeMb  { get; set; } = 8;
        public bool         AllowChrRam { get; set; } = false;
        public bool         InitLcd     { get; set; } = false;
        public int          PinSwap     { get; set; } = 0;   // 0=None, 1=D1↔D9/D2↔D10
        public string       OutputPath  { get; set; } = "multicart";
        public bool         GenerateNes { get; set; } = true;
        public List<NesRom> Games       { get; set; } = new List<NesRom>();

        /// <summary>
        /// NES 2.0 mapper number — kept for backward compatibility with tests.
        /// Derived from the selected target; do not set manually.
        /// </summary>
        public int Mapper => TargetRegistry.Get(TargetId)?.OutputMapper ?? 256;

        /// <summary>
        /// NES 2.0 submapper number for the selected target.
        /// Meaning depends on the target (e.g. for VTxx: 0=Normal, 2=Power Joy, …).
        /// </summary>
        public int Submapper { get; set; } = 0;

        public long ChipSizeBytes => (long)ChipSizeMb * 1024L * 1024L;
    }
}

