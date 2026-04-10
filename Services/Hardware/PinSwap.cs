namespace VT03Builder.Services.Hardware
{
    /// <summary>
    /// Applies the D1↔D9, D2↔D10 physical pin swap to a flash binary.
    ///
    /// Some VTxx OneBus boards have D1 and D9 physically crossed on the PCB,
    /// and D2 and D10 likewise. The .bin written to the programmer must have
    /// bits 1 and 2 swapped between each adjacent even/odd byte pair to
    /// compensate. Applied to .bin ONLY — .nes and .unf must be generated
    /// from unswapped data first.
    /// </summary>
    public static class PinSwap
    {
        /// <summary>
        /// Apply the pin swap in-place. Safe to call twice — it is self-inverse.
        /// </summary>
        public static void Apply(byte[] data)
        {
            for (int i = 0; i < data.Length / 2; i++)
            {
                byte b1  = data[i * 2];
                byte b2  = data[i * 2 + 1];
                byte tmp = b1;
                SetBit(ref b1, 1, GetBit(b2, 1));
                SetBit(ref b1, 2, GetBit(b2, 2));
                SetBit(ref b2, 1, GetBit(tmp, 1));
                SetBit(ref b2, 2, GetBit(tmp, 2));
                data[i * 2]     = b1;
                data[i * 2 + 1] = b2;
            }
        }

        private static bool GetBit(byte b, int bit) => ((b >> bit) & 1) == 1;
        private static void SetBit(ref byte b, int bit, bool v)
            => b = (byte)(v ? b | (1 << bit) : b & ~(1 << bit));
    }
}
