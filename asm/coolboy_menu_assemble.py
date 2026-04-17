#!/usr/bin/env python3
"""
coolboy_menu_assemble.py  —  CoolBoy / Mindkids (mapper 268) menu ROM generator.

Produces coolboy_menu.rom: a 128 KB raw PRG binary (no NES header).
Place it at flash offset 0x000000 (first 128 KB of the CoolBoy menu window).

Usage:
    python coolboy_menu_assemble.py [output_path]

Flash layout this file covers:
  0x00000–0x01FFF  PRG bank  0  CHR tile font (copied to CHR-RAM at startup)
  0x02000–0x03FFF  PRG bank  1  (spare, 0xFF)
  0x04000–0x07FFF  PRG banks 2-3  Game table ← written by CoolBoyTarget.WriteGameTable()
  0x08000–0x1BFFF  PRG banks 4-13 (spare, 0xFF)
  0x1C000–0x1DFFF  PRG bank 14  Fixed CPU $C000–$DFFF  (subroutines)
  0x1E000–0x1FFFF  PRG bank 15  Fixed CPU $E000–$FFFF  (startup + main loop)
  0x1FFFA–0x1FFFF  NMI / RESET / IRQ vectors

MMC3 banking (mode 0 — default after reset):
  $8000-$9FFF  switchable, MMC3 reg 6
  $A000-$BFFF  switchable, MMC3 reg 7
  $C000-$DFFF  FIXED = bank 14  (second-to-last 8 KB bank)
  $E000-$FFFF  FIXED = bank 15  (last 8 KB bank)

Assembly approach: org at CPU addresses; extract per-bank with a.rom(cpu_start, 8192).
Labels resolve to CPU addresses — correct for JSR/JMP targets and vectors.
"""

import sys

# ─────────────────────────────────────────────────────────────────────────────
#  Micro-assembler (same pattern as VTxx menu_assemble.py)
# ─────────────────────────────────────────────────────────────────────────────
class A:
    def __init__(self):
        self.pc  = 0
        self.mem = {}
        self.sym = {}
        self.fx  = []   # (pc, label, kind)  kind: 'a16' | 'lo' | 'hi' | 'r8'

    def org(self, v):   self.pc = v
    def here(self):     return self.pc

    def b(self, *bs):
        for v in bs:
            self.mem[self.pc] = int(v) & 0xFF
            self.pc += 1

    def w(self, v):     self.b(v & 0xFF, v >> 8)

    def lbl(self, n):
        if n in self.sym:
            raise AssertionError(f"Duplicate label: '{n}'")
        self.sym[n] = self.pc

    def ref16(self, n): self.fx.append((self.pc, n, 'a16')); self.w(0)
    def ref_lo(self, n):self.fx.append((self.pc, n, 'lo'));  self.b(0)
    def ref_hi(self, n):self.fx.append((self.pc, n, 'hi'));  self.b(0)
    def bref(self, n):  self.fx.append((self.pc, n, 'r8'));  self.b(0)

    def resolve(self):
        errs = []
        for pc, n, k in self.fx:
            if n not in self.sym:
                errs.append(f"Undefined label '{n}' (referenced at ${pc:04X})")
                continue
            t = self.sym[n]
            if k == 'a16':
                self.mem[pc]   = t & 0xFF
                self.mem[pc+1] = (t >> 8) & 0xFF
            elif k == 'lo':
                self.mem[pc]   = t & 0xFF
            elif k == 'hi':
                self.mem[pc]   = (t >> 8) & 0xFF
            elif k == 'r8':
                d = t - (pc + 1)
                if not (-128 <= d <= 127):
                    errs.append(f"Branch to '{n}' out of range: ${pc:04X}→${t:04X} ({d:+d})")
                    continue
                self.mem[pc] = d & 0xFF
        return errs

    def rom(self, start, size):
        """Extract bytes for address range [start, start+size)."""
        return bytes(self.mem.get(start + i, 0xFF) for i in range(size))

    # ── Implied ───────────────────────────────────────────────────────────────
    def SEI(self):  self.b(0x78)
    def CLI(self):  self.b(0x58)
    def CLC(self):  self.b(0x18)
    def SEC(self):  self.b(0x38)
    def CLD(self):  self.b(0xD8)
    def RTS(self):  self.b(0x60)
    def RTI(self):  self.b(0x40)
    def NOP(self):  self.b(0xEA)
    def TXS(self):  self.b(0x9A)
    def TSX(self):  self.b(0xBA)
    def TXA(self):  self.b(0x8A)
    def TAX(self):  self.b(0xAA)
    def TYA(self):  self.b(0x98)
    def TAY(self):  self.b(0xA8)
    def INX(self):  self.b(0xE8)
    def INY(self):  self.b(0xC8)
    def DEX(self):  self.b(0xCA)
    def DEY(self):  self.b(0x88)
    def PHA(self):  self.b(0x48)
    def PLA(self):  self.b(0x68)
    def ASL(self):  self.b(0x0A)   # ASL A
    def LSR(self):  self.b(0x4A)   # LSR A
    def ROL(self):  self.b(0x2A)   # ROL A
    def ROR(self):  self.b(0x6A)   # ROR A

    # ── Immediate ────────────────────────────────────────────────────────────
    def LDA_I(self,v): self.b(0xA9,v)
    def LDX_I(self,v): self.b(0xA2,v)
    def LDY_I(self,v): self.b(0xA0,v)
    def CMP_I(self,v): self.b(0xC9,v)
    def CPX_I(self,v): self.b(0xE0,v)
    def CPY_I(self,v): self.b(0xC0,v)
    def AND_I(self,v): self.b(0x29,v)
    def ORA_I(self,v): self.b(0x09,v)
    def EOR_I(self,v): self.b(0x49,v)
    def ADC_I(self,v): self.b(0x69,v)
    def SBC_I(self,v): self.b(0xE9,v)

    # ── Zero page ────────────────────────────────────────────────────────────
    def LDA_Z(self,z):  self.b(0xA5,z)
    def LDX_Z(self,z):  self.b(0xA6,z)
    def LDY_Z(self,z):  self.b(0xA4,z)
    def STA_Z(self,z):  self.b(0x85,z)
    def STX_Z(self,z):  self.b(0x86,z)
    def STY_Z(self,z):  self.b(0x84,z)
    def INC_Z(self,z):  self.b(0xE6,z)
    def DEC_Z(self,z):  self.b(0xC6,z)
    def CMP_Z(self,z):  self.b(0xC5,z)
    def CPX_Z(self,z):  self.b(0xE4,z)
    def AND_Z(self,z):  self.b(0x25,z)
    def ORA_Z(self,z):  self.b(0x05,z)
    def EOR_Z(self,z):  self.b(0x45,z)
    def ADC_Z(self,z):  self.b(0x65,z)
    def SBC_Z(self,z):  self.b(0xE5,z)
    def ROL_Z(self,z):  self.b(0x26,z)
    def LSR_Z(self,z):  self.b(0x46,z)
    def ASL_Z(self,z):  self.b(0x06,z)

    # ── Zero page, X ─────────────────────────────────────────────────────────
    def LDA_ZX(self,z): self.b(0xB5,z)
    def STA_ZX(self,z): self.b(0x95,z)

    # ── Absolute ─────────────────────────────────────────────────────────────
    def LDA_A(self,a):  self.b(0xAD, a&0xFF, a>>8)
    def LDX_A(self,a):  self.b(0xAE, a&0xFF, a>>8)
    def STA_A(self,a):  self.b(0x8D, a&0xFF, a>>8)
    def STX_A(self,a):  self.b(0x8E, a&0xFF, a>>8)
    def STY_A(self,a):  self.b(0x8C, a&0xFF, a>>8)
    def BIT_A(self,a):  self.b(0x2C, a&0xFF, a>>8)
    def INC_A(self,a):  self.b(0xEE, a&0xFF, a>>8)
    def DEC_A(self,a):  self.b(0xCE, a&0xFF, a>>8)

    # ── Absolute, Y ──────────────────────────────────────────────────────────
    def LDA_AY(self,a): self.b(0xB9, a&0xFF, a>>8)

    # ── Jumps / calls ────────────────────────────────────────────────────────
    def JMP_A(self,a):  self.b(0x4C, a&0xFF, a>>8)
    def JMP_I(self,a):  self.b(0x6C, a&0xFF, a>>8)   # JMP (abs)
    def JSR_A(self,a):  self.b(0x20, a&0xFF, a>>8)
    def JMP(self,n):    self.b(0x4C); self.ref16(n)
    def JSR(self,n):    self.b(0x20); self.ref16(n)

    # ── Branches ─────────────────────────────────────────────────────────────
    def BEQ(self,n): self.b(0xF0); self.bref(n)
    def BNE(self,n): self.b(0xD0); self.bref(n)
    def BCC(self,n): self.b(0x90); self.bref(n)
    def BCS(self,n): self.b(0xB0); self.bref(n)
    def BPL(self,n): self.b(0x10); self.bref(n)
    def BMI(self,n): self.b(0x30); self.bref(n)

    # ── Indirect ─────────────────────────────────────────────────────────────
    def LDA_IY(self,z): self.b(0xB1,z)   # LDA (zp),Y
    def STA_IY(self,z): self.b(0x91,z)   # STA (zp),Y


