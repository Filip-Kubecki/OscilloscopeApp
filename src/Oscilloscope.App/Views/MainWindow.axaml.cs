using System;
using System.ComponentModel;
using Avalonia.Controls;
using Oscilloscope.App.ViewModels;
using Oscilloscope.Core.Protocol;

namespace Oscilloscope.App.Views;

public partial class MainWindow : Window
{
    private static readonly ScottPlot.Color Channel1Color = ScottPlot.Color.FromHex("#FFB000");
    private static readonly ScottPlot.Color Channel2Color = ScottPlot.Color.FromHex("#4FC3F7");
    private static readonly ScottPlot.Color TriggerColor = ScottPlot.Color.FromHex("#E5484D");
    private static readonly ScottPlot.Color PretriggerColor = ScottPlot.Color.FromHex("#8E8E99");

    private MainViewModel? _viewModel;
    private uint _lastSamples;
    private ScottPlot.Plottables.HorizontalLine? _triggerLine;
    private ScottPlot.Plottables.VerticalLine? _pretriggerLine;

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
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#1B1B1F");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#101014");
        plot.Axes.Color(ScottPlot.Color.FromHex("#D0D0D0"));
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#2E2E36");
    }

    /// <summary>
    /// Sets the view to the whole record and restricts zoom and pan to the sample range
    /// (X: 0..N-1, Y: 0..255). Called only when the record length changes,
    /// so the user's zoom is kept in Run mode.
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
        {
            _viewModel.RecordReceived -= OnRecordReceived;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;

        if (_viewModel is not null)
        {
            _viewModel.RecordReceived += OnRecordReceived;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainViewModel.AppliedTriggerLevel)
                                   or nameof(MainViewModel.AppliedPretrigger)))
            return;

        UpdateMarkers();
        Plot.Refresh();
    }

    /// <summary>Draws (or removes) the trigger level line and the pretrigger line.</summary>
    private void UpdateMarkers()
    {
        if (_triggerLine is not null)
        {
            Plot.Plot.Remove(_triggerLine);
            _triggerLine = null;
        }
        if (_pretriggerLine is not null)
        {
            Plot.Plot.Remove(_pretriggerLine);
            _pretriggerLine = null;
        }

        if (_viewModel?.AppliedTriggerLevel is { } level)
        {
            _triggerLine = Plot.Plot.Add.HorizontalLine(level);
            _triggerLine.LineColor = TriggerColor;
            _triggerLine.LineWidth = 1;
            _triggerLine.LinePattern = ScottPlot.LinePattern.Dashed;
        }

        if (_viewModel?.AppliedPretrigger is { } pretrigger)
        {
            _pretriggerLine = Plot.Plot.Add.VerticalLine(pretrigger);
            _pretriggerLine.LineColor = PretriggerColor;
            _pretriggerLine.LineWidth = 1;
            _pretriggerLine.LinePattern = ScottPlot.LinePattern.Dashed;
        }
    }

    private void OnRecordReceived(AcquisitionRecord record)
    {
        Plot.Plot.Clear();
        _triggerLine = null;
        _pretriggerLine = null;

        for (int channel = 1; channel <= 2; channel++)
        {
            if ((record.Header.ChannelMask & (1 << (channel - 1))) == 0)
                continue;

            double[] ys = Array.ConvertAll(record.ExtractChannel(channel), b => (double)b);
            var signal = Plot.Plot.Add.Signal(ys);
            signal.Color = channel == 1 ? Channel1Color : Channel2Color;
        }

        UpdateMarkers();

        if (record.Header.SamplesPerChannel != _lastSamples)
        {
            _lastSamples = record.Header.SamplesPerChannel;
            ApplyLimits(_lastSamples);
        }

        Plot.Refresh();
    }
}