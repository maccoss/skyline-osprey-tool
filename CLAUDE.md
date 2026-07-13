# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project status: early — M0 groundwork in place

The goal is an **interactive Skyline external tool** that uses the **Osprey** search engine to do peptide
detection, peak picking, and confidence scoring for **large PRM experiments** (initially Thermo Stellar
unit-resolution PRM) inside Skyline.

What exists today:
- **`dotnet/` solution `OspreyTool`** — projects `OspreyTool.Core` / `.Scoring` / `.Cli` / `.Tests`
  (net8.0), plus **`.Skyline`** (RPC seam: vendored `external/SkylineTool/*`, `ISkylineClient` /
  `SkylineSession`, net8.0-windows) and **`.App`** (WPF tool, net8.0-windows) now scaffolded. Builds
  zero-warning; `dotnet test` green. Packaging: `dotnet/build/package.proj` → `dotnet/publish/OspreyTool.zip`
  (a drop-in Skyline external tool). Scoring/reconciliation have advanced well past M0 — see the memory
  files and the `reconcile` / `decoyfdr` / `explain` CLI commands; this bullet is the only part of the
  status below that is current.
- **M0 input readers, validated on real data**: `ChromatogramTsvReader` (Skyline `--chromatogram-file`
  export → `List<XicData>` per precursor), `BlibReader` (Bibliospec `.blib`, incl. zlib peak blobs),
  joined by `M0Report` on `(PeptideModifiedSequence, PrecursorCharge)`. `ospreytool m0` runs the join +
  RT-alignment sanity check.
- **`OspreyTool.Scoring` — the prototype Osprey.Api facade, working end-to-end**: `OspreyFeatureScorer`
  drives real Osprey `CwtPeakDetector` + the 12-feature subset via an `XicPeakData` adapter
  (`IOspreyDetailedPeakData`), publishing the median-polish byproduct like Osprey's own harness. Osprey
  is referenced by project (path via `$(OspreyDir)`, default `D:\Dev\pwiz\pwiz_tools\Osprey`; override
  with `-p:OspreyDir=…` or `OSPREY_DIR`).
- **`M0ScoringPipeline` — M0 achieved**: scores every (precursor, replicate) target + its decoy-XIC,
  measures per-feature target/decoy separation (rank AUC), and runs Osprey `PercolatorFdr` per replicate
  for per-run q-values. `ospreytool score --xics --blib --sky` runs it. **On the real Stellar data:
  `fragment_coelution_sum` AUC 0.902 (target 6.4 vs decoy 0.4); per-run Percolator gives 100–236
  targets at q≤0.01 for real samples and ~0 for blanks** — decoy-XIC separation confirmed.
  `FeatureSelection` + `RawFileAvailability` drop the spectrum families when the raw is missing and warn.
- **`docs/data-formats.md`** — the export/blib/join formats, **verified against the real test data** (not
  assumed). Read it before touching the readers.
- **`docs/PROJECT_BRIEF.md`** — the authoritative spec: locked-in decisions, verified upstream Osprey/
  Skyline API signatures, the milestone plan, risks. **Read it first.** It is the source of truth; this
  file is the orientation layer on top. Two things have since evolved from the brief — see below.

