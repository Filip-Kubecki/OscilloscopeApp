using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Oscilloscope.Core.Device;

namespace Oscilloscope.App.ViewModels;

/// <summary>One analog channel in the side panel.</summary>
public partial class ChannelViewModel(
    int number, bool enabled, string colorHex, Action<ChannelViewModel> enabledChanged)
    : ViewModelBase
{
    /// <summary>Largest AD8370 gain word: bit 7 selects the range (0-127 low, 128-255 high), bits 6..0 are the code.</summary>
    public const decimal MaxGainWord = 255;

    /// <summary>Largest DAC code (MCP4822 is a 12-bit DAC).</summary>
    public const decimal MaxOffsetCode = 4095;

    public int Number { get; } = number;
    public string Title { get; } = $"CH{number}";

    /// <summary>The channel's color, the same as its trace on the plot.</summary>
    public IBrush Accent { get; } = Brush.Parse(colorHex);

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = enabled;

    /// <summary>The block is highlighted in the side panel.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>The advanced overlay of this channel is open. Set by MainViewModel.</summary>
    [ObservableProperty]
    public partial bool IsAdvancedOpen { get; set; }

    /// <summary>Settings may be edited (connected and not busy). Set by MainViewModel.</summary>
    [ObservableProperty]
    public partial bool CanEdit { get; set; }

    /// <summary>PGA gain word, raw register value.</summary>
    [ObservableProperty]
    public partial decimal Gain { get; set; }

    /// <summary>DAC code of the input offset, raw register value.</summary>
    [ObservableProperty]
    public partial decimal Offset { get; set; }

    // --- Read-only state from the device -------------------------------

    /// <summary>What the device reports as applied (null when unknown, e.g. not connected).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AppliedGainText))]
    [NotifyPropertyChangedFor(nameof(AppliedOffsetText))]
    [NotifyPropertyChangedFor(nameof(OverrangeText))]
    [NotifyPropertyChangedFor(nameof(IsOverrange))]
    [NotifyPropertyChangedFor(nameof(BusyText))]
    public partial ChannelStatus? AppliedStatus { get; set; }

    public string AppliedGainText => AppliedStatus is { } s
        ? $"Applied gain: {s.AppliedGain} (0x{s.AppliedGain:X2})"
        : "Applied gain: -";

    public string AppliedOffsetText => AppliedStatus is { } s
        ? $"Applied offset: {s.AppliedOffset} (0x{s.AppliedOffset:X3})"
        : "Applied offset: -";

    public bool IsOverrange => AppliedStatus?.Overrange == true;

    public string OverrangeText => AppliedStatus is { } s
        ? $"Overrange: {(s.Overrange ? "yes" : "no")}"
        : "Overrange: -";

    public string BusyText => AppliedStatus is { } s
        ? $"PGA busy: {(s.PgaBusy ? "yes" : "no")}, DAC busy: {(s.DacBusy ? "yes" : "no")}"
        : "PGA busy: -, DAC busy: -";

    // --- Events --------------------------------------------------------

    /// <summary>Raised when the user clicks the block.</summary>
    public event Action<ChannelViewModel>? SelectRequested;

    /// <summary>Raised when the user clicks the Advanced button of the block.</summary>
    public event Action<ChannelViewModel>? AdvancedRequested;

    /// <summary>Raised when the user asks for the calibration window of this channel.</summary>
    public event Action<ChannelViewModel>? CalibrationRequested;

    public void RequestSelect() => SelectRequested?.Invoke(this);

    public void RequestAdvanced() => AdvancedRequested?.Invoke(this);

    public void RequestCalibration() => CalibrationRequested?.Invoke(this);

    partial void OnIsEnabledChanged(bool value) => enabledChanged(this);
}
