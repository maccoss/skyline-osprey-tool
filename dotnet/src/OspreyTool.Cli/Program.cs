using System.Globalization;
using OspreyTool.Core;
using OspreyTool.Scoring;
using OspreyTool.Scoring.Detection;

// M0 harness: join a Skyline chromatogram export to a Carafe/Cadenza .blib and print the
// sanity report (clean join + predicted-RT-in-window). The Osprey scoring path (CWT + the
// 12-feature subset) lives in OspreyTool.Scoring; wiring target/decoy scoring + Percolator
// into this command is the next step.
//
//   ospreytool m0 --xics <export.tsv> --blib <library.blib>

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return 0;
}

if (args[0] is "-v" or "--version" or "version")
{
    Console.WriteLine($"ospreytool {typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown"}");
    return 0;
}

switch (args[0])
{
    case "m0":
        return RunM0(args[1..]);
    case "settings":
        return RunSettings(args[1..]);
    case "score":
        return RunScore(args[1..]);
    case "repick":
        return RunRepick(args[1..]);
    case "decoyfdr":
        return RunDecoyFdr(args[1..]);
    case "reconcile":
        return RunReconcile(args[1..]);
    case "explain":
        return RunExplain(args[1..]);
    default:
        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
        PrintUsage();
        return 2;
}

// Re-picks target peaks with Osprey's bestPeak rank score (CWT + DIA-NN co-elution + RT penalty)
// and writes a Skyline --import-peak-boundaries file.
static int RunRepick(string[] args)
{
    string? xics = null;
    string? blib = null;
    string? outPath = null;
    string? reportPath = null;
    var minConsensus = 0.0;
    var rtTol = 0.5;
    var rtSigma = 0.3;
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--xics" when i + 1 < args.Length:
                xics = args[++i];
                break;
            case "--blib" when i + 1 < args.Length:
                blib = args[++i];
                break;
            case "--out" when i + 1 < args.Length:
                outPath = args[++i];
                break;
            case "--min-consensus" when i + 1 < args.Length:
                minConsensus = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                break;
            case "--rt-tol" when i + 1 < args.Length:
                rtTol = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                break;
            case "--rt-sigma" when i + 1 < args.Length:
                rtSigma = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                break;
            case "--report" when i + 1 < args.Length:
                reportPath = args[++i];
                break;
            default:
                Console.Error.WriteLine($"Unrecognized argument '{args[i]}'.");
                return 2;
        }
    }
    if (xics is null || outPath is null)
    {
        Console.Error.WriteLine("repick requires --xics <export.tsv> --out <boundaries.csv> [--blib <lib.blib>] [--rt-tol 0.5] [--rt-sigma 0.3].");
        return 2;
    }
    if (!File.Exists(xics))
    {
        Console.Error.WriteLine($"XIC export not found: {xics}");
        return 1;
    }

    IReadOnlyDictionary<OspreyTool.Core.PrecursorKey, double> rtMap =
        new Dictionary<OspreyTool.Core.PrecursorKey, double>();
    if (blib is not null)
    {
        if (!File.Exists(blib))
        {
            Console.Error.WriteLine($"Blib not found: {blib}");
            return 1;
        }
        Console.WriteLine($"Reading predicted RTs from library: {blib}");
        rtMap = new BlibReader(blib).ReadRetentionTimes();
        Console.WriteLine($"  {rtMap.Count} predicted retention times loaded.");
    }
    else
    {
        Console.WriteLine("No --blib given: re-picking on co-elution + intensity only (no RT penalty / gate).");
    }

    Console.WriteLine($"Reading chromatograms: {xics}");
    var groups = ChromatogramTsvReader.ReadGroups(xics);
    Console.WriteLine($"Re-picking {groups.Count} groups with Osprey rank score " +
        $"(rt-tol {rtTol}, rt-sigma {rtSigma}) ...");
    var summary = RepickPipeline.Run(groups, rtMap, rtTol, rtSigma, minConsensus);

    var ci = System.Globalization.CultureInfo.InvariantCulture;
    using (var w = new StreamWriter(outPath))
    {
        w.WriteLine("PeptideModifiedSequence,FileName,PrecursorCharge,MinStartTime,MaxEndTime");
        foreach (var r in summary.Rows)
        {
            w.WriteLine($"{r.PeptideModifiedSequence},{r.FileName},{r.PrecursorCharge}," +
                $"{r.MinStartTime.ToString("R", ci)},{r.MaxEndTime.ToString("R", ci)}");
        }
    }

    if (reportPath is not null)
    {
        using var w = new StreamWriter(reportPath);
        w.WriteLine("PeptideModifiedSequence,FileName,PrecursorCharge,MinStartTime,MaxEndTime,ApexRt," +
            "ExpectedRt,RtResidual,Coelution,SecondBestCoelution,Candidates,Qvalue,QvaluePercolator");
        foreach (var r in summary.Rows)
        {
            w.WriteLine(string.Join(",",
                r.PeptideModifiedSequence, r.FileName, r.PrecursorCharge,
                r.MinStartTime.ToString("R", ci), r.MaxEndTime.ToString("R", ci),
                r.ApexRt.ToString("R", ci), r.ExpectedRt.ToString("R", ci), r.RtResidual.ToString("R", ci),
                r.Coelution.ToString("R", ci), r.SecondBestCoelution.ToString("R", ci),
                r.Candidates, r.Qvalue.ToString("R", ci), r.QvaluePercolator.ToString("R", ci)));
        }
    }

    Console.WriteLine();
    Console.WriteLine("Re-pick summary");
    Console.WriteLine("===============");
    Console.WriteLine($"Groups                       : {summary.Groups}");
    Console.WriteLine($"Re-picked (boundary written) : {summary.Repicked}");
    Console.WriteLine($"Too few fragments (<2)       : {summary.TooFewFragments} (kept Skyline's pick)");
    Console.WriteLine($"No CWT peak                  : {summary.NoCwtPeak}");
    Console.WriteLine($"Rejected by RT gate          : {summary.GateRejected} (kept Skyline's pick)");
    Console.WriteLine($"No predicted RT in library   : {summary.NoRtMatch}");
    Console.WriteLine($"Rank overrode top CWT peak   : {summary.RankOverrodeCwt} " +
        $"({(summary.Repicked == 0 ? 0 : 100.0 * summary.RankOverrodeCwt / summary.Repicked):F1}% of re-picked)");
    Console.WriteLine();
    Console.WriteLine("Second-best-peak FDR, null = non-overlapping runner-up peak");
    Console.WriteLine("----------------------------------------------------------");
    Console.WriteLine($"Precursors with a 2nd peak   : {summary.Repicked - summary.NoSecondBest} " +
        $"(no 2nd peak / no null: {summary.NoSecondBest})");
    Console.WriteLine("(B) single co-elution discriminant");
    Console.WriteLine($"    Targets at q<=0.01       : {summary.TargetsAtQ01}");
    Console.WriteLine($"    Targets at q<=0.05       : {summary.TargetsAtQ05}");
    Console.WriteLine($"(A) multi-feature Percolator model ({OspreyFeatureScorer.FdrFeatureIndices.Length} features)");
    Console.WriteLine($"    Targets at q<=0.01       : {summary.PercolatorTargetsAtQ01}");
    Console.WriteLine($"    Targets at q<=0.05       : {summary.PercolatorTargetsAtQ05}");
    if (summary.PercolatorFailedRuns > 0)
    {
        Console.WriteLine($"    Replicates w/ failed run : {summary.PercolatorFailedRuns}");
    }
    Console.WriteLine($"Boundaries written           : {outPath}");
    if (reportPath is not null)
    {
        Console.WriteLine($"Per-precursor report written : {reportPath}");
    }
    return 0;
}

