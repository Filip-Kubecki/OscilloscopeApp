using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Oscilloscope.App.ViewModels;
using Oscilloscope.Core.Device;

namespace Oscilloscope.App.Views;

public partial class CalibrationWindow : Window
{
    private static readonly ScottPlot.Color LowColor = ScottPlot.Color.FromHex("#8BD17C");
    private static readonly ScottPlot.Color HighColor = ScottPlot.Color.FromHex("#C792EA");
    private static readonly ScottPlot.Color DividerColor = ScottPlot.Color.FromHex("#8E8E99");

    private CalibrationViewModel? _viewModel;

    public CalibrationWindow()
    {
        InitializeComponent();

        var plot = Plot.Plot;
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#1B1B1F");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#101014");
        plot.Axes.Color(ScottPlot.Color.FromHex("#D0D0D0"));
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#2E2E36");
        plot.XLabel("Gain word");
        plot.YLabel("Mean ADC code");

        Redraw();

        DataContextChanged += (_, _) => Attach(DataContext as CalibrationViewModel);
        Closed += (_, _) => Attach(null);
    }

    private void Attach(CalibrationViewModel? viewModel)
    {
        if (_viewModel is not null)
            _viewModel.ResultsChanged -= Redraw;

        _viewModel = viewModel;

        if (_viewModel is not null)
        {
            _viewModel.ResultsChanged += Redraw;
            Redraw();
        }
    }

    private void Redraw()
    {
        var plot = Plot.Plot;
        plot.Clear();

        var points = _viewModel?.Points.ToList() ?? new List<CalibrationPoint>();
        AddSeries(points.Where(p => !p.IsHighRange), LowColor);
        AddSeries(points.Where(p => p.IsHighRange), HighColor);

        // The boundary between the two gain ranges.
        var divider = plot.Add.VerticalLine(127.5);
        divider.LineColor = DividerColor;
        divider.LineWidth = 1;
        divider.LinePattern = ScottPlot.LinePattern.Dashed;

        if (points.Count == 0)
        {
            plot.Axes.SetLimits(0, 256, 0, 255);
        }
        else
        {
            double min = points.Min(p => p.Mean);
            double max = points.Max(p => p.Mean);
            double margin = Math.Max(1, (max - min) * 0.1);
            plot.Axes.SetLimits(0, 256, min - margin, max + margin);
        }

        Plot.Refresh();
    }

    private void AddSeries(IEnumerable<CalibrationPoint> points, ScottPlot.Color color)
    {
        var list = points.ToList();
        if (list.Count == 0)
            return;

        double[] xs = list.Select(p => (double)p.GainWord).ToArray();
        double[] ys = list.Select(p => p.Mean).ToArray();

        var scatter = Plot.Plot.Add.Scatter(xs, ys);
        scatter.Color = color;
        scatter.LineWidth = 1.5f;
        scatter.MarkerSize = 4;
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { HasResults: true } vm)
            return;

        try
        {
            string name = $"calibration_ch{vm.ChannelNumber}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            string? saved = await FileDialogs.SaveTextAsync(this, "Save calibration results", name, vm.ExportCsv());
            if (saved is not null)
                vm.Status = $"Saved {saved}.";
        }
        catch (Exception ex)
        {
            vm.Status = $"Save failed: {ex.Message}";
        }
    }
}
