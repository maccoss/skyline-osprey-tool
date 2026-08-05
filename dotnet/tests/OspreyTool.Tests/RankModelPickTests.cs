using OspreyTool.Core;
using OspreyTool.Scoring;
using OspreyTool.Scoring.Ranking;
using Xunit;

namespace OspreyTool.Tests;

/// <summary>
/// The rank model is the seam that decides WHICH detected candidate is the peak. These tests pin the default
/// (Osprey's product rank, unchanged) and show that a learned model changes only the pick.
/// </summary>
public sealed class RankModelPickTests
{
    private static OspreyFeatureScorer Scorer() => new(OspreyFeatureScorer.UnitResolutionConfig());

    private static readonly List<LibraryFragment> Library = new()
    {
        new LibraryFragment(100.0, 1.0f), new LibraryFragment(200.0, 0.5f), new LibraryFragment(300.0, 0.25f),
    };

    /// <summary>The QSMAQRAR case from RepickLibCosineTests: a real library-matching peak at 7.40 against an
    /// interference ~150x more intense at 7.56.</summary>
    private static List<XicData> RealPeakVersusIntenseInterference()
    {
        const int n = 120;
        var xics = new List<XicData>();
        for (var f = 0; f < 3; f++)
        {
            var t = new double[n];
            var y = new double[n];
            var libRatio = new[] { 1.0, 0.5, 0.25 }[f];
            var interRatio = new[] { 1.0, 0.1, 0.1 }[f];
            for (var i = 0; i < n; i++)
            {
                t[i] = 7.0 + i * 0.01;
                var real = i - 40;
                var inter = i - (56 + (f == 2 ? 1 : 0));
                y[i] = libRatio * 900.0 * Math.Exp(-(real * real) / 8.0)
                     + interRatio * 137_000.0 * Math.Exp(-(inter * inter) / 8.0);
            }
            xics.Add(new XicData
            {
                FragmentIndex = f, RetentionTimes = t, Intensities = y,
                FragmentIon = $"y{f}", ProductMz = 100.0 * (f + 1),
            });
        }
        return xics;
    }

    /// <summary>Osprey's default pick since #4484 is the frozen learned model, not the product form. A
    /// unit-resolution config must therefore default to the Stellar weights.</summary>
    [Fact]
    public void Default_ranker_is_the_frozen_learned_pick_for_the_configs_resolution()
    {
        var unit = new OspreyFeatureScorer(OspreyFeatureScorer.UnitResolutionConfig()).DefaultRankModel;
        var hram = new OspreyFeatureScorer(OspreyFeatureScorer.HramConfig()).DefaultRankModel;

        Assert.Equal("lda", unit.Id);
        Assert.Same(PickLdaModel.Stellar, Assert.IsType<PickLdaRankModel>(unit).Model);
        Assert.Same(PickLdaModel.Astral, Assert.IsType<PickLdaRankModel>(hram).Model);
    }

    /// <summary>Passing no model must be identical to passing the scorer's default explicitly.</summary>
    [Fact]
    public void Passing_no_model_uses_the_scorers_default()
    {
        var xics = RealPeakVersusIntenseInterference();
        var scorer = Scorer();
        var implicitDefault = scorer.Repick(xics, 7.4, 0.0, 0.3, libraryFragments: Library, traceCandidates: true);
        var explicitDefault = scorer.Repick(xics, 7.4, 0.0, 0.3, libraryFragments: Library,
            traceCandidates: true, rankModel: scorer.DefaultRankModel);

        Assert.Equal(implicitDefault.ApexRt, explicitDefault.ApexRt);
        Assert.Equal(implicitDefault.StartRt, explicitDefault.StartRt);
        Assert.Equal(implicitDefault.EndRt, explicitDefault.EndRt);
        Assert.Equal(implicitDefault.RankScore, explicitDefault.RankScore);
    }

    [Fact]
    public void Product_rank_is_exactly_the_product_of_the_terms()
    {
        var chosen = Scorer()
            .Repick(RealPeakVersusIntenseInterference(), 7.4, 0.0, 0.3, libraryFragments: Library,
                traceCandidates: true, rankModel: ProductRankModel.Instance)
            .CandidatePeaks!.Single(c => c.Chosen);

        Assert.Equal(chosen.Coelution * chosen.LibCosine * chosen.RtPenalty * chosen.IntensityWeight,
            chosen.Rank, 12);
    }