// Calibrated FDR with GENUINE reversed decoys: score each target + its decoy (extracted at the target's
// precursor m/z in the same scheduled window) and run Osprey's Percolator over the target-decoy competition.
static int RunDecoyFdr(string[] args)
{
    var ci = System.Globalization.CultureInfo.InvariantCulture;
    string? xics = null, blib = null, decoysFile = null, outPath = null, reportPath = null;
    var minConsensus = 0.0;
    var rtTol = 0.5;
    var rtSigma = 0.3;
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--xics" when i + 1 < args.Length: xics = args[++i]; break;
            case "--blib" when i + 1 < args.Length: blib = args[++i]; break;
            case "--decoys" when i + 1 < args.Length: decoysFile = args[++i]; break;
            case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
            case "--report" when i + 1 < args.Length: reportPath = args[++i]; break;
            case "--min-consensus" when i + 1 < args.Length: minConsensus = double.Parse(args[++i], ci); break;
            case "--rt-tol" when i + 1 < args.Length: rtTol = double.Parse(args[++i], ci); break;
            case "--rt-sigma" when i + 1 < args.Length: rtSigma = double.Parse(args[++i], ci); break;
            default:
                Console.Error.WriteLine($"Unrecognized argument '{args[i]}'.");
                return 2;
        }
    }
    if (xics is null || blib is null || decoysFile is null)
    {
        Console.Error.WriteLine("decoyfdr requires --xics <target+decoy export.tsv> --blib <lib.blib> " +
            "--decoys <decoys.tsv> [--out <boundaries.csv>] [--report <report.csv>] [--rt-tol 0.5] [--rt-sigma 0.3].");
        return 2;
    }
    foreach (var (path, label) in new[] { (xics, "XIC export"), (blib, "Blib"), (decoysFile, "Decoys list") })
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"{label} not found: {path}");
            return 1;
        }
    }

    Console.WriteLine($"Reading predicted RTs from library: {blib}");
    var rtByTarget = new BlibReader(blib).ReadRetentionTimes();
    Console.WriteLine($"  {rtByTarget.Count} predicted retention times loaded.");

    // decoys.tsv: plain decoy sequence + charge + explicit RT (= its target's scheduled RT). Parse by
    // header name (the file also carries precursor/product m/z columns) and key by unmodified sequence +
    // charge so it matches Skyline's fixed-mod-applied export.
    var decoyRtByUnmod = new Dictionary<(string, int), double>();
    using (var reader = new StreamReader(decoysFile))
    {
        var header = (reader.ReadLine() ?? string.Empty).Split('\t');
        int Col(string name) => Array.FindIndex(header, h => h.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
        var iSeq = Col("Peptide Modified Sequence");
        var iCharge = Col("Precursor Charge");
        var iRt = Col("Explicit Retention Time");
        if (iSeq < 0 || iCharge < 0 || iRt < 0)
        {
            Console.Error.WriteLine("decoys.tsv must have 'Peptide Modified Sequence', 'Precursor Charge', 'Explicit Retention Time' columns.");
            return 1;
        }
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var f = line.Split('\t');
            if (f.Length <= Math.Max(iSeq, Math.Max(iCharge, iRt)))
            {
                continue;
            }
            var unmod = ModifiedSequence.StripMods(f[iSeq]);
            if (int.TryParse(f[iCharge], out var z) && double.TryParse(f[iRt], NumberStyles.Float, ci, out var rt))
            {
                decoyRtByUnmod[(unmod, z)] = rt;
            }
        }
    }
    Console.WriteLine($"  {decoyRtByUnmod.Count} decoy precursors in the pairing list.");

    Console.WriteLine($"Reading chromatograms: {xics}");
    var groups = ChromatogramTsvReader.ReadGroups(xics);

    // Classify each exported group as target or decoy by its bare sequence, and give each decoy the
    // expected RT of its target.
    var decoyKeys = new HashSet<PrecursorKey>();
    var rtByDecoy = new Dictionary<PrecursorKey, double>();
    foreach (var g in groups)
    {
        var unmod = ModifiedSequence.StripMods(g.PeptideModifiedSequence);
        if (decoyRtByUnmod.TryGetValue((unmod, g.PrecursorCharge), out var rt))
        {
            var key = new PrecursorKey(ModifiedSequence.Normalize(g.PeptideModifiedSequence), g.PrecursorCharge);
            decoyKeys.Add(key);
            rtByDecoy[key] = rt;
        }
    }

    Console.WriteLine($"Scoring {groups.Count} groups (targets + genuine decoys) with Osprey rank score " +
        $"(rt-tol {rtTol}, rt-sigma {rtSigma}) then Percolator ...");
    var summary = DecoyFdrPipeline.Run(groups, rtByTarget, decoyKeys, rtByDecoy, rtTol, rtSigma, minConsensus);

    if (outPath is not null)
    {
        using var w = new StreamWriter(outPath);
        w.WriteLine("PeptideModifiedSequence,FileName,PrecursorCharge,MinStartTime,MaxEndTime");
        foreach (var r in summary.Rows.Where(r => !r.IsDecoy))
        {
            w.WriteLine($"{r.PeptideModifiedSequence},{r.FileName},{r.PrecursorCharge}," +
                $"{r.MinStartTime.ToString("R", ci)},{r.MaxEndTime.ToString("R", ci)}");
        }
    }
    if (reportPath is not null)
    {
        using var w = new StreamWriter(reportPath);
        w.WriteLine("PeptideModifiedSequence,FileName,PrecursorCharge,IsDecoy,MinStartTime,MaxEndTime,ApexRt,ExpectedRt,Coelution,Qvalue");
        foreach (var r in summary.Rows)
        {
            w.WriteLine(string.Join(",", r.PeptideModifiedSequence, r.FileName, r.PrecursorCharge,
                r.IsDecoy ? 1 : 0, r.MinStartTime.ToString("R", ci), r.MaxEndTime.ToString("R", ci),
                r.ApexRt.ToString("R", ci), r.ExpectedRt.ToString("R", ci),
                r.Coelution.ToString("R", ci), r.Qvalue.ToString("R", ci)));
        }
    }

    Console.WriteLine();
    Console.WriteLine("Genuine target-decoy Percolator FDR");
    Console.WriteLine("===================================");
    Console.WriteLine($"Targets scored               : {summary.TargetsScored} / {summary.TargetGroups}");
    Console.WriteLine($"Decoys scored                : {summary.DecoysScored} / {summary.DecoyGroups}");
    if (!summary.PercolatorOk)
    {
        Console.WriteLine("Percolator FAILED (q-values are NaN).");
    }
    Console.WriteLine($"Targets at q<=0.01           : {summary.TargetsAtQ01}");
    Console.WriteLine($"Targets at q<=0.05           : {summary.TargetsAtQ05}");
    Console.WriteLine();
    Console.WriteLine($"{"replicate",-46}{"targets",8}{"decoys",8}{"q<=.01",8}{"q<=.05",8}");
    foreach (var run in summary.PerRun)
    {
        var name = run.FileName.Length > 44 ? run.FileName[..44] : run.FileName;
        Console.WriteLine($"{name,-46}{run.Targets,8}{run.Decoys,8}{run.TargetsAtQ01,8}{run.TargetsAtQ05,8}");
    }
    if (outPath is not null)
    {
        Console.WriteLine($"\nTarget boundaries written    : {outPath}");
    }
    if (reportPath is not null)
    {
        Console.WriteLine($"Per-precursor report written : {reportPath}");
    }
    return 0;
}

