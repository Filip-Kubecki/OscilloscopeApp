using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Oscilloscope.App.ViewModels;

namespace Oscilloscope.App.Views;

public partial class ChannelBlock : UserControl
{
    public ChannelBlock()
    {
        InitializeComponent();
    }

    private void OnTapped(object? sender, TappedEventArgs e) =>
        (DataContext as ChannelViewModel)?.RequestSelect();

    private void OnAdvancedClick(object? sender, RoutedEventArgs e) =>
        (DataContext as ChannelViewModel)?.RequestAdvanced();
}