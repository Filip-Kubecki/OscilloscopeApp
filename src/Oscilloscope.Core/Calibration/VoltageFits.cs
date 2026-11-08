using Oscilloscope.Core.Device;

namespace Oscilloscope.Core.Calibration;

/// <summary>A measurement at a known DC voltage: the ADC statistics at one gain word.</summary>
public readonly record struct VoltagePoint(double Volts, CalibrationPoint Point);

/// <summary>
/// Fit ADC code = Slope * volts + Intercept for one gain word. Slope is in ADC codes per input volt,
/// Intercept is the code at 0 V. Not valid when fewer than two usable points exist.
/// </summary>
public sealed record GainFit(
    int Channel, uint GainWord, int UsedPoints, int TotalPoints,
    double Slope, double Intercept, double RmsResidual, double MaxResidual,
    double MinVolts, double MaxVolts)
{
    public bool IsValid => double.IsFinite(Slope);

    /// <summary>Input voltage that moves the ADC by one code, in millivolts.</summary>
    public double LsbMillivolts => IsValid && Slope != 0 ? 1000.0 / Math.Abs(Slope) : double.NaN;

    public double VoltsFromCode(double code) => (code - Intercept) / Slope;

    public double VoltsAtCode0 => IsValid ? VoltsFromCode(0) : double.NaN;
    public double VoltsAtCode255 => IsValid ? VoltsFromCode(255) : double.NaN;
}

public static class VoltageFits
{
    /// <summary>One fit per channel and gain word. Points that touched the ADC limits are left out of the fit.</summary>
    public static IReadOnlyList<GainFit> Compute(IEnumerable<VoltagePoint> points)
    {
        var result = new List<GainFit>();

        var groups = points
            .GroupBy(p => (p.Point.Channel, p.Point.GainWord))
            .OrderBy(g => g.Key.Channel)
            .ThenBy(g => g.Key.GainWord);

        foreach (var group in groups)
        {
            var all = group.ToList();
            var used = all.Where(p => !p.Point.Clipped).ToList();
            var fit = LinearFit.Fit(used.Select(p => (p.Volts, p.Point.Mean)).ToList());
            (int channel, uint word) = group.Key;

            if (fit is { } f)
            {
                result.Add(new GainFit(
                    channel, word, used.Count, all.Count,
                    f.Slope, f.Intercept, f.RmsResidual, f.MaxResidual,
                    used.Min(p => p.Volts), used.Max(p => p.Volts)));
            }
            else
            {
                result.Add(new GainFit(
                    channel, word, used.Count, all.Count,
                    double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN));
            }
        }

        return result;
    }
}
