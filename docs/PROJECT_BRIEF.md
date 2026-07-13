# HANDOFF: Stellar-PRM interactive detection/scoring tool for Skyline (Osprey-powered)

> **This is a project brief / hand-off for a fresh Claude Code session in a new repo.** It is self-
> contained: a new session with no prior context can start from this document. All API signatures, file
> paths, and decisions below were verified by reading source in `ProteoWizard/pwiz` (Osprey + Skyline),
> `maccoss/carafe`, and the local `skyline-prism` repo. Save it into the new repo (e.g.
> `docs/PROJECT_BRIEF.md`). The reference implementation to model the tool on is **Skyline-PRISM** at
> `D:\GitHub-Repo\maccoss\skyline-prism\dotnet\` (its `docs/skyline-external-tools.md` is the tool-
> interface field guide). **Feasibility verdict: YES — most of the machinery already exists in Osprey;
> this is an integration project.**

## Context

We want a new **interactive Skyline external tool** (in the spirit of Skyline-PRISM) that performs
peptide **detection, peak picking, and confidence scoring** for **PRM data collected on a Thermo
Stellar** (unit-resolution quadrupole–linear-ion-trap), using a **Carafe-generated `.blib`** that
contains AI-predicted spectra + RT for all **targets and their paired decoys**.

The scoring engine we want to reuse is **Osprey** (`pwiz_tools/Osprey` in ProteoWizard/pwiz) — the
C#/.NET port of Mike MacCoss's Rust `osprey`. Research confirmed Osprey **already implements the entire
chain requested**: CWT peak picking, the DIA-NN-style co-elution correlation as its primary score, the
full 21-feature PIN vocabulary, a **native** Percolator (3-fold CV SVM → per-run q-value + PEP, no
external binary), and cross-run reconciliation/MBR. So this is fundamentally an **integration** project,
not a new-algorithm project.

**Intended outcome:** the analyst builds the Carafe target+decoy `.blib` in Skyline, imports the Stellar
PRM runs (Skyline extracts chromatograms), then launches this tool from Skyline's Tools menu. The tool
drives Osprey's CWT + a chosen subset of Osprey scores + Osprey's Percolator on **Skyline's already-
extracted chromatograms**, computes **per-run q-value/PEP**, reconciles detections **across runs**, and
writes results back into the live Skyline document (peak boundaries + q/PEP annotations) with an
interactive review UI.

### Decisions locked in (from clarification)
1. **Integration = embed in-process** via a thin new `Osprey.Api` facade that *references* the existing
   Osprey DLLs (no rewrite of Osprey internals).
2. **Chromatograms come from Skyline's extraction** of the Stellar raw data (Carafe supplies predicted
   spectra + RT + target/decoy pairs only). **No custom `.blib` schema** — a standard `.blib` cannot and
   need not store elution profiles. Osprey's correlation co-elutes the *observed* Skyline XICs against
   the *predicted* spectrum.
3. **New standalone tool repo** modeled on `skyline-prism`; the **`Osprey.Api` facade is contributed
   upstream to ProteoWizard/pwiz** so it is maintained alongside Osprey.

## Grounding facts (verified from source)

**The two integration hinges are both solved:**

- **Getting chromatograms out of Skyline — first-class.** `pwiz_tools/Skyline/Model/ExportChromatograms.cs`
  (`ChromatogramExporter`) + `CommandArgs.cs` flag `--chromatogram-file=<path>` (with
  `--chromatogram-precursors --chromatogram-products`) writes a TSV, **one row per (transition,
  replicate)**, columns: `FileName, PeptideModifiedSequence, PrecursorCharge, ProductMz, FragmentIon,
  ProductCharge, IsotopeLabelType, TotalArea, Times, Intensities` — with the full point-arrays in the
  `Times`/`Intensities` cells. Callable from a live tool via `RunCommandSilent(["--chromatogram-file=…", …])`.
  No `.skyd` parsing, no re-import.

- **Osprey's engine is reachable as a library** (all paths under `pwiz_tools/Osprey/`):
  - CWT: `Osprey.Chromatography/CwtPeakDetector.cs` → `public static List<XICPeakBounds>
    DetectConsensusPeaks(List<XicData> xics, double minConsensusHeight)`. `XicData` = `double[]
    RetentionTimes` + `double[] Intensities` per fragment (public DTO).
  - Features: `Osprey.Scoring/OspreyFeatureCalculators.cs` → **public** registry `Get(int)`; calculators
    are `internal` but typed against **public** interfaces `IOspreySummaryPeakData` /
    `IOspreyDetailedPeakData` / `IOspreyApexSpectrumPeakData` / `IOspreyApexSpectraPeakData`
    (`Osprey.Scoring/IOspreyPeakData.cs`). `OspreyScoringContext` (`OspreyScoringContext.cs`) has a public
    ctor `OspreyScoringContext(OspreyConfig)`.
  - FDR/ML (all public, DTO-based): `Osprey.FDR/PercolatorFdr.cs` (`RunPercolator(IList<PercolatorEntry>,
    PercolatorConfig)`, `ScorePopulationAndComputeFdr(...)`; `PercolatorEntry { double[] Features, bool
    IsDecoy, string Peptide, byte Charge, uint EntryId, double CoelutionSum }`), `Osprey.ML/
    QValueCalculator.cs`, `PepEstimator.cs`, `LinearSvmClassifier.cs`.
  - Reconciliation: `Osprey.FDR/Reconciliation/ReconciliationPlanner.cs` (`Plan(...)`, `DetermineAction(...)`).
  - Library load: `Osprey.IO/BlibLoader.cs` → `public List<LibraryEntry> Load(string path)` (parses the
    Carafe target+decoy `.blib` standalone). `LibraryEntry`/`LibraryFragment`/`Spectrum` are public
    (`Osprey.Core`).

- **The only Osprey internal blocker** is the 3 xcorr-family features (`xcorr`, `sg_weighted_xcorr`,
  `sg_weighted_cosine`) which need an internal `WindowXcorrCache`. **Excluded from the initial subset**
  (compatible with "use a subset of the scores").

**The tool's model — Skyline-PRISM's C# tool** (`skyline-prism/dotnet/`):
- WPF `WinExe` app Skyline launches (`SkylinePrism.App`, `Arguments=$(SkylineConnection)`).
- Connect-per-call JSON-RPC over named pipe with `ReadMode=Message` (`SkylinePrism.Skyline/
  SkylineSession.cs`); `ISkylineClient`/`ISkylineExecutor` seam for testing.
- Vendored RPC client `dotnet/external/SkylineTool/*` (Apache-2.0, link-compiled into the one Windows-only
  project) — `RunCommand`/`RunCommandSilent`, `ExportReport`, `ImportProperties`, `GetGraphData`, etc.
- Packaging + ship gate: `dotnet/build/package.proj`, `package-and-verify.ps1`, `verify-tool.ps1`;
  `tool-inf/info.properties` + `<Tool>.properties`; `Reports/*.skyr`.

## Architecture

Skyline (Carafe target+decoy `.blib` loaded, Stellar PRM imported)
        │  RunCommandSilent(--chromatogram-file)  → XIC TSV (Times[]/Intensities[] per transition×replicate)
        ▼
  **New interactive tool** (WPF, PRISM-modeled)  ── reads Carafe `.blib` directly (BlibLoader)
        │
        ▼   per (precursor, replicate):
  **Osprey.Api facade** (new; references Osprey DLLs)
     1. CwtPeakDetector.DetectConsensusPeaks(List<XicData>)            → candidate peak bounds
     2. adapter : IOspreyDetailedPeakData over the XICs + peak bounds  → feature-subset vector
        via OspreyFeatureCalculators.Get(i).Calculate(ctx, adapter)
        │
        ▼   per run (replicate): gather target+decoy feature vectors
     3. PercolatorFdr.RunPercolator + ScorePopulationAndComputeFdr     → per-run q-value + PEP
        │
        ▼   across runs
     4. ReconciliationPlanner.Plan(...)                                → experiment-level q + MBR actions
        │
        ▼
  Write-back via RunCommandSilent:
     - `--import-peak-boundaries=<csv>`  (chosen peak start/end per peptide per file)
     - custom annotations (`--annotation-*` + ImportProperties)  → per-precursor q-value / PEP / detection flag
     + interactive review UI (ScottPlot XIC + score panels), like PRISM's MainWindow

### `Osprey.Api` facade (new project, upstreamed to pwiz/Osprey)
A small project referencing `Osprey.Core/Chromatography/Scoring/FDR/ML/IO`. Public entry points:
1. `double[] ScorePrecursor(LibraryEntry candidate, IReadOnlyList<XicData> fragmentXics, double[] rts,
   double expectedRt, OspreyConfig config, int[] featureSubset)` — runs CWT, wraps results in a
   caller-side `IOspreyDetailedPeakData`, loops the chosen calculators. **No edits to existing Osprey
   classes** for the XIC-only subset.
2. `FdrResult[] RunFdr(IEnumerable<(double[] features, bool isDecoy, string peptide, byte charge)>,
   PercolatorConfig)` — wraps `PercolatorFdr` (per-run) → q-value + PEP.
3. `Reconcile(...)` — wraps `ReconciliationPlanner.Plan` for the inter-run step.
The adapter class implementing `IOspreyDetailedPeakData` lives here too. (If the 3 xcorr features are
later wanted, add a public factory for `WindowXcorrCache` in Osprey — deferred.)

### Initial feature subset (the "subset of Osprey scores")
Start with the XIC-only + RT + median-polish families that are cleanly derivable from Skyline's XIC
export (indices per `ParquetScoreCache.PIN_FEATURE_NAMES`):
`fragment_coelution_sum` (0, **primary**), `fragment_coelution_max` (1), `n_coeluting_fragments` (2),
`peak_apex` (3), `peak_area` (4), `peak_sharpness` (5), `rt_deviation` (11), `abs_rt_deviation` (12),
`median_polish_cosine` (15), `median_polish_residual_ratio` (16), `median_polish_min_fragment_r2` (19),
`median_polish_residual_correlation` (20).
**Deferred:** `ms1_*` (13,14 — HRAM/MS1 only; Stellar PRM has no useful HRAM MS1), the apex-match
spectrum features (7–10 — need the apex MS2 spectrum, not just XICs) and the xcorr family (6,17,18 —
need the internal cache). Adding apex-match/xcorr later requires **also** supplying apex MS2 spectra
(from the raw/mzML or a Skyline spectrum export) — a separate, optional extension.

## Milestones

**M0 — De-risking spike (validate science + embedding with ~no new Osprey code).**
On a real Stellar PRM `.sky` with the Carafe target+decoy `.blib` imported and results extracted:
`SkylineCmd --in=doc.sky --chromatogram-file=xics.tsv --chromatogram-precursors --chromatogram-products`.
Write a small console harness that: `BlibLoader.Load` the blib → per (precursor, replicate) build
`List<XicData>` from the TSV → `CwtPeakDetector.DetectConsensusPeaks` → compute the feature subset via
the `IOspreyDetailedPeakData` adapter + `OspreyFeatureCalculators` → per run, `PercolatorFdr.RunPercolator`
over target+decoy vectors. **Success = decoys separate from targets and per-run q-values/PEP are sane.**
This proves the whole premise before any UI work and independent of Osprey's mzML front-end.

**M1 — `Osprey.Api` facade project + unit tests** (the 3 entry points above), on a MacCoss pwiz branch;
open a draft PR to ProteoWizard/pwiz.

**M2 — New external-tool repo scaffold** cloned from skyline-prism's `dotnet/` structure: WPF app +
`SkylineSession` connect-per-call + vendored `SkylineTool` + `tool-inf/` manifest + `package.proj` /
`package-and-verify.ps1` ship gate. Tool exports XICs via `RunCommandSilent`, loads the blib, calls
`Osprey.Api`, logs per-run q/PEP.

**M3 — Inter-run reconciliation** wired via `ReconciliationPlanner`; experiment-level q-values + MBR
gap-fill actions.

**M4 — Write-back + interactive review UI**: `--import-peak-boundaries` for chosen peaks; q-value/PEP/
detection as custom annotations; ScottPlot XIC + score-panel review (PRISM `MainWindow` pattern), off-
UI-thread RPC.

**M5 — Packaging + docs + validation** on a real Stellar PRM cohort; compare against Skyline's native
mProphet DetectionQValue and against a full Osprey DIA run where available.

## Risks / caveats to surface
- **Carafe not validated on unit-resolution Stellar data** (its intensity models are Orbitrap/TOF-
  trained). Predicted *relative* fragment intensities may be less accurate at unit resolution; mitigate
  via Carafe fine-tuning and/or lean on co-elution (which uses observed XICs) over spectral-match scores.
  Osprey's `--resolution unit` matching mode is the relevant analogue for tolerances.
- **Decoys must be in the Skyline document** so Skyline extracts decoy chromatograms too — the Carafe
  target+decoy transition list/blib must be imported such that decoy transitions get XICs. Confirm how
  Carafe decoys map into Skyline (as decoy peptides vs. ordinary targets) during M0.
- **Per-run statistics need adequate target+decoy counts** per replicate for Percolator's SVM; small
  scheduled-PRM panels may need pooled-training or the `--fdr-method simple` fallback (both exist in
  Osprey).
- **`--chromatogram-file` cell format** (delimiter, raw vs interpolated grid, empty PRM product traces)
  must be spot-checked against the Skyline chromatogram graph in M0.
- **RT axis alignment** between Skyline's exported `Times[]` and Osprey's expected-RT / calibration
  inputs must be consistent (both in minutes; expected RT from the library's predicted RT).

## Verification
- **M0:** inspect `xics.tsv` (10-column header; equal-length `Times`/`Intensities` arrays; row count ≈
  transitions × replicates; a spot-checked trace matches Skyline's chromatogram graph). Confirm the
  harness produces a target/decoy score separation and a monotone q-value curve; sanity-check a handful
  of high-confidence peptides against Skyline's own peak picks.
- **M1:** `Osprey.Api` unit tests with synthetic XICs (known co-eluting vs. non-co-eluting fragments)
  asserting expected `fragment_coelution_sum` ordering and Percolator q/PEP monotonicity; run under the
  existing pwiz/Osprey test harness.
- **M2–M4:** drive the live tool against a running Skyline via the PRISM `FakeExecutor`/`ISkylineClient`
  seam for unit tests, and end-to-end against a real Skyline instance (the MCP `skyline` tools —
  `skyline_run_command`, `skyline_get_graph_data`, `skyline_get_report_from_definition` — can script the
  export/annotation round-trip during development). Confirm imported peak boundaries and annotation
  values appear in the Document Grid.
- **Ship gate:** reuse PRISM's `package-and-verify.ps1` pattern (extract zip to clean dir, launch the exe
  with a dummy connection arg, grep the log for load failures).

## For the new session — first actions
1. **Clone/checkout inputs:** ProteoWizard/pwiz (for `pwiz_tools/Osprey` + `pwiz_tools/Skyline`), and
   keep `D:\GitHub-Repo\maccoss\skyline-prism` handy as the tool template. Get a real **Stellar PRM
   `.sky`** with a **Carafe target+decoy `.blib`** imported and results extracted.
2. **Do M0 first (the spike).** It validates the entire scientific premise + the embedding path with
   almost no new code and no Osprey edits. Only build the WPF tool (M2+) once M0 shows target/decoy
   separation.
3. **Scaffold the new tool repo** by copying the structure (not the algorithms) of `skyline-prism/dotnet/`:
   4 projects (`Core` net8.0 / `Cli` net8.0 / `Skyline` net8.0-windows with vendored `SkylineTool` /
   `App` WPF), `tool-inf/` manifest, `build/package.proj` + `package-and-verify.ps1` ship gate.
4. **Open the pwiz PR** for `Osprey.Api` early (M1) so the facade is reviewed upstream while the tool is built.

## Open items to re-verify (flagged uncertainties from research)
- **`WindowXcorrCache` factory** — needed only if the 3 xcorr features are wanted; its builder appeared
  internal (likely in `SpectralScorer.cs`/`ResolutionStrategy.cs`). Confirm before promising xcorr scores.
- **Chromatogram TSV format** — exact `Times`/`Intensities` cell delimiter, raw-vs-interpolated grid,
  and whether PRM **product** traces populate (not empty). Spot-check against Skyline's chromatogram graph.
- **How Carafe decoys import into Skyline** — as decoy peptides vs. ordinary targets — and confirm Skyline
  extracts XICs for them. Determines how the tool tags decoys when building `PercolatorEntry.IsDecoy`.
- **`DecoyGenerator` access modifier** (`Osprey.Scoring/DecoyGenerator.cs`) — only matters if we generate
  decoys instead of trusting the Carafe `.blib` (plan trusts the blib).
- **`MS1Spectrum` ctor** (`Osprey.Core/MS1Spectrum.cs`) — only matters if MS1 features are added later.
- **Line-by-line confirm** none of the ~12 chosen calculators reach into `SetWindow`-only state
  (interface tiering implies they don't; verify in `CoelutionCalculators.cs`, `PeakShapeCalculators.cs`,
  `MedianPolishCalculators.cs`, `RtDeviationCalculators.cs`).

## Verified API signatures (reference — avoid re-researching)
```csharp
// pwiz_tools/Osprey/Osprey.Chromatography/CwtPeakDetector.cs
public static class CwtPeakDetector {
  public static List<XICPeakBounds> DetectConsensusPeaks(List<XicData> xics, double minConsensusHeight);
}
public class XicData { public int FragmentIndex; public double[] RetentionTimes; public double[] Intensities; }

// pwiz_tools/Osprey/Osprey.Scoring/  (calculators internal; reach via public registry + public interfaces)
public static class OspreyFeatureCalculators { public static IOspreyFeatureCalculator Get(int featureIndex); }
public interface IOspreyFeatureCalculator { double Calculate(OspreyScoringContext ctx, IOspreySummaryPeakData peak); }
// tiers: IOspreySummaryPeakData ⊂ IOspreyDetailedPeakData (adds IReadOnlyList<XicData> Xics, ApexIsotopeEnvelope)
//        ⊂ IOspreyApexSpectrumPeakData (adds Spectrum ApexSpectrum) ⊂ IOspreyApexSpectraPeakData
public OspreyScoringContext(OspreyConfig config);   // XIC-only subset needs only this (no SetWindow)

// pwiz_tools/Osprey/Osprey.FDR/PercolatorFdr.cs   (all public, DTO-based)
public static PercolatorResults RunPercolator(IList<PercolatorEntry> entries, PercolatorConfig config);
public class PercolatorEntry { string FileName; string Peptide; byte Charge; bool IsDecoy; uint EntryId; double CoelutionSum; double[] Features; }

// pwiz_tools/Osprey/Osprey.ML/  and  Osprey.FDR/Reconciliation/ReconciliationPlanner.cs
QValueCalculator.ComputeQValues(...); PepEstimator.FitDefault(scores, isDecoy).PosteriorError(score);
ReconciliationPlanner.Plan(...);  ReconciliationPlanner.DetermineAction(...);

// pwiz_tools/Osprey/Osprey.IO/BlibLoader.cs
public class BlibLoader { public List<LibraryEntry> Load(string path); }   // Carafe target+decoy .blib

// Skyline chromatogram export (pwiz_tools/Skyline/Model/ExportChromatograms.cs + CommandArgs.cs)
// SkylineCmd --in=doc.sky --chromatogram-file=out.tsv --chromatogram-precursors --chromatogram-products
// or, from a live tool:  RunCommandSilent(new[]{ "--chromatogram-file=out.tsv","--chromatogram-precursors","--chromatogram-products" })
// TSV cols: FileName, PeptideModifiedSequence, PrecursorCharge, ProductMz, FragmentIon, ProductCharge,
//           IsotopeLabelType, TotalArea, Times, Intensities   (Times/Intensities = arrays in one cell)
```

## Critical files / references
**Reuse from Osprey (`pwiz_tools/Osprey/`):** `Osprey.Chromatography/CwtPeakDetector.cs`,
`Osprey.Scoring/{OspreyFeatureCalculators,IOspreyPeakData,IOspreyFeatureCalculator,OspreyScoringContext}.cs`,
`Osprey.FDR/{PercolatorFdr}.cs` + `Reconciliation/ReconciliationPlanner.cs`,
`Osprey.ML/{QValueCalculator,PepEstimator,LinearSvmClassifier}.cs`, `Osprey.IO/{BlibLoader,ParquetScoreCache}.cs`,
`Osprey.Core/{LibraryEntry,Spectrum,XicData usage}.cs`. **New:** `Osprey.Api/` facade (upstream PR).
**Skyline export:** `pwiz_tools/Skyline/Model/ExportChromatograms.cs`, `CommandArgs.cs` (`--chromatogram-*`).
**Tool model (`skyline-prism/dotnet/`):** `src/SkylinePrism.App/*` (+ `tool-inf/{info,SkylinePrism}.properties`),
`src/SkylinePrism.Skyline/{SkylineSession,ISkylineClient,SkylineReportDriver}.cs`, `Reports/Skyline-PRISM.skyr`,
`external/SkylineTool/*`, `build/{package.proj,package-and-verify.ps1,verify-tool.ps1}`.
**Docs:** `skyline-prism/docs/skyline-external-tools.md` (tool interface field guide).
**Write-back mechanisms:** `RunCommandSilent(["--import-peak-boundaries=…"])`; `--annotation-*` +
`ImportProperties` for per-precursor q/PEP annotations.
