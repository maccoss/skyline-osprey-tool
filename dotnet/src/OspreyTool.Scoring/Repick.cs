using OspreyTool.Core;
using pwiz.Osprey.Core;
using pwiz.Osprey.FDR;
using pwiz.Osprey.IO;
using pwiz.Osprey.Scoring;

namespace OspreyTool.Scoring;

/// <summary>
/// One CWT candidate peak with the full breakdown of its pick score - the data behind the tool's
/// "Candidate Peaks" panel (analogous to Skyline's View &gt; Live Reports &gt; Candidate Peaks). The rank is
/// the product of the individual terms: <c>Coelution * LibCosine * RtPenalty * IntensityWeight</c>.
/// </summary>
public sealed class CandidatePeak
{
    public required double StartRt { get; init; }
    public required double ApexRt { get; init; }
    public required double EndRt { get; init; }
    public required double Coelution { get; init; }
    public required double LibCosine { get; init; }
    public required double RtResidual { get; init; }
    public required double RtPenalty { get; init; }
    public required double IntensityWeight { get; init; }
    public required double Rank { get; init; }
    /// <summary>This candidate was selected as the best peak.</summary>
    public bool Chosen { get; set; }
    /// <summary>This candidate was selected as the (non-overlapping) second-best null.</summary>
    public bool SecondBest { get; set; }
}

/// <summary>The re-picked peak for one precursor (RT boundaries + why it was chosen + the second-best null).</summary>
public sealed class RepickResult
{
    public bool HasPeak { get; init; }
    public double StartRt { get; init; }
    public double EndRt { get; init; }
    public double ApexRt { get; init; }

    /// <summary>Mean pairwise fragment co-elution correlation (the DIA-NN score) at the chosen peak.</summary>
    public double Coelution { get; init; }

    /// <summary>The full bestPeak rank score of the chosen peak: coelution * libCosine * exp(-dt^2/2sigma^2)
    /// * ln(1+apex_intensity). The tool's overall peak-quality estimate - used to weight the cross-run
    /// consensus so confident, spectrally-matching, intense picks anchor the RT (not a majority of blanks).</summary>
    public double RankScore { get; init; }

    /// <summary>Whether a runner-up CWT candidate peak exists (the mProphet-style null example).</summary>
    public bool HasSecondBest { get; init; }

    /// <summary>Co-elution of the runner-up candidate peak; NaN when there is only one candidate.</summary>
    public double SecondBestCoelution { get; init; }

    public double ExpectedRt { get; init; }

    /// <summary>|chosen apex RT - expected (library) RT|.</summary>
    public double RtResidual { get; init; }

    public int CandidateCount { get; init; }

    /// <summary>Candidates that passed the apex-acceptance RT gate.</summary>
    public int ScoredCount { get; init; }

    /// <summary>Rank of the chosen peak in CWT consensus order (0 = CWT's top peak).</summary>
    public int ChosenCwtRank { get; init; }

    /// <summary>Fewer than 2 fragment XICs after excluding the precursor - not scorable.</summary>
    public bool TooFew { get; init; }

    /// <summary>Full feature vector (21-length) of the chosen best peak; null unless FDR features were requested.</summary>
    public double[]? BestFeatures { get; init; }

    /// <summary>Full feature vector (21-length) of the non-overlapping second-best peak (the null); null if none.</summary>
    public double[]? SecondBestFeatures { get; init; }

    /// <summary>
    /// Every CWT consensus peak (pre-RT-gate) as an Osprey <see cref="pwiz.Osprey.Core.CwtCandidate"/>,
    /// for inter-run reconciliation to snap a peak to the consensus RT. Null unless candidates were
    /// retained; present even on gate-rejected results so a missed run can still be rescued.
    /// </summary>
    public IReadOnlyList<pwiz.Osprey.Core.CwtCandidate>? Candidates { get; init; }

    /// <summary>Per-candidate score breakdown (for the Candidate Peaks diagnostic); null unless traced.</summary>
    public IReadOnlyList<CandidatePeak>? CandidatePeaks { get; init; }

    public static RepickResult NoCandidates() => new() { HasPeak = false, CandidateCount = 0 };

    public static RepickResult GateRejected(int candidateCount) =>
        new() { HasPeak = false, CandidateCount = candidateCount };

    public static RepickResult TooFewFragments() => new() { HasPeak = false, CandidateCount = 0, TooFew = true };
}

