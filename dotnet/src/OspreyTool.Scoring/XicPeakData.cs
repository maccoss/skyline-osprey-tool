using pwiz.Osprey.Chromatography;
using pwiz.Osprey.Core;
using pwiz.Osprey.Scoring;

namespace OspreyTool.Scoring;

/// <summary>
/// Adapter that presents Skyline-extracted XICs plus a chosen CWT peak to Osprey's feature
/// calculators. Implements the Detailed tier (<see cref="IOspreyDetailedPeakData"/>); the
/// three MS1 / isotope members are null because unit-resolution Stellar PRM has no useful MS1
/// - and only the MS1 features (indices 13, 14) read them, none of our 12-feature subset.
/// </summary>
internal sealed class XicPeakData : IOspreyDetailedPeakData
{
    public XicPeakData(
        LibraryEntry candidate,
        XICPeakBounds peakBounds,
        double apexRetentionTime,
        double expectedRt,
        IReadOnlyList<XicData> xics)
    {
        Candidate = candidate;
        PeakBounds = peakBounds;
        ApexRetentionTime = apexRetentionTime;
        ExpectedRt = expectedRt;
        Xics = xics;
    }

    public LibraryEntry Candidate { get; }

    public XICPeakBounds PeakBounds { get; }

    public double ApexRetentionTime { get; }

    public double ExpectedRt { get; }

    public IReadOnlyList<XicData> Xics { get; }

    public XicData? Ms1PrecursorXic => null;

    public XicData? Ms1ReferenceXic => null;

    public double[]? ApexIsotopeEnvelope => null;
}