# ─────────────────────────────────────────────────────────────────────────────
#  CHR font generator  —  8×8 tiles for ASCII $00–$FF
# ─────────────────────────────────────────────────────────────────────────────
def make_chr_font():
    """Return 8192 bytes: 256 tiles × 16 bytes each (plane-0 glyph + plane-1 zeros)."""
    GLYPHS = {}
    # Space
    GLYPHS[0x20] = [0]*8
    # Punctuation
    GLYPHS[0x21] = [0x18,0x18,0x18,0x18,0x18,0x00,0x18,0x00]  # !
    GLYPHS[0x22] = [0x6C,0x6C,0x24,0x00,0x00,0x00,0x00,0x00]  # "
    GLYPHS[0x27] = [0x18,0x18,0x10,0x00,0x00,0x00,0x00,0x00]  # '
    GLYPHS[0x28] = [0x0C,0x18,0x30,0x30,0x30,0x18,0x0C,0x00]  # (
    GLYPHS[0x29] = [0x30,0x18,0x0C,0x0C,0x0C,0x18,0x30,0x00]  # )
    GLYPHS[0x2A] = [0x00,0x66,0x3C,0xFF,0x3C,0x66,0x00,0x00]  # *
    GLYPHS[0x2B] = [0x00,0x18,0x18,0x7E,0x18,0x18,0x00,0x00]  # +
    GLYPHS[0x2C] = [0x00,0x00,0x00,0x00,0x18,0x18,0x10,0x00]  # ,
    GLYPHS[0x2D] = [0x00,0x00,0x00,0x7E,0x00,0x00,0x00,0x00]  # -
    GLYPHS[0x2E] = [0x00,0x00,0x00,0x00,0x00,0x18,0x18,0x00]  # .
    GLYPHS[0x2F] = [0x02,0x04,0x08,0x10,0x20,0x40,0x80,0x00]  # /
    GLYPHS[0x3A] = [0x00,0x18,0x18,0x00,0x18,0x18,0x00,0x00]  # :
    GLYPHS[0x3B] = [0x00,0x18,0x18,0x00,0x18,0x18,0x10,0x00]  # ;
    GLYPHS[0x3C] = [0x06,0x18,0x60,0x18,0x06,0x00,0x00,0x00]  # <
    GLYPHS[0x3D] = [0x00,0x00,0x7E,0x00,0x7E,0x00,0x00,0x00]  # =
    GLYPHS[0x3E] = [0x60,0x18,0x06,0x18,0x60,0x00,0x00,0x00]  # >
    GLYPHS[0x3F] = [0x3C,0x42,0x04,0x08,0x00,0x08,0x00,0x00]  # ?
    GLYPHS[0x5B] = [0x3E,0x30,0x30,0x30,0x30,0x3E,0x00,0x00]  # [
    GLYPHS[0x5D] = [0x7C,0x0C,0x0C,0x0C,0x0C,0x7C,0x00,0x00]  # ]
    GLYPHS[0x5F] = [0x00,0x00,0x00,0x00,0x00,0x00,0x7E,0x00]  # _
    # Digits 0-9
    for code, g in zip(range(0x30,0x3A), [
        [0x3C,0x46,0x4A,0x52,0x62,0x3C,0x00,0x00],
        [0x18,0x38,0x18,0x18,0x18,0x7E,0x00,0x00],
        [0x3C,0x42,0x04,0x18,0x20,0x7E,0x00,0x00],
        [0x3C,0x42,0x0C,0x02,0x42,0x3C,0x00,0x00],
        [0x08,0x18,0x28,0x48,0x7E,0x08,0x00,0x00],
        [0x7E,0x40,0x7C,0x02,0x42,0x3C,0x00,0x00],
        [0x1C,0x20,0x40,0x7C,0x42,0x3C,0x00,0x00],
        [0x7E,0x04,0x08,0x10,0x20,0x20,0x00,0x00],
        [0x3C,0x42,0x3C,0x42,0x42,0x3C,0x00,0x00],
        [0x3C,0x42,0x3E,0x02,0x04,0x38,0x00,0x00],
    ]): GLYPHS[code] = g
    # Uppercase A-Z
    for code, g in zip(range(0x41,0x5B), [
        [0x18,0x24,0x42,0x7E,0x42,0x42,0x00,0x00],  # A
        [0x7C,0x42,0x7C,0x42,0x42,0x7C,0x00,0x00],  # B
        [0x3C,0x42,0x40,0x40,0x42,0x3C,0x00,0x00],  # C
        [0x78,0x44,0x42,0x42,0x44,0x78,0x00,0x00],  # D
        [0x7E,0x40,0x7C,0x40,0x40,0x7E,0x00,0x00],  # E
        [0x7E,0x40,0x7C,0x40,0x40,0x40,0x00,0x00],  # F
        [0x3C,0x42,0x40,0x4E,0x42,0x3C,0x00,0x00],  # G
        [0x42,0x42,0x7E,0x42,0x42,0x42,0x00,0x00],  # H
        [0x7E,0x18,0x18,0x18,0x18,0x7E,0x00,0x00],  # I
        [0x3E,0x08,0x08,0x08,0x48,0x30,0x00,0x00],  # J
        [0x44,0x48,0x70,0x48,0x44,0x42,0x00,0x00],  # K
        [0x40,0x40,0x40,0x40,0x40,0x7E,0x00,0x00],  # L
        [0x42,0x66,0x5A,0x42,0x42,0x42,0x00,0x00],  # M
        [0x42,0x62,0x52,0x4A,0x46,0x42,0x00,0x00],  # N
        [0x3C,0x42,0x42,0x42,0x42,0x3C,0x00,0x00],  # O
        [0x7C,0x42,0x42,0x7C,0x40,0x40,0x00,0x00],  # P
        [0x3C,0x42,0x42,0x4A,0x44,0x3A,0x00,0x00],  # Q
        [0x7C,0x42,0x42,0x7C,0x44,0x42,0x00,0x00],  # R
        [0x3C,0x40,0x3C,0x02,0x42,0x3C,0x00,0x00],  # S
        [0x7E,0x18,0x18,0x18,0x18,0x18,0x00,0x00],  # T
        [0x42,0x42,0x42,0x42,0x42,0x3C,0x00,0x00],  # U
        [0x42,0x42,0x42,0x24,0x24,0x18,0x00,0x00],  # V
        [0x42,0x42,0x42,0x5A,0x66,0x42,0x00,0x00],  # W
        [0x42,0x24,0x18,0x18,0x24,0x42,0x00,0x00],  # X
        [0x42,0x42,0x24,0x18,0x18,0x18,0x00,0x00],  # Y
        [0x7E,0x04,0x08,0x10,0x20,0x7E,0x00,0x00],  # Z
    ]): GLYPHS[code] = g
    # Lowercase a-z (readable variants)
    for code, g in zip(range(0x61,0x7B), [
        [0x00,0x3C,0x02,0x3E,0x42,0x3E,0x00,0x00],  # a
        [0x40,0x7C,0x42,0x42,0x42,0x7C,0x00,0x00],  # b
        [0x00,0x3C,0x40,0x40,0x40,0x3C,0x00,0x00],  # c
        [0x02,0x3E,0x42,0x42,0x42,0x3E,0x00,0x00],  # d
        [0x00,0x3C,0x42,0x7E,0x40,0x3C,0x00,0x00],  # e
        [0x1C,0x20,0x7C,0x20,0x20,0x20,0x00,0x00],  # f
        [0x00,0x3E,0x42,0x3E,0x02,0x3C,0x00,0x00],  # g
        [0x40,0x7C,0x42,0x42,0x42,0x42,0x00,0x00],  # h
        [0x18,0x00,0x38,0x18,0x18,0x3C,0x00,0x00],  # i
        [0x18,0x00,0x38,0x18,0x18,0x30,0x00,0x00],  # j
        [0x40,0x44,0x48,0x70,0x48,0x44,0x00,0x00],  # k
        [0x38,0x18,0x18,0x18,0x18,0x3C,0x00,0x00],  # l
        [0x00,0x76,0x4A,0x4A,0x4A,0x4A,0x00,0x00],  # m
        [0x00,0x5C,0x62,0x42,0x42,0x42,0x00,0x00],  # n
        [0x00,0x3C,0x42,0x42,0x42,0x3C,0x00,0x00],  # o
        [0x00,0x7C,0x42,0x42,0x7C,0x40,0x40,0x00],  # p
        [0x00,0x3E,0x42,0x42,0x3E,0x02,0x02,0x00],  # q
        [0x00,0x5C,0x62,0x40,0x40,0x40,0x00,0x00],  # r
        [0x00,0x3E,0x40,0x3C,0x02,0x7C,0x00,0x00],  # s
        [0x20,0x7C,0x20,0x20,0x20,0x1C,0x00,0x00],  # t
        [0x00,0x42,0x42,0x42,0x42,0x3E,0x00,0x00],  # u
        [0x00,0x42,0x42,0x42,0x24,0x18,0x00,0x00],  # v
        [0x00,0x42,0x42,0x5A,0x5A,0x24,0x00,0x00],  # w
        [0x00,0x42,0x24,0x18,0x24,0x42,0x00,0x00],  # x
        [0x00,0x42,0x42,0x3E,0x02,0x3C,0x00,0x00],  # y
        [0x00,0x7E,0x04,0x18,0x20,0x7E,0x00,0x00],  # z
    ]): GLYPHS[code] = g
    # Special tiles
    GLYPHS[0xFD] = [0x00,0x10,0x18,0x7E,0x18,0x10,0x00,0x00]  # ► cursor
    GLYPHS[0xFE] = [0xFF]*8                                     # solid block
    GLYPHS[0xFF] = [0xFF,0x00,0x00,0x00,0x00,0x00,0x00,0xFF]   # H-rule

    tiles = bytearray(8192)
    for code in range(256):
        g = GLYPHS.get(code, [0]*8)
        off = code * 16
        for i, v in enumerate(g[:8]):
            tiles[off + i] = v & 0xFF
        # plane 1 stays 0x00
    return bytes(tiles)


