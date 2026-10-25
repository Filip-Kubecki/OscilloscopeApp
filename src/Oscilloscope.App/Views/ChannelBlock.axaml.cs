using Avalonia.Controls;
using Avalonia.Input;
using Oscilloscope.App.ViewModels;

namespace Oscilloscope.App.Views;

public partial class ChannelBlock : UserControl
{
    public ChannelBlock()
    {
        InitializeComponent();
    }

    private void OnHeaderTapped(object? sender, TappedEventArgs e) =>
        (DataContext as ChannelViewModel)?.RequestSelect();
}
