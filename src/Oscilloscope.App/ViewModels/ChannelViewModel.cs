using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Oscilloscope.App.ViewModels;

/// <summary>
/// One analog channel in the side panel. For now only "enabled" is wired to the device;
/// gain, offset and the status values are placeholders in the view.
/// </summary>
public partial class ChannelViewModel(int number, bool enabled, Action<ChannelViewModel> enabledChanged)
    : ViewModelBase
{
    public int Number { get; } = number;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = enabled;

    partial void OnIsEnabledChanged(bool value) => enabledChanged(this);
}
