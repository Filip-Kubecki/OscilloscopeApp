using System.Buffers.Binary;
using Oscilloscope.Core.Protocol;
using Xunit;

namespace Oscilloscope.Tests;

public class FrameTests
{
    [Fact]
    public void Write_encodes_example_from_docs()
    {
        var bytes = Frame.Write(Registers.DebugScratch0, 0x12345678).ToBytes();
        Assert.Equal(new byte[] { 0xC7, 0xE2, 0x01, 0xF0, 0x12, 0x34, 0x56, 0x78 }, bytes);
    }

    [Fact]
    public void Read_encodes_example_from_docs()
    {
        var bytes = Frame.Read(Registers.DeviceId).ToBytes();
        Assert.Equal(new byte[] { 0xC7, 0xE2, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00 }, bytes);
    }

    [Fact]
    public void Parses_read_response()
    {
        byte[] raw = { 0xC7, 0xE2, 0x82, 0x00, 0x00, 0x00, 0xAB, 0x01 };

        Assert.True(Frame.TryParse(raw, out var frame));
        Assert.Equal(Command.ResponseRead, frame.Command);
        Assert.Equal(Registers.DeviceId, frame.Address);
        Assert.Equal(0x0000AB01u, frame.Data);
        Assert.False(frame.IsError);
    }

    [Fact]
    public void Parses_error_response()
    {
        byte[] raw = { 0xC7, 0xE2, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x03 };

        Assert.True(Frame.TryParse(raw, out var frame));
        Assert.True(frame.IsError);
        Assert.Equal(ErrorCode.ReadOnly, frame.ErrorCode);
    }

    [Fact]
    public void Rejects_bad_magic()
    {
        byte[] raw = { 0xAA, 0xE2, 0x82, 0x00, 0x00, 0x00, 0x00, 0x00 };
        Assert.False(Frame.TryParse(raw, out _));
    }

    [Fact]
    public void Rejects_short_buffer()
    {
        Assert.False(Frame.TryParse(new byte[] { 0xC7, 0xE2, 0x82 }, out _));
    }
}

public class RecordTests
{
    private static byte[] MakeHeader(
        byte mask = 0x01, uint samples = 4, uint? payload = null,
        byte type = 0xD0, uint id = 7, byte bits = 8)
    {
        var h = new byte[64];
        h[0] = 0xC7;
        h[1] = 0xE2;
        h[2] = type;
        uint channels = mask == 0x03 ? 2u : 1u;
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), payload ?? samples * channels * bits / 8);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(8), id);
        h[12] = mask;
        h[13] = bits;
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(16), samples);
        return h;
    }

    [Fact]
    public void Parses_valid_header()
    {
        var header = RecordHeader.Parse(MakeHeader(mask: 0x03, samples: 100, id: 42));

        Assert.Equal(42u, header.RecordId);
        Assert.Equal(100u, header.SamplesPerChannel);
        Assert.Equal(200u, header.PayloadLength);
        Assert.Equal(2, header.ChannelCount);
    }

    [Fact]
    public void Rejects_payload_length_mismatch()
    {
        Assert.Throws<ProtocolException>(() => RecordHeader.Parse(MakeHeader(payload: 999)));
    }

    [Fact]
    public void Rejects_wrong_block_type()
    {
        Assert.Throws<ProtocolException>(() => RecordHeader.Parse(MakeHeader(type: 0xD1)));
    }

    [Fact]
    public void Rejects_empty_channel_mask()
    {
        Assert.Throws<ProtocolException>(() => RecordHeader.Parse(MakeHeader(mask: 0x00)));
    }

    [Fact]
    public void Rejects_short_header()
    {
        Assert.Throws<ProtocolException>(() => RecordHeader.Parse(new byte[10]));
    }

    [Fact]
    public void Extracts_channels_from_interleaved_record()
    {
        var header = RecordHeader.Parse(MakeHeader(mask: 0x03, samples: 3));
        var record = new AcquisitionRecord(header, new byte[] { 1, 10, 2, 20, 3, 30 });

        Assert.Equal(new byte[] { 1, 2, 3 }, record.ExtractChannel(1));
        Assert.Equal(new byte[] { 10, 20, 30 }, record.ExtractChannel(2));
    }

    [Fact]
    public void Single_channel_record_rejects_missing_channel()
    {
        var header = RecordHeader.Parse(MakeHeader(mask: 0x01, samples: 3));
        var record = new AcquisitionRecord(header, new byte[] { 5, 6, 7 });

        Assert.Equal(new byte[] { 5, 6, 7 }, record.ExtractChannel(1));
        Assert.Throws<InvalidOperationException>(() => record.ExtractChannel(2));
    }
    
    [Fact]
    public void Accepts_maximum_record_with_two_channels()
    {
        var header = RecordHeader.Parse(MakeHeader(mask: 0x03, samples: 65536));
        Assert.Equal(131072u, header.PayloadLength);
    } 
}
