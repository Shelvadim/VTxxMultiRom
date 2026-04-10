using System;
using System.Linq;
using VT03Builder.Models;
using VT03Builder.Services.SourceMappers;
using VT03Builder.Services.Targets;

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
        public static SpaceInfo Calculate(BuildConfig cfg)
        {
            var target = TargetRegistry.Get(cfg.TargetId)
                      ?? TargetRegistry.GetRequired(TargetRegistry.DefaultId);

            long usedBytes = 0;

            foreach (var g in cfg.Games)
            {
                if (!g.IsValid) continue;

                var handler = SourceMapperRegistry.Get(g.Mapper);
                if (handler == null) continue;

                // Skip CHR-RAM MMC3 unless the target always uses CHR-RAM or user allows it
                if (g.HasChrRam && !target.AlwaysChrRam && !cfg.AllowChrRam) continue;

                // Use handler to compute physical size (includes CHR padding)
                int  phys  = handler.PhysicalSize(g, target);
                int  align = handler.PlacementAlignment(g, target);
                usedBytes += ((long)(phys + align - 1) / align) * align;
            }

            // Usable space depends on whether the target has a reserved kernel area.
            long chipBytes = cfg.ChipSizeBytes;
            long usable;

            if (target.HasKernelArea)
            {
                // VTxx OneBus: kernel gaps (480 KB) + everything after kernel end
                long kernelFree = VtxxOneBusTarget.KernelFreeRegions
                                      .Sum(r => (long)(r.End - r.Start));
                long afterKernel = Math.Max(0L, chipBytes - target.KernelAreaSize);
                usable = kernelFree + afterKernel;
            }
            else
            {
                // CoolBoy / other targets: usable = everything after the reserved
                // area (menu window etc.). KernelAreaSize = 0x080000 for CoolBoy.
                long afterReserved = Math.Max(0L, chipBytes - target.KernelAreaSize);
                usable = Math.Min(afterReserved, target.MaxFlashBytes);
            }

            return new SpaceInfo { UsableBytes = usable, UsedBytes = usedBytes };
        }
    }
}