**Deltas from the brief (the brief is not yet updated):**
1. **Decoys are generated in-memory**, *not* taken from a Carafe target+decoy `.blib` (the available
   `.blib` is target-only). The chosen method is **decoy-XIC** (transform the observed XICs), kept as a
   pluggable **option** behind `IDecoyChromatogramGenerator`; the first implementation is
   `XicShuffleDecoyGenerator` (independent per-fragment circular time-shift → breaks co-elution,
   preserves per-fragment area, deterministic per seed). Other strategies (round-trip decoy transitions
   through Skyline, or porting Osprey's sequence-based `DecoyGenerator`) can be added behind the same seam.
2. **Project namespace is `OspreyTool`** (not `Osprey.*`), to avoid colliding with the real Osprey
   namespaces it will reference.

When the brief and reality diverge, trust the source you can read (and `docs/data-formats.md`) over the
brief's prose.

## What the tool does (big picture)

The analyst builds a Carafe target+decoy `.blib` in Skyline, imports the Stellar PRM runs (Skyline
extracts chromatograms), then launches this tool from Skyline's Tools menu. The data flow:

```
Skyline (Carafe target+decoy .blib loaded, Stellar PRM imported)
   │  RunCommandSilent(--chromatogram-file ...)  → XIC TSV (Times[]/Intensities[] per transition×replicate)
   ▼
This tool (WPF, modeled on Skyline-PRISM)  ── also reads the Carafe .blib directly (Osprey BlibLoader)
   │  per (precursor, replicate):
   ▼
Osprey.Api facade (new; references existing Osprey DLLs — no rewrite of Osprey internals)
   1. IPeakDetector.Detect(...)  [default: Osprey CwtPeakDetector]  → candidate peak windows  ← PLUGGABLE
   2. IOspreyDetailedPeakData adapter + OspreyFeatureCalculators  → feature-subset vector
   3. PercolatorFdr (per run, over target+decoy vectors)          → per-run q-value + PEP
   4. ReconciliationPlanner.Plan (across runs)                    → experiment-level q + MBR actions
   ▼
Write back via RunCommandSilent: --import-peak-boundaries + custom annotations (q/PEP/detection)
   + interactive review UI (ScottPlot XIC + score panels), like PRISM's MainWindow
```

Two design decisions drive everything (see brief §"Decisions locked in"):
1. **Integration, not new algorithms.** Osprey already implements the whole chain (CWT, DIA-NN-style
   co-elution scoring, the 21-feature PIN vocabulary, a native Percolator, and cross-run reconciliation).
   We embed it in-process via a thin **`Osprey.Api`** facade that *references* the Osprey DLLs.
2. **Chromatograms come from Skyline's extraction**, not a custom `.blib` schema. Osprey co-elutes the
   *observed* Skyline XICs against the *predicted* Carafe spectrum. No `.skyd` parsing, no re-import.

## Adding a peak-detection algorithm (the pluggable seam)

Peak **detection** is swappable; everything downstream (scoring, ranking, FDR, reconciliation) is not
affected by the choice. The seam is `OspreyTool.Core/Detection/IPeakDetector.cs` — it lives in **Core and
speaks only in arrays, so an implementation needs no Osprey reference at all**.

```csharp
public interface IPeakDetector
{
    string Id { get; }            // "osprey-cwt" — CLI --detector, persisted settings
    string DisplayName { get; }   // shown in the tool's Settings dropdown
    IReadOnlyList<PeakWindow> Detect(PeakDetectionInput input);   // fragment XICs on a shared RT grid
}
```

To add one: implement it, then `PeakDetectors.Register(...)` (or pass the instance to
`new OspreyFeatureScorer(config, detector)` / `ReconcileConfig.Detector`). It appears in the Settings
dropdown and under `--detector` automatically.

Two ship today: `OspreyCwtPeakDetector` (`osprey-cwt`, the default — wraps Osprey's CWT) and
`LocalMaximaPeakDetector` (`local-maxima`, a dependency-free baseline in Core; it is the worked example).

Two things to know when writing one:
- **Return all plausible candidates, not just the winner.** The tool ranks them itself, and reconciliation
  needs the alternatives to snap to. A detector that pre-selects one peak degrades both.
- **`PeakWindow` carries optional `Area` / `SignalToNoise` / `ApexIntensity`.** Osprey's feature calculators
  read those off `XICPeakBounds`, so `OspreyCwtPeakDetector` passes CWT's own values through unchanged (that
  is what keeps the default path bit-identical to stock Osprey — verified: reconciled boundaries over the
  full 11,196-group Stellar run are byte-identical before/after the seam). Leave them null and the tool
  derives them from the chromatogram instead.

## Where the code will come from (external dependencies)

This tool sits between two upstream codebases. Neither lives in this repo; you will read them to work here.

- **Osprey (the scoring engine)** — the C#/.NET port lives in `ProteoWizard/pwiz` under
  `pwiz_tools/Osprey/`, **checked out at `D:\Dev\pwiz`** (the `D:\Dev` root also holds `ai/`, the shared
  dev-tooling repo). Projects are SDK-style, multi-target `net472;net8.0`, AnyCPU;x64 — buildable
  standalone. **The exact public API contract (signatures, the 12-feature subset, the median-polish
  byproduct recipe, gotchas) is in [`docs/osprey-api.md`](docs/osprey-api.md) — read it before writing the
  facade.** The reusable entry points (verified in the brief) are
  `Osprey.Chromatography/CwtPeakDetector.cs`, `Osprey.Scoring/{OspreyFeatureCalculators,IOspreyPeakData,
  OspreyScoringContext}.cs`, `Osprey.FDR/{PercolatorFdr, Reconciliation/ReconciliationPlanner}.cs`,
  `Osprey.ML/*`, `Osprey.IO/BlibLoader.cs`, and `Osprey.Scoring/DecoyGenerator.cs` (for in-memory decoys).
  The Rust original is `D:\GitHub-Repo\maccoss\osprey`.
- **Skyline** — also in `pwiz_tools/Skyline/` in the same pwiz repo. The chromatogram export
  (`Model/ExportChromatograms.cs` + the `--chromatogram-file` / `--chromatogram-precursors` /
  `--chromatogram-products` flags in `CommandArgs.cs`) is the tool's data source.
- **Skyline-PRISM (the tool template)** — `D:\GitHub-Repo\maccoss\skyline-prism`. This tool copies its
  **structure, not its algorithms**. Study `skyline-prism/dotnet/` and `dotnet/CLAUDE.md`.
- **External-tool field guide (canonical)** — the Skyline-external-tool platform knowledge (RPC, reports,
  `.blib`, chromatogram export, reading the `.sky`, embedding a pwiz engine, packaging) + a scaffolding
  skill + a `dotnet new` template now live in
  **[uw-maccosslab/skyline-external-tools-ai](https://github.com/uw-maccosslab/skyline-external-tools-ai)**
  (`docs/skyline-external-tools.md`). This tool (chromatograms, `.sky` parsing, engine embedding) is one of
  its example sources.
- **Carafe/Cadenza** — `D:\GitHub-Repo\maccoss\Carafe` — generates the AI-predicted `.blib` (RT + fragment
  spectra). Currently target-only; decoys are generated in-memory (see delta #1 above).

### ⚠️ Two rule sets — do not cross them

- **This repo (`OspreyTool`)** follows the **Skyline-PRISM template's modern .NET conventions** (idiomatic
  C# 12, `async`/`await` allowed, standard build).
- **The Osprey codebase (`D:\Dev\pwiz`)** — where the **`Osprey.Api` facade will be contributed
  upstream** — is governed by **`D:\Dev\ai`** (`CRITICAL-RULES.md`, `STYLEGUIDE.md`, `TESTING.md`,
  `WORKFLOW.md`). Those rules are strict and different: **no `async`/`await`**, CRLF line endings,
  ASCII-only, resource strings for all UI text, `_camelCase` fields / `snake_case` enum members, private
  helpers *after* their callers, build via `quickbuild.bat` / `Build-Skyline.ps1` (no new build systems,
  no reformatting unrelated code), tests use `AssertEx` with no English literals, and pwiz work is ideally
  driven from the `D:\Dev` root (its `status` MCP + C# LSP). **Read `D:\Dev\ai\CRITICAL-RULES.md` before
  editing anything under `D:\Dev\pwiz`.**

## Layout (`dotnet/`, modeled on Skyline-PRISM)

Exists now:
- `dotnet/src/OspreyTool.Core` (`net8.0`) — engine-agnostic DTOs + readers: `XicData`,
  `PrecursorChromatograms`, `PrecursorKey`, `LibraryEntry`, `ChromatogramTsvReader`, `BlibReader`,
  `M0Report`. Depends on `Microsoft.Data.Sqlite` (for the blib).
- `dotnet/src/OspreyTool.Cli` (`net8.0`) — the M0 console harness (`ospreytool m0 ...`).
- `dotnet/tests/OspreyTool.Tests` (`net8.0`, xUnit) — hermetic tests (synthetic data; no `TestData/`
  dependency), incl. a builds-its-own-`.blib` test covering both zlib and raw peak blobs.

Still to add (copy the shape from `skyline-prism/dotnet/` when needed):
- `OspreyTool.Skyline` (`net8.0-windows`) — Skyline RPC seam; vendored `external/SkylineTool/*`
  (Apache-2.0), connect-per-call JSON-RPC over a named pipe, `ISkylineClient`/`ISkylineExecutor` seam for
  fakes. (For now, drive live Skyline via the `skyline` MCP tools instead.)
- `OspreyTool.App` (WPF `WinExe`) — the GUI Skyline launches, with `tool-inf/` manifest + `Reports/*.skyr`.
  **UI models Skyline-PRISM's `MainWindow`** (`skyline-prism/dotnet/src/SkylinePrism.App/MainWindow.xaml`):
  a `TabControl` with a **Settings** tab (Osprey scoring options — feature subset, decoy method, FDR, unit-
  resolution tolerances — as form controls in a `ScrollViewer`), a **Log** tab (read-only Consolas
  `TextBox` = console output), and later a **Review** tab (ScottPlot XIC + score panels); a Run button and
  a "Show Command Line" button. Keep RPC off the UI thread.
- `dotnet/build/{package.proj, package-and-verify.ps1}` — packaging + ship gate.
- A `Directory.Build.props` `<Version>` ↔ `tool-inf/info.properties` lockstep at release time.

## Build / test / run

```bash
dotnet build dotnet/OspreyTool.sln
dotnet test  dotnet/OspreyTool.sln                                        # all tests
dotnet test  dotnet/OspreyTool.sln --filter "FullyQualifiedName~BlibReaderTests"   # a single test class
# M0 harness against real (git-ignored) TestData:
dotnet run --project dotnet/src/OspreyTool.Cli -- m0 --xics <export.tsv> --blib <library.blib>
```

Export `<export.tsv>` from Skyline with `--chromatogram-file=<out.tsv> --chromatogram-precursors
--chromatogram-products` (live: via the `skyline` MCP `skyline_run_command`).

**.NET SDK pinned to `8.0.100`** (`dotnet/global.json`, `rollForward: latestFeature` → resolves to the
latest installed 8.0.x). `Directory.Build.props` enables `Nullable`/`ImplicitUsings` and
`EnableWindowsTargeting=true` (so the future WPF/Windows projects cross-compile on Linux/macOS/CI). Do
**not** set `InvariantGlobalization=true` (crashes WPF text layout). All numeric parse/format paths use
`CultureInfo.InvariantCulture` — the chromatogram TSV is exported invariant (e.g. `6.400576E+07`).

## Milestones — M0 achieved

The brief's plan is deliberately ordered to de-risk the science before any UI. **M0** required showing
**decoys separate from targets with sane per-run q-values** — **done** (`ospreytool score` on the real
Stellar data: `fragment_coelution_sum` AUC 0.902, per-run Percolator sane, blanks ~0). The join/format
groundwork (`docs/data-formats.md`) and the readers were validated first (5652 groups, 100% joined).

Next:
- **M1** — harden `Osprey.Api` as a clean facade + tests, then upstream to pwiz (draft PR). Optionally add
  the spectrum features (→19) once a raw/mzML source is available (4 apex-match easy; 3 xcorr/SG need the
  window scan list). Wire full q/PEP write-back fields.
- **M2** — WPF tool scaffold (`OspreyTool.Skyline` RPC seam + `OspreyTool.App`, Settings + Log tabs).
- **M3** — inter-run reconciliation (`ReconciliationPlanner`); **M4** — write-back + review UI;
  **M5** — packaging + validation.

Caveats surfaced by the M0 run: the rank-AUC metric doesn't account for reversed features (e.g.
`abs_rt_deviation` shows 0.242 = good separation in reverse); Osprey prints `[COUNT]`/`[TIMING]`
Percolator diagnostics to stdout (harmless, can be quieted later).

**Initial feature subset** (XIC-only + RT + median-polish families derivable from Skyline's XIC export):
indices `0,1,2,3,4,5,11,12,15,16,19,20` per `ParquetScoreCache.PIN_FEATURE_NAMES`. **Deferred** because
they need inputs beyond XICs: MS1 features (13,14), apex-match spectrum features (7–10), and the
xcorr family (6,17,18 — needs Osprey's internal `WindowXcorrCache`). See the brief for the exact list
and the open items to re-verify.

## Developing against a live Skyline

A running Skyline is reachable through the `skyline` MCP tools in this session — use them to script the
export/annotation round-trip during development without hand-driving the GUI. The most relevant:
`skyline_run_command` (drives `--chromatogram-file`, `--import-peak-boundaries`, `--annotation-*`),
`skyline_get_graph_data` (spot-check an exported XIC against Skyline's chromatogram graph — an explicit
M0 verification step), and `skyline_get_report_from_definition` / `skyline_import_properties` (confirm
q/PEP/detection annotations land in the Document Grid).
