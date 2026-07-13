using System.Globalization;
using System.Text;

namespace OspreyTool.Core;

/// <summary>
/// The M0 de-risking sanity check: join the chromatogram export to the .blib on
/// (PeptideModifiedSequence, PrecursorCharge) and confirm the two premises the whole tool
/// rests on - the join is clean, and each precursor's exported RT window brackets the
/// library's predicted RT. No Osprey, no decoys; runs on the real target-only inputs.
/// </summary>
public sealed class M0Report
{
    public int LibraryEntries { get; private init; }

    public int Replicates { get; private init; }

    public int PrecursorReplicateGroups { get; private init; }

    /// <summary>Groups whose (peptide, charge) key exists in the library.</summary>
    public int GroupsMatchedToLibrary { get; private init; }

    /// <summary>Groups with no matching library entry (expect 0 for a clean join).</summary>
    public int GroupsUnmatched { get; private init; }

    /// <summary>Library keys that never appear in any replicate's chromatograms.</summary>
    public int LibraryEntriesWithoutXics { get; private init; }

    /// <summary>Matched groups whose library entry carries a predicted RT.</summary>
    public int GroupsWithPredictedRt { get; private init; }

    /// <summary>Matched groups whose predicted RT falls inside the exported RT window.</summary>
    public int GroupsPredictedRtInWindow { get; private init; }

    /// <summary>Groups where fragments disagree on RT-grid length (expect 0).</summary>
    public int GroupsInconsistentGrid { get; private init; }

    public IReadOnlyDictionary<string, int> GroupsPerReplicate { get; private init; } =
        new Dictionary<string, int>();

    public double RtInWindowFraction =>
        GroupsWithPredictedRt == 0 ? double.NaN : (double)GroupsPredictedRtInWindow / GroupsWithPredictedRt;

    public static M0Report Build(
        IReadOnlyList<PrecursorChromatograms> chromatograms,
        IReadOnlyList<LibraryEntry> library)
    {
        var libByKey = new Dictionary<PrecursorKey, LibraryEntry>();
        foreach (var entry in library)
        {
            libByKey[entry.Key] = entry;
        }

        var seenLibKeys = new HashSet<PrecursorKey>();
        var perReplicate = new Dictionary<string, int>();

        var matched = 0;
        var unmatched = 0;
        var withRt = 0;
        var rtInWindow = 0;
        var inconsistentGrid = 0;

        foreach (var group in chromatograms)
        {
            perReplicate[group.FileName] = perReplicate.GetValueOrDefault(group.FileName) + 1;

            if (!group.GridIsConsistent)
            {
                inconsistentGrid++;
            }

            if (libByKey.TryGetValue(group.Key, out var entry))
            {
                matched++;
                seenLibKeys.Add(group.Key);
                if (entry.PredictedRetentionTime is { } rt)
                {
                    withRt++;
                    if (rt >= group.RtStart && rt <= group.RtEnd)
                    {
                        rtInWindow++;
                    }
                }
            }
            else
            {
                unmatched++;
            }
        }

        return new M0Report
        {
            LibraryEntries = library.Count,
            Replicates = perReplicate.Count,
            PrecursorReplicateGroups = chromatograms.Count,
            GroupsMatchedToLibrary = matched,
            GroupsUnmatched = unmatched,
            LibraryEntriesWithoutXics = libByKey.Count - seenLibKeys.Count,
            GroupsWithPredictedRt = withRt,
            GroupsPredictedRtInWindow = rtInWindow,
            GroupsInconsistentGrid = inconsistentGrid,
            GroupsPerReplicate = perReplicate,
        };
    }

    public string Format()
    {
        var sb = new StringBuilder();
        var c = CultureInfo.InvariantCulture;
        sb.AppendLine("M0 join / RT-alignment sanity report");
        sb.AppendLine("=====================================");
        sb.AppendLine($"Library entries (RefSpectra) : {LibraryEntries}");
        sb.AppendLine($"Replicates                   : {Replicates}");
        sb.AppendLine($"Precursor x replicate groups : {PrecursorReplicateGroups}");
        sb.AppendLine($"  matched to library         : {GroupsMatchedToLibrary}");
        sb.AppendLine($"  unmatched (expect 0)       : {GroupsUnmatched}");
        sb.AppendLine($"Library keys without XICs    : {LibraryEntriesWithoutXics}");
        sb.AppendLine($"Inconsistent RT grid (exp 0) : {GroupsInconsistentGrid}");
        sb.AppendLine($"Predicted-RT-in-window       : {GroupsPredictedRtInWindow}/{GroupsWithPredictedRt} " +
            $"({RtInWindowFraction.ToString("P1", c)})");
        sb.AppendLine();
        sb.AppendLine("Groups per replicate:");
        foreach (var kv in GroupsPerReplicate.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"  {kv.Key,-48} {kv.Value}");
        }
        return sb.ToString();
    }
}
