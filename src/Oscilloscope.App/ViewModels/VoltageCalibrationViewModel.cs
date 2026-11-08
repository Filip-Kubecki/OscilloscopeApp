using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oscilloscope.Core.Calibration;
using Oscilloscope.Core.Device;
using Oscilloscope.Core.Export;
using Oscilloscope.Core.Protocol;

namespace Oscilloscope.App.ViewModels;

/// <summary>A gain that can be ticked for the measurement.</summary>
public partial class PresetChoice(GainPreset preset) : ObservableObject
{
    public uint Word { get; } = preset.Word;
    public string Label { get; } = preset.Label;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = true;
}

/// <summary>One measured point in the list.</summary>
public sealed class PointRow(VoltagePoint point)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public VoltagePoint Point { get; } = point;

    public string VoltsText => Point.Volts.ToString("0.####", Inv);
    public string WordText => Point.Point.GainWord.ToString(Inv);
    public string MeanText => Point.Point.Mean.ToString("0.00", Inv);
    public string StdDevText => Point.Point.StdDev.ToString("0.00", Inv);
    public string MinMaxText => $"{Point.Point.Min}-{Point.Point.Max}";
    public string FlagText => Point.Point.Clipped ? "limit" : "";
}

/// <summary>One line of the fit table.</summary>
public sealed class FitRow(GainFit fit, string gainText, double nominalSlope, string colorHex)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public GainFit Fit { get; } = fit;
    public string GainText { get; } = gainText;
    public IBrush Swatch { get; } = Brush.Parse(colorHex);

    public string WordText => Fit.GainWord.ToString(Inv);
    public string SlopeText => Fit.IsValid ? Fit.Slope.ToString("0.00", Inv) : "-";
    public string OffsetText => Fit.IsValid ? Fit.Intercept.ToString("0.00", Inv) : "-";
    public string LsbText => Fit.IsValid ? Fit.LsbMillivolts.ToString("0.00", Inv) : "-";
    public string RmsText => Fit.IsValid ? Fit.RmsResidual.ToString("0.00", Inv) : "-";
    public string CountText => $"{Fit.UsedPoints}/{Fit.TotalPoints}";

    /// <summary>Measured slope divided by the nominal one. Should be about the same for every gain.</summary>
    public string KText => Fit.IsValid && nominalSlope != 0
        ? (Fit.Slope / nominalSlope).ToString("0.000", Inv)
        : "-";

    /// <summary>Input voltages for which the ADC reads 0 and 255.</summary>
    public string RangeText
    {
        get
        {
            if (!Fit.IsValid)
                return "-";

            double a = Fit.VoltsAtCode0, b = Fit.VoltsAtCode255;
            return FormattableString.Invariant($"{Math.Min(a, b):0.###} .. {Math.Max(a, b):0.###}");
        }
    }

    public string FlagText => Fit.IsValid ? "" : "need 2+ usable points";
}

/// <summary>
/// Voltage calibration: DC voltages are put on the input one after another (by hand) and, at each of them,
/// the mean ADC code is measured at the ticked gains. A line is fitted per gain: code = slope * volts + offset.
/// </summary>
public partial class VoltageCalibrationViewModel : ViewModelBase, IDisposable
{
    /// <summary>Colors of the gains on the plot and in the fit table.</summary>
    public static readonly string[] Palette =
    {
        "#8BD17C", "#C792EA", "#FFB000", "#4FC3F7", "#F78C6C", "#FF5370", "#89DDFF", "#FFCB6B",
    };

    // Used only for the "k" column (measured slope / nominal slope).
    private const double InputAttenuationDb = 26;
    private const double AdcVoltsPerCode = 1.024 / 256;

    private readonly MainViewModel _main;
    private readonly List<VoltagePoint> _points = new();
    private readonly Stack<int> _batches = new(); // number of points added by each Measure
    private CancellationTokenSource? _stopCts;

    // Settings of the run the stored points come from (for the CSV header).
    private int _resultsChannel;
    private uint? _resultsOffsetCode;
    private uint _resultsSamples = 8192;
    private int _resultsRecords = 4;

    public ObservableCollection<PresetChoice> Presets { get; }
    public ObservableCollection<PointRow> PointRows { get; } = new();
    public ObservableCollection<FitRow> FitRows { get; } = new();

    public IReadOnlyList<VoltagePoint> Points => _points;
    public IReadOnlyList<GainFit> Fits { get; private set; } = Array.Empty<GainFit>();

    public bool HasResults => _points.Count > 0;
    public bool HasFits => Fits.Any(f => f.IsValid);

    /// <summary>Raised after the points or the fits changed. The window redraws the plot.</summary>
    public event Action? ResultsChanged;

