using System.Net.Sockets;
using Oscilloscope.Core.Protocol;
using static Oscilloscope.Core.Protocol.ProtocolConstants;

namespace Oscilloscope.Core.Device;

/// <summary>
/// Protocol client: one request in flight, timeouts handled on the host side.
/// After a protocol error, a timeout or a dropped connection it closes the socket
/// (per docs/protocol.md the board does not resynchronize block data).
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
                throw new ProtocolException($"Expected a read response, got 0x{(byte)response.Command:X2}.");
            return response.Data;
        }, ct);

    public Task WriteRegisterAsync(byte address, uint value, CancellationToken ct = default) =>
        ExecuteAsync(async stream =>
        {
            var response = await ExchangeAsync(stream, Frame.Write(address, value), ct);
            if (response.Command != Command.ResponseWrite)
                throw new ProtocolException($"Expected a write response, got 0x{(byte)response.Command:X2}.");
            return 0;
        }, ct);

    /// <summary>Sends a command (a write with data 0), e.g. ARM, STOP, FORCE_TRIGGER.</summary>
    public Task SendCommandAsync(byte address, CancellationToken ct = default) =>
        WriteRegisterAsync(address, 0, ct);

    /// <summary>Reads the last record. Null when the device returns ERROR_NOT_READY.</summary>
    public Task<AcquisitionRecord?> ReadRecordAsync(CancellationToken ct = default) =>
        ExecuteAsync<AcquisitionRecord?>(async stream =>
        {
            var request = Frame.Write(Registers.CommandReadRecord, 0);
            await WriteAsync(stream, request.ToBytes(), ct);
            var response = await ReadFrameAsync(stream, ct);

            if (response.Address != Registers.CommandReadRecord)
                throw new ProtocolException("The response is for a different address than the request.");
            if (response.IsError)
            {
                if (response.ErrorCode == ErrorCode.NotReady) return null;
                throw new DeviceErrorException(response.ErrorCode, response.Address);
            }
            if (response.Command != Command.ResponseWrite)
                throw new ProtocolException($"Unexpected response 0x{(byte)response.Command:X2}.");

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
            throw new ProtocolException("The response is for a different address than the request.");
        if (response.IsError)
            throw new DeviceErrorException(response.ErrorCode, response.Address);
        return response;
    }

    private async Task<Frame> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[FrameLength];
        await ReadExactlyAsync(stream, buffer, ct);
        if (!Frame.TryParse(buffer, out var frame))
            throw new ProtocolException("Bad magic in the response.");
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
            throw new TimeoutException($"No response within {Timeout.TotalSeconds:0.#} s.");
        }
    }

    /// <summary>Serializes requests and closes the connection after a transport or protocol error.</summary>
    private async Task<T> ExecuteAsync<T>(Func<NetworkStream, Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var stream = _stream ?? throw new InvalidOperationException("Not connected.");
            try
            {
                return await action(stream);
            }
            catch (DeviceErrorException)
            {
                throw; // a valid error frame, the connection is fine
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