using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Oscilloscope.App.ViewModels;
using Oscilloscope.Core.Protocol;

namespace Oscilloscope.App.Views;

public partial class MainWindow : Window
{
    private static readonly ScottPlot.Color Channel1Color = ScottPlot.Color.FromHex("#FFB000");
    private static readonly ScottPlot.Color Channel2Color = ScottPlot.Color.FromHex("#4FC3F7");
    private static readonly ScottPlot.Color TriggerColor = ScottPlot.Color.FromHex("#E5484D");
    private static readonly ScottPlot.Color PretriggerColor = ScottPlot.Color.FromHex("#8E8E99");

    /// <summary>How close (in DIPs) the pointer must be to a marker line to grab it.</summary>
    private const double GrabTolerance = 6;

    private static readonly Cursor TriggerCursor = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor PretriggerCursor = new(StandardCursorType.SizeWestEast);

    private enum DragTarget { None, TriggerLevel, Pretrigger }

    private MainViewModel? _viewModel;
    private uint _lastSamples;

    private ScottPlot.Plottables.HorizontalLine? _triggerLine;
    private ScottPlot.Plottables.VerticalLine? _pretriggerLine;
    private double? _triggerLevelShown;
    private double? _pretriggerShown;
    private DragTarget _drag = DragTarget.None;

    public MainWindow()
    {
        InitializeComponent();

        ApplyDarkTheme();
        ApplyLimits(sampleCount: 0);
        Plot.Refresh();

        // Tunnel routing: we see the events before ScottPlot does and can stop its panning.
        Plot.AddHandler(PointerPressedEvent, OnPlotPointerPressed, RoutingStrategies.Tunnel);
        Plot.AddHandler(PointerMovedEvent, OnPlotPointerMoved, RoutingStrategies.Tunnel);
        Plot.AddHandler(PointerReleasedEvent, OnPlotPointerReleased, RoutingStrategies.Tunnel);
        Plot.AddHandler(PointerCaptureLostEvent, OnPlotCaptureLost, RoutingStrategies.Direct);

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

        // Do not fight the user's hand while a line is being dragged.
        if (_drag != DragTarget.None)
            return;

        UpdateMarkers();
        Plot.Refresh();
    }

    // --- Markers -------------------------------------------------------

    private void SetTriggerLine(double? level)
    {
        if (_triggerLine is not null)
        {
            Plot.Plot.Remove(_triggerLine);
            _triggerLine = null;
        }

        _triggerLevelShown = level;
        if (level is not { } value)
            return;

        _triggerLine = Plot.Plot.Add.HorizontalLine(value);
        _triggerLine.LineColor = TriggerColor;
        _triggerLine.LineWidth = 1;
        _triggerLine.LinePattern = ScottPlot.LinePattern.Dashed;
    }

    private void SetPretriggerLine(double? position)
    {
        if (_pretriggerLine is not null)
        {
            Plot.Plot.Remove(_pretriggerLine);
            _pretriggerLine = null;
        }

        _pretriggerShown = position;
        if (position is not { } value)
            return;

        _pretriggerLine = Plot.Plot.Add.VerticalLine(value);
        _pretriggerLine.LineColor = PretriggerColor;
        _pretriggerLine.LineWidth = 1;
        _pretriggerLine.LinePattern = ScottPlot.LinePattern.Dashed;
    }

    /// <summary>
    /// Draws both marker lines from the applied values. The line that is being dragged
    /// keeps the position of the hand instead.
    /// </summary>
    private void UpdateMarkers()
    {
        double? level = _drag == DragTarget.TriggerLevel
            ? _triggerLevelShown
            : _viewModel?.AppliedTriggerLevel;

        double? pretrigger = _drag == DragTarget.Pretrigger
            ? _pretriggerShown
            : _viewModel?.AppliedPretrigger;

        SetTriggerLine(level);
        SetPretriggerLine(pretrigger);
    }

    // --- Dragging the marker lines ------------------------------------

    /// <summary>Pointer position in ScottPlot pixels (physical, so scaled by the display scale).</summary>
    private ScottPlot.Pixel ToPixel(PointerEventArgs e)
    {
        var p = e.GetPosition(Plot);
        double scale = Plot.DisplayScale;
        return new ScottPlot.Pixel((float)(p.X * scale), (float)(p.Y * scale));
    }

    /// <summary>Which line (if any) is under the pointer. The nearer one wins when both are in reach.</summary>
    private DragTarget HitTest(ScottPlot.Pixel pixel)
    {
        if (_viewModel is not { CanEditSettings: true })
            return DragTarget.None;

        double tolerance = GrabTolerance * Plot.DisplayScale;
        double bestDistance = double.MaxValue;
        var best = DragTarget.None;

        if (_triggerLevelShown is { } level)
        {
            float lineY = Plot.Plot.GetPixel(new ScottPlot.Coordinates(0, level)).Y;
            double distance = Math.Abs(pixel.Y - lineY);
            if (distance <= tolerance && distance < bestDistance)
            {
                bestDistance = distance;
                best = DragTarget.TriggerLevel;
            }
        }

        if (_pretriggerShown is { } position)
        {
            float lineX = Plot.Plot.GetPixel(new ScottPlot.Coordinates(position, 0)).X;
            double distance = Math.Abs(pixel.X - lineX);
            if (distance <= tolerance && distance < bestDistance)
            {
                best = DragTarget.Pretrigger;
            }
        }

        return best;
    }

    private void OnPlotPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Plot).Properties.IsLeftButtonPressed)
            return;

        var target = HitTest(ToPixel(e));
        if (target == DragTarget.None)
            return;

        _drag = target;
        e.Pointer.Capture(Plot);
        e.Handled = true; // ScottPlot does not start panning
    }

    private void OnPlotPointerMoved(object? sender, PointerEventArgs e)
    {
        var pixel = ToPixel(e);

        if (_drag == DragTarget.None)
        {
            Plot.Cursor = HitTest(pixel) switch
            {
                DragTarget.TriggerLevel => TriggerCursor,
                DragTarget.Pretrigger => PretriggerCursor,
                _ => Cursor.Default,
            };
            return;
        }

        var coordinates = Plot.Plot.GetCoordinates(pixel);

        if (_drag == DragTarget.TriggerLevel)
        {
            double level = Math.Round(Math.Clamp(coordinates.Y, 0, 255));
            SetTriggerLine(level);
            if (_viewModel is not null)
                _viewModel.TriggerLevel = (decimal)level; // the numeric field follows live
        }
        else
        {
            double max = _viewModel is null ? 0 : (double)_viewModel.MaxPretrigger;
            double position = Math.Round(Math.Clamp(coordinates.X, 0, max));
            SetPretriggerLine(position);
            if (_viewModel is not null)
                _viewModel.Pretrigger = (decimal)position; // the numeric field follows live
        }

        Plot.Refresh();
        e.Handled = true;
    }

    private async void OnPlotPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_drag == DragTarget.None)
            return;

        // Release the drag state first, so markers can be redrawn from the applied values.
        _drag = DragTarget.None;
        e.Pointer.Capture(null);
        e.Handled = true;

        // Same path as the Apply button (queued if an acquisition is running).
        if (_viewModel is not null)
            await _viewModel.ApplyTriggerCommand.ExecuteAsync(null);
    }

    private void OnPlotCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_drag == DragTarget.None)
            return;

        // Capture was taken away mid-drag: drop the gesture and show the applied values again.
        _drag = DragTarget.None;
        UpdateMarkers();
        Plot.Refresh();
    }

    // --- Records -------------------------------------------------------

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