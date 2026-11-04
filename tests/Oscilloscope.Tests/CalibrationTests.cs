using Oscilloscope.Core.Device;
using Oscilloscope.Core.Export;
using Oscilloscope.Core.Protocol;
using Xunit;

namespace Oscilloscope.Tests;

public class Ad8370Tests
{
    [Fact]
    public void Splits_the_word_into_range_and_code()
    {
        Assert.False(Ad8370.IsHighRange(127));
        Assert.True(Ad8370.IsHighRange(128));
        Assert.Equal(51u, Ad8370.Code(0xB3));
        Assert.Equal(0xB3u, Ad8370.MakeWord(high: true, code: 51));
        Assert.Equal(18u, Ad8370.MakeWord(high: false, code: 18));
    }

    [Fact]
    public void Nominal_gain_follows_the_data_sheet_formula()
    {
        Assert.InRange(Ad8370.NominalVoltageGain(18), 1.003, 1.004);    // LG18, about 0 dB
        Assert.InRange(Ad8370.NominalVoltageGain(255), 50.10, 50.14);   // HG127, about 34 dB
        Assert.Equal(0, Ad8370.NominalVoltageGain(0));
        Assert.Equal(0, Ad8370.NominalVoltageGain(128));
        Assert.True(double.IsNegativeInfinity(Ad8370.NominalGainDb(0)));
    }
}

public class SampleStatsTests
{
    [Fact]
    public void Computes_mean_deviation_and_limits()
    {
        var stats = new SampleStats();
        stats.Add(new byte[] { 1, 2, 3, 4 });

        Assert.Equal(4, stats.Count);
        Assert.Equal(2.5, stats.Mean, 6);
        Assert.Equal(Math.Sqrt(1.25), stats.StdDev, 6);
        Assert.Equal(1, stats.Min);
        Assert.Equal(4, stats.Max);
    }

    [Fact]
    public void Accumulates_over_several_records()
    {
        var stats = new SampleStats();
        stats.Add(new byte[] { 10, 20 });
        stats.Add(new byte[] { 30, 40 });

        Assert.Equal(4, stats.Count);
        Assert.Equal(25, stats.Mean, 6);
    }
}

public class CsvExportTests
{
    private static string[] Lines(StringWriter writer) =>
        writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Waveform_export_writes_only_the_requested_range()
    {
        var header = new RecordHeader(
            Flags: 0, PayloadLength: 6, RecordId: 5, ChannelMask: 0x03,
            SampleBits: 8, SamplesPerChannel: 3, TriggerPosition: 1);
        var record = new AcquisitionRecord(header, new byte[] { 1, 2, 3, 4, 5, 6 });

        using var writer = new StringWriter();
        WaveformCsv.Write(writer, record, 10e-9, firstSample: 1, lastSample: 2);
        var lines = Lines(writer);

        Assert.Contains("sample,time_ns,ch1,ch2", lines);
        Assert.Contains("1,10,3,4", lines);
        Assert.Contains("2,20,5,6", lines);
        Assert.DoesNotContain("0,0,1,2", lines);
    }

    [Fact]
    public void Waveform_export_writes_only_the_channels_present()
    {
        var header = new RecordHeader(
            Flags: 0, PayloadLength: 2, RecordId: 1, ChannelMask: 0x02,
            SampleBits: 8, SamplesPerChannel: 2, TriggerPosition: 0);
        var record = new AcquisitionRecord(header, new byte[] { 7, 8 });

        using var writer = new StringWriter();
        WaveformCsv.Write(writer, record, 10e-9, firstSample: 0, lastSample: 1);
        var lines = Lines(writer);

        Assert.Contains("sample,time_ns,ch2", lines);
        Assert.Contains("0,0,7", lines);
        Assert.Contains("1,10,8", lines);
    }

    [Fact]
    public void Calibration_export_writes_header_and_rows()
    {
        var points = new[]
        {
            new CalibrationPoint(1, 18, 128.5, 0.5, 127, 130, 8192),
            new CalibrationPoint(1, 179, 3.25, 1.5, 0, 9, 8192),
        };
        var meta = new CalibrationMeta(1, 2048, 8192, 1, new DateTime(2026, 11, 4, 22, 0, 0));

        using var writer = new StringWriter();
        CalibrationCsv.Write(writer, points, meta);
        var lines = Lines(writer);

        Assert.Contains("# channel=1", lines);
        Assert.Contains("# dac_offset_code=2048", lines);
        Assert.Contains("18,low,18,0.03,128.5,0.5,127,130,8192,no", lines);
        Assert.Contains(lines, l => l.StartsWith("179,high,51,") && l.EndsWith(",3.25,1.5,0,9,8192,yes"));
    }
}
