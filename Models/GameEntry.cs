using VT03Builder.Models;

namespace VT03Builder.Models
{
    /// <summary>
    /// A game that has been successfully placed in flash, together with
    /// the target-specific config record the menu loader will use.
    /// Produced by RomBuilder.Build() and consumed by WriteGameTable().
    /// </summary>
    public class GameEntry
    {
        public NesRom  Rom          { get; }
        public string  DisplayName  { get; }
        public int     NorOffset    { get; }
        public byte[]  ConfigRecord { get; }

        public GameEntry(NesRom rom, string displayName, int norOffset, byte[] configRecord)
        {
            Rom          = rom;
            DisplayName  = displayName;
            NorOffset    = norOffset;
            ConfigRecord = configRecord;
        }
    }
}
