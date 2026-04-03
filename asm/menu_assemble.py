#!/usr/bin/env python3
"""
VTxx OneBus Multicart Menu v6

Changes from v5:
  1. Title "MULTICARIK" in NES palette colour 0x3B (aqua) via BG palette 1
  2. Left padding 2 tiles; top/bottom screen padding (games rows 6-25, VISIBLE=20)
  3. Game names in palette colour 0x36 (light orange) via BG palette 2
  4. Page navigation: Down at page-end jumps to next page; Left/Right change page
  5. Game number prefix "NN. " before each game name (1-based)
"""
import sys

class A:
    def __init__(self):
        self.pc=0; self.mem={}; self.sym={}; self.fx=[]
    def org(self,v): self.pc=v
    def b(self,*bs):
        for v in bs: self.mem[self.pc]=int(v)&0xFF; self.pc+=1
    def w(self,v): self.b(v&0xFF,v>>8)
    def lbl(self,n):
        assert n not in self.sym,f"dup {n}"; self.sym[n]=self.pc
    def ref16(self,n): self.fx.append((self.pc,n,'a16')); self.w(0)
    def ref_lo(self,n): self.fx.append((self.pc,n,'lo')); self.b(0)
    def ref_hi(self,n): self.fx.append((self.pc,n,'hi')); self.b(0)
    def bref(self,n): self.fx.append((self.pc,n,'r8')); self.b(0)
    def resolve(self):
        errs=[]
        for pc,n,k in self.fx:
            if n not in self.sym: errs.append(f"undef:{n}"); continue
            t=self.sym[n]
            if k=='a16': self.mem[pc]=t&0xFF; self.mem[pc+1]=t>>8
            elif k=='lo': self.mem[pc]=t&0xFF
            elif k=='hi': self.mem[pc]=t>>8
            elif k=='r8':
                d=t-(pc+1)
                if d<-128 or d>127: errs.append(f"OOR:{n}@{pc:04X}>{t:04X}"); continue
                self.mem[pc]=d&0xFF
        return errs
    def rom(self,s,n): return bytes(self.mem.get(s+i,0xFF) for i in range(n))
    def SEI(self): self.b(0x78)
    def CLD(self): self.b(0xD8)
    def CLC(self): self.b(0x18)
    def SEC(self): self.b(0x38)
    def RTS(self): self.b(0x60)
    def RTI(self): self.b(0x40)
    def TXS(self): self.b(0x9A)
    def PHA(self): self.b(0x48)
    def PLA(self): self.b(0x68)
    def TXA(self): self.b(0x8A)
    def TAX(self): self.b(0xAA)
    def TYA(self): self.b(0x98)
    def TAY(self): self.b(0xA8)
    def INX(self): self.b(0xE8)
    def INY(self): self.b(0xC8)
    def DEX(self): self.b(0xCA)
    def DEY(self): self.b(0x88)
    def ASL(self): self.b(0x0A)
    def LSR(self): self.b(0x4A)
    def ROL(self): self.b(0x2A)
    def LDA_I(self,v): self.b(0xA9,v)
    def LDX_I(self,v): self.b(0xA2,v)
    def LDY_I(self,v): self.b(0xA0,v)
    def CMP_I(self,v): self.b(0xC9,v)
    def CPX_I(self,v): self.b(0xE0,v)
    def AND_I(self,v): self.b(0x29,v)
    def ORA_I(self,v): self.b(0x09,v)
    def ADC_I(self,v): self.b(0x69,v)
    def SBC_I(self,v): self.b(0xE9,v)
    def EOR_I(self,v): self.b(0x49,v)
    def LDA_Z(self,z): self.b(0xA5,z)
    def LDX_Z(self,z): self.b(0xA6,z)
    def STA_Z(self,z): self.b(0x85,z)
    def INC_Z(self,z): self.b(0xE6,z)
    def DEC_Z(self,z): self.b(0xC6,z)
    def CMP_Z(self,z): self.b(0xC5,z)
    def CPX_Z(self,z): self.b(0xE4,z)
    def AND_Z(self,z): self.b(0x25,z)
    def ORA_Z(self,z): self.b(0x05,z)
    def EOR_Z(self,z): self.b(0x45,z)
    def ADC_Z(self,z): self.b(0x65,z)
    def SBC_Z(self,z): self.b(0xE5,z)
    def ROL_Z(self,z): self.b(0x26,z)
    def STA_ZX(self,z):self.b(0x95,z)
    def LDA_A(self,a): self.b(0xAD,a&0xFF,a>>8)
    def STA_A(self,a): self.b(0x8D,a&0xFF,a>>8)
    def BIT_A(self,a): self.b(0x2C,a&0xFF,a>>8)
    def LDA_AX(self,a): self.b(0xBD,a&0xFF,a>>8)
    def STA_AX(self,a): self.b(0x9D,a&0xFF,a>>8)
    def LDA_AY(self,a): self.b(0xB9,a&0xFF,a>>8)
    def LDA_AXr(self,n): self.b(0xBD); self.ref16(n)
    def LDA_IY(self,z): self.b(0xB1,z)
    def JMP_A(self,a): self.b(0x4C,a&0xFF,a>>8)
    def JMP_I(self,a): self.b(0x6C,a&0xFF,a>>8)
    def JSR(self,n): self.b(0x20); self.ref16(n)
    def JMP(self,n): self.b(0x4C); self.ref16(n)
    def BEQ(self,n): self.b(0xF0); self.bref(n)
    def BNE(self,n): self.b(0xD0); self.bref(n)
    def BCC(self,n): self.b(0x90); self.bref(n)
    def BCS(self,n): self.b(0xB0); self.bref(n)
    def BPL(self,n): self.b(0x10); self.bref(n)
    def BMI(self,n): self.b(0x30); self.bref(n)
    def LDA_lo(self,n): self.b(0xA9); self.ref_lo(n)
    def LDA_hi(self,n): self.b(0xA9); self.ref_hi(n)
    def str_z(self,t):
        for c in t.encode('ascii'): self.b(c)
        self.b(0)