// A reconciled boundary matches a CWT candidate when its start/end line up to well within a scan; a
// force-integrated window generally lines up with no candidate at all.
static bool SameWindow(CandidatePeak c, ReconcileRow r) =>
    Math.Abs(c.StartRt - r.MinStartTime) < 0.005 && Math.Abs(c.EndRt - r.MaxEndTime) < 0.005;

// Experiment-level reconciliation: re-pick + confidence (Percolator or co-elution) + intra-run charge
// consensus + inter-run consensus RT / reconciliation, writing reconciled boundaries.
static int RunReconcile(string[] args)
{
    var ci = System.Globalization.CultureInfo.InvariantCulture;
    string? xics = null, blib = null, decoysFile = null, outPath = null, reportPath = null, rtCsv = null;
    var useFdr = true;
    var chargeConsensus = true;
    var interRun = true;
    var allTargets = false;
    var libCosine = false;
    double? threshold = null;
    var rtTol = 0.0; // 0 = no hard RT gate; the PRM scheduling window (XIC extent) is the hard limit
    var rtSigma = 0.3;
    var minConsensus = 0.0;
    var intensityExp = 1.0;
    var detectorId = "osprey-cwt";
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--xics" when i + 1 < args.Length: xics = args[++i]; break;
            case "--blib" when i + 1 < args.Length: blib = args[++i]; break;
            case "--decoys" when i + 1 < args.Length: decoysFile = args[++i]; break;
            case "--rt-csv" when i + 1 < args.Length: rtCsv = args[++i]; break;
            case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
            case "--report" when i + 1 < args.Length: reportPath = args[++i]; break;
            case "--no-fdr": useFdr = false; break;
            case "--all-targets": allTargets = true; break;
            case "--lib-cosine": libCosine = true; break;
            case "--no-charge-consensus": chargeConsensus = false; break;
            case "--no-reconcile": interRun = false; break;
            case "--threshold" when i + 1 < args.Length: threshold = double.Parse(args[++i], ci); break;
            case "--rt-tol" when i + 1 < args.Length: rtTol = double.Parse(args[++i], ci); break;
            case "--rt-sigma" when i + 1 < args.Length: rtSigma = double.Parse(args[++i], ci); break;
            case "--min-consensus" when i + 1 < args.Length: minConsensus = double.Parse(args[++i], ci); break;
            case "--intensity-exp" when i + 1 < args.Length: intensityExp = double.Parse(args[++i], ci); break;
            case "--detector" when i + 1 < args.Length: detectorId = args[++i]; break;
            default:
                Console.Error.WriteLine($"Unrecognized argument '{args[i]}'.");
                return 2;
        }
    }
    if (xics is null || blib is null || decoysFile is null)
    {
        Console.Error.WriteLine("reconcile requires --xics <target+decoy export.tsv> --blib <lib.blib> --decoys <decoys.tsv> " +
            "[--out <boundaries.csv>] [--report <report.csv>] [--no-fdr] [--no-charge-consensus] [--no-reconcile] [--threshold <q|coelution>].");
        return 2;
    }
    foreach (var (path, label) in new[] { (xics, "XIC export"), (blib, "Blib"), (decoysFile, "Decoys list") })
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"{label} not found: {path}");
            return 1;
        }
    }

    var gate = threshold ?? (useFdr ? 0.01 : 0.5);

    IReadOnlyDictionary<PrecursorKey, double> rtByTarget;
    if (rtCsv is not null)
    {
        // Prefer the document's explicit (scheduling) RT over the blib's predicted RT - the acquisition
        // was scheduled on the doc RT, so the data and the real peak are there. The blib prediction can be
        // off by >0.5 min for ~7% of peptides, which the RT gate then misreads.
        if (!File.Exists(rtCsv))
        {
            Console.Error.WriteLine($"RT CSV not found: {rtCsv}");
            return 1;
        }
        Console.WriteLine($"Reading expected (scheduling) RTs from document export: {rtCsv}");
        rtByTarget = LoadRtCsv(rtCsv, ci);
    }
    else
    {
        Console.WriteLine($"Reading predicted RTs from library: {blib}");
        rtByTarget = new BlibReader(blib).ReadRetentionTimes();
    }
    var (decoyKeys, rtByDecoy) = LoadDecoyMap(decoysFile, xics, ci, out var loadErr);
    if (loadErr is not null)
    {
        Console.Error.WriteLine(loadErr);
        return 1;
    }

    IReadOnlyDictionary<PrecursorKey, IReadOnlyList<LibraryFragment>>? fragsByPrecursor = null;
    if (libCosine)
    {
        Console.WriteLine($"Loading library fragments for the spectral-match (median_polish_cosine) term: {blib}");
        var byPrec = new Dictionary<PrecursorKey, IReadOnlyList<LibraryFragment>>();
        foreach (var e in new BlibReader(blib).ReadEntries(includeFragments: true))
        {
            byPrec[new PrecursorKey(ModifiedSequence.Normalize(e.PeptideModifiedSequence), e.PrecursorCharge)] = e.Fragments;
        }
        fragsByPrecursor = byPrec;
        Console.WriteLine($"  {byPrec.Count} library spectra loaded.");
    }

    Console.WriteLine($"Reading chromatograms: {xics}");
    var groups = ChromatogramTsvReader.ReadGroups(xics);

    var detector = PeakDetectors.ById(detectorId);
    if (detector is null)
    {
        Console.Error.WriteLine($"Unknown --detector '{detectorId}'. Available: {PeakDetectors.Ids}.");
        return 2;
    }

    var mode = useFdr ? $"Percolator FDR (q<={gate.ToString(ci)})" : $"co-elution (>={gate.ToString(ci)})";
    Console.WriteLine($"Reconciling {groups.Count} groups | detector: {detector.DisplayName} | confidence: {mode} | " +
        $"charge-consensus: {(chargeConsensus ? "on" : "off")} | inter-run: {(interRun ? "on" : "off")} | " +
        $"intensity^{intensityExp.ToString(ci)} ...");

    var summary = ReconciliationPipeline.Run(groups, rtByTarget, decoyKeys, rtByDecoy, new ReconcileConfig
    {
        UseFdr = useFdr,
        ChargeConsensus = chargeConsensus,
        InterRunReconcile = interRun,
        AllTargets = allTargets,
        GateThreshold = gate,
        RtTolerance = rtTol,
        RtSigma = rtSigma,
        MinConsensusHeight = minConsensus,
        IntensityExponent = intensityExp,
        Detector = detector,
    }, fragsByPrecursor);

    if (outPath is not null)
    {
        using var w = new StreamWriter(outPath);
        w.WriteLine("PeptideModifiedSequence,FileName,PrecursorCharge,MinStartTime,MaxEndTime");
        foreach (var r in summary.Rows.Where(r => !r.IsDecoy && !double.IsNaN(r.MinStartTime) && !double.IsNaN(r.MaxEndTime)))
        {
            w.WriteLine($"{r.PeptideModifiedSequence},{r.FileName},{r.PrecursorCharge}," +
                $"{r.MinStartTime.ToString("R", ci)},{r.MaxEndTime.ToString("R", ci)}");
        }
    }
    if (reportPath is not null)
    {
        using var w = new StreamWriter(reportPath);
        w.WriteLine("PeptideModifiedSequence,FileName,PrecursorCharge,IsDecoy,MinStartTime,MaxEndTime,ApexRt,Coelution,Score,Qvalue,Action");
        foreach (var r in summary.Rows)
        {
            w.WriteLine(string.Join(",", r.PeptideModifiedSequence, r.FileName, r.PrecursorCharge,
                r.IsDecoy ? 1 : 0, r.MinStartTime.ToString("R", ci), r.MaxEndTime.ToString("R", ci),
                r.ApexRt.ToString("R", ci), r.Coelution.ToString("R", ci), r.Score.ToString("R", ci),
                r.Qvalue.ToString("R", ci), r.Action));
        }
    }

    Console.WriteLine();
    Console.WriteLine("Reconciliation summary");
    Console.WriteLine("======================");
    Console.WriteLine($"Confidence source            : {(summary.UsedFdr ? "Percolator SVM score + q-values" : "co-elution rank score")}");
    Console.WriteLine($"Targets scored / passing     : {summary.TargetsScored} / {summary.TargetsPassing}");
    Console.WriteLine($"Decoys scored                : {summary.DecoysScored}");
    Console.WriteLine($"Consensus peptides           : {summary.ConsensusPeptides}");
    Console.WriteLine($"Charge-consensus moves       : {summary.ChargeConsensusMoves}");
    Console.WriteLine($"Snapped to CWT candidate     : {summary.UseCwtMoves}");
    Console.WriteLine($"Forced integrations (MBR)    : {summary.ForcedIntegrations}");
    foreach (var wrn in summary.Warnings)
    {
        Console.WriteLine($"  ! {wrn}");
    }
    if (outPath is not null)
    {
        Console.WriteLine($"Reconciled boundaries written: {outPath}");
    }
    if (reportPath is not null)
    {
        Console.WriteLine($"Per-precursor report written : {reportPath}");
    }
    return 0;
}

