using OspreyTool.Core;
using OspreyTool.Scoring;
using Xunit;

namespace OspreyTool.Tests;

public sealed class RepickTests
{
    private static XicData Peak(int index, string fragmentIon, double scale, int apex = 20, int n = 40)
    {
        var times = new double[n];
        var intensity = new double[n];
        for (var i = 0; i < n; i++)
        {
            times[i] = 7.0 + i * 0.02;
            var d = i - apex;
            intensity[i] = scale * Math.Exp(-(d * d) / 4.0);
        }
        return new XicData
        {
            FragmentIndex = index,
            RetentionTimes = times,
            Intensities = intensity,
            FragmentIon = fragmentIon,
        };
    }

    private static OspreyFeatureScorer Scorer() => new(OspreyFeatureScorer.UnitResolutionConfig());

    [Fact]
    public void Repick_skips_when_only_one_fragment_after_excluding_precursor()
    {
        // Precursor + a single fragment -> after dropping the precursor, 1 fragment -> not scorable.
        var xics = new List<XicData> { Peak(0, "precursor", 1000), Peak(1, "y1", 700) };
        var r = Scorer().Repick(xics, expectedRt: 7.4, rtTolerance: 0.5, rtSigma: 0.3);

        Assert.False(r.HasPeak);
        Assert.True(r.TooFew);
    }

    [Fact]
    public void Repick_scores_fragments_only_not_the_precursor()
    {
        // The precursor is by far the most intense trace; if it leaked into scoring it would dominate the
        // reference XIC. With 3 co-eluting fragments the fragment co-elution is high and a peak is found.
        var xics = new List<XicData>
        {
            Peak(0, "precursor", 100000),
            Peak(1, "y1", 1000),
            Peak(2, "y2", 700),
            Peak(3, "y3", 400),
        };
        var r = Scorer().Repick(xics, expectedRt: 7.4, rtTolerance: 0.5, rtSigma: 0.3);

        Assert.True(r.HasPeak);
        Assert.True(r.Coelution > 0.5, $"3 co-eluting fragments should correlate well, was {r.Coelution:F3}");
    }
}
