using Avalonia.Controls;
using Avalonia.Interactivity;
using Oscilloscope.App.ViewModels;

namespace Oscilloscope.App.Views;

public partial class ChannelAdvanced : UserControl
{
    public ChannelAdvanced()
    {
        InitializeComponent();
    }

    private void OnCalibrationClick(object? sender, RoutedEventArgs e) =>
        (DataContext as ChannelViewModel)?.RequestCalibration();

    private void OnVoltageCalibrationClick(object? sender, RoutedEventArgs e) =>
        (DataContext as ChannelViewModel)?.RequestVoltageCalibration();
}