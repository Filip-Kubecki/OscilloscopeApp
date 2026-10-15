using System.Buffers.Binary;
using static Oscilloscope.Core.Protocol.ProtocolConstants;

namespace Oscilloscope.Core.Protocol;

/// <summary>Nagłówek bloku danych (64 B), zob. docs/protocol.md, sekcja 5.1.</summary>
public readonly record struct RecordHeader(
    byte Flags,
    uint PayloadLength,
    uint RecordId,
    byte ChannelMask,
    byte SampleBits,
    uint SamplesPerChannel)
{
    public bool Overrange => (Flags & 0x01) != 0;

    /// <summary>2 dla maski 03, 1 dla pozostałych.</summary>
    public int ChannelCount => ChannelMask == 0x03 ? 2 : 1;

    public static RecordHeader Parse(ReadOnlySpan<byte> s)
    {
        if (s.Length < RecordHeaderLength)
            throw new ProtocolException($"Nagłówek rekordu za krótki: {s.Length} B.");
        if (s[0] != Magic0 || s[1] != Magic1)
            throw new ProtocolException("Zły magic w nagłówku rekordu.");
        if (s[2] != RecordTypeData)
            throw new ProtocolException($"Nieznany typ bloku: 0x{s[2]:X2}.");

        var header = new RecordHeader(
            Flags: s[3],
            PayloadLength: BinaryPrimitives.ReadUInt32BigEndian(s[4..8]),
            RecordId: BinaryPrimitives.ReadUInt32BigEndian(s[8..12]),
            ChannelMask: s[12],
            SampleBits: s[13],
            SamplesPerChannel: BinaryPrimitives.ReadUInt32BigEndian(s[16..20]));

        header.Validate();
        return header;
    }

    private void Validate()
    {
        if (ChannelMask is 0 or > 3)
            throw new ProtocolException($"Niepoprawna maska kanałów: 0x{ChannelMask:X2}.");
        if (SampleBits != 8)
            throw new ProtocolException($"Nieobsługiwana liczba bitów na próbkę: {SampleBits}.");

        ulong expected = (ulong)SamplesPerChannel * (ulong)ChannelCount * SampleBits / 8;
        if (expected != PayloadLength)
            throw new ProtocolException(
                $"PAYLOAD_LEN = {PayloadLength}, a z nagłówka wynika {expected}.");
        if (PayloadLength > MaxPayloadLength)
            throw new ProtocolException($"PAYLOAD_LEN = {PayloadLength} przekracza limit.");
    }
}