    /// <summary>The learned rank must be exactly Osprey's arithmetic on the four raw terms - the weighted sum
    /// of z-scores, with ln_intensity taken from the RAW apex intensity (not the product form's ln(1+I)^w).</summary>
    [Fact]
    public void Learned_rank_is_the_weighted_sum_of_z_scored_terms()
    {
        var chosen = Scorer()
            .Repick(RealPeakVersusIntenseInterference(), 7.4, 0.0, 0.3, libraryFragments: Library,
                traceCandidates: true, intensityExponent: 0.0) // w=0: the product's intensity term is 1.0
            .CandidatePeaks!.Single(c => c.Chosen);

        var expected = PickLdaModel.Stellar.Score(
            chosen.Coelution, Math.Log(1.0 + chosen.ApexIntensity), chosen.RtPenalty, chosen.LibCosine);
        Assert.Equal(expected, chosen.Rank, 12);
        Assert.Equal(1.0, chosen.IntensityWeight); // proving the learned path ignores --intensity-exp
    }

    /// <summary>
    /// Why the learned pick exists: at Osprey's w=1 the unbounded ln(1+I) factor lets an intense interference
    /// outvote co-elution, spectral match and RT combined. The Stellar weights put ~all the weight on
    /// co-elution (0.993) and almost none on intensity (0.047), so the real peak wins - with no
    /// --intensity-exp hand-tuning.
    /// </summary>
    [Fact]
    public void Learned_pick_finds_the_real_peak_where_the_product_rank_is_captured_by_intensity()
    {
        var xics = RealPeakVersusIntenseInterference();
        const double realRt = 7.4;

        var product = Scorer().Repick(xics, realRt, 0.0, 0.3, libraryFragments: Library,
            traceCandidates: true, intensityExponent: 1.0, rankModel: ProductRankModel.Instance);
        Assert.True(Math.Abs(product.ApexRt - realRt) > 0.1,
            $"the product rank at w=1 should be captured by the interference, but picked {product.ApexRt:F2}");

        var learned = Scorer().Repick(xics, realRt, 0.0, 0.3, libraryFragments: Library,
            traceCandidates: true, intensityExponent: 1.0);

        var dump = string.Join("\n", learned.CandidatePeaks!.OrderByDescending(c => c.Rank).Select(c =>
            $"apex {c.ApexRt:F2} coel {c.Coelution:F3} libcos {c.LibCosine:F3} rtPen {c.RtPenalty:F3} " +
            $"lnI {Math.Log(1.0 + c.ApexIntensity):F2} lda {c.Rank:F3}"));
        Assert.True(Math.Abs(learned.ApexRt - realRt) <= 0.05,
            $"the learned pick should choose the real peak at {realRt:F2}, but picked {learned.ApexRt:F2}\n{dump}");
    }

    /// <summary>Candidates must carry the raw apex intensity the learned model's ln_intensity term needs -
    /// the product term ln(1+I)^w alone is not enough (at w=0 it is a constant).</summary>
    [Fact]
    public void Candidates_expose_the_raw_evidence_the_learned_model_needs()
    {
        var r = Scorer().Repick(RealPeakVersusIntenseInterference(), 7.4, 0.0, 0.3,
            libraryFragments: Library, traceCandidates: true, intensityExponent: 0.0);

        Assert.NotNull(r.ChosenTerms);
        Assert.Equal(1.0, r.ChosenTerms!.IntensityWeight); // w=0 drops the product's intensity term...
        Assert.True(r.ChosenTerms.ApexIntensity > 0.0);    // ...but the raw intensity is still available
        Assert.All(r.CandidatePeaks!, c => Assert.True(c.ApexIntensity > 0.0));
    }

    /// <summary>The frozen weights are Osprey's, copied verbatim - pin them so an accidental edit fails a test
    /// rather than silently changing every pick. Source: Osprey.Scoring/PickLdaModel.cs (pwiz dd9e84581).</summary>
    [Fact]
    public void Frozen_weights_match_osprey()
    {
        Assert.Equal(
            new[] { 0.9933168416485256, 0.047052481253413006, 0.027130393118192445, 0.10184133676728513 },
            PickLdaModel.Stellar.Weights);
        Assert.Equal(
            new[] { 0.5348241578558818, 0.0041302671426268105, 0.3352868625222239, 0.7755828652613985 },
            PickLdaModel.Astral.Weights);
        Assert.Equal(new[] { "coelution", "ln_intensity", "rt_penalty", "median_polish" }, PickLdaModel.FeatureNames);
    }

    /// <summary>A zero scale must standardize to 0, not divide by zero - matching Osprey's <c>Z</c>.</summary>
    [Fact]
    public void Learned_score_is_finite_for_degenerate_inputs()
    {
        var score = PickLdaModel.Stellar.Score(0.0, 0.0, 0.0, 0.0);
        Assert.True(double.IsFinite(score));
    }
}
