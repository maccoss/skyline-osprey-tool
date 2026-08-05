using System.Globalization;
using pwiz.Osprey.Core;

namespace OspreyTool.Scoring.Ranking;

/// <summary>
/// Osprey's learned linear peak-pick model: a <b>frozen</b> weighting of four standardized terms that
/// replaces the product-form candidate rank.
///
/// <code>
/// rank = w0*z(coelution) + w1*z(ln_intensity) + w2*z(rt_penalty) + w3*z(median_polish)
///        where z(x_i) = (x_i - mean_i) / scale_i
/// </code>
///
/// The weights are <b>not</b> learned here and there is nothing to train, no decoys to supply and no FDR
/// attached: they were fit offline in the Osprey repo from target/decoy per-candidate dumps
/// (<c>OSPREY_PICK_DUMP_CANDIDATES</c> -> <c>pick_lda_train.py</c>, see Osprey's
/// <c>docs/peak-model-training.md</c>) and copied into the source as constants. This class carries those
/// constants verbatim from <c>Osprey.Scoring/PickLdaModel.cs</c> (pwiz <c>dd9e84581</c>, default flipped on
/// in <c>#4484</c>).
///
/// They are copied rather than referenced because Osprey's <c>PickLdaModel</c> is <c>internal sealed</c> -
/// making it (or a factory for the resolution-keyed defaults) public is the upstream ask when the
/// <c>Osprey.Api</c> facade lands. Osprey's <c>OSPREY_PICK_LDA_MODEL</c> JSON override is a test hook and is
/// deliberately NOT wired up here: this tool always uses the hardcoded model.
///
/// One model per platform, keyed on resolution exactly as Osprey keys it - unit resolution (Stellar) and HRAM
/// (Astral) were trained separately and are not interchangeable.
/// </summary>
public sealed class PickLdaModel
{
    /// <summary>Feature order the weights apply to. Fixed: Osprey's weights are positional.</summary>
    public static readonly string[] FeatureNames =
    {
        "coelution", "ln_intensity", "rt_penalty", "median_polish",
    };

    private readonly double[] _weights;
    private readonly double[] _means;
    private readonly double[] _scales;

    private PickLdaModel(string id, string displayName, double[] weights, double[] means, double[] scales)
    {
        Id = id;
        DisplayName = displayName;
        _weights = weights;
        _means = means;
        _scales = scales;
    }

    public string Id { get; }

    public string DisplayName { get; }

    /// <summary>Unit-resolution (Thermo Stellar-trained) model - the one this tool's PRM path uses.
    /// Values verbatim from Osprey's <c>StellarModel</c> / <c>pick-model-stellar.json</c>.</summary>
    public static readonly PickLdaModel Stellar = new(
        "stellar", "Osprey learned pick (unit resolution / Stellar)",
        new[] { 0.9933168416485256, 0.047052481253413006, 0.027130393118192445, 0.10184133676728513 },
        new[] { 0.14931086687377143, 9.15749607304815, 0.9158212545538758, 0.8904037620854307 },
        new[] { 0.2074610054197197, 2.260347450504608, 0.07333900267211861, 0.08220791724094854 });

    /// <summary>HRAM (Thermo Astral-trained) model. Values verbatim from Osprey's <c>AstralModel</c> /
    /// <c>pick-model-astral.json</c>.</summary>
    public static readonly PickLdaModel Astral = new(
        "astral", "Osprey learned pick (HRAM / Astral)",
        new[] { 0.5348241578558818, 0.0041302671426268105, 0.3352868625222239, 0.7755828652613985 },
        new[] { 0.027393438120134818, 6.585876043601798, 0.939316453828307, 0.6880222328717774 },
        new[] { 0.11714825645722571, 3.9104476306002494, 0.05338768554968953, 0.1461117956225306 });

    /// <summary>
    /// The model for a scoring resolution, exactly as Osprey's <c>PickLdaModel.ForResolution</c> keys it:
    /// HRAM (MS1 features present) -> Astral, unit resolution -> Stellar.
    /// </summary>
    public static PickLdaModel ForResolution(bool hasMs1Features) => hasMs1Features ? Astral : Stellar;

    /// <summary>
    /// The model for a resolution mode. The split that matters is the fragment tolerance <b>unit</b>: a fixed
    /// m/z window (LIT, or a triple quad matching on <c>mz_match_tolerance</c>) is unit resolution and gets the
    /// Stellar weights; ppm / resolving-power tolerances (Orbitrap, TOF, FT-ICR, centroided) get Astral. Derive
    /// it from the document with <see cref="OspreyTool.Core.SkylineTransitionSettings.IsUnitResolution"/> +
    /// <see cref="OspreyConfigFactory.FromTransitionSettings"/> rather than guessing.
    ///
    /// <see cref="ResolutionMode.Auto"/> means Osprey would infer it from the raw spectra, which this tool
    /// cannot do from Skyline's XIC export - it resolves to Stellar, the tool's documented PRM target. Callers
    /// that know better should say so; picking Astral by mistake would weight <c>median_polish</c> 7.6x too
    /// heavily and <c>rt_penalty</c> 12x too heavily.
    /// </summary>
    public static PickLdaModel ForResolutionMode(ResolutionMode mode) =>
        mode == ResolutionMode.HRAM ? Astral : Stellar;

    /// <summary>The model for an Osprey config's resolution mode.</summary>
    public static PickLdaModel ForConfig(OspreyConfig config) => ForResolutionMode(config.ResolutionMode);

    /// <summary>Per-feature weight, index-aligned to <see cref="FeatureNames"/>.</summary>
    public IReadOnlyList<double> Weights => _weights;

    /// <summary>
    /// The learned rank score for one candidate from its four raw terms. A zero scale standardizes to 0 for
    /// that feature (a constant feature contributes nothing) rather than dividing by zero - matching Osprey.
    /// </summary>
    public double Score(double coelution, double lnIntensity, double rtPenalty, double medianPolish) =>
        _weights[0] * Z(coelution, 0)
        + _weights[1] * Z(lnIntensity, 1)
        + _weights[2] * Z(rtPenalty, 2)
        + _weights[3] * Z(medianPolish, 3);

    private double Z(double x, int i) => _scales[i] != 0.0 ? (x - _means[i]) / _scales[i] : 0.0;

    /// <summary>The model as one line, for the console banner.</summary>
    public string Describe()
    {
        var ci = CultureInfo.InvariantCulture;
        var terms = new string[FeatureNames.Length];
        for (var j = 0; j < FeatureNames.Length; j++)
        {
            terms[j] = $"{_weights[j].ToString("F3", ci)}*z({FeatureNames[j]})";
        }
        return string.Join(" + ", terms);
    }
}
