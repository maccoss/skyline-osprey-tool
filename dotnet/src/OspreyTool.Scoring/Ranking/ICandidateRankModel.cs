using pwiz.Osprey.Core;

namespace OspreyTool.Scoring.Ranking;

/// <summary>
/// How a candidate peak's evidence is combined into the single number the pick ranks on. The detector
/// proposes candidate windows (<see cref="OspreyTool.Core.Detection.IPeakDetector"/>); the rank model decides
/// which of them is the peak.
///
/// Two ship, mirroring Osprey's own two arms (<c>OSPREY_PICK_LDA</c>):
/// <list type="bullet">
/// <item><see cref="PickLdaRankModel"/> (<c>lda</c>, the default) - Osprey's frozen learned linear pick:
/// a weighting of four standardized terms.</item>
/// <item><see cref="ProductRankModel"/> (<c>product</c>) - the legacy product form
/// <c>coelution * libCosine * exp(-dt^2/2sigma^2) * ln(1+I)^w</c>, i.e. <c>OSPREY_PICK_LDA=0</c>.</item>
/// </list>
/// </summary>
public interface ICandidateRankModel
{
    /// <summary>Stable id for the CLI (<c>--ranker</c>) and persisted settings.</summary>
    string Id { get; }

    /// <summary>Shown in the tool's Settings dropdown and the console banner.</summary>
    string DisplayName { get; }

    /// <summary>Higher is more peak-like. May be negative (the learned score is unbounded).</summary>
    double Score(CandidatePeak candidate);
}

/// <summary>
/// Osprey's learned linear pick (<see cref="PickLdaModel"/>) as a rank model: the four raw terms are read off
/// the candidate and scored with the frozen per-platform weights. Nothing is trained, no decoys are needed.
///
/// Two of the tool's knobs do not apply on this path, because the model consumes the RAW terms:
/// <c>--intensity-exp</c> (the model weights <c>ln(1+I)</c> itself) and <c>--lib-cosine</c> (the median-polish
/// cosine is always a weighted feature, never a multiplier). The library IS still needed - without it
/// <c>median_polish</c> falls back to the neutral 1.0 for every candidate, which merely adds a constant and
/// silently drops a real feature (weight 0.10 on Stellar, 0.78 on Astral).
/// </summary>
public sealed class PickLdaRankModel : ICandidateRankModel
{
    private readonly PickLdaModel _model;

    public PickLdaRankModel(PickLdaModel model) => _model = model;

    /// <summary>The model keyed to the config's resolution (unit -> Stellar, HRAM -> Astral).</summary>
    public static PickLdaRankModel ForConfig(OspreyConfig config) => new(PickLdaModel.ForConfig(config));

    public string Id => "lda";

    public string DisplayName => _model.DisplayName;

    /// <summary>The frozen model being applied - its weights and platform.</summary>
    public PickLdaModel Model => _model;

    public double Score(CandidatePeak c) =>
        _model.Score(c.Coelution, Math.Log(1.0 + Math.Max(0.0, c.ApexIntensity)), c.RtPenalty, c.LibCosine);
}

/// <summary>
/// The legacy product-form pick: <c>coelution * libCosine * exp(-dt^2 / 2*sigma^2) * ln(1 + apexIntensity)^w</c>.
/// Osprey's pick before the learned model became the default, and still its <c>OSPREY_PICK_LDA=0</c> arm -
/// kept so the two stay directly comparable.
/// </summary>
public sealed class ProductRankModel : ICandidateRankModel
{
    public static readonly ProductRankModel Instance = new();

    public string Id => "product";

    public string DisplayName => "Product of terms (legacy Osprey bestPeak rank)";

    // Operand order matches the original inline expression, so the arithmetic is bit-identical.
    public double Score(CandidatePeak c) => c.Coelution * c.LibCosine * c.RtPenalty * c.IntensityWeight;
}
