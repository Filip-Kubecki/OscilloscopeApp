using System.Buffers.Binary;
using static Oscilloscope.Core.Protocol.ProtocolConstants;

namespace Oscilloscope.Core.Protocol;

/// <summary>Data block header (64 B), see docs/protocol.md, section 5.1.</summary>
public readonly record struct RecordHeader(
    byte Flags,
    uint PayloadLength,
    uint RecordId,
    byte ChannelMask,
    byte SampleBits,
    uint SamplesPerChannel)
{
    public bool Overrange => (Flags & 0x01) != 0;

    /// <summary>2 for mask 03, 1 otherwise.</summary>
    public int ChannelCount => ChannelMask == 0x03 ? 2 : 1;

    public static RecordHeader Parse(ReadOnlySpan<byte> s)
    {
        if (s.Length < RecordHeaderLength)
            throw new ProtocolException($"Record header too short: {s.Length} B.");
        if (s[0] != Magic0 || s[1] != Magic1)
            throw new ProtocolException("Bad magic in the record header.");
        if (s[2] != RecordTypeData)
            throw new ProtocolException($"Unknown block type: 0x{s[2]:X2}.");

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
            throw new ProtocolException($"Invalid channel mask: 0x{ChannelMask:X2}.");
        if (SampleBits != 8)
            throw new ProtocolException($"Unsupported sample width: {SampleBits} bits.");

        ulong expected = (ulong)SamplesPerChannel * (ulong)ChannelCount * SampleBits / 8;
        if (expected != PayloadLength)
            throw new ProtocolException(
                $"PAYLOAD_LEN = {PayloadLength}, but the header fields imply {expected}.");
        if (PayloadLength > MaxPayloadLength)
            throw new ProtocolException($"PAYLOAD_LEN = {PayloadLength} exceeds the limit.");
    }
}