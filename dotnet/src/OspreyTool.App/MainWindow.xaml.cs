using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using OspreyTool.Core;
using OspreyTool.Core.Detection;
using OspreyTool.Scoring;
using OspreyTool.Scoring.Detection;
using OspreyTool.Skyline;

namespace OspreyTool.App;

public partial class MainWindow : Window
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private const string LibraryListType = "Spectral Libraries";

    private readonly ObservableCollection<CandidateRow> _candidates = new();
    private readonly ISkylineExecutor? _session;
    private bool _connected;

    // Inputs gathered from the live document (chromatograms + RT + library intensities), cached across
    // Run/Explain within a scoring mode. Fragment XICs of the explained peptide (per replicate) for the plot.
    private PipelineInputs? _docInputs;
    private string _docInputsKey = "";
    private readonly Dictionary<string, IReadOnlyList<XicData>> _xicByFile = new();

    // Skyline colours product ions starting AFTER the precursor colours, so matching its palette needs the
    // number of precursor transitions in the group (see SkylineColorScheme.ColorFor).
    private readonly Dictionary<string, int> _precursorCountByFile = new();
    private IReadOnlyList<SchemeColor> _transitionColors = SkylineColorScheme.ClassicTransitions;
    private double? _explainExpectedRt;

    // The last reconciliation, so the Candidate Peaks panel can show what actually landed in the document
    // (a forced boundary is not a CWT candidate, so the panel cannot derive it from the picker alone).
    private ReconcileSummary? _summary;
    private string _summaryKey = "";

    // The seam has no push notification for selection changes, so poll the Targets-tree selection and
    // re-explain only when it actually changes.
    private readonly DispatcherTimer _selectionTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private string _lastSelectionKey = "";
    private bool _pollBusy;
    private bool _explainBusy;
    private bool _loggedPollError;

    public MainWindow(ISkylineExecutor? session = null)
    {
        InitializeComponent();
        _session = session;
        CandidatesGrid.ItemsSource = _candidates;
        DetectorCombo.ItemsSource = PeakDetectors.All;
        DetectorCombo.DisplayMemberPath = nameof(IPeakDetector.DisplayName);
        DetectorCombo.SelectedIndex = 0;
        Loaded += (_, _) => TestConnection();
    }

    // ---- Skyline connection ----
    private void TestConnection()
    {
        if (_session is null)
        {
            SetConnected(false, "Not connected — launch this tool from Skyline's Tools menu.");
            return;
        }
        try
        {
            var info = _session.Execute(c => (Doc: c.GetDocumentPath(), Ver: c.GetVersion()));
            _connected = true;
            SetConnected(true, $"Connected: {Path.GetFileName(info.Doc)}  (Skyline {info.Ver})");
            StatusText.Text = "Run reconciles the document and writes the peak boundaries back.";
            PopulateLibraries();
            PopulateColorSchemes();
            _selectionTimer.Tick += OnSelectionPoll;
            _selectionTimer.Start();
        }
        catch (Exception ex)
        {
            SetConnected(false, "Connection failed — see Log.");
            Log("Connection error: " + ex.Message);
        }
    }

    private void PopulateLibraries()
    {
        try
        {
            var names = _session!.Execute(c => c.GetSettingsListNames(LibraryListType));
            var selected = _session!.Execute(c => c.GetSettingsListSelectedItems(LibraryListType));
            LibraryCombo.ItemsSource = names;
            LibraryCombo.SelectedItem = selected.FirstOrDefault() ?? names.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log("Could not list spectral libraries: " + ex.Message);
        }
    }

    /// <summary>Lists Skyline's colour schemes (Tools &gt; Options &gt; Display) and loads the selected one. Which
    /// scheme is ACTIVE is an application setting Skyline does not expose over the seam, so we default to
    /// "Skyline classic" and let the user switch.</summary>
    private void PopulateColorSchemes()
    {
        try
        {
            var names = _session!.Execute(c => c.GetSettingsListNames(SkylineColorScheme.ListType));
            if (names.Length == 0)
            {
                return;
            }
            ColorSchemeCombo.ItemsSource = names;
            ColorSchemeCombo.SelectedItem = names.Contains(SkylineColorScheme.DefaultName)
                ? SkylineColorScheme.DefaultName
                : names[0];
        }
        catch (Exception ex)
        {
            Log("Could not list colour schemes (using Skyline classic): " + ex.Message);
        }
    }

    private void OnColorSchemeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsInitialized || !_connected || ColorSchemeCombo.SelectedItem is not string name)
        {
            return;
        }
        try
        {
            var xml = _session!.Execute(c => c.GetSettingsListItem(SkylineColorScheme.ListType, name));
            _transitionColors = SkylineColorScheme.Parse(xml).Transitions;
            if (CandidatesGrid.SelectedItem is CandidateRow row)
            {
                PlotReplicate(row.File, row);
            }
        }
        catch (Exception ex)
        {
            Log($"Could not read colour scheme '{name}' (using Skyline classic): " + ex.Message);
            _transitionColors = SkylineColorScheme.ClassicTransitions;
        }
    }

    private void SetConnected(bool ok, string text)
    {
        ConnDot.Fill = new System.Windows.Media.SolidColorBrush(ok
            ? System.Windows.Media.Color.FromRgb(0x2e, 0xa0, 0x43)
            : System.Windows.Media.Color.FromRgb(0x99, 0x99, 0x99));
        ConnText.Text = text;
    }

    // ---- settings -> config ----
    private bool UseCosine => ScoringCombo.SelectedIndex == 1; // "Osprey + median_polish_cosine"

    /// <summary>The peak-detection algorithm chosen in Settings (pluggable - see IPeakDetector).</summary>
    private IPeakDetector SelectedDetector =>
        DetectorCombo.SelectedItem as IPeakDetector ?? PeakDetectors.Default;

    private ReconcileConfig BuildConfig() => new()
    {
        UseFdr = false, // FDR path needs decoys in the document; not wired into the connected workflow yet.
        AllTargets = AllTargetsCheck.IsChecked == true,
        ChargeConsensus = ChargeConsensusCheck.IsChecked == true,
        InterRunReconcile = ReconcileCheck.IsChecked == true,
        GateThreshold = 0.5,
        RtTolerance = ParseD(RtTolBox.Text, 0.0),
        RtSigma = ParseD(RtSigmaBox.Text, 0.3),
        IntensityExponent = ParseD(IntensityExpBox.Text, 1.0),
        Detector = SelectedDetector,
    };

    /// <summary>Signature of the settings a reconciliation was produced under, so the Candidate Peaks panel
    /// can say when the cached reconciliation no longer matches the current settings.</summary>
    private string SettingsKey() =>
        $"{UseCosine}|{RtSigmaBox.Text}|{RtTolBox.Text}|{IntensityExpBox.Text}|{AllTargetsCheck.IsChecked}|" +
        $"{ChargeConsensusCheck.IsChecked}|{ReconcileCheck.IsChecked}|{SelectedDetector.Id}";

    private static double ParseD(string t, double dflt) =>
        double.TryParse(t, NumberStyles.Float, Ci, out var v) ? v : dflt;

    // ---- gather inputs from the live document (chromatograms + RT + library intensities) ----
    private async Task<PipelineInputs> EnsureDocInputs()
    {
        var key = UseCosine ? "cos" : "nocos";
        if (_docInputs is not null && _docInputsKey == key)
        {
            return _docInputs;
        }
        var session = _session!;
        var xicTemp = Path.Combine(Path.GetTempPath(), $"osprey_xic_{Guid.NewGuid():N}.tsv");
        try
        {
            Log("Exporting chromatograms from Skyline ...");
            await Task.Run(() => session.Execute(c => c.RunCommandSilent(new[]
            {
                $"--chromatogram-file={xicTemp}", "--chromatogram-precursors", "--chromatogram-products",
            })));

            Log("Reading retention times ...");
            var rtRows = await Task.Run(() => session.Execute(c =>
                c.GetReport(new[] { "ModifiedSequence", "PrecursorCharge", "ExplicitRetentionTime" })));

            IReadOnlyDictionary<PrecursorKey, IReadOnlyList<LibraryFragment>>? fragments = null;
            if (UseCosine)
            {
                Log("Reading library fragment intensities ...");
                var libRows = await Task.Run(() => session.Execute(c =>
                    c.GetReport(new[] { "PeptideModifiedSequence", "PrecursorCharge", "ProductMz", "LibraryIntensity" })));
                fragments = FragmentsFromReport(libRows);
            }

            var groups = await Task.Run(() => ChromatogramTsvReader.ReadGroups(xicTemp));
            _docInputs = PipelineInputs.FromParts(groups, RtFromReport(rtRows), fragments);
            _docInputsKey = key;
            return _docInputs;
        }
        finally
        {
            try { if (File.Exists(xicTemp)) { File.Delete(xicTemp); } } catch { /* best effort */ }
        }
    }

    // ---- run ----
    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (!_connected)
        {
            Log("Not connected to Skyline. Launch this tool from Skyline's Tools menu.");
            MainTabs.SelectedIndex = 2;
            return;
        }
        RunButton.IsEnabled = false;
        MainTabs.SelectedIndex = 2; // Log
        try
        {
            var config = BuildConfig();
            var useCosine = UseCosine; // capture UI-bound state on the UI thread (Task.Run runs off it)
            var inp = await EnsureDocInputs();
            Log($"Scoring + reconciling (scoring: {(useCosine ? "Osprey + median_polish_cosine" : "Osprey")}) ...");
            var summary = await Task.Run(() => ReconciliationPipeline.Run(
                inp.Groups, inp.RtByTarget, inp.DecoyKeys, inp.RtByDecoy, config, useCosine ? inp.Fragments : null));
            LogSummary(summary);
            _summary = summary;
            _summaryKey = SettingsKey();
            _lastSelectionKey = ""; // refresh the Candidate Peaks panel against the new reconciliation

            var boundsTemp = Path.Combine(Path.GetTempPath(), $"osprey_bounds_{Guid.NewGuid():N}.csv");
            try
            {
                var written = WriteBoundaries(boundsTemp, summary);
                Log($"Importing {written} reconciled boundaries into Skyline ...");
                await Task.Run(() => _session!.Execute(c =>
                    c.RunCommandSilent(new[] { $"--import-peak-boundaries={boundsTemp}" })));
                Log("Done. The Skyline document is updated.");
                StatusText.Text = $"Updated {written} peak boundaries in Skyline.";
            }
            finally
            {
                try { if (File.Exists(boundsTemp)) { File.Delete(boundsTemp); } } catch { /* best effort */ }
            }
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            StatusText.Text = "Failed — see Log.";
        }
        finally
        {
            RunButton.IsEnabled = true;
        }
    }

    // ---- candidate peaks (explain) ----
    // ---- follow the Skyline Targets-tree selection ----
    private void OnFollowChanged(object sender, RoutedEventArgs e)
    {
        // The checkbox is IsChecked="True" in XAML, so Checked fires DURING InitializeComponent - while the
        // controls below it in the tree are still null. Do nothing until the window is built (their XAML
        // IsEnabled already matches the checked state).
        if (!IsInitialized)
        {
            return;
        }
        var follow = FollowSelectionCheck.IsChecked == true;
        ExplainPeptideBox.IsEnabled = !follow;
        ExplainReplicateBox.IsEnabled = !follow;
        ExplainButton.IsEnabled = !follow;
        _lastSelectionKey = ""; // re-explain the current selection when following is switched back on
    }

    private async void OnSelectionPoll(object? sender, EventArgs e)
    {
        if (!_connected || FollowSelectionCheck.IsChecked != true || _pollBusy || _explainBusy)
        {
            return;
        }
        _pollBusy = true;
        try
        {
            var sel = await Task.Run(() => _session!.Execute(SkylineSelectionReader.Read));
            if (sel.PeptideModifiedSequence is null)
            {
                return; // a protein (or nothing) is selected
            }
            var key = $"{sel.PeptideModifiedSequence}|{sel.PrecursorCharge}|{sel.Replicate}";
            if (key == _lastSelectionKey)
            {
                return;
            }
            _lastSelectionKey = key;
            ExplainPeptideBox.Text = sel.PeptideModifiedSequence;
            ExplainReplicateBox.Text = sel.Replicate ?? "";
            await ExplainAsync(sel.PeptideModifiedSequence, sel.Replicate ?? "", sel.PrecursorCharge);
        }
        catch (Exception ex)
        {
            if (!_loggedPollError) // a busy Skyline can refuse the pipe; log once, keep polling
            {
                _loggedPollError = true;
                Log("Selection polling error (will keep trying): " + ex.Message);
            }
        }
        finally
        {
            _pollBusy = false;
        }
    }

    private async void OnExplain(object sender, RoutedEventArgs e) =>
        await ExplainAsync(ExplainPeptideBox.Text.Trim(), ExplainReplicateBox.Text.Trim(), null);

    private async Task ExplainAsync(string peptide, string replicate, int? charge)
    {
        _candidates.Clear();
        _xicByFile.Clear();
        _precursorCountByFile.Clear();
        _explainExpectedRt = null;
        ExplainHeader.Text = "";
        if (!_connected)
        {
            ExplainHeader.Text = "Not connected to Skyline.";
            return;
        }
        if (peptide.Length == 0)
        {
            ExplainHeader.Text = "Select a peptide in Skyline's Targets tree.";
            return;
        }
        _explainBusy = true;
        try
        {
            var rtSigma = ParseD(RtSigmaBox.Text, 0.3);
            var rtTol = ParseD(RtTolBox.Text, 0.0);
            var intensityExp = ParseD(IntensityExpBox.Text, 1.0);
            var useCosine = UseCosine; // capture UI-bound state on the UI thread
            var detector = SelectedDetector;
            var inp = await EnsureDocInputs();
            var reconciled = ReconciledRows(peptide, charge); // what Run actually wrote to the document
            var rows = await Task.Run(() =>
            {
                var scorer = OspreyFeatureScorer.CreateDefault(detector);
                var results = new List<(string File, int Charge, double? Rt, IReadOnlyList<CandidatePeak>? Cands,
                    List<XicData> Frags, ReconcileRow? Recon, CandidatePeak? Applied, int PrecursorCount)>();
                foreach (var g in SelectGroups(inp.Groups, peptide, replicate, charge))
                {
                    var key = new PrecursorKey(ModifiedSequence.Normalize(g.PeptideModifiedSequence), g.PrecursorCharge);
                    double? expected = inp.RtByTarget.TryGetValue(key, out var rt) ? rt : null;
                    IReadOnlyList<LibraryFragment>? frags = null;
                    inp.Fragments?.TryGetValue(key, out frags);
                    var libFrags = useCosine ? frags : null;
                    var r = scorer.Repick(g.Xics, expected, rtTol, rtSigma, 0.0,
                        libraryFragments: libFrags, traceCandidates: true,
                        intensityExponent: intensityExp);

                    // The window reconciliation actually applied. When it is not one of the CWT candidates
                    // (force-integration at the consensus RT), score it on the same terms so the panel can
                    // show it next to the peak the signal processing chose.
                    reconciled.TryGetValue((g.FileName, g.PrecursorCharge), out var recon);
                    CandidatePeak? applied = null;
                    if (recon is not null && !double.IsNaN(recon.MinStartTime) && !double.IsNaN(recon.MaxEndTime) &&
                        !MatchesACandidate(r.CandidatePeaks, recon))
                    {
                        applied = scorer.ScoreWindow(g.Xics, recon.MinStartTime, recon.MaxEndTime, expected,
                            rtSigma, libFrags, intensityExp);
                    }
                    results.Add((g.FileName, g.PrecursorCharge, expected, r.CandidatePeaks,
                        g.Xics.Where(x => !x.IsPrecursor).ToList(), recon, applied,
                        g.Xics.Count(x => x.IsPrecursor)));
                }
                return results;
            });

            if (rows.Count == 0)
            {
                ExplainHeader.Text = $"No chromatograms for '{peptide}'. Run first, or re-export.";
                return;
            }
            _explainExpectedRt = rows[0].Rt;
            var reconNote = _summary is null
                ? "  |  reconciliation: (Run to see it)"
                : _summaryKey == SettingsKey() ? "" : "  |  reconciliation: STALE - re-Run";
            ExplainHeader.Text = $"{peptide}  |  {rows.Count} replicate(s)  |  expected RT " +
                (rows[0].Rt is { } rt0 ? rt0.ToString("F2", Ci) : "n/a") + reconNote +
                "   (CHOSEN = signal processing; APPLIED = what reconciliation wrote to the document)";
            CandidateRow? firstChosen = null;
            foreach (var (file, _, _, cands, fragXics, recon, applied, precursorCount) in rows)
            {
                _xicByFile[ShortName(file)] = fragXics;
                _precursorCountByFile[ShortName(file)] = precursorCount;
                foreach (var c in (cands ?? Array.Empty<CandidatePeak>()).OrderByDescending(c => c.Rank))
                {
                    var row = new CandidateRow
                    {
                        File = ShortName(file),
                        Source = "CWT",
                        ApexRt = c.ApexRt,
                        StartRt = c.StartRt,
                        EndRt = c.EndRt,
                        Coelution = c.Coelution,
                        LibCosine = c.LibCosine,
                        RtResidual = c.RtResidual,
                        RtPenalty = c.RtPenalty,
                        IntensityWeight = c.IntensityWeight,
                        Rank = c.Rank,
                        Pick = c.Chosen ? "CHOSEN" : (c.SecondBest ? "2nd" : ""),
                        // Reconciliation kept this candidate (or snapped to it): it is what the document holds.
                        Applied = recon is not null && applied is null && SameWindow(c, recon) ? recon.Action : "",
                    };
                    _candidates.Add(row);
                    firstChosen ??= c.Chosen ? row : null;
                }

                // Reconciliation forced a window that is NOT a CWT candidate - show it, scored the same way,
                // so it is obvious what the consensus overrode the signal processing with.
                if (recon is not null && applied is not null)
                {
                    _candidates.Add(new CandidateRow
                    {
                        File = ShortName(file),
                        Source = "consensus",
                        ApexRt = applied.ApexRt,
                        StartRt = applied.StartRt,
                        EndRt = applied.EndRt,
                        Coelution = applied.Coelution,
                        LibCosine = applied.LibCosine,
                        RtResidual = applied.RtResidual,
                        RtPenalty = applied.RtPenalty,
                        IntensityWeight = applied.IntensityWeight,
                        Rank = applied.Rank,
                        Pick = "",
                        Applied = recon.Action,
                    });
                }
            }
            var seed = firstChosen ?? _candidates.FirstOrDefault();
            if (seed is not null)
            {
                PlotReplicate(seed.File, seed);
            }
        }
        catch (Exception ex)
        {
            ExplainHeader.Text = "ERROR: " + ex.Message;
        }
        finally
        {
            _explainBusy = false;
        }
    }

    /// <summary>The last Run's reconciled rows for this peptide, keyed by (file, charge). Empty when the tool
    /// has not been Run yet - the panel then shows the signal processing only.</summary>
    private Dictionary<(string File, int Charge), ReconcileRow> ReconciledRows(string peptide, int? charge)
    {
        var map = new Dictionary<(string, int), ReconcileRow>();
        if (_summary is null)
        {
            return map;
        }
        var norm = ModifiedSequence.Normalize(peptide);
        foreach (var r in _summary.Rows)
        {
            if (r.IsDecoy || (charge is { } z && r.PrecursorCharge != z))
            {
                continue;
            }
            if (ModifiedSequence.Normalize(r.PeptideModifiedSequence).Equals(norm, StringComparison.Ordinal))
            {
                map[(r.FileName, r.PrecursorCharge)] = r;
            }
        }
        return map;
    }

    // Boundaries are written from the candidate's own start/end, so an applied CWT candidate matches to well
    // within a scan; a forced window generally will not line up with any candidate.
    private const double WindowTol = 0.005;

    private static bool SameWindow(CandidatePeak c, ReconcileRow r) =>
        Math.Abs(c.StartRt - r.MinStartTime) < WindowTol && Math.Abs(c.EndRt - r.MaxEndTime) < WindowTol;

    private static bool MatchesACandidate(IReadOnlyList<CandidatePeak>? cands, ReconcileRow r) =>
        cands is not null && cands.Any(c => SameWindow(c, r));

    /// <summary>The chromatogram groups for the selected peptide, narrowed by replicate and charge only when
    /// that narrowing actually leaves something (Skyline's replicate name need not equal the raw file name,
    /// and the tree selection may sit above the precursor level).</summary>
    private static List<PrecursorChromatograms> SelectGroups(
        IReadOnlyList<PrecursorChromatograms> groups, string peptide, string replicate, int? charge)
    {
        var norm = ModifiedSequence.Normalize(peptide);
        var hits = groups
            .Where(g => ModifiedSequence.Normalize(g.PeptideModifiedSequence).Equals(norm, StringComparison.Ordinal))
            .ToList();
        if (charge is { } z)
        {
            var byCharge = hits.Where(g => g.PrecursorCharge == z).ToList();
            if (byCharge.Count > 0)
            {
                hits = byCharge;
            }
        }
        if (replicate.Length > 0)
        {
            var byFile = hits.Where(g => g.FileName.Contains(replicate, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byFile.Count > 0)
            {
                hits = byFile;
            }
        }
        return hits;
    }

    private void OnCandidateSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CandidatesGrid.SelectedItem is CandidateRow row)
        {
            PlotReplicate(row.File, row);
        }
    }

    private void PlotReplicate(string shortFile, CandidateRow? highlight)
    {
        var plt = XicPlot.Plot;
        plt.Clear();
        if (_xicByFile.TryGetValue(shortFile, out var frags))
        {
            // Colour the fragments exactly as Skyline's chromatogram graph does, so the two read as one view.
            _precursorCountByFile.TryGetValue(shortFile, out var precursorCount);
            for (var i = 0; i < frags.Count; i++)
            {
                var f = frags[i];
                var c = SkylineColorScheme.ColorFor(_transitionColors, i, precursorCount);
                var sc = plt.Add.Scatter(f.RetentionTimes, f.Intensities);
                sc.MarkerSize = 0;
                sc.LineWidth = 1;
                sc.Color = new ScottPlot.Color(c.R, c.G, c.B);
                sc.LegendText = f.FragmentIon;
            }
        }
        if (_explainExpectedRt is { } exp)
        {
            var vl = plt.Add.VerticalLine(exp);
            vl.Color = ScottPlot.Colors.Gray;
            vl.LinePattern = ScottPlot.LinePattern.Dotted;
        }
        if (highlight is not null)
        {
            var s = plt.Add.VerticalLine(highlight.StartRt);
            s.Color = ScottPlot.Colors.Red;
            s.LinePattern = ScottPlot.LinePattern.Dashed;
            var en = plt.Add.VerticalLine(highlight.EndRt);
            en.Color = ScottPlot.Colors.Red;
            en.LinePattern = ScottPlot.LinePattern.Dashed;
        }
        plt.Axes.AutoScale();
        plt.Axes.Title.Label.Text = highlight is null ? shortFile : $"{shortFile}   (apex {highlight.ApexRt:F2})";
        XicPlot.Refresh();
    }

    private void OnShowCommandLine(object sender, RoutedEventArgs e)
    {
        var c = BuildConfig();
        var cmd = "ospreytool reconcile --xics <export.tsv> --blib <lib.blib> --rt-csv <docRT.csv>" +
            (c.AllTargets ? " --all-targets" : "") +
            (UseCosine ? " --lib-cosine" : "") +
            " --no-fdr" +
            (c.ChargeConsensus ? "" : " --no-charge-consensus") +
            (c.InterRunReconcile ? "" : " --no-reconcile") +
            $" --rt-sigma {c.RtSigma.ToString(Ci)} --rt-tol {c.RtTolerance.ToString(Ci)} --out <bounds.csv>";
        Log("CLI equivalent (for exported files):");
        Log("  " + cmd);
        MainTabs.SelectedIndex = 2;
    }

    // ---- report parsing ----
    private static int Col(IReadOnlyList<string> cols, params string[] names)
    {
        for (var i = 0; i < cols.Count; i++)
        {
            if (names.Any(n => cols[i].Trim().Equals(n, StringComparison.OrdinalIgnoreCase)))
            {
                return i;
            }
        }
        return -1;
    }

    private static Dictionary<PrecursorKey, double> RtFromReport(ReportRows? rows)
    {
        var map = new Dictionary<PrecursorKey, double>();
        if (rows is null)
        {
            return map;
        }
        int iSeq = Col(rows.Columns, "ModifiedSequence", "PeptideModifiedSequence");
        int iCharge = Col(rows.Columns, "PrecursorCharge");
        int iRt = Col(rows.Columns, "ExplicitRetentionTime", "PredictedResultRetentionTime");
        if (iSeq < 0 || iCharge < 0 || iRt < 0)
        {
            return map;
        }
        foreach (var r in rows.Rows)
        {
            if (int.TryParse(r[iCharge], out var z) && double.TryParse(r[iRt], NumberStyles.Float, Ci, out var rt))
            {
                map[new PrecursorKey(ModifiedSequence.Normalize(r[iSeq]), z)] = rt;
            }
        }
        return map;
    }

    private static Dictionary<PrecursorKey, IReadOnlyList<LibraryFragment>> FragmentsFromReport(ReportRows? rows)
    {
        var byKey = new Dictionary<PrecursorKey, List<LibraryFragment>>();
        if (rows is null)
        {
            return new Dictionary<PrecursorKey, IReadOnlyList<LibraryFragment>>();
        }
        int iSeq = Col(rows.Columns, "PeptideModifiedSequence", "ModifiedSequence");
        int iCharge = Col(rows.Columns, "PrecursorCharge");
        int iMz = Col(rows.Columns, "ProductMz");
        int iInt = Col(rows.Columns, "LibraryIntensity");
        if (iSeq < 0 || iCharge < 0 || iMz < 0 || iInt < 0)
        {
            return new Dictionary<PrecursorKey, IReadOnlyList<LibraryFragment>>();
        }
        foreach (var r in rows.Rows)
        {
            if (!int.TryParse(r[iCharge], out var z)
                || !double.TryParse(r[iMz], NumberStyles.Float, Ci, out var mz)
                || !double.TryParse(r[iInt], NumberStyles.Float, Ci, out var inten)) // #N/A on precursor rows -> skip
            {
                continue;
            }
            var key = new PrecursorKey(ModifiedSequence.Normalize(r[iSeq]), z);
            if (!byKey.TryGetValue(key, out var list))
            {
                list = new List<LibraryFragment>();
                byKey[key] = list;
            }
            list.Add(new LibraryFragment(mz, (float)inten));
        }
        return byKey.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<LibraryFragment>)kv.Value);
    }

    private int WriteBoundaries(string path, ReconcileSummary summary)
    {
        var written = 0;
        using var w = new StreamWriter(path);
        w.WriteLine("PeptideModifiedSequence,FileName,PrecursorCharge,MinStartTime,MaxEndTime");
        foreach (var r in summary.Rows.Where(r => !r.IsDecoy && !double.IsNaN(r.MinStartTime) && !double.IsNaN(r.MaxEndTime)))
        {
            w.WriteLine($"{r.PeptideModifiedSequence},{r.FileName},{r.PrecursorCharge}," +
                $"{r.MinStartTime.ToString("R", Ci)},{r.MaxEndTime.ToString("R", Ci)}");
            written++;
        }
        return written;
    }

    private void LogSummary(ReconcileSummary summary)
    {
        Log($"Targets scored/passing : {summary.TargetsScored} / {summary.TargetsPassing}");
        Log($"Consensus peptides     : {summary.ConsensusPeptides}");
        Log($"Charge-consensus moves : {summary.ChargeConsensusMoves}");
        Log($"Snapped to CWT         : {summary.UseCwtMoves}");
        Log($"Forced integrations    : {summary.ForcedIntegrations}");
        foreach (var w in summary.Warnings)
        {
            Log($"  ! {w}");
        }
    }

    private static string ShortName(string file)
    {
        try { return Path.GetFileNameWithoutExtension(file); } catch { return file; }
    }

    private void Log(string message)
    {
        LogBox.AppendText(message + Environment.NewLine);
        LogBox.ScrollToEnd();
    }
}

/// <summary>Row bound to the Candidate Peaks DataGrid.</summary>
public sealed class CandidateRow
{
    public string File { get; init; } = "";

    /// <summary>"CWT" = a candidate the signal processing found. "consensus" = a window reconciliation
    /// forced that is NOT a CWT candidate (MBR force-integration at the cross-run consensus RT), scored
    /// here on the same terms so it can be compared with what the signal processing chose.</summary>
    public string Source { get; init; } = "CWT";
    public double ApexRt { get; init; }
    public double StartRt { get; init; }
    public double EndRt { get; init; }
    public double Coelution { get; init; }
    public double LibCosine { get; init; }
    public double RtResidual { get; init; }
    public double RtPenalty { get; init; }
    public double IntensityWeight { get; init; }
    public double Rank { get; init; }

    /// <summary>What the signal processing chose: CHOSEN / 2nd / blank.</summary>
    public string Pick { get; init; } = "";

    /// <summary>Set on the row whose boundaries were actually written to the Skyline document, naming the
    /// reconcile action that put them there (keep / charge-consensus / use-cwt / forced-integration).</summary>
    public string Applied { get; set; } = "";
}
