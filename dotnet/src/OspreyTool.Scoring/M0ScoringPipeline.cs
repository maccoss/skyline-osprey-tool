using OspreyTool.Core;
using pwiz.Osprey.Core;
using pwiz.Osprey.FDR;
using pwiz.Osprey.IO;
using pwiz.Osprey.Scoring;
using LibraryEntry = pwiz.Osprey.Core.LibraryEntry;

namespace OspreyTool.Scoring;

/// <summary>Per-replicate Percolator outcome.</summary>
public sealed class RunFdrSummary
{
    public required string FileName { get; init; }
    public required int Targets { get; init; }
    public required int Decoys { get; init; }
    public int TargetsAtQ01 { get; init; }
    public int TargetsAtQ05 { get; init; }
    public string? Error { get; init; }
}

/// <summary>Result of the M0 scoring run: join coverage, target/decoy separation, per-run FDR.</summary>
public sealed class M0ScoringResult
{
    public required int Groups { get; init; }
    public required int Matched { get; init; }
    public required int Unmatched { get; init; }
    public required int TargetsWithPeak { get; init; }
    public required int DecoysWithPeak { get; init; }
    public required IReadOnlyList<FeatureSeparation> Separations { get; init; }
    public required IReadOnlyList<RunFdrSummary> PerRun { get; init; }
}

/// <summary>
/// The M0 scoring pipeline: for each (precursor, replicate) group, score the target and its
/// decoy-XIC through Osprey, measure how well each feature separates target from decoy, then run
/// Osprey's Percolator per replicate for per-run q-values. Proves (or disproves) the M0 premise -
/// that decoy-XIC decoys separate from real targets - on the real data before any UI.
/// </summary>
public static class M0ScoringPipeline
{
    public static M0ScoringResult Run(
        IReadOnlyList<PrecursorChromatograms> chromatograms,
        OspreyLibrary library,
        OspreyConfig config,
        IReadOnlyList<int> featureIndices,
        IDecoyChromatogramGenerator decoyGenerator)
    {
        var scorer = new OspreyFeatureScorer(config);

        var perRunEntries = new Dictionary<string, List<PercolatorEntry>>();
        var targetValues = featureIndices.ToDictionary(i => i, _ => new List<double>());
        var decoyValues = featureIndices.ToDictionary(i => i, _ => new List<double>());

        var matched = 0;
        var unmatched = 0;
        var targetsWithPeak = 0;
        var decoysWithPeak = 0;
        uint entryId = 0;

        foreach (var group in chromatograms)
        {
            if (!library.TryGet(group.PeptideModifiedSequence, group.PrecursorCharge, out var candidate))
            {
                unmatched++;
                continue;
            }
            matched++;

            var expectedRt = candidate.RetentionTime;
            var targetScore = scorer.Score(candidate, group.Xics, expectedRt);
            var decoy = decoyGenerator.Generate(group);
            var decoyScore = scorer.Score(candidate, decoy.Xics, expectedRt);

            if (targetScore.HasPeak)
            {
                targetsWithPeak++;
            }
            if (decoyScore.HasPeak)
            {
                decoysWithPeak++;
            }

            foreach (var i in featureIndices)
            {
                targetValues[i].Add(targetScore.Features[i]);
                decoyValues[i].Add(decoyScore.Features[i]);
            }

            var entries = GetRun(perRunEntries, group.FileName);
            entries.Add(MakeEntry(group.FileName, candidate, targetScore, isDecoy: false, entryId++));
            entries.Add(MakeEntry(group.FileName, candidate, decoyScore, isDecoy: true, entryId++));
        }

        var separations = new List<FeatureSeparation>();
        foreach (var i in featureIndices)
        {
            separations.Add(FeatureSeparation.Compute(
                i, ParquetScoreCache.PIN_FEATURE_NAMES[i], targetValues[i], decoyValues[i]));
        }

        var perRun = new List<RunFdrSummary>();
        foreach (var run in perRunEntries.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            perRun.Add(RunPercolatorForFile(run.Key, run.Value, config));
        }

        return new M0ScoringResult
        {
            Groups = chromatograms.Count,
            Matched = matched,
            Unmatched = unmatched,
            TargetsWithPeak = targetsWithPeak,
            DecoysWithPeak = decoysWithPeak,
            Separations = separations,
            PerRun = perRun,
        };
    }

    private static List<PercolatorEntry> GetRun(Dictionary<string, List<PercolatorEntry>> runs, string fileName)
    {
        if (!runs.TryGetValue(fileName, out var list))
        {
            list = new List<PercolatorEntry>();
            runs[fileName] = list;
        }
        return list;
    }

    private static PercolatorEntry MakeEntry(
        string fileName, LibraryEntry candidate, ScoredCandidate scored, bool isDecoy, uint entryId) => new()
    {
        FileName = fileName,
        Peptide = isDecoy ? XicShuffleDecoyGenerator.DecoyPrefix + candidate.ModifiedSequence : candidate.ModifiedSequence,
        Charge = candidate.Charge,
        IsDecoy = isDecoy,
        EntryId = entryId,
        CoelutionSum = scored.CoelutionSum,
        Features = scored.Features, // 21-length; uncomputed slots are 0 (standardizer guards zero variance)
    };

    private static RunFdrSummary RunPercolatorForFile(string fileName, List<PercolatorEntry> entries, OspreyConfig config)
    {
        var targets = entries.Count(e => !e.IsDecoy);
        var decoys = entries.Count(e => e.IsDecoy);
        try
        {
            var percolatorConfig = new PercolatorConfig
            {
                FeatureInfos = OspreyFeatureCalculators.BuildFeatureInfos(ParquetScoreCache.PIN_FEATURE_NAMES),
            };
            var results = PercolatorFdr.RunPercolator(entries, percolatorConfig);

            var atQ01 = 0;
            var atQ05 = 0;
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].IsDecoy)
                {
                    continue;
                }
                var q = results.Entries[i].RunPrecursorQvalue;
                if (q <= 0.01)
                {
                    atQ01++;
                }
                if (q <= 0.05)
                {
                    atQ05++;
                }
            }

            return new RunFdrSummary
            {
                FileName = fileName,
                Targets = targets,
                Decoys = decoys,
                TargetsAtQ01 = atQ01,
                TargetsAtQ05 = atQ05,
            };
        }
        catch (Exception ex)
        {
            return new RunFdrSummary
            {
                FileName = fileName,
                Targets = targets,
                Decoys = decoys,
                Error = ex.Message,
            };
        }
    }
}
