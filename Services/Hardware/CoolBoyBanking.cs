using VT03Builder.Models;

namespace VT03Builder.Services.Hardware
{
    /// <summary>
    /// CoolBoy / Mindkids (mapper 268) outer bank register math.
    ///
    /// Register values are derived from ClusterM's loader.asm:
    /// https://github.com/ClusterM/coolboy-multirom-builder/blob/master/loader.asm
    ///
    /// The AA6023 ASIC has four outer bank registers ($6000-$6003 for submapper 0,
    /// $5000-$5003 for submapper 1) that extend the MMC3's addressing from 512 KB
    /// to 32 MB.  The registers are written before launching each game.
    ///
    /// Input: the game's flash offset expressed as a 16 KB bank number N.
    ///   NROM_BANK_L = N & 0xFF
    ///   NROM_BANK_H = (N >> 8) & 0x07
    ///
    /// REG0 (submapper 0-3):
    ///   bit7,6 = 1,1  (PRG / CHR mask bits — lock outer address)
    ///   bit5,4,3 = NROM_BANK_H bits 2,1 and NROM_BANK_L bits 5,4,3 shifted in
    ///   bit2,1,0 = NROM_BANK_L bits 5,4,3 >> 3  (PRG A19,A18,A17)
    ///
    /// REG1 (submapper 0/1):
    ///   bit7 = 1  (PRG mask)
    ///   bit4,3,2 = NROM_BANK_L A20, NROM_BANK_L A21, NROM_BANK_H A22
    ///
    /// REG2:  CHR bank low nibble (for CHR-RAM offset during loading)
    ///
    /// REG3:  (NROM_BANK_L bits 2,1,0) << 1  |  0x10  (NROM mode flag)
    ///   bit4 = 1  NROM mode — required for game launch
    ///   bit3..1 = PRG A16,A15,A14
    ///
    /// After reg0-reg3 are written, the menu:
    ///   1. Copies CHR data from flash to CHR-RAM (8 KB at a time via PPU $0000)
    ///   2. Writes MMC3 registers ($8000/$8001) for the game's PRG/CHR banks
    ///   3. Locks the outer registers by writing reg3 | 0x80 to COOLBOY_REG_3
    ///   4. Jumps to ($FFFC) to start the game
    /// </summary>
    public static class CoolBoyBanking
    {
        // ── Config record layout (10 bytes) ──────────────────────────────────
        //  [0]  REG0           outer bank A19..A17, mask bits
        //  [1]  REG1           outer bank A22..A20, mask bits
        //  [2]  REG2           0x00 (CHR-RAM, no GNROM offset)
        //  [3]  REG3           NROM mode flag (0x10) | PRG A16..A14
        //  [4]  CHR_START_H   high byte of 16KB CHR source bank number
        //  [5]  CHR_START_L   low  byte of 16KB CHR source bank number
        //  [6]  CHR_START_S   $80=$8000 or $C0=$C000 within the 16KB bank
        //  [7]  CHR_SIZE      number of 8KB CHR banks to load (0 = NROM/no CHR)
        //  [8]  MIRRORING     0=vertical, 1=horizontal (written to MMC3 $A000)
        //  [9]  RESERVED      0
        //
        //  In the flash game table, each record is 32 bytes:
        //    [0..9]   this config record
        //    [10]     name_len (0-20)
        //    [11..30] name (ASCII, null-padded to 20 bytes)
        //    [31]     pad (0)

        public const int RecordBytes = 10;
        /// <summary>Alias kept for any callers using ConfigBytes.</summary>
        public const int ConfigBytes  = RecordBytes;

        /// <summary>
        /// Build the 10-byte CoolBoy config record for a game placed at norOffset.
        /// </summary>
        public static byte[] BuildConfig(NesRom game, int norOffset)
        {
            // 16KB bank number for the start of the game's PRG
            int  bank16k = norOffset / 0x4000;
            byte bkLo    = (byte)(bank16k & 0xFF);
            byte bkHi    = (byte)((bank16k >> 8) & 0x07);

            // ── Outer bank registers (from loader.asm select_prg_chr_banks) ──
            // REG0 — submapper 0-3
            byte reg0  = (byte)(((bkLo & 0x38) >> 3) |       // PRG A19,A18,A17
                                 (bkHi & 0x06) << 3  |       // PRG A24,A23 → bits 5,4
                                 0xC0);                        // mask bits always set

            // REG1 — submapper 0/1
            byte reg1  = (byte)(((bkLo & 0x40) >> 2) |       // PRG A20
                                 ((bkLo & 0x80) >> 5) |       // PRG A21
                                 ((bkHi & 0x01) << 3) |       // PRG A22
                                 0x80);                        // mask bit always set

            // REG2 — CHR bank (lower nibble; filled during CHR loading, 0 here)
            byte reg2 = 0x00;

            // REG3 — NROM mode flag + PRG A16..A14
            // bit4 = 1 → NROM mode (game launched via NROM banking, not bare MMC3)
            byte reg3 = (byte)(((bkLo & 0x07) << 1) | 0x10);

            // ── CHR loading parameters ────────────────────────────────────────
            // CHR data sits right after PRG data in flash (no VTxx-style padding).
            int chrOffset = norOffset + game.PrgSize;
            int chr16kBank = chrOffset / 0x4000;
            byte chrH = (byte)((chr16kBank >> 8) & 0xFF);
            byte chrL = (byte)(chr16kBank & 0xFF);
            // Within the 16KB bank: first 8KB → $8000, second 8KB → $C000
            byte chrS = (byte)((chrOffset % 0x4000 < 0x2000) ? 0x80 : 0xC0);
            // Number of 8KB CHR banks to copy
            byte chrSize = (byte)((game.ChrSize + 8191) / 8192);

            // ── Mirroring ─────────────────────────────────────────────────────
            byte mirror = game.Vertical ? (byte)0x00 : (byte)0x01;

            return new byte[10]
            {
                reg0, reg1, reg2, reg3,         // [0..3] outer bank regs
                chrH, chrL, chrS, chrSize,      // [4..7] CHR loading params
                mirror, 0x00                    // [8]=mirroring, [9]=reserved
            };
        }
    }
}
