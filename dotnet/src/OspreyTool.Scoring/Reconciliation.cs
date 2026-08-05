using OspreyTool.Core;
using OspreyTool.Core.Detection;
using OspreyTool.Scoring.Ranking;
using pwiz.Osprey.Chromatography;
using pwiz.Osprey.Core;
using pwiz.Osprey.FDR;
using pwiz.Osprey.FDR.Reconciliation;
using pwiz.Osprey.IO;
using pwiz.Osprey.Scoring;
using CoreLibFragment = OspreyTool.Core.LibraryFragment;

namespace OspreyTool.Scoring;

/// <summary>Knobs for the reconciliation pipeline. Confidence comes from Percolator (q-values) when
/// <see cref="UseFdr"/>, else from the co-elution rank score gated at <see cref="GateThreshold"/>.</summary>
public sealed class ReconcileConfig
{
    public bool UseFdr { get; init; } = true;
    public bool ChargeConsensus { get; init; } = true;
    public bool InterRunReconcile { get; init; } = true;

    /// <summary>Targeted mode: reconcile EVERY target precursor (a known target in a targeted assay),
    /// not only those passing the confidence gate. The consensus RT is still score-weighted, so confident
    /// runs anchor it and weak runs are force-integrated there; only the eligibility gate is opened.</summary>
    public bool AllTargets { get; init; }

    /// <summary>Confidence gate: max q-value (FDR mode) or min co-elution (no-FDR mode) for a
    /// detection to anchor consensus / charge-consensus leadership.</summary>
    public double GateThreshold { get; init; } = 0.01;

    public double RtTolerance { get; init; } = 0.5;
    public double RtSigma { get; init; } = 0.3;
    public double MinConsensusHeight { get; init; }

    /// <summary>Exponent w on the intensity tiebreaker <c>ln(1+I)^w</c> in the pick rank. 1.0 is
    /// Osprey-exact; 0.0 removes the term so peaks rank on co-elution, library cosine and RT alone.</summary>
    public double IntensityExponent { get; init; } = 1.0;

    /// <summary>Which algorithm proposes candidate peaks. Null = Osprey's CWT. Everything downstream
    /// (scoring, ranking, FDR, reconciliation) is independent of the choice.</summary>
    public IPeakDetector? Detector { get; init; }

    /// <summary>How a candidate's evidence is combined into the number the pick ranks on. Null = Osprey's
    /// default, the frozen learned linear pick for the config's resolution
    /// (<see cref="PickLdaRankModel"/>); pass <see cref="ProductRankModel.Instance"/> for the legacy
    /// product form.</summary>
    public ICandidateRankModel? RankModel { get; init; }

    /// <summary>Osprey scoring config - build it from the document's transition settings
    /// (<see cref="OspreyConfigFactory.FromTransitionSettings"/>) so the resolution mode, and with it which
    /// frozen pick model applies, come from the instrument rather than a guess. Null = Osprey defaults.</summary>
    public OspreyConfig? Osprey { get; init; }

    /// <summary>The confidence threshold used for calibration anchoring and pass/count reporting. In
    /// no-FDR mode q is 0/1 (pass/fail on co-elution), so 0.5 makes the pass/fail gate behave correctly.</summary>
    public double OspreyGate => UseFdr ? GateThreshold : 0.5;

    /// <summary>The eligibility gate handed to the Osprey reconciliation classes. In targeted mode this
    /// is wide open (1.0 admits every q), so all targets are reconciled while score weighting keeps the
    /// consensus RT anchored to the confident detections.</summary>
    public double ReconcileGate => AllTargets ? 1.0 : OspreyGate;
}

