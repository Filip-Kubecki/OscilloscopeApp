using System.Globalization;
using Oscilloscope.Core.Calibration;
using Oscilloscope.Core.Device;

namespace Oscilloscope.Core.Export;

/// <summary>Raw points of a voltage calibration. The fits are written as comment lines at the top.</summary>
public static class VoltageCalibrationCsv
{
    public static void Write(
        TextWriter writer, IReadOnlyList<VoltagePoint> points, IReadOnlyList<GainFit> fits, CalibrationMeta meta)
    {
        var inv = CultureInfo.InvariantCulture;

        writer.Write("# Voltage calibration (known DC voltages on the input). Values are raw 8-bit ADC codes.\n");
        writer.Write(FormattableString.Invariant($"# channel={meta.Channel}\n"));
        writer.Write(FormattableString.Invariant(
            $"# dac_offset_code={(meta.DacOffsetCode is { } code ? code.ToString(inv) : "unknown")}\n"));
        writer.Write(FormattableString.Invariant($"# samples_per_point={meta.SamplesPerPoint}\n"));
        writer.Write(FormattableString.Invariant($"# records_per_point={meta.RecordsPerPoint}\n"));
        writer.Write($"# date={meta.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", inv)}\n");
        writer.Write("# Fit per gain word: mean_adc = slope * volts + intercept (points at the ADC limit are excluded).\n");

        foreach (var f in fits)
        {
            if (f.IsValid)
            {
                writer.Write(FormattableString.Invariant(
                    $"# fit gain_word={f.GainWord} slope_codes_per_volt={f.Slope:0.####} intercept_code={f.Intercept:0.####} rms_residual_codes={f.RmsResidual:0.####} lsb_mv={f.LsbMillivolts:0.###} used={f.UsedPoints}/{f.TotalPoints}\n"));
            }
            else
            {
                writer.Write(FormattableString.Invariant(
                    $"# fit gain_word={f.GainWord} not enough usable points ({f.UsedPoints}/{f.TotalPoints})\n"));
            }
        }

        writer.Write("volts,gain_word,range,code,gain_nominal_vv,mean_adc,stddev_adc,min_adc,max_adc,samples,at_adc_limit\n");

        foreach (var vp in points)
        {
            var p = vp.Point;
            writer.Write(FormattableString.Invariant(
                $"{vp.Volts:0.######},{p.GainWord},{(p.IsHighRange ? "high" : "low")},{p.Code},{Ad8370.NominalVoltageGain(p.GainWord):0.####},{p.Mean:0.####},{p.StdDev:0.####},{p.Min},{p.Max},{p.Samples},{(p.Clipped ? "yes" : "no")}\n"));
        }
    }
}
