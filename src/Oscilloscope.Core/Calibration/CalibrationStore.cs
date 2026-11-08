using System.Text.Json;

namespace Oscilloscope.Core.Calibration;

/// <summary>Stored calibration of one gain word of one channel. Valid for the DAC offset code it was measured at.</summary>
public sealed class StoredGainCalibration
{
    public int Channel { get; set; }
    public uint GainWord { get; set; }

    /// <summary>ADC codes per input volt.</summary>
    public double Slope { get; set; }

    /// <summary>ADC code at 0 V.</summary>
    public double Intercept { get; set; }

    public double RmsResidual { get; set; }
    public double MinVolts { get; set; }
    public double MaxVolts { get; set; }
    public uint? DacOffsetCode { get; set; }
    public DateTime Date { get; set; }

    public double VoltsFromCode(double code) => (code - Intercept) / Slope;

    public static StoredGainCalibration From(GainFit fit, uint? dacOffsetCode, DateTime date) => new()
    {
        Channel = fit.Channel,
        GainWord = fit.GainWord,
        Slope = fit.Slope,
        Intercept = fit.Intercept,
        RmsResidual = fit.RmsResidual,
        MinVolts = fit.MinVolts,
        MaxVolts = fit.MaxVolts,
        DacOffsetCode = dacOffsetCode,
        Date = date,
    };
}

/// <summary>The calibration file in the user's application data folder (on Linux ~/.config/OscilloscopeApp).</summary>
public sealed class CalibrationStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public List<StoredGainCalibration> Entries { get; set; } = new();

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OscilloscopeApp", "calibration.json");

    public static CalibrationStore Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return new CalibrationStore();

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<CalibrationStore>(stream) ?? new CalibrationStore();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, this, Options);
    }

    /// <summary>Replaces the entry of the same channel and gain word, or adds a new one.</summary>
    public void Upsert(StoredGainCalibration entry)
    {
        Entries.RemoveAll(e => e.Channel == entry.Channel && e.GainWord == entry.GainWord);
        Entries.Add(entry);
        Entries.Sort((a, b) => a.Channel != b.Channel
            ? a.Channel.CompareTo(b.Channel)
            : a.GainWord.CompareTo(b.GainWord));
    }

    public StoredGainCalibration? Find(int channel, uint gainWord) =>
        Entries.FirstOrDefault(e => e.Channel == channel && e.GainWord == gainWord);
}
