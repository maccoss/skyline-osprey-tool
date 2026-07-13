using OspreyTool.Core;
using OspreyTool.Core.Detection;
using OspreyTool.Scoring;
using OspreyTool.Scoring.Detection;
using Xunit;

namespace OspreyTool.Tests;

public sealed class PeakDetectorTests
{
    // Two resolved peaks: a big one at scan 40 and a smaller one at 75, on a 120-scan grid.
    private static List<XicData> TwoPeaks(int n = 120)
    {
        var xics = new List<XicData>();
        for (var f = 0; f < 3; f++)
        {
            var t = new double[n];
            var y = new double[n];
            var rel = new[] { 1.0, 0.6, 0.3 }[f];
            for (var i = 0; i < n; i++)
            {
                t[i] = 7.0 + i * 0.01;
                var a = i - 40;
                var b = i - 75;
                y[i] = rel * (10_000.0 * Math.Exp(-(a * a) / 8.0) + 3_000.0 * Math.Exp(-(b * b) / 8.0));
            }
            xics.Add(new XicData { FragmentIndex = f, RetentionTimes = t, Intensities = y, FragmentIon = $"y{f}", ProductMz = 100.0 * (f + 1) });
        }
        return xics;
    }

    private static PeakDetectionInput InputFrom(List<XicData> xics) => new()
    {
        FragmentIntensities = xics.Select(x => x.Intensities).ToList(),
        RetentionTimes = xics[0].RetentionTimes,
        ExpectedRt = 7.4,
    };

    [Fact]
    public void Both_shipped_detectors_find_both_peaks()
    {
        var input = InputFrom(TwoPeaks());
        foreach (var detector in new IPeakDetector[] { new OspreyCwtPeakDetector(), new LocalMaximaPeakDetector() })
        {
            var peaks = detector.Detect(input);
            var apexes = peaks.Select(p => input.RetentionTimes[p.ApexIndex]).ToList();
            Assert.True(apexes.Any(rt => Math.Abs(rt - 7.40) <= 0.02),
                $"{detector.Id} missed the peak at 7.40 (found: {string.Join(", ", apexes.Select(a => a.ToString("F2")))})");
            Assert.True(apexes.Any(rt => Math.Abs(rt - 7.75) <= 0.02),
                $"{detector.Id} missed the peak at 7.75 (found: {string.Join(", ", apexes.Select(a => a.ToString("F2")))})");
            Assert.All(peaks, p => Assert.True(
                p.StartIndex >= 0 && p.EndIndex < input.ScanCount && p.StartIndex <= p.ApexIndex && p.ApexIndex <= p.EndIndex,
                $"{detector.Id} returned a malformed window"));
        }
    }

    [Fact]
    public void Detectors_return_nothing_rather_than_throwing_on_thin_data()
    {
        foreach (var detector in new IPeakDetector[] { new OspreyCwtPeakDetector(), new LocalMaximaPeakDetector() })
        {
            var oneFragment = new PeakDetectionInput
            {
                FragmentIntensities = new List<double[]> { new double[20] },
                RetentionTimes = Enumerable.Range(0, 20).Select(i => 7.0 + i * 0.01).ToArray(),
            };
            Assert.Empty(detector.Detect(oneFragment));
        }
    }

    /// <summary>The point of the seam: a detector with NO Osprey dependency drives the scorer end to end.</summary>
    [Fact]
    public void A_custom_detector_drives_the_scorer()
    {
        var xics = TwoPeaks();
        // Proposes exactly one window - the smaller peak - so the pick must land there and nowhere else.
        var fake = new FixedWindowDetector(new PeakWindow(70, 75, 80));
        var r = new OspreyFeatureScorer(OspreyFeatureScorer.UnitResolutionConfig(), fake)
            .Repick(xics, expectedRt: 7.4, rtTolerance: 0.0, rtSigma: 0.3, traceCandidates: true);

        Assert.True(r.HasPeak);
        Assert.Single(r.CandidatePeaks!);
        Assert.Equal(7.75, r.ApexRt, 2); // the window the custom detector proposed, not CWT's better peak
        Assert.True(r.Coelution > 0.9, "the scorer still scores the window it was handed");
    }

    [Fact]
    public void Registry_exposes_both_detectors_and_resolves_by_id()
    {
        Assert.Equal("osprey-cwt", PeakDetectors.Default.Id);
        Assert.NotNull(PeakDetectors.ById("osprey-cwt"));
        Assert.NotNull(PeakDetectors.ById("local-maxima"));
        Assert.Null(PeakDetectors.ById("no-such-detector"));
    }

    private sealed class FixedWindowDetector : IPeakDetector
    {
        private readonly PeakWindow _window;
        public FixedWindowDetector(PeakWindow window) => _window = window;
        public string Id => "fixed-window";
        public string DisplayName => "Fixed window (test)";
        public IReadOnlyList<PeakWindow> Detect(PeakDetectionInput input) => new[] { _window };
    }
}
