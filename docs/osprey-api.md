# Osprey API contract (for the `Osprey.Api` facade + adapter)

Verified by reading source at `D:\Dev\pwiz\pwiz_tools\Osprey\` (checkout of ProteoWizard/pwiz). All
namespaces are `pwiz.Osprey.*`. This is the integration contract for M1 - avoid re-researching; if a
signature here stops matching source, fix it here.

**Osprey build/targeting:** every Osprey project is SDK-style, `<TargetFrameworks>net472;net8.0`,
`<Platforms>AnyCPU;x64`, `Nullable disable`, `ImplicitUsings disable`. Builds standalone
(`dotnet build Osprey.sln`, net8.0). `XicData` and `XICPeakBounds` live in **different assemblies**
(`Osprey.Chromatography` vs `Osprey.Core`) - reference both.

## Call path (the 12-feature, XIC-only subset)

```
List<XicData>  (pwiz.Osprey.Chromatography; ctor: new XicData(fragmentIndex, retentionTimes[], intensities[]))
   -> CwtPeakDetector.DetectConsensusPeaks(xics, minConsensusHeight)  -> List<XICPeakBounds>   (sorted, best first)
        requires >=2 XICs, >=5 scans each, all equal length, else returns empty
   -> build an IOspreyDetailedPeakData adapter over (candidate, chosen XICPeakBounds, xics, expectedRt)
   -> ctx = new OspreyScoringContext(new OspreyConfig())
   -> per candidate: ctx.ClearByproducts(); publish median-polish byproduct (see below);
      foreach i in {0,1,2,3,4,5,11,12,15,16,19,20}: features[i] = OspreyFeatureCalculators.Get(i).Calculate(ctx, adapter)
   -> per run: PercolatorFdr.RunPercolator(entries, new PercolatorConfig())  -> PercolatorResults
        Results.Entries[k].{ExperimentPrecursorQvalue, RunPrecursorQvalue, Pep, Score}
```

### Signatures (verbatim)

```csharp
// pwiz.Osprey.Chromatography (Osprey.Chromatography\CwtPeakDetector.cs)
public static List<XICPeakBounds> CwtPeakDetector.DetectConsensusPeaks(List<XicData> xics, double minConsensusHeight);
public class XicData {                       // NOTE: Chromatography, not Core
    public int FragmentIndex { get; set; }
    public double[] RetentionTimes { get; set; }
    public double[] Intensities { get; set; }
    public XicData(int fragmentIndex, double[] retentionTimes, double[] intensities);
}

// pwiz.Osprey.Core (Osprey.Core\XICPeakBounds.cs) - parameterless ctor + object initializer
public class XICPeakBounds {
    public double ApexRt, ApexIntensity, StartRt, EndRt, Area, SignalToNoise; // all get;set;
    public int ApexIndex, StartIndex, EndIndex;
}

// pwiz.Osprey.Scoring (Osprey.Scoring\IOspreyPeakData.cs) - implement the Detailed tier
public interface IOspreySummaryPeakData {
    LibraryEntry Candidate { get; }          // pwiz.Osprey.Core
    XICPeakBounds PeakBounds { get; }        // pwiz.Osprey.Core
    double ApexRetentionTime { get; }
    double ExpectedRt { get; }
}
public interface IOspreyDetailedPeakData : IOspreySummaryPeakData {
    IReadOnlyList<XicData> Xics { get; }     // XicData = Osprey.Chromatography
    XicData Ms1PrecursorXic { get; }         // return null for unit-res PRM (features 13,14 only)
    XicData Ms1ReferenceXic { get; }         // return null
    double[] ApexIsotopeEnvelope { get; }    // return null
}
// (ApexSpectrum / ApexSpectra tiers add MS2-spectrum members - NOT needed by the 12.)

