using OspreyTool.Core;
using OspreyTool.Scoring;
using OspreyTool.Scoring.Ranking;
using Xunit;

namespace OspreyTool.Tests;

public sealed class RepickLibCosineTests
{
    // A single Gaussian peak at scan `apex`, scaled by a per-fragment relative intensity.
    private static XicData Frag(int idx, double mz, double relIntensity, int apex = 20, int n = 40)
    {
        var t = new double[n];
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            t[i] = 7.0 + i * 0.02;
            var d = i - apex;
            y[i] = relIntensity * 1000.0 * System.Math.Exp(-(d * d) / 8.0);
        }
        return new XicData { FragmentIndex = idx, RetentionTimes = t, Intensities = y, FragmentIon = $"y{idx}", ProductMz = mz };
    }

    private static List<XicData> ThreeFragments(double i0, double i1, double i2) =>
        new() { Frag(0, 100.0, i0), Frag(1, 200.0, i1), Frag(2, 300.0, i2) };

    private static readonly List<LibraryFragment> Library = new()
    {
        new LibraryFragment(100.0, 1.0f), new LibraryFragment(200.0, 0.5f), new LibraryFragment(300.0, 0.25f),
    };

    private static OspreyFeatureScorer Scorer() => new(OspreyFeatureScorer.UnitResolutionConfig());

    [Fact]
    public void Trace_lists_candidates_with_terms_and_exactly_one_chosen()
    {
        // Explicitly the product ranker: the tool's default is now Osprey's frozen learned pick, whose rank is
        // a weighted sum of z-scores rather than a product (see RankModelPickTests).
        var r = Scorer().Repick(ThreeFragments(1.0, 0.5, 0.25), expectedRt: 7.4, rtTolerance: 0.0,
            rtSigma: 0.3, traceCandidates: true, rankModel: ProductRankModel.Instance);

        Assert.True(r.HasPeak);
        Assert.NotNull(r.CandidatePeaks);
        Assert.NotEmpty(r.CandidatePeaks!);
        Assert.Equal(1, r.CandidatePeaks!.Count(c => c.Chosen));
        // Rank is the product of the individual terms.
        var chosen = r.CandidatePeaks!.Single(c => c.Chosen);
        Assert.Equal(chosen.Coelution * chosen.LibCosine * chosen.RtPenalty * chosen.IntensityWeight, chosen.Rank, 6);
    }

    /// <summary>Reconciliation can force a window that is no CWT candidate at all. ScoreWindow must score that
    /// window on the SAME terms, so the panel can show what the consensus overrode the signal processing with -
    /// and an empty stretch of baseline must come out looking empty (near-zero co-elution), not plausible.</summary>
    [Fact]
    public void ScoreWindow_scores_an_arbitrary_window_on_the_same_terms_as_the_picker()
    {
        var xics = ThreeFragments(1.0, 0.5, 0.25); // one clean peak at 7.40
        var scorer = Scorer();

        // The picker's own window, re-scored via ScoreWindow, reproduces the candidate's terms.
        var chosen = scorer.Repick(xics, 7.4, 0.0, 0.3, libraryFragments: Library, traceCandidates: true)
            .CandidatePeaks!.Single(c => c.Chosen);
        var same = scorer.ScoreWindow(xics, chosen.StartRt, chosen.EndRt, 7.4, 0.3, Library);
        Assert.NotNull(same);
        Assert.Equal(chosen.Coelution, same!.Coelution, 6);
        Assert.Equal(chosen.LibCosine, same.LibCosine, 6);
        Assert.Equal(chosen.Rank, same.Rank, 6);

        // A window over bare baseline, away from the peak, scores as the nothing that it is.
        var empty = scorer.ScoreWindow(xics, 7.02, 7.10, 7.4, 0.3, Library);
        Assert.NotNull(empty);
        Assert.True(empty!.Coelution < 0.5, $"baseline window should not look co-eluting (got {empty.Coelution:F3})");
        Assert.True(empty.Rank < chosen.Rank);
    }

    [Fact]
    public void LibCosine_is_neutral_1_when_no_library_is_supplied()
    {
        var r = Scorer().Repick(ThreeFragments(1.0, 0.5, 0.25), expectedRt: 7.4, rtTolerance: 0.0,
            rtSigma: 0.3, libraryFragments: null, traceCandidates: true);

        Assert.All(r.CandidatePeaks!, c => Assert.Equal(1.0, c.LibCosine, 6));
    }

    /// <summary>The QSMAQRAR case, in miniature: a real peak with better co-elution, better library match and
    /// a better RT, competing with an interference ~150x more intense. ln(1+I) is unbounded while the other
    /// three terms are capped at 1, so at Osprey's w=1 the interference outvotes all the evidence; at w=0 the
    /// evidence decides. This pins the PRODUCT ranker - the failure mode the frozen learned pick replaces
    /// (see RankModelPickTests.Learned_pick_finds_the_real_peak_where_the_product_rank_is_captured_by_intensity).</summary>
    [Fact]
    public void An_intense_interference_beats_the_real_peak_at_w1_and_loses_at_w0()
    {
        const int n = 120;
        var xics = new List<XicData>();
        for (var f = 0; f < 3; f++)
        {
            var t = new double[n];
            var y = new double[n];
            var libRatio = new[] { 1.0, 0.5, 0.25 }[f];   // real peak: matches the library
            var interRatio = new[] { 1.0, 0.1, 0.1 }[f];  // interference: wrong fragment pattern
            for (var i = 0; i < n; i++)
            {
                t[i] = 7.0 + i * 0.01;
                var real = i - 40;                          // co-eluting: the same apex in every fragment
                var inter = i - (56 + (f == 2 ? 1 : 0));    // one fragment off by a scan: co-elutes well, but not perfectly
                y[i] = libRatio * 900.0 * Math.Exp(-(real * real) / 8.0)
                     + interRatio * 137_000.0 * Math.Exp(-(inter * inter) / 8.0);
            }
            xics.Add(new XicData { FragmentIndex = f, RetentionTimes = t, Intensities = y, FragmentIon = $"y{f}", ProductMz = 100.0 * (f + 1) });
        }

        const double realRt = 7.4; // 7.0 + 40 * 0.01; the interference is 0.16 min later, as in QSMAQRAR
        var atW1 = Scorer().Repick(xics, realRt, 0.0, 0.3, libraryFragments: Library, traceCandidates: true,
            intensityExponent: 1.0, rankModel: ProductRankModel.Instance);
        var atW0 = Scorer().Repick(xics, realRt, 0.0, 0.3, libraryFragments: Library, traceCandidates: true,
            intensityExponent: 0.0, rankModel: ProductRankModel.Instance);

        Assert.True(atW1.HasPeak && atW0.HasPeak);
        var dump = string.Join("\n", atW1.CandidatePeaks!.OrderByDescending(c => c.Rank).Select(c =>
            $"apex {c.ApexRt:F2} coel {c.Coelution:F3} libcos {c.LibCosine:F3} rtPen {c.RtPenalty:F3} lnI {c.IntensityWeight:F2} rank {c.Rank:F3}"));
        Assert.True(Math.Abs(atW1.ApexRt - realRt) > 0.1,
            $"w=1 should be captured by the intense interference, but picked {atW1.ApexRt:F2}\n{dump}");
        var dump0 = string.Join("\n", atW0.CandidatePeaks!.OrderByDescending(c => c.Rank).Select(c =>
            $"apex {c.ApexRt:F2} coel {c.Coelution:F3} libcos {c.LibCosine:F3} rtPen {c.RtPenalty:F3} lnI {c.IntensityWeight:F2} rank {c.Rank:F3}"));
        Assert.True(Math.Abs(atW0.ApexRt - realRt) <= 0.05,
            $"w=0 should pick the real peak at {realRt:F2}, but picked {atW0.ApexRt:F2}\n{dump0}");

        // ...and it loses for the right reason: the real peak was ahead on every bounded term.
        var chosen0 = atW0.CandidatePeaks!.Single(c => c.Chosen);
        var runnerUp = atW0.CandidatePeaks!.Where(c => !c.Chosen).OrderByDescending(c => c.Rank).First();
        Assert.True(chosen0.Coelution > runnerUp.Coelution);
        Assert.True(chosen0.LibCosine > runnerUp.LibCosine);
        Assert.True(chosen0.IntensityWeight < runnerUp.IntensityWeight * 1.0001); // still the weaker signal
    }

    [Fact]
    public void LibCosine_is_higher_when_the_fragment_pattern_matches_the_library()
    {
        // Same three m/z, same peak position - only the fragment INTENSITY ratios differ.
        var matched = Scorer().Repick(ThreeFragments(1.0, 0.5, 0.25), 7.4, 0.0, 0.3,
            libraryFragments: Library, traceCandidates: true).CandidatePeaks!.Single(c => c.Chosen).LibCosine;
        var mismatched = Scorer().Repick(ThreeFragments(0.1, 0.1, 1.0), 7.4, 0.0, 0.3,
            libraryFragments: Library, traceCandidates: true).CandidatePeaks!.Single(c => c.Chosen).LibCosine;

        Assert.True(matched > 0.9, $"matched cosine {matched:F3} should be high");
        Assert.True(matched > mismatched + 0.05,
            $"library-matching peak (cos {matched:F3}) should score higher than the mismatched one ({mismatched:F3})");
    }
}
