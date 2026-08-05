# The learned peak pick (`--ranker lda`)

Peak picking is two decisions, and they are separately pluggable:

| decision | seam | ships today |
| --- | --- | --- |
| **which windows are candidates** | `IPeakDetector` ([guide](adding-a-peak-detector.md)) | `osprey-cwt` (default), `local-maxima` |
| **which candidate is the peak** | `ICandidateRankModel` (this doc) | `lda` (default), `product` |

This document covers the second one.

## What it is

A **frozen linear ranking of four standardized terms**. Nothing is trained at run time, no decoys are
involved, and no FDR is attached — it decides only *which* candidate window represents a precursor:

```text
rank = w0*z(coelution) + w1*z(ln_intensity) + w2*z(rt_penalty) + w3*z(median_polish)
       z(x_i) = (x_i - mean_i) / scale_i
```

Compare the pick it replaces — the same argmax, but a product of three terms with no spectral match in it:

```text
rank = coelution * exp(-dt^2 / 2*sigma^2) * ln(1 + apex_intensity)
```

So relative to before: co-elution and the RT penalty are still there, intensity moves from a multiplier to a
weighted term, and **`median_polish` (the library spectral match) joins the pick as a fourth weighted
feature** — where previously it was computed only *after* the pick, as a Percolator feature.

"LDA" describes where the numbers came from, not anything that runs here. Osprey's authors fit them offline
from target/decoy per-candidate dumps (`OSPREY_PICK_DUMP_CANDIDATES` → `pick_lda_train.py`, see Osprey's
`docs/peak-model-training.md`) and pasted the result into the source as constants.

**This is Osprey's default pick**, not an experiment: `OSPREY_PICK_LDA` defaults on (flipped with the #4484
golden re-baseline), so it is the default here too. `--ranker product` is the opt-out, matching
`OSPREY_PICK_LDA=0`.

## The weights, and picking the right set

Osprey ships **one model per platform** and they are not interchangeable. Copied verbatim from
`Osprey.Scoring/PickLdaModel.cs` (pwiz `dd9e84581`):

| | `coelution` | `ln_intensity` | `rt_penalty` | `median_polish` |
| --- | --- | --- | --- | --- |
| **Stellar** (unit resolution) | 0.9933 | 0.0471 | 0.0271 | 0.1018 |
| **Astral** (HRAM) | 0.5348 | 0.0041 | 0.3353 | 0.7756 |

Two things to read off that table. On unit resolution the pick is **co-elution, essentially alone** — which
is what the old `ln(1+I)` multiplier kept overriding, and why `--intensity-exp 0` existed as a hand-tuned
workaround. On HRAM the spectral match is worth more than co-elution, because at high resolution the fragment
pattern is trustworthy. Applying the wrong set would weight `median_polish` 7.6x and `rt_penalty` 12x off.

**The split is the fragment-tolerance unit, not the instrument name:**

| document | model |
| --- | --- |
| LIT product analyzer (`qit` / `ion_trap`) — e.g. Stellar | **Stellar** (unit) |
| no full-scan product analyzer, matching on `mz_match_tolerance` — a **triple quad**, SRM | **Stellar** (unit) |
| `orbitrap`, `tof`, `ft_icr`, `centroided` | **Astral** (HRAM) |

Pass `--sky <document.sky>` and the tool reads `transition_settings` and reports which way it went. The same
setting already drives Osprey's fragment tolerance, so the two stay consistent
(`SkylineTransitionSettings.IsUnitResolution` → `OspreyConfigFactory` → `ScoringSetupFactory`).
`--resolution unit|hram` overrides. With neither, the tool assumes unit resolution — its documented PRM
target — and **says so on the banner** rather than assuming silently:

```text
Resolution     : 2026-06-EISAI-Stellar-...sky (product analyzer 'qit') -> unit resolution
Pick rank model: Osprey learned pick (unit resolution / Stellar)
  rank = 0.993*z(coelution) + 0.047*z(ln_intensity) + 0.027*z(rt_penalty) + 0.102*z(median_polish)
  (frozen weights from Osprey; --intensity-exp and --lib-cosine do not affect this pick)
```

