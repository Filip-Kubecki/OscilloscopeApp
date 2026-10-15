using System;
using Avalonia.Controls;
using Oscilloscope.App.ViewModels;
using Oscilloscope.Core.Protocol;

namespace Oscilloscope.App.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private uint _lastSamples;

    public MainWindow()
    {
        InitializeComponent();

        Plot.Plot.YLabel("Kod ADC");
        Plot.Plot.XLabel("Próbka");
        Plot.Plot.Axes.SetLimitsY(0, 255);
        Plot.Refresh();

        DataContextChanged += (_, _) => AttachViewModel(DataContext as MainViewModel);
        Closed += (_, _) => _viewModel?.Dispose();
    }

    private void AttachViewModel(MainViewModel? viewModel)
    {
        if (_viewModel is not null)
            _viewModel.RecordReceived -= OnRecordReceived;

        _viewModel = viewModel;

        if (_viewModel is not null)
            _viewModel.RecordReceived += OnRecordReceived;
    }

    private void OnRecordReceived(AcquisitionRecord record)
    {
        Plot.Plot.Clear();

        for (int channel = 1; channel <= 2; channel++)
        {
            if ((record.Header.ChannelMask & (1 << (channel - 1))) == 0)
                continue;

            double[] ys = Array.ConvertAll(record.ExtractChannel(channel), b => (double)b);
            var signal = Plot.Plot.Add.Signal(ys);
            signal.Color = channel == 1 ? ScottPlot.Colors.Orange : ScottPlot.Colors.Blue;
        }

        // Zakres X ustawiamy tylko przy zmianie długości rekordu,
        // żeby nie kasować zoomu użytkownika w trybie Run.
        if (record.Header.SamplesPerChannel != _lastSamples)
        {
            _lastSamples = record.Header.SamplesPerChannel;
            Plot.Plot.Axes.SetLimitsX(0, _lastSamples);
        }

        Plot.Refresh();
    }
}