// Diagnostic: dump the CWT candidate peaks + full pick-score breakdown for one peptide (optionally one
// replicate), plus the reconciliation action - the data behind the Candidate Peaks panel.
static int RunExplain(string[] args)
{
    var ci = CultureInfo.InvariantCulture;
    string? xics = null, blib = null, decoysFile = null, rtCsv = null, peptide = null, replicate = null;
    var rtSigma = 0.3;
    var rtTol = 0.0;
    var libCosine = true;
    var intensityExp = 1.0;
    var detectorId = "osprey-cwt";
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--xics" when i + 1 < args.Length: xics = args[++i]; break;
            case "--blib" when i + 1 < args.Length: blib = args[++i]; break;
            case "--decoys" when i + 1 < args.Length: decoysFile = args[++i]; break;
            case "--rt-csv" when i + 1 < args.Length: rtCsv = args[++i]; break;
            case "--peptide" when i + 1 < args.Length: peptide = args[++i]; break;
            case "--replicate" when i + 1 < args.Length: replicate = args[++i]; break;
            case "--rt-sigma" when i + 1 < args.Length: rtSigma = double.Parse(args[++i], ci); break;
            case "--rt-tol" when i + 1 < args.Length: rtTol = double.Parse(args[++i], ci); break;
            case "--intensity-exp" when i + 1 < args.Length: intensityExp = double.Parse(args[++i], ci); break;
            case "--detector" when i + 1 < args.Length: detectorId = args[++i]; break;
            case "--no-lib-cosine": libCosine = false; break;
            default: Console.Error.WriteLine($"Unrecognized argument '{args[i]}'."); return 2;
        }
    }
    if (xics is null || blib is null || peptide is null)
    {
        Console.Error.WriteLine("explain requires --xics <export.tsv> --blib <lib.blib> --peptide <ModSeq> " +
            "[--replicate <name>] [--decoys <decoys.tsv>] [--rt-csv <doc RT.csv>] [--rt-sigma 0.3] " +
            "[--intensity-exp 1.0] [--no-lib-cosine].");
        return 2;
    }

    IReadOnlyDictionary<PrecursorKey, double> rtByTarget = rtCsv is not null
        ? LoadRtCsv(rtCsv, ci)
        : new BlibReader(blib).ReadRetentionTimes();

    var fragsByPrec = new Dictionary<PrecursorKey, IReadOnlyList<LibraryFragment>>();
    if (libCosine)
    {
        foreach (var e in new BlibReader(blib).ReadEntries(includeFragments: true))
        {
            fragsByPrec[new PrecursorKey(ModifiedSequence.Normalize(e.PeptideModifiedSequence), e.PrecursorCharge)] = e.Fragments;
        }
    }

    var groups = ChromatogramTsvReader.ReadGroups(xics);

    // The reconciled outcome per replicate (targeted no-FDR) when decoys are available: the action AND the
    // boundary that was actually applied, which for a force-integration is NOT one of the CWT candidates.
    var reconByFile = new Dictionary<string, ReconcileRow>(StringComparer.OrdinalIgnoreCase);
    if (decoysFile is not null)
    {
        var (decoyKeys, rtByDecoy) = LoadDecoyMap(decoysFile, xics, ci, out _);
        var summary = ReconciliationPipeline.Run(groups, rtByTarget, decoyKeys, rtByDecoy,
            new ReconcileConfig
            {
                UseFdr = false, AllTargets = true, RtTolerance = rtTol, RtSigma = rtSigma,
                IntensityExponent = intensityExp, Detector = PeakDetectors.ById(detectorId),
            },
            libCosine ? fragsByPrec : null);
        foreach (var row in summary.Rows.Where(r => r.PeptideModifiedSequence == peptide && !r.IsDecoy))
        {
            reconByFile[row.FileName] = row;
        }
    }

    var detector = PeakDetectors.ById(detectorId);
    if (detector is null)
    {
        Console.Error.WriteLine($"Unknown --detector '{detectorId}'. Available: {PeakDetectors.Ids}.");
        return 2;
    }
    var scorer = OspreyFeatureScorer.CreateDefault(detector);
    var shown = 0;
    foreach (var g in groups.Where(g => g.PeptideModifiedSequence == peptide
        && (replicate is null || g.FileName.Contains(replicate, StringComparison.OrdinalIgnoreCase))))
    {
        var key = new PrecursorKey(ModifiedSequence.Normalize(g.PeptideModifiedSequence), g.PrecursorCharge);
        double? expected = rtByTarget.TryGetValue(key, out var rt) ? rt : null;
        fragsByPrec.TryGetValue(key, out var frags);
        var r = scorer.Repick(g.Xics, expected, rtTol, rtSigma, 0.0, libraryFragments: frags,
            traceCandidates: true, intensityExponent: intensityExp);

        reconByFile.TryGetValue(g.FileName, out var recon);

        Console.WriteLine();
        Console.WriteLine($"{peptide} +{g.PrecursorCharge}  |  {g.FileName}");
        Console.WriteLine($"  expected RT {(expected.HasValue ? expected.Value.ToString("F2", ci) : "n/a")}   " +
            $"action: {recon?.Action ?? "(reconcile off)"}");
        if (r.CandidatePeaks is null || r.CandidatePeaks.Count == 0)
        {
            Console.WriteLine("  (no scorable CWT candidates)");
            shown++;
            continue;
        }
        Console.WriteLine($"  {"from",-9} {"apex",6} {"bounds",13} {"coel",7} {"libcos",7} {"dRT",6} {"rtPen",6} {"lnI",6} {"RANK",9}");
        foreach (var c in r.CandidatePeaks.OrderByDescending(c => c.Rank))
        {
            var applied = recon is not null && SameWindow(c, recon) ? $"  <= APPLIED ({recon.Action})" : "";
            var tag = (c.Chosen ? "  <= CHOSEN" : (c.SecondBest ? "  (2nd)" : "")) + applied;
            Console.WriteLine($"  {"CWT",-9} {c.ApexRt,6:F2} [{c.StartRt,5:F2},{c.EndRt,5:F2}] {c.Coelution,7:F3} {c.LibCosine,7:F3} " +
                $"{c.RtResidual,6:F2} {c.RtPenalty,6:F3} {c.IntensityWeight,6:F2} {c.Rank,9:F3}{tag}");
        }

        // Reconciliation can force a window that is not a CWT candidate at all. Score it on the same terms so
        // it can be compared directly with the peak the signal processing chose.
        if (recon is not null && !double.IsNaN(recon.MinStartTime) && !double.IsNaN(recon.MaxEndTime) &&
            !r.CandidatePeaks.Any(c => SameWindow(c, recon)))
        {
            var w = scorer.ScoreWindow(g.Xics, recon.MinStartTime, recon.MaxEndTime, expected, rtSigma,
                frags, intensityExp);
            if (w is not null)
            {
                Console.WriteLine($"  {"consensus",-9} {w.ApexRt,6:F2} [{w.StartRt,5:F2},{w.EndRt,5:F2}] {w.Coelution,7:F3} {w.LibCosine,7:F3} " +
                    $"{w.RtResidual,6:F2} {w.RtPenalty,6:F3} {w.IntensityWeight,6:F2} {w.Rank,9:F3}  <= APPLIED ({recon.Action})");
            }
        }
        shown++;
    }
    if (shown == 0)
    {
        Console.Error.WriteLine($"No groups for peptide '{peptide}'{(replicate is null ? "" : $" replicate '{replicate}'")}.");
        return 1;
    }
    return 0;
}

