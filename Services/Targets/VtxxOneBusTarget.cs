using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using VT03Builder.Models;
using VT03Builder.Services.Hardware;
using VT03Builder.Services.Output;

namespace VT03Builder.Services.Targets
{
    /// <summary>
    /// Hardware target: VTxx OneBus NOR flash multicart (NES 2.0 mapper 256).
    ///
    /// Used in NOR-flash handheld consoles such as the SUP 400-in-1.
    /// The kernel is a 512 KB binary embedded as a resource (original_menu_patched.rom).
    ///
    /// Flash layout:
    ///   0x000000–0x07FFFF   Kernel (512 KB): CHR font, GAMELIST, CFGTABLE, menu code
    ///   0x080000–0x1FFFFF   NROM overflow + MMC3 window 0 (PA=0)
    ///   0x200000+           MMC3 windows 1, 2, … (PA=1, 2, …)
    ///
    /// Kernel free gaps available for NROM packing:
    ///   0x000000–0x03FFFF   256 KB
    ///   0x041000–0x078FFF   224 KB
    /// </summary>
    public sealed class VtxxOneBusTarget : IHardwareTarget
    {
        // ── Identity ──────────────────────────────────────────────────────────

        public string Id           => "vtxx";
        public string DisplayName  => "VTxx OneBus (Mapper 256)";
        public int    OutputMapper => 256;

        // ── Hardware limits ───────────────────────────────────────────────────

        public long MaxFlashBytes     => 32L * 1024 * 1024;   // 32 MB max
        public int  ConfigRecordBytes => 9;
        public bool AlwaysChrRam      => false;   // CHR-ROM supported
        public bool HasKernelArea     => true;
        public int  KernelAreaSize    => KernelEnd;

        public IReadOnlyList<int> SupportedSourceMappers { get; } =
            new[] { 0, 4 };   // NROM and MMC3

        // ── Submappers ────────────────────────────────────────────────────────

        private static readonly string OpcodeSwapNote =
            "Hardware CPU opcode bit-swap — standard NES ROMs will not work";

        public IReadOnlyList<SubmapperInfo> Submappers { get; } = new[]
        {
            new SubmapperInfo { Number =  0, Name = "Normal" },
            new SubmapperInfo { Number =  1, Name = "Waixing VT03" },
            new SubmapperInfo { Number =  2, Name = "Power Joy Supermax" },
            new SubmapperInfo { Number =  3, Name = "Zechess / Hummer Team" },
            new SubmapperInfo { Number =  4, Name = "Sports Game 69-in-1" },
            new SubmapperInfo { Number =  5, Name = "Waixing VT02" },
            new SubmapperInfo { Number = 11, Name = "Vibes",     Warning = OpcodeSwapNote },
            new SubmapperInfo { Number = 12, Name = "Cheertone", Warning = OpcodeSwapNote },
            new SubmapperInfo { Number = 13, Name = "Cube Tech",  Warning = OpcodeSwapNote },
            new SubmapperInfo { Number = 14, Name = "Karaoto",    Warning = OpcodeSwapNote },
            new SubmapperInfo { Number = 15, Name = "Jungletac",  Warning = OpcodeSwapNote },
        };

        // ── Layout constants ──────────────────────────────────────────────────

        internal const int MenuStart      = 0x079000;
        internal const int MenuEnd        = 0x079FFF;
        internal const int MenuHeaderEnd  = 0x079010;
        internal const int ConfigTableAddr = 0x07C000;
        internal const int KernelEnd      = 0x080000;
        internal const int NromEnd        = 0x200000;
        internal const int WindowSize     = 0x200000;
        internal const int LcdStubAddr    = 0x06E000;

        internal static readonly (int Start, int End)[] KernelFreeRegions =
        {
            (0x000000, 0x040000),   // 256 KB — before CHR font
            (0x041000, 0x079000),   // 224 KB — between CHR font and GAMELIST
        };

        // 14-byte constants written to the GAMELIST header after the count word.
        private static readonly byte[] GameListConstants =
        {
            0x14, 0x00, 0x08, 0x08, 0x08, 0x04, 0x04, 0x08,
            0x01, 0x00, 0x00, 0x00, 0x00, 0x00,
        };

        // ── Build steps ───────────────────────────────────────────────────────

        /// <summary>
        /// Copy the kernel binary into flash and optionally write the LCD init stub.
        /// </summary>
        public void InitialiseFlash(byte[] flash, BuildConfig cfg)
        {
            byte[] kernel = LoadKernel();
            if (kernel.Length > flash.Length)
                throw new InvalidOperationException(
                    $"Kernel ({kernel.Length} bytes) is larger than flash ({flash.Length} bytes).");
            Array.Copy(kernel, flash, kernel.Length);

            if (cfg.InitLcd)
                WriteLcdStub(flash);
        }

