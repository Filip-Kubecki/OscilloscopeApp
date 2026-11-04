using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oscilloscope.Core.Device;
using Oscilloscope.Core.Export;
using Oscilloscope.Core.Protocol;

namespace Oscilloscope.App.ViewModels;

/// <summary>One line of the results list.</summary>
public sealed class CalibrationRow(CalibrationPoint point)
{
    public CalibrationPoint Point { get; } = point;

    public string WordText => Point.GainWord.ToString(CultureInfo.InvariantCulture);

    public string GainText => double.IsFinite(Point.NominalGainDb)
        ? Point.NominalGainDb.ToString("0.0", CultureInfo.InvariantCulture)
        : "-";

    public string MeanText => Point.Mean.ToString("0.00", CultureInfo.InvariantCulture);
    public string StdDevText => Point.StdDev.ToString("0.00", CultureInfo.InvariantCulture);
    public string MinMaxText => $"{Point.Min}-{Point.Max}";
    public bool IsClipped => Point.Clipped;
    public string FlagText => IsClipped ? "limit" : "";
}

/// <summary>
/// DC offset calibration: with the input shorted, the gain is swept and the mean ADC code is measured
/// at each step. The low (1-127) and high (129-255) ranges are separate sweeps.
/// </summary>
public partial class CalibrationViewModel : ViewModelBase, IDisposable
{
    private readonly MainViewModel _main;
    private readonly SortedList<uint, CalibrationPoint> _points = new();
    private CancellationTokenSource? _stopCts;

    // Settings of the run the stored results come from (for the CSV header).
    private int _resultsChannel;
    private uint? _resultsOffsetCode;
    private uint _resultsSamples = 8192;
    private int _resultsRecords = 1;

    public ObservableCollection<CalibrationRow> Rows { get; } = new();

    /// <summary>Results in the order of the gain word.</summary>
    public IEnumerable<CalibrationPoint> Points => _points.Values;

    public bool HasResults => _points.Count > 0;

    /// <summary>Raised after a point was added or the results were cleared. The window redraws the plot.</summary>
    public event Action? ResultsChanged;

    public CalibrationViewModel(MainViewModel main, int channelNumber)
    {
        _main = main;
        ChannelIndex = channelNumber == 2 ? 1 : 0;
        _main.PropertyChanged += OnMainPropertyChanged;
    }

    /// <summary>0 = channel 1, 1 = channel 2.</summary>
    [ObservableProperty]
    public partial int ChannelIndex { get; set; }

    public int ChannelNumber => ChannelIndex + 1;

    /// <summary>0 = low range, 1 = high range, 2 = both.</summary>
    [ObservableProperty]
    public partial int RangeIndex { get; set; } = 2;

    /// <summary>Gain code step (1 = every code).</summary>
    [ObservableProperty]
    public partial decimal Step { get; set; } = 1;

    /// <summary>Samples averaged per record at each gain.</summary>
    [ObservableProperty]
    public partial decimal SamplesPerPoint { get; set; } = 10000;
    
    /// <summary>Pause after the new gain is applied, before measuring (ms).</summary>
    [ObservableProperty]
    public partial decimal SettleMs { get; set; } = 100;

    /// <summary>Records acquired and discarded after the pause, before measuring.</summary>
    [ObservableProperty]
    public partial decimal DiscardRecords { get; set; } = 1;

