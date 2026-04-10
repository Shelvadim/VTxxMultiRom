using VT03Builder.Models;

namespace VT03Builder.Services.Hardware
{
    /// <summary>
    /// CoolBoy / Mindkids (mapper 268) outer bank register math.
    ///
    /// The AA6023 ASIC is an MMC3 clone with six outer bank registers that
    /// extend addressing from 512 KB to 32 MB. The registers live at different
    /// addresses depending on the solder-pad setting:
    ///   Submapper 0 (CoolBoy):  $6000–$6003
    ///   Submapper 1 (Mindkids): $5000–$5003
    ///
    /// Register layout ($6000/$5000 = reg 0, $6001/$5001 = reg 1, etc.):
    ///
    ///   Reg 0  $6000/$5000:
    ///     bits 7-3  PRG outer bank A20-A16 (bits 4-0 of outer PRG bank)
    ///     bits 2-0  CHR outer bank A18-A16 (bits 2-0 of CHR outer)
    ///
    ///   Reg 1  $6001/$5001:
    ///     bits 7-4  CHR outer bank A22-A19 (bits 6-3 of CHR outer)
    ///     bits 3-2  PRG outer bank A22-A21 (bits 6-5 of outer PRG)
    ///     bit  1    PRG outer A23            (bit 7 of outer PRG, rarely used)
    ///     bit  0    unused
    ///
    ///   Reg 2  $6002/$5002:
    ///     bit  6    PRG mask: 0=use 512KB inner window, 1=use 256KB
    ///     bit  0    CHR mask: 0=256KB CHR-RAM mode,    1=128KB CHR-RAM mode
    ///
    ///   Reg 3  $6003/$5003  (Lockout):
    ///     bit  7    write 1 to lock all outer registers (prevents game from
    ///               changing outer banks). Must be set AFTER all other regs.
    ///     (also handles WRAM enable in the $6000 variant)
    ///
    /// The inner MMC3 registers ($8000-$A000 range) work exactly as on real
    /// MMC3, selecting 8 KB banks within the outer window.
    ///
    /// Sources:
    ///   NESdev wiki — NES 2.0 Mapper 268
    ///   ClusterM's coolboy-multirom-builder source
    /// </summary>
    public static class CoolBoyBanking
    {
        // ── Config record layout (8 bytes) ───────────────────────────────────
        //
        //  [0]  Reg0  outer PRG/CHR bank A
        //  [1]  Reg1  outer PRG/CHR bank B
        //  [2]  Reg2  PRG/CHR mask flags
        //  [3]  Reg3  lockout (always 0x80 after init)
        //  [4]  PRG inner bank 0 ($8000)   — written to MMC3 $8000/$8001
        //  [5]  PRG inner bank 1 ($A000)   — written to MMC3 $8000/$8001
        //  [6]  CHR size hint              — 0=256KB CHR-RAM, 1=128KB
        //  [7]  mirroring                  — 0=vertical, 1=horizontal

        /// <summary>
        /// Build the 8-byte CoolBoy config record for a game placed at norOffset.
        /// </summary>
        public static byte[] BuildConfig(NesRom game, int norOffset, int submapper)
        {
            // PRG outer bank = norOffset / 512KB (each outer window = 512KB)
            // Within the outer window MMC3 inner banks address 8KB at a time.
            int outerPrg  = norOffset / (512 * 1024);

            // Reg0 bits 7-3 = PRG outer A20-A16 = bits 4-0 of outerPrg
            // Reg0 bits 2-0 = CHR outer A18-A16 = 0 (CHR always from start of
            //                 outer window on CoolBoy — no CHR-ROM splitting)
            byte reg0 = (byte)((outerPrg & 0x1F) << 3);

            // Reg1 bits 3-2 = PRG outer A22-A21 = bits 6-5 of outerPrg
            // Reg1 bit  1   = PRG outer A23     = bit 7 of outerPrg
            byte reg1 = (byte)(((outerPrg >> 5) & 0x03) << 2 |
                                ((outerPrg >> 7) & 0x01) << 1);

            // Reg2: use 512KB inner window (bit6=0), 256KB CHR-RAM (bit0=0)
            byte reg2 = 0x00;

            // Reg3 lockout byte — written last by menu to lock outer banks.
            // We store 0x00 here; menu code writes 0x80 after copying all regs.
            byte reg3 = 0x00;

            // Inner PRG banks for $8000 and $A000 within the 512KB outer window.
            // For MMC3: inner = (norOffset % 512KB) / 8KB
            int innerBase = (norOffset % (512 * 1024)) / 8192;
            byte prgBank0 = (byte)(innerBase & 0x3F);       // $8000 bank
            byte prgBank1 = (byte)((innerBase + 2) & 0x3F); // $A000 bank

            // CHR size hint: 0 = 256KB CHR-RAM, 1 = 128KB CHR-RAM
            byte chrHint = game.ChrSize > 128 * 1024 ? (byte)0x00 : (byte)0x01;

            // Mirroring: 0=vertical, 1=horizontal
            byte mirror = game.Vertical ? (byte)0x00 : (byte)0x01;

            return new byte[] { reg0, reg1, reg2, reg3,
                                prgBank0, prgBank1, chrHint, mirror };
        }
    }
}