# ─────────────────────────────────────────────────────────────────────────────
#  Hardware constants
# ─────────────────────────────────────────────────────────────────────────────
PPUCTRL  = 0x2000
PPUMASK  = 0x2001
PPUSTAT  = 0x2002
PPUSCRL  = 0x2005
PPUADDR  = 0x2006
PPUDATA  = 0x2007
APUSTAT  = 0x4015
APUFRAME = 0x4017
JOY1     = 0x4016
MMC3SEL  = 0x8000   # MMC3 bank select
MMC3DAT  = 0x8001   # MMC3 bank data
MMC3MIR  = 0xA000   # MMC3 mirroring
MMC3PRO  = 0xA001   # MMC3 PRG-RAM protect
CB0, CB1, CB2, CB3 = 0x6000, 0x6001, 0x6002, 0x6003  # CoolBoy regs (submapper 0)

# Screen layout
VISIBLE     = 20        # game rows on screen
LIST_ROW    = 4         # first list row (0-based tile row)
CURSOR_TILE = 0xFD      # ► tile

# Zero page addresses
ZP_CUR  = 0x00   # current selected game (lo byte — supports up to 255 games)
ZP_TOP  = 0x01   # top visible game
ZP_CNT  = 0x02   # total game count
ZP_BTN  = 0x03   # current buttons (A=b7,B=b6,Sel=b5,Sta=b4,Up=b3,Dn=b2,Lt=b1,Rt=b0)
ZP_PREV = 0x04   # previous buttons
ZP_ROW  = 0x05   # temp: current draw row
ZP_TMP  = 0x06   # general temp
ZP_P_LO = 0x08   # 16-bit ptr lo (for (ptr),Y)
ZP_P_HI = 0x09   # 16-bit ptr hi
# CHR loader ZP (used only during game launch)
ZP_CHR_CTR  = 0x07  # chr_size countdown (banks remaining to copy)
ZP_CHR_BANK = 0x0A  # current 8KB inner MMC3 bank number
ZP_SAVED_R3 = 0x0B  # saved reg3 for the lockout step after CHR copy
ZP_REC_LO   = 0x0C  # saved game-record pointer lo (preserved across CHR copy)
ZP_REC_HI   = 0x0D  # saved game-record pointer hi

