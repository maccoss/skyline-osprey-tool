using OspreyTool.Core;
using pwiz.Osprey.IO;
using LibraryEntry = pwiz.Osprey.Core.LibraryEntry;

namespace OspreyTool.Scoring;

/// <summary>
/// The Carafe/Cadenza library loaded through Osprey's own <see cref="BlibLoader"/> (so candidates
/// carry the fragment list the median-polish-cosine feature needs), indexed by
/// (ModifiedSequence, Charge) for the join to Skyline's chromatogram groups.
/// </summary>
public sealed class OspreyLibrary
{
    private readonly Dictionary<PrecursorKey, LibraryEntry> _byKey;

    private OspreyLibrary(IReadOnlyList<LibraryEntry> entries, Dictionary<PrecursorKey, LibraryEntry> byKey)
    {
        Entries = entries;
        _byKey = byKey;
    }

    public IReadOnlyList<LibraryEntry> Entries { get; }

    public int Count => Entries.Count;

    public static OspreyLibrary Load(string blibPath) => FromEntries(new BlibLoader().Load(blibPath));

    /// <summary>Builds an index over already-loaded entries (used by tests without a blib file).</summary>
    public static OspreyLibrary FromEntries(IReadOnlyList<LibraryEntry> entries)
    {
        var byKey = new Dictionary<PrecursorKey, LibraryEntry>();
        foreach (var entry in entries)
        {
            byKey[new PrecursorKey(entry.ModifiedSequence, entry.Charge)] = entry;
        }
        return new OspreyLibrary(entries, byKey);
    }

    public bool TryGet(string modifiedSequence, int charge, out LibraryEntry entry) =>
        _byKey.TryGetValue(new PrecursorKey(modifiedSequence, charge), out entry!);
}