// Shared: read decoys.tsv (plain decoy seq + charge + explicit RT), then classify the export's groups into
// decoy keys + each decoy's expected (target) RT.
static (HashSet<PrecursorKey> DecoyKeys, Dictionary<PrecursorKey, double> RtByDecoy) LoadDecoyMap(
    string decoysFile, string xicsFile, CultureInfo ci, out string? error)
{
    error = null;
    var decoyRtByUnmod = new Dictionary<(string, int), double>();
    using (var reader = new StreamReader(decoysFile))
    {
        var header = (reader.ReadLine() ?? string.Empty).Split('\t');
        int Col(string name) => Array.FindIndex(header, h => h.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
        var iSeq = Col("Peptide Modified Sequence");
        var iCharge = Col("Precursor Charge");
        var iRt = Col("Explicit Retention Time");
        if (iSeq < 0 || iCharge < 0 || iRt < 0)
        {
            error = "decoys.tsv must have 'Peptide Modified Sequence', 'Precursor Charge', 'Explicit Retention Time' columns.";
            return (new(), new());
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
    foreach (var g in ChromatogramTsvReader.ReadGroups(xicsFile))
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

// Read expected target RTs from a Skyline report export (ModifiedSequence + PrecursorCharge +
// ExplicitRetentionTime), keyed by normalized modified sequence + charge.
static Dictionary<PrecursorKey, double> LoadRtCsv(string path, CultureInfo ci)
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

static int RunScore(string[] args)
{
    string? xics = null;
    string? blib = null;
    string? sky = null;
    var seed = 1337;
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--xics" when i + 1 < args.Length:
                xics = args[++i];
                break;
            case "--blib" when i + 1 < args.Length:
                blib = args[++i];
                break;
            case "--sky" when i + 1 < args.Length:
                sky = args[++i];
                break;
            case "--seed" when i + 1 < args.Length:
                seed = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                break;
            default:
                Console.Error.WriteLine($"Unrecognized argument '{args[i]}'.");
                return 2;
        }
    }

    if (xics is null || blib is null || sky is null)
    {
        Console.Error.WriteLine("score requires --xics <export.tsv> --blib <library.blib> --sky <document.sky>.");
        return 2;
    }
    foreach (var (label, path) in new[] { ("XIC export", xics), ("Blib", blib), ("Document", sky) })
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"{label} not found: {path}");
            return 1;
        }
    }

    Console.WriteLine($"Reading chromatograms: {xics}");
    var chromatograms = ChromatogramTsvReader.ReadGroups(xics);
    Console.WriteLine($"Loading library      : {blib}");
    var library = OspreyLibrary.Load(blib);
    var settings = SkylineTransitionSettings.FromDocument(sky);
    var config = OspreyConfigFactory.FromTransitionSettings(settings);
    var raw = RawFileAvailability.FromDocument(sky);
    var selection = FeatureSelection.Resolve(observedSpectraAvailable: raw.AllAccessible);

    Console.WriteLine($"Config               : {settings.ProductMassAnalyzer} " +
        $"(unit-resolution: {settings.IsUnitResolution}), product tol {settings.EffectiveProductTolerance}");
    Console.WriteLine($"Features computed     : {selection.FeatureIndices.Count} of 21");
    foreach (var warning in selection.Warnings)
    {
        Console.WriteLine(warning);
    }

    Console.WriteLine();
    Console.WriteLine($"Scoring {chromatograms.Count} target groups + decoy-XICs (seed {seed}) ...");
    var result = M0ScoringPipeline.Run(
        chromatograms, library, config, selection.FeatureIndices, new XicShuffleDecoyGenerator(seed));

    Console.WriteLine();
    Console.WriteLine("Target/decoy separation (rank AUC; 0.5 = none, 1.0 = perfect)");
    Console.WriteLine("=============================================================");
    Console.WriteLine($"Matched {result.Matched}/{result.Groups} groups; " +
        $"targets with peak {result.TargetsWithPeak}, decoys with peak {result.DecoysWithPeak}");
    Console.WriteLine($"{"feature",-34}{"target mean",14}{"decoy mean",14}{"AUC",8}");
    foreach (var s in result.Separations.OrderByDescending(s => s.Auc))
    {
        Console.WriteLine($"{s.FeatureName,-34}{Fmt(s.TargetMean),14}{Fmt(s.DecoyMean),14}{s.Auc,8:F3}");
    }

    Console.WriteLine();
    Console.WriteLine("Per-run Percolator (targets passing q-value)");
    Console.WriteLine("============================================");
    Console.WriteLine($"{"replicate",-48}{"targets",9}{"q<=.01",8}{"q<=.05",8}");
    foreach (var run in result.PerRun)
    {
        var file = Path.GetFileName(run.FileName);
        if (run.Error is not null)
        {
            Console.WriteLine($"{file,-48}  ERROR: {run.Error}");
            continue;
        }
        Console.WriteLine($"{file,-48}{run.Targets,9}{run.TargetsAtQ01,8}{run.TargetsAtQ05,8}");
    }
    return 0;
}