        /// <summary>
        /// Translate a hardware-neutral GameBankingInfo into the 9-byte VTxx OneBus
        /// config record that the menu stub writes to the $41xx registers.
        /// </summary>
        public byte[] BuildConfigRecord(NesRom game, GameBankingInfo info, BuildConfig cfg)
        {
            if (info.SourceMapper == 0)
                return BuildNromConfig(info.PrgFlashOffset, info.NativePrgSize,
                                       info.ChrSize, game.Mapper, info.Vertical);
            else
                return BuildMmc3Config(info.PrgFlashOffset, info.NativePrgSize,
                                       info.ChrSize);
        }

        // ── Config record builders ────────────────────────────────────────────

        private static byte[] BuildNromConfig(
            int romOffset, int prgSize, int chrSize, int mapper, bool vertical)
        {
            CalcNromVideo(romOffset + prgSize, mapper,
                          out byte v4100, out byte r2018, out byte r201A);
            byte pa    = (byte)((romOffset >> 21) & 0x0F);
            byte r4100 = (byte)((pa << 4) | (v4100 & 0x0F));
            byte b4    = Bnk(romOffset);
            byte b5    = Bnk(romOffset + 0x2000);
            byte b6    = Bnk(romOffset + prgSize - 0x4000);
            byte b7    = Bnk(romOffset + prgSize - 0x2000);
            byte mir   = vertical ? (byte)0x00 : (byte)0x01;
            byte mode  = prgSize >= 32768 ? (byte)0x04 : (byte)0x05;
            return new byte[] { r4100, r2018, r201A, mode, b4, b5, b6, b7, mir };
        }

        private static void CalcNromVideo(int addr, int mapper,
                                          out byte v4100, out byte r2018, out byte r201A)
        {
            addr  += 0x1800;
            v4100  = (byte)((addr >> 21) & 0x0F);
            r2018  = (byte)(((addr & 0x1FFFFF) / 0x40000) << 4);
            r201A  = (byte)((addr >> 10) & 0xFF);
        }

        private static byte[] BuildMmc3Config(int romOffset, int prgSize, int chrSize)
        {
            var (ps, _, _)             = OneBusBanking.PsForPrg(prgSize);
            int  effPrg                = OneBusBanking.EffectivePrgForChr(prgSize, chrSize);
            int  chrAddr               = romOffset + effPrg;
            byte va                    = (byte)((chrAddr >> 21) & 0x0F);
            byte r2018                 = (byte)((chrAddr >> 14) & 0x70);
            var (vb0s, chrInnerMask)   = OneBusBanking.Vb0sForChr(chrSize);
            byte antiMask              = (byte)(~chrInnerMask & 0xFF);
            byte middle                = (byte)(((chrAddr >> 10) & 0xFF) & antiMask);
            byte r201A                 = (byte)(vb0s | middle);
            byte pa                    = (byte)((romOffset >> 21) & 0x0F);
            byte r4100                 = (byte)((pa << 4) | va);
            const int Window           = 0x200000;
            int  wb                    = (romOffset / Window) * Window;
            int  oin                   = romOffset - wb;
            byte b4                    = Bnk(oin);
            byte b5                    = Bnk(oin + 0x2000);
            byte b7                    = Bnk(oin + prgSize - 8192);
            byte b6                    = Bnk(oin + prgSize - 16384);
            byte f                     = chrSize > 128 * 1024 ? (byte)0x80 : (byte)0x00;
            return new byte[] { r4100, r2018, r201A, ps, b4, b5, b6, b7, f };
        }

        private static byte Bnk(int addr) => (byte)((addr / 8192) & 0xFF);

        /// <summary>
        /// Write game names to GAMELIST (0x079000) and config records to CFGTABLE (0x07C000).
        /// </summary>
        public void WriteGameTable(byte[] flash, IReadOnlyList<GameEntry> entries,
                                   BuildConfig cfg)
        {
            // GAMELIST area
            var names = entries.Select(e => e.DisplayName).ToList();
            WriteGameList(flash, names);

            // CFGTABLE: 9 bytes per game starting at ConfigTableAddr
            for (int i = 0; i < entries.Count; i++)
                Array.Copy(entries[i].ConfigRecord, 0, flash,
                           ConfigTableAddr + i * ConfigRecordBytes,
                           ConfigRecordBytes);
        }

        /// <summary>
        /// Produce .bin (+ optional .nes / .unf), applying pin swap to .bin only.
        /// </summary>
        public BuildResult BuildOutputFiles(byte[] flash, BuildConfig cfg)
        {
            // Generate .nes/.unf from the UNSWAPPED flash first.
            byte[] nesFile  = cfg.GenerateNes
                ? NesFileWriter.MakeNes2Rom(flash, cfg.Submapper)
                : Array.Empty<byte>();
            byte[] unifFile = cfg.GenerateNes
                ? NesFileWriter.MakeUnifRom(flash)
                : Array.Empty<byte>();

            // Apply pin swap to the .bin copy only — after generating .nes/.unf.
            byte[] norBinary = (byte[])flash.Clone();
            if (cfg.PinSwap == 1)
                PinSwap.Apply(norBinary);

            return new BuildResult
            {
                NorBinary = norBinary,
                NesFile   = nesFile,
                UnifFile  = unifFile,
            };
        }

