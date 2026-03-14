using System;
using VT03Builder.Models;

namespace VT03Builder.Services
{
    public class SpaceInfo
    {
        public long   UsableBytes { get; set; }
        public long   UsedBytes   { get; set; }
        public bool   Overflow    => UsedBytes > UsableBytes;
        public double Percent     => UsableBytes > 0
                                     ? Math.Min(100.0, (double)UsedBytes / UsableBytes * 100.0)
                                     : 0;
        public string Summary     => $"{Fmt(UsedBytes)} / {Fmt(UsableBytes)}  ({Percent:F1}%)";

        private static string Fmt(long b) =>
            b >= 1048576 ? $"{b / 1048576.0:F2} MB" : $"{b / 1024} KB";
    }

    public static class SpaceCalculator
    {
        // Kernel occupies the first 512 KB; game data begins at 0x080000.
        private const long KernelSize = 0x080000L;

        public static SpaceInfo Calculate(BuildConfig cfg)
        {
            long usedBytes = 0;

            foreach (var g in cfg.Games)
            {
                if (!g.IsValid) continue;
                int effPrg   = g.Mapper == 4 ? RomBuilder.EffectivePrgForChr(g.PrgSize, g.ChrSize) : g.PrgSize;
                int gameSize = effPrg + g.ChrSize;
                // NROM/CNROM: align to 16 KB. MMC3: align to PRG inner-window size.
                int align = g.Mapper != 4 ? 0x4000 : RomBuilder.PrgAlignFor(g.PrgSize);
                usedBytes += ((gameSize + align - 1) / align) * align;
            }

            long chipTotal  = cfg.ChipSizeBytes;
            long usable     = chipTotal - KernelSize;

            return new SpaceInfo
            {
                UsableBytes = usable,
                UsedBytes   = usedBytes
            };
        }
    }
}