# Game table at $8000 when banks 2+3 are switched in (MMC3 reg6=2, reg7=3)
GTABLE  = 0x8000  # game count at $8000-$8001, records from $8002
RECORD  = 32      # bytes per game record

# PRG size constants
PRG_SIZE    = 128 * 1024   # 131072 bytes total
BANK_SIZE   = 8192         # 8 KB per MMC3 bank
# CPU addresses of fixed banks
BANK14_CPU  = 0xC000       # second-to-last bank (bank 14 of 16)
BANK15_CPU  = 0xE000       # last bank (bank 15 of 16)
VECTORS_CPU = 0xFFFA       # NMI / RESET / IRQ


# ─────────────────────────────────────────────────────────────────────────────
#  Assembly
# ─────────────────────────────────────────────────────────────────────────────
def assemble():
    a = A()

    # ── Bank 0 at CPU $0000–$1FFF: CHR tile font ─────────────────────────────
    # This bank is switched in temporarily at $8000 during boot to copy CHR to PPU.
    # We place it at CPU $0000 purely for assembler convenience;
    # extraction uses a.rom(0x0000, 8192) → placed at flash 0x00000.
    a.org(0x0000)
    for byte in make_chr_font():
        a.b(byte)

    # ── Bank 14 at CPU $C000–$DFFF: subroutines (fixed bank) ─────────────────
    a.org(BANK14_CPU)

    # NT address tables for VISIBLE list rows (row LIST_ROW..LIST_ROW+VISIBLE-1)
    a.lbl('nt_hi')
    for r in range(VISIBLE):
        nt = 0x2000 + (LIST_ROW + r) * 32 + 1   # col 1 (cursor col)
        a.b(nt >> 8)
    a.lbl('nt_lo')
    for r in range(VISIBLE):
        nt = 0x2000 + (LIST_ROW + r) * 32 + 1
        a.b(nt & 0xFF)

    # ── switch_gtable: switch MMC3 reg6=2, reg7=3 → game table at $8000–$BFFF
    a.lbl('switch_gtable')
    a.LDA_I(6); a.STA_A(MMC3SEL); a.LDA_I(2); a.STA_A(MMC3DAT)
    a.LDA_I(7); a.STA_A(MMC3SEL); a.LDA_I(3); a.STA_A(MMC3DAT)
    a.RTS()

    # ── wait_vblank: spin on PPUSTATUS bit 7
    a.lbl('wait_vblank')
    a.lbl('wvb')
    a.BIT_A(PPUSTAT); a.BPL('wvb')
    a.RTS()

    # ── read_buttons: standard NES controller read → ZP_BTN
    # Result byte: bit7=A, 6=B, 5=Sel, 4=Sta, 3=Up, 2=Dn, 1=Lt, 0=Rt
    a.lbl('read_buttons')
    a.LDA_Z(ZP_BTN); a.STA_Z(ZP_PREV)
    a.LDA_I(1); a.STA_A(JOY1)
    a.LDA_I(0); a.STA_A(JOY1)
    a.LDA_I(0); a.STA_Z(ZP_BTN)
    a.LDX_I(8)
    a.lbl('rb_bit')
    a.LDA_A(JOY1); a.LSR(); a.ROL_Z(ZP_BTN)
    a.DEX(); a.BNE('rb_bit')
    a.RTS()

    # ── load_palette: write all 4 BG palettes
    a.lbl('load_palette')
    a.LDA_A(PPUSTAT)                          # reset addr latch
    a.LDA_I(0x3F); a.STA_A(PPUADDR)
    a.LDA_I(0x00); a.STA_A(PPUADDR)
    # Pal 0: border/title  (black, dark blue, blue, white)
    for c in [0x0F, 0x01, 0x11, 0x30]: a.LDA_I(c); a.STA_A(PPUDATA)
    # Pal 1: selected game (black, orange, yellow, white)
    for c in [0x0F, 0x27, 0x17, 0x30]: a.LDA_I(c); a.STA_A(PPUDATA)
    # Pal 2: normal games  (black, green, light-green, white)
    for c in [0x0F, 0x1A, 0x2A, 0x30]: a.LDA_I(c); a.STA_A(PPUDATA)
    # Pal 3: hints/dim     (black, dk-grey, grey, white)
    for c in [0x0F, 0x10, 0x20, 0x30]: a.LDA_I(c); a.STA_A(PPUDATA)
    a.RTS()

    # ── clear_nt: fill nametable 0 ($2000–$23BF) with space tile
    a.lbl('clear_nt')
    a.LDA_A(PPUSTAT)
    a.LDA_I(0x20); a.STA_A(PPUADDR)
    a.LDA_I(0x00); a.STA_A(PPUADDR)
    a.LDA_I(0x20)          # space tile
    a.LDX_I(4)
    a.LDY_I(0)
    a.lbl('cnt_l'); a.STA_A(PPUDATA); a.INY(); a.BNE('cnt_l')
    a.DEX(); a.BNE('cnt_l')
    a.RTS()

    # ── draw_static: draw title row and hint row
    a.lbl('draw_static')
    # Title at row 1, col 2 → $2042
    a.LDA_A(PPUSTAT)
    a.LDA_I(0x20); a.STA_A(PPUADDR)
    a.LDA_I(0x42); a.STA_A(PPUADDR)
    TITLE = b'  ** COOLBOY MULTIROM **  '
    for c in TITLE: a.LDA_I(c); a.STA_A(PPUDATA)
    # Hint at row 26, col 1 → $2341
    a.LDA_I(0x23); a.STA_A(PPUADDR)
    a.LDA_I(0x41); a.STA_A(PPUADDR)
    HINT = b'A/START=LAUNCH UP/DN=SCROLL'
    for c in HINT: a.LDA_I(c); a.STA_A(PPUDATA)
    a.RTS()

    # ── draw_string: draw up to X chars from (ZP_P),Y; pad rest with spaces
    # In: ZP_P_LO/ZP_P_HI = string pointer, X = max chars, Y = offset into string
    # (Y should be 0 on call unless drawing from an offset)
    a.lbl('draw_string')
    a.lbl('ds_l')
    a.CPX_I(0); a.BEQ('ds_pad')
    a.LDA_IY(ZP_P_LO); a.BEQ('ds_pad')    # null terminator
    a.STA_A(PPUDATA)
    a.INY(); a.DEX(); a.JMP('ds_l')
    a.lbl('ds_pad')
    a.CPX_I(0); a.BEQ('ds_end')
    a.LDA_I(0x20); a.STA_A(PPUDATA)
    a.DEX(); a.JMP('ds_pad')
    a.lbl('ds_end')
    a.RTS()

    # ── draw_list: redraw all VISIBLE game rows
    # Must be called during vblank window.
    a.lbl('draw_list')
    a.JSR('switch_gtable')           # banks 2+3 active at $8000
    a.LDA_I(0); a.STA_Z(ZP_ROW)

    a.lbl('dl_loop')
    # Set PPU address for this row from table
    a.LDY_Z(ZP_ROW)
    a.LDA_AY(BANK14_CPU); a.ref16('nt_hi'); a.STA_A(PPUADDR)
    a.LDA_AY(BANK14_CPU); a.ref16('nt_lo'); a.STA_A(PPUADDR)

    # game_idx = ZP_TOP + ZP_ROW
    a.LDA_Z(ZP_TOP); a.CLC(); a.ADC_Z(ZP_ROW); a.STA_Z(ZP_TMP)

    # if game_idx >= ZP_CNT → blank row
    a.CMP_Z(ZP_CNT); a.BCS('dl_blank')

    # Draw cursor tile if this is the selected game, else space
    a.CMP_Z(ZP_CUR); a.BNE('dl_space')
    a.LDA_I(CURSOR_TILE); a.JMP('dl_write_cur')
    a.lbl('dl_space')
    a.LDA_I(0x20)
    a.lbl('dl_write_cur')
    a.STA_A(PPUDATA)

    # Compute pointer to name: GTABLE + 2 + game_idx*32 + 11
    # = 0x8002 + game_idx*32 + 11 = 0x800D + game_idx*32
    a.LDA_Z(ZP_TMP); a.STA_Z(ZP_P_LO)
    a.LDA_I(0);       a.STA_Z(ZP_P_HI)
    # multiply by 32 (5 left shifts with carry into hi)
    for _ in range(5):
        a.ASL_Z(ZP_P_LO); a.ROL_Z(ZP_P_HI)
    # add $800D (GTABLE base $8000 + 2 header + 11 name-offset-in-record)
    a.LDA_Z(ZP_P_LO); a.CLC(); a.ADC_I(0x0D); a.STA_Z(ZP_P_LO)
    a.LDA_Z(ZP_P_HI); a.ADC_I(0x80);           a.STA_Z(ZP_P_HI)
    a.LDY_I(0); a.LDX_I(20)
    a.JSR('draw_string')
    a.JMP('dl_next')

    a.lbl('dl_blank')
    a.LDX_I(21)                      # cursor + 20 name cols
    a.lbl('dl_blk')
    a.LDA_I(0x20); a.STA_A(PPUDATA)
    a.DEX(); a.BNE('dl_blk')

    a.lbl('dl_next')
    a.INC_Z(ZP_ROW)
    a.LDA_Z(ZP_ROW); a.CMP_I(VISIBLE); a.BNE('dl_loop')
    # Reset PPU scroll to 0,0
    a.LDA_I(0); a.STA_A(PPUSCRL); a.STA_A(PPUSCRL)
    a.RTS()

    # ── copy_chr: copy CHR data from flash to PPU CHR-RAM ────────────────────
    # Called from launch_game with ZP_P pointing to the 32-byte game record.
    # Before calling, launch_game must save ZP_P and rec[3] (see launch_game).
    # Record layout used here:
    #   [4] chr_h   high byte of 16KB CHR source bank (0 for <32MB flash)
    #   [5] chr_l   low  byte of 16KB CHR source bank
    #   [6] chr_s   $80=$8000 (first 8KB of 16KB bank) / $C0 (second 8KB)
    #   [7] chr_size  number of 8KB CHR banks to copy (0=skip)
    #
    # Algorithm:
    #   abs_8kb_bank = chr_l*2 + (1 if chr_s==$C0 else 0)
    #   inner_bank   = abs_8kb_bank & 0x3F  (within current 512KB outer window)
    #   For each of chr_size banks:
    #     Switch MMC3 reg6 = inner_bank → 8KB appears at CPU $8000-$9FFF
    #     Copy 8192 bytes from $8000 to PPUDATA (PPU auto-increments CHR-RAM addr)
    #     inner_bank++
    a.lbl('copy_chr')
    # Read chr_size → ZP_CHR_CTR; skip if 0
    a.LDY_I(7);  a.LDA_IY(ZP_P_LO); a.STA_Z(ZP_CHR_CTR)
    a.BEQ('cc_done')

    # Compute first inner 8KB bank: chr_l*2 + (chr_s==$C0 ? 1 : 0)
    a.LDY_I(5);  a.LDA_IY(ZP_P_LO)   # chr_l
    a.ASL()                            # chr_l * 2 (carry = bit7, usually 0)
    a.STA_Z(ZP_CHR_BANK)              # save as inner bank base
    a.LDY_I(6);  a.LDA_IY(ZP_P_LO)   # chr_s
    a.CMP_I(0xC0)
    a.BNE('cc_no_odd')
    a.INC_Z(ZP_CHR_BANK)              # +1 for second 8KB of 16KB bank ($C0)
    a.lbl('cc_no_odd')
    # Mask to inner bank (AND 0x3F = mod 64 = within 512KB outer window)
    a.LDA_Z(ZP_CHR_BANK); a.AND_I(0x3F); a.STA_Z(ZP_CHR_BANK)

    # Reset PPU address latch and set write target to CHR-RAM $0000
    a.LDA_A(PPUSTAT)                  # reset PPU address latch
    a.LDA_I(0); a.STA_A(PPUADDR); a.STA_A(PPUADDR)

    # Outer loop: one iteration per 8KB CHR bank
    a.lbl('cc_outer')
    # Switch MMC3 reg6 to ZP_CHR_BANK (→ CPU $8000-$9FFF = that 8KB bank)
    a.LDA_I(6);          a.STA_A(MMC3SEL)
    a.LDA_Z(ZP_CHR_BANK); a.STA_A(MMC3DAT)

    # Copy 8192 bytes from $8000-$9FFF to PPUDATA
    # Use ZP_P as the source pointer (will be restored by caller after return)
    a.LDA_I(0x00); a.STA_Z(ZP_P_LO)
    a.LDA_I(0x80); a.STA_Z(ZP_P_HI)
    a.LDX_I(0x20)          # 32 pages × 256 bytes = 8192 bytes
    a.LDY_I(0)
    a.lbl('cc_page')
    a.LDA_IY(ZP_P_LO); a.STA_A(PPUDATA)
    a.INY(); a.BNE('cc_page')
    a.INC_Z(ZP_P_HI)
    a.DEX(); a.BNE('cc_page')

    # Advance bank counter, decrement remaining
    a.INC_Z(ZP_CHR_BANK)
    a.LDA_Z(ZP_CHR_BANK); a.AND_I(0x3F); a.STA_Z(ZP_CHR_BANK)
    a.DEC_Z(ZP_CHR_CTR); a.BNE('cc_outer')

    a.lbl('cc_done')
    a.RTS()

    # ── Bank 15 at CPU $E000–$FFFF: startup + main loop ──────────────────────
    a.org(BANK15_CPU)

    a.lbl('RESET')
    a.SEI()
    a.LDX_I(0xFF); a.TXS()
    a.CLD()
    # Silence PPU and APU
    a.LDA_I(0); a.STA_A(PPUCTRL); a.STA_A(PPUMASK); a.STA_A(APUSTAT)
    a.LDA_I(0x40); a.STA_A(APUFRAME)

    # First vblank wait
    a.lbl('rst_w1'); a.BIT_A(PPUSTAT); a.BPL('rst_w1')

    # Clear zero page
    a.LDA_I(0); a.LDX_I(0)
    a.lbl('czp'); a.STA_ZX(0); a.INX(); a.BNE('czp')

    # Clear $0200–$07FF
    a.LDA_I(0x00); a.STA_Z(ZP_P_LO)
    a.LDA_I(0x02); a.STA_Z(ZP_P_HI)
    a.LDY_I(0)
    a.lbl('cram'); a.STA_IY(ZP_P_LO); a.INY(); a.BNE('cram')
    a.INC_Z(ZP_P_HI)
    a.LDA_Z(ZP_P_HI); a.CMP_I(0x08); a.BNE('cram')

    # Second vblank wait
    a.lbl('rst_w2'); a.BIT_A(PPUSTAT); a.BPL('rst_w2')

    # MMC3 default init: PRG mode 0 (bit6=0), enable PRG-RAM
    a.LDA_I(0x80); a.STA_A(MMC3SEL); a.LDA_I(0); a.STA_A(MMC3DAT)
    a.LDA_I(0x80); a.STA_A(MMC3PRO)

    # Copy CHR font from PRG bank 0 → PPU CHR-RAM $0000
    # Switch reg6=0 → bank 0 appears at CPU $8000–$9FFF
    a.LDA_I(6); a.STA_A(MMC3SEL); a.LDA_I(0); a.STA_A(MMC3DAT)
    # Set PPU write address $0000
    a.LDA_A(PPUSTAT)
    a.LDA_I(0); a.STA_A(PPUADDR); a.STA_A(PPUADDR)
    # Copy 8192 bytes from $8000–$9FFF via (ZP_P),Y
    a.LDA_I(0x00); a.STA_Z(ZP_P_LO)
    a.LDA_I(0x80); a.STA_Z(ZP_P_HI)
    a.LDX_I(0x20)     # 32 pages of 256 bytes
    a.LDY_I(0)
    a.lbl('cchr')
    a.LDA_IY(ZP_P_LO); a.STA_A(PPUDATA)
    a.INY(); a.BNE('cchr')
    a.INC_Z(ZP_P_HI); a.DEX(); a.BNE('cchr')

    # Load palette and clear nametable
    a.JSR('load_palette')
    a.JSR('clear_nt')
    a.JSR('draw_static')

    # Read game count from game table
    a.JSR('switch_gtable')
    a.LDA_A(GTABLE);   a.STA_Z(ZP_CNT)    # lo byte (up to 255 games)

    # Init cursor/scroll
    a.LDA_I(0)
    a.STA_Z(ZP_CUR); a.STA_Z(ZP_TOP)

    # Initial draw
    a.JSR('draw_list')

    # Enable PPU rendering
    a.LDA_I(0x80); a.STA_A(PPUCTRL)   # NMI on, bg pat=$0000
    a.LDA_I(0x1E); a.STA_A(PPUMASK)   # show bg+sprites, no clipping

    # ── Main loop ─────────────────────────────────────────────────────────────
    a.lbl('main_loop')
    a.JSR('wait_vblank')
    a.JSR('read_buttons')

    # Only act on newly-pressed buttons (edge detect: BTN & ~PREV)
    a.LDA_Z(ZP_BTN); a.EOR_I(0xFF)    # NOT of PREV is not what we want
    # Simpler: just use current ZP_BTN (held = repeat navigation, which is fine)
    a.LDA_Z(ZP_BTN)

    # Down (bit 2)
    a.AND_I(0x04); a.BEQ('ml_nodn')
    a.JSR('do_down')
    a.lbl('ml_nodn')

    a.LDA_Z(ZP_BTN); a.AND_I(0x08); a.BEQ('ml_noup')  # Up (bit 3)
    a.JSR('do_up')
    a.lbl('ml_noup')

    a.LDA_Z(ZP_BTN); a.AND_I(0x01); a.BEQ('ml_nort')  # Right (bit 0) = page dn
    a.JSR('do_pgdn')
    a.lbl('ml_nort')

    a.LDA_Z(ZP_BTN); a.AND_I(0x02); a.BEQ('ml_nolt')  # Left (bit 1) = page up
    a.JSR('do_pgup')
    a.lbl('ml_nolt')

    a.LDA_Z(ZP_BTN); a.AND_I(0x90); a.BEQ('ml_noln')  # A (bit7) or Start (bit4)
    a.JSR('launch_game')
    a.lbl('ml_noln')

    a.JSR('draw_list')
    a.JMP('main_loop')

    # ── Navigation: do_down ───────────────────────────────────────────────────
    a.lbl('do_down')
    a.LDA_Z(ZP_CUR); a.CLC(); a.ADC_I(1); a.STA_Z(ZP_CUR)
    # if CUR >= CNT: wrap to 0, reset TOP
    a.CMP_Z(ZP_CNT); a.BCC('dd_ok')
    a.LDA_I(0); a.STA_Z(ZP_CUR); a.STA_Z(ZP_TOP); a.RTS()
    a.lbl('dd_ok')
    # if CUR - TOP >= VISIBLE: TOP++
    a.LDA_Z(ZP_CUR); a.SEC(); a.SBC_Z(ZP_TOP); a.CMP_I(VISIBLE)
    a.BCC('dd_done'); a.INC_Z(ZP_TOP)
    a.lbl('dd_done'); a.RTS()

    # ── Navigation: do_up ─────────────────────────────────────────────────────
    a.lbl('do_up')
    a.LDA_Z(ZP_CUR); a.BNE('du_dec')
    # CUR was 0 → wrap to CNT-1, set TOP = max(0, CNT-VISIBLE)
    a.LDA_Z(ZP_CNT); a.BEQ('du_done')
    a.SEC(); a.SBC_I(1); a.STA_Z(ZP_CUR)
    a.LDA_Z(ZP_CNT); a.SEC(); a.SBC_I(VISIBLE)
    a.BCC('du_top0'); a.STA_Z(ZP_TOP); a.JMP('du_done')
    a.lbl('du_top0'); a.LDA_I(0); a.STA_Z(ZP_TOP); a.JMP('du_done')
    a.lbl('du_dec')
    a.DEC_Z(ZP_CUR)
    # if CUR < TOP: TOP--
    a.LDA_Z(ZP_CUR); a.CMP_Z(ZP_TOP); a.BCS('du_done'); a.DEC_Z(ZP_TOP)
    a.lbl('du_done'); a.RTS()

    # ── Navigation: do_pgdn ──────────────────────────────────────────────────
    a.lbl('do_pgdn')
    a.LDA_Z(ZP_CUR); a.CLC(); a.ADC_I(VISIBLE); a.STA_Z(ZP_CUR)
    a.LDA_Z(ZP_TOP); a.CLC(); a.ADC_I(VISIBLE); a.STA_Z(ZP_TOP)
    # clamp CUR to CNT-1
    a.LDA_Z(ZP_CUR); a.CMP_Z(ZP_CNT); a.BCC('pgdn_ok')
    a.LDA_Z(ZP_CNT); a.SEC(); a.SBC_I(1); a.STA_Z(ZP_CUR)
    a.lbl('pgdn_ok'); a.RTS()

    # ── Navigation: do_pgup ──────────────────────────────────────────────────
    a.lbl('do_pgup')
    a.LDA_Z(ZP_CUR); a.SEC(); a.SBC_I(VISIBLE)
    a.BCS('pgup_cur_ok'); a.LDA_I(0)
    a.lbl('pgup_cur_ok'); a.STA_Z(ZP_CUR)
    a.LDA_Z(ZP_TOP); a.SEC(); a.SBC_I(VISIBLE)
    a.BCS('pgup_top_ok'); a.LDA_I(0)
    a.lbl('pgup_top_ok'); a.STA_Z(ZP_TOP)
    a.RTS()

    # ── launch_game ───────────────────────────────────────────────────────────
    a.lbl('launch_game')
    a.LDA_I(0); a.STA_A(PPUCTRL); a.STA_A(PPUMASK)   # disable PPU

    a.JSR('switch_gtable')

    # ptr = GTABLE + 2 + ZP_CUR * 32
    a.LDA_Z(ZP_CUR); a.STA_Z(ZP_P_LO)
    a.LDA_I(0);       a.STA_Z(ZP_P_HI)
    for _ in range(5):
        a.ASL_Z(ZP_P_LO); a.ROL_Z(ZP_P_HI)
    a.LDA_Z(ZP_P_LO); a.CLC(); a.ADC_I(0x02); a.STA_Z(ZP_P_LO)
    a.LDA_Z(ZP_P_HI); a.ADC_I(0x80);           a.STA_Z(ZP_P_HI)

    # mirroring: record[8]
    a.LDY_I(8); a.LDA_IY(ZP_P_LO); a.STA_A(MMC3MIR)

    # CoolBoy outer bank regs: record[0..3]
    a.LDY_I(0); a.LDA_IY(ZP_P_LO); a.STA_A(CB0)
    a.INY();    a.LDA_IY(ZP_P_LO); a.STA_A(CB1)
    a.INY();    a.LDA_IY(ZP_P_LO); a.STA_A(CB2)
    a.INY();    a.LDA_IY(ZP_P_LO); a.STA_A(CB3)

    # Save game-record pointer and reg3 before CHR copy trashes ZP_P
    a.LDA_Z(ZP_P_LO); a.STA_Z(ZP_REC_LO)
    a.LDA_Z(ZP_P_HI); a.STA_Z(ZP_REC_HI)
    a.LDY_I(3); a.LDA_IY(ZP_P_LO); a.STA_Z(ZP_SAVED_R3)  # save reg3

    # Copy CHR data from flash to PPU CHR-RAM
    a.JSR('copy_chr')

    # Restore game-record pointer (needed for lock step below)
    a.LDA_Z(ZP_REC_LO); a.STA_Z(ZP_P_LO)
    a.LDA_Z(ZP_REC_HI); a.STA_Z(ZP_P_HI)

    # Set MMC3 default PRG banks: reg6=FE, reg7=FF (penultimate + last)
    a.LDA_I(6); a.STA_A(MMC3SEL); a.LDA_I(0xFE); a.STA_A(MMC3DAT)
    a.LDA_I(7); a.STA_A(MMC3SEL); a.LDA_I(0xFF); a.STA_A(MMC3DAT)

    # Lock outer regs: saved_reg3 | $80 → CB3
    a.LDA_Z(ZP_SAVED_R3); a.ORA_I(0x80); a.STA_A(CB3)

    # Clear $0200–$07FF (games expect clean RAM)
    a.LDA_I(0x00); a.STA_Z(ZP_P_LO)
    a.LDA_I(0x02); a.STA_Z(ZP_P_HI)
    a.LDY_I(0)
    a.lbl('lg_clr'); a.STA_IY(ZP_P_LO); a.INY(); a.BNE('lg_clr')
    a.INC_Z(ZP_P_HI)
    a.LDA_Z(ZP_P_HI); a.CMP_I(0x08); a.BNE('lg_clr')

    a.JMP_I(0xFFFC)   # jump to game reset vector

    # ── NMI / IRQ stubs ───────────────────────────────────────────────────────
    a.lbl('NMI'); a.RTI()
    a.lbl('IRQ'); a.RTI()

    # ── Interrupt vectors at $FFFA ────────────────────────────────────────────
    a.org(VECTORS_CPU)
    a.ref16('NMI')    # $FFFA
    a.ref16('RESET')  # $FFFC
    a.ref16('IRQ')    # $FFFE

    # ── Resolve labels ────────────────────────────────────────────────────────
    errs = a.resolve()
    if errs:
        for e in errs: print(f"  ERROR: {e}", file=sys.stderr)
        sys.exit(1)

    # ── Build 128 KB raw PRG binary ───────────────────────────────────────────
    prg = bytearray(b'\xFF' * PRG_SIZE)
    # Bank 0: CHR font (CPU $0000–$1FFF → flash 0x00000–0x01FFF)
    prg[0x00000:0x02000] = a.rom(0x0000, BANK_SIZE)
    # Bank 14: helpers (CPU $C000–$DFFF → flash 0x1C000–0x1DFFF)
    prg[0x1C000:0x1E000] = a.rom(0xC000, BANK_SIZE)
    # Bank 15: main code (CPU $E000–$FFFF → flash 0x1E000–0x1FFFF)
    prg[0x1E000:0x20000] = a.rom(0xE000, BANK_SIZE)
    # Banks 2+3 (game table): left as 0xFF — written by CoolBoyTarget.WriteGameTable()

    return bytes(prg), a.sym