/// <summary>One target/decoy detection with its final (possibly reconciled) boundary and how it got there.</summary>
public sealed class ReconcileRow
{
    public required string FileName { get; init; }
    public required string PeptideModifiedSequence { get; init; }
    public required int PrecursorCharge { get; init; }
    public required bool IsDecoy { get; init; }
    public double MinStartTime { get; set; }
    public double MaxEndTime { get; set; }
    public double ApexRt { get; set; }
    public double Coelution { get; init; }
    public double Score { get; init; }
    public double Qvalue { get; init; }
    /// <summary>keep | charge-consensus | use-cwt | forced-integration | unpicked.</summary>
    public string Action { get; set; } = "keep";
}

public sealed class ReconcileSummary
{
    public required bool UsedFdr { get; init; }
    public required int TargetsScored { get; init; }
    public required int DecoysScored { get; init; }
    public required int TargetsPassing { get; init; }
    public required int ChargeConsensusMoves { get; init; }
    public required int UseCwtMoves { get; init; }
    public required int ForcedIntegrations { get; init; }
    public required int ConsensusPeptides { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required IReadOnlyList<ReconcileRow> Rows { get; init; }

    /// <summary>The rank model the picks were made with ("lda" or "product").</summary>
    public required string RankerId { get; init; }
}

/// <summary>
/// Turns per-run peak picks into an experiment-level result by chaining Osprey's reconciliation:
/// (1) re-pick every target + genuine decoy, retaining CWT candidates; (2) attach a confidence -
/// Percolator SVM score + q-values (FDR mode) or the co-elution rank score (no-FDR mode); (3)
/// <see cref="MultiChargeConsensus"/> snaps a peptide's minority charge states onto the leader's peak
/// within each run; (4) <see cref="ConsensusRts"/> + <see cref="ReconciliationPlanner"/> compute a
/// cross-run consensus RT (per-file <see cref="RTCalibration"/>) and Keep / snap-to-CWT / force-integrate
/// each detection. The Osprey classes are driven unchanged in both modes by mapping the co-elution
/// confidence into the same <see cref="FdrEntry.Score"/> + q-value fields Percolator would fill.
/// </summary>
public static class ReconciliationPipeline
{
    private sealed class Det
    {
        public required string File;
        public required string RawModseq;
        public required string NormModseq;
        public required int Charge;
        public required bool IsDecoy;
        public required uint BaseId;
        public bool HasPeak;
        public double Start;
        public double End;
        public double Apex;
        public double Coelution;
        public double RankScore;
        public double? ExpectedRt;
        public double[]? Features;
        public IReadOnlyList<CwtCandidate>? Candidates;
        public double Score;
        public double RunPrecQ = 1.0;
        public double RunPepQ = 1.0;
        public double ExpPrecQ = 1.0;
        public double ExpPepQ = 1.0;
        public string Action = "keep";
    }

