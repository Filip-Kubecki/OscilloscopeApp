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

    // Settings queued while an acquisition is running (the latest one wins).
    private readonly object _pendingLock = new();
    private CaptureConfig? _pendingConfig;

    // Last configuration known to be committed in the device.
    private CaptureConfig? _lastApplied;

    /// <summary>The device refused a configuration. The message is meant for the status bar.</summary>
    private sealed class ConfigRejectedException(string message) : Exception(message);

    /// <summary>Raised on the UI thread after a record is received. The view draws the plot.</summary>
    public event Action<AcquisitionRecord>? RecordReceived;

    [ObservableProperty]
    public partial string Host { get; set; } = "192.168.1.50";

    [ObservableProperty]
    public partial int Port { get; set; } = 5000;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAcquire))]
    [NotifyPropertyChangedFor(nameof(CanEditSettings))]
    [NotifyPropertyChangedFor(nameof(ConnectButtonText))]
    public partial bool IsConnected { get; set; }

    /// <summary>An acquisition (single or continuous) is in progress.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAcquire))]
    [NotifyPropertyChangedFor(nameof(CanToggleConnection))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAcquire))]
    [NotifyPropertyChangedFor(nameof(CanEditSettings))]
    [NotifyPropertyChangedFor(nameof(CanToggleConnection))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsConnectionLost { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Not connected.";

    [ObservableProperty]
    public partial string RecordInfo { get; set; } = "";

    // --- Settings panel ------------------------------------------------

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

    /// <summary>Record length in samples per channel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxPretrigger))]
    public partial decimal RecordLength { get; set; } = 65536;

    public decimal MaxRecordLength => ProtocolConstants.MaxSamplesPerChannel;

    /// <summary>Largest allowed pretrigger (assumed: record length - 1).</summary>
    public decimal MaxPretrigger => RecordLength > 0 ? RecordLength - 1 : 0;

    partial void OnRecordLengthChanged(decimal value)
    {
        if (Pretrigger > MaxPretrigger)
            Pretrigger = MaxPretrigger;
    }

    /// <summary>Trigger level last applied or read from the device. The view draws it as a line.</summary>
    [ObservableProperty]
    public partial int? AppliedTriggerLevel { get; set; }

    /// <summary>Pretrigger last applied or read from the device. The view draws it as a line.</summary>
    [ObservableProperty]
    public partial int? AppliedPretrigger { get; set; }

    public bool CanAcquire => IsConnected && !IsRunning && !IsBusy;

    /// <summary>Settings can be edited during an acquisition too (they are queued).</summary>
    public bool CanEditSettings => IsConnected && !IsBusy;

    public string ConnectButtonText => IsConnected ? "Disconnect" : "Connect";

    /// <summary>Connecting or disconnecting is blocked during an acquisition.</summary>
    public bool CanToggleConnection => !IsRunning && !IsBusy;

    // --- Connection ----------------------------------------------------

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
            var config = await device.ReadCaptureConfigAsync();

            _device = device;
            _lastApplied = config; // assumption: pending == active right after connecting
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
        _lastApplied = null;
        ClearPending();
        IsConnected = false;
        IsConnectionLost = false;
        AppliedTriggerLevel = null;
        AppliedPretrigger = null;
        Status = "Disconnected.";
    }

    // --- Settings ------------------------------------------------------

    private void ShowConfig(CaptureConfig config)
    {
        var t = config.Trigger;

        // The record length goes first: it limits the pretrigger.
        RecordLength = config.RecordLength;
        Pretrigger = config.Pretrigger;
        TriggerSourceIndex = (int)t.Source;
        TriggerEdgeIndex = (int)t.Edge;
        TriggerLevel = t.Level;
        TriggerHysteresis = t.Hysteresis;

        AppliedTriggerLevel = t.Level;
        AppliedPretrigger = (int)config.Pretrigger;
    }

    private CaptureConfig BuildConfig()
    {
        uint length = (uint)Math.Clamp(RecordLength, 1m, MaxRecordLength);
        uint pretrigger = (uint)Math.Clamp(Pretrigger, 0m, (decimal)(length - 1));

        var trigger = new TriggerConfig(
            (TriggerSource)TriggerSourceIndex,
            (TriggerEdge)TriggerEdgeIndex,
            (byte)Math.Clamp(TriggerLevel, 0, 255),
            (byte)Math.Clamp(TriggerHysteresis, 0, 255));

        return new CaptureConfig(trigger, pretrigger, length);
    }

    [RelayCommand]
    private async Task ApplyTriggerAsync()
    {
        var device = _device;
        if (device is null) return;

        var config = BuildConfig();

        if (IsRunning)
        {
            // Never COMMIT during a capture: the acquisition loop applies it after STOP.
            lock (_pendingLock)
                _pendingConfig = config;
            Status = "Settings queued, the acquisition restarts to apply them ...";
            return;
        }

        IsBusy = true;
        try
        {
            await device.SendCommandAsync(Registers.CommandStop);
            await ApplyConfigAsync(device, config);
            Status = "Settings applied.";
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

    /// <summary>
    /// Writes the configuration and COMMIT, then shows what the device holds.
    /// The device must be stopped. On BAD_VALUE it restores the last good settings.
    /// </summary>
    private async Task ApplyConfigAsync(DeviceClient device, CaptureConfig config)
    {
        try
        {
            await device.ApplyCaptureConfigAsync(config);
        }
        catch (DeviceErrorException ex) when (ex.Code == ErrorCode.BadValue)
        {
            string details = await DescribeDeviceAsync(device);
            await RestoreLastAppliedAsync(device);

            // Make the view redraw the markers from the values that really apply.
            OnPropertyChanged(nameof(AppliedTriggerLevel));
            OnPropertyChanged(nameof(AppliedPretrigger));

            throw new ConfigRejectedException(
                $"Register 0x{ex.Address:X2} rejected (BAD_VALUE), previous settings restored. {details}");
        }

        var applied = await device.ReadCaptureConfigAsync();
        _lastApplied = applied;
        ShowConfig(applied);
    }

    private async Task RestoreLastAppliedAsync(DeviceClient device)
    {
        if (_lastApplied is not { } good) return;
        try
        {
            await device.ApplyCaptureConfigAsync(good);
        }
        catch
        {
            // Best effort. If the connection is gone, the next exchange reports it.
        }
    }

    private static async Task<string> DescribeDeviceAsync(DeviceClient device)
    {
        try
        {
            var state = await device.GetStateAsync();
            var c = await device.ReadCaptureConfigAsync();
            uint mask = await device.ReadRegisterAsync(Registers.AcquisitionChannelMask);
            var t = c.Trigger;
            return $"Rejected values: state={state}, src={t.Source}, edge={t.Edge}, level={t.Level}, " +
                   $"hyst={t.Hysteresis}, len={c.RecordLength}, pre={c.Pretrigger}, mask=0x{mask:X2}.";
        }
        catch (Exception ex)
        {
            return $"(could not read the device back: {ex.Message})";
        }
    }

    // Callbacks for the acquisition loop in Core.
    private bool HasPendingConfig()
    {
        lock (_pendingLock)
            return _pendingConfig is not null;
    }

    private async Task ApplyPendingConfigAsync()
    {
        CaptureConfig? config;
        lock (_pendingLock)
        {
            config = _pendingConfig;
            _pendingConfig = null;
        }

        var device = _device;
        if (device is null || config is null) return;

        await ApplyConfigAsync(device, config.Value);
        Status = "Settings applied. Waiting for trigger ...";
    }

    private void ClearPending()
    {
        lock (_pendingLock)
            _pendingConfig = null;
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
                var record = await device.AcquireTriggeredAsync(
                    token, HasPendingConfig, ApplyPendingConfigAsync);
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
            ConfigRejectedException => ex.Message,
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