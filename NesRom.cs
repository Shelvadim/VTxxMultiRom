using System;
using System.IO;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("VT03Builder.Tests")]

namespace VT03Builder.Models
{
    public enum MirroringMode { Horizontal = 0, Vertical = 1, FourScreen = 2 }

    public class NesRom
    {
        private static readonly byte[] Magic = { 0x4E, 0x45, 0x53, 0x1A };

        public string FilePath    { get; private set; } = string.Empty;
        public string FileName    => Path.GetFileName(FilePath);
        public string DisplayName { get; set; } = string.Empty;

        public int           PrgRomBanks { get; private set; }   // x16KB
        public int           ChrRomBanks { get; private set; }   // x8KB
        public int           Mapper      { get; private set; }
        public MirroringMode Mirroring   { get; private set; }
        public bool          HasBattery  { get; private set; }
        public bool          HasTrainer  { get; private set; }
        public bool          Vertical    => Mirroring == MirroringMode.Vertical;

        public byte[] PrgData { get; private set; } = Array.Empty<byte>();
        public byte[] ChrData { get; private set; } = Array.Empty<byte>();

        /// <summary>PRG + CHR concatenated, no iNES header. Used by RomBuilder.</summary>
        public byte[] RawData
        {
            get
            {
                var r = new byte[PrgData.Length + ChrData.Length];
                Buffer.BlockCopy(PrgData, 0, r, 0, PrgData.Length);
                Buffer.BlockCopy(ChrData, 0, r, PrgData.Length, ChrData.Length);
                return r;
            }
        }

        public int PrgSize => PrgData.Length;
        public int ChrSize => ChrData.Length;

        public bool   IsValid    { get; private set; }
        public string ParseError { get; private set; } = string.Empty;

        public static NesRom Load(string filePath)
        {
            var rom = new NesRom
            {
                FilePath    = filePath,
                DisplayName = Path.GetFileNameWithoutExtension(filePath)
            };

            try
            {
                byte[] raw = File.ReadAllBytes(filePath);
                if (raw.Length < 16)
                    throw new InvalidDataException("File too small to be a valid iNES ROM.");

                for (int i = 0; i < 4; i++)
                    if (raw[i] != Magic[i])
                        throw new InvalidDataException("Not a valid iNES file (bad magic bytes).");

                bool nes20 = (raw[7] & 0x0C) == 0x08;
                if (nes20)
                {
                    rom.Mapper      = (raw[8] << 8) | (raw[7] & 0xF0) | (raw[6] >> 4);
                    int prgMsb      = raw[9] & 0x0F;
                    rom.PrgRomBanks = (prgMsb << 8) | raw[4];
                    int chrMsb      = (raw[9] >> 4) & 0x0F;
                    rom.ChrRomBanks = (chrMsb << 8) | raw[5];
                }
                else
                {
                    rom.Mapper      = ((raw[6] >> 4) & 0x0F) | (raw[7] & 0xF0);
                    rom.PrgRomBanks = raw[4];
                    rom.ChrRomBanks = raw[5];
                }

                byte f6 = raw[6];
                rom.Mirroring  = (f6 & 0x08) != 0 ? MirroringMode.FourScreen
                               : ((f6 & 0x01) != 0 ? MirroringMode.Vertical : MirroringMode.Horizontal);
                rom.HasBattery = (f6 & 0x02) != 0;
                rom.HasTrainer = (f6 & 0x04) != 0;

                int trainerSize = rom.HasTrainer ? 512 : 0;
                int prgSize     = rom.PrgRomBanks * 16384;
                int chrSize     = rom.ChrRomBanks * 8192;
                int prgStart    = 16 + trainerSize;
                int chrStart    = prgStart + prgSize;

                if (raw.Length < chrStart + chrSize)
                    throw new InvalidDataException(
                        $"File truncated. Expected {chrStart + chrSize} bytes, got {raw.Length}.");

                rom.PrgData = new byte[prgSize];
                Buffer.BlockCopy(raw, prgStart, rom.PrgData, 0, prgSize);

                if (chrSize > 0)
                {
                    rom.ChrData = new byte[chrSize];
                    Buffer.BlockCopy(raw, chrStart, rom.ChrData, 0, chrSize);
                }

                rom.IsValid = true;
            }
            catch (Exception ex)
            {
                rom.IsValid    = false;
                rom.ParseError = ex.Message;
            }

            return rom;
        }

        public string MapperName =>
            Mapper switch
            {
                0 => "NROM",  1 => "MMC1",  2 => "UxROM",
                3 => "CNROM", 4 => "MMC3",  _ => $"Mapper{Mapper}"
            };

        public string MapperDescription => $"{MapperName} ({Mapper})";

        // Mappers supported by this VT03 OneBus builder
        public bool IsSupportedByVT03 => Mapper is 0 or 3 or 4;

        /// <summary>True if this game uses CHR-RAM instead of CHR-ROM.</summary>
        public bool HasChrRam => ChrRomBanks == 0;

        /// <summary>
        /// VT03 OneBus MMC3 compatibility warning.
        /// Returns null if OK, or a short warning string if likely to grey-screen.
        /// </summary>
        public string? Vt03CompatWarning
        {
            get
            {
                if (Mapper != 4) return null;
                if (HasChrRam)
                    return "CHR-RAM — will grey screen (VT03 MMC3 needs CHR-ROM)";
                if (PrgSize > 256 * 1024)
                    return "PRG>256KB — may grey screen if game uses MMC3 IRQ";
                return null;
            }
        }


        /// <summary>Create a synthetic NesRom for unit tests — no file I/O.</summary>
        internal static NesRom CreateForTest(int mapper, int prgKb, int chrKb,
                                             bool vertical = false,
                                             string fileName = "test.nes")
        {
            int prgSize = prgKb * 1024;
            int chrSize = chrKb * 1024;
            var rom = new NesRom
            {
                FilePath    = fileName,
                DisplayName = System.IO.Path.GetFileNameWithoutExtension(fileName),
                Mapper      = mapper,
                PrgRomBanks = prgKb / 16,
                ChrRomBanks = chrKb / 8,
                Mirroring   = vertical ? MirroringMode.Vertical : MirroringMode.Horizontal,
                IsValid     = true,
            };
            // Fill PRG with a recognisable repeating pattern based on bank number
            var prg = new byte[prgSize];
            for (int i = 0; i < prgSize; i++)
                prg[i] = (byte)((i / 8192) + 1);   // bank N filled with byte (N+1)
            // Fill CHR with 0xC0 | bank index
            var chr = new byte[chrSize];
            for (int i = 0; i < chrSize; i++)
                chr[i] = (byte)(0xC0 | ((i / 1024) & 0x3F));
            rom.PrgData = prg;
            rom.ChrData = chr;
            return rom;
        }

        public override string ToString() => DisplayName;
    }
}
