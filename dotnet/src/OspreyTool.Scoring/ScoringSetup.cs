using OspreyTool.Core;
using OspreyTool.Scoring.Ranking;
using pwiz.Osprey.Core;

namespace OspreyTool.Scoring;

/// <summary>
/// The resolution-dependent scoring choices, resolved once per run: the Osprey config (tolerances +
/// resolution mode) and the candidate rank model that follows from it.
/// <see cref="ResolutionSource"/> says where the answer came from, so a run never silently assumes.
/// </summary>
public sealed record ScoringSetup(OspreyConfig Osprey, ICandidateRankModel Ranker, string ResolutionSource);

/// <summary>
/// Resolves <see cref="ScoringSetup"/> from the Skyline document.
///
/// This matters because Osprey ships <b>one frozen peak-pick model per platform</b> and they are not
/// interchangeable (<see cref="PickLdaModel"/>). The split is the fragment-tolerance <b>unit</b>, not the
/// instrument's name:
/// <list type="bullet">
/// <item>a fixed <b>m/z</b> window - a LIT (<c>qit</c>/<c>ion_trap</c>, e.g. Stellar), or a <b>triple quad</b>
/// matching on <c>mz_match_tolerance</c> - is unit resolution -> the <b>Stellar</b> weights;</item>
/// <item>a ppm / resolving-power tolerance (<c>orbitrap</c>, <c>tof</c>, <c>ft_icr</c>, <c>centroided</c>) is
/// HRAM -> the <b>Astral</b> weights.</item>
/// </list>
/// </summary>
public static class ScoringSetupFactory
{
    /// <summary>Explicit override values accepted for <c>resolution</c>.</summary>
    public const string UnitResolution = "unit";
    public const string Hram = "hram";

    /// <summary>
    /// <paramref name="skylineDocumentPath"/> is the authority (its transition settings say which analyzer
    /// extracted the chromatograms). <paramref name="resolutionOverride"/> (<c>"unit"</c>/<c>"hram"</c>) wins
    /// when supplied. With neither, unit resolution is assumed - the tool's documented LIT/QQQ PRM target -
    /// and <see cref="ScoringSetup.ResolutionSource"/> says so, so it is visible rather than silent.
    /// </summary>
    public static ScoringSetup Resolve(
        string? skylineDocumentPath, string? resolutionOverride, string rankerId)
    {
        var (config, source) = ResolveConfig(skylineDocumentPath, resolutionOverride);
        var ranker = rankerId == ProductRankModel.Instance.Id
            ? ProductRankModel.Instance
            : (ICandidateRankModel)PickLdaRankModel.ForConfig(config);
        return new ScoringSetup(config, ranker, source);
    }

    private static (OspreyConfig Config, string Source) ResolveConfig(string? skyPath, string? resolutionOverride)
    {
        switch (resolutionOverride)
        {
            case UnitResolution:
                return (OspreyFeatureScorer.UnitResolutionConfig(), "--resolution unit");
            case Hram:
                return (OspreyFeatureScorer.HramConfig(), "--resolution hram");
        }

        if (!string.IsNullOrEmpty(skyPath))
        {
            var settings = SkylineTransitionSettings.FromDocument(skyPath);
            var analyzer = string.IsNullOrEmpty(settings.ProductMassAnalyzer)
                ? $"no full-scan product analyzer, m/z match tolerance {Fmt(settings.MzMatchTolerance)}"
                : $"product analyzer '{settings.ProductMassAnalyzer}'";
            var source = $"{Path.GetFileName(skyPath)} ({analyzer}) -> " +
                $"{(settings.IsUnitResolution ? "unit resolution" : "HRAM")}";
            return (OspreyConfigFactory.FromTransitionSettings(settings), source);
        }

        return (OspreyFeatureScorer.UnitResolutionConfig(),
            "assumed unit resolution (no --sky or --resolution given; pass --sky <document.sky> to read it)");
    }

    private static string Fmt(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