/// <summary>One row for a Skyline `--import-peak-boundaries` file (plus diagnostics + q-value).</summary>
public sealed class RepickRow
{
    public required string FileName { get; init; }
    public required string PeptideModifiedSequence { get; init; }
    public required int PrecursorCharge { get; init; }
    public required double MinStartTime { get; init; }
    public required double MaxEndTime { get; init; }
    public double ApexRt { get; init; }
    public double Coelution { get; init; }
    public double SecondBestCoelution { get; init; }
    public double ExpectedRt { get; init; }
    public double RtResidual { get; init; }
    public int Candidates { get; init; }
    public int CwtRank { get; init; }

    /// <summary>Full feature vectors of the best/second-best peaks (for the Percolator FDR); not serialized.</summary>
    public double[]? BestFeatures { get; init; }
    public double[]? SecondBestFeatures { get; init; }

    /// <summary>Second-best-peak q-value from the single co-elution discriminant; set after the per-replicate FDR pass.</summary>
    public double Qvalue { get; set; } = double.NaN;

    /// <summary>Second-best-peak q-value from the full multi-feature Percolator model; set after the per-replicate FDR pass.</summary>
    public double QvaluePercolator { get; set; } = double.NaN;
}

public sealed class RepickSummary
{
    public required int Groups { get; init; }
    public required int Repicked { get; init; }
    public required int NoCwtPeak { get; init; }
    public required int GateRejected { get; init; }
    /// <summary>Skipped: &lt;2 fragment XICs after excluding the precursor (kept Skyline's pick).</summary>
    public required int TooFewFragments { get; init; }
    public required int NoRtMatch { get; init; }
    public required int RankOverrodeCwt { get; init; }
    /// <summary>Re-picked but only one candidate peak, so no second-best null example.</summary>
    public required int NoSecondBest { get; init; }
    public required int TargetsAtQ01 { get; init; }
    public required int TargetsAtQ05 { get; init; }
    /// <summary>Detections from the full multi-feature Percolator model (A).</summary>
    public int PercolatorTargetsAtQ01 { get; init; }
    public int PercolatorTargetsAtQ05 { get; init; }
    /// <summary>Replicates whose Percolator run failed (too few examples / degenerate); those rows keep NaN q.</summary>
    public int PercolatorFailedRuns { get; init; }
    public required IReadOnlyList<RepickRow> Rows { get; init; }
}

/// <summary>
/// Re-picks peaks with Osprey's bestPeak rank score (CWT candidates ranked by
/// <c>coelution * exp(-dt^2 / 2*sigma^2) * ln(1 + apex_intensity)</c> with the apex-acceptance RT gate,
/// using the library predicted RT), then estimates confidence with a **second-best-peak** null: the
/// runner-up candidate in each target's own chromatogram is the decoy, and target-decoy q-values are
/// computed per replicate on the co-elution discriminant. Target-only - no decoys, no re-extraction.
/// </summary>
public static class RepickPipeline
{
    public static RepickSummary Run(
        IReadOnlyList<PrecursorChromatograms> groups,
        IReadOnlyDictionary<PrecursorKey, double> rtByPrecursor,
        double rtTolerance,
        double rtSigma,
        double minConsensusHeight = 0.0)
    {
        var scorer = new OspreyFeatureScorer(new OspreyConfig());
        var rows = new List<RepickRow>();
        var noCwt = 0;
        var gateRejected = 0;
        var tooFew = 0;
        var noRt = 0;
        var overrode = 0;
        var noSecond = 0;

        foreach (var group in groups)
        {
            var rtKey = new PrecursorKey(
                ModifiedSequence.Normalize(group.PeptideModifiedSequence), group.PrecursorCharge);
            double? expectedRt = rtByPrecursor.TryGetValue(rtKey, out var rt) ? rt : null;
            if (expectedRt is null)
            {
                noRt++;
            }

            var r = scorer.Repick(group.Xics, expectedRt, rtTolerance, rtSigma, minConsensusHeight, computeFdrFeatures: true);
            if (!r.HasPeak)
            {
                if (r.TooFew)
                {
                    tooFew++;
                }
                else if (r.CandidateCount == 0)
                {
                    noCwt++;
                }
                else
                {
                    gateRejected++;
                }
                continue;
            }
            if (r.ChosenCwtRank > 0)
            {
                overrode++;
            }
            if (!r.HasSecondBest)
            {
                noSecond++;
            }
            rows.Add(new RepickRow
            {
                FileName = group.FileName,
                PeptideModifiedSequence = group.PeptideModifiedSequence,
                PrecursorCharge = group.PrecursorCharge,
                MinStartTime = r.StartRt,
                MaxEndTime = r.EndRt,
                ApexRt = r.ApexRt,
                Coelution = r.Coelution,
                SecondBestCoelution = r.SecondBestCoelution,
                ExpectedRt = r.ExpectedRt,
                RtResidual = r.RtResidual,
                Candidates = r.CandidateCount,
                CwtRank = r.ChosenCwtRank,
                BestFeatures = r.BestFeatures,
                SecondBestFeatures = r.SecondBestFeatures,
            });
        }

        // (B) Second-best-peak FDR per replicate on the single co-elution discriminant:
        // targets = best-peak co-elution, null = non-overlapping second-best co-elution. Per-replicate is
        // fine here - it is a plain target-decoy count, not a trained model.
        foreach (var run in rows.GroupBy(r => r.FileName))
        {
            var list = run.ToList();
            var targetScores = list.Select(r => r.Coelution).ToList();
            var nullScores = list
                .Where(r => !double.IsNaN(r.SecondBestCoelution))
                .Select(r => r.SecondBestCoelution)
                .ToList();
            var q = SecondBestFdr.QValues(targetScores, nullScores);
            for (var i = 0; i < list.Count; i++)
            {
                list[i].Qvalue = q[i];
            }
        }

        // (A) Multi-feature Percolator model on the full best-vs-second-best feature vectors. Train ONE
        // model pooled across every replicate (so the SVM sees ~all targets + nulls, not ~300 per run),
        // and let Percolator emit per-file (RunPrecursor) q-values - this is how Osprey's own pipeline runs
        // and it removes the per-replicate training instability.
        var percolatorFailed = RunPercolatorSecondBest(rows) ? 0 : rows.Select(r => r.FileName).Distinct().Count();

        return new RepickSummary
        {
            Groups = groups.Count,
            Repicked = rows.Count,
            NoCwtPeak = noCwt,
            GateRejected = gateRejected,
            TooFewFragments = tooFew,
            NoRtMatch = noRt,
            RankOverrodeCwt = overrode,
            NoSecondBest = noSecond,
            TargetsAtQ01 = rows.Count(r => r.Qvalue <= 0.01),
            TargetsAtQ05 = rows.Count(r => r.Qvalue <= 0.05),
            PercolatorTargetsAtQ01 = rows.Count(r => r.QvaluePercolator <= 0.01),
            PercolatorTargetsAtQ05 = rows.Count(r => r.QvaluePercolator <= 0.05),
            PercolatorFailedRuns = percolatorFailed,
            Rows = rows,
        };
    }

