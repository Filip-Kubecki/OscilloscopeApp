using Oscilloscope.Core.Protocol;

namespace Oscilloscope.Core.Device;

public enum TriggerSource : uint
{
    Channel1 = 0,
    Channel2 = 1,
}

public enum TriggerEdge : uint
{
    Rising = 0,
    Falling = 1,
    Both = 2,
}

/// <summary>Trigger settings. Level and hysteresis are in ADC codes (0..255).</summary>
public readonly record struct TriggerConfig(
    TriggerSource Source,
    TriggerEdge Edge,
    byte Level,
    byte Hysteresis);

/// <summary>Analog front end of one channel, as raw register values (PGA gain word, DAC code).</summary>
public readonly record struct ChannelConfig(uint Gain, uint Offset);

/// <summary>
/// Everything the application configures: trigger, pretrigger, record length
/// (samples per channel), the channel mask (bit 0 = channel 1, bit 1 = channel 2)
/// and the analog settings of both channels.
/// </summary>
public readonly record struct CaptureConfig(
    TriggerConfig Trigger,
    uint Pretrigger,
    uint RecordLength,
    uint ChannelMask,
    ChannelConfig Channel1,
    ChannelConfig Channel2);

public static class TriggerExtensions
{
    /// <summary>
    /// Reads the pending trigger registers (not necessarily the active ones,
    /// the device returns the value written since the last COMMIT).
    /// </summary>
    public static async Task<TriggerConfig> ReadTriggerConfigAsync(
        this DeviceClient device, CancellationToken ct = default)
    {
        uint source = await device.ReadRegisterAsync(Registers.TriggerSource, ct);
        uint edge = await device.ReadRegisterAsync(Registers.TriggerEdge, ct);
        uint level = await device.ReadRegisterAsync(Registers.TriggerLevel, ct);
        uint hysteresis = await device.ReadRegisterAsync(Registers.TriggerHysteresis, ct);

        if (!Enum.IsDefined(typeof(TriggerSource), source))
            throw new ProtocolException($"Unknown TRIGGER_SOURCE value: {source}.");
        if (!Enum.IsDefined(typeof(TriggerEdge), edge))
            throw new ProtocolException($"Unknown TRIGGER_EDGE value: {edge}.");
        if (level > 255 || hysteresis > 255)
            throw new ProtocolException("TRIGGER_LEVEL or TRIGGER_HYSTERESIS out of 0..255.");

        return new TriggerConfig((TriggerSource)source, (TriggerEdge)edge, (byte)level, (byte)hysteresis);
    }

    /// <summary>Reads the whole pending configuration.</summary>
    public static async Task<CaptureConfig> ReadCaptureConfigAsync(
        this DeviceClient device, CancellationToken ct = default)
    {
        var trigger = await device.ReadTriggerConfigAsync(ct);
        uint pretrigger = await device.ReadRegisterAsync(Registers.AcquisitionPretrigger, ct);
        uint length = await device.ReadRegisterAsync(Registers.AcquisitionRecordLength, ct);
        uint mask = await device.ReadRegisterAsync(Registers.AcquisitionChannelMask, ct);

        if (mask is 0 or > 3)
            throw new ProtocolException($"Unexpected ACQUISITION_CHANNEL_MASK value: 0x{mask:X}.");

        var ch1 = new ChannelConfig(
            await device.ReadRegisterAsync(Registers.Channel1Gain, ct),
            await device.ReadRegisterAsync(Registers.Channel1Offset, ct));
        var ch2 = new ChannelConfig(
            await device.ReadRegisterAsync(Registers.Channel2Gain, ct),
            await device.ReadRegisterAsync(Registers.Channel2Offset, ct));

        return new CaptureConfig(trigger, pretrigger, length, mask, ch1, ch2);
    }

    /// <summary>Pending record length in samples per channel.</summary>
    public static Task<uint> ReadRecordLengthAsync(
        this DeviceClient device, CancellationToken ct = default) =>
        device.ReadRegisterAsync(Registers.AcquisitionRecordLength, ct);

    /// <summary>Writes the four trigger registers, then COMMIT.</summary>
    public static async Task ApplyTriggerConfigAsync(
        this DeviceClient device, TriggerConfig config, CancellationToken ct = default)
    {
        await WriteTriggerRegistersAsync(device, config, ct);
        await device.SendCommandAsync(Registers.CommandCommit, ct);
    }

    /// <summary>
    /// Writes the whole configuration, then one COMMIT. The device must be IDLE
    /// (COMMIT is accepted only there).
    /// <para>
    /// Record length and channel mask are checked against each other when written
    /// (docs/protocol.md, section 3.1): moving to mask 03 needs the length to be at most 65536
    /// first, a length above 65536 needs a single-channel mask first.
    /// </para>
    /// </summary>
    public static async Task ApplyCaptureConfigAsync(
        this DeviceClient device, CaptureConfig config, CancellationToken ct = default)
    {
        if (config.ChannelMask == 0x03)
        {
            await device.WriteRegisterAsync(Registers.AcquisitionRecordLength, config.RecordLength, ct);
            await device.WriteRegisterAsync(Registers.AcquisitionChannelMask, config.ChannelMask, ct);
        }
        else
        {
            await device.WriteRegisterAsync(Registers.AcquisitionChannelMask, config.ChannelMask, ct);
            await device.WriteRegisterAsync(Registers.AcquisitionRecordLength, config.RecordLength, ct);
        }

        await device.WriteRegisterAsync(Registers.AcquisitionPretrigger, config.Pretrigger, ct);
        await WriteTriggerRegistersAsync(device, config.Trigger, ct);

        await device.WriteRegisterAsync(Registers.Channel1Gain, config.Channel1.Gain, ct);
        await device.WriteRegisterAsync(Registers.Channel1Offset, config.Channel1.Offset, ct);
        await device.WriteRegisterAsync(Registers.Channel2Gain, config.Channel2.Gain, ct);
        await device.WriteRegisterAsync(Registers.Channel2Offset, config.Channel2.Offset, ct);

        await device.SendCommandAsync(Registers.CommandCommit, ct);
    }

    private static async Task WriteTriggerRegistersAsync(
        DeviceClient device, TriggerConfig config, CancellationToken ct)
    {
        await device.WriteRegisterAsync(Registers.TriggerSource, (uint)config.Source, ct);
        await device.WriteRegisterAsync(Registers.TriggerEdge, (uint)config.Edge, ct);
        await device.WriteRegisterAsync(Registers.TriggerLevel, config.Level, ct);
        await device.WriteRegisterAsync(Registers.TriggerHysteresis, config.Hysteresis, ct);
    }
}