static string Fmt(double v) => double.IsNaN(v) ? "n/a" : v.ToString("G4", System.Globalization.CultureInfo.InvariantCulture);

static int RunSettings(string[] args)
{
    string? sky = null;
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--sky" && i + 1 < args.Length)
        {
            sky = args[++i];
        }
    }
    if (sky is null || !File.Exists(sky))
    {
        Console.Error.WriteLine("settings requires --sky <document.sky>.");
        return sky is null ? 2 : 1;
    }

    var s = SkylineTransitionSettings.FromDocument(sky);
    Console.WriteLine($"Acquisition method     : {s.AcquisitionMethod}");
    Console.WriteLine($"Product mass analyzer  : {s.ProductMassAnalyzer}  (unit-resolution: {s.IsUnitResolution})");
    Console.WriteLine($"Product res            : {s.ProductRes}");
    Console.WriteLine($"Precursor mass analyzer: {s.PrecursorMassAnalyzer}");
    Console.WriteLine($"Precursor res          : {s.PrecursorRes}");
    Console.WriteLine($"Instrument mz tol      : {s.MzMatchTolerance}");
    Console.WriteLine($"-> Osprey fragment tol : {s.EffectiveProductTolerance} (product), {s.EffectivePrecursorTolerance} (precursor)");
    return 0;
}