## Consequences worth knowing

* **The library is now needed for the pick.** `median_polish` is a weighted feature, so `reconcile`,
  `explain` and `repick` load the `.blib` fragment intensities whether or not `--lib-cosine` was given.
  Without a library the term falls back to the neutral 1.0 for every candidate — a constant offset that
  changes no ranking, but silently drops a real feature. The tool warns.
* **`--intensity-exp` and `--lib-cosine` do not apply.** Both shape the *product* form. The learned model
  consumes the raw terms: `ln(1+I)` itself (not `ln(1+I)^w`) and `median_polish` as a weighted feature (not a
  multiplier). They still work under `--ranker product`.

## What this is for, and how to judge it

**Picking the correct peak — not improving FDR.** In a targeted PRM assay every precursor is a known target
that is expected to be there; the question is never "does this pass a discovery threshold", it is "did we
integrate the right elution". Pick the wrong window and the peptide is not detected at all and its
quantification is wrong, no matter what any q-value says. So the metric that matters here is **pick
correctness**, judged per precursor per replicate.

That is worth stating explicitly because Osprey's own commit note on this model reports its effect as *small*:
a −3.5% to +1.8% swing across seven A/B cells at matched true FDP, moving discoveries by ~1%, while relocating
~44% of contested peaks. **That measurement is peptides passing FDR in a DIA search — a different problem.**
Do not carry it over to this tool: in a DIA discovery search a mis-picked peak costs one ID out of many
thousands, whereas in a targeted assay it costs the measurement you actually set out to make. The number to
read across from that note is the *relocation* rate, not the discovery delta.

By the targeted-assay metric the change is **large**: on the Stellar data 570 of 5614 (**10.2%**) apexes move,
median shift 0.36 min — well beyond peak width, so these are different elutions, not boundary nudges. Each one
is a measurement that is now either fixed or broken. Reviewing them is the evaluation; `--ranker product` exists
so the two picks can be compared directly, and `explain --peptide <seq>` shows every candidate with all four
terms so a disagreement can be adjudicated.

### Caution: the unit-resolution weights nearly discard the RT prior

Look again at the Stellar row — `rt_penalty` carries a weight of **0.027** against co-elution's **0.993**. In
the product form the RT prior was a *multiplicative* Gaussian at σ = 0.3 that heavily punished distance from the
expected RT; here it is one z-scored term with ~3% of the weight. The pick is co-elution, almost alone.

On the real Stellar run the two rankers disagree on 570 of 5614 picks, and the disagreements trade RT proximity
for co-elution. Both halves of that trade are measured below; **they point opposite ways, and neither is ground
truth**, which is why this needs adjudication on real chromatograms rather than a summary statistic.

Against the document's `ExplicitRetentionTime` (the **scheduling** RT — the instrument was targeted at it):

| | disagreements | learned pick closer to the scheduling RT |
| --- | --- | --- |
| all | 570 | 102 (17.9%) |
| blanks | 156 | 22 (14.1%) |
| real samples | 414 | 80 (19.3%) |

Median |apex − scheduling RT|: **0.086 min product → 0.349 min learned**, a 4x increase.

Against fragment co-elution at the window each one chose:

| | product pick | learned pick |
| --- | --- | --- |
| median co-elution | +0.317 | **+0.452** |
| learned pick had the higher co-elution | — | **526 / 570 (92%)** |

For scale, the 5044 picks where the two **agree** have a median co-elution of +0.686. So the disagreements are
concentrated in the weak, genuinely ambiguous precursors — not spread evenly.

**A worked case, and a caution about reading the RT statistic naively.** `ILGQQVPYATK +2` in `MMCC-3-001`: the
candidate 0.01 min from the scheduling RT spans 7 scans of flat baseline a few hundred counts high, while each
fragment's whole-chromatogram area is orders of magnitude larger — there is no peak there at all. Its co-elution
is −0.104, correctly, and the learned pick goes elsewhere (11.28, co-elution 0.895). The multiplicative Gaussian
RT prior is what held the old pick near an empty window. Restricting co-elution to the top-3 fragments does not
rescue that window either (−0.145), so this is not an artifact of averaging over noisy transitions.

