#!/usr/bin/env python3
"""
VT03 OneBus Multicart Builder
Port of Wassermann1/sup400 rom_tool/go to Python.
Builds a NES 2.0 mapper 256 ROM from NROM (mapper 0), CNROM (mapper 3), and MMC3 (mapper 4) games.

ROM Layout:
  [0x000000 - 0x07FFFF]  original_menu.rom (512KB kernel)
    [0x079000]           Game list: count(2B LE) + 14 constants + null-terminated names
    [0x07C000]           Config table: 9 bytes per game
  [0x080000 - 0x1FFFFF]  NROM/CNROM games
  [0x200000 - romEnd]    MMC3 games
"""

import os
import sys
import struct
import argparse
from pathlib import Path

# --- Constants ---
MENU_ROM_PATH = None
for _name in ("original_menu_patched.rom", "original_menu.rom"):
    _candidate = Path(__file__).parent / _name
    if _candidate.exists():
        MENU_ROM_PATH = _candidate
        break
if MENU_ROM_PATH is None:
    MENU_ROM_PATH = Path(__file__).parent / "original_menu.rom"  # will raise on load
MENU_START     = 0x79000
MENU_END       = 0x79FFF
MENU_HEADER_END = 0x79010   # names start here (after 16-byte header)
CONFIG_TABLE_ADDR = 0x7C000

NROM_START = 0x080000
MMC3_START = 0x200000
WINDOW_SIZE = 0x200000   # 2MB

MENU_CONSTANTS = bytes([
    0x14, 0x00, 0x08, 0x08, 0x08, 0x04, 0x04, 0x08,
    0x01, 0x00, 0x00, 0x00, 0x00, 0x00,
])

# --- NES file parser ---
class NESGame:
    def __init__(self, path):
        self.path = Path(path)
        self.filename = self.path.name
        data = self.path.read_bytes()
        if len(data) < 16 or data[:4] != b'NES\x1a':
            raise ValueError(f"Not a valid NES file: {path}")
        h = data[:16]
        flags6 = h[6]
        flags7 = h[7]
        nes20 = (flags7 & 0x0C) == 0x08
        if nes20:
            self.mapper = (h[8] << 8) | (flags7 & 0xF0) | (flags6 >> 4)
            prg_msb = h[9] & 0x0F
            self.prg_size = ((prg_msb << 8) | h[4]) * 16384
            chr_msb = (h[9] >> 4) & 0x0F
            self.chr_size = ((chr_msb << 8) | h[5]) * 8192
        else:
            self.mapper = (flags7 & 0xF0) | (flags6 >> 4)
            self.prg_size = h[4] * 16384
            self.chr_size = h[5] * 8192
        self.vertical = bool(flags6 & 0x01)
        self.raw_data = data[16:]  # without header
        if len(self.raw_data) < self.prg_size + self.chr_size:
            raise ValueError(f"File too short: {path}")

# --- Bank math ---
def bank_for_addr(physical_addr):
    """8KB bank number from physical address."""
    return (physical_addr // 8192) & 0xFF

def calc_nrom_video(chr_addr, mapper):
    """Calculate reg4100_video, reg2018, reg201A for NROM/CNROM CHR."""
    chr_addr += 0x1800
    if mapper == 3:  # CNROM: extra offset
        chr_addr += 0x4000
    reg4100_v = (chr_addr >> 21) & 0x0F
    offset_in_2mb = chr_addr & 0x1FFFFF
    bank1_index   = offset_in_2mb // 0x40000
    reg2018 = bank1_index << 4
    reg201A = (chr_addr >> 10) & 0xFF
    return reg4100_v, reg2018, reg201A

def build_nrom_config(game, rom_offset):
    """Build 9-byte config for NROM (mapper 0) or CNROM (mapper 3)."""
    chr_start = rom_offset + game.prg_size
    v4100, reg2018, reg201A = calc_nrom_video(chr_start, game.mapper)
    pa24_21 = (rom_offset >> 21) & 0x0F
    reg4100 = (pa24_21 << 4) | (v4100 & 0x0F)

    bank4 = bank_for_addr(rom_offset)
    bank5 = bank_for_addr(rom_offset + 8192)
    bank6 = bank_for_addr(rom_offset + game.prg_size - 2 * 8192)
    bank7 = bank_for_addr(rom_offset + game.prg_size - 8192)

    mirroring = 0x00 if game.vertical else 0x01
    mode = 0x04 if game.prg_size >= 4 * 8192 else 0x05

    return bytes([reg4100, reg2018, reg201A, mode, bank4, bank5, bank6, bank7, mirroring])

def build_mmc3_config(game, rom_offset):
    """Build 9-byte config for MMC3 (mapper 4)."""
    # Video
    chr_start = rom_offset + game.prg_size
    starts_at_boundary = (rom_offset % (256 * 1024)) == 0
    is_small = (game.prg_size + game.chr_size) < 256 * 1024

    v4100, reg2018, reg201A = calc_video_registers(chr_start, game.prg_size, starts_at_boundary, is_small)
    pa24_21 = (rom_offset >> 21) & 0x0F
    reg4100 = (pa24_21 << 4) | (v4100 & 0x0F)

    # PRG banks (relative to 2MB window)
    window_base   = (rom_offset // WINDOW_SIZE) * WINDOW_SIZE
    offset_in_win = rom_offset - window_base

    bank4 = bank_for_addr(offset_in_win)
    bank5 = bank_for_addr(offset_in_win + 8192)

    ps = game.prg_size
    if ps == 128 * 1024:
        bank6 = bank_for_addr((offset_in_win + 14 * 8192) % WINDOW_SIZE)
        bank7 = bank_for_addr((offset_in_win + 15 * 8192) % WINDOW_SIZE)
    elif ps == 256 * 1024:
        bank6 = bank_for_addr((offset_in_win + 30 * 8192) % WINDOW_SIZE)
        bank7 = bank_for_addr((offset_in_win + 31 * 8192) % WINDOW_SIZE)
    else:
        last = offset_in_win + ps - 16384
        bank6 = bank_for_addr(last % WINDOW_SIZE)
        bank7 = bank_for_addr((last + 8192) % WINDOW_SIZE)

    byte8 = 0x80 if game.chr_size > 128 * 1024 else 0x00

    return bytes([reg4100, reg2018, reg201A, 0x02, bank4, bank5, bank6, bank7, byte8])

def calc_video_registers(chr_addr, prg_size, starts_at_boundary, is_small):
    """Port of CalcVideoRegisters from video.go."""
    va24_21 = (chr_addr >> 21) & 0x0F
    reg4100 = va24_21
    va20_10 = (chr_addr >> 14) & 0x70
    eva12_10 = 0

    if starts_at_boundary and not is_small:
        base = chr_addr
        if prg_size >= 128 * 1024:
            if prg_size > 128 * 1024:
                base = chr_addr
            else:
                window_base = (chr_addr // WINDOW_SIZE) * WINDOW_SIZE
                candidate = chr_addr - 0x20000
                base = candidate if candidate >= window_base else chr_addr
            va24_21 = (base >> 21) & 0x0F
            reg4100 = va24_21
            va20_10 = (base >> 14) & 0x70
        reg2018 = va20_10 | eva12_10
        if prg_size >= 128 * 1024:
            reg201A = 0x81
        else:
            reg201A = 0x01
    else:
        eva12_10 = (chr_addr >> 10) & 0x07
        reg2018 = va20_10 | eva12_10
        reg201A = 0x01

    return reg4100, reg2018, reg201A

# --- Name sanitizer ---
def clean_name(filename):
    name = Path(filename).stem
    # Drop anything after '('
    if '(' in name:
        name = name[:name.index('(')]
    name = name.upper().strip()
    # Keep only A-Z 0-9 space
    name = ''.join(c for c in name if c.isalnum() or c == ' ')
    # Normalize spaces
    name = ' '.join(name.split())
    return name[:20]   # cap length for menu display

# --- NES 2.0 header builder ---
def make_unif_rom(prg: bytes) -> bytes:
    """
    Build a UNIF file for NintendulatorNRS (mapper UNL-OneBus).
    Structure:
      "UNIF" + revision(4) + 24-byte padding
      MAPR chunk: "UNL-OneBus\\0"
      MIRR chunk: 0x05 (mapper-controlled)
      PRG0 chunk: ROM[0x000000-0x1FFFFF]  (2 MB)
      PRG1 chunk: ROM[0x200000-0x3FFFFF]  (if size >= 4 MB)
      PRG2 chunk: ROM[0x400000-0x5FFFFF]  (if size >= 6 MB)
      PRG3 chunk: ROM[0x600000-0x7FFFFF]  (if size  = 8 MB)
    """
    WINDOW = 0x200000  # 2 MB per chunk

    def chunk(name: str, data: bytes) -> bytes:
        n = name.encode('ascii')
        assert len(n) == 4
        return n + len(data).to_bytes(4, 'little') + data

    parts = []
    # 32-byte UNIF header: magic + revision=4 + 24 zeros
    parts.append(b'UNIF')
    parts.append((4).to_bytes(4, 'little'))
    parts.append(b'\x00' * 24)

    parts.append(chunk('MAPR', b'UNL-OneBus\x00'))
    parts.append(chunk('MIRR', b'\x05'))

    num_windows = (len(prg) + WINDOW - 1) // WINDOW
    for w in range(num_windows):
        start = w * WINDOW
        end   = min(start + WINDOW, len(prg))
        window_data = prg[start:end]
        if len(window_data) < WINDOW:
            window_data = window_data + b'\xff' * (WINDOW - len(window_data))
        parts.append(chunk(f'PRG{w}', window_data))

    return b''.join(parts)


def make_nes2_header(prg_bytes, mapper=256):
    """Build a 16-byte NES 2.0 header for the given PRG size."""
    prg_banks = prg_bytes // 16384  # 16KB units
    prg_lsb   = prg_banks & 0xFF
    prg_msb   = (prg_banks >> 8) & 0x0F

    flags6 = (mapper & 0xF) << 4        # mapper low nibble, no mirroring bit
    flags7 = (mapper & 0xF0) | 0x08     # mapper mid nibble + NES 2.0 marker
    mapper_hi = (mapper >> 8) & 0xFF    # = 0x01 for mapper 256

    # byte9: PRG MSB in D3-0, CHR MSB in D7-4 (CHR=0)
    byte9 = prg_msb & 0x0F

    header = bytearray(16)
    header[0:4] = b'NES\x1a'
    header[4]   = prg_lsb
    header[5]   = 0      # no CHR ROM banks
    header[6]   = flags6
    header[7]   = flags7
    header[8]   = mapper_hi
    header[9]   = byte9
    # bytes 10-15 = 0
    return bytes(header)

# --- Main builder ---
def build_multicart(nes_files, rom_size_mb=8, output_bin="multicart.bin", output_nes="multicart.nes"):
    rom_size = rom_size_mb * 1024 * 1024

    # Load kernel
    if not MENU_ROM_PATH.exists():
        print(f"ERROR: original_menu.rom not found at {MENU_ROM_PATH}")
        sys.exit(1)
    menu_rom = MENU_ROM_PATH.read_bytes()
    if len(menu_rom) != 0x80000:
        print(f"ERROR: original_menu.rom should be 512KB, got {len(menu_rom)}")
        sys.exit(1)

    # Init ROM buffer
    rom = bytearray(rom_size)
    rom[:len(menu_rom)] = menu_rom
    for i in range(len(menu_rom), rom_size):
        rom[i] = 0xFF

    # Parse games
    games = []
    for path in nes_files:
        try:
            g = NESGame(path)
        except Exception as e:
            print(f"  SKIP {Path(path).name}: {e}")
            continue
        if g.mapper not in (0, 3, 4):
            print(f"  SKIP {g.filename}: mapper {g.mapper} (only 0/NROM, 3/CNROM, 4/MMC3 supported)")
            continue
        games.append(g)
        print(f"  OK   {g.filename}: mapper={g.mapper} prg={g.prg_size//1024}KB chr={g.chr_size//1024}KB {'V' if g.vertical else 'H'}")

    if not games:
        print("No compatible games found.")
        sys.exit(1)

    # Sort: NROM/CNROM first, then MMC3
    nrom_games = [g for g in games if g.mapper in (0, 3)]
    mmc3_games = [g for g in games if g.mapper == 4]

    cur_nrom = NROM_START
    cur_mmc3 = MMC3_START
    nrom_limit = MMC3_START - 1

    names   = []
    configs = []

    # Place NROM/CNROM games
    for g in nrom_games:
        game_size = g.prg_size + g.chr_size
        if cur_nrom + game_size > nrom_limit:
            print(f"  SKIP {g.filename}: not enough NROM space")
            continue
        rom[cur_nrom:cur_nrom+game_size] = g.raw_data[:game_size]
        cfg = build_nrom_config(g, cur_nrom)
        names.append(clean_name(g.filename))
        configs.append(cfg)
        print(f"  NROM {g.filename:30s} @ {cur_nrom:#08x}  cfg={cfg.hex()}")
        # Advance: align to 16KB boundary (match Go: += gameSize + gameSize%0x4000)
        align = game_size % 0x4000
        cur_nrom += game_size + align

    # Place MMC3 games
    for g in mmc3_games:
        game_size = g.prg_size + g.chr_size
        # Don't split across 2MB window
        window_base = (cur_mmc3 // WINDOW_SIZE) * WINDOW_SIZE
        window_end  = window_base + WINDOW_SIZE
        if cur_mmc3 + game_size > window_end:
            cur_mmc3 = window_end  # jump to next window
        if cur_mmc3 + game_size > rom_size:
            print(f"  SKIP {g.filename}: not enough MMC3 space")
            continue
        rom[cur_mmc3:cur_mmc3+game_size] = g.raw_data[:game_size]
        cfg = build_mmc3_config(g, cur_mmc3)
        names.append(clean_name(g.filename))
        configs.append(cfg)
        print(f"  MMC3 {g.filename:30s} @ {cur_mmc3:#08x}  cfg={cfg.hex()}")
        cur_mmc3 += game_size

    # Write game list at 0x79000
    menu_area = bytearray(MENU_END - MENU_START + 1)
    menu_area[:] = b'\xFF' * len(menu_area)
    num_games = len(names)
    struct.pack_into('<H', menu_area, 0, num_games)
    menu_area[2:2+len(MENU_CONSTANTS)] = MENU_CONSTANTS
    offset = MENU_HEADER_END - MENU_START
    for name in names:
        nb = name.encode('ascii') + b'\x00'
        menu_area[offset:offset+len(nb)] = nb
        offset += len(nb)
    rom[MENU_START:MENU_END+1] = menu_area

    # Write configs at 0x7C000
    for i, cfg in enumerate(configs):
        rom[CONFIG_TABLE_ADDR + i*9 : CONFIG_TABLE_ADDR + i*9 + 9] = cfg

    # Output .bin
    Path(output_bin).write_bytes(rom)
    print(f"\n✓ Wrote {output_bin} ({rom_size//1024//1024}MB)")

    # Output .nes (NES 2.0, mapper 256)
    # NROM/CNROM: works in FCEUX and Mesen
    # MMC3: Mesen only — FCEUX does not emulate MMC3 mode in mapper 256
    header = make_nes2_header(rom_size)
    Path(output_nes).write_bytes(header + bytes(rom))
    print(f"✓ Wrote {output_nes} ({(rom_size+16)//1024//1024}MB, mapper 256 — use Mesen for MMC3)")

    # Output .unf (UNIF, UNL-OneBus — for NintendulatorNRS)
    output_unf = str(output_nes).replace('.nes', '.unf')
    Path(output_unf).write_bytes(make_unif_rom(bytes(rom)))
    print(f"✓ Wrote {output_unf} (UNL-OneBus UNIF — for NintendulatorNRS)")

    print(f"\n  Games: {num_games} ({len(nrom_games)} NROM/CNROM, {len(mmc3_games)} MMC3)")
    print(f"  NROM used: {(cur_nrom-NROM_START)//1024}KB / {(MMC3_START-NROM_START)//1024}KB")
    print(f"  MMC3 used: {(cur_mmc3-MMC3_START)//1024}KB")

if __name__ == '__main__':
    p = argparse.ArgumentParser(description='VT03 OneBus Multicart Builder')
    p.add_argument('games', nargs='+', help='Input .nes files')
    p.add_argument('--size', type=int, default=8, help='ROM size in MB (default 8)')
    p.add_argument('--out-bin', default='multicart.bin')
    p.add_argument('--out-nes', default='multicart.nes')
    args = p.parse_args()
    build_multicart(args.games, args.size, args.out_bin, args.out_nes)