    public VoltageCalibrationViewModel(MainViewModel main, int channelNumber)
    {
        _main = main;
        ChannelIndex = channelNumber == 2 ? 1 : 0;
        Presets = new ObservableCollection<PresetChoice>(GainPresets.All.Select(p => new PresetChoice(p)));
        _main.PropertyChanged += OnMainPropertyChanged;
    }

    /// <summary>0 = channel 1, 1 = channel 2.</summary>
    [ObservableProperty]
    public partial int ChannelIndex { get; set; }

    public int ChannelNumber => ChannelIndex + 1;

    /// <summary>DC voltage on the input for the next Measure, in volts. Negative values are allowed.</summary>
    [ObservableProperty]
    public partial decimal Voltage { get; set; }

    [ObservableProperty]
    public partial decimal SamplesPerPoint { get; set; } = 8192;

    [ObservableProperty]
    public partial decimal RecordsPerPoint { get; set; } = 4;

    /// <summary>Pause after the new gain is applied, before measuring (ms).</summary>
    [ObservableProperty]
    public partial decimal SettleMs { get; set; } = 100;

    /// <summary>Records acquired and discarded after the pause, before measuring.</summary>
    [ObservableProperty]
    public partial decimal DiscardRecords { get; set; } = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMeasure))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyPropertyChangedFor(nameof(CanUndo))]
    [NotifyPropertyChangedFor(nameof(StartBlockedText))]
    public partial bool IsMeasuring { get; set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = "";

    [ObservableProperty]
    public partial string Status { get; set; } = "Ready.";

    public bool CanEdit => !IsMeasuring;

    public bool CanMeasure => !IsMeasuring && _main.IsConnected && !_main.IsRunning && !_main.IsBusy;

    public bool CanUndo => !IsMeasuring && _batches.Count > 0;

    public string StartBlockedText =>
        IsMeasuring ? "" :
        !_main.IsConnected ? "Not connected." :
        _main.IsRunning ? "Stop the acquisition in the main window first." :
        _main.IsBusy ? "The device is busy." : "";

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsConnected) or nameof(MainViewModel.IsRunning)
            or nameof(MainViewModel.IsBusy) or nameof(MainViewModel.CanAcquire))
        {
            OnPropertyChanged(nameof(CanMeasure));
            OnPropertyChanged(nameof(StartBlockedText));
        }
    }

    // --- Commands ------------------------------------------------------

    [RelayCommand]
    private async Task MeasureAsync()
    {
        if (!CanMeasure || _main.Device is not { } device)
            return;

        var words = Presets.Where(p => p.IsChecked).Select(p => p.Word).OrderBy(w => w).ToList();
        if (words.Count == 0)
        {
            Status = "Tick at least one gain.";
            return;
        }

        int channelNumber = ChannelNumber;

        // Points of another channel do not belong in the same fit.
        if (_points.Count > 0 && _resultsChannel != channelNumber)
            ClearResults();

        double volts = (double)Math.Clamp(Voltage, -100m, 100m);
        var batch = new List<VoltagePoint>();
        int done = 0;

        var progress = new DirectProgress<CalibrationPoint>(point =>
        {
            var vp = new VoltagePoint(volts, point);
            batch.Add(vp);
            AddPoint(vp);

            done++;
            ProgressPercent = 100.0 * done / words.Count;
            ProgressText = $"{done} / {words.Count}  (word {point.GainWord})";
        });

        IsMeasuring = true;
        _main.IsBusy = true;
        _stopCts = new CancellationTokenSource();
        ProgressPercent = 0;
        ProgressText = "";
        Status = "Measuring ...";

        try
        {
            // The offset DAC shifts every fitted line, so it must not change between the points.
            byte offsetRegister = channelNumber == 1 ? Registers.Channel1Offset : Registers.Channel2Offset;
            uint offsetCode = await device.ReadRegisterAsync(offsetRegister);

            if (_points.Count > 0 && _resultsOffsetCode != offsetCode)
            {
                Status = $"The DAC offset changed ({_resultsOffsetCode} -> {offsetCode}) since the first point. " +
                         "Restore it or clear the results.";
                return;
            }

            if (_points.Count == 0)
            {
                _resultsChannel = channelNumber;
                _resultsOffsetCode = offsetCode;
                _resultsSamples = (uint)Math.Clamp(SamplesPerPoint, 1m, ProtocolConstants.MaxSamplesSingleChannel);
                _resultsRecords = (int)Math.Clamp(RecordsPerPoint, 1m, 100m);
            }

            var options = new GainSweepOptions(
                channelNumber, words,
                (uint)Math.Clamp(SamplesPerPoint, 1m, ProtocolConstants.MaxSamplesSingleChannel),
                (int)Math.Clamp(RecordsPerPoint, 1m, 100m),
                SettleMs: (int)Math.Clamp(SettleMs, 0m, 10000m),
                DiscardRecords: (int)Math.Clamp(DiscardRecords, 0m, 10m));

            await GainSweep.RunAsync(device, options, progress, _stopCts.Token);

            string volt = volts.ToString("0.####", CultureInfo.InvariantCulture);
            Status = _stopCts.IsCancellationRequested
                ? $"Stopped after {batch.Count} gains at {volt} V. Settings restored."
                : $"Measured {batch.Count} gains at {volt} V. Settings restored. Set the next voltage.";
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
            _main.ReportDeviceError(ex);
        }
        finally
        {
            if (batch.Count > 0)
                _batches.Push(batch.Count);

            IsMeasuring = false;
            _main.IsBusy = false;
            _stopCts?.Dispose();
            _stopCts = null;
            OnPropertyChanged(nameof(CanUndo));
        }
    }

    [RelayCommand]
    private void Stop() => _stopCts?.Cancel();

    /// <summary>Removes the points of the last Measure (e.g. when the voltage was typed wrongly).</summary>
    [RelayCommand]
    private void UndoLast()
    {
        if (IsMeasuring || _batches.Count == 0)
            return;

        int count = _batches.Pop();
        _points.RemoveRange(_points.Count - count, count);
        for (int i = 0; i < count; i++)
            PointRows.RemoveAt(PointRows.Count - 1);

        if (_points.Count == 0)
            _resultsOffsetCode = null;

        RecomputeFits();
        Status = "Last measurement removed.";
    }

    [RelayCommand]
    private void Clear()
    {
        if (IsMeasuring)
            return;

        ClearResults();
        Status = "Results cleared.";
    }

    /// <summary>Writes the fitted lines to the calibration file of the application.</summary>
    [RelayCommand]
    private void StoreCalibration()
    {
        var valid = Fits.Where(f => f.IsValid).ToList();
        if (valid.Count == 0)
        {
            Status = "No gain has two usable points yet.";
            return;
        }

        try
        {
            var store = CalibrationStore.Load();
            var now = DateTime.Now;
            foreach (var fit in valid)
                store.Upsert(StoredGainCalibration.From(fit, _resultsOffsetCode, now));

            store.Save();
            Status = $"Stored {valid.Count} gain calibrations of CH{_resultsChannel} in {CalibrationStore.DefaultPath}.";
        }
        catch (Exception ex)
        {
            Status = $"Could not store the calibration: {ex.Message}";
        }
    }

    /// <summary>The points as CSV text. The window asks for a file name and writes it.</summary>
    public string ExportPointsCsv()
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        VoltageCalibrationCsv.Write(
            writer, _points, Fits,
            new CalibrationMeta(_resultsChannel, _resultsOffsetCode, _resultsSamples, _resultsRecords, DateTime.Now));
        return writer.ToString();
    }

    // --- Helpers -------------------------------------------------------

    private void AddPoint(VoltagePoint point)
    {
        _points.Add(point);
        PointRows.Add(new PointRow(point));
        RecomputeFits();
    }

    private void ClearResults()
    {
        _points.Clear();
        _batches.Clear();
        PointRows.Clear();
        _resultsChannel = 0;
        _resultsOffsetCode = null;
        ProgressPercent = 0;
        ProgressText = "";
        RecomputeFits();
    }

    private void RecomputeFits()
    {
        Fits = VoltageFits.Compute(_points);

        FitRows.Clear();
        for (int i = 0; i < Fits.Count; i++)
        {
            var fit = Fits[i];
            FitRows.Add(new FitRow(fit, GainText(fit.GainWord), NominalSlope(fit.GainWord), Palette[i % Palette.Length]));
        }

        OnPropertyChanged(nameof(Fits));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasFits));
        OnPropertyChanged(nameof(CanUndo));
        ResultsChanged?.Invoke();
    }

    private static string GainText(uint word)
    {
        foreach (var preset in GainPresets.All)
        {
            if (preset.Word == word)
                return FormattableString.Invariant($"{preset.Target:0.##} V/V");
        }

        return FormattableString.Invariant($"{Ad8370.NominalVoltageGain(word):0.###} V/V");
    }

    /// <summary>ADC codes per input volt expected from the data sheet gain, the fixed attenuator and the ADC range.</summary>
    private static double NominalSlope(uint word) =>
        Ad8370.NominalVoltageGain(word) * Math.Pow(10, -InputAttenuationDb / 20) / AdcVoltsPerCode;

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
