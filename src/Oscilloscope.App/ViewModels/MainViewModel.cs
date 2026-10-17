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

    // --- Trigger panel -------------------------------------------------

    /// <summary>Index into the source list: 0 = channel 1, 1 = channel 2.</summary>
    [ObservableProperty]
    public partial int TriggerSourceIndex { get; set; }

    /// <summary>Index into the edge list: 0 = rising, 1 = falling, 2 = both.</summary>
    [ObservableProperty]
    public partial int TriggerEdgeIndex { get; set; }

    [ObservableProperty]
    public partial decimal TriggerLevel { get; set; } = 128;

    [ObservableProperty]
    public partial decimal TriggerHysteresis { get; set; } = 2;

    /// <summary>Samples kept from before the trigger, per channel.</summary>
    [ObservableProperty]
    public partial decimal Pretrigger { get; set; }

    /// <summary>Record length in samples per channel, as reported by the device.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxPretrigger))]
    public partial uint RecordLength { get; set; } = 8192;

    /// <summary>Largest allowed pretrigger (assumed: record length - 1).</summary>
    public decimal MaxPretrigger => RecordLength > 0 ? RecordLength - 1 : 0;

    /// <summary>Trigger level last sent to or read from the device. The view draws it as a line.</summary>
    [ObservableProperty]
    public partial int? AppliedTriggerLevel { get; set; }

    /// <summary>Pretrigger last sent to or read from the device. The view draws it as a line.</summary>
    [ObservableProperty]
    public partial int? AppliedPretrigger { get; set; }

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
            uint length = await device.ReadRecordLengthAsync();
            var config = await device.ReadCaptureConfigAsync();

            _device = device;
            RecordLength = length;
            ShowConfig(config);
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
        AppliedTriggerLevel = null;
        AppliedPretrigger = null;
        Status = "Disconnected.";
    }

    private void ShowConfig(CaptureConfig config)
    {
        var t = config.Trigger;
        TriggerSourceIndex = (int)t.Source;
        TriggerEdgeIndex = (int)t.Edge;
        TriggerLevel = t.Level;
        TriggerHysteresis = t.Hysteresis;
        Pretrigger = config.Pretrigger;

        AppliedTriggerLevel = t.Level;
        AppliedPretrigger = (int)config.Pretrigger;
    }

    [RelayCommand]
    private async Task ApplyTriggerAsync()
    {
        var device = _device;
        if (device is null) return;

        IsBusy = true;
        try
        {
            var trigger = new TriggerConfig(
                (TriggerSource)TriggerSourceIndex,
                (TriggerEdge)TriggerEdgeIndex,
                (byte)Math.Clamp(TriggerLevel, 0, 255),
                (byte)Math.Clamp(TriggerHysteresis, 0, 255));

            uint pretrigger = (uint)Math.Clamp(Pretrigger, 0, MaxPretrigger);

            await device.ApplyCaptureConfigAsync(new CaptureConfig(trigger, pretrigger));

            // Show exactly what the device now holds.
            RecordLength = await device.ReadRecordLengthAsync();
            ShowConfig(await device.ReadCaptureConfigAsync());
            Status = "Trigger settings applied.";
        }
        catch (DeviceErrorException ex) when (ex.Code == ErrorCode.BadValue)
        {
            Status = $"Register 0x{ex.Address:X2} rejected (BAD_VALUE). {await DescribeDeviceAsync(device)}";
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static async Task<string> DescribeDeviceAsync(DeviceClient device)
    {
        try
        {
            var state = await device.GetStateAsync();
            var c = await device.ReadCaptureConfigAsync();
            uint length = await device.ReadRecordLengthAsync();
            uint mask = await device.ReadRegisterAsync(Registers.AcquisitionChannelMask);
            var t = c.Trigger;
            return $"State={state}, src={t.Source}, edge={t.Edge}, level={t.Level}, " +
                   $"hyst={t.Hysteresis}, len={length}, pre={c.Pretrigger}, mask=0x{mask:X2}.";
        }
        catch (Exception ex)
        {
            return $"(could not read the device back: {ex.Message})";
        }
    }

    // --- Acquisition ---------------------------------------------------

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
            DeviceErrorException { Code: ErrorCode.BadValue } e =>
                $"Device rejected the value for register 0x{e.Address:X2} (BAD_VALUE).",
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