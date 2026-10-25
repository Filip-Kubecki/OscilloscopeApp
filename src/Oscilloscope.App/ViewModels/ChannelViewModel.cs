using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Oscilloscope.App.ViewModels;

/// <summary>
/// One analog channel in the side panel. For now only "enabled" is wired to the device;
/// gain, offset and the status values are placeholders in the view.
/// </summary>
public partial class ChannelViewModel(
    int number, bool enabled, string colorHex, Action<ChannelViewModel> enabledChanged)
    : ViewModelBase
{
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

    /// <summary>Raised when the user clicks the block header.</summary>
    public event Action<ChannelViewModel>? SelectRequested;

    public void RequestSelect() => SelectRequested?.Invoke(this);

    partial void OnIsEnabledChanged(bool value) => enabledChanged(this);
}
