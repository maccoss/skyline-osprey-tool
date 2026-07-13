# Verified data formats (M0 inputs)

Facts on this page were **verified against real data** on 2026-07-10 using the live Skyline instance
and the target-only Carafe/Cadenza `.blib`, not just read from source. They resolve several "open items
to re-verify" in [`PROJECT_BRIEF.md`](PROJECT_BRIEF.md). Re-verify if the Skyline version or the Carafe
export pipeline changes.

**Test document used:** `TestData/…/2026-06-EISAI-Stellar-CSF-EV-targeted-PRM-refined.sky`
(Skyline-daily 26.1.1.159), 82 proteins · 303 peptides · **314 target precursors** · 1799 transitions ·
**18 replicates** · **0 decoys**. Library: `Cadenza-20260622-112136.blib`.
(`TestData/` is git-ignored — large binaries, kept local only.)

## 1. Skyline chromatogram export (`--chromatogram-file`)

The M0 XIC source. From a live tool: `RunCommandSilent`; from CLI: `SkylineCmd`.

```
--chromatogram-file=<out.tsv> --chromatogram-precursors --chromatogram-products
```
(`--chromatogram-precursors`/`--chromatogram-products` both require `--chromatogram-file`; other
options: `--chromatogram-base-peaks`, `--chromatogram-tics`. Precursors+products is the default if
neither is given.)

**Format — verified:**

- **Tab-delimited, 10 columns**, one row per **(transition, replicate)**:
  `FileName, PeptideModifiedSequence, PrecursorCharge, ProductMz, FragmentIon, ProductCharge,
  IsotopeLabelType, TotalArea, Times, Intensities`
- Row count = **transitions × replicates** (1799 × 18 = 32 382 data rows + 1 header). Precursor and
  product transitions are exported together; the precursor row has `FragmentIon = precursor` and
  `ProductMz` = the precursor m/z.
- **`Times` and `Intensities` are comma-separated numeric arrays inside a single tab cell** (no spaces),
  **equal length within a row**.
- **`Times` are in minutes.** Within a row the grid is regular/interpolated (Δ ≈ 0.0199 min ≈ 1.2 s,
  constant to ~1e-6).
- **The grid is per (precursor, replicate), NOT global.** Point counts observed range **72–167**
  (mode 79) — different peptides have different scheduled/extraction windows. **Parser must read each
  row's own arrays**; do not assume a shared length or axis across peptides or replicates.
- **All fragments of the same (FileName, precursor) share one identical `Times` grid** — so a
  precursor×replicate is already co-aligned (point counts appear in multiples of the ~6 transitions per
  precursor). This is exactly what CWT consensus + co-elution scoring want.
- **PRM product traces populate** (verified: `y8/y7/y6/b2` all carry full non-empty traces) — this was
  flagged as uncertain in the brief; it is not empty.

**→ Build `List<XicData>` per (FileName, PeptideModifiedSequence, PrecursorCharge):** group rows by those
three keys; each row → one `XicData { RetentionTimes = split(Times,','), Intensities = split(Intensities,',') }`;
`FragmentIndex` from row order / `FragmentIon`. Parse all numbers with `CultureInfo.InvariantCulture`
(export used invariant format, e.g. `6.400576E+07`).

## 2. Carafe/Cadenza `.blib` (Bibliospec SQLite)

Standard Bibliospec schema — `Osprey.IO/BlibLoader.Load(path)` should read it directly. Verified tables:
`LibInfo, RefSpectra, RefSpectraPeaks, RefSpectraProteins, Proteins, Modifications, RetentionTimes,
ScoreTypes, SpectrumSourceFiles, IonMobilityTypes`.

- **`RefSpectra`: 314 rows = exactly the 314 document precursors** (1 predicted spectrum per precursor).
- Key columns: `peptideSeq, peptideModSeq, precursorCharge, precursorMZ, numPeaks, retentionTime,
  startTime, endTime, totalIonCurrent, score, scoreType`. Fragment peaks live in `RefSpectraPeaks`
  (~6 per precursor, matching ~5.7 transitions/precursor in the doc).
- **`retentionTime` is the predicted RT in minutes** and falls inside the exported XIC window
  (e.g. `CLAVYQAGAR` predicted 8.27 min; extracted window 7.50–9.05 min). Use it as Osprey's expected-RT
  / RT-deviation input directly — no unit conversion.
- **Currently target-only — 0 decoys.** (Decoy strategy is being decided: in-memory generation à la
  Osprey `DecoyGenerator` vs. round-tripping decoy transitions through Skyline — see the brief's revised
  decoy decision.)

## 3. Join key

`peptideModSeq` in the blib and `PeptideModifiedSequence` in the chromatogram TSV use the **identical**
modified-sequence format (e.g. `C[+57]LAVYQAGAR`), and both carry `precursorCharge`/`PrecursorCharge`.
**Join blib ↔ document ↔ XICs on `(PeptideModifiedSequence, PrecursorCharge)` with no normalization.**
