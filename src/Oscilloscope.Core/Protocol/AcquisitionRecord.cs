namespace Oscilloscope.Core.Protocol;

/// <summary>Kompletny rekord: nagłówek i surowe próbki.</summary>
public sealed class AcquisitionRecord
{
    public RecordHeader Header { get; }
    public byte[] Samples { get; }

    public AcquisitionRecord(RecordHeader header, byte[] samples)
    {
        if (samples.Length != header.PayloadLength)
            throw new ProtocolException(
                $"Liczba próbek ({samples.Length}) różni się od PAYLOAD_LEN ({header.PayloadLength}).");
        Header = header;
        Samples = samples;
    }

    /// <summary>Próbki jednego kanału (1 lub 2) jako osobna tablica.</summary>
    public byte[] ExtractChannel(int channel)
    {
        if (channel is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(channel), "Kanał to 1 lub 2.");

        int bit = 1 << (channel - 1);
        if ((Header.ChannelMask & bit) == 0)
            throw new InvalidOperationException($"Kanał {channel} nie jest w tym rekordzie.");

        if (Header.ChannelMask != 0x03)
            return (byte[])Samples.Clone();

        // Maska 03: próbki naprzemiennie ch1[0] ch2[0] ch1[1] ch2[1] ...
        int count = (int)Header.SamplesPerChannel;
        int offset = channel - 1;
        var result = new byte[count];
        for (int i = 0; i < count; i++)
            result[i] = Samples[2 * i + offset];
        return result;
    }
}
