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
        // Kernel gap regions available for NROM games (480 KB total)
        private static readonly (int Start, int End)[] KernelFreeRegions =
        {
            (0x000000, 0x040000),  // 256 KB
            (0x041000, 0x079000),  // 224 KB
        };
        private const long KernelFreeTotal = (0x040000 - 0x000000) + (0x079000 - 0x041000);  // 480 KB
        private const long NromOverflow    = 0x200000 - 0x080000;  // 1.5 MB after kernel
        private const long Mmc3Start       = 0x200000;

        public static SpaceInfo Calculate(BuildConfig cfg)
        {
            long usedNrom = 0;
            long usedMmc3 = 0;

            foreach (var g in cfg.Games)
            {
                if (!g.IsValid) continue;
                int effPrg   = g.Mapper == 4 ? RomBuilder.EffectivePrgForChr(g.PrgSize, g.ChrSize) : g.PrgSize;
                int gameSize = effPrg + g.ChrSize;
                int align    = g.Mapper != 4 ? RomBuilder.PrgAlignFor(g.PrgSize) : RomBuilder.PrgAlignFor(g.PrgSize);
                long aligned = ((gameSize + align - 1) / align) * align;
                if (g.Mapper == 4)
                    usedMmc3 += aligned;
                else
                    usedNrom += aligned;
            }

            // NROM usable = kernel gaps (480 KB) + overflow region (1.5 MB)
            long nromUsable = KernelFreeTotal + NromOverflow;
            // MMC3 usable = unbounded (MMC3 always at 0x200000+, no chip size limit)
            long mmc3Usable = 8L * 1024 * 1024 - Mmc3Start;  // up to 8 MB chip max

            return new SpaceInfo
            {
                UsableBytes = nromUsable + mmc3Usable,
                UsedBytes   = usedNrom + usedMmc3,
            };
        }
    }
}