def assemble():
    a = A()

    PPUCTRL=0x2000; PPUMASK=0x2001; PPUSTAT=0x2002
    PPUSCR=0x2005; PPUADDR=0x2006; PPUDATA=0x2007
    OAMDMA=0x4014; APUSTAT=0x4015; APUFRAME=0x4017; JOY1=0x4016
    CHRINT=0x2018; CHRVB0S=0x201A
    PRGOUTER=0x4100; PRGB0=0x4107; PRGB1=0x4108
    PRGB2=0x4109; PRGB3=0x410A; PRGMODE=0x410B; MIRROR=0xA000
    GAMELIST=0x9000; CFGTABLE=0xC000
    ATTRTABLE=0x23C0  # nametable 0 attribute table

    # Zero page layout
    CUR=0x00   # selected game index (0-based)
    TOP=0x01   # first visible game index (top of current page)
    GCN=0x02   # total game count
    JNN=0x03   # new buttons this frame (edge-triggered)
    JCR=0x04   # current buttons
    JPR=0x05   # previous buttons
    NMI_DONE=0x06  # NMI sets 1 each frame
    FRM=0x07
    SR0=0x08   # setrow scratch
    TMP=0x09
    PLO=0x0A   # name pointer lo
    PHI=0x0B   # name pointer hi
    ROW=0x0C   # current draw row (0..VISIBLE-1)
    NUM=0x0D   # scratch for number rendering
    # ZP $10-$12: precomputed launch values
    ZP_4100=0x10
    ZP_410A=0x11
    ZP_410B=0x12
    # ZP stub at $40 (23 bytes)
    STUB=0x40

    # Layout constants
    VISIBLE    = 22   # game rows on screen (rows 4-25)
    LIST_ROW   = 4    # first game row on screen
    HDR_ROW    = 1    # header row
    FTR_ROW    = 28   # footer row

    OAM_PAGE=0x02; CFG=0x0300
    BTN_A=0x80; BTN_ST=0x10; BTN_UP=0x08; BTN_DN=0x04
    BTN_LF=0x02; BTN_RT=0x01

    a.org(0xE000)

    # ── RESET ────────────────────────────────────────────────────────────────
    a.lbl('RESET')
    a.SEI(); a.CLD(); a.LDX_I(0xFF); a.TXS()
    a.LDA_I(0); a.STA_A(PPUCTRL); a.STA_A(PPUMASK)
    a.LDA_I(0x40); a.STA_A(APUFRAME)
    a.LDA_I(0x0F); a.STA_A(APUSTAT)
    # PRG banking
    a.LDA_I(0x00); a.STA_A(PRGOUTER)
    a.LDA_I(0x3C); a.STA_A(PRGB0)
    a.LDA_I(0x3D); a.STA_A(PRGB1)
    # CHR: tile N = ASCII char N
    a.LDA_I(0x10); a.STA_A(CHRINT)
    a.LDA_I(0x00); a.STA_A(CHRVB0S)
    a.LDA_I(0x01); a.STA_A(MIRROR)

    # PPU warm-up: wait two VBlanks
    a.JSR('wvbl'); a.JSR('wvbl')

    # Clear ZP
    a.LDA_I(0); a.LDX_I(0)
    a.lbl('czp'); a.STA_ZX(0); a.INX(); a.BNE('czp')
    # Hide OAM
    a.LDA_I(0xFF); a.LDX_I(0)
    a.lbl('coam'); a.STA_AX(0x0200); a.INX(); a.BNE('coam')

    # Install ZP launch stub (23 bytes at $40)
    # Writes $4100, $410A, $410B from ZP RAM (immune to bank switching)
    stub=[0xA5,ZP_4100, 0x8D,0x00,0x41,   # LDA ZP_4100, STA $4100
          0xA5,ZP_410A,  0x8D,0x0A,0x41,   # LDA ZP_410A, STA $410A
          0xA5,ZP_410B,  0x8D,0x0B,0x41,   # LDA ZP_410B, STA $410B
          0xA9,0x00,     0x8D,0x0E,0x02,   # LDA #0, STA $020E
          0x6C,0xFC,0xFF]                  # JMP ($FFFC)
    for i,v in enumerate(stub):
        a.LDA_I(v); a.STA_Z(STUB+i)

    # PPU static init
    a.JSR('loadpal'); a.JSR('clrnt'); a.JSR('setattr')
    a.JSR('drawhdr'); a.JSR('drawftr')

    # Read game count
    a.LDA_A(GAMELIST); a.STA_Z(GCN)
    a.LDA_A(GAMELIST+1); a.BEQ('gcok')
    a.LDA_I(0xFF); a.STA_Z(GCN)
    a.lbl('gcok')

    a.LDA_I(0); a.STA_Z(CUR); a.STA_Z(TOP)
    a.STA_Z(FRM); a.STA_Z(JCR); a.STA_Z(JPR); a.STA_Z(NMI_DONE)

    # Draw initial list
    a.JSR('drawlist')

    # Enable PPU + NMI
    a.LDA_I(0x88); a.STA_A(PPUCTRL)
    a.LDA_I(0x0E); a.STA_A(PPUMASK)
    a.LDA_I(0); a.STA_A(PPUSCR); a.STA_A(PPUSCR)

    # ── MAIN LOOP ────────────────────────────────────────────────────────────
    a.lbl('MAIN')
    a.lbl('mwait')
    a.LDA_Z(NMI_DONE); a.BEQ('mwait')
    a.LDA_I(0); a.STA_Z(NMI_DONE)
    a.JSR('readjoy')
    a.JSR('input')
    a.JMP('MAIN')

    # ── NMI ──────────────────────────────────────────────────────────────────
    a.lbl('NMI')
    a.PHA(); a.TXA(); a.PHA(); a.TYA(); a.PHA()
    a.LDA_I(OAM_PAGE); a.STA_A(OAMDMA)
    a.INC_Z(FRM)
    a.BIT_A(PPUSTAT)
    a.LDA_I(0); a.STA_A(PPUSCR); a.STA_A(PPUSCR)
    a.LDA_I(1); a.STA_Z(NMI_DONE)
    a.PLA(); a.TAY(); a.PLA(); a.TAX(); a.PLA()
    a.RTI()

    a.lbl('IRQ'); a.RTI()

    # ── wvbl ─────────────────────────────────────────────────────────────────
    a.lbl('wvbl'); a.BIT_A(PPUSTAT)
    a.lbl('wvl'); a.BIT_A(PPUSTAT); a.BPL('wvl'); a.RTS()

    # ── setrow: set PPUADDR to NT0 row A ─────────────────────────────────────
    a.lbl('setrow')
    a.STA_Z(TMP); a.AND_I(0x07)
    a.ASL(); a.ASL(); a.ASL(); a.ASL(); a.ASL()
    a.STA_Z(SR0)
    a.LDA_Z(TMP); a.LSR(); a.LSR(); a.LSR()
    a.CLC(); a.ADC_I(0x20)
    a.BIT_A(PPUSTAT)
    a.STA_A(PPUADDR)
    a.LDA_Z(SR0); a.STA_A(PPUADDR)
    a.RTS()

    # ── loadpal: load 32-byte palette ────────────────────────────────────────
    # BG palette layout:
    #   Pal 0 ($3F00): 0x0F, 0x30, 0x2C, 0x16  — default (white text)
    #   Pal 1 ($3F04): 0x0F, 0x3B, 0x2C, 0x16  — title colour 0x3B (aqua)
    #   Pal 2 ($3F08): 0x0F, 0x36, 0x2C, 0x16  — game names 0x36 (light orange)
    #   Pal 3 ($3F0C): 0x0F, 0x30, 0x20, 0x10  — footer
    a.lbl('loadpal')
    a.BIT_A(PPUSTAT)
    a.LDA_I(0x3F); a.STA_A(PPUADDR)
    a.LDA_I(0x00); a.STA_A(PPUADDR)
    a.LDX_I(0)
    a.lbl('plp'); a.LDA_AXr('paldata'); a.STA_A(PPUDATA)
    a.INX(); a.CPX_I(0x20); a.BNE('plp'); a.RTS()
    a.lbl('paldata')
    # BG palettes (each palette: background colour, then colours 1,2,3)
    # Colour index 1 = the main text colour for that palette
    # CHR font has BOTH bitplanes set to the same value, so all lit pixels
    # use colour INDEX 3 of the active palette (not index 1).
    # Colour index 3 is the last byte in each 4-byte palette entry.
    a.b(0x0F,0x00,0x2C,0x30,   # BG pal 0: black bg, white text (idx3=0x30)
        0x0F,0x00,0x2C,0x3B,   # BG pal 1: black bg, AQUA title (idx3=0x3B)
        0x0F,0x00,0x2C,0x36,   # BG pal 2: black bg, LT-ORANGE games (idx3=0x36)
        0x0F,0x00,0x20,0x30,   # BG pal 3: black bg, white footer (idx3=0x30)
        # Sprite palettes (unused)
        0x0F,0x16,0x26,0x36, 0x0F,0x30,0x20,0x10,
        0x0F,0x30,0x20,0x10, 0x0F,0x30,0x20,0x10)

    # ── clrnt: fill NT0 with spaces (tile $20) ────────────────────────────────
    a.lbl('clrnt')
    a.BIT_A(PPUSTAT)
    a.LDA_I(0x20); a.STA_A(PPUADDR)
    a.LDA_I(0x00); a.STA_A(PPUADDR)
    a.LDA_I(0x20); a.LDX_I(0); a.LDY_I(4)
    a.lbl('cnl'); a.STA_A(PPUDATA); a.INX(); a.BNE('cnl')
    a.DEY(); a.BNE('cnl'); a.RTS()

    # ── setattr: write attribute table for colour regions ─────────────────────
    # Attribute table at $23C0 (64 bytes, 8×8 grid of 4×4-tile blocks).
    # Each byte: D7D6=BR, D5D4=BL, D3D2=TR, D1D0=TL (palette index 0-3)
    #
    # Header at row 1: attr row 0 (rows 0-3).
    #   top-half (rows 0-1): D1D0 = TL quad, D3D2 = TR quad
    #   We want palette 1 for top half of all columns.
    #   D3D2=01, D1D0=01 → bottom half stays 00 → byte = 0b00000101 = 0x05
    #   8 bytes for attr row 0: all 0x05
    #
    # Game rows 4-25: attr rows 1-6 (rows 4-27, covering game area + padding).
    #   We want palette 2 for all quadrants.
    #   D7D6=10, D5D4=10, D3D2=10, D1D0=10 → 0b10101010 = 0xAA
    #   6 rows × 8 bytes = 48 bytes, all 0xAA
    #
    # Footer at row 28: attr row 7 (rows 28-29).
    #   Palette 0 (default) = 0x00. 8 bytes, all 0x00.
    a.lbl('setattr')
    a.BIT_A(PPUSTAT)
    a.LDA_I(0x23); a.STA_A(PPUADDR)
    a.LDA_I(0xC0); a.STA_A(PPUADDR)  # $23C0 = start of attr table

    # 8 bytes = 0x55 (all quadrants palette 1 — covers row 3 for title)
    a.LDA_I(0x55); a.LDX_I(8)
    a.lbl('sa_h'); a.STA_A(PPUDATA); a.DEX(); a.BNE('sa_h')

    # 48 bytes = 0xAA (game rows, palette 2)
    a.LDA_I(0xAA); a.LDX_I(48)
    a.lbl('sa_g'); a.STA_A(PPUDATA); a.DEX(); a.BNE('sa_g')

    # 8 bytes = 0x00 (footer, palette 0)
    a.LDA_I(0x00); a.LDX_I(8)
    a.lbl('sa_f'); a.STA_A(PPUDATA); a.DEX(); a.BNE('sa_f')
    a.RTS()

    # ── wrtstr: write null-terminated string from PLO/PHI ─────────────────────
    a.lbl('wrtstr'); a.LDY_I(0)
    a.lbl('wsl'); a.LDA_IY(PLO); a.BEQ('wsd')
    a.STA_A(PPUDATA); a.INY(); a.BNE('wsl')
    a.lbl('wsd'); a.RTS()

    # ── drawhdr: title "MULTICARIK" centred at row 1 ─────────────────────────
    # "MULTICARIK" = 10 chars. Centred on 32-col screen = 11 spaces each side.
    # "           MULTICARIK           " (32 chars)
    a.lbl('drawhdr')
    a.LDA_I(HDR_ROW); a.JSR('setrow')
    a.LDA_lo('hdrtxt'); a.STA_Z(PLO)
    a.LDA_hi('hdrtxt'); a.STA_Z(PHI)
    a.JMP('wrtstr')
    a.lbl('hdrtxt')
    # 11 spaces + MULTICARIK + 11 spaces = 32 chars
    a.str_z("           MULTICARIK           ")

    # ── drawftr: footer at row 28 ─────────────────────────────────────────────
    a.lbl('drawftr')
    a.LDA_I(FTR_ROW); a.JSR('setrow')
    a.LDA_lo('ftrtxt'); a.STA_Z(PLO)
    a.LDA_hi('ftrtxt'); a.STA_Z(PHI)
    a.JMP('wrtstr')
    a.lbl('ftrtxt')
    a.str_z(" UP/DN:SCROLL L/R:PAGE A:LAUNCH ")

    # ── drawlist ──────────────────────────────────────────────────────────────
    # Draw VISIBLE game rows starting from TOP.
    # Row layout (each screen row = LIST_ROW + ROW):
    #   Col 0:   space (left pad 1)
    #   Col 1:   space (left pad 2)
    #   Col 2:   cursor arrow (0x1A) if this is CUR, else space
    #   Col 3-4: game number tens digit, units digit (1-based)
    #   Col 5:   '.' (0x2E)
    #   Col 6:   space
    #   Col 7-31: game name (up to 25 chars)
    #
    # PLO/PHI walks through GAMELIST names linearly.
    a.lbl('drawlist')

    # Point PLO/PHI at first name in list (GAMELIST+16 skips the count word)
    a.LDA_I((GAMELIST+16)&0xFF); a.STA_Z(PLO)
    a.LDA_I((GAMELIST+16)>>8);   a.STA_Z(PHI)

    # Skip TOP names (advance pointer past TOP entries)
    a.LDA_Z(TOP); a.BEQ('dl_nosk')
    a.STA_Z(TMP)
    a.lbl('dl_sk')
    a.LDY_I(0)
    a.lbl('dl_skc')
    a.LDA_IY(PLO); a.INY(); a.BNE('dl_skb'); a.JMP('dl_skd')
    a.lbl('dl_skb'); a.CMP_I(0); a.BNE('dl_skc')
    a.lbl('dl_skd')
    a.TYA(); a.CLC(); a.ADC_Z(PLO); a.STA_Z(PLO)
    a.BCC('dl_skn'); a.INC_Z(PHI)
    a.lbl('dl_skn')
    a.DEC_Z(TMP); a.BNE('dl_sk')
    a.lbl('dl_nosk')

    # Draw rows 0..VISIBLE-1
    a.LDA_I(0); a.STA_Z(ROW)
    a.lbl('dl_row')
    # Done if ROW >= VISIBLE
    a.LDA_Z(ROW); a.CMP_I(VISIBLE); a.BCS('dl_done')
    # Done if TOP+ROW >= GCN
    a.CLC(); a.ADC_Z(TOP); a.CMP_Z(GCN); a.BCS('dl_done')

    # Set PPU address for this row (LIST_ROW + ROW)
    a.LDA_Z(ROW); a.CLC(); a.ADC_I(LIST_ROW); a.JSR('setrow')

    # Col 0-1: left padding (2 spaces)
    a.LDA_I(0x20); a.STA_A(PPUDATA); a.STA_A(PPUDATA)

    # Col 2: cursor arrow or space
    a.LDA_Z(ROW); a.CLC(); a.ADC_Z(TOP)   # A = game index
    a.CMP_Z(CUR)
    a.BNE('dl_noc')
    a.LDA_I(0x1A); a.STA_A(PPUDATA); a.JMP('dl_num')
    a.lbl('dl_noc')
    a.LDA_I(0x20); a.STA_A(PPUDATA)

    # Col 3-6: game number "NN. " (1-based)
    # game number = TOP+ROW+1
    a.lbl('dl_num')
    a.LDA_Z(TOP); a.CLC(); a.ADC_Z(ROW)   # A = TOP+ROW (0-based index)
    a.CLC(); a.ADC_I(1)                    # A = game number (1-based)
    a.STA_Z(NUM)

    # Tens digit: NUM / 10
    # Use repeated subtraction (simple, fits in ZP space)
    # A = tens = 0, subtract 10 while NUM >= 10
    a.LDA_I(0x30)                          # ASCII '0'
    a.lbl('dl_ten')
    a.LDA_Z(NUM); a.CMP_I(10); a.BCC('dl_ten_done')
    a.SBC_I(10); a.STA_Z(NUM)             # NUM -= 10
    a.LDA_A(PPUDATA-1)                    # dummy read to get to next write
    # Actually we need to track tens separately. Use TMP.
    # Restart: compute tens in TMP, units in NUM
    a.lbl('dl_ten_done')                  # can't reach here cleanly - redo below
    a.RTS()                               # placeholder - will replace whole block

    # ── REDO number rendering using scratch registers ──────────────────────────
    # Replace the number rendering above with a cleaner approach.
    # We'll use a subroutine: JSR 'drawnum'
    # On entry: A = number (1-based, 1..255)
    # Outputs: writes 2 ASCII digits + '.' + ' ' to PPUDATA (4 bytes total)
    # Uses: TMP, NUM
    a.RTS()  # end of placeholder - this whole section replaced below

    return None  # signal to redo

