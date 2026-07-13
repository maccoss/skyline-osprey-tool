namespace OspreyTool.Core;

/// <summary>
/// One predicted spectrum from the Carafe/Cadenza .blib (a Bibliospec RefSpectra row).
/// One entry per document precursor. Mirrors the fields Osprey's BlibLoader exposes; the
/// eventual pipeline will hand Osprey the real library, but M0 reads it directly to prove
/// the join and RT alignment.
/// </summary>
public sealed class LibraryEntry
{
    public required string PeptideModifiedSequence { get; init; }

    public required string PeptideSequence { get; init; }

    public required int PrecursorCharge { get; init; }

    public required double PrecursorMz { get; init; }

    /// <summary>Predicted retention time in minutes (Bibliospec RefSpectra.retentionTime).</summary>
    public double? PredictedRetentionTime { get; init; }

    public int NumPeaks { get; init; }

    public IReadOnlyList<LibraryFragment> Fragments { get; init; } = Array.Empty<LibraryFragment>();

    public PrecursorKey Key => new(PeptideModifiedSequence, PrecursorCharge);
}

/// <summary>A single predicted fragment peak: m/z and (typically normalized) intensity.</summary>
public readonly record struct LibraryFragment(double Mz, float Intensity);
