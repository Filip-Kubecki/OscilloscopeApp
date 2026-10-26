using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Oscilloscope.App.ViewModels;

/// <summary>One analog channel in the side panel.</summary>
public partial class ChannelViewModel(
    int number, bool enabled, string colorHex, Action<ChannelViewModel> enabledChanged)
    : ViewModelBase
{
    /// <summary>Largest PGA gain word accepted by the UI (AD8370 word + range bit, to be confirmed).</summary>
    public const decimal MaxGainWord = 511;

    /// <summary>Largest DAC code (MCP4822 is a 12-bit DAC).</summary>
    public const decimal MaxOffsetCode = 4095;

    public int Number { get; } = number;
    public string Title { get; } = $"CH{number}";

    /// <summary>The channel's color, the same as its trace on the plot.</summary>
    public IBrush Accent { get; } = Brush.Parse(colorHex);

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = enabled;

    /// <summary>The block is open (highlighted) in the side panel.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Settings may be edited (connected and not busy). Set by MainViewModel.</summary>
    [ObservableProperty]
    public partial bool CanEdit { get; set; }

    /// <summary>PGA gain word, raw register value.</summary>
    [ObservableProperty]
    public partial decimal Gain { get; set; }

    /// <summary>DAC code of the input offset, raw register value.</summary>
    [ObservableProperty]
    public partial decimal Offset { get; set; }

    /// <summary>Raised when the user clicks the block header.</summary>
    public event Action<ChannelViewModel>? SelectRequested;

    public void RequestSelect() => SelectRequested?.Invoke(this);

    partial void OnIsEnabledChanged(bool value) => enabledChanged(this);
}