using Oscilloscope.Core.Protocol;

namespace Oscilloscope.Core.Device;

/// <summary>
/// Read-only state of one channel. Gain is the word last accepted by the PGA driver,
/// offset is the 12-bit DAC code. Flags: bit 0 ADC overrange (sticky, cleared by 0x14),
/// bit 1 PGA busy, bit 2 DAC busy (both momentary).
/// </summary>
public readonly record struct ChannelStatus(uint AppliedGain, uint AppliedOffset, uint Flags)
{
    public bool Overrange => (Flags & 0x01) != 0;
    public bool PgaBusy => (Flags & 0x02) != 0;
    public bool DacBusy => (Flags & 0x04) != 0;
}

public static class ChannelStatusExtensions
{
    /// <summary>Reads applied gain, applied offset and flags of channel 1 or 2.</summary>
    public static async Task<ChannelStatus> ReadChannelStatusAsync(
        this DeviceClient device, int channel, CancellationToken ct = default)
    {
        (byte gain, byte offset, byte flags) = channel switch
        {
            1 => (Registers.Channel1AppliedGain, Registers.Channel1AppliedOffset, Registers.Channel1Flags),
            2 => (Registers.Channel2AppliedGain, Registers.Channel2AppliedOffset, Registers.Channel2Flags),
            _ => throw new ArgumentOutOfRangeException(nameof(channel), "Channel must be 1 or 2."),
        };

        return new ChannelStatus(
            await device.ReadRegisterAsync(gain, ct),
            await device.ReadRegisterAsync(offset, ct),
            await device.ReadRegisterAsync(flags, ct));
    }
}