def assemble_v6():
    a = A()

    PPUCTRL=0x2000; PPUMASK=0x2001; PPUSTAT=0x2002
    PPUSCR=0x2005; PPUADDR=0x2006; PPUDATA=0x2007
    OAMDMA=0x4014; APUSTAT=0x4015; APUFRAME=0x4017; JOY1=0x4016
    CHRINT=0x2018; CHRVB0S=0x201A
    PRGOUTER=0x4100; PRGB0=0x4107; PRGB1=0x4108
    PRGB2=0x4109; PRGB3=0x410A; PRGMODE=0x410B; MIRROR=0xA000
    GAMELIST=0x9000; CFGTABLE=0xC000

    # Zero page
    CUR=0x00; TOP=0x01; GCN=0x02; JNN=0x03; JCR=0x04; JPR=0x05
    NMI_DONE=0x06; FRM=0x07; SR0=0x08; TMP=0x09; PLO=0x0A; PHI=0x0B
    ROW=0x0C; NUM=0x0D; TEN=0x0E
    ZP_4100=0x10; ZP_410A=0x11; ZP_410B=0x12
    STUB=0x40

    VISIBLE=20; LIST_ROW=6; HDR_ROW=3; FTR_ROW=28
    OAM_PAGE=0x02; CFG=0x0300
    BTN_A=0x80; BTN_ST=0x10; BTN_UP=0x08; BTN_DN=0x04
    BTN_LF=0x02; BTN_RT=0x01

    a.org(0xE000)

    # ── RESET ────────────────────────────────────────────────────────────────
    a.lbl('RESET')
    a.SEI(); a.CLD(); a.LDX_I(0xFF); a.TXS()
    a.LDA_I(0); a.STA_A(PPUCTRL); a.STA_A(PPUMASK)
    a.LDA_I(0x40); a.STA_A(APUFRAME)
    a.LDA_I(0x0F); a.STA_A(APUSTAT)
    a.LDA_I(0x00); a.STA_A(PRGOUTER)
    a.LDA_I(0x3C); a.STA_A(PRGB0)
    a.LDA_I(0x3D); a.STA_A(PRGB1)
    a.LDA_I(0x10); a.STA_A(CHRINT)
    a.LDA_I(0x00); a.STA_A(CHRVB0S)
    a.LDA_I(0x01); a.STA_A(MIRROR)
    a.JSR('wvbl'); a.JSR('wvbl')

    # Clear ZP
    a.LDA_I(0); a.LDX_I(0)
    a.lbl('czp'); a.STA_ZX(0); a.INX(); a.BNE('czp')
    # Hide OAM
    a.LDA_I(0xFF); a.LDX_I(0)
    a.lbl('coam'); a.STA_AX(0x0200); a.INX(); a.BNE('coam')

    # Install ZP stub (must happen AFTER ZP clear)
    stub=[0xA5,ZP_4100, 0x8D,0x00,0x41,
          0xA5,ZP_410A,  0x8D,0x0A,0x41,
          0xA5,ZP_410B,  0x8D,0x0B,0x41,
          0xA9,0x00,     0x8D,0x0E,0x02,
          0x6C,0xFC,0xFF]
    for i,v in enumerate(stub):
        a.LDA_I(v); a.STA_Z(STUB+i)

    # PPU init
    a.JSR('loadpal'); a.JSR('clrnt'); a.JSR('setattr')
    a.JSR('drawhdr'); a.JSR('drawftr')

    # Read game count (max 255)
    a.LDA_A(GAMELIST); a.STA_Z(GCN)
    a.LDA_A(GAMELIST+1); a.BEQ('gcok')
    a.LDA_I(0xFF); a.STA_Z(GCN)
    a.lbl('gcok')

    a.LDA_I(0); a.STA_Z(CUR); a.STA_Z(TOP)
    a.STA_Z(FRM); a.STA_Z(JCR); a.STA_Z(JPR); a.STA_Z(NMI_DONE)
    a.JSR('drawlist')

    # Enable rendering
    a.LDA_I(0x88); a.STA_A(PPUCTRL)
    a.LDA_I(0x0E); a.STA_A(PPUMASK)
    a.LDA_I(0); a.STA_A(PPUSCR); a.STA_A(PPUSCR)

    # ── MAIN LOOP ────────────────────────────────────────────────────────────
    a.lbl('MAIN')
    a.lbl('mwait'); a.LDA_Z(NMI_DONE); a.BEQ('mwait')
    a.LDA_I(0); a.STA_Z(NMI_DONE)
    a.JSR('readjoy'); a.JSR('input')
    a.JMP('MAIN')

    # ── NMI ──────────────────────────────────────────────────────────────────
    a.lbl('NMI')
    a.PHA(); a.TXA(); a.PHA(); a.TYA(); a.PHA()
    a.LDA_I(OAM_PAGE); a.STA_A(OAMDMA)
    a.INC_Z(FRM)
    a.BIT_A(PPUSTAT)
    a.LDA_I(0); a.STA_A(PPUSCR); a.STA_A(PPUSCR)
    a.LDA_I(1); a.STA_Z(NMI_DONE)
    a.PLA(); a.TAY(); a.PLA(); a.TAX(); a.PLA()
    a.RTI()

    a.lbl('IRQ'); a.RTI()

    # ── wvbl ─────────────────────────────────────────────────────────────────
    a.lbl('wvbl'); a.BIT_A(PPUSTAT)
    a.lbl('wvl'); a.BIT_A(PPUSTAT); a.BPL('wvl'); a.RTS()

    # ── setrow ────────────────────────────────────────────────────────────────
    a.lbl('setrow')
    a.STA_Z(TMP); a.AND_I(0x07)
    a.ASL(); a.ASL(); a.ASL(); a.ASL(); a.ASL()
    a.STA_Z(SR0)
    a.LDA_Z(TMP); a.LSR(); a.LSR(); a.LSR()
    a.CLC(); a.ADC_I(0x20)
    a.BIT_A(PPUSTAT); a.STA_A(PPUADDR)
    a.LDA_Z(SR0); a.STA_A(PPUADDR)
    a.RTS()

    # ── loadpal ───────────────────────────────────────────────────────────────
    a.lbl('loadpal')
    a.BIT_A(PPUSTAT)
    a.LDA_I(0x3F); a.STA_A(PPUADDR)
    a.LDA_I(0x00); a.STA_A(PPUADDR)
    a.LDX_I(0)
    a.lbl('plp'); a.LDA_AXr('paldata'); a.STA_A(PPUDATA)
    a.INX(); a.CPX_I(0x20); a.BNE('plp'); a.RTS()
    a.lbl('paldata')
    # CHR tiles have BOTH bitplanes identical → lit pixels = colour index 3.
    # Target colour MUST be at index 3 (last byte) of each palette entry.
    a.b(0x0F,0x00,0x2C,0x30,   # BG pal 0: index3=0x30 WHITE  (default)
        0x0F,0x00,0x2C,0x3B,   # BG pal 1: index3=0x3B AQUA   (title)
        0x0F,0x00,0x2C,0x36,   # BG pal 2: index3=0x36 LT-ORANGE (games)
        0x0F,0x00,0x20,0x30,   # BG pal 3: index3=0x30 WHITE  (footer)
        0x0F,0x16,0x26,0x36, 0x0F,0x30,0x20,0x10,
        0x0F,0x30,0x20,0x10, 0x0F,0x30,0x20,0x10)

    # ── clrnt ─────────────────────────────────────────────────────────────────
    a.lbl('clrnt')
    a.BIT_A(PPUSTAT)
    a.LDA_I(0x20); a.STA_A(PPUADDR)
    a.LDA_I(0x00); a.STA_A(PPUADDR)
    a.LDA_I(0x20); a.LDX_I(0); a.LDY_I(4)
    a.lbl('cnl'); a.STA_A(PPUDATA); a.INX(); a.BNE('cnl')
    a.DEY(); a.BNE('cnl'); a.RTS()

    # ── setattr: write attribute table ────────────────────────────────────────
    # $23C0: 8 bytes = 0x05 (header row → BG pal 1, aqua)
    # $23C8: 48 bytes = 0xAA (game rows → BG pal 2, light-orange)
    # $23F8: 8 bytes = 0x00 (footer → BG pal 0, white)
    a.lbl('setattr')
    a.BIT_A(PPUSTAT)
    a.LDA_I(0x23); a.STA_A(PPUADDR); a.LDA_I(0xC0); a.STA_A(PPUADDR)
    a.LDA_I(0x55); a.LDX_I(8)  # 0x55 = all quadrants palette 1 (covers row 3)
    a.lbl('sa1'); a.STA_A(PPUDATA); a.DEX(); a.BNE('sa1')
    a.LDA_I(0xAA); a.LDX_I(48)
    a.lbl('sa2'); a.STA_A(PPUDATA); a.DEX(); a.BNE('sa2')
    a.LDA_I(0x00); a.LDX_I(8)
    a.lbl('sa3'); a.STA_A(PPUDATA); a.DEX(); a.BNE('sa3')
    a.RTS()

    # ── wrtstr ────────────────────────────────────────────────────────────────
    a.lbl('wrtstr'); a.LDY_I(0)
    a.lbl('wsl'); a.LDA_IY(PLO); a.BEQ('wsd')
    a.STA_A(PPUDATA); a.INY(); a.BNE('wsl')
    a.lbl('wsd'); a.RTS()

    # ── drawhdr ───────────────────────────────────────────────────────────────
    # "MULTICARIK" centred: 11 leading spaces + 10 chars + 11 trailing = 32
    a.lbl('drawhdr')
    a.LDA_I(HDR_ROW); a.JSR('setrow')
    a.LDA_lo('hdrtxt'); a.STA_Z(PLO)
    a.LDA_hi('hdrtxt'); a.STA_Z(PHI)
    a.JMP('wrtstr')
    a.lbl('hdrtxt'); a.str_z("           MULTICARIK           ")

    # ── drawftr ───────────────────────────────────────────────────────────────
    a.lbl('drawftr')
    a.LDA_I(FTR_ROW); a.JSR('setrow')
    a.LDA_lo('ftrtxt'); a.STA_Z(PLO)
    a.LDA_hi('ftrtxt'); a.STA_Z(PHI)
    a.JMP('wrtstr')
    a.lbl('ftrtxt'); a.str_z(" UP/DN:SCROLL L/R:PAGE A:LAUNCH ")

    # ── drawnum: write "NN. " (4 chars) to PPUDATA ────────────────────────────
    # Entry: A = game number (1..255)
    # Uses: TMP (units), TEN (tens digit character)
    # Writes: tens-digit char, units-digit char, '.', ' '
    a.lbl('drawnum')
    a.STA_Z(TMP)            # TMP = number (will become units)
    a.LDA_I(0x30)           # TEN = '0' initially
    a.STA_Z(TEN)
    # Subtract 10 repeatedly to get tens digit
    a.lbl('dn_ten')
    a.LDA_Z(TMP); a.CMP_I(10); a.BCC('dn_done')
    a.SBC_I(10); a.STA_Z(TMP)   # TMP = units remainder
    a.INC_Z(TEN)                 # TEN advances through '0','1','2'...
    a.JMP('dn_ten')
    a.lbl('dn_done')
    # Write tens digit
    a.LDA_Z(TEN); a.STA_A(PPUDATA)
    # Write units digit: TMP + '0'
    a.LDA_Z(TMP); a.CLC(); a.ADC_I(0x30); a.STA_A(PPUDATA)
    # Write '.' and ' '
    a.LDA_I(0x2E); a.STA_A(PPUDATA)   # '.'
    a.LDA_I(0x20); a.STA_A(PPUDATA)   # ' '
    a.RTS()

    # ── drawlist ──────────────────────────────────────────────────────────────
    # For each visible row (0..VISIBLE-1):
    #   screen row = LIST_ROW + ROW
    #   game index = TOP + ROW
    #   Col 0-1: spaces (left padding)
    #   Col 2:   cursor '▶' (0x1A) if game==CUR, else ' '
    #   Col 3-6: "NN. " via drawnum (game number 1-based)
    #   Col 7-31: up to 25 chars of name
    a.lbl('drawlist')
    # Set PLO/PHI to first name in game list
    a.LDA_I((GAMELIST+16)&0xFF); a.STA_Z(PLO)
    a.LDA_I((GAMELIST+16)>>8);   a.STA_Z(PHI)

    # Skip TOP names
    a.LDA_Z(TOP); a.BEQ('dl_nosk')
    a.STA_Z(TMP)
    a.lbl('dl_sk')
    a.LDY_I(0)
    a.lbl('dl_skc')
    a.LDA_IY(PLO); a.INY()
    a.BNE('dl_skb'); a.JMP('dl_skd')
    a.lbl('dl_skb'); a.CMP_I(0); a.BNE('dl_skc')
    a.lbl('dl_skd')
    a.TYA(); a.CLC(); a.ADC_Z(PLO); a.STA_Z(PLO)
    a.BCC('dl_skn'); a.INC_Z(PHI)
    a.lbl('dl_skn')
    a.DEC_Z(TMP); a.BNE('dl_sk')
    a.lbl('dl_nosk')

    a.LDA_I(0); a.STA_Z(ROW)
    a.lbl('dl_row')
    # Exit if ROW >= VISIBLE
    a.LDA_Z(ROW); a.CMP_I(VISIBLE); a.BCS('dl_done')
    # Exit if TOP+ROW >= GCN
    a.CLC(); a.ADC_Z(TOP); a.CMP_Z(GCN); a.BCS('dl_done')

    # Set PPU address for this screen row
    a.LDA_Z(ROW); a.CLC(); a.ADC_I(LIST_ROW); a.JSR('setrow')

    # Col 0-1: left padding
    a.LDA_I(0x20); a.STA_A(PPUDATA); a.STA_A(PPUDATA)

    # Col 2: cursor or space
    a.LDA_Z(ROW); a.CLC(); a.ADC_Z(TOP)   # A = game index
    a.CMP_Z(CUR)
    a.BNE('dl_noc')
    a.LDA_I(0x1A); a.JMP('dl_wrcur')
    a.lbl('dl_noc'); a.LDA_I(0x20)
    a.lbl('dl_wrcur'); a.STA_A(PPUDATA)

    # Col 3-6: game number "NN. "
    a.LDA_Z(TOP); a.CLC(); a.ADC_Z(ROW)  # A = 0-based game index
    a.CLC(); a.ADC_I(1)                   # A = 1-based number
    a.JSR('drawnum')                       # writes 4 chars: tens, units, '.', ' '

    # Col 7-31: up to 25 chars from PLO/PHI
    a.LDY_I(0); a.LDX_I(0)
    a.lbl('dl_ch')
    a.LDA_IY(PLO); a.BEQ('dl_ce')
    a.STA_A(PPUDATA)
    a.INY(); a.INX(); a.CPX_I(25); a.BNE('dl_ch')
    a.lbl('dl_ce')

    # Advance PLO/PHI past this name (skip to null byte + 1)
    a.INY()   # skip past null terminator
    a.TYA(); a.CLC(); a.ADC_Z(PLO); a.STA_Z(PLO)
    a.BCC('dl_nn'); a.INC_Z(PHI)
    a.lbl('dl_nn')

    a.INC_Z(ROW); a.JMP('dl_row')
    a.lbl('dl_done'); a.RTS()

    # ── readjoy ───────────────────────────────────────────────────────────────
    a.lbl('readjoy')
    a.LDA_I(1); a.STA_A(JOY1); a.LDA_I(0); a.STA_A(JOY1)
    a.LDA_Z(JCR); a.STA_Z(JPR); a.LDA_I(0); a.STA_Z(JCR)
    a.LDX_I(8)
    a.lbl('rjl'); a.LDA_A(JOY1); a.LSR(); a.ROL_Z(JCR)
    a.DEX(); a.BNE('rjl')
    a.LDA_Z(JCR); a.EOR_Z(JPR); a.AND_Z(JCR); a.STA_Z(JNN); a.RTS()

    # ── redraw ────────────────────────────────────────────────────────────────
    # Full clear before every redraw — eliminates ghost tiles on page change.
    a.lbl('redraw')
    a.LDA_I(0x00); a.STA_A(PPUMASK)
    a.JSR('clrnt')                           # clear entire nametable first
    a.JSR('setattr')                         # re-apply palette attributes
    a.JSR('drawhdr'); a.JSR('drawftr'); a.JSR('drawlist')
    a.JSR('wvbl')
    a.LDA_I(0); a.STA_A(PPUSCR); a.STA_A(PPUSCR)
    a.LDA_I(0x0E); a.STA_A(PPUMASK)
    a.RTS()

    # ── input ─────────────────────────────────────────────────────────────────
    a.lbl('input')
    a.LDA_Z(JNN); a.BNE('in_start'); a.JMP('ine'); a.lbl('in_start')

    # ── UP: move cursor up; page-jump when crossing page boundary ───────────────
    # After jump: CUR stays at the decremented game (preserves position).
    a.AND_I(BTN_UP); a.BEQ('ckdn')
    a.LDA_Z(CUR); a.BNE('up_notfirst'); a.JMP('ine'); a.lbl('up_notfirst')
    a.DEC_Z(CUR)                           # CUR-- (this is where we want to land)
    # Still on same page? (CUR >= TOP)
    a.LDA_Z(CUR); a.CMP_Z(TOP); a.BCS('do_up_rdr')
    # CUR < TOP: jump to previous page. TOP = max(0, TOP-VISIBLE).
    # CUR is already correct — DO NOT set CUR=TOP here.
    a.LDA_Z(TOP); a.CMP_I(VISIBLE); a.BCC('up_clamp')
    a.SBC_I(VISIBLE)
    a.JMP('up_settop')
    a.lbl('up_clamp'); a.LDA_I(0)
    a.lbl('up_settop'); a.STA_Z(TOP)      # update TOP only; CUR already correct
    a.lbl('do_up_rdr'); a.JSR('redraw'); a.JMP('ine')

    # ── DOWN: page-jump or single scroll ──────────────────────────────────────
    # If cursor is on the LAST visible row of current page → jump to next page
    # Otherwise → move cursor down one (with scroll if needed)
    a.lbl('ckdn')
    a.LDA_Z(JNN); a.AND_I(BTN_DN); a.BEQ('cklf')
    # Check if already at last game
    a.LDA_Z(CUR); a.CLC(); a.ADC_I(1); a.CMP_Z(GCN)
    a.BCC('dn_notlast'); a.JMP('ine'); a.lbl('dn_notlast')

    # Check if cursor is at last visible row of page:
    # last visible row index = TOP + VISIBLE - 1 (clamped to GCN-1)
    # If CUR == TOP + VISIBLE - 1 → page jump
    a.LDA_Z(TOP); a.CLC(); a.ADC_I(VISIBLE-1); a.CMP_Z(CUR); a.BNE('dn_scroll')

    # PAGE DOWN: TOP += VISIBLE, CUR = TOP (first game of new page)
    a.lbl('dn_page')
    a.LDA_Z(TOP); a.CLC(); a.ADC_I(VISIBLE); a.STA_Z(TOP)
    # Cap TOP so last page isn't past end of list
    # If TOP >= GCN, set TOP = GCN - 1 (last game page)
    a.CMP_Z(GCN); a.BCC('dn_pg_ok')
    a.LDA_Z(GCN); a.SBC_I(1); a.STA_Z(TOP)
    # Also align to page boundary: TOP = (TOP/VISIBLE)*VISIBLE
    # Skip alignment for simplicity — just clamp
    a.lbl('dn_pg_ok')
    a.LDA_Z(TOP); a.STA_Z(CUR)          # cursor to first game of new page
    a.JSR('redraw'); a.JMP('ine')

    # SCROLL DOWN: normal single-step
    a.lbl('dn_scroll')
    a.INC_Z(CUR)
    a.LDA_Z(CUR); a.SEC(); a.SBC_Z(TOP)
    a.CMP_I(VISIBLE); a.BCC('do_dn_rdr')
    a.INC_Z(TOP)
    a.lbl('do_dn_rdr'); a.JSR('redraw'); a.JMP('ine')

    # ── LEFT: page up — preserve relative cursor row ─────────────────────────────
    a.lbl('cklf')
    a.LDA_Z(JNN); a.AND_I(BTN_LF); a.BEQ('ckrt')
    a.LDA_Z(TOP); a.BNE('lf_go'); a.JMP('ine'); a.lbl('lf_go')
    # Save old_row = CUR - TOP into TMP before changing TOP
    a.LDA_Z(CUR); a.SEC(); a.SBC_Z(TOP); a.STA_Z(TMP)
    # TOP -= VISIBLE, clamp to 0
    a.LDA_Z(TOP); a.SEC(); a.SBC_I(VISIBLE); a.BCS('lf_ok')
    a.LDA_I(0)
    a.lbl('lf_ok'); a.STA_Z(TOP)
    # CUR = new_TOP + old_row, clamped to GCN-1
    a.CLC(); a.ADC_Z(TMP)                     # A = new_TOP + old_row
    a.CMP_Z(GCN); a.BCC('lf_cur_ok')
    a.LDA_Z(GCN); a.SBC_I(1)
    a.lbl('lf_cur_ok'); a.STA_Z(CUR)
    a.JSR('redraw'); a.JMP('ine')

    # ── RIGHT: page down — preserve relative cursor row ─────────────────────────
    a.lbl('ckrt')
    a.LDA_Z(JNN); a.AND_I(BTN_RT); a.BEQ('ckla')
    # Only page if TOP+VISIBLE < GCN
    a.LDA_Z(TOP); a.CLC(); a.ADC_I(VISIBLE)   # A = new TOP
    a.CMP_Z(GCN); a.BCS('ine')                 # no next page
    a.STA_Z(TOP)                               # commit new TOP
    # CUR = new_TOP + old_row, clamped to GCN-1
    # old_row = old_CUR - old_TOP = CUR - (TOP - VISIBLE)
    # Easier: new_CUR = new_TOP + (old_CUR - old_TOP)
    #        new_CUR = TOP + CUR - (TOP - VISIBLE)  ... use TMP
    # TMP = old_row = CUR - (new_TOP - VISIBLE) = CUR - new_TOP + VISIBLE
    a.LDA_Z(CUR); a.SEC(); a.SBC_Z(TOP)       # A = CUR - new_TOP (negative since old_TOP < new_TOP)
    a.CLC(); a.ADC_I(VISIBLE)                 # A = CUR - new_TOP + VISIBLE = old_row
    a.STA_Z(TMP)                               # TMP = old_row
    a.LDA_Z(TOP); a.CLC(); a.ADC_Z(TMP)       # A = new_TOP + old_row
    a.CMP_Z(GCN); a.BCC('rt_cur_ok')          # if < GCN: fine
    a.LDA_Z(GCN); a.SBC_I(1)                  # else: clamp to GCN-1
    a.lbl('rt_cur_ok'); a.STA_Z(CUR)
    a.JSR('redraw'); a.JMP('ine')

    # ── LAUNCH (A or START) ───────────────────────────────────────────────────
    a.lbl('ckla')
    a.LDA_Z(JNN); a.AND_I(BTN_A|BTN_ST); a.BEQ('ine')
    a.JMP('launch')
    a.lbl('ine'); a.RTS()

    # ── launch ────────────────────────────────────────────────────────────────
    a.lbl('launch')
    # Set PLO/PHI = CFGTABLE + CUR*9  (16-bit — Y overflow fix for CUR >= 29)
    # Step 1: PLO/PHI = &CFGTABLE
    a.LDA_I(0); a.STA_Z(PLO)
    a.LDA_I(192);   a.STA_Z(PHI)
    # Step 2: add 9 CUR times
    a.LDA_Z(CUR); a.BEQ('ldc_skip')   # CUR==0: already pointing at game 0
    a.STA_Z(TMP)
    a.lbl('ldc_adv')
    a.LDA_Z(PLO); a.CLC(); a.ADC_I(9); a.STA_Z(PLO)
    a.BCC('ldc_noc'); a.INC_Z(PHI)
    a.lbl('ldc_noc')
    a.DEC_Z(TMP); a.BNE('ldc_adv')
    a.lbl('ldc_skip')
    # Step 3: copy 9 bytes from (PLO/PHI) to CFG ($0300)
    a.LDA_I(0); a.STA_A(PPUCTRL); a.STA_A(PPUMASK)
    a.LDY_I(0); a.LDX_I(0)
    a.lbl('ldc'); a.LDA_IY(PLO); a.STA_AX(CFG)
    a.INY(); a.INX(); a.CPX_I(9); a.BNE('ldc')
    a.LDX_I(0);  a.LDA_AX(CFG); a.AND_I(0x77); a.STA_Z(ZP_4100)
    a.LDX_I(8);  a.LDA_AX(CFG); a.LSR(); a.LSR(); a.LSR(); a.LSR()
    a.ORA_Z(ZP_4100); a.STA_Z(ZP_4100)
    a.LDX_I(7);  a.LDA_AX(CFG); a.STA_Z(ZP_410A)
    a.LDX_I(3);  a.LDA_AX(CFG); a.STA_Z(ZP_410B)
    a.LDX_I(1);  a.LDA_AX(CFG); a.STA_A(CHRINT)
    a.LDX_I(2);  a.LDA_AX(CFG); a.STA_A(CHRVB0S)
    a.LDX_I(8);  a.LDA_AX(CFG); a.STA_A(0x4118)
    a.LDX_I(8);  a.LDA_AX(CFG); a.AND_I(0x0F); a.STA_A(MIRROR)
    a.LDX_I(4);  a.LDA_AX(CFG); a.STA_A(PRGB0)
    a.LDX_I(5);  a.LDA_AX(CFG); a.STA_A(PRGB1)
    a.LDX_I(6);  a.LDA_AX(CFG); a.STA_A(PRGB2)
    a.JMP_A(STUB)

    a.org(0xFFFA); a.ref16('NMI'); a.ref16('RESET'); a.ref16('IRQ')
    return a


