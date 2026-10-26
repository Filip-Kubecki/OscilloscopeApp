using System;
using System.ComponentModel;
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

    // True while the channel checkboxes are set from the device (no "last channel" check then).
    private bool _updatingChannels;

    /// <summary>The device refused a configuration. The message is meant for the status bar.</summary>
    private sealed class ConfigRejectedException(string message) : Exception(message);

    /// <summary>Raised on the UI thread after a record is received. The view draws the plot.</summary>
    public event Action<AcquisitionRecord>? RecordReceived;

    public ChannelViewModel Channel1 { get; }
    public ChannelViewModel Channel2 { get; }

    /// <summary>The connected client, or null. Used by the debug window.</summary>
    public DeviceClient? Device => _device;

    public MainViewModel()
    {
        Channel1 = new ChannelViewModel(1, enabled: true, ChannelColors.Channel1, OnChannelToggled);
        Channel2 = new ChannelViewModel(2, enabled: false, ChannelColors.Channel2, OnChannelToggled);

        Channel1.SelectRequested += c => SelectChannel(c.Number);
        Channel2.SelectRequested += c => SelectChannel(c.Number);
        Channel1.IsSelected = true;
    }

    private void OnChannelToggled(ChannelViewModel changed)
    {
        if (_updatingChannels)
            return;

        // At least one channel must stay enabled (mask 0 is invalid in the device).
        if (!Channel1.IsEnabled && !Channel2.IsEnabled)
        {
            changed.IsEnabled = true;
            Status = "At least one channel must stay enabled.";
            return;
        }

        UpdateRecordLengthLimit();
    }

    /// <summary>The maximum record length depends on the mask, so it is recomputed when channels change.</summary>
    private void UpdateRecordLengthLimit()
    {
        OnPropertyChanged(nameof(MaxRecordLength));
        if (RecordLength > MaxRecordLength)
            RecordLength = MaxRecordLength;
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName == nameof(CanEditSettings))
        {
            Channel1.CanEdit = CanEditSettings;
            Channel2.CanEdit = CanEditSettings;
        }
    }

    [ObservableProperty]
    public partial string Host { get; set; } = "192.168.1.50";

    [ObservableProperty]
    public partial int Port { get; set; } = 5000;

    [ObservableProperty]
    public partial bool IsPanelOpen { get; set; } = true;

    /// <summary>Which channel block is open in the side panel (1 or 2). The trigger block is always open.</summary>
    [ObservableProperty]
    public partial int SelectedChannel { get; set; } = 1;

    partial void OnSelectedChannelChanged(int value)
    {
        Channel1.IsSelected = value == 1;
        Channel2.IsSelected = value == 2;
    }

    public void SelectChannel(int number) => SelectedChannel = number == 2 ? 2 : 1;

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
    [NotifyPropertyChangedFor(nameof(TriggerSummary))]
    public partial int TriggerSourceIndex { get; set; }

    /// <summary>Index into the edge list: 0 = rising, 1 = falling, 2 = both.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TriggerSummary))]
    public partial int TriggerEdgeIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TriggerSummary))]
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

    /// <summary>One-line description shown in the header of the trigger block.</summary>
    public string TriggerSummary
    {
        get
        {
            string edge = TriggerEdgeIndex switch
            {
                0 => "rising",
                1 => "falling",
                _ => "both edges",
            };
            return $"CH{TriggerSourceIndex + 1}, {edge}, level {TriggerLevel:0}";
        }
    }

    /// <summary>65536 samples with one channel enabled, 32768 per channel with both.</summary>
    public decimal MaxRecordLength =>
        Channel1.IsEnabled && Channel2.IsEnabled
            ? ProtocolConstants.MaxSamplesDualChannel
            : ProtocolConstants.MaxSamplesSingleChannel;

    /// <summary>Largest allowed pretrigger (record length - 1).</summary>
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
            OnPropertyChanged(nameof(Device));
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

        _updatingChannels = true;
        Channel1.IsEnabled = (config.ChannelMask & 0x01) != 0;
        Channel2.IsEnabled = (config.ChannelMask & 0x02) != 0;
        _updatingChannels = false;

        Channel1.Gain = config.Channel1.Gain;
        Channel1.Offset = config.Channel1.Offset;
        Channel2.Gain = config.Channel2.Gain;
        Channel2.Offset = config.Channel2.Offset;

        // The record length limit depends on the mask, and the pretrigger on the record length.
        OnPropertyChanged(nameof(MaxRecordLength));
        RecordLength = config.RecordLength;
        Pretrigger = config.Pretrigger;

        TriggerSourceIndex = (int)t.Source;
        TriggerEdgeIndex = (int)t.Edge;
        TriggerLevel = t.Level;
        TriggerHysteresis = t.Hysteresis;

        AppliedTriggerLevel = t.Level;
        AppliedPretrigger = (int)config.Pretrigger;
    }

    private bool TryBuildConfig(out CaptureConfig config)
    {
        config = default;

        bool sourceEnabled = TriggerSourceIndex == 0 ? Channel1.IsEnabled : Channel2.IsEnabled;
        if (!sourceEnabled)
        {
            Status = "The trigger source channel is disabled. Enable it or pick another source.";
            return false;
        }

        uint length = (uint)Math.Clamp(RecordLength, 1m, MaxRecordLength);
        uint pretrigger = (uint)Math.Clamp(Pretrigger, 0m, (decimal)(length - 1));
        uint mask = (Channel1.IsEnabled ? 0x01u : 0u) | (Channel2.IsEnabled ? 0x02u : 0u);

        var trigger = new TriggerConfig(
            (TriggerSource)TriggerSourceIndex,
            (TriggerEdge)TriggerEdgeIndex,
            (byte)Math.Clamp(TriggerLevel, 0, 255),
            (byte)Math.Clamp(TriggerHysteresis, 0, 255));

        config = new CaptureConfig(
            trigger, pretrigger, length, mask,
            ToChannelConfig(Channel1), ToChannelConfig(Channel2));
        return true;
    }

    private static ChannelConfig ToChannelConfig(ChannelViewModel channel) => new(
        (uint)Math.Clamp(channel.Gain, 0m, ChannelViewModel.MaxGainWord),
        (uint)Math.Clamp(channel.Offset, 0m, ChannelViewModel.MaxOffsetCode));

    [RelayCommand]
    private async Task ApplyTriggerAsync()
    {
        var device = _device;
        if (device is null) return;

        if (!TryBuildConfig(out var config))
            return;

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
            var t = c.Trigger;
            return $"Rejected values: state={state}, src={t.Source}, edge={t.Edge}, level={t.Level}, " +
                   $"hyst={t.Hysteresis}, len={c.RecordLength}, pre={c.Pretrigger}, mask=0x{c.ChannelMask:X2}, " +
                   $"ch1 gain={c.Channel1.Gain} offset={c.Channel1.Offset}, " +
                   $"ch2 gain={c.Channel2.Gain} offset={c.Channel2.Offset}.";
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