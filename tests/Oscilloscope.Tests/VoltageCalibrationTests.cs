using Oscilloscope.Core.Calibration;
using Oscilloscope.Core.Device;
using Xunit;

namespace Oscilloscope.Tests;

public class GainPresetsTests
{
    [Theory]
    [InlineData(0.1, 2u)]
    [InlineData(0.5, 9u)]
    [InlineData(1.0, 18u)]
    [InlineData(2.0, 36u)]
    [InlineData(4.0, 72u)]
    [InlineData(8.0, 148u)]   // above the low range (7.08 V/V), so the high range
    [InlineData(16.0, 169u)]
    [InlineData(32.0, 209u)]
    public void Picks_the_word_closest_to_the_requested_gain(double voltsPerVolt, uint expectedWord)
    {
        Assert.Equal(expectedWord, GainPresets.WordFor(voltsPerVolt));
    }

    [Fact]
    public void Presets_are_distinct_and_close_to_their_target()
    {
        var words = GainPresets.All.Select(p => p.Word).ToList();
        Assert.Equal(words.Count, words.Distinct().Count());

        foreach (var preset in GainPresets.All.Where(p => p.Target >= 0.5))
            Assert.InRange(Math.Abs(preset.ErrorPercent), 0, 2);
    }

    [Fact]
    public void Nearest_index_snaps_any_word_to_a_preset()
    {
        Assert.Equal(2, GainPresets.NearestIndex(18));  // 1 V/V
        Assert.Equal(2, GainPresets.NearestIndex(19));
        Assert.Equal(0, GainPresets.NearestIndex(0));   // gain stage off
        Assert.Equal(0, GainPresets.NearestIndex(128));
    }
}

public class LinearFitTests
{
    [Fact]
    public void Fits_an_exact_line()
    {
        var fit = LinearFit.Fit(new List<(double X, double Y)> { (0, 10), (1, 14), (2, 18) });

        Assert.NotNull(fit);
        Assert.Equal(4, fit!.Value.Slope, 9);
        Assert.Equal(10, fit.Value.Intercept, 9);
        Assert.Equal(0, fit.Value.RmsResidual, 9);
    }

    [Fact]
    public void Needs_two_distinct_x_values()
    {
        Assert.Null(LinearFit.Fit(new List<(double X, double Y)> { (1, 5) }));
        Assert.Null(LinearFit.Fit(new List<(double X, double Y)> { (1, 5), (1, 6) }));
    }
}

public class VoltageFitsTests
{
    [Fact]
    public void Leaves_clipped_points_out_of_the_fit()
    {
        var points = new[]
        {
            new VoltagePoint(-1, new CalibrationPoint(1, 18, 108, 0.5, 100, 115, 8192)),
            new VoltagePoint(0, new CalibrationPoint(1, 18, 128, 0.5, 120, 135, 8192)),
            new VoltagePoint(1, new CalibrationPoint(1, 18, 148, 0.5, 140, 155, 8192)),
            new VoltagePoint(10, new CalibrationPoint(1, 18, 255, 0.0, 250, 255, 8192)), // at the ADC limit
        };

        var fit = Assert.Single(VoltageFits.Compute(points));

        Assert.True(fit.IsValid);
        Assert.Equal(3, fit.UsedPoints);
        Assert.Equal(4, fit.TotalPoints);
        Assert.Equal(20, fit.Slope, 6);
        Assert.Equal(128, fit.Intercept, 6);
        Assert.Equal(1, fit.MaxVolts, 6);
        Assert.Equal(50, fit.LsbMillivolts, 6);
        Assert.Equal(1, fit.VoltsFromCode(148), 6);
    }

    [Fact]
    public void Reports_gains_without_two_usable_points_as_invalid()
    {
        var points = new[]
        {
            new VoltagePoint(0, new CalibrationPoint(1, 36, 128, 0.5, 120, 135, 8192)),
        };

        var fit = Assert.Single(VoltageFits.Compute(points));
        Assert.False(fit.IsValid);
    }

    [Fact]
    public void Fits_each_gain_word_separately()
    {
        var points = new[]
        {
            new VoltagePoint(0, new CalibrationPoint(1, 18, 128, 0.5, 120, 135, 8192)),
            new VoltagePoint(1, new CalibrationPoint(1, 18, 140, 0.5, 130, 150, 8192)),
            new VoltagePoint(0, new CalibrationPoint(1, 36, 128, 0.5, 120, 135, 8192)),
            new VoltagePoint(1, new CalibrationPoint(1, 36, 152, 0.5, 140, 160, 8192)),
        };

        var fits = VoltageFits.Compute(points);

        Assert.Equal(2, fits.Count);
        Assert.Equal(12, fits[0].Slope, 6);
        Assert.Equal(24, fits[1].Slope, 6);
    }
}

public class CalibrationStoreTests
{
    [Fact]
    public void Upsert_replaces_the_same_channel_and_word()
    {
        var store = new CalibrationStore();
        store.Upsert(new StoredGainCalibration { Channel = 1, GainWord = 18, Slope = 10 });
        store.Upsert(new StoredGainCalibration { Channel = 1, GainWord = 18, Slope = 12 });
        store.Upsert(new StoredGainCalibration { Channel = 2, GainWord = 18, Slope = 7 });

        Assert.Equal(2, store.Entries.Count);
        Assert.Equal(12, store.Find(1, 18)!.Slope);
        Assert.Equal(7, store.Find(2, 18)!.Slope);
        Assert.Null(store.Find(1, 36));
    }

    [Fact]
    public void Survives_a_save_and_load()
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.json");
        try
        {
            var store = new CalibrationStore();
            store.Upsert(new StoredGainCalibration
            {
                Channel = 1, GainWord = 36, Slope = 25.5, Intercept = 127.25, DacOffsetCode = 2048,
            });
            store.Save(path);

            var loaded = CalibrationStore.Load(path).Find(1, 36);

            Assert.NotNull(loaded);
            Assert.Equal(25.5, loaded!.Slope);
            Assert.Equal(127.25, loaded.Intercept);
            Assert.Equal(2048u, loaded.DacOffsetCode);
            Assert.Equal(1.0, loaded.VoltsFromCode(152.75), 6);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
