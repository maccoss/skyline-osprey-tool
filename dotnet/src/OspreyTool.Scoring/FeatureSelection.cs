using System.Text;
using pwiz.Osprey.IO;

namespace OspreyTool.Scoring;

/// <summary>
/// The 21 PIN features grouped by the input each family needs (see docs/osprey-api.md). Verified
/// against Osprey source: every family is reachable from an external assembly - even xcorr, whose
/// cache is built through the public IResolutionStrategy.PreprocessWindowSpectra factory - given
/// the observed spectra. What gates them here is data availability, not Osprey internals.
/// </summary>
public static class OspreyFeatureFamilies
{
    /// <summary>Computable from Skyline XICs alone (+ RT + the median-polish byproduct). Always available.</summary>
    public static readonly int[] XicRtMedianPolish = { 0, 1, 2, 3, 4, 5, 11, 12, 15, 16, 19, 20 };

    /// <summary>Need only the observed apex MS2 spectrum (IOspreyApexSpectrumPeakData). No window state.</summary>
    public static readonly int[] ApexSpectrumMatch = { 7, 8, 9, 10 };

    /// <summary>Need the full isolation-window scan list + SetWindow (IOspreyApexSpectraPeakData): xcorr / SG.</summary>
    public static readonly int[] WindowXcorr = { 6, 17, 18 };

    /// <summary>Need HRAM MS1 (precursor XIC + a >=5-channel isotope envelope) - not available on unit-res PRM.</summary>
    public static readonly int[] Ms1 = { 13, 14 };
}

/// <summary>
/// Resolves which of the 21 Osprey features to compute given what inputs are actually available,
/// and produces user-facing warnings for the families that get dropped. The XIC family (12) is
/// always computed. The apex-match (4) and window/xcorr (3) families need observed MS2 spectra,
/// which come only from the raw files (as mzML). MS1 (2) is out on unit-resolution PRM. When the
/// spectra are missing the caller surfaces <see cref="Warnings"/> so the user knows those scores
/// were not used.
/// </summary>
public sealed class FeatureSelection
{
    private FeatureSelection(IReadOnlyList<int> featureIndices, IReadOnlyList<string> warnings)
    {
        FeatureIndices = featureIndices;
        Warnings = warnings;
    }

    public IReadOnlyList<int> FeatureIndices { get; }

    public IReadOnlyList<string> Warnings { get; }

    /// <param name="observedSpectraAvailable">Can we supply observed MS2 spectra (raw/mzML present)?</param>
    /// <param name="includeWindowXcorr">Whether to also compute the window/xcorr set (needs the full window scan list).</param>
    /// <param name="ms1Available">HRAM MS1 present (false on unit-resolution PRM).</param>
    public static FeatureSelection Resolve(
        bool observedSpectraAvailable,
        bool includeWindowXcorr = true,
        bool ms1Available = false)
    {
        var indices = new List<int>(OspreyFeatureFamilies.XicRtMedianPolish);
        var warnings = new List<string>();

        if (observedSpectraAvailable)
        {
            indices.AddRange(OspreyFeatureFamilies.ApexSpectrumMatch);
        }
        else
        {
            warnings.Add(Warn("apex-spectrum match", OspreyFeatureFamilies.ApexSpectrumMatch,
                "raw files are not accessible, so observed apex MS2 spectra are unavailable"));
        }

        if (observedSpectraAvailable && includeWindowXcorr)
        {
            indices.AddRange(OspreyFeatureFamilies.WindowXcorr);
        }
        else
        {
            var reason = observedSpectraAvailable
                ? "window-spectra (xcorr/SG) scoring is not enabled"
                : "raw files are not accessible, so observed window spectra are unavailable";
            warnings.Add(Warn("xcorr / SG", OspreyFeatureFamilies.WindowXcorr, reason));
        }

        if (!ms1Available)
        {
            warnings.Add(Warn("MS1", OspreyFeatureFamilies.Ms1,
                "unit-resolution PRM has no usable HRAM MS1 / isotope envelope"));
        }

        indices.Sort();
        return new FeatureSelection(indices, warnings);
    }

    private static string Warn(string family, int[] indices, string reason)
    {
        var names = new StringBuilder();
        for (var i = 0; i < indices.Length; i++)
        {
            if (i > 0)
            {
                names.Append(", ");
            }
            names.Append(ParquetScoreCache.PIN_FEATURE_NAMES[indices[i]]);
        }
        return $"WARNING: {indices.Length} {family} score(s) will NOT be used ({names}) - {reason}.";
    }
}
