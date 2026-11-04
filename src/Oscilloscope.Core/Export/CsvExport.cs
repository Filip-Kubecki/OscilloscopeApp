using System.Globalization;
using System.Text;
using Oscilloscope.Core.Device;
using Oscilloscope.Core.Protocol;

namespace Oscilloscope.Core.Export;

/// <summary>Writes a range of a record as CSV: sample index, time from the first sample and the ADC codes.</summary>
public static class WaveformCsv
{
    public static void Write(
        TextWriter writer, AcquisitionRecord record, double samplePeriodSeconds, int firstSample, int lastSample)
    {
        var h = record.Header;
        int count = (int)h.SamplesPerChannel;
        int first = Math.Clamp(firstSample, 0, count - 1);
        int last = Math.Clamp(lastSample, first, count - 1);
        double periodNs = samplePeriodSeconds * 1e9;

        byte[]? ch1 = (h.ChannelMask & 0x01) != 0 ? record.ExtractChannel(1) : null;
        byte[]? ch2 = (h.ChannelMask & 0x02) != 0 ? record.ExtractChannel(2) : null;

        writer.Write("# Oscilloscope waveform export. Values are raw 8-bit ADC codes.\n");
        writer.Write(FormattableString.Invariant($"# record_id={h.RecordId}\n"));
        writer.Write(FormattableString.Invariant($"# sample_period_ns={periodNs:0.###}\n"));
        writer.Write(FormattableString.Invariant($"# samples_in_record={count}\n"));
        writer.Write(FormattableString.Invariant($"# trigger_sample={h.TriggerPosition}\n"));
        writer.Write(FormattableString.Invariant($"# exported_samples={last - first + 1} (from {first} to {last})\n"));
        writer.Write(FormattableString.Invariant($"# overrange={(h.Overrange ? "yes" : "no")}\n"));

        var line = new StringBuilder("sample,time_ns");
        if (ch1 is not null) line.Append(",ch1");
        if (ch2 is not null) line.Append(",ch2");
        writer.Write(line.Append('\n').ToString());

        for (int i = first; i <= last; i++)
        {
            line.Clear();
            line.Append(i.ToString(CultureInfo.InvariantCulture)).Append(',');
            line.Append((i * periodNs).ToString("0.###", CultureInfo.InvariantCulture));
            if (ch1 is not null) line.Append(',').Append(ch1[i]);
            if (ch2 is not null) line.Append(',').Append(ch2[i]);
            writer.Write(line.Append('\n').ToString());
        }
    }
}

public sealed record CalibrationMeta(
    int Channel, uint? DacOffsetCode, uint SamplesPerPoint, int RecordsPerPoint, DateTime Timestamp);

/// <summary>Writes the calibration results: one row per gain word.</summary>
public static class CalibrationCsv
{
    public static void Write(TextWriter writer, IEnumerable<CalibrationPoint> points, CalibrationMeta meta)
    {
        var inv = CultureInfo.InvariantCulture;

        writer.Write("# DC offset calibration (input shorted). Values are raw 8-bit ADC codes.\n");
        writer.Write(FormattableString.Invariant($"# channel={meta.Channel}\n"));
        writer.Write(FormattableString.Invariant(
            $"# dac_offset_code={(meta.DacOffsetCode is { } code ? code.ToString(inv) : "unknown")}\n"));
        writer.Write(FormattableString.Invariant($"# samples_per_point={meta.SamplesPerPoint}\n"));
        writer.Write(FormattableString.Invariant($"# records_per_point={meta.RecordsPerPoint}\n"));
        writer.Write($"# date={meta.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", inv)}\n");
        writer.Write("# gain_nominal_db comes from the data sheet formula (a few percent accurate).\n");
        writer.Write("gain_word,range,code,gain_nominal_db,mean_adc,stddev_adc,min_adc,max_adc,samples,at_adc_limit\n");

        foreach (var p in points)
        {
            string db = double.IsFinite(p.NominalGainDb) ? p.NominalGainDb.ToString("0.##", inv) : "";
            writer.Write(FormattableString.Invariant(
                $"{p.GainWord},{(p.IsHighRange ? "high" : "low")},{p.Code},{db},{p.Mean:0.####},{p.StdDev:0.####},{p.Min},{p.Max},{p.Samples},{(p.Clipped ? "yes" : "no")}\n"));
        }
    }
}