// pwiz.Osprey.Scoring
public interface IOspreyFeatureCalculator {
    string Name { get; } string DisplayName { get; } bool IsReversedScore { get; }
    double Calculate(OspreyScoringContext context, IOspreySummaryPeakData peakData);
}
public static class OspreyFeatureCalculators {
    public const int FeatureCount = 21;
    public static IOspreyFeatureCalculator Get(int featureIndex);              // 0..20; only public reach
    public static OspreyFeatureInfo[] BuildFeatureInfos(string[] featureNames); // length must be 21
}
public class OspreyScoringContext {
    public OspreyScoringContext(OspreyConfig config);   // construction needs ONLY a config
    public void ClearByproducts();                      // call per candidate
    public void AddInfo<TInfo>(TInfo info);             // publish byproduct (throws on dup type)
    public bool TryGetInfo<TInfo>(out TInfo info);
    // SetWindow(...) + Resolution/PreprocessedXcorr/Scorer are ONLY for xcorr/SG features (6,17,18) - skip.
}

// pwiz.Osprey.FDR (Osprey.FDR\PercolatorFdr.cs)
public static PercolatorResults PercolatorFdr.RunPercolator(IList<PercolatorEntry> entries, PercolatorConfig config);
public static PercolatorResults PercolatorFdr.ScorePopulationAndComputeFdr(IList<PercolatorEntry> entries,
    PercolatorResults trainResults, PercolatorConfig config, Func<string, IReadOnlyList<double[]>> loadFileFeatures = null);
public class PercolatorEntry {   // all get;set;
    public string FileName; public string Peptide; public byte Charge; public bool IsDecoy;
    public uint EntryId; public uint ParquetIndex; public double CoelutionSum; public double[] Features; // length 21
}
public class PercolatorConfig {  // parameterless ctor; defaults NFolds=3, Seed=42, Train/TestFdr=0.01, MaxIterations=10
    public double TrainFdr, TestFdr; public int MaxIterations, NFolds; public ulong Seed;
    public double[] CValues; public int MaxTrainSize; public OspreyFeatureInfo[] FeatureInfos;
    public bool CollectFeatureHistograms, TrainOnly; public PercolatorDiagnosticsConfig Diagnostics;
}
public class PercolatorResult {  // per input entry, same order, all default to 1.0
    public double Score, RunPrecursorQvalue, RunPeptideQvalue, ExperimentPrecursorQvalue, ExperimentPeptideQvalue, Pep;
}
public class PercolatorResults { public List<PercolatorResult> Entries; /* + fold weights/biases, standardizer */ }

