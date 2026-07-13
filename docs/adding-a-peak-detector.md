# Adding a peak-detection algorithm

Peak **detection** in OspreyTool is pluggable. You can replace Osprey's CWT with any algorithm — a
Savitzky-Golay derivative method, a deep-learning segmenter, a port of someone else's picker — without
touching scoring, FDR, or reconciliation, and without taking a dependency on Osprey.

This document is the contract. If you only read one thing, read
[the two rules that actually bite](#the-two-rules-that-actually-bite).

## What is and is not pluggable

The pipeline, per precursor per replicate:

```
     fragment XICs (from Skyline's chromatogram export)
              │
              ▼
   ┌──────────────────────┐
   │  IPeakDetector       │  ← YOU ARE HERE. Proposes CANDIDATE peak windows.
   └──────────────────────┘
              │  many candidates
              ▼
      rank each candidate:  coelution × libCosine × exp(-Δt²/2σ²) × ln(1+I)^w
              │
              ▼
      confidence (Percolator q-values from genuine decoys)
              │
              ▼
      cross-run reconciliation (charge consensus, consensus RT,
      keep / snap-to-candidate / force-integrate)
              │
              ▼
      peak boundaries → Skyline
```

A detector's **only** job is to propose windows. It does not choose the winner, score confidence, or decide
anything cross-run. Everything below the seam is unchanged by your choice of detector — which is what makes
detectors comparable to each other on identical downstream logic.

## The interface

It lives in `OspreyTool.Core` ([`Detection/IPeakDetector.cs`](../dotnet/src/OspreyTool.Core/Detection/IPeakDetector.cs))
and speaks only in arrays, so **an implementation needs no reference to Osprey**:

```csharp
public interface IPeakDetector
{
    /// Stable id: the --detector value and the persisted setting. e.g. "osprey-cwt".
    string Id { get; }

    /// Human-readable, for the Settings dropdown. e.g. "Osprey CWT (default)".
    string DisplayName { get; }

    IReadOnlyList<PeakWindow> Detect(PeakDetectionInput input);
}
```

What you are given:

```csharp
public sealed class PeakDetectionInput
{
    /// Fragment (product-ion) XICs ONLY - the precursor trace is already excluded for you.
    /// Every entry has the same length as RetentionTimes.
    public required IReadOnlyList<double[]> FragmentIntensities { get; init; }

    /// The shared RT grid, minutes, ascending. For scheduled PRM this IS the acquisition window:
    /// there is no data outside it, so you do not need to (and should not) impose your own RT limit.
    public required double[] RetentionTimes { get; init; }

    /// The expected RT, when known. You MAY use it; you must not require it.
    public double? ExpectedRt { get; init; }

    /// Optional floor on a candidate's height; 0 means no floor.
    public double MinConsensusHeight { get; init; }

    public int ScanCount { get; }      // == RetentionTimes.Length
    public int FragmentCount { get; }  // == FragmentIntensities.Count
}
```

What you return:

```csharp
public readonly record struct PeakWindow(int StartIndex, int ApexIndex, int EndIndex)
{
    public double? Area { get; init; }           // see "Peak statistics" below
    public double? SignalToNoise { get; init; }
    public double? ApexIntensity { get; init; }

    public int ScanCount { get; }
}
```

Indices, not retention times — every consumer slices the intensity arrays with them.

## The two rules that actually bite

### 1. Return every plausible candidate, not just your best one

This is the rule people get wrong, because most peak-picking code is written to answer "where is the peak?".
Here, answering that question **destroys information the pipeline needs**:

- The tool ranks candidates itself, using evidence a detector usually cannot see — agreement with the
  predicted library spectrum, and distance from the expected retention time.
- Reconciliation snaps a run's peak onto the cross-run consensus RT by looking for *one of your other
  candidates* near it. With a single candidate there is nothing to snap to, so it must force-integrate a
  window where no peak was detected at all.

A detector that pre-selects one winner will produce visibly worse results even if its detection is excellent.
Emit everything plausible and let the ranking sort it out.

### 2. Peak statistics affect Osprey's feature vector

`Area`, `SignalToNoise` and `ApexIntensity` are optional, but they are not decorative: Osprey's feature
calculators read them off the peak bounds, so they feed the FDR features.

- **Fill them in** if your algorithm computes them meaningfully. That is what
  `OspreyCwtPeakDetector` does — it passes CWT's own values straight through, which is precisely what keeps
  the default path **bit-identical to stock Osprey** (verified: reconciled boundaries over an 11,196-group
  Stellar run are byte-identical before and after the seam was introduced).
- **Leave them null** and the tool derives them from the chromatogram (area = summed reference-fragment
  intensity over the window; apex = the reference fragment at the apex index). Perfectly fine — just be aware
  the numbers are the tool's, not yours.

## The rest of the contract

- **Order is not significant.** Duplicate windows are tolerated (the tool de-duplicates for display; CWT
  itself emits the same window at several wavelet scales).
- **Return an empty list when there is nothing to find.** Do not throw on thin data — fewer than two
  fragments, too few scans. The caller guards that, and an exception will fail the whole run.
- **Be thread-safe / re-entrant.** The pipeline scores precursors in parallel. Do not keep mutable state on
  the instance between `Detect` calls.
- **Stay inside the grid.** `0 <= StartIndex <= ApexIndex <= EndIndex < ScanCount`. Out-of-range windows are
  dropped defensively, but silently.
- **Do not impose your own RT gate.** For scheduled PRM the extracted XIC *is* the instrument's scheduling
  window; the RT prior is applied during ranking, where it is a soft penalty rather than a hard cut.

## A worked example, start to finish

The full example that ships is
[`LocalMaximaPeakDetector`](../dotnet/src/OspreyTool.Core/Detection/LocalMaximaPeakDetector.cs) (~120 lines,
in Core, zero Osprey dependency). Here is the shape of a new one:

```csharp
using OspreyTool.Core.Detection;

public sealed class MyPeakDetector : IPeakDetector
{
    public string Id => "my-detector";
    public string DisplayName => "My detector";

    public IReadOnlyList<PeakWindow> Detect(PeakDetectionInput input)
    {
        if (input.FragmentCount < 2 || input.ScanCount < 5)
        {
            return Array.Empty<PeakWindow>();   // nothing to find; do NOT throw
        }

        // A real peak appears in several fragments at once, so a summed trace suppresses
        // single-fragment noise relative to co-eluting signal.
        var trace = new double[input.ScanCount];
        foreach (var frag in input.FragmentIntensities)
        {
            for (var i = 0; i < trace.Length; i++)
            {
                trace[i] += frag[i];
            }
        }

        var peaks = new List<PeakWindow>();
        foreach (var (start, apex, end) in FindWhateverYouLike(trace))
        {
            peaks.Add(new PeakWindow(start, apex, end)
            {
                // Optional; omit if your algorithm has no meaningful notion of these.
                Area = Sum(trace, start, end),
                ApexIntensity = trace[apex],
            });
        }
        return peaks;   // ALL of them, not just the best
    }
}
```

Register it once at startup — or skip the registry and inject it directly:

```csharp
// Appears in the Settings dropdown and under --detector automatically.
PeakDetectors.Register(new MyPeakDetector());

// Or use it directly, bypassing the registry:
var scorer = new OspreyFeatureScorer(new OspreyConfig(), new MyPeakDetector());
var summary = ReconciliationPipeline.Run(groups, rts, decoys, decoyRts,
    new ReconcileConfig { Detector = new MyPeakDetector() });
```

Nothing else needs to change. The CLI picks it up (`--detector my-detector`), and so does the GUI's **Peak
detection** dropdown in Settings.

## Testing it

Mirror [`PeakDetectorTests`](../dotnet/tests/OspreyTool.Tests/PeakDetectorTests.cs). The tests that matter:

1. **It finds the peaks it should**, on synthetic Gaussians — including the smaller of two resolved peaks
   (a detector that only reports the tallest is the single-winner failure mode in disguise).
2. **It returns empty rather than throwing** on one fragment / too few scans.
3. **It drives the scorer end to end** — the real proof the seam is honoured:

```csharp
var r = new OspreyFeatureScorer(OspreyFeatureScorer.UnitResolutionConfig(), new MyPeakDetector())
    .Repick(xics, expectedRt: 7.4, rtTolerance: 0.0, rtSigma: 0.3, traceCandidates: true);

Assert.True(r.HasPeak);
```

## Comparing it against Osprey CWT on real data

This is what the seam buys you: an apples-to-apples comparison, since everything downstream is identical.
Run the same reconciliation twice, changing only `--detector`:

```bash
ospreytool reconcile --xics export.tsv --blib library.blib --decoys decoys.tsv --rt-csv doc_rt.csv \
                     --no-fdr --all-targets --lib-cosine \
                     --detector osprey-cwt --out bounds_cwt.csv --report report_cwt.csv

ospreytool reconcile --xics export.tsv --blib library.blib --decoys decoys.tsv --rt-csv doc_rt.csv \
                     --no-fdr --all-targets --lib-cosine \
                     --detector my-detector --out bounds_mine.csv --report report_mine.csv
```

Compare the summaries directly, and then import both boundary files into Skyline and compare the aggregate
`LibraryDotProduct` — an independent referee, since Skyline computes it from its own library and not from any
of our score terms.

For calibration, that is exactly how the shipped `local-maxima` baseline was measured against CWT on the
Stellar validation set (11,196 groups):

| | targets passing | snapped to a candidate | forced integrations |
|---|---|---|---|
| `osprey-cwt` | 3604 | 1061 | 210 |
| `local-maxima` | 3232 | 121 | 1723 |

The simple detector is clearly worse — and the *shape* of how it is worse is the interesting part. The huge
forced-integration count says it is failing to propose a candidate near the consensus RT, so reconciliation
has nothing to snap to. That is the signature of rule 1 being violated in practice (too few candidates), and
it is the first thing to look at when a new detector underperforms.

## Where the seam is wired

| | |
|---|---|
| [`OspreyTool.Core/Detection/IPeakDetector.cs`](../dotnet/src/OspreyTool.Core/Detection/IPeakDetector.cs) | the interface + `PeakWindow` + `PeakDetectionInput` |
| [`OspreyTool.Core/Detection/LocalMaximaPeakDetector.cs`](../dotnet/src/OspreyTool.Core/Detection/LocalMaximaPeakDetector.cs) | the dependency-free worked example |
| [`OspreyTool.Scoring/Detection/OspreyCwtPeakDetector.cs`](../dotnet/src/OspreyTool.Scoring/Detection/OspreyCwtPeakDetector.cs) | the default; wraps Osprey's CWT |
| [`OspreyTool.Scoring/Detection/PeakDetectors.cs`](../dotnet/src/OspreyTool.Scoring/Detection/PeakDetectors.cs) | the registry (`Default`, `All`, `ById`, `Register`) |
| `OspreyFeatureScorer.DetectPeaks` | bridges `PeakWindow` → Osprey's `XICPeakBounds`, deriving only the statistics your detector did not supply |