    /// <summary>Records averaged at each gain.</summary>
    [ObservableProperty]
    public partial decimal RecordsPerPoint { get; set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyPropertyChangedFor(nameof(StartBlockedText))]
    public partial bool IsSweeping { get; set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    [ObservableProperty]
    public partial string Status { get; set; } = "Ready.";

    public bool CanEdit => !IsSweeping;

    public bool CanStart => !IsSweeping && _main.IsConnected && !_main.IsRunning && !_main.IsBusy;

    public string StartBlockedText =>
        IsSweeping ? "" :
        !_main.IsConnected ? "Not connected." :
        _main.IsRunning ? "Stop the acquisition in the main window first." :
        _main.IsBusy ? "The device is busy." : "";

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsConnected) or nameof(MainViewModel.IsRunning)
            or nameof(MainViewModel.IsBusy) or nameof(MainViewModel.CanAcquire))
        {
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(StartBlockedText));
        }
    }

    // --- Commands ------------------------------------------------------

    [RelayCommand]
    private async Task StartAsync()
    {
        if (!CanStart || _main.Device is not { } device)
            return;

        int channelNumber = ChannelNumber;
        var channel = channelNumber == 1 ? _main.Channel1 : _main.Channel2;

        // Results of another channel do not belong on the same plot.
        if (_resultsChannel != channelNumber)
            ClearResults();

        _resultsChannel = channelNumber;
        _resultsOffsetCode = (uint)Math.Clamp(channel.Offset, 0m, ChannelViewModel.MaxOffsetCode);
        _resultsSamples = (uint)Math.Clamp(SamplesPerPoint, 1m, ProtocolConstants.MaxSamplesSingleChannel);
        _resultsRecords = (int)Math.Clamp(RecordsPerPoint, 1m, 100m);

        var words = BuildWords();
        int done = 0;
        var progress = new DirectProgress<CalibrationPoint>(point =>
        {
            AddPoint(point);
            done++;
            ProgressPercent = 100.0 * done / words.Count;
            ProgressText = $"{done} / {words.Count}  (word {point.GainWord})";
        });

        IsSweeping = true;
        _main.IsBusy = true;
        _stopCts = new CancellationTokenSource();
        ProgressPercent = 0;
        ProgressText = "";
        Status = "Measuring ...";

        try
        {
            var options = new GainSweepOptions(
                channelNumber, words, _resultsSamples, _resultsRecords,
                SettleMs: (int)Math.Clamp(SettleMs, 0m, 10000m),
                DiscardRecords: (int)Math.Clamp(DiscardRecords, 0m, 10m)); 
            await GainSweep.RunAsync(device, options, progress, _stopCts.Token);

            Status = _stopCts.IsCancellationRequested
                ? $"Stopped after {done} points. Settings restored."
                : $"Done: {done} points. Settings restored.";
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
            _main.ReportDeviceError(ex);
        }
        finally
        {
            IsSweeping = false;
            _main.IsBusy = false;
            _stopCts?.Dispose();
            _stopCts = null;
        }
    }

    [RelayCommand]
    private void Stop() => _stopCts?.Cancel();

    [RelayCommand]
    private void Clear()
    {
        if (IsSweeping)
            return;

        ClearResults();
        Status = "Results cleared.";
    }

    /// <summary>The results as CSV text. The window asks for a file name and writes it.</summary>
    public string ExportCsv()
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        CalibrationCsv.Write(
            writer, _points.Values,
            new CalibrationMeta(_resultsChannel, _resultsOffsetCode, _resultsSamples, _resultsRecords, DateTime.Now));
        return writer.ToString();
    }

    // --- Helpers -------------------------------------------------------

    private List<uint> BuildWords()
    {
        uint step = (uint)Math.Clamp(Step, 1m, 127m);
        var words = new List<uint>();

        if (RangeIndex is 0 or 2)
            AddRange(words, baseWord: 0, step);
        if (RangeIndex is 1 or 2)
            AddRange(words, baseWord: 128, step);

        return words;
    }

    /// <summary>Code 0 switches the gain stage off, so a range starts at code 1 and always ends at 127.</summary>
    private static void AddRange(List<uint> words, uint baseWord, uint step)
    {
        uint code = 1;
        for (; code <= 127; code += step)
            words.Add(baseWord + code);

        if (code - step != 127)
            words.Add(baseWord + 127);
    }

    private void AddPoint(CalibrationPoint point)
    {
        // A word measured again replaces the earlier result.
        _points[point.GainWord] = point;
        int index = _points.IndexOfKey(point.GainWord);
        var row = new CalibrationRow(point);

        if (index < Rows.Count && Rows[index].Point.GainWord == point.GainWord)
            Rows[index] = row;
        else
            Rows.Insert(index, row);

        OnPropertyChanged(nameof(HasResults));
        ResultsChanged?.Invoke();
    }

    private void ClearResults()
    {
        _points.Clear();
        Rows.Clear();
        _resultsChannel = 0;
        ProgressPercent = 0;
        ProgressText = "";
        OnPropertyChanged(nameof(HasResults));
        ResultsChanged?.Invoke();
    }

    /// <summary>Runs the handler on the same thread, without posting to a synchronization context.</summary>
    private sealed class DirectProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    public void Dispose()
    {
        _main.PropertyChanged -= OnMainPropertyChanged;
        _stopCts?.Cancel(); // the sweep ends after the current point and restores the configuration
    }
}
