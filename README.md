# OspreyTool

[![ci](https://github.com/maccoss/skyline-osprey-tool/actions/workflows/ci.yml/badge.svg)](https://github.com/maccoss/skyline-osprey-tool/actions/workflows/ci.yml)
[![release](https://img.shields.io/github/v/release/maccoss/skyline-osprey-tool)](https://github.com/maccoss/skyline-osprey-tool/releases)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)

An external tool that uses the [**Osprey**](https://github.com/ProteoWizard/pwiz/tree/master/pwiz_tools/Osprey)
search engine to improve peak picking within **Skyline**: peptide detection, peak picking, and confidence
scoring for **large PRM experiments**. Built for Thermo Stellar unit-resolution scheduled PRM, where a run
carries thousands of scheduled precursors across many replicates and hand-curating peak boundaries stops
being possible.

OspreyTool *embeds* Osprey — its CWT peak detection, DIA-NN-style co-elution scoring, Percolator FDR, and
cross-run reconciliation are called as a library, not reimplemented.

It ships two ways to run the same engine:

| | |
|---|---|
| **Skyline external tool** | A window you launch from Skyline's Tools menu. Reads the open document, re-picks and reconciles the peaks, writes the boundaries back, and lets you inspect *why* each peak was chosen. |
| **`ospreytool` CLI** | The same pipeline over exported files, for scripting and batch work. |

## What it does

Per precursor and replicate, over the chromatograms Skyline already extracted:

1. **Detect** candidate peaks (Osprey CWT — or [any detector you plug in](#adding-a-peak-detection-algorithm)).
2. **Rank** them with Osprey's [learned pick](docs/lda-peak-picking.md) — a frozen linear weighting of four
   standardized terms: fragment co-elution, apex intensity, closeness to the expected retention time, and
   agreement with the library spectrum (`median_polish`). Nothing is trained; the weights ship with the tool,
   one set per platform. `--ranker product` selects the legacy multiplicative rank instead.
3. **Score confidence**: genuine reversed decoys → Percolator q-values. (A reversed decoy has the same
   precursor m/z as its target, so it is probed inside the target's own scheduled isolation window and real
   decoy chromatograms can be extracted.)
4. **Reconcile across runs**: intra-run charge-state consensus, then a cross-run consensus RT with
   keep / snap-to-candidate / force-integrate decisions, so replicates agree.
5. **Write back** peak boundaries to the Skyline document.

## Install

### 1. Prerequisite: the .NET 8 Desktop Runtime

```powershell
winget install Microsoft.DotNet.DesktopRuntime.8
```

(Or download it from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/8.0). The **Desktop**
Runtime covers both the Skyline tool and the CLI.)

### 2. Install the tool in Skyline

1. Download **`OspreyTool.zip`** from [Releases](https://github.com/maccoss/skyline-osprey-tool/releases)
   (or [build it yourself](#build-from-source)).
2. In Skyline: **Tools → External Tools → Add → From File...**, and choose the zip.
   (In some Skyline versions this is **Tools → Tool Store → Install from File**.)
3. The tool appears in the **Tools** menu as **Osprey Tool**. Launching it connects to the open document —
   the indicator turns green and names the document.

> **Reinstalling over a running copy:** close the tool first. Skyline cannot overwrite files that are still
> open, and a partial extraction leaves the tool unable to start.

### Before you run it

Two Skyline settings will otherwise fight the imported boundaries:

- **Turn off peak-boundary imputation** — it silently overrides imported boundaries.
- **Do not apply an mProphet peak-scoring model** at the same time; let Osprey drive the peak choice.

## Using the Skyline tool

Everything comes from the open document; there are no file paths to fill in.

| tab | |
|---|---|
| **Settings** | Peak detector, scoring mode, RT prior width (σ), the intensity exponent, charge consensus, reconciliation, and the chromatogram color scheme. Hover any control for what it does. |
| **Candidate Peaks** | Select a peptide in Skyline's Targets tree and this fills itself in: every candidate peak with its full score breakdown, which one the signal processing **CHOSE**, and which boundaries reconciliation **APPLIED** to the document. If reconciliation force-integrated a window that was not a candidate at all, that window is shown too, scored the same way — so you can see exactly what overrode the picker, and why. Click a row to plot it. |
| **Log** | What the run did. |

**Run** re-picks and reconciles the whole document and imports the boundaries.

## Command line

```bash
ospreytool --help        # all commands and options
ospreytool --version
```

Everything works off a Skyline chromatogram export.

```bash
# 1. Export chromatograms from your Skyline document
SkylineCmd --in=study.sky \
           --chromatogram-file=export.tsv --chromatogram-precursors --chromatogram-products

# 2. Re-pick and reconcile; write Skyline peak boundaries
ospreytool reconcile --xics export.tsv --blib library.blib --rt-csv doc_rt.csv \
                     --decoys decoys.tsv --no-fdr --all-targets --lib-cosine \
                     --out boundaries.csv --report report.csv

# 3. Import the boundaries back into the document
SkylineCmd --in=study.sky --import-peak-boundaries=boundaries.csv --save
```

### Commands

| command | |
|---|---|
| `reconcile` | Re-pick, score, reconcile across runs, and write peak boundaries. **The main command.** |
| `explain` | Dump every candidate peak and its score breakdown for one peptide/replicate, plus what reconciliation did. The diagnostic behind the GUI's Candidate Peaks panel. |
| `repick` | Per-run peak picking only, without cross-run reconciliation. |
| `decoyfdr` | Genuine target/decoy Percolator FDR over the re-picked peaks. |
| `score` | Score targets + decoy-XICs and report per-feature target/decoy separation. |
| `m0` | Join the chromatogram export with the library; sanity-check the RT alignment. |
| `settings` | Print the transition settings read from a `.sky` document. |

### Options worth knowing

| flag | |
|---|---|
| `--rt-csv <rt.csv>` | Expected RTs from the **document** (`ExplicitRetentionTime`). Prefer this over the library's predicted RT — on real data the predicted RT was off by more than 0.5 min for 7% of peptides. |
| `--detector <id>` | `osprey-cwt` (default) or `local-maxima`. |
| `--ranker <id>` | Which candidate wins. [`lda`](docs/lda-peak-picking.md) (default, and Osprey's default) is Osprey's frozen linear pick over four standardized terms — co-elution, `ln_intensity`, RT penalty, `median_polish`. Nothing is trained and no decoys are needed. `product` is the legacy multiplicative rank. |
| `--sky <document.sky>` | Read the product mass analyzer from the document. This selects the frozen weight set: an m/z tolerance (LIT or triple quad) takes the unit-resolution set, ppm takes the HRAM set. Also sets Osprey's fragment tolerances. |
| `--resolution unit\|hram` | Override that choice explicitly. |
| `--lib-cosine` | Multiply the library spectral match into the pick score (`--ranker product` only — the learned pick always weights it). |
| `--intensity-exp <w>` | Exponent on the `ln(1+I)` term (`--ranker product` only). `1` = Osprey's legacy pick; `0` removes it. The other three terms are bounded on [0,1] and this one is not, so at `w=1` a far more intense interference can outvote co-elution, RT and spectral match combined — the hand-tuned workaround the learned pick replaces. |
| `--rt-sigma <min>` | Width of the Gaussian RT prior (default `0.3`). |
| `--rt-tol <min>` | Hard RT gate. `0` = off, which is right for scheduled PRM: the extracted window *is* the scheduling window. |
| `--no-fdr` | Confidence from co-elution rather than Percolator (no decoys needed). |
| `--all-targets` | Reconcile every target, not only the confident ones — right for a targeted assay. |

Run `ospreytool explain --peptide PEPTIDEK --replicate <run>` when a peak looks wrong: it prints the
candidates, every score term, and whether reconciliation kept, moved, or force-integrated the peak.

## Adding a peak-detection algorithm

Peak detection is pluggable. `IPeakDetector` lives in `OspreyTool.Core` and speaks only in arrays, so an
implementation **needs no Osprey dependency**:

```csharp
public interface IPeakDetector
{
    string Id { get; }            // "osprey-cwt" - the --detector value
    string DisplayName { get; }   // shown in the Settings dropdown
    IReadOnlyList<PeakWindow> Detect(PeakDetectionInput input);
}
```

Implement it, call `PeakDetectors.Register(...)`, and it appears in the Settings dropdown and under
`--detector` automatically. Scoring, FDR and reconciliation are unaffected by the choice, which is what makes
detectors directly comparable. `LocalMaximaPeakDetector` is a complete, dependency-free worked example.

**→ [`docs/adding-a-peak-detector.md`](docs/adding-a-peak-detector.md)** is the full contract, with a worked
example, how to test it, and how to benchmark it against Osprey CWT on real data. The rule people get wrong:
**return every plausible candidate, not just your best one** — the tool ranks them, and reconciliation needs
the alternatives to snap to.

*Which* candidate wins is the second, separate seam: `ICandidateRankModel` in `OspreyTool.Scoring`, selected
with `--ranker`. → [`docs/lda-peak-picking.md`](docs/lda-peak-picking.md).

## Build from source

Osprey lives in the [ProteoWizard/pwiz](https://github.com/ProteoWizard/pwiz) tree and is referenced by
project. Point `OSPREY_DIR` at your checkout (it defaults to `D:\Dev\pwiz\pwiz_tools\Osprey`):

```bash
export OSPREY_DIR=/path/to/pwiz/pwiz_tools/Osprey

dotnet build dotnet/OspreyTool.sln
dotnet test  dotnet/OspreyTool.sln

# The Skyline tool zip -> dotnet/publish/OspreyTool.zip
dotnet build dotnet/build/package.proj -c Release

# Or the full ship gate: test -> package -> load the engine + open the UI from the packaged zip
pwsh -File dotnet/build/package-and-verify.ps1
```

Only the Osprey subtree is needed; CI fetches it with a sparse checkout of pwiz (see
[`.github/workflows/ci.yml`](.github/workflows/ci.yml)).

## Documentation

| | |
|---|---|
| [`docs/PROJECT_BRIEF.md`](docs/PROJECT_BRIEF.md) | The spec: decisions, verified upstream APIs, milestones, risks. |
| [`docs/adding-a-peak-detector.md`](docs/adding-a-peak-detector.md) | How to plug in your own peak-detection algorithm — the contract, a worked example, and how to benchmark it. |
| [`docs/lda-peak-picking.md`](docs/lda-peak-picking.md) | The learned peak pick: Osprey's frozen four-term linear ranking, the per-platform weight sets, and how the right one is chosen. |
| [`docs/osprey-api.md`](docs/osprey-api.md) | The Osprey API contract this tool depends on. |
| [`docs/data-formats.md`](docs/data-formats.md) | The chromatogram export / `.blib` / join formats, verified against real data. |
| [`release-notes/`](release-notes/) | Release notes, plus the versioning and release process. |
| [`CLAUDE.md`](CLAUDE.md) | Architecture and conventions. |

## Known limitations

- **RT calibration is unconstrained at early retention times.** Few confident peptides elute before ~4 min,
  so the per-run RT calibration extrapolates there, and an early-eluting peptide can be force-integrated at a
  consensus RT where no peak exists — even when the picker found the right peak.
- **FDR is not yet wired into the connected (GUI) workflow**; it needs a decoy pairing manifest. Use the
  `decoyfdr` / `reconcile` CLI commands with `--decoys`.
- **The GUI does not yet expose `--ranker` or the resolution choice.** It uses the defaults (learned pick,
  unit resolution); the Settings "Primary scoring" dropdown still offers only the two product-form variants.

## License

Apache-2.0. The vendored `SkylineTool` RPC sources under `dotnet/external/` are Apache-2.0, from ProteoWizard.
