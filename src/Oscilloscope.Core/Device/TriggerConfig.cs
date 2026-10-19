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

/// <summary>Everything the application configures: trigger, pretrigger and record length (samples per channel).</summary>
public readonly record struct CaptureConfig(
    TriggerConfig Trigger,
    uint Pretrigger,
    uint RecordLength);

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

    /// <summary>Reads the pending trigger, pretrigger and record length.</summary>
    public static async Task<CaptureConfig> ReadCaptureConfigAsync(
        this DeviceClient device, CancellationToken ct = default)
    {
        var trigger = await device.ReadTriggerConfigAsync(ct);
        uint pretrigger = await device.ReadRegisterAsync(Registers.AcquisitionPretrigger, ct);
        uint length = await device.ReadRegisterAsync(Registers.AcquisitionRecordLength, ct);
        return new CaptureConfig(trigger, pretrigger, length);
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
    /// Writes record length, pretrigger and the trigger registers, then one COMMIT.
    /// Call it only while the acquisition is stopped.
    /// </summary>
    public static async Task ApplyCaptureConfigAsync(
        this DeviceClient device, CaptureConfig config, CancellationToken ct = default)
    {
        await device.WriteRegisterAsync(Registers.AcquisitionRecordLength, config.RecordLength, ct);
        await device.WriteRegisterAsync(Registers.AcquisitionPretrigger, config.Pretrigger, ct);
        await WriteTriggerRegistersAsync(device, config.Trigger, ct);
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