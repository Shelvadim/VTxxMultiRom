namespace VT03Builder.Models
{
    /// <summary>
    /// Describes one submapper variant of a hardware target.
    /// Shown in the submapper dropdown in the UI.
    /// </summary>
    public class SubmapperInfo
    {
        public int     Number  { get; init; }
        public string  Name    { get; init; } = string.Empty;

        /// <summary>
        /// Optional warning shown when this submapper is selected.
        /// Null = no warning.
        /// </summary>
        public string? Warning { get; init; }

        public override string ToString() =>
            Warning != null
                ? $"{Number,2}  {Name}  ⚠"
                : $"{Number,2}  {Name}";
    }
}