static int RunM0(string[] args)
{
    string? xics = null;
    string? blib = null;
    string? sky = null;
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--xics" when i + 1 < args.Length:
                xics = args[++i];
                break;
            case "--blib" when i + 1 < args.Length:
                blib = args[++i];
                break;
            case "--sky" when i + 1 < args.Length:
                sky = args[++i];
                break;
            default:
                Console.Error.WriteLine($"Unrecognized argument '{args[i]}'.");
                return 2;
        }
    }

    if (xics is null || blib is null)
    {
        Console.Error.WriteLine("m0 requires --xics <export.tsv> and --blib <library.blib> (--sky <doc.sky> optional).");
        return 2;
    }
    if (!File.Exists(xics))
    {
        Console.Error.WriteLine($"XIC export not found: {xics}");
        return 1;
    }
    if (!File.Exists(blib))
    {
        Console.Error.WriteLine($"Blib not found: {blib}");
        return 1;
    }

    Console.WriteLine($"Reading chromatograms: {xics}");
    var chromatograms = ChromatogramTsvReader.ReadGroups(xics);
    Console.WriteLine($"Reading library      : {blib}");
    var library = new BlibReader(blib).ReadEntries();

    var report = M0Report.Build(chromatograms, library);
    Console.WriteLine();
    Console.WriteLine(report.Format());

    if (sky is not null && File.Exists(sky))
    {
        ReportFeatureAvailability(sky);
    }

    // Non-zero exit if the core premises fail, so CI / scripts can gate on it.
    var ok = report.GroupsUnmatched == 0 && report.GroupsInconsistentGrid == 0;
    return ok ? 0 : 1;
}

