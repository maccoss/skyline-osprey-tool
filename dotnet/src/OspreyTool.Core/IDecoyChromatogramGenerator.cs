namespace OspreyTool.Core;

/// <summary>
/// Produces a decoy precursor's chromatograms from a target's observed chromatograms. This
/// is the "decoy-XIC" family: decoys are built by transforming the already-extracted Skyline
/// XICs (no Skyline round-trip, no decoy fragment m/z), so a decoy is a deliberately
/// non-co-eluting arrangement of real traces. Kept behind an interface because it is one
/// option among several (the others - round-tripping decoy transitions through Skyline, or
/// porting Osprey's sequence-based DecoyGenerator - are not decoy-XIC methods).
/// </summary>
public interface IDecoyChromatogramGenerator
{
    /// <summary>Stable identifier of the method, recorded in output/provenance.</summary>
    string MethodName { get; }

    /// <summary>Builds the decoy chromatograms for one target (precursor, replicate) group.</summary>
    PrecursorChromatograms Generate(PrecursorChromatograms target);
}
