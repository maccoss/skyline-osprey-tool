namespace OspreyTool.Core;

/// <summary>
/// All extracted chromatograms for one precursor in one replicate (raw file). Verified
/// (docs/data-formats.md) that every fragment of a precursor shares one identical RT grid,
/// so the precursor is already co-aligned for CWT consensus / co-elution scoring.
/// </summary>
public sealed class PrecursorChromatograms
{
    public required string FileName { get; init; }

    public required string PeptideModifiedSequence { get; init; }

    public required int PrecursorCharge { get; init; }

    public required IReadOnlyList<XicData> Xics { get; init; }

    /// <summary>
    /// The fragment XICs only - the precursor isotope trace (<see cref="XicData.IsPrecursor"/>) excluded.
    /// Osprey's co-elution is fragment-to-fragment; the precursor is a separate (MS1) path, so scoring
    /// uses this, not <see cref="Xics"/>.
    /// </summary>
    public IReadOnlyList<XicData> FragmentXics => Xics.Where(x => !x.IsPrecursor).ToList();

    public PrecursorKey Key => new(PeptideModifiedSequence, PrecursorCharge);

    /// <summary>The shared retention-time grid (taken from the first fragment).</summary>
    public double[] RetentionTimes => Xics.Count > 0 ? Xics[0].RetentionTimes : Array.Empty<double>();

    public double RtStart => RetentionTimes.Length > 0 ? RetentionTimes[0] : double.NaN;

    public double RtEnd => RetentionTimes.Length > 0 ? RetentionTimes[^1] : double.NaN;

    /// <summary>True when every fragment shares the same RT grid length as the first.</summary>
    public bool GridIsConsistent => Xics.All(x => x.RetentionTimes.Length == RetentionTimes.Length);
}
