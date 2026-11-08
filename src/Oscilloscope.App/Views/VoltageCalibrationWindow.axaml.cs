using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Oscilloscope.App.ViewModels;
using Oscilloscope.Core.Calibration;

namespace Oscilloscope.App.Views;

public partial class VoltageCalibrationWindow : Window
{
    private static readonly ScottPlot.Color ExcludedColor = ScottPlot.Color.FromHex("#6B6B73");

    private VoltageCalibrationViewModel? _viewModel;

    public VoltageCalibrationWindow()
    {
        InitializeComponent();

        var plot = Plot.Plot;
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#1B1B1F");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#101014");
        plot.Axes.Color(ScottPlot.Color.FromHex("#D0D0D0"));
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#2E2E36");
        plot.XLabel("Input voltage (V)");
        plot.YLabel("Mean ADC code");

        Redraw();

        DataContextChanged += (_, _) => Attach(DataContext as VoltageCalibrationViewModel);
        Closed += (_, _) => Attach(null);
    }

    private void Attach(VoltageCalibrationViewModel? viewModel)
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

        var points = _viewModel?.Points ?? Array.Empty<VoltagePoint>();
        var fits = _viewModel?.Fits ?? Array.Empty<GainFit>();
        var palette = VoltageCalibrationViewModel.Palette;

        // The same order as the rows of the fit table, so the colors match.
        var words = points.Select(p => p.Point.GainWord).Distinct().OrderBy(w => w).ToList();

        for (int i = 0; i < words.Count; i++)
        {
            uint word = words[i];
            var color = ScottPlot.Color.FromHex(palette[i % palette.Length]);

            var used = points.Where(p => p.Point.GainWord == word && !p.Point.Clipped).OrderBy(p => p.Volts).ToList();
            var excluded = points.Where(p => p.Point.GainWord == word && p.Point.Clipped).OrderBy(p => p.Volts).ToList();

            AddMarkers(excluded, ExcludedColor);
            AddMarkers(used, color);

            var fit = fits.FirstOrDefault(f => f.GainWord == word && f.IsValid);
            if (fit is not null)
            {
                var line = plot.Add.Line(
                    fit.MinVolts, fit.Slope * fit.MinVolts + fit.Intercept,
                    fit.MaxVolts, fit.Slope * fit.MaxVolts + fit.Intercept);
                line.Color = color;
                line.LineWidth = 1.5f;
            }
        }

        if (points.Count == 0)
        {
            plot.Axes.SetLimits(-5, 5, 0, 255);
        }
        else
        {
            plot.Axes.AutoScale();
            plot.Axes.SetLimitsY(0, 255);
        }

        Plot.Refresh();
    }

    private void AddMarkers(System.Collections.Generic.List<VoltagePoint> points, ScottPlot.Color color)
    {
        if (points.Count == 0)
            return;

        double[] xs = points.Select(p => p.Volts).ToArray();
        double[] ys = points.Select(p => p.Point.Mean).ToArray();

        var scatter = Plot.Plot.Add.Scatter(xs, ys);
        scatter.Color = color;
        scatter.LineWidth = 0;
        scatter.MarkerSize = 6;
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { HasResults: true } vm)
            return;

        try
        {
            string name = $"voltage_calibration_ch{vm.ChannelNumber}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            string? saved = await FileDialogs.SaveTextAsync(this, "Save calibration points", name, vm.ExportPointsCsv());
            if (saved is not null)
                vm.Status = $"Saved {saved}.";
        }
        catch (Exception ex)
        {
            vm.Status = $"Save failed: {ex.Message}";
        }
    }
}