    public static ReconcileSummary Run(
        IReadOnlyList<PrecursorChromatograms> groups,
        IReadOnlyDictionary<PrecursorKey, double> rtByTarget,
        IReadOnlyCollection<PrecursorKey> decoyKeys,
        IReadOnlyDictionary<PrecursorKey, double> rtByDecoy,
        ReconcileConfig config,
        IReadOnlyDictionary<PrecursorKey, IReadOnlyList<CoreLibFragment>>? fragmentsByPrecursor = null)
    {
        var scorer = new OspreyFeatureScorer(config.Osprey ?? new OspreyConfig(), config.Detector);
        var decoySet = new HashSet<PrecursorKey>(decoyKeys);
        var warnings = new List<string>();

        // Stable base_id per (peptide, charge, target/decoy) - the same across every file so a detection
        // that passes in one run can rescue its siblings in the others. Unique per entity (no target/decoy
        // base_id linkage in v1: reconciliation acts on targets; decoys keep the FDR honest).
        var baseIds = new Dictionary<(string, int, bool), uint>();
        uint nextId = 0;
        uint BaseIdFor(string k, int z, bool d)
        {
            var key = (k, z, d);
            if (!baseIds.TryGetValue(key, out var id))
            {
                id = nextId++;
                baseIds[key] = id;
            }
            return id;
        }

        // Which rank model picks the winner among the detected candidates. Null = the scorer's default, which
        // is Osprey's frozen learned linear pick for this resolution (nothing is trained - see PickLdaModel).
        var rankModel = config.RankModel ?? scorer.DefaultRankModel;

        // The learned model weights median_polish as a real feature, so a missing library silently demotes it
        // to the neutral 1.0 for every candidate (a constant offset - the pick then rests on the other three).
        if (rankModel is PickLdaRankModel && fragmentsByPrecursor is null)
        {
            warnings.Add("No library fragments supplied: the learned pick's median_polish term is neutral " +
                "for every candidate, so that feature is effectively dropped.");
        }

        // Stage 1: re-pick every group, keep everything (even gate-rejected) so reconciliation can rescue.
        var perFile = new List<(string File, List<Det> Dets)>();
        var fileIndex = new Dictionary<string, int>();
        foreach (var g in groups)
        {
            var norm = ModifiedSequence.Normalize(g.PeptideModifiedSequence);
            var key = new PrecursorKey(norm, g.PrecursorCharge);
            var isDecoy = decoySet.Contains(key);
            double? expected = isDecoy
                ? (rtByDecoy.TryGetValue(key, out var dr) ? dr : null)
                : (rtByTarget.TryGetValue(key, out var tr) ? tr : null);

            IReadOnlyList<CoreLibFragment>? frags = null;
            fragmentsByPrecursor?.TryGetValue(key, out frags);
            var r = scorer.Repick(g.Xics, expected, config.RtTolerance, config.RtSigma,
                config.MinConsensusHeight, computeFdrFeatures: config.UseFdr, retainCandidates: true,
                libraryFragments: frags, intensityExponent: config.IntensityExponent, rankModel: rankModel);

            var det = new Det
            {
                File = g.FileName,
                RawModseq = g.PeptideModifiedSequence,
                NormModseq = norm,
                Charge = g.PrecursorCharge,
                IsDecoy = isDecoy,
                BaseId = BaseIdFor(norm, g.PrecursorCharge, isDecoy),
                HasPeak = r.HasPeak,
                Start = r.HasPeak ? r.StartRt : double.NaN,
                End = r.HasPeak ? r.EndRt : double.NaN,
                Apex = r.HasPeak ? r.ApexRt : double.NaN,
                Coelution = r.HasPeak ? r.Coelution : 0.0,
                RankScore = r.HasPeak ? r.RankScore : 0.0,
                ExpectedRt = expected,
                Features = r.BestFeatures,
                Candidates = r.Candidates,
            };
            det.Action = r.HasPeak ? "keep" : "unpicked";

            if (!fileIndex.TryGetValue(g.FileName, out var fi))
            {
                fi = perFile.Count;
                fileIndex[g.FileName] = fi;
                perFile.Add((g.FileName, new List<Det>()));
            }
            perFile[fi].Dets.Add(det);
        }

        // Stage 2: confidence.
        if (config.UseFdr)
        {
            AssignFdrConfidence(perFile);
        }
        else
        {
            // Weight the consensus by the FULL peak-quality rank (coelution*libCosine*rtPenalty*ln(1+I)^w),
            // not co-elution alone, so the confident runs anchor the consensus RT. ConsensusRts weights by
            // sigmoid(Score), so the rank must be turned into a LOGIT: standardize it against the observed
            // distribution of ranks (median +/- robust spread). This must not hardcode a center, because the
            // rank's scale depends on the intensity exponent - at w=1 ranks run ~0..15, at w=0 they are a
            // product of bounded terms and cannot exceed 1, so any fixed center would call every pick weak
            // and flatten the weighting that reconciliation depends on.
            var ranks = perFile.SelectMany(f => f.Dets).Where(d => d.HasPeak).Select(d => d.RankScore).ToList();
            var (center, spread) = MedianAndSpread(ranks);
            foreach (var (_, dets) in perFile)
            {
                foreach (var d in dets)
                {
                    d.Score = d.HasPeak ? 2.0 * (d.RankScore - center) / spread : -20.0;
                    var pass = d.HasPeak && d.Coelution >= config.GateThreshold;
                    d.RunPrecQ = d.RunPepQ = d.ExpPrecQ = d.ExpPepQ = pass ? 0.0 : 1.0;
                }
            }
        }

        // Stage 3: build FdrEntry lists + per-file CWT candidate lists + RT calibrations.
        var perFileEntries = new List<KeyValuePair<string, IReadOnlyList<FdrEntry>>>();
        var perFileCwt = new Dictionary<string, IReadOnlyList<IReadOnlyList<CwtCandidate>>>();
        var perFileCal = new Dictionary<string, RTCalibration>();
        var detByEntry = new Dictionary<(string, int), Det>();

        foreach (var (file, dets) in perFile)
        {
            var entries = new List<FdrEntry>(dets.Count);
            var cwt = new List<IReadOnlyList<CwtCandidate>>(dets.Count);
            for (var i = 0; i < dets.Count; i++)
            {
                var d = dets[i];
                entries.Add(new FdrEntry
                {
                    EntryId = d.BaseId,
                    ParquetIndex = (uint)i,
                    IsDecoy = d.IsDecoy,
                    Charge = (byte)d.Charge,
                    ModifiedSequence = d.NormModseq,
                    ApexRt = d.Apex,
                    StartRt = d.Start,
                    EndRt = d.End,
                    CoelutionSum = d.Coelution,
                    Score = d.Score,
                    RunPrecursorQvalue = d.RunPrecQ,
                    RunPeptideQvalue = d.RunPepQ,
                    RunProteinQvalue = 1.0,
                    ExperimentPrecursorQvalue = d.ExpPrecQ,
                    ExperimentPeptideQvalue = d.ExpPepQ,
                });
                cwt.Add(d.Candidates ?? (IReadOnlyList<CwtCandidate>)Array.Empty<CwtCandidate>());
                detByEntry[(file, i)] = d;
            }
            perFileEntries.Add(new KeyValuePair<string, IReadOnlyList<FdrEntry>>(file, entries));
            perFileCwt[file] = cwt;

            var cal = FitCalibration(dets, rtByTarget, config.OspreyGate, out var calWarn);
            if (cal != null)
            {
                perFileCal[file] = cal;
            }
            else if (calWarn != null)
            {
                warnings.Add($"{file}: {calWarn}");
            }
        }

        // Stage 4: intra-run multi-charge consensus - snap minority charges onto the leader's peak.
        var chargeMoves = 0;
        if (config.ChargeConsensus)
        {
            foreach (var kvp in perFileEntries)
            {
                var targets = MultiChargeConsensus.SelectRescoreTargets(kvp.Value, config.ReconcileGate);
                foreach (var (idx, apex, start, end) in targets)
                {
                    var d = detByEntry[(kvp.Key, idx)];
                    d.Start = start;
                    d.End = end;
                    d.Apex = apex;
                    d.HasPeak = true;
                    d.Action = "charge-consensus";
                    kvp.Value[idx].StartRt = start;
                    kvp.Value[idx].EndRt = end;
                    kvp.Value[idx].ApexRt = apex;
                    chargeMoves++;
                }
            }
        }

        // Stage 5: inter-run consensus RT + reconciliation.
        var useCwt = 0;
        var forced = 0;
        var consensusCount = 0;
        if (config.InterRunReconcile)
        {
            if (perFileCal.Count < perFileEntries.Count)
            {
                warnings.Add("Skipping inter-run reconciliation: RT calibration unavailable for some runs.");
            }
            else
            {
                var consensus = ConsensusRts.Compute(perFileEntries, perFileCal, config.ReconcileGate, 0.0);
                consensusCount = consensus.Count(c => !c.IsDecoy);
                var consensusHalfWidth = new Dictionary<(string, bool), double>();
                foreach (var c in consensus)
                {
                    consensusHalfWidth[(c.ModifiedSequence, c.IsDecoy)] = c.MedianPeakWidth / 2.0;
                }
                var actions = ReconciliationPlanner.Plan(
                    consensus, perFileEntries, perFileCwt, perFileCal, perFileCal, config.ReconcileGate);

                foreach (var ((file, idx), action) in actions)
                {
                    var d = detByEntry[(file, idx)];
                    switch (action)
                    {
                        case ReconcileAction.UseCwtPeak u:
                            d.Start = u.StartRt;
                            d.End = u.EndRt;
                            d.Apex = u.ApexRt;
                            d.HasPeak = true;
                            d.Action = "use-cwt";
                            useCwt++;
                            break;
                        case ReconcileAction.ForcedIntegration f:
                            d.Start = f.ExpectedRt - f.HalfWidth;
                            d.End = f.ExpectedRt + f.HalfWidth;
                            d.Apex = f.ExpectedRt;
                            d.HasPeak = true;
                            d.Action = "forced-integration";
                            forced++;
                            break;
                    }
                }

                // Maintain the consensus (most-confident) peak WIDTH on every consensus-peptide detection -
                // including 'keep' ones - so a run whose apex is fine but whose CWT width is anomalously
                // narrow (or wide) still integrates the same window as the confident replicates. The apex is
                // preserved; only the boundaries are re-centered to the consensus half-width.
                foreach (var (_, dets) in perFile)
                {
                    foreach (var d in dets)
                    {
                        if (d.IsDecoy || double.IsNaN(d.Apex))
                        {
                            continue;
                        }
                        if (consensusHalfWidth.TryGetValue((d.NormModseq, d.IsDecoy), out var hw) && hw > 0.0)
                        {
                            d.Start = d.Apex - hw;
                            d.End = d.Apex + hw;
                        }
                    }
                }
            }
        }

        // Emit rows.
        var rows = new List<ReconcileRow>();
        foreach (var (_, dets) in perFile)
        {
            foreach (var d in dets)
            {
                rows.Add(new ReconcileRow
                {
                    FileName = d.File,
                    PeptideModifiedSequence = d.RawModseq,
                    PrecursorCharge = d.Charge,
                    IsDecoy = d.IsDecoy,
                    MinStartTime = d.Start,
                    MaxEndTime = d.End,
                    ApexRt = d.Apex,
                    Coelution = d.Coelution,
                    Score = d.Score,
                    Qvalue = d.RunPrecQ,
                    Action = d.Action,
                });
            }
        }

        var allDets = perFile.SelectMany(f => f.Dets).ToList();
        return new ReconcileSummary
        {
            UsedFdr = config.UseFdr,
            TargetsScored = allDets.Count(d => !d.IsDecoy && d.HasPeak),
            DecoysScored = allDets.Count(d => d.IsDecoy && d.HasPeak),
            TargetsPassing = allDets.Count(d => !d.IsDecoy && d.RunPrecQ <= config.OspreyGate),
            ChargeConsensusMoves = chargeMoves,
            UseCwtMoves = useCwt,
            ForcedIntegrations = forced,
            ConsensusPeptides = consensusCount,
            Warnings = warnings,
            Rows = rows,
            RankerId = rankModel.Id,
        };
    }