        // ── LCD init stub ─────────────────────────────────────────────────────

        private static void WriteLcdStub(byte[] rom)
        {
            byte[] stub =
            {
                0xA9, 0x20, 0x8D, 0x18, 0x20,   // LDA #$20, STA $2018
                0xA9, 0x00, 0x8D, 0x1A, 0x20,   // LDA #$00, STA $201A
                0xA9, 0x00, 0x8D, 0x16, 0x20,   // LDA #$00, STA $2016
                0xA9, 0x02, 0x8D, 0x17, 0x20,   // LDA #$02, STA $2017
                0xA0, 0x04, 0x8C, 0x12, 0x20,   // LDY #$04, STY $2012
                0xC8,       0x8C, 0x13, 0x20,   // INY,      STY $2013
                0xC8,       0x8C, 0x14, 0x20,   // INY,      STY $2014
                0xC8,       0x8C, 0x15, 0x20,   // INY,      STY $2015
                0xA9, 0x1F, 0x8D, 0x3F, 0x41,   // LDA #$1F, STA $413F (Type 3)
                0xA9, 0x0B, 0x8D, 0x38, 0x41,   // LDA #$0B, STA $4138
                0xA9, 0x0F, 0x8D, 0x39, 0x41,   // LDA #$0F, STA $4139
                0xA9, 0x0F, 0x8D, 0x2C, 0x41,   // LDA #$0F, STA $412C (Type 1/2)
                0xA2, 0x20,                      // LDX #$20
                0xBD, 0x47, 0x80,               // LDA $8047,X  [loop]
                0x9D, 0x00, 0x04,               // STA $0400,X
                0xCA,                            // DEX
                0x10, 0xF9,                      // BPL loop
                0x4C, 0x00, 0x04,               // JMP $0400
                // RAM stub (copied to $0400):
                0xA9, 0x3C, 0x8D, 0x0A, 0x41,   // LDA #$3C, STA $410A
                0xA9, 0x00, 0x8D, 0x0B, 0x41,   // LDA #$00, STA $410B
                0xA9, 0x00, 0x8D, 0x00, 0x41,   // LDA #$00, STA $4100
                0xA9, 0x3C, 0x8D, 0x07, 0x41,   // LDA #$3C, STA $4107
                0xA9, 0x3D, 0x8D, 0x08, 0x41,   // LDA #$3D, STA $4108
                0xA9, 0x00, 0x8D, 0x09, 0x41,   // LDA #$00, STA $4109
                0x6C, 0xFC, 0xFF,               // JMP ($FFFC)
            };
            if (LcdStubAddr + stub.Length <= rom.Length)
                Array.Copy(stub, 0, rom, LcdStubAddr, stub.Length);
        }

        // ── Game list writer ──────────────────────────────────────────────────

        private static void WriteGameList(byte[] rom, List<string> names)
        {
            byte[] area = new byte[MenuEnd - MenuStart + 1];
            Array.Fill(area, (byte)0xFF);
            area[0] = (byte)(names.Count & 0xFF);
            area[1] = (byte)((names.Count >> 8) & 0xFF);
            Array.Copy(GameListConstants, 0, area, 2, GameListConstants.Length);
            int off = MenuHeaderEnd - MenuStart;
            foreach (string n in names)
            {
                byte[] nb = Encoding.ASCII.GetBytes(n);
                if (off + nb.Length + 1 > area.Length) break;
                Array.Copy(nb, 0, area, off, nb.Length);
                area[off + nb.Length] = 0;
                off += nb.Length + 1;
            }
            Array.Copy(area, 0, rom, MenuStart, area.Length);
        }

        // ── Kernel loader ─────────────────────────────────────────────────────

        internal static byte[] LoadKernel()
        {
            var asm = Assembly.GetExecutingAssembly();
            string[] candidates =
            {
                "original_menu_patched.rom",
                "VT03Builder.original_menu_patched.rom",
            };
            foreach (string name in candidates)
            {
                using var s = asm.GetManifestResourceStream(name);
                if (s != null) return ReadKernelStream(s, name);
            }
            string[] diskPaths =
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                             "original_menu_patched.rom"),
                Path.Combine(Directory.GetCurrentDirectory(),
                             "original_menu_patched.rom"),
            };
            foreach (string path in diskPaths)
            {
                if (!File.Exists(path)) continue;
                using var fs = File.OpenRead(path);
                return ReadKernelStream(fs, path);
            }
            string[] rn = asm.GetManifestResourceNames();
            throw new InvalidOperationException(
                "Kernel ROM not found. Embed Services\\original_menu_patched.rom and rebuild.\n" +
                $"Resources: {string.Join(", ", rn.DefaultIfEmpty("(none)"))}");
        }

        private static byte[] ReadKernelStream(Stream s, string source)
        {
            if (s.Length != 0x80000)
                throw new InvalidDataException(
                    $"Kernel '{source}' is {s.Length} bytes — expected 524288 (512 KB).");
            byte[] data = new byte[s.Length];
            _ = s.Read(data, 0, data.Length);
            return data;
        }
    }
}
