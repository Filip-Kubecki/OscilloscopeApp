namespace Oscilloscope.Core.Device;

/// <summary>
/// The AD8370 gain word: bit 7 selects the range (0 = low, 1 = high), bits 6..0 are the gain code.
/// Nominal values come from the data sheet formula and are accurate to a few percent only.
/// </summary>
public static class Ad8370
{
    /// <summary>V/V per code in the low range.</summary>
    public const double Vernier = 0.055744;

    /// <summary>The high range is this much above the low range at the same code (17 dB).</summary>
    public const double HighRangeFactor = 7.079458;

    public static bool IsHighRange(uint word) => (word & 0x80) != 0;

    public static uint Code(uint word) => word & 0x7F;

    public static uint MakeWord(bool high, uint code) => (high ? 0x80u : 0u) | (code & 0x7F);

    public static double NominalVoltageGain(uint word) =>
        Code(word) * Vernier * (IsHighRange(word) ? HighRangeFactor : 1.0);

    /// <summary>Nominal gain in dB, negative infinity for code 0 (the gain stage is off).</summary>
    public static double NominalGainDb(uint word)
    {
        double gain = NominalVoltageGain(word);
        return gain > 0 ? 20 * Math.Log10(gain) : double.NegativeInfinity;
    }
}
