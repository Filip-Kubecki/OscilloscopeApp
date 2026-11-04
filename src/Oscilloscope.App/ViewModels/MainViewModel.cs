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
    /// <summary>Time between samples without decimation (100 MHz ADC clock, docs/protocol.md section 5.1).</summary>
    private const double BaseSamplePeriod = 10e-9;

    /// <summary>Hysteresis (ADC codes) the application keeps by default. Applied on connect when the device differs.</summary>
    private const byte DefaultHysteresis = 3;

    /// <summary>How often the channel status (applied gain, offset, flags) is read while an overlay is open.</summary>
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromMilliseconds(500);

    private DeviceClient? _device;
    private CancellationTokenSource? _acquisitionCts;
    private CancellationTokenSource? _statusCts;

    // Settings queued while an acquisition is running (the latest one wins).
    private readonly object _pendingLock = new();
    private CaptureConfig? _pendingConfig;

    // Last configuration known to be committed in the device.
    private CaptureConfig? _lastApplied;

    // True while the channel checkboxes are set from the device (no "last channel" check then).
    private bool _updatingChannels;

    /// <summary>The device refused a configuration. The message is meant for the status bar.</summary>
    private sealed class ConfigRejectedException(string message) : Exception(message);

    /// <summary>
    /// Raised on the UI thread after a record is received. The second argument is the time between
    /// two samples of the record in seconds (10 ns times the decimation). The view draws the plot.
    /// </summary>
    public event Action<AcquisitionRecord, double>? RecordReceived;

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
        Channel1.AdvancedRequested += ToggleChannelAdvanced;
        Channel2.AdvancedRequested += ToggleChannelAdvanced;
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

    // Closing the panel also closes the advanced overlay that belongs to it.
    partial void OnIsPanelOpenChanged(bool value)
    {
        if (!value)
            AdvancedSection = 0;
    }

    /// <summary>The highlighted channel block in the side panel (1 or 2). Both blocks are always open.</summary>
    [ObservableProperty]
    public partial int SelectedChannel { get; set; } = 1;

    partial void OnSelectedChannelChanged(int value)
    {
        Channel1.IsSelected = value == 1;
        Channel2.IsSelected = value == 2;
    }

    public void SelectChannel(int number) => SelectedChannel = number == 2 ? 2 : 1;

    // --- Advanced overlay (floats over the plot, left of the side panel) ---

    /// <summary>Which advanced overlay is open: 0 = none, 1 = trigger, 2 = CH1, 3 = CH2.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAdvancedOpen))]
    [NotifyPropertyChangedFor(nameof(IsTriggerAdvancedOpen))]
    [NotifyPropertyChangedFor(nameof(IsChannel1AdvancedOpen))]
    [NotifyPropertyChangedFor(nameof(IsChannel2AdvancedOpen))]
    public partial int AdvancedSection { get; set; }

    public bool IsAdvancedOpen => AdvancedSection != 0;
    public bool IsTriggerAdvancedOpen => AdvancedSection == 1;
    public bool IsChannel1AdvancedOpen => AdvancedSection == 2;
    public bool IsChannel2AdvancedOpen => AdvancedSection == 3;

    partial void OnAdvancedSectionChanged(int value)
    {
        Channel1.IsAdvancedOpen = value == 2;
        Channel2.IsAdvancedOpen = value == 3;

        // A channel overlay just opened: show fresh values right away, polling takes over from here.
        if (value >= 2 && _device is { } device)
            _ = RefreshChannelStatusSafeAsync(device);
    }

    [RelayCommand]
    private void ToggleTriggerAdvanced() => AdvancedSection = AdvancedSection == 1 ? 0 : 1;

    [RelayCommand]
    private void CloseAdvanced() => AdvancedSection = 0;

    private void ToggleChannelAdvanced(ChannelViewModel channel)
    {
        int section = channel.Number + 1;
        AdvancedSection = AdvancedSection == section ? 0 : section;

        if (AdvancedSection == section)
            SelectChannel(channel.Number);
    }

    // --- Connection state ----------------------------------------------

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

    // --- Settings ------------------------------------------------------

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
    public partial decimal TriggerHysteresis { get; set; } = DefaultHysteresis;

    /// <summary>Samples kept from before the trigger, per channel.</summary>
    [ObservableProperty]
    public partial decimal Pretrigger { get; set; }

    /// <summary>Record length in samples per channel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxPretrigger))]
    [NotifyPropertyChangedFor(nameof(RecordDurationText))]
    public partial decimal RecordLength { get; set; } = 65536;

    /// <summary>Only every Nth sample goes to the record (1 = no decimation).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SampleRateText))]
    [NotifyPropertyChangedFor(nameof(RecordDurationText))]
    public partial decimal Decimation { get; set; } = 1;

    /// <summary>
    /// Assumed upper limit of the decimation field. The register is 32-bit and the register map
    /// does not state a limit, so the device decides (BAD_VALUE ends up on the status bar).
    /// </summary>
    public decimal MaxDecimation => 1_000_000;

    /// <summary>Effective sample rate for the decimation typed in the panel.</summary>
    public string SampleRateText =>
        $"Sample rate: {FormatRate(1.0 / (BaseSamplePeriod * DecimationFactor))}";

    /// <summary>Length in time of a record for the length and decimation typed in the panel.</summary>
    public string RecordDurationText =>
        $"Record time: {FormatDuration((double)RecordLength * DecimationFactor * BaseSamplePeriod)}";

    private double DecimationFactor => Math.Max(1.0, (double)Decimation);

    /// <summary>One-line description of the trigger settings.</summary>
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

    // --- Formatting helpers --------------------------------------------

    private static string FormatRate(double hertz) => hertz switch
    {
        >= 1e6 => $"{hertz / 1e6:0.###} MHz",
        >= 1e3 => $"{hertz / 1e3:0.###} kHz",
        _ => $"{hertz:0.###} Hz",
    };

    private static string FormatDuration(double seconds) => seconds switch
    {
        >= 1 => $"{seconds:0.###} s",
        >= 1e-3 => $"{seconds * 1e3:0.###} ms",
        >= 1e-6 => $"{seconds * 1e6:0.###} µs",
        _ => $"{seconds * 1e9:0.###} ns",
    };

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
            await RefreshChannelStatusAsync(device);
            IsConnected = true;
            OnPropertyChanged(nameof(Device));
            Status = $"Connected. DEVICE_ID = 0x{id:X8}, protocol v{version}.";
            StartStatusPolling(device);

            if (config.Trigger.Hysteresis != DefaultHysteresis)
                await ApplyDefaultHysteresisAsync(device, config);
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

    /// <summary>The device holds a different hysteresis than the default: stop, write it and COMMIT.</summary>
    private async Task ApplyDefaultHysteresisAsync(DeviceClient device, CaptureConfig config)
    {
        try
        {
            var trigger = config.Trigger with { Hysteresis = DefaultHysteresis };
            await device.SendCommandAsync(Registers.CommandStop);
            await ApplyConfigAsync(device, config with { Trigger = trigger });
            Status += $" Hysteresis set to {DefaultHysteresis}.";
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
    }

    private async Task DisconnectAsync()
    {
        StopStatusPolling();
        if (_device is not null)
            await _device.DisposeAsync();
        _device = null;
        _lastApplied = null;
        ClearPending();
        Channel1.AppliedStatus = null;
        Channel2.AppliedStatus = null;
        IsConnected = false;
        IsConnectionLost = false;
        AppliedTriggerLevel = null;
        AppliedPretrigger = null;
        Status = "Disconnected.";
    }

    // --- Channel status (applied gain/offset, flags) --------------------

    /// <summary>Reads applied gain, applied offset and flags of both channels.</summary>
    private async Task RefreshChannelStatusAsync(DeviceClient device)
    {
        Channel1.AppliedStatus = await device.ReadChannelStatusAsync(1);
        Channel2.AppliedStatus = await device.ReadChannelStatusAsync(2);
    }

    private async Task RefreshChannelStatusSafeAsync(DeviceClient device)
    {
        try
        {
            await RefreshChannelStatusAsync(device);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(device, _device))
                HandleError(ex);
        }
    }

    private void StartStatusPolling(DeviceClient device)
    {
        StopStatusPolling();
        _statusCts = new CancellationTokenSource();
        _ = PollChannelStatusAsync(device, _statusCts.Token);
    }

    private void StopStatusPolling()
    {
        _statusCts?.Cancel();
        _statusCts?.Dispose();
        _statusCts = null;
    }

    /// <summary>
    /// Reads the channel status every 0.5 s, but only while a channel overlay is open.
    /// Cancellation is checked only between exchanges, so it never cuts the connection mid-frame.
    /// </summary>
    private async Task PollChannelStatusAsync(DeviceClient device, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(StatusPollInterval, token);

                if (!ReferenceEquals(device, _device))
                    return;
                if (AdvancedSection < 2)
                    continue;

                await RefreshChannelStatusAsync(device);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && ReferenceEquals(device, _device))
                HandleError(ex);
        }
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
        Decimation = Math.Max(1u, config.Decimation);

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
        uint decimation = (uint)Math.Clamp(Decimation, 1m, MaxDecimation);
        uint mask = (Channel1.IsEnabled ? 0x01u : 0u) | (Channel2.IsEnabled ? 0x02u : 0u);

        var trigger = new TriggerConfig(
            (TriggerSource)TriggerSourceIndex,
            (TriggerEdge)TriggerEdgeIndex,
            (byte)Math.Clamp(TriggerLevel, 0, 255),
            (byte)Math.Clamp(TriggerHysteresis, 0, 255));

        config = new CaptureConfig(
            trigger, pretrigger, length, decimation, mask,
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
        await RefreshChannelStatusAsync(device);
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
                   $"hyst={t.Hysteresis}, len={c.RecordLength}, pre={c.Pretrigger}, dec={c.Decimation}, " +
                   $"mask=0x{c.ChannelMask:X2}, " +
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

                // The configuration applied before this acquisition decides the sample period.
                double decimation = Math.Max(1u, _lastApplied?.Decimation ?? 1u);
                double samplePeriod = decimation * BaseSamplePeriod;

                var h = record.Header;
                string duration = FormatDuration(h.SamplesPerChannel * samplePeriod);
                RecordInfo = $"Record #{h.RecordId}, {h.SamplesPerChannel} samples/channel, {duration}";
                RecordReceived?.Invoke(record, samplePeriod);
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
            StopStatusPolling();
            _ = _device.DisposeAsync();
            _device = null;
            IsConnected = false;
            IsConnectionLost = true;
        }
    }

    public void Dispose()
    {
        StopStatusPolling();
        _acquisitionCts?.Cancel();
        _ = _device?.DisposeAsync();
    }
}