# VTxx OneBus NOR Flash Multicart — NES 2.0 Mapper 256 https://www.nesdev.org/wiki/NES_2.0_Mapper_256/Submapper_table




## Output Files

| File | Purpose |
|------|---------|
| `multicart.bin` | Raw NOR flash image — flash this to the chip |
| `multicart.nes` | NES 2.0 mapper 256 — open in **NintendulatorNRS** or FCEUX (NROM + MMC3) |
| `multicart.unf` | UNIF UNL-OneBus — open in **NintendulatorNRS** (works for NROM + MMC3) |

### Emulator compatibility

| Emulator | NROM/CNROM | MMC3 |
|----------|-----------|------|
| NintendulatorNRS + `.unf` | ✓ | ✓ |
| FCEUX + `.nes` | ✓ | ✗ grey screen (some MMC3) |

---

## Compatible NOR Flash Chips

The VT0x OneBus console uses a **parallel NOR flash** in **TSOP48**  **TSOP56** package, running at **3.3 V**.

### 8 MB (64 Mbit) — recommended

| Part number | Manufacturer | Notes |
|-------------|-------------|-------|
| MX29LV640EBXXX | Macronix | Most common in Chinese game consoles |
| AM29LV640D / S29AL064D | AMD / Spansion | Pin-compatible |
| EN29LV640 | EON | Pin-compatible |
| HY29LV640 | Hynix | Pin-compatible |
| SST39VF6401 / SST39VF6402 | Microchip | Note: bottom/top boot sector variants |

### 16 MB (128 Mbit) — if you need more space

| Part number | Notes |
|-------------|-------|
| MX29GL128 | Macronix, TSOP48, 3.3 V |
| S29GL128 | Spansion, TSOP48, 3.3 V |

> **Tip:** All of the above are pin-compatible 48-pin TSOP parts.
> The original SUP/RetroFC consoles use MX29LV640 or equivalent.
> Buy the chip desoldered from a donor board, or new old stock.

---

## Flashing with T48 (TL866-3G) + Xgpro

### What you need

- T48 programmer (TL866-3G) with Xgpro software installed
- **TSOP48 adapter** for the T48 — sold separately, required for this chip package
- The `multicart.bin` file built by this tool


---

## MMC3 Game Compatibility

The VT0x OneBus hardware MMC3 emulator works with most standard MMC3 games but has limits.



### Grey screen on hardware (rejected by builder)

- **CHR-RAM games** (`chr_banks = 0`) — VT0x MMC3 emulator expects CHR-ROM
  - Castlevania III, Adventures of Lolo 1–3, Solstice
  - These are blocked at load time with `✗ CHR-RAM` in the Status column

### May grey screen (warned with `⚠ IRQ?`)

- Games with PRG > 256 KB that depend on cycle-accurate MMC3 scanline IRQ
  - Kirby's Adventure, Mega Man 3–6, Super Mario Bros. 3, Contra (some versions)
  - If the game uses IRQ only for music (not raster effects) it will usually still work

---

## Chip Size vs. Capacity

| Chip size | NROM/CNROM area | MMC3 area | Total game space |
|-----------|----------------|-----------|-----------------|
| 4 MB (32 Mbit) | 1.5 MB | 2 MB | 3.5 MB |
| 8 MB (64 Mbit) | 1.5 MB | 6 MB | 7.5 MB |
| 16 MB (128 Mbit) | 1.5 MB | 14 MB | 15.5 MB |

The first 512 KB is always occupied by the menu kernel (`original_menu_patched.rom`).
