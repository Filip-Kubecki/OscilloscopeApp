namespace Oscilloscope.Core.Device;

/// <summary>Running mean, standard deviation (population), minimum and maximum of 8-bit samples.</summary>
public sealed class SampleStats
{
    private long _sum;
    private long _sumSquares;

    public long Count { get; private set; }
    public byte Min { get; private set; } = byte.MaxValue;
    public byte Max { get; private set; } = byte.MinValue;

    public void Add(ReadOnlySpan<byte> samples)
    {
        foreach (byte b in samples)
        {
            _sum += b;
            _sumSquares += b * b;
            if (b < Min) Min = b;
            if (b > Max) Max = b;
        }
        Count += samples.Length;
    }

    public double Mean => Count == 0 ? 0 : (double)_sum / Count;

    public double StdDev
    {
        get
        {
            if (Count == 0) return 0;
            double mean = Mean;
            double variance = (double)_sumSquares / Count - mean * mean;
            return variance > 0 ? Math.Sqrt(variance) : 0;
        }
    }
}
