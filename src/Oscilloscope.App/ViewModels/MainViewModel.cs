using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oscilloscope.Core.Device;
using Oscilloscope.Core.Protocol;

namespace Oscilloscope.App.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private DeviceClient? _device;
    private CancellationTokenSource? _acquisitionCts;

    /// <summary>Raised on the UI thread after a record is received. The view draws the plot.</summary>
    public event Action<AcquisitionRecord>? RecordReceived;

    [ObservableProperty]
    public partial string Host { get; set; } = "192.168.1.50";

    [ObservableProperty]
    public partial int Port { get; set; } = 5000;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAcquire))]
    [NotifyPropertyChangedFor(nameof(ConnectButtonText))]
    public partial bool IsConnected { get; set; }

    /// <summary>An acquisition (single or continuous) is in progress.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAcquire))]
    [NotifyPropertyChangedFor(nameof(CanToggleConnection))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAcquire))]
    [NotifyPropertyChangedFor(nameof(CanToggleConnection))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsConnectionLost { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Not connected.";

    [ObservableProperty]
    public partial string RecordInfo { get; set; } = "";

    public bool CanAcquire => IsConnected && !IsRunning && !IsBusy;

    public string ConnectButtonText => IsConnected ? "Disconnect" : "Connect";

    /// <summary>Connecting or disconnecting is blocked during an acquisition.</summary>
    public bool CanToggleConnection => !IsRunning && !IsBusy;

    [RelayCommand]
    private async Task ToggleConnectionAsync()
    {
        if (IsConnected)
            await DisconnectAsync();
        else
            await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        IsBusy = true;
        IsConnectionLost = false;
        Status = $"Connecting to {Host}:{Port} ...";
        var device = new DeviceClient();
        try
        {
            await device.ConnectAsync(Host, Port);
            uint id = await device.ReadRegisterAsync(Registers.DeviceId);
            uint version = await device.ReadRegisterAsync(Registers.ProtocolVersion);

            _device = device;
            IsConnected = true;
            Status = $"Connected. DEVICE_ID = 0x{id:X8}, protocol v{version}.";
        }
        catch (Exception ex)
        {
            await device.DisposeAsync();
            Status = $"Connection error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DisconnectAsync()
    {
        if (_device is not null)
            await _device.DisposeAsync();
        _device = null;
        IsConnected = false;
        IsConnectionLost = false;
        Status = "Disconnected.";
    }

    [RelayCommand]
    private Task SingleAsync() => RunAcquisitionAsync(continuous: false);

    [RelayCommand]
    private Task StartRunAsync() => RunAcquisitionAsync(continuous: true);

    /// <summary>Stops between exchanges, so the connection is never cut mid-frame.</summary>
    [RelayCommand]
    private void StopRun() => _acquisitionCts?.Cancel();

    /// <summary>Forces a trigger while the device is armed and waiting.</summary>
    [RelayCommand]
    private async Task ForceTriggerAsync()
    {
        if (_device is null) return;
        try
        {
            await _device.SendCommandAsync(Registers.CommandForceTrigger);
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
    }

    private async Task RunAcquisitionAsync(bool continuous)
    {
        var device = _device;
        if (device is null || IsRunning) return;

        _acquisitionCts = new CancellationTokenSource();
        var token = _acquisitionCts.Token;
        IsRunning = true;
        try
        {
            do
            {
                Status = "Waiting for trigger ...";
                var record = await device.AcquireTriggeredAsync(token);
                if (record is null)
                {
                    Status = "Stopped.";
                    return;
                }

                var h = record.Header;
                RecordInfo = $"Record #{h.RecordId}, {h.SamplesPerChannel} samples/channel";
                RecordReceived?.Invoke(record);
                Status = continuous ? "Continuous acquisition." : "Done.";

                // Limit the refresh rate to about 30 frames per second.
                if (continuous)
                    await Task.Delay(30, token);
            }
            while (continuous && !token.IsCancellationRequested);

            if (token.IsCancellationRequested)
                Status = "Stopped.";
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
        finally
        {
            IsRunning = false;
            _acquisitionCts?.Dispose();
            _acquisitionCts = null;
        }
    }

    private void HandleError(Exception ex)
    {
        Status = ex switch
        {
            DeviceErrorException e => $"Device error: {e.Code} (address 0x{e.Address:X2}).",
            TimeoutException => $"Timeout: {ex.Message}",
            _ => $"Error: {ex.Message}",
        };

        // DeviceClient closes the socket itself after a transport or protocol error.
        if (_device is { IsConnected: false })
        {
            _ = _device.DisposeAsync();
            _device = null;
            IsConnected = false;
            IsConnectionLost = true;
        }
    }

    public void Dispose()
    {
        _acquisitionCts?.Cancel();
        _ = _device?.DisposeAsync();
    }
}