def make_chr():
    def c(*r): return list(r)+[0]*(8-len(r))
    chars={
        0x20:c(0,0,0,0,0,0,0,0),0x21:c(24,24,24,24,24,0,24,0),
        0x22:c(108,108,108,0,0,0,0,0),0x23:c(54,54,127,54,127,54,54,0),
        0x24:c(24,62,96,60,6,124,24,0),0x25:c(98,102,12,24,48,102,70,0),
        0x26:c(56,108,56,118,220,204,118,0),0x27:c(24,24,24,0,0,0,0,0),
        0x28:c(12,24,48,48,48,24,12,0),0x29:c(48,24,12,12,12,24,48,0),
        0x2A:c(0,102,60,255,60,102,0,0),0x2B:c(0,24,24,126,24,24,0,0),
        0x2C:c(0,0,0,0,0,28,12,24),0x2D:c(0,0,0,126,0,0,0,0),
        0x2E:c(0,0,0,0,0,28,28,0),0x2F:c(2,6,12,24,48,96,64,0),
        0x30:c(60,102,110,118,102,102,60,0),0x31:c(24,56,24,24,24,24,126,0),
        0x32:c(60,102,6,12,24,48,126,0),0x33:c(60,102,6,28,6,102,60,0),
        0x34:c(12,28,60,108,126,12,12,0),0x35:c(126,96,124,6,6,102,60,0),
        0x36:c(60,96,124,102,102,102,60,0),0x37:c(126,6,6,12,24,24,24,0),
        0x38:c(60,102,102,60,102,102,60,0),0x39:c(60,102,102,62,6,102,60,0),
        0x3A:c(0,28,28,0,0,28,28,0),0x3B:c(0,28,28,0,28,12,24,0),
        0x3C:c(14,24,48,96,48,24,14,0),0x3D:c(0,0,126,0,126,0,0,0),
        0x3E:c(112,24,12,6,12,24,112,0),0x3F:c(60,102,6,12,24,0,24,0),
        0x40:c(60,102,110,110,96,98,60,0),
        0x41:c(24,60,102,126,102,102,102,0),0x42:c(124,102,102,124,102,102,124,0),
        0x43:c(60,102,96,96,96,102,60,0),0x44:c(120,108,102,102,102,108,120,0),
        0x45:c(126,96,96,124,96,96,126,0),0x46:c(126,96,96,124,96,96,96,0),
        0x47:c(60,102,96,110,102,102,62,0),0x48:c(102,102,102,126,102,102,102,0),
        0x49:c(60,24,24,24,24,24,60,0),0x4A:c(6,6,6,6,102,102,60,0),
        0x4B:c(102,108,120,112,120,108,102,0),0x4C:c(96,96,96,96,96,96,126,0),
        0x4D:c(99,119,127,107,99,99,99,0),0x4E:c(102,118,126,126,110,102,102,0),
        0x4F:c(60,102,102,102,102,102,60,0),0x50:c(124,102,102,124,96,96,96,0),
        0x51:c(60,102,102,102,110,60,6,0),0x52:c(124,102,102,124,108,102,102,0),
        0x53:c(60,102,96,60,6,102,60,0),0x54:c(126,24,24,24,24,24,24,0),
        0x55:c(102,102,102,102,102,102,60,0),0x56:c(102,102,102,102,60,60,24,0),
        0x57:c(99,99,107,127,119,99,99,0),0x58:c(102,102,60,24,60,102,102,0),
        0x59:c(102,102,60,24,24,24,24,0),0x5A:c(126,6,12,24,48,96,126,0),
        0x5B:c(60,48,48,48,48,48,60,0),0x5C:c(64,96,48,24,12,6,2,0),
        0x5D:c(60,12,12,12,12,12,60,0),0x5E:c(24,60,102,0,0,0,0,0),
        0x5F:c(0,0,0,0,0,0,0,126),0x60:c(48,24,12,0,0,0,0,0),
        0x61:c(0,0,60,6,62,102,62,0),0x62:c(96,96,124,102,102,102,124,0),
        0x63:c(0,0,60,102,96,102,60,0),0x64:c(6,6,62,102,102,102,62,0),
        0x65:c(0,0,60,102,126,96,60,0),0x66:c(28,48,124,48,48,48,48,0),
        0x67:c(0,0,62,102,102,62,6,60),0x68:c(96,96,124,102,102,102,102,0),
        0x69:c(24,0,56,24,24,24,60,0),0x6A:c(6,0,6,6,6,102,102,60),
        0x6B:c(96,96,102,108,120,108,102,0),0x6C:c(56,24,24,24,24,24,60,0),
        0x6D:c(0,0,99,119,127,107,99,0),0x6E:c(0,0,124,102,102,102,102,0),
        0x6F:c(0,0,60,102,102,102,60,0),0x70:c(0,0,124,102,102,124,96,96),
        0x71:c(0,0,62,102,102,62,6,6),0x72:c(0,0,108,118,96,96,96,0),
        0x73:c(0,0,60,96,60,6,124,0),0x74:c(24,24,126,24,24,24,14,0),
        0x75:c(0,0,102,102,102,102,62,0),0x76:c(0,0,102,102,60,60,24,0),
        0x77:c(0,0,99,107,127,119,99,0),0x78:c(0,0,102,60,24,60,102,0),
        0x79:c(0,0,102,102,62,6,60,0),0x7A:c(0,0,126,12,24,48,126,0),
        0x7B:c(14,24,24,48,24,24,14,0),0x7C:c(24,24,24,0,24,24,24,0),
        0x7D:c(112,24,24,12,24,24,112,0),0x7E:c(114,156,0,0,0,0,0,0),
    }
    data=bytearray(4096)
    for code,bm in chars.items():
        off=code*16
        for row in range(8):
            v=bm[row] if row<len(bm) else 0
            data[off+row]=v; data[off+row+8]=v
    # Arrow cursor glyph at tile $1A
    ar=[0,32,48,56,60,56,48,32]
    for r in range(8): data[0x1A*16+r]=ar[r]; data[0x1A*16+r+8]=0
    return bytes(data)


def main():
    a = assemble_v6()
    errs = a.resolve()
    if errs:
        for e in errs: print(f"ERROR: {e}", file=sys.stderr)
        sys.exit(1)
    code = a.rom(0xE000, 0x2000)

    # Sanity checks
    assert code[0] == 0x78, "RESET must start with SEI"
    reset = code[0x1FFC] | (code[0x1FFD] << 8)
    assert reset == 0xE000, f"RESET vector = ${reset:04X}, expected $E000"
    nmi   = code[0x1FFA] | (code[0x1FFB] << 8)
    # Check no direct STA $410B from ROM
    for i in range(len(code)-2):
        if code[i]==0x8D and code[i+1]==0x0B and code[i+2]==0x41:
            print(f"WARNING: direct STA $410B at ${0xE000+i:04X}")

    kernel = bytearray(b'\xFF' * 0x80000)
    kernel[0x07E000:0x080000] = code
    chr_data = make_chr()
    kernel[0x040000:0x040000+len(chr_data)] = chr_data

    print(f"OK  RESET=${reset:04X}  NMI=${nmi:04X}  code={len(code)} bytes")
    for nm in ['drawlist','drawnum','setattr','redraw','launch','input','readjoy']:
        if nm in a.sym:
            print(f"  {nm:12s} = ${a.sym[nm]:04X}")
    print(f"Layout: VISIBLE=20 rows, LIST_ROW=6, HDR=3, FTR=28")
    print(f"Colours: title=0x3B (BG pal 1), games=0x36 (BG pal 2)")
    print(f"Nav: Up/Down scroll; Down at page-end → page jump; L/R page change")
    return kernel


if __name__ == '__main__':
    k = main()
    out = sys.argv[1] if len(sys.argv) > 1 else 'original_menu_patched.rom'
    open(out, 'wb').write(k)
    print(f"Saved {out} ({len(k)//1024} KB)")
