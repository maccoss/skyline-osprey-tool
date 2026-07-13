# OspreyTool v26.1.0 Release Notes

First release. An interactive Skyline external tool that uses the **Osprey** search engine for peptide
detection, peak picking, and confidence scoring of large scheduled-PRM experiments (initially Thermo Stellar
unit-resolution PRM). Osprey is referenced as a library and driven in-process — none of its algorithms are
reimplemented.

## New Features

### Skyline external tool (WPF)

- **Runs against the live document.** Launched from Skyline's Tools menu, the tool reads everything it needs
  over the Skyline RPC connection — chromatograms (`--chromatogram-file`), expected retention times
  (`ExplicitRetentionTime`), and library fragment intensities (`LibraryIntensity`) — re-picks and reconciles
  peaks, and writes the result back with `--import-peak-boundaries`. No file paths to fill in, and the
  intermediate files are written to the system temp directory and deleted when the run finishes.
- **Connection indicator.** A green dot with the document name and Skyline version, or grey when the tool was
  started outside Skyline.
- **Candidate Peaks panel** (analogous to Skyline's View > Live Reports > Candidate Peaks). Lists every
  candidate peak for the selected peptide and replicate with the full score breakdown — co-elution, library
  cosine, RT deviation, RT penalty, intensity term, and the combined rank — and shows **what the signal
  processing chose (`CHOSEN`) next to what reconciliation actually applied to the document (`APPLIED`)**. When
  reconciliation force-integrates a window that is not a candidate peak at all, that window is added to the
  table and scored on the same terms, so a forced integration is visible rather than silently disagreeing
  with the picker.
- **Follows the Skyline selection.** Click a peptide in the Targets tree and the panel updates itself; no
  retyping a sequence. (Skyline's RPC seam has no selection callback, so the tool polls.)
- **XIC plot colored like Skyline.** The fragment traces use the user's own Skyline color scheme, read over
  RPC from the `Color Schemes` settings list (Tools > Options > Display), including custom schemes.
- **Tunable settings with tooltips**, defaulting to the values validated on real Stellar data.

### Scoring and reconciliation

- **Pluggable peak detection.** `IPeakDetector` lives in `OspreyTool.Core` and speaks only in arrays, so a new
  algorithm needs no Osprey dependency to implement. Osprey's CWT is the default (`osprey-cwt`);
  `LocalMaximaPeakDetector` (`local-maxima`) ships as a dependency-free baseline and worked example. Select
  with the Settings dropdown or `--detector`.
- **Peak picking** ranks candidates by `coelution × libCosine × exp(-Δt²/2σ²) × ln(1+I)^w`, with the library
  spectral match (`median_polish_cosine`) optionally multiplied in (`--lib-cosine`), and the intensity term
  exponent tunable (`--intensity-exp`; `1` is Osprey-exact, `0` removes it).
- **Genuine reversed decoys → calibrated Percolator FDR.** A reversed decoy shares its target's precursor m/z,
  so it is probed inside the target's own scheduled isolation window and real decoy chromatograms can be
  extracted. On the Stellar validation set, targets pass at 12.9% / 37.7% (q ≤ 0.01 / 0.05) against decoys at
  0% / 1.4% (median decoy q = 0.60).
- **Cross-run reconciliation.** Osprey's intra-run charge-state consensus and inter-run consensus RT +
  reconciliation (keep / snap-to-candidate / force-integrate) give replicates a consistent, uniform-width
  peak. Cross-run apex MAD improved from 0.050 to 0.037 min on the validation set.

### Command line

`ospreytool reconcile | explain | repick | decoyfdr | score | m0 | settings`, plus `--version`. See
[Command line](../README.md#command-line) in the README.

## Known Limitations

- **RT calibration is unconstrained at early retention times.** Only 2–11 confident anchors elute before
  4 minutes in a typical run, so the per-file RT calibration extrapolates badly there and an early-eluting
  peptide can be force-integrated at a consensus RT where no peak exists — even when the picker found the
  correct peak. Later-eluting peptides are unaffected (replicates align to within ±0.02 min).
- **FDR is not yet available in the connected (GUI) workflow.** It needs a decoy pairing manifest; the
  checkbox is present but disabled. Use the `decoyfdr` / `reconcile` CLI commands with `--decoys` for now.
- **Spectrum-derived features are deferred.** The scoring uses the 12-feature XIC/RT/median-polish subset;
  the MS1, apex-match and xcorr families need inputs beyond Skyline's chromatogram export.

## Requirements

Windows, Skyline (or Skyline-daily), and the **.NET 8 Desktop Runtime**.
