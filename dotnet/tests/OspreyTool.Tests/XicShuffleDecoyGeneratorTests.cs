using OspreyTool.Core;
using Xunit;

namespace OspreyTool.Tests;

public sealed class XicShuffleDecoyGeneratorTests
{
    // A target whose fragments all co-elute: three narrow peaks at the same grid position,
    // differing only in height. Mean pairwise correlation is ~1. A good decoy must break that.
    private static PrecursorChromatograms CoElutingTarget()
    {
        const int n = 40;
        var times = new double[n];
        for (var i = 0; i < n; i++)
        {
            times[i] = 7.0 + i * 0.02;
        }

        var xics = new List<XicData>();
        var heights = new[] { 1000.0, 700.0, 400.0 };
        for (var f = 0; f < heights.Length; f++)
        {
            var intensity = new double[n];
            for (var i = 0; i < n; i++)
            {
                var d = i - 20;                        // shared apex at index 20
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

    [Fact]
    public void Generate_breaks_coelution_while_preserving_grid_and_area()
    {
        var target = CoElutingTarget();
        var decoy = new XicShuffleDecoyGenerator(seed: 1337).Generate(target);

        // Decoy identity is labelled and otherwise matches the target replicate/charge.
        Assert.StartsWith(XicShuffleDecoyGenerator.DecoyPrefix, decoy.PeptideModifiedSequence);
        Assert.True(XicShuffleDecoyGenerator.IsDecoyLabel(decoy.PeptideModifiedSequence));
        Assert.Equal(target.PrecursorCharge, decoy.PrecursorCharge);
        Assert.Equal(target.Xics.Count, decoy.Xics.Count);

        for (var f = 0; f < target.Xics.Count; f++)
        {
            // Same shared RT grid.
            Assert.Equal(target.Xics[f].RetentionTimes, decoy.Xics[f].RetentionTimes);
            // Per-fragment intensity values are only rotated: same multiset, so peak area/height preserved.
            Assert.Equal(
                target.Xics[f].Intensities.OrderBy(v => v).ToArray(),
                decoy.Xics[f].Intensities.OrderBy(v => v).ToArray());
        }

        // The point of the decoy: fragments that co-eluted no longer do.
        var targetCoelution = MeanPairwiseCorrelation(target);
        var decoyCoelution = MeanPairwiseCorrelation(decoy);
        Assert.True(targetCoelution > 0.99, $"target should co-elute, was {targetCoelution:F3}");
        Assert.True(decoyCoelution < 0.5, $"decoy co-elution should collapse, was {decoyCoelution:F3}");
    }

    [Fact]
    public void Generate_is_deterministic_for_a_given_seed()
    {
        var target = CoElutingTarget();
        var a = new XicShuffleDecoyGenerator(seed: 7).Generate(target);
        var b = new XicShuffleDecoyGenerator(seed: 7).Generate(target);
        for (var f = 0; f < a.Xics.Count; f++)
        {
            Assert.Equal(a.Xics[f].Intensities, b.Xics[f].Intensities);
        }
    }

    private static double MeanPairwiseCorrelation(PrecursorChromatograms group)
    {
        var xics = group.Xics;
        double sum = 0;
        var count = 0;
        for (var i = 0; i < xics.Count; i++)
        {
            for (var j = i + 1; j < xics.Count; j++)
            {
                sum += Pearson(xics[i].Intensities, xics[j].Intensities);
                count++;
            }
        }
        return count == 0 ? 0 : sum / count;
    }

    private static double Pearson(double[] a, double[] b)
    {
        var n = a.Length;
        double ma = a.Average();
        double mb = b.Average();
        double cov = 0, va = 0, vb = 0;
        for (var i = 0; i < n; i++)
        {
            var da = a[i] - ma;
            var db = b[i] - mb;
            cov += da * db;
            va += da * da;
            vb += db * db;
        }
        if (va == 0 || vb == 0)
        {
            return 0;
        }
        return cov / Math.Sqrt(va * vb);
    }
}