    /// <summary>
    /// Runs Osprey's Percolator over one replicate using the second-best peak as the decoy: each row's
    /// best-peak feature vector is a target example and its (non-overlapping) second-best feature vector is
    /// a decoy example. Writes the learned q-value onto each row's <see cref="RepickRow.QvaluePercolator"/>.
    /// Returns false (and leaves q at NaN) if the run has too few examples or Percolator throws.
    /// </summary>
    private static bool RunPercolatorSecondBest(List<RepickRow> rows)
    {
        var entries = new List<PercolatorEntry>(rows.Count * 2);
        var targetEntryRow = new Dictionary<uint, RepickRow>();
        uint entryId = 0;

        foreach (var row in rows)
        {
            if (row.BestFeatures is null)
            {
                continue;
            }
            var targetId = entryId++;
            targetEntryRow[targetId] = row;
            entries.Add(MakeEntry(row, row.BestFeatures, isDecoy: false, targetId));
            if (row.SecondBestFeatures is not null)
            {
                entries.Add(MakeEntry(row, row.SecondBestFeatures, isDecoy: true, entryId++));
            }
        }

        var targets = entries.Count(e => !e.IsDecoy);
        var decoys = entries.Count(e => e.IsDecoy);
        if (targets < 2 || decoys < 2)
        {
            return false; // not enough to train a model
        }

        try
        {
            var percolatorConfig = new PercolatorConfig
            {
                FeatureInfos = OspreyFeatureCalculators.BuildFeatureInfos(ParquetScoreCache.PIN_FEATURE_NAMES),
            };
            var results = PercolatorFdr.RunPercolator(entries, percolatorConfig);
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].IsDecoy)
                {
                    continue;
                }
                if (targetEntryRow.TryGetValue(entries[i].EntryId, out var row))
                {
                    row.QvaluePercolator = results.Entries[i].RunPrecursorQvalue;
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static PercolatorEntry MakeEntry(RepickRow row, double[] features, bool isDecoy, uint entryId) => new()
    {
        FileName = row.FileName,
        Peptide = (isDecoy ? "DECOY_" : string.Empty) + row.PeptideModifiedSequence,
        Charge = (byte)row.PrecursorCharge,
        IsDecoy = isDecoy,
        EntryId = entryId,
        CoelutionSum = features[0],
        Features = features,
    };
}
