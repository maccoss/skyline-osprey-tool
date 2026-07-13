namespace OspreyTool.Core.Detection;

/// <summary>
/// One candidate peak, as indices into the shared RT grid of the fragment XICs it was found in.
/// Indices (not RTs) because every consumer needs to slice the intensity arrays.
/// </summary>
public readonly record struct PeakWindow(int StartIndex, int ApexIndex, int EndIndex)
{
    /// <summary>Peak area, if the detector computes one. Osprey's feature calculators read it, so its own
    /// detector passes CWT's value through unchanged; a detector that leaves these null gets a value derived
    /// from the chromatogram instead, which is fine but not identical to what that detector might intend.</summary>
    public double? Area { get; init; }

    public double? SignalToNoise { get; init; }

    public double? ApexIntensity { get; init; }

    public int ScanCount => EndIndex - StartIndex + 1;
}

/// <summary>What a detector is given: the fragment XICs of ONE precursor in ONE run, on a shared RT grid.</summary>
public sealed class PeakDetectionInput
{
    /// <summary>Fragment (product-ion) XICs only - the precursor trace is already excluded. Every entry has
    /// the same length as <see cref="RetentionTimes"/>.</summary>
    public required IReadOnlyList<double[]> FragmentIntensities { get; init; }

    /// <summary>The shared RT grid, in minutes, ascending. For scheduled PRM this IS the acquisition window.</summary>
    public required double[] RetentionTimes { get; init; }

    /// <summary>The expected (predicted or scheduled) RT in minutes, when known. A detector MAY use this, but
    /// it must not be required: the tool scores and ranks candidates itself, including an RT term.</summary>
    public double? ExpectedRt { get; init; }

    /// <summary>Optional floor on a candidate's consensus height; 0 means no floor.</summary>
    public double MinConsensusHeight { get; init; }

    public int ScanCount => RetentionTimes.Length;
    public int FragmentCount => FragmentIntensities.Count;
}

/// <summary>
/// Finds CANDIDATE peaks in a precursor's fragment chromatograms. This is the pluggable seam: swap Osprey's
/// CWT detector for any other algorithm without touching scoring, FDR or reconciliation.
///
/// Contract:
/// - Return every plausible candidate, not just the best one. The tool RANKS them (co-elution x library
///   cosine x RT prior x intensity) and reconciliation needs the alternatives to snap to, so a detector that
///   pre-selects a single winner degrades both.
/// - Order is not significant, and duplicate windows are tolerated (the tool de-duplicates for display).
/// - Return an empty list when there is nothing to find. Do not throw for thin data (fewer than 2 fragments,
///   too few scans) - the caller guards that.
/// - Must be thread-safe / re-entrant: the pipeline scores precursors in parallel.
///
/// Implementations need NO Osprey dependency - this interface lives in OspreyTool.Core and speaks only in
/// arrays. See <see cref="LocalMaximaPeakDetector"/> for a minimal one.
/// </summary>
public interface IPeakDetector
{
    /// <summary>Stable identifier used by the CLI (--detector) and persisted settings, e.g. "osprey-cwt".</summary>
    string Id { get; }

    /// <summary>Human-readable name for the tool's Settings dropdown, e.g. "Osprey CWT (default)".</summary>
    string DisplayName { get; }

    IReadOnlyList<PeakWindow> Detect(PeakDetectionInput input);
}
