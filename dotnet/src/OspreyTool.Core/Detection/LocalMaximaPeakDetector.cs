namespace OspreyTool.Core.Detection;

/// <summary>
/// A minimal, dependency-free peak detector: smooth the summed fragment trace, take its local maxima above a
/// noise floor, and walk out to the valleys on either side.
///
/// It exists for two reasons. It is the reference implementation of <see cref="IPeakDetector"/> - proof that
/// a detector needs nothing from Osprey but arrays - and it is a usable baseline to compare Osprey's CWT
/// against on real data. It is deliberately simple: no wavelets, no deconvolution of shoulders.
/// </summary>
public sealed class LocalMaximaPeakDetector : IPeakDetector
{
    /// <summary>Candidate apexes must exceed the baseline by this many robust deviations (MAD-based).</summary>
    private const double NoiseSigmas = 3.0;

    /// <summary>A peak ends where the trace has fallen to this fraction of its apex, or at a valley.</summary>
    private const double BoundaryFraction = 0.05;

    private const int MinScans = 3;

    public string Id => "local-maxima";

    public string DisplayName => "Local maxima (simple baseline)";

    public IReadOnlyList<PeakWindow> Detect(PeakDetectionInput input)
    {
        var n = input.ScanCount;
        if (input.FragmentCount < 2 || n < 5)
        {
            return Array.Empty<PeakWindow>();
        }

        // Consensus trace: the summed fragments. A real peak is present in several fragments at once, so
        // summing suppresses single-fragment noise spikes relative to co-eluting signal.
        var trace = new double[n];
        foreach (var frag in input.FragmentIntensities)
        {
            for (var i = 0; i < n && i < frag.Length; i++)
            {
                trace[i] += frag[i];
            }
        }
        var smooth = MovingAverage(trace, 3);
        var (baseline, spread) = BaselineAndSpread(smooth);
        var floor = Math.Max(baseline + NoiseSigmas * spread, input.MinConsensusHeight);

        var peaks = new List<PeakWindow>();
        for (var i = 1; i < n - 1; i++)
        {
            if (smooth[i] < floor || smooth[i] < smooth[i - 1] || smooth[i] < smooth[i + 1])
            {
                continue;
            }
            // Skip the flat interior of a plateau: only the first scan of it apexes.
            if (smooth[i] == smooth[i - 1])
            {
                continue;
            }

            var cutoff = Math.Max(baseline, smooth[i] * BoundaryFraction);
            var start = i;
            while (start > 0 && smooth[start - 1] < smooth[start] && smooth[start] > cutoff)
            {
                start--;
            }
            var end = i;
            while (end < n - 1 && smooth[end + 1] < smooth[end] && smooth[end] > cutoff)
            {
                end++;
            }
            if (end - start + 1 < MinScans)
            {
                continue;
            }

            var area = 0.0;
            for (var k = start; k <= end; k++)
            {
                area += trace[k];
            }
            peaks.Add(new PeakWindow(start, i, end)
            {
                Area = area,
                ApexIntensity = trace[i],
                SignalToNoise = spread > 0.0 ? (trace[i] - baseline) / spread : 0.0,
            });
        }
        return peaks;
    }

    private static double[] MovingAverage(double[] v, int half)
    {
        var result = new double[v.Length];
        for (var i = 0; i < v.Length; i++)
        {
            var sum = 0.0;
            var count = 0;
            for (var k = Math.Max(0, i - half); k <= Math.Min(v.Length - 1, i + half); k++)
            {
                sum += v[k];
                count++;
            }
            result[i] = sum / count;
        }
        return result;
    }

    /// <summary>Median and MAD-derived sigma - robust to the peaks themselves, unlike mean/stddev.</summary>
    private static (double Baseline, double Spread) BaselineAndSpread(double[] v)
    {
        var sorted = (double[])v.Clone();
        Array.Sort(sorted);
        var median = Median(sorted);
        var deviations = new double[v.Length];
        for (var i = 0; i < v.Length; i++)
        {
            deviations[i] = Math.Abs(v[i] - median);
        }
        Array.Sort(deviations);
        var mad = Median(deviations) * 1.4826;
        return (median, mad > 0.0 ? mad : 1.0);
    }

    private static double Median(double[] sorted) =>
        sorted.Length == 0 ? 0.0
        : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
        : 0.5 * (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]);
}
