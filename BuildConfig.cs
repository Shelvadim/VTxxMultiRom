using System.Collections.Generic;

namespace VT03Builder.Models
{
    public class BuildConfig
    {
        /// <summary>NOR chip size in megabytes (e.g. 8 = 8 MB = 64 Mbit).</summary>
        public int          ChipSizeMb   { get; set; } = 8;
        public bool         AllowChrRam  { get; set; } = false;
        public bool         InitLcd      { get; set; } = false;
        public int          PinSwap      { get; set; } = 0;   // 0=None, 1=D1↔D9/D2↔D10
        public string       OutputPath  { get; set; } = "multicart";
        public bool         GenerateNes { get; set; } = true;
        public List<NesRom> Games       { get; set; } = new List<NesRom>();

        /// <summary>NES 2.0 mapper number (currently always 256).</summary>
        public int Mapper     { get; set; } = 256;

        /// <summary>
        /// NES 2.0 submapper number for mapper 256.
        /// 0=Normal, 1=Waixing VT03, 2=Power Joy Supermax, 3=Zechess/Hummer Team,
        /// 4=Sports Game 69-in-1, 5=Waixing VT02, 11=Vibes, 12=Cheertone,
        /// 13=Cube Tech, 14=Karaoto, 15=Jungletac.
        /// </summary>
        public int Submapper  { get; set; } = 0;

        public long ChipSizeBytes => (long)ChipSizeMb * 1024L * 1024L;
    }
}
