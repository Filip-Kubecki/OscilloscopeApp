using System;
using Avalonia.Controls;
using Oscilloscope.App.ViewModels;
using Oscilloscope.Core.Protocol;

namespace Oscilloscope.App.Views;

public partial class MainWindow : Window
{
    private static readonly ScottPlot.Color Channel1Color = ScottPlot.Color.FromHex("#FFB000");
    private static readonly ScottPlot.Color Channel2Color = ScottPlot.Color.FromHex("#4FC3F7");

    private MainViewModel? _viewModel;
    private uint _lastSamples;

    public MainWindow()
    {
        InitializeComponent();

        ApplyDarkTheme();
        ApplyLimits(sampleCount: 0);
        Plot.Refresh();

        DataContextChanged += (_, _) => AttachViewModel(DataContext as MainViewModel);
        Closed += (_, _) => _viewModel?.Dispose();
    }

    private void ApplyDarkTheme()
    {
        var plot = Plot.Plot;
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#2a2a2a");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#000000");
        plot.Axes.Color(ScottPlot.Color.FromHex("#D0D0D0"));
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#2E2E36");
    }

    /// <summary>
    /// Ustawia widok na cały rekord i blokuje zoom oraz przesuwanie poza zakres próbek
    /// (X: 0..N-1, Y: 0..255). Wywoływane tylko przy zmianie długości rekordu,
    /// żeby w trybie Run nie kasować zoomu użytkownika.
    /// </summary>
    private void ApplyLimits(uint sampleCount)
    {
        double right = sampleCount > 1 ? sampleCount - 1 : 1;
        var limits = new ScottPlot.AxisLimits(left: 0, right: right, bottom: 0, top: 255);

        var axes = Plot.Plot.Axes;
        axes.Rules.Clear();
        axes.Rules.Add(new ScottPlot.AxisRules.MaximumBoundary(axes.Bottom, axes.Left, limits));
        axes.SetLimits(limits);
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
            signal.Color = channel == 1 ? Channel1Color : Channel2Color;
        }

        if (record.Header.SamplesPerChannel != _lastSamples)
        {
            _lastSamples = record.Header.SamplesPerChannel;
            ApplyLimits(_lastSamples);
        }

        Plot.Refresh();
    }
}