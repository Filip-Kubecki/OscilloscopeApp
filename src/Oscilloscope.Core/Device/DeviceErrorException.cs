using Oscilloscope.Core.Protocol;

namespace Oscilloscope.Core.Device;

/// <summary>Urządzenie odpowiedziało ramką błędu (RESPONSE_ERROR).</summary>
public sealed class DeviceErrorException(ErrorCode code, byte address)
    : Exception($"Urządzenie zwróciło błąd {code} dla adresu 0x{address:X2}.")
{
    public ErrorCode Code { get; } = code;
    public byte Address { get; } = address;
}
