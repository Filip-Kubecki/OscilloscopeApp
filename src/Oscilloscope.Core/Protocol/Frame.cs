using System.Buffers.Binary;
using static Oscilloscope.Core.Protocol.ProtocolConstants;

namespace Oscilloscope.Core.Protocol;

/// <summary>Ramka 8-bajtowa: magic(2) komenda(1) adres(1) dane(4, big-endian).</summary>
public readonly record struct Frame(Command Command, byte Address, uint Data)
{
    public static Frame Read(byte address) => new(Command.Read, address, 0);
    public static Frame Write(byte address, uint value) => new(Command.Write, address, value);

    public bool IsError => Command == Command.ResponseError;

    /// <summary>Kod błędu (najmłodszy bajt danych). Sensowny tylko gdy <see cref="IsError"/>.</summary>
    public ErrorCode ErrorCode => (ErrorCode)(Data & 0xFF);

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < FrameLength)
            throw new ArgumentException("Bufor za krótki na ramkę.", nameof(destination));

        destination[0] = Magic0;
        destination[1] = Magic1;
        destination[2] = (byte)Command;
        destination[3] = Address;
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], Data);
    }

    public byte[] ToBytes()
    {
        var bytes = new byte[FrameLength];
        WriteTo(bytes);
        return bytes;
    }

    /// <summary>False, gdy brak 8 bajtów albo zły magic.</summary>
    public static bool TryParse(ReadOnlySpan<byte> source, out Frame frame)
    {
        frame = default;
        if (source.Length < FrameLength) return false;
        if (source[0] != Magic0 || source[1] != Magic1) return false;

        frame = new Frame(
            (Command)source[2],
            source[3],
            BinaryPrimitives.ReadUInt32BigEndian(source[4..]));
        return true;
    }
}
