namespace OspreyTool.Core;

/// <summary>
/// The join key shared by the Skyline document, the chromatogram export, and the
/// Carafe/Cadenza .blib. Verified (docs/data-formats.md) to match verbatim across all
/// three sources - same modified-sequence bracket format (e.g. "C[+57]LAVYQAGAR") and
/// the same precursor charge - so no normalization is needed.
/// </summary>
public readonly record struct PrecursorKey(string PeptideModifiedSequence, int PrecursorCharge)
{
    public override string ToString() => $"{PeptideModifiedSequence}/{PrecursorCharge}";
}
