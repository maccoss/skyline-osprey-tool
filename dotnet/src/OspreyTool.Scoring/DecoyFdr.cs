using OspreyTool.Core;
using pwiz.Osprey.Core;
using pwiz.Osprey.FDR;
using pwiz.Osprey.IO;
using pwiz.Osprey.Scoring;

namespace OspreyTool.Scoring;

/// <summary>One scored precursor (target or genuine reversed decoy) with its calibrated q-value.</summary>
public sealed class DecoyFdrRow
{
    public required string FileName { get; init; }
    public required string PeptideModifiedSequence { get; init; }
    public required int PrecursorCharge { get; init; }
    public required bool IsDecoy { get; init; }
    public double MinStartTime { get; init; }
    public double MaxEndTime { get; init; }
    public double ApexRt { get; init; }
    public double Coelution { get; init; }
    public double ExpectedRt { get; init; }

    /// <summary>Per-run precursor q-value from Osprey's Percolator over the target+decoy competition.</summary>
    public double Qvalue { get; set; } = double.NaN;
}

public sealed record RunDetections(string FileName, int Targets, int Decoys, int TargetsAtQ01, int TargetsAtQ05);

public sealed class DecoyFdrSummary
{
    public required int TargetGroups { get; init; }
    public required int DecoyGroups { get; init; }
    public required int TargetsScored { get; init; }
    public required int DecoysScored { get; init; }
    public required int TargetsAtQ01 { get; init; }
    public required int TargetsAtQ05 { get; init; }
    public required bool PercolatorOk { get; init; }
    public required IReadOnlyList<RunDetections> PerRun { get; init; }
    public required IReadOnlyList<DecoyFdrRow> Rows { get; init; }
}

/// <summary>
/// Calibrated FDR for the PRM re-pick using <b>genuine reversed-sequence decoys</b> (Osprey's real
/// null): the decoy shares the target's precursor m/z, so it was acquired in the same scheduled window,
/// and Skyline extracts real decoy chromatograms. Each target and its decoy are re-picked with Osprey's
/// bestPeak rank score and scored on the XIC/RT/median-polish feature subset; the full best-peak feature
/// vectors go into Osprey's <see cref="PercolatorFdr"/> as target vs decoy, which learns the discriminant
/// and emits per-run q-values from the target-decoy competition. Unlike the second-best null, a decoy can
/// out-score its target when the target is noise, so the q-values are properly calibrated.
/// </summary>
public static class DecoyFdrPipeline
{
    public static DecoyFdrSummary Run(
        IReadOnlyList<PrecursorChromatograms> groups,
        IReadOnlyDictionary<PrecursorKey, double> rtByTarget,
        IReadOnlyCollection<PrecursorKey> decoyKeys,
        IReadOnlyDictionary<PrecursorKey, double> rtByDecoy,
        double rtTolerance,
        double rtSigma,
        double minConsensusHeight = 0.0)
    {
        var scorer = new OspreyFeatureScorer(new OspreyConfig());
        var decoySet = new HashSet<PrecursorKey>(decoyKeys);

        var rows = new List<DecoyFdrRow>();
        var entries = new List<PercolatorEntry>();
        var entryRow = new Dictionary<uint, DecoyFdrRow>();
        uint entryId = 0;
        var targetGroups = 0;
        var decoyGroups = 0;

        foreach (var g in groups)
        {
            var key = new PrecursorKey(ModifiedSequence.Normalize(g.PeptideModifiedSequence), g.PrecursorCharge);
            var isDecoy = decoySet.Contains(key);
            if (isDecoy)
            {
                decoyGroups++;
            }
            else
            {
                targetGroups++;
            }

            double? expected = isDecoy
                ? (rtByDecoy.TryGetValue(key, out var dr) ? dr : null)
                : (rtByTarget.TryGetValue(key, out var tr) ? tr : null);

            var r = scorer.Repick(g.Xics, expected, rtTolerance, rtSigma, minConsensusHeight, computeFdrFeatures: true);
            if (!r.HasPeak || r.BestFeatures is null)
            {
                continue;
            }

            var row = new DecoyFdrRow
            {
                FileName = g.FileName,
                PeptideModifiedSequence = g.PeptideModifiedSequence,
                PrecursorCharge = g.PrecursorCharge,
                IsDecoy = isDecoy,
                MinStartTime = r.StartRt,
                MaxEndTime = r.EndRt,
                ApexRt = r.ApexRt,
                Coelution = r.Coelution,
                ExpectedRt = r.ExpectedRt,
            };
            rows.Add(row);

            var eid = entryId++;
            entryRow[eid] = row;
            entries.Add(new PercolatorEntry
            {
                FileName = g.FileName,
                Peptide = (isDecoy ? "DECOY_" : string.Empty) + g.PeptideModifiedSequence,
                Charge = (byte)g.PrecursorCharge,
                IsDecoy = isDecoy,
                EntryId = eid,
                CoelutionSum = r.BestFeatures[0],
                Features = r.BestFeatures,
            });
        }

        // Train ONE Percolator model pooled across replicates; it emits per-file (RunPrecursor) q-values.
        var percolatorOk = false;
        try
        {
            var cfg = new PercolatorConfig
            {
                FeatureInfos = OspreyFeatureCalculators.BuildFeatureInfos(ParquetScoreCache.PIN_FEATURE_NAMES),
            };
            var res = PercolatorFdr.RunPercolator(entries, cfg);
            for (var i = 0; i < entries.Count; i++)
            {
                if (entryRow.TryGetValue(entries[i].EntryId, out var row))
                {
                    row.Qvalue = res.Entries[i].RunPrecursorQvalue;
                }
            }
            percolatorOk = true;
        }
        catch
        {
            // leave q-values NaN
        }

        var perRun = rows
            .GroupBy(r => r.FileName)
            .Select(gr =>
            {
                var targets = gr.Where(x => !x.IsDecoy).ToList();
                return new RunDetections(
                    gr.Key,
                    targets.Count,
                    gr.Count(x => x.IsDecoy),
                    targets.Count(x => x.Qvalue <= 0.01),
                    targets.Count(x => x.Qvalue <= 0.05));
            })
            .OrderBy(x => x.FileName, StringComparer.Ordinal)
            .ToList();

        return new DecoyFdrSummary
        {
            TargetGroups = targetGroups,
            DecoyGroups = decoyGroups,
            TargetsScored = rows.Count(r => !r.IsDecoy),
            DecoysScored = rows.Count(r => r.IsDecoy),
            TargetsAtQ01 = rows.Count(r => !r.IsDecoy && r.Qvalue <= 0.01),
            TargetsAtQ05 = rows.Count(r => !r.IsDecoy && r.Qvalue <= 0.05),
            PercolatorOk = percolatorOk,
            PerRun = perRun,
            Rows = rows,
        };
    }
}
