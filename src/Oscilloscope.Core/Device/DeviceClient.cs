using System.Net.Sockets;
using Oscilloscope.Core.Protocol;
using static Oscilloscope.Core.Protocol.ProtocolConstants;

namespace Oscilloscope.Core.Device;

/// <summary>
/// Klient protokołu: jedno zapytanie w toku, timeouty po stronie hosta.
/// Po błędzie protokołu, timeoucie albo zerwaniu połączenia zamyka gniazdo
/// (zgodnie z docs/protocol.md płytka nie resynchronizuje danych).
/// </summary>
public sealed class DeviceClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);
    public bool IsConnected => _tcp?.Connected == true && _stream is not null;

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        Close();
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await WithTimeout(ct, t => tcp.ConnectAsync(host, port, t).AsTask());
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public Task<uint> ReadRegisterAsync(byte address, CancellationToken ct = default) =>
        ExecuteAsync(async stream =>
        {
            var response = await ExchangeAsync(stream, Frame.Read(address), ct);
            if (response.Command != Command.ResponseRead)
                throw new ProtocolException($"Oczekiwano odpowiedzi odczytu, jest 0x{(byte)response.Command:X2}.");
            return response.Data;
        }, ct);

    public Task WriteRegisterAsync(byte address, uint value, CancellationToken ct = default) =>
        ExecuteAsync(async stream =>
        {
            var response = await ExchangeAsync(stream, Frame.Write(address, value), ct);
            if (response.Command != Command.ResponseWrite)
                throw new ProtocolException($"Oczekiwano odpowiedzi zapisu, jest 0x{(byte)response.Command:X2}.");
            return 0;
        }, ct);

    /// <summary>Wysyła komendę (zapis z danymi 0), np. ARM, STOP, FORCE_TRIGGER.</summary>
    public Task SendCommandAsync(byte address, CancellationToken ct = default) =>
        WriteRegisterAsync(address, 0, ct);

    /// <summary>Pobiera ostatni rekord. Null, gdy urządzenie zwróci ERROR_NOT_READY.</summary>
    public Task<AcquisitionRecord?> ReadRecordAsync(CancellationToken ct = default) =>
        ExecuteAsync<AcquisitionRecord?>(async stream =>
        {
            var request = Frame.Write(Registers.CommandReadRecord, 0);
            await WriteAsync(stream, request.ToBytes(), ct);
            var response = await ReadFrameAsync(stream, ct);

            if (response.Address != Registers.CommandReadRecord)
                throw new ProtocolException("Odpowiedź dotyczy innego adresu niż zapytanie.");
            if (response.IsError)
            {
                if (response.ErrorCode == ErrorCode.NotReady) return null;
                throw new DeviceErrorException(response.ErrorCode, response.Address);
            }
            if (response.Command != Command.ResponseWrite)
                throw new ProtocolException($"Nieoczekiwana odpowiedź 0x{(byte)response.Command:X2}.");

            var headerBytes = new byte[RecordHeaderLength];
            await ReadExactlyAsync(stream, headerBytes, ct);
            var header = RecordHeader.Parse(headerBytes);

            var samples = new byte[header.PayloadLength];
            await ReadExactlyAsync(stream, samples, ct);
            return new AcquisitionRecord(header, samples);
        }, ct);

    // ---------------------------------------------------------------

    private async Task<Frame> ExchangeAsync(NetworkStream stream, Frame request, CancellationToken ct)
    {
        await WriteAsync(stream, request.ToBytes(), ct);
        var response = await ReadFrameAsync(stream, ct);

        if (response.Address != request.Address)
            throw new ProtocolException("Odpowiedź dotyczy innego adresu niż zapytanie.");
        if (response.IsError)
            throw new DeviceErrorException(response.ErrorCode, response.Address);
        return response;
    }

    private async Task<Frame> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[FrameLength];
        await ReadExactlyAsync(stream, buffer, ct);
        if (!Frame.TryParse(buffer, out var frame))
            throw new ProtocolException("Zły magic w odpowiedzi.");
        return frame;
    }

    private Task WriteAsync(NetworkStream stream, byte[] data, CancellationToken ct) =>
        WithTimeout(ct, t => stream.WriteAsync(data, t).AsTask());

    private Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct) =>
        WithTimeout(ct, t => stream.ReadExactlyAsync(buffer, t).AsTask());

    private async Task WithTimeout(CancellationToken ct, Func<CancellationToken, Task> action)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            await action(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Brak odpowiedzi w ciągu {Timeout.TotalSeconds:0.#} s.");
        }
    }

    /// <summary>Serializuje zapytania i zamyka połączenie po błędzie transportu lub protokołu.</summary>
    private async Task<T> ExecuteAsync<T>(Func<NetworkStream, Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var stream = _stream ?? throw new InvalidOperationException("Brak połączenia.");
            try
            {
                return await action(stream);
            }
            catch (DeviceErrorException)
            {
                throw; // poprawna ramka błędu, połączenie jest w porządku
            }
            catch
            {
                Close();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Close()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }
}
