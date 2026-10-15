namespace Oscilloscope.Core.Protocol;

/// <summary>A complete record: header and raw samples.</summary>
public sealed class AcquisitionRecord
{
    public RecordHeader Header { get; }
    public byte[] Samples { get; }

    public AcquisitionRecord(RecordHeader header, byte[] samples)
    {
        if (samples.Length != header.PayloadLength)
            throw new ProtocolException(
                $"Sample count ({samples.Length}) differs from PAYLOAD_LEN ({header.PayloadLength}).");
        Header = header;
        Samples = samples;
    }

    /// <summary>Samples of one channel (1 or 2) as a separate array.</summary>
    public byte[] ExtractChannel(int channel)
    {
        if (channel is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(channel), "Channel must be 1 or 2.");

        int bit = 1 << (channel - 1);
        if ((Header.ChannelMask & bit) == 0)
            throw new InvalidOperationException($"Channel {channel} is not in this record.");

        if (Header.ChannelMask != 0x03)
            return (byte[])Samples.Clone();

        // Mask 03: samples are interleaved ch1[0] ch2[0] ch1[1] ch2[1] ...
        int count = (int)Header.SamplesPerChannel;
        int offset = channel - 1;
        var result = new byte[count];
        for (int i = 0; i < count; i++)
            result[i] = Samples[2 * i + offset];
        return result;
    }
}