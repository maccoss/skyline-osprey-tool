using pwiz.Osprey.Core;
using pwiz.Osprey.Scoring;

namespace OspreyTool.Scoring;

/// <summary>
/// Result of scoring one candidate (target or decoy) at its best CWT peak: the 21-length PIN
/// feature vector (only the 12-feature subset populated - the rest stay 0) plus the chosen
/// peak bounds. Feeds a <c>PercolatorEntry</c> per run.
/// </summary>
public sealed class ScoredCandidate
{
    public ScoredCandidate(LibraryEntry candidate, XICPeakBounds? peakBounds, double[] features, bool hasPeak)
    {
        Candidate = candidate;
        PeakBounds = peakBounds;
        Features = features;
        HasPeak = hasPeak;
    }

    public LibraryEntry Candidate { get; }

    /// <summary>The best consensus CWT peak; null when none was detected.</summary>
    public XICPeakBounds? PeakBounds { get; }

    /// <summary>Length-21 PIN vector; indices outside the subset are 0 (see docs/osprey-api.md).</summary>
    public double[] Features { get; }

    public bool HasPeak { get; }

    /// <summary>fragment_coelution_sum (PIN index 0) - Osprey's primary co-elution score.</summary>
    public double CoelutionSum => Features[0];

    public static ScoredCandidate NoPeak(LibraryEntry candidate) =>
        new(candidate, null, new double[OspreyFeatureCalculators.FeatureCount], hasPeak: false);
}
