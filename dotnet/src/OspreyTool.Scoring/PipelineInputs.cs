using System.Globalization;
using OspreyTool.Core;

namespace OspreyTool.Scoring;

/// <summary>
/// Loads every engine input - chromatograms, expected (target) RTs, the decoy map, and library fragments -
/// from the file paths a Skyline export produces, so the CLI and the WPF app share one loader. Expected RTs
/// come from a document RT export (<paramref name="rtCsvPath"/>: ModifiedSequence + PrecursorCharge +
/// ExplicitRetentionTime) when given, else from the blib's predicted RTs.
/// </summary>
public sealed class PipelineInputs
{
    public required IReadOnlyList<PrecursorChromatograms> Groups { get; init; }
    public required IReadOnlyDictionary<PrecursorKey, double> RtByTarget { get; init; }
    public required HashSet<PrecursorKey> DecoyKeys { get; init; }
    public required Dictionary<PrecursorKey, double> RtByDecoy { get; init; }
    public IReadOnlyDictionary<PrecursorKey, IReadOnlyList<LibraryFragment>>? Fragments { get; init; }

    /// <summary>Bundle pre-gathered pieces (e.g. exported from a live Skyline document over RPC) - no decoys
    /// (FDR-only), fragments already keyed by precursor.</summary>
    public static PipelineInputs FromParts(
        IReadOnlyList<PrecursorChromatograms> groups,
        IReadOnlyDictionary<PrecursorKey, double> rtByTarget,
        IReadOnlyDictionary<PrecursorKey, IReadOnlyList<LibraryFragment>>? fragments)
        => new()
        {
            Groups = groups,
            RtByTarget = rtByTarget,
            DecoyKeys = new HashSet<PrecursorKey>(),
            RtByDecoy = new Dictionary<PrecursorKey, double>(),
            Fragments = fragments,
        };

    public static PipelineInputs Load(
        string xicsPath, string blibPath, string? decoysPath, string? rtCsvPath, bool loadFragments)
    {
        var ci = CultureInfo.InvariantCulture;
        var groups = ChromatogramTsvReader.ReadGroups(xicsPath);

        IReadOnlyDictionary<PrecursorKey, double> rtByTarget = rtCsvPath is not null
            ? LoadRtCsv(rtCsvPath, ci)
            : new BlibReader(blibPath).ReadRetentionTimes();

        var decoyKeys = new HashSet<PrecursorKey>();
        var rtByDecoy = new Dictionary<PrecursorKey, double>();
        if (decoysPath is not null)
        {
            (decoyKeys, rtByDecoy) = LoadDecoyMap(decoysPath, groups, ci);
        }

        IReadOnlyDictionary<PrecursorKey, IReadOnlyList<LibraryFragment>>? frags = null;
        if (loadFragments)
        {
            var byPrec = new Dictionary<PrecursorKey, IReadOnlyList<LibraryFragment>>();
            foreach (var e in new BlibReader(blibPath).ReadEntries(includeFragments: true))
            {
                byPrec[new PrecursorKey(ModifiedSequence.Normalize(e.PeptideModifiedSequence), e.PrecursorCharge)] = e.Fragments;
            }
            frags = byPrec;
        }

        return new PipelineInputs
        {
            Groups = groups,
            RtByTarget = rtByTarget,
            DecoyKeys = decoyKeys,
            RtByDecoy = rtByDecoy,
            Fragments = frags,
        };
    }

    private static Dictionary<PrecursorKey, double> LoadRtCsv(string path, CultureInfo ci)
    {
        var map = new Dictionary<PrecursorKey, double>();
        using var reader = new StreamReader(path);
        var header = (reader.ReadLine() ?? string.Empty).Split(',');
        int Col(params string[] names) => Array.FindIndex(header,
            h => names.Any(n => h.Trim().Equals(n, StringComparison.OrdinalIgnoreCase)));
        var iSeq = Col("ModifiedSequence", "PeptideModifiedSequence");
        var iCharge = Col("PrecursorCharge");
        var iRt = Col("ExplicitRetentionTime", "PredictedResultRetentionTime", "BestRetentionTime");
        if (iSeq < 0 || iCharge < 0 || iRt < 0)
        {
            throw new InvalidOperationException(
                "RT CSV must have ModifiedSequence, PrecursorCharge, and ExplicitRetentionTime columns.");
        }
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var f = line.Split(',');
            if (f.Length <= Math.Max(iSeq, Math.Max(iCharge, iRt)))
            {
                continue;
            }
            if (int.TryParse(f[iCharge], out var z) && double.TryParse(f[iRt], NumberStyles.Float, ci, out var rt))
            {
                map[new PrecursorKey(ModifiedSequence.Normalize(f[iSeq]), z)] = rt;
            }
        }
        return map;
    }

    private static (HashSet<PrecursorKey>, Dictionary<PrecursorKey, double>) LoadDecoyMap(
        string decoysPath, IReadOnlyList<PrecursorChromatograms> groups, CultureInfo ci)
    {
        var decoyRtByUnmod = new Dictionary<(string, int), double>();
        using (var reader = new StreamReader(decoysPath))
        {
            var header = (reader.ReadLine() ?? string.Empty).Split('\t');
            int Col(string name) => Array.FindIndex(header, h => h.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
            var iSeq = Col("Peptide Modified Sequence");
            var iCharge = Col("Precursor Charge");
            var iRt = Col("Explicit Retention Time");
            if (iSeq < 0 || iCharge < 0 || iRt < 0)
            {
                throw new InvalidOperationException(
                    "decoys.tsv must have 'Peptide Modified Sequence', 'Precursor Charge', 'Explicit Retention Time' columns.");
            }
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var f = line.Split('\t');
                if (f.Length <= Math.Max(iSeq, Math.Max(iCharge, iRt)))
                {
                    continue;
                }
                if (int.TryParse(f[iCharge], out var z) && double.TryParse(f[iRt], NumberStyles.Float, ci, out var rt))
                {
                    decoyRtByUnmod[(ModifiedSequence.StripMods(f[iSeq]), z)] = rt;
                }
            }
        }

        var decoyKeys = new HashSet<PrecursorKey>();
        var rtByDecoy = new Dictionary<PrecursorKey, double>();
        foreach (var g in groups)
        {
            if (decoyRtByUnmod.TryGetValue((ModifiedSequence.StripMods(g.PeptideModifiedSequence), g.PrecursorCharge), out var rt))
            {
                var key = new PrecursorKey(ModifiedSequence.Normalize(g.PeptideModifiedSequence), g.PrecursorCharge);
                decoyKeys.Add(key);
                rtByDecoy[key] = rt;
            }
        }
        return (decoyKeys, rtByDecoy);
    }
}
