namespace Oscilloscope.Core.Calibration;

public readonly record struct LinearFitResult(double Slope, double Intercept, double RmsResidual, double MaxResidual);

public static class LinearFit
{
    /// <summary>Least squares line y = Slope * x + Intercept. Null with fewer than two distinct x values.</summary>
    public static LinearFitResult? Fit(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count < 2)
            return null;

        double meanX = points.Average(p => p.X);
        double meanY = points.Average(p => p.Y);

        double sxx = 0, sxy = 0;
        foreach (var (x, y) in points)
        {
            sxx += (x - meanX) * (x - meanX);
            sxy += (x - meanX) * (y - meanY);
        }

        if (sxx < 1e-18)
            return null;

        double slope = sxy / sxx;
        double intercept = meanY - slope * meanX;

        double sumSquares = 0, max = 0;
        foreach (var (x, y) in points)
        {
            double residual = y - (slope * x + intercept);
            sumSquares += residual * residual;
            max = Math.Max(max, Math.Abs(residual));
        }

        return new LinearFitResult(slope, intercept, Math.Sqrt(sumSquares / points.Count), max);
    }
}
