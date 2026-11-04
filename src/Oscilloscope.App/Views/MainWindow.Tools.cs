using System;
using System.IO;
using Avalonia.Interactivity;
using Oscilloscope.App.ViewModels;
using Oscilloscope.Core.Export;
using Oscilloscope.Core.Protocol;

namespace Oscilloscope.App.Views;

/// <summary>Saving the visible samples and the calibration window, kept apart from the plot code.</summary>
public partial class MainWindow
{
    private MainViewModel? _toolsViewModel;
    private CalibrationWindow? _calibrationWindow;
    private AcquisitionRecord? _exportRecord;
    private double _exportPeriodSeconds;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_toolsViewModel is not null)
        {
            _toolsViewModel.RecordReceived -= OnRecordForExport;
            _toolsViewModel.Channel1.CalibrationRequested -= OnCalibrationRequested;
            _toolsViewModel.Channel2.CalibrationRequested -= OnCalibrationRequested;
        }

        _toolsViewModel = DataContext as MainViewModel;

        if (_toolsViewModel is not null)
        {
            _toolsViewModel.RecordReceived += OnRecordForExport;
            _toolsViewModel.Channel1.CalibrationRequested += OnCalibrationRequested;
            _toolsViewModel.Channel2.CalibrationRequested += OnCalibrationRequested;
        }
    }

    private void OnRecordForExport(AcquisitionRecord record, double samplePeriod)
    {
        _exportRecord = record;
        _exportPeriodSeconds = samplePeriod;
        SaveButton.IsEnabled = true;
    }

    /// <summary>Saves the part of the last record that is visible on the plot (zoom and pan are respected).</summary>
    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_exportRecord is not { } record || _toolsViewModel is not { } vm)
            return;

        try
        {
            // The X axis is in nanoseconds, from the first sample of the record.
            double periodNs = _exportPeriodSeconds * 1e9;
            var limits = Plot.Plot.Axes.GetLimits();
            int first = (int)Math.Max(Math.Ceiling(limits.Left / periodNs), 0);
            int last = (int)Math.Min(Math.Floor(limits.Right / periodNs), record.Header.SamplesPerChannel - 1.0);
            last = Math.Max(last, first);

            using var writer = new StringWriter();
            WaveformCsv.Write(writer, record, _exportPeriodSeconds, first, last);

            string name = $"waveform_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            string? saved = await FileDialogs.SaveTextAsync(this, "Save visible samples", name, writer.ToString());
            if (saved is not null)
                vm.Status = $"Saved {last - first + 1} samples to {saved}.";
        }
        catch (Exception ex)
        {
            vm.Status = $"Save failed: {ex.Message}";
        }
    }

    private void OnCalibrationRequested(ChannelViewModel channel)
    {
        if (_toolsViewModel is not { } main)
            return;

        if (_calibrationWindow is not null)
        {
            if (_calibrationWindow.DataContext is CalibrationViewModel { IsSweeping: false } open)
                open.ChannelIndex = channel.Number - 1;

            _calibrationWindow.Activate();
            return;
        }

        var viewModel = new CalibrationViewModel(main, channel.Number);
        _calibrationWindow = new CalibrationWindow { DataContext = viewModel };
        _calibrationWindow.Closed += (_, _) =>
        {
            viewModel.Dispose();
            _calibrationWindow = null;
        };
        _calibrationWindow.Show(this);
    }
}