// pwiz.Osprey.IO (Osprey.IO\BlibLoader.cs) - use THIS for the facade's Candidate objects (gives Fragments)
public class BlibLoader { public List<LibraryEntry> Load(string path); }
// pwiz.Osprey.Core
public class LibraryEntry {
    public const uint DECOY_ID_BIT = 0x80000000u;
    public uint Id; public string Sequence, ModifiedSequence; public List<Modification> Modifications;
    public byte Charge; public double PrecursorMz, RetentionTime; public bool RtCalibrated;
    public List<LibraryFragment> Fragments; public List<string> ProteinIds, GeneNames; public bool IsDecoy;
    public LibraryEntry(uint id, string sequence, string modifiedSequence, byte charge, double precursorMz, double retentionTime);
}
public struct LibraryFragment { public double Mz { get; set; } public float RelativeIntensity { get; set; } public FragmentAnnotation Annotation { get; set; } }
```

### Unit-resolution config (Stellar PRM) — derived from the document, not hardcoded

Tolerances come from the Skyline document's transition settings (Skyline extracted the chromatograms
with them), via `SkylineTransitionSettings.FromDocument` → `OspreyConfigFactory.FromTransitionSettings`.
The Stellar test doc: `product_mass_analyzer=qit` (unit resolution), `product_res=0.5`,
`precursor_res=0.7` → 

```csharp
var config = new OspreyConfig {
    ResolutionMode = ResolutionMode.UnitResolution,                       // enum: Auto, UnitResolution, HRAM
    FragmentTolerance = FragmentToleranceConfig.UnitResolution(0.5),      // product_res
    PrecursorTolerance = FragmentToleranceConfig.UnitResolution(0.7),     // precursor_res
};
```
Note: none of the 12-feature subset reads the tolerance yet (only the apex-spectrum families do), but it
is set correctly from the document so it is right when that surface grows.

## The 21 PIN features (`ParquetScoreCache.PIN_FEATURE_NAMES`) and our subset

| idx | name | in subset? | | idx | name | in subset? |
|--|--|--|--|--|--|--|
| 0 | fragment_coelution_sum | YES (primary) | | 11 | rt_deviation | YES |
| 1 | fragment_coelution_max | YES | | 12 | abs_rt_deviation | YES |
| 2 | n_coeluting_fragments | YES | | 13 | ms1_precursor_coelution | no (MS1) |
| 3 | peak_apex | YES | | 14 | ms1_isotope_cosine | no (MS1) |
| 4 | peak_area | YES | | 15 | median_polish_cosine | YES |
| 5 | peak_sharpness | YES | | 16 | median_polish_residual_ratio | YES |
| 6 | xcorr | no (xcorr cache) | | 17 | sg_weighted_xcorr | no (xcorr) |
| 7 | consecutive_ions | no (apex MS2) | | 18 | sg_weighted_cosine | no (xcorr) |
| 8 | explained_intensity | no (apex MS2) | | 19 | median_polish_min_fragment_r2 | YES |
| 9 | mass_accuracy_deviation_mean | no (apex MS2) | | 20 | median_polish_residual_correlation | YES |
| 10 | abs_mass_accuracy_deviation_mean | no (apex MS2) | | | | |

## Median-polish byproduct - the ONE place the facade carries real logic

Features 15/16/19/20 do **not** compute the fit; they read a `MedianPolishByproduct` the harness must
publish, else they return defaults (cosine 0.0, **residual_ratio 1.0**, min_fragment_r2 0.0,
residual_correlation 0.0). Replicate `CoelutionScorer.ScoreCandidate` exactly, per candidate:

1. Only when peak width `EndIndex - StartIndex + 1 >= 3`.
2. Crop each XIC's `Intensities` to `[StartIndex .. StartIndex+peakLen)` into
   `List<KeyValuePair<int,double[]>>` (key = `FragmentIndex`); build `peakRts` from
   `xics[0].RetentionTimes` over the same slice.
3. `var polish = TukeyMedianPolish.Compute(peakXics, peakRts, 10, 0.01);`  // params 10, 0.01 (NOT the
   method's 20/1e-4 doc default); returns null if <2 fragments or <3 scans.
4. `if (polish != null) ctx.AddInfo(new MedianPolishByproduct(polish, peakXics));`

`TukeyMedianPolish`, `TukeyMedianPolishResult`, `MedianPolishByproduct` are all public. Coelution (0/1/2)
and peak-shape (3/4/5) self-cache lazily - no publish needed.

## Gotchas

- **`XicData` is `pwiz.Osprey.Chromatography`**, `XICPeakBounds` is `pwiz.Osprey.Core` - two assemblies.
- Concrete calculators are **`internal sealed`** - reach only via `OspreyFeatureCalculators.Get(i)`.
- `Calculate` is typed to `IOspreySummaryPeakData`; passing a view narrower than a calculator's tier
  throws **`InvalidOperationException`** at runtime. Implement the full `IOspreyDetailedPeakData` on one
  adapter and reuse it for all 12.
- `LibraryFragment` is a **struct** - mutating requires write-back into the list.
- `RunPercolator` reads `entries[0].Features.Length`, so `Features` (length 21) must be populated;
  decide how the 9 non-subset slots are handled (constant/zeroed vs restricting `FeatureInfos`) when wiring.
- Target-decoy pairing convention: decoy `Id = target.Id | 0x80000000` (`LibraryEntry.DECOY_ID_BIT`).

## `DecoyGenerator` (the sequence-decoy alternative to our decoy-XIC)

`public class DecoyGenerator` (`Osprey.Scoring`) - reusable as-is, but it is **sequence reversal**
(enzyme-terminus-preserving; positional cycling on palindrome/collision), recomputing fragment m/z on
`LibraryEntry`. It does NOT touch chromatograms. Our chosen method is **decoy-XIC**
(`XicShuffleDecoyGenerator`); Osprey's `DecoyGenerator` is available if we later add a sequence-decoy
option behind a separate seam. Batch entry point:
`DecoyGenerator.GenerateAllWithCollisionDetection(targets, config, logInfo, out validTargets)`.

## Feature reachability — ALL 21 are reachable from an external assembly (verified)

The brief feared the xcorr family was blocked by an internal `WindowXcorrCache`. **It is not.** Verified
against source: every spectrum-dependent feature is reachable if we supply the observed MS2 spectra.

- **7,8,9,10 (apex-match) — easy.** Extend the adapter to `IOspreyApexSpectrumPeakData` and return
  `ApexSpectrum` = a `Spectrum` you build. `Spectrum` (`Osprey.Core`) has a public parameterless ctor;
  set `Mzs` (double[], **ascending-sorted**), `Intensities` (**float[]**, index-aligned), optionally
  ScanNumber/RetentionTime/PrecursorMz/IsolationWindow. These 4 read only `ApexSpectrum` +
  `Candidate.Fragments` + `Config.FragmentTolerance` - **no `SetWindow`, no cache**.
- **6,17,18 (xcorr / SG) — with effort.** Extend to `IOspreyApexSpectraPeakData`
  (`TryGetApexOffsetSpectrum(-2..+2)`, `ApexGlobalIndex`) and call `context.SetWindow(...)`. Build the args
  from PUBLIC API: `var res = ResolutionStrategy.Create(ResolutionMode.UnitResolution); var scorer =
  res.CreateScorer(); var pool = new XcorrScratchPool(scorer.BinConfig.NBins); var cache =
  res.PreprocessWindowSpectra(windowSpectra, scorer, pool); context.SetWindow(res, cache, scorer, pool);`.
  `WindowXcorrCache`'s ctors are internal, but `IResolutionStrategy.PreprocessWindowSpectra` (public,
  public interface) is the sanctioned factory. Cost: must supply the **full isolation-window scan list**
  (RT-sorted) and keep the apex's window-global index consistent with it. (Note 18 `sg_weighted_cosine`
  transitively needs this too - its shared sweep also fires `Resolution.ScoreXcorr`.)
- **13,14 (MS1) — out on unit-res PRM.** 13 needs `Ms1PrecursorXic`+`Ms1ReferenceXic`; 14 needs a
  **>=5-channel** `[M-1,M0,M+1,M+2,M+3]` isotope envelope (`envelope.Length < 5 -> -1.0 -> 0.0`). Stellar
  PRM has no usable MS1, so these stay 0. In production Osprey they are HRAM-only (`HasMs1Features=false`).

**Spectrum source:** Osprey reads **mzML only** - `MzmlReader.LoadAllSpectra(path)` /
`LoadAllSpectraSequential` (public, `Osprey.IO`) return `List<Spectrum>`; `SpectraCache` caches to
`.spectra.bin`. There is **NO Thermo `.raw` reader in Osprey.** So observed spectra require the raw
converted to mzML (msconvert), or we build `Spectrum` objects ourselves from another reader - the
calculators don't care how the spectrum was produced, only that `Mzs`/`Intensities` are set and sorted.

**Ceiling: 19 of 21** on unit-resolution Stellar PRM (12 XIC + 4 apex-match + 3 xcorr/SG), MS1 excluded -
and only when observed spectra are available. See `OspreyTool.Scoring.FeatureSelection` /
`RawFileAvailability`: when the raw is missing the tool computes the 12 and warns which scores were dropped.
