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
        // Kernel free regions available for NROM games (480 KB total)
        private const long KernelFreeTotal = (0x040000 - 0x000000) + (0x079000 - 0x041000);
        private const long KernelEnd       = 0x080000L;

        public static SpaceInfo Calculate(BuildConfig cfg)
        {
            long usedBytes = 0;

            foreach (var g in cfg.Games)
            {
                if (!g.IsValid) continue;
                if (g.HasChrRam && g.Mapper == 4 && !cfg.AllowChrRam) continue;
                int effPrg   = g.Mapper == 4 ? RomBuilder.EffectivePrgForChr(g.PrgSize, g.ChrSize) : g.PrgSize;
                int gameSize = effPrg + g.ChrSize;
                int align    = RomBuilder.PrgAlignFor(g.PrgSize);
                usedBytes += ((long)(gameSize + align - 1) / align) * align;
            }

            // Usable = kernel free gaps (480 KB) + everything after kernel end
            long chipBytes   = (long)cfg.ChipSizeMb * 1024L * 1024L;
            long afterKernel = Math.Max(0L, chipBytes - KernelEnd);

            return new SpaceInfo
            {
                UsableBytes = KernelFreeTotal + afterKernel,
                UsedBytes   = usedBytes,
            };
        }
    }
}
