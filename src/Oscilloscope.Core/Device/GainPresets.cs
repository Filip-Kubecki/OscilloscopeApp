namespace Oscilloscope.Core.Device;

/// <summary>A selectable gain: the target voltage gain (V/V) and the AD8370 word that realizes it best.</summary>
public readonly record struct GainPreset(double Target, uint Word)
{
    /// <summary>Gain of the word according to the data sheet formula.</summary>
    public double NominalVoltsPerVolt => Ad8370.NominalVoltageGain(Word);

    /// <summary>How far the realized gain is from the target, in percent.</summary>
    public double ErrorPercent => (NominalVoltsPerVolt / Target - 1) * 100;

    public string Label
    {
        get
        {
            double db = 20 * Math.Log10(Target);
            return FormattableString.Invariant($"{Target:0.##} V/V ({db:+0.#;-0.#;0} dB)");
        }
    }
}

/// <summary>
/// The gains the application offers. Only these words are ever written to the PGA, so every one of them
/// can be calibrated. Change <see cref="Targets"/> to change the offer.
/// </summary>
public static class GainPresets
{
    private static readonly double[] Targets = { 0.1, 0.5, 1, 2, 4, 8, 16, 32 };

    public static IReadOnlyList<GainPreset> All { get; } =
        Targets.Select(target => new GainPreset(target, WordFor(target))).ToArray();

    /// <summary>
    /// The word whose nominal gain is closest to the requested one. The low range has finer steps,
    /// so it is used whenever the gain fits in it.
    /// </summary>
    public static uint WordFor(double voltsPerVolt)
    {
        double lowPerCode = Ad8370.Vernier;
        double highPerCode = Ad8370.Vernier * Ad8370.HighRangeFactor;

        long lowCode = (long)Math.Round(voltsPerVolt / lowPerCode);
        if (lowCode is >= 1 and <= 127)
            return Ad8370.MakeWord(false, (uint)lowCode);

        long highCode = Math.Clamp((long)Math.Round(voltsPerVolt / highPerCode), 1, 127);
        return Ad8370.MakeWord(true, (uint)highCode);
    }

    /// <summary>Index of the preset closest (in dB) to the gain of an arbitrary word. Words that switch the gain stage off give 0.</summary>
    public static int NearestIndex(uint word)
    {
        double gain = Ad8370.NominalVoltageGain(word);
        if (gain <= 0)
            return 0;

        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < All.Count; i++)
        {
            double distance = Math.Abs(Math.Log(gain / All[i].NominalVoltsPerVolt));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }
}
