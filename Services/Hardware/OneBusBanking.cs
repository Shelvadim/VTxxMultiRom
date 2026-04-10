namespace VT03Builder.Services.Hardware
{
    /// <summary>
    /// VTxx OneBus PRG and CHR bank register math.
    ///
    /// These calculations are specific to the VTxx OneBus ($41xx register) hardware.
    /// They are used by VtxxOneBusTarget when building config records.
    ///
    /// All public methods are pure functions — no side effects.
    /// </summary>
    public static class OneBusBanking
    {
        // ── PRG bank helpers ──────────────────────────────────────────────────

        /// <summary>
        /// PS field ($410B bits 0-2), InnerMask, and required placement alignment
        /// for a game with the given PRG size.
        ///
        ///   PS  InnerMask  inner window  PRG size   alignment
        ///    5    0x01       16 KB         16 KB      16 KB
        ///    4    0x03       32 KB         32 KB      32 KB
        ///    3    0x07       64 KB         64 KB      64 KB
        ///    2    0x0F      128 KB        128 KB     128 KB
        ///    1    0x1F      256 KB        256 KB     256 KB
        ///    0    0x3F      512 KB        512 KB+    512 KB
        /// </summary>
        public static (byte ps, byte innerMask, int align) PsForPrg(int prgSize)
        {
            int banks = prgSize / 8192;
            if (banks <=  2) return (5, 0x01,  16 * 1024);
            if (banks <=  4) return (4, 0x03,  32 * 1024);
            if (banks <=  8) return (3, 0x07,  64 * 1024);
            if (banks <= 16) return (2, 0x0F, 128 * 1024);
            if (banks <= 32) return (1, 0x1F, 256 * 1024);
            return                  (0, 0x3F, 512 * 1024);
        }

        /// <summary>Returns the alignment in bytes for an MMC3 game with the given PRG size.</summary>
        public static int PrgAlignFor(int prgSize) => PsForPrg(prgSize).align;

        // ── CHR bank helpers ──────────────────────────────────────────────────

        /// <summary>
        /// VB0S value and InnerMask for CHR banking based on CHR size.
        ///
        ///   VB0S   InnerMask   CHR inner window
        ///     6      0x07        8 KB
        ///     5      0x0F       16 KB
        ///     4      0x1F       32 KB
        ///     2      0x3F       64 KB
        ///     1      0x7F      128 KB
        ///     0      0xFF      256 KB
        /// </summary>
        public static (byte vb0s, byte innerMask) Vb0sForChr(int chrSize)
        {
            int kb = chrSize / 1024;
            if      (kb <=   8) return (6, 0x07);
            else if (kb <=  16) return (5, 0x0F);
            else if (kb <=  32) return (4, 0x1F);
            else if (kb <=  64) return (2, 0x3F);
            else if (kb <= 128) return (1, 0x7F);
            else                return (0, 0xFF);
        }

        /// <summary>
        /// Effective PRG size used for CHR placement.
        /// Pads PRG up to the CHR inner-window boundary so InnerBank=0
        /// maps exactly to the start of CHR data.
        /// </summary>
        public static int EffectivePrgForChr(int prgSize, int chrSize)
        {
            int innerWindow = ChrInnerWindow(chrSize);
            if (prgSize % innerWindow != 0)
                return ((prgSize + innerWindow - 1) / innerWindow) * innerWindow;
            return prgSize;
        }

        private static int ChrInnerWindow(int chrSize)
        {
            int kb = chrSize / 1024;
            if (kb <=   8) return   8 * 1024;
            if (kb <=  16) return  16 * 1024;
            if (kb <=  32) return  32 * 1024;
            if (kb <=  64) return  64 * 1024;
            if (kb <= 128) return 128 * 1024;
            return                256 * 1024;
        }
    }
}