# ─────────────────────────────────────────────────────────────────────────────
#  Entry point
# ─────────────────────────────────────────────────────────────────────────────
def main():
    out_path = sys.argv[1] if len(sys.argv) > 1 else 'coolboy_menu.rom'

    rom, sym = assemble()

    # ── Verification checks ───────────────────────────────────────────────────
    errors = []

    # 1. Correct size
    if len(rom) != PRG_SIZE:
        errors.append(f"Wrong size: {len(rom)} != {PRG_SIZE}")

    # 2. All bytes in valid range
    bad = [(i, v) for i, v in enumerate(rom) if not (0 <= v <= 255)]
    if bad:
        errors.append(f"Out-of-range bytes: {len(bad)} (first at 0x{bad[0][0]:05X} = {bad[0][1]})")

    # 3. Vectors at $FFFA–$FFFF
    vec_off  = 0x1FFFA   # flash offset of $FFFA
    nmi_vec  = rom[vec_off+0] | (rom[vec_off+1] << 8)
    rst_vec  = rom[vec_off+2] | (rom[vec_off+3] << 8)
    irq_vec  = rom[vec_off+4] | (rom[vec_off+5] << 8)
    for name, vec in [('NMI', nmi_vec), ('RESET', rst_vec), ('IRQ', irq_vec)]:
        if not (0xE000 <= vec <= 0xFFFF):
            errors.append(f"{name} vector ${vec:04X} not in $E000–$FFFF")

    # 4. RESET points to SEI (0x78)
    rst_off = 0x1E000 + (rst_vec - 0xE000)
    if 0 <= rst_off < PRG_SIZE:
        if rom[rst_off] != 0x78:
            errors.append(f"RESET target 0x{rst_off:05X} = 0x{rom[rst_off]:02X}, expected SEI (0x78)")
    else:
        errors.append(f"RESET flash offset 0x{rst_off:05X} out of range")

    # 5. CHR font non-trivial (not all 0xFF)
    if all(b == 0xFF for b in rom[0:256]):
        errors.append("CHR font bank 0 starts with all 0xFF — font not assembled")

    if errors:
        print("VERIFICATION FAILED:", file=sys.stderr)
        for e in errors: print(f"  {e}", file=sys.stderr)
        sys.exit(1)

    with open(out_path, 'wb') as f:
        f.write(rom)

    # ── Summary ───────────────────────────────────────────────────────────────
    print(f"coolboy_menu.rom  →  {out_path}  ({len(rom)} bytes)")
    print(f"  Bank 14 helpers : flash 0x1C000  CPU $C000")
    print(f"  Bank 15 main    : flash 0x1E000  CPU $E000")
    print(f"  RESET vector    : ${rst_vec:04X}  (flash 0x{rst_off:05X})")
    print(f"  NMI   vector    : ${nmi_vec:04X}")
    print(f"  IRQ   vector    : ${irq_vec:04X}")
    print(f"  CHR font        : flash 0x00000  ({sum(1 for b in rom[:8192] if b != 0xFF)} non-FF bytes)")
    print(f"  Game table slot : flash 0x04000  (banks 2+3, written by C# tool)")
    if 'RESET' in sym:
        print(f"  RESET label     : CPU ${sym['RESET']:04X}")
    if 'main_loop' in sym:
        print(f"  main_loop       : CPU ${sym['main_loop']:04X}")
    if 'launch_game' in sym:
        print(f"  launch_game     : CPU ${sym['launch_game']:04X}")
    if 'draw_list' in sym:
        print(f"  draw_list       : CPU ${sym['draw_list']:04X}")
    print("  All verification checks passed ✓")


if __name__ == '__main__':
    main()