    /// <summary>Median and a robust spread (MAD, scaled to a standard deviation) of the rank scores - the
    /// reference points used to turn a rank of any scale into a logit. Falls back to a unit spread when the
    /// ranks are degenerate (all equal, or too few to estimate), so the weighting never divides by zero.</summary>
    private static (double Center, double Spread) MedianAndSpread(List<double> values)
    {
        if (values.Count == 0)
        {
            return (0.0, 1.0);
        }
        var sorted = values.OrderBy(v => v).ToList();
        var median = Median(sorted);
        var mad = Median(sorted.Select(v => Math.Abs(v - median)).OrderBy(v => v).ToList()) * 1.4826;
        return (median, mad > 1e-9 ? mad : 1.0);
    }

    private static double Median(List<double> sorted) =>
        sorted.Count == 0 ? 0.0
        : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2]
        : 0.5 * (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]);

    /// <summary>Runs the genuine target-decoy Percolator over the scored detections and copies the SVM
    /// score + q-values back onto each <see cref="Det"/>.</summary>
    private static void AssignFdrConfidence(List<(string File, List<Det> Dets)> perFile)
    {
        var entries = new List<PercolatorEntry>();
        var order = new List<Det>();
        foreach (var (file, dets) in perFile)
        {
            foreach (var d in dets)
            {
                if (!d.HasPeak || d.Features is null)
                {
                    d.Score = -20.0; // unscored -> very low so it never anchors consensus
                    continue;
                }
                entries.Add(new PercolatorEntry
                {
                    FileName = file,
                    Peptide = (d.IsDecoy ? "DECOY_" : string.Empty) + d.NormModseq,
                    Charge = (byte)d.Charge,
                    IsDecoy = d.IsDecoy,
                    EntryId = (uint)entries.Count,
                    CoelutionSum = d.Features[0],
                    Features = d.Features,
                });
                order.Add(d);
            }
        }
        if (entries.Count == 0)
        {
            return;
        }

        var cfg = new PercolatorConfig
        {
            FeatureInfos = OspreyFeatureCalculators.BuildFeatureInfos(ParquetScoreCache.PIN_FEATURE_NAMES),
        };
        var res = PercolatorFdr.RunPercolator(entries, cfg);
        for (var i = 0; i < order.Count; i++)
        {
            var d = order[i];
            var e = res.Entries[i];
            d.Score = e.Score;
            d.RunPrecQ = e.RunPrecursorQvalue;
            d.RunPepQ = e.RunPeptideQvalue;
            d.ExpPrecQ = e.ExperimentPrecursorQvalue;
            d.ExpPepQ = e.ExperimentPeptideQvalue;
        }
    }

    /// <summary>Fits a per-file measured&#8596;library RT calibration from confident target detections
    /// (blib RT vs observed apex). Falls back to all target peaks if too few pass; returns null (with a
    /// reason) if a calibration cannot be fit.</summary>
    private static RTCalibration? FitCalibration(
        List<Det> dets, IReadOnlyDictionary<PrecursorKey, double> rtByTarget, double gate, out string? warn)
    {
        warn = null;
        List<(double lib, double meas)> Pairs(bool confidentOnly)
        {
            var pairs = new List<(double, double)>();
            foreach (var d in dets)
            {
                if (d.IsDecoy || !d.HasPeak || double.IsNaN(d.Apex))
                {
                    continue;
                }
                if (confidentOnly && d.RunPrecQ > gate)
                {
                    continue;
                }
                if (rtByTarget.TryGetValue(new PrecursorKey(d.NormModseq, d.Charge), out var lib))
                {
                    pairs.Add((lib, d.Apex));
                }
            }
            return pairs;
        }

        var p = Pairs(confidentOnly: true);
        if (p.Count < 20)
        {
            p = Pairs(confidentOnly: false);
        }
        if (p.Count < 20)
        {
            warn = $"only {p.Count} target RT pairs - no RT calibration";
            return null;
        }

        try
        {
            var lib = p.Select(x => x.lib).ToArray();
            var meas = p.Select(x => x.meas).ToArray();
            return new RTCalibrator().Fit(lib, meas);
        }
        catch (Exception ex)
        {
            warn = $"RT calibration fit failed ({ex.Message})";
            return null;
        }
    }
}