// Reports which Osprey feature families will actually be computed, given raw-file accessibility,
// and warns about any that are dropped (per the user's request to surface unused scores).
static void ReportFeatureAvailability(string sky)
{
    var raw = RawFileAvailability.FromDocument(sky);
    var selection = FeatureSelection.Resolve(observedSpectraAvailable: raw.AllAccessible);

    Console.WriteLine("Feature availability");
    Console.WriteLine("====================");
    Console.WriteLine($"Raw files referenced   : {raw.AllPaths.Count} " +
        $"({raw.AccessiblePaths.Count} accessible, {raw.MissingPaths.Count} missing)");
    Console.WriteLine($"Osprey features in use  : {selection.FeatureIndices.Count} of 21 " +
        $"[{string.Join(",", selection.FeatureIndices)}]");
    if (raw.AnyMissing)
    {
        var show = Math.Min(raw.MissingPaths.Count, 3);
        for (var i = 0; i < show; i++)
        {
            Console.WriteLine($"  missing raw: {raw.MissingPaths[i]}");
        }
        if (raw.MissingPaths.Count > show)
        {
            Console.WriteLine($"  ... and {raw.MissingPaths.Count - show} more");
        }
    }
    Console.WriteLine();
    foreach (var warning in selection.Warnings)
    {
        Console.WriteLine(warning);
    }
}

static void PrintUsage()
{
    Console.WriteLine("ospreytool - Osprey-powered peak detection, picking and scoring for Skyline PRM data");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  reconcile   Re-pick + reconcile across runs, write Skyline peak boundaries (the main command)");
    Console.WriteLine("  explain     Dump the candidate peaks + score breakdown for one peptide (diagnostics)");
    Console.WriteLine("  repick      Re-pick peaks per run only (no cross-run reconciliation)");
    Console.WriteLine("  decoyfdr    Genuine target/decoy Percolator FDR over the re-picked peaks");
    Console.WriteLine("  score       Score targets + decoy-XICs and report per-feature separation (M0 harness)");
    Console.WriteLine("  m0          Join the chromatogram export with the library and sanity-check RTs");
    Console.WriteLine("  settings    Print the transition settings read from a .sky document");
    Console.WriteLine("  --version   Print the version");
    Console.WriteLine();
    Console.WriteLine("Typical use (write reconciled boundaries, then import them into Skyline):");
    Console.WriteLine("  ospreytool reconcile --xics export.tsv --blib library.blib --decoys decoys.tsv \\");
    Console.WriteLine("             --rt-csv doc_rt.csv --no-fdr --all-targets --lib-cosine \\");
    Console.WriteLine("             --out boundaries.csv --report report.csv");
    Console.WriteLine();
    Console.WriteLine("  ospreytool explain --xics export.tsv --blib library.blib --peptide PEPTIDEK \\");
    Console.WriteLine("             --replicate MMCC-2-001 --decoys decoys.tsv --rt-csv doc_rt.csv");
    Console.WriteLine();
    Console.WriteLine("Key options (reconcile / explain):");
    Console.WriteLine("  --xics <export.tsv>      Skyline chromatogram export (required)");
    Console.WriteLine("  --blib <library.blib>    spectral library, for the predicted RT + fragment intensities");
    Console.WriteLine("  --rt-csv <rt.csv>        expected RTs from the document (ExplicitRetentionTime); beats the blib RT");
    Console.WriteLine("  --decoys <decoys.tsv>    decoy precursors, enabling FDR and the reconcile action column");
    Console.WriteLine("  --out <boundaries.csv>   Skyline --import-peak-boundaries file to write");
    Console.WriteLine("  --report <report.csv>    per precursor x replicate detail (boundaries, scores, action)");
    Console.WriteLine("  --detector <id>          peak detector: " + PeakDetectors.Ids + " (default osprey-cwt)");
    Console.WriteLine("  --lib-cosine             multiply median_polish_cosine into the pick score");
    Console.WriteLine("  --intensity-exp <w>      exponent on the ln(1+I) term; 1 = Osprey-exact, 0 = off");
    Console.WriteLine("  --rt-sigma <min>         width of the Gaussian RT prior (default 0.3)");
    Console.WriteLine("  --rt-tol <min>           hard RT gate; 0 = off (scheduled PRM: the window is the limit)");
    Console.WriteLine("  --no-fdr                 confidence from co-elution instead of Percolator");
    Console.WriteLine("  --all-targets            reconcile every target (targeted assay), not only confident ones");
    Console.WriteLine("  --no-charge-consensus    disable intra-run charge-state consensus");
    Console.WriteLine("  --no-reconcile           disable inter-run consensus + reconciliation");
    Console.WriteLine();
    Console.WriteLine("Export <export.tsv> from Skyline with:");
    Console.WriteLine("  --chromatogram-file=export.tsv --chromatogram-precursors --chromatogram-products");
    Console.WriteLine();
    Console.WriteLine("Full documentation: https://github.com/maccoss/skyline-osprey-tool#command-line");
}