That case is the product rank's failure mode, and the 92% co-elution figure says it is the common direction. The
opposite failure — a strong interference elsewhere winning on co-elution because `rt_penalty` now carries only 3%
of the weight — is the risk the RT statistic is pointing at, and it is real: a mis-picked peak in a targeted assay
is a lost measurement. Which one dominates on *your* data is an empirical question about your chromatography, not
something these numbers settle.

Practical notes. The learned pick **is** the default, matching Osprey. If you have an established assay whose
picks were reviewed under the product rank, diff the two before switching — `--ranker product` reproduces the old
picks exactly, and `explain --peptide <seq>` shows all four terms per candidate. `--rt-sigma` will not buy the RT
prior back: it shapes `rt_penalty`, but the weight on that term is what shrank, and re-weighting means a
retrained model, which belongs upstream in Osprey.

## Where the code is

| file | what |
| --- | --- |
| `Ranking/PickLdaModel.cs` | the frozen weights + `Score(coelution, lnIntensity, rtPenalty, medianPolish)`, and the per-resolution selection |
| `Ranking/ICandidateRankModel.cs` | the seam, plus `PickLdaRankModel` and `ProductRankModel` |
| `ScoringSetup.cs` | document → resolution → (Osprey config, rank model), with the provenance string |
| `OspreyFeatureScorer.Repick` | computes the four raw terms per candidate and takes the argmax |

The weights are **copied** rather than referenced because Osprey's `PickLdaModel` is `internal sealed`.
Making it — or a factory for the resolution-keyed defaults — public is the upstream ask when the `Osprey.Api`
facade lands; `RankModelPickTests.Frozen_weights_match_osprey` pins the constants so a drift fails a test.
Osprey's `OSPREY_PICK_LDA_MODEL` JSON override is a test hook and is deliberately not wired up: this tool
always uses the hardcoded model, and there is no model to save or load.

The four terms are computed in `OspreyFeatureScorer.WindowTerms` and match Osprey's definitions
(`PeakDataExtractor.CandidateLibCosine` is documented upstream as mirroring this repo's version). One
difference is inherent to the tool: co-elution is the mean pairwise Pearson over **the transitions Skyline
extracted**, whereas Osprey computes it over its own top-N fragment set, so the input distribution is not
identical to the one the weights were standardized against.

## Verified

* **Real Stellar data (5652 groups, 18 runs).** `--ranker product` reproduces the pre-change output
  **byte-identically** — `repick` boundaries and per-precursor report, md5
  `7c27be13f179678a28c4d6454b2a8940`, confirmed against a build of the previous commit.
* **The learned pick relocates 10.2% of apexes** on that data — 570 of 5614, with 556 of those moving by more
  than 0.1 min and a median shift of 0.360 min. That is a large change to a targeted assay's results, not a
  tuning tweak: 570 measurements now integrate a different elution. `--sky` correctly resolved the document to
  `qit` → unit resolution → Stellar weights.
* **69 tests pass**, including: the frozen constants pinned against Osprey's source; the rank equalling
  Osprey's arithmetic on the four raw terms; unit vs HRAM selection from a document for `qit`, a bare
  `mz_match_tolerance` (triple quad), and each of `orbitrap`/`tof`/`ft_icr`/`centroided`; `--resolution`
  overriding the document; and the no-document assumption being stated in the provenance string.
* **Not measured, and this is the open question: are the 570 relocations right?** Nothing above answers it.
  It needs the moved picks reviewed against the true elution — start with the known-hard cases
  (`ILGQQVPYATK`, `QSMAQRAR`, `KDVLETFTVK`), then work the list of disagreements between `--ranker lda` and
  `--ranker product`. Note that `repick`'s second-best-peak FDR counts also move, but that null is not a
  calibrated FDR (see the decoy-FDR notes) and it is not evidence about pick correctness either way.
