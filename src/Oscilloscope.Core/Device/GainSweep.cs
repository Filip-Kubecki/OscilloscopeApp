using System.Diagnostics;
using Oscilloscope.Core.Protocol;

namespace Oscilloscope.Core.Device;

/// <summary>One measured gain setting: ADC statistics with the input shorted.</summary>
public readonly record struct CalibrationPoint(
    int Channel, uint GainWord, double Mean, double StdDev, byte Min, byte Max, long Samples)
{
    public bool IsHighRange => Ad8370.IsHighRange(GainWord);
    public uint Code => Ad8370.Code(GainWord);
    public double NominalGainDb => Ad8370.NominalGainDb(GainWord);

    /// <summary>A sample touched an end of the ADC range, so the mean is biased.</summary>
    public bool Clipped => Min == 0 || Max == 255;
}

/// <summary>
/// Settings of one sweep. <paramref name="SettleMs"/> is the pause after the new gain is applied and
/// <paramref name="DiscardRecords"/> is the number of records acquired and thrown away before measuring,
/// both so that the signal path (coupling capacitors, offset) can settle.
/// </summary>
public sealed record GainSweepOptions(
    int Channel,
    IReadOnlyList<uint> Words,
    uint SamplesPerPoint = 8192,
    int RecordsPerPoint = 1,
    int SettleMs = 100,
    int DiscardRecords = 1);

/// <summary>Measures the mean ADC code at each gain word. The device is stopped and its configuration restored at the end.</summary>
public static class GainSweep
{
    public static async Task RunAsync(
        DeviceClient device, GainSweepOptions options, IProgress<CalibrationPoint> progress, CancellationToken stop)
    {
        if (options.Channel is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(options), "Channel must be 1 or 2.");
        if (options.Words.Count == 0)
            return;

        await device.SendCommandAsync(Registers.CommandStop);
        var original = await device.ReadCaptureConfigAsync();

        try
        {
            await device.ApplyCaptureConfigAsync(BuildSweepConfig(original, options));

            foreach (uint word in options.Words)
            {
                // Checked only between exchanges, so the connection is never cut mid-frame.
                if (stop.IsCancellationRequested)
                    break;

                var point = await MeasureAsync(device, options, word, stop);
                if (point is null)
                    break; // stopped during the settling time: the point is incomplete

                progress.Report(point.Value);
            }
        }
        finally
        {
            await RestoreAsync(device, original);
        }
    }

    /// <summary>Only the measured channel, a short record, trigger source on that channel.</summary>
    private static CaptureConfig BuildSweepConfig(CaptureConfig original, GainSweepOptions options)
    {
        uint length = Math.Clamp(options.SamplesPerPoint, 1u, ProtocolConstants.MaxSamplesSingleChannel);
        uint mask = options.Channel == 1 ? 0x01u : 0x02u;
        var source = options.Channel == 1 ? TriggerSource.Channel1 : TriggerSource.Channel2;

        return original with
        {
            Trigger = original.Trigger with { Source = source },
            Pretrigger = 0,
            RecordLength = length,
            Decimation = 1,
            Mode = AcquisitionMode.Single,
            ChannelMask = mask,
        }; 
    }

    /// <summary>Null when the sweep was stopped before the point was complete.</summary>
    private static async Task<CalibrationPoint?> MeasureAsync(
        DeviceClient device, GainSweepOptions options, uint word, CancellationToken stop)
    {
        byte gainRegister = options.Channel == 1 ? Registers.Channel1Gain : Registers.Channel2Gain;

        // COMMIT is accepted only in IDLE.
        await device.SendCommandAsync(Registers.CommandStop);
        await device.WriteRegisterAsync(gainRegister, word);
        await device.SendCommandAsync(Registers.CommandCommit);
        await WaitForGainAsync(device, options.Channel, word);

        // Let the signal path settle, then throw away the first records after the change.
        if (options.SettleMs > 0 && !await DelayOrStopAsync(TimeSpan.FromMilliseconds(options.SettleMs), stop))
            return null;

        for (int i = 0; i < Math.Max(0, options.DiscardRecords); i++)
        {
            if (stop.IsCancellationRequested)
                return null;
            await AcquireAsync(device);
        }

        var stats = new SampleStats();
        for (int i = 0; i < Math.Max(1, options.RecordsPerPoint); i++)
        {
            var record = await AcquireAsync(device);
            stats.Add(record.ExtractChannel(options.Channel));
        }

        return new CalibrationPoint(
            options.Channel, word, stats.Mean, stats.StdDev, stats.Min, stats.Max, stats.Count);
    }

    private static async Task<AcquisitionRecord> AcquireAsync(DeviceClient device) =>
        await device.AcquireSingleAsync()
        ?? throw new ProtocolException("The device returned NOT_READY after a forced trigger.");

    /// <summary>Waits for the given time. Returns false when the sweep was stopped meanwhile.</summary>
    private static async Task<bool> DelayOrStopAsync(TimeSpan time, CancellationToken stop)
    {
        try
        {
            // No exchange with the device is in progress here, so cancelling the wait is safe.
            await Task.Delay(time, stop);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Waits until the driver reports the requested word and the PGA is no longer busy.</summary>
    private static async Task WaitForGainAsync(DeviceClient device, int channel, uint word)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var status = await device.ReadChannelStatusAsync(channel);
            if (status.AppliedGain == word && !status.PgaBusy)
                return;

            if (timer.Elapsed > TimeSpan.FromSeconds(1))
                throw new TimeoutException(
                    $"The PGA did not settle on gain word {word} (applied gain {status.AppliedGain}, PGA busy: {status.PgaBusy}).");

            await Task.Delay(2);
        }
    }

    private static async Task RestoreAsync(DeviceClient device, CaptureConfig original)
    {
        try
        {
            await device.SendCommandAsync(Registers.CommandStop);
            await device.ApplyCaptureConfigAsync(original);
        }
        catch
        {
            // Best effort. If the connection is gone, the next exchange reports it.
        }
    }
}