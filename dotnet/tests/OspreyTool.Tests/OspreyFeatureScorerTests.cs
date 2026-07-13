using OspreyTool.Core;
using OspreyTool.Scoring;
using Xunit;
using LibraryEntry = pwiz.Osprey.Core.LibraryEntry;
using LibraryFragment = pwiz.Osprey.Core.LibraryFragment;

namespace OspreyTool.Tests;

/// <summary>
/// End-to-end integration test through the real Osprey engine (CWT + the 12-feature subset),
/// driven entirely from our Skyline-shaped types. Proves two things at once: the Osprey.Api
/// facade wiring works against the live assemblies, and the decoy-XIC generator lowers Osprey's
/// primary co-elution score - the M0 target/decoy signal, in a hermetic test.
/// </summary>
public sealed class OspreyFeatureScorerTests
{
    private const int ScanCount = 40;
    private const int ApexScan = 20;

    private static PrecursorChromatograms CoElutingTarget()
    {
        var times = new double[ScanCount];
        for (var i = 0; i < ScanCount; i++)
        {
            times[i] = 7.0 + i * 0.02;
        }

        var xics = new List<XicData>();
        var heights = new[] { 1000.0, 700.0, 400.0, 250.0 };
        for (var f = 0; f < heights.Length; f++)
        {
            var intensity = new double[ScanCount];
            for (var i = 0; i < ScanCount; i++)
            {
                var d = i - ApexScan;
                intensity[i] = heights[f] * Math.Exp(-(d * d) / 4.0);
            }
            xics.Add(new XicData
            {
                FragmentIndex = f,
                RetentionTimes = times,
                Intensities = intensity,
                FragmentIon = f == 0 ? "precursor" : $"y{f}",
            });
        }

        return new PrecursorChromatograms
        {
            FileName = "run1.raw",
            PeptideModifiedSequence = "PEPTIDER",
            PrecursorCharge = 2,
            Xics = xics,
        };
    }

    private static LibraryEntry Candidate()
    {
        var entry = new LibraryEntry(1, "PEPTIDER", "PEPTIDER", 2, 500.25, 7.4)
        {
            Fragments = new List<LibraryFragment>
            {
                new() { Mz = 500.25, RelativeIntensity = 1.0f },
                new() { Mz = 600.30, RelativeIntensity = 0.7f },
                new() { Mz = 700.40, RelativeIntensity = 0.4f },
                new() { Mz = 800.50, RelativeIntensity = 0.25f },
            },
        };
        return entry;
    }

    [Fact]
    public void Score_target_beats_decoy_xic_on_coelution()
    {
        var target = CoElutingTarget();
        var decoy = new XicShuffleDecoyGenerator(seed: 1337).Generate(target);
        var candidate = Candidate();

        var scorer = new OspreyFeatureScorer(OspreyFeatureScorer.UnitResolutionConfig());
        var targetScore = scorer.Score(candidate, target.Xics, expectedRt: 7.4);
        var decoyScore = scorer.Score(candidate, decoy.Xics, expectedRt: 7.4);

        // CWT found the shared peak in the co-eluting target and computed a positive primary score.
        Assert.True(targetScore.HasPeak);
        Assert.True(targetScore.CoelutionSum > 0, $"target coelution_sum was {targetScore.CoelutionSum}");

        // The whole point of the decoy: Osprey's primary co-elution score drops for the scrambled traces.
        Assert.True(
            targetScore.CoelutionSum > decoyScore.CoelutionSum,
            $"expected target ({targetScore.CoelutionSum:F3}) > decoy ({decoyScore.CoelutionSum:F3})");

        // Only the 12-feature subset is populated; the rest of the 21-vector stays 0.
        Assert.Equal(21, targetScore.Features.Length);
        foreach (var i in new[] { 6, 7, 8, 9, 10, 13, 14, 17, 18 })
        {
            Assert.Equal(0.0, targetScore.Features[i]);
        }
    }
}
