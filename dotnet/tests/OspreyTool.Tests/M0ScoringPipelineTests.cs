using OspreyTool.Core;
using OspreyTool.Scoring;
using Xunit;
using LibraryEntry = pwiz.Osprey.Core.LibraryEntry;
using LibraryFragment = pwiz.Osprey.Core.LibraryFragment;

namespace OspreyTool.Tests;

/// <summary>
/// Hermetic end-to-end pipeline test: a handful of co-eluting target groups through the full
/// score -> decoy-XIC -> separation -> Percolator flow, asserting the M0 signal (co-elution
/// separates target from decoy) holds without any real data file.
/// </summary>
public sealed class M0ScoringPipelineTests
{
    private const int ScanCount = 40;

    private static PrecursorChromatograms CoElutingGroup(string peptide, int charge, int apexScan, double scale)
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
                var d = i - apexScan;
                intensity[i] = scale * heights[f] * Math.Exp(-(d * d) / 4.0);
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
            PeptideModifiedSequence = peptide,
            PrecursorCharge = charge,
            Xics = xics,
        };
    }

    private static LibraryEntry Candidate(string peptide, int charge, double rt) =>
        new((uint)peptide.GetHashCode(), peptide, peptide, (byte)charge, 500.0, rt)
        {
            Fragments = new List<LibraryFragment>
            {
                new() { Mz = 500.0, RelativeIntensity = 1.0f },
                new() { Mz = 600.0, RelativeIntensity = 0.7f },
                new() { Mz = 700.0, RelativeIntensity = 0.4f },
                new() { Mz = 800.0, RelativeIntensity = 0.25f },
            },
        };

    [Fact]
    public void Coelution_separates_target_from_decoy_across_a_synthetic_run()
    {
        var peptides = Enumerable.Range(0, 16).Select(i => $"PEPTIDE{i}K").ToArray();
        var groups = new List<PrecursorChromatograms>();
        var entries = new List<LibraryEntry>();
        for (var i = 0; i < peptides.Length; i++)
        {
            // Vary apex position and scale so the features aren't degenerate.
            var apex = 16 + (i % 9);
            groups.Add(CoElutingGroup(peptides[i], 2, apex, 1.0 + 0.1 * i));
            entries.Add(Candidate(peptides[i], 2, 7.0 + apex * 0.02));
        }

        var library = OspreyLibrary.FromEntries(entries);
        var config = OspreyFeatureScorer.UnitResolutionConfig();
        var featureIndices = new[] { 0, 2, 15 }; // coelution_sum, n_coeluting, md_cosine

        var result = M0ScoringPipeline.Run(
            groups, library, config, featureIndices, new XicShuffleDecoyGenerator(seed: 1337));

        Assert.Equal(16, result.Matched);
        Assert.Equal(0, result.Unmatched);
        Assert.Equal(16, result.TargetsWithPeak);

        // The M0 signal: co-elution clearly separates real targets from scrambled decoys.
        var coelution = result.Separations.Single(s => s.FeatureIndex == 0);
        Assert.True(coelution.Auc > 0.8, $"coelution AUC was {coelution.Auc:F3}");
        Assert.True(coelution.TargetMean > coelution.DecoyMean);

        // Percolator ran per replicate (one run here) without crashing the pipeline.
        Assert.Single(result.PerRun);
        Assert.Equal("run1.raw", result.PerRun[0].FileName);
    }
}
