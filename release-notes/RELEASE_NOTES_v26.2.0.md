# OspreyTool v26.2.0 Release Notes

Peak picking moves to Osprey's learned linear pick — the frozen four-term model that is now Osprey's own
default — with the per-platform weight set chosen from the Skyline document's mass analyzer.

## New Features

- **Osprey's learned peak pick is now the default (`--ranker lda`).** Which candidate peak wins is a pluggable
  seam (`ICandidateRankModel`), and the default moved to Osprey's frozen linear pick — matching Osprey's own
  default (`OSPREY_PICK_LDA`, on since #4484):
  `w0*z(coelution) + w1*z(ln_intensity) + w2*z(rt_penalty) + w3*z(median_polish)`, with the weights copied
  verbatim from `Osprey.Scoring/PickLdaModel.cs`. Nothing is trained, no decoys are needed, no FDR is attached
  — the weights were fit offline in the Osprey repo and ship as constants. Relative to the previous pick,
  co-elution and the RT penalty are unchanged, intensity moves from an unbounded multiplier to a weighted
  term, and the library spectral match (`median_polish`) joins the pick instead of being computed only after
  it. This targets **peak-pick correctness**, which is the whole game in a targeted assay — a mis-picked peak
  is a lost measurement, not a small FDR penalty. On the Stellar test data it relocates **10.2% of per-run
  apexes** (570 of 5614) and **20.2% of reconciled apexes** (1144 of 5652, median shift 0.200 min), so diff the
  two on an established assay before switching — `--ranker product` reproduces the old picks exactly for
  comparison. See [`docs/lda-peak-picking.md`](../docs/lda-peak-picking.md).
- **The right weight set is chosen from the document.** Osprey ships one model per platform and they are not
  interchangeable. `--sky <document.sky>` reads the product mass analyzer: a fixed m/z fragment tolerance (a
  LIT such as Stellar, or a triple quad matching on `mz_match_tolerance`) selects the unit-resolution weights;
  a ppm / resolving-power analyzer (`orbitrap`, `tof`, `ft_icr`, `centroided`) selects the HRAM weights.
  `--resolution unit|hram` overrides, and the choice is always printed with where it came from. The GUI reads
  it from the open document.
- **`--ranker product`** selects the previous multiplicative rank (Osprey's `OSPREY_PICK_LDA=0`) and is
  byte-identical to before this change, verified on the full 5652-group Stellar export.
- **The Settings tab's "Primary scoring" dropdown** now offers the learned pick (default) alongside the two
  legacy product-rank variants, and the Log reports the resolution, its provenance, and the active weights.

## Notes on the change in behavior

- **The unit-resolution weights lean almost entirely on co-elution** — `rt_penalty` carries 0.027 against
  co-elution's 0.993, where the previous pick applied the RT prior as a *multiplicative* Gaussian at σ = 0.3.
  Per run this makes picks wander: on the Stellar export the two rankers disagree on 570 of 5614 per-run picks,
  and the learned pick chose the **higher co-elution** window in 526 of them (92%; median +0.452 vs +0.317)
  while sitting **further from the scheduling RT** (median |apex − `ExplicitRetentionTime`| 0.086 → 0.349 min).
- **Reconciliation absorbs almost all of that**, which is what it is for — the cross-run consensus RT is
  anchored by the confident replicates, so one replicate's bad pick is outvoted. Measured through the full
  pipeline on the same data, median |apex − scheduling RT| in the **boundaries actually written to Skyline** is
  0.040 min (product) vs 0.046 min (learned). The learned pick's real downstream effect is that reconciliation
  does more work: 1553 snap-to-candidate moves vs 1045, 303 forced integrations vs 214, 64 charge-consensus
  moves vs 34, with the same 301 consensus peptides. 1144 of 5652 delivered apexes (20.2%) differ, median shift
  0.200 min.
- **So evaluate a ranker change on `reconcile` output, not `repick`** — the per-run numbers overstate it.
  `--rt-sigma` cannot restore the RT prior: it shapes the term whose weight shrank. See
  [`docs/lda-peak-picking.md`](../docs/lda-peak-picking.md).

## Breaking Changes

- **Default peak picks move.** `reconcile`, `repick`, `decoyfdr`, `explain` and the GUI now rank candidates
  with the learned model by default; use `--ranker product` for the previous behavior.
- **`--intensity-exp` and `--lib-cosine` no longer affect the default pick** — both shape the product form
  only. The learned model consumes the raw `ln(1+I)` and weights `median_polish` itself.
- **The `.blib` fragment intensities are now read for the pick**, not just for `--lib-cosine`.
