# BiblioSpec `.blib` target/decoy pairing table — specification

**Status:** proposal (for Carafe to emit; for the Osprey tool + upstream Osprey to consume).

## Motivation

A Carafe target+decoy `.blib` stores decoys as **ordinary `RefSpectra`** (reversed sequences). Nothing in
the standard BiblioSpec schema marks *which* entries are decoys or links a target to its paired decoy — that
information currently lives in a side-car file (`osprey_library_db_pairing.tsv`: `sequence, decoy,
proteins, peptide_type, peptide_pair_index`). This spec moves the pairing **into the `.blib`** as one extra
SQLite table so the library is self-describing, **while remaining invisible to Skyline**.

## Design constraints

1. **Skyline-neutral (the hard requirement).** Skyline must load and use the `.blib` *identically* whether
   or not this table is present. Achieved by making it a purely **additive** table that Skyline never
   queries, touching **no** standard table/column and **not** changing the `LibInfo` schema version. (The
   decoy `RefSpectra` are already ordinary library entries that Skyline handles today; this table only adds
   linkage Skyline ignores.)
2. **Self-contained.** Keyed to `RefSpectra.id` — no dependence on an external file.
3. **Minimal.** One small table, one index, no version bump.

## The table

```sql
CREATE TABLE IF NOT EXISTS DecoyPairs (
    RefSpectraID INTEGER NOT NULL PRIMARY KEY,  -- FK -> RefSpectra.id (one row per participating precursor)
    IsDecoy      INTEGER NOT NULL,              -- 0 = target, 1 = decoy
    IsEntrapment INTEGER NOT NULL,              -- 1 = FDRBench entrapment peptide (p_target/p_decoy); 0 = real
    PairID       INTEGER NOT NULL,              -- the target precursor and its paired decoy share this value
    Method       TEXT                           -- how the decoy was built (see below); NULL for target rows
);
CREATE INDEX IF NOT EXISTS idx_DecoyPairs_PairID ON DecoyPairs(PairID);
```

**Semantics**
- One row per `RefSpectra` (a *precursor* = `peptideModSeq` + `precursorCharge`) that participates in a
  target/decoy pair.
- `IsDecoy` = 0 for the target precursor, 1 for the decoy precursor.
- `IsEntrapment` = 1 for an FDRBench entrapment peptide (`p_target` / `p_decoy`), 0 for a real
  `target` / `decoy`. It is **orthogonal** to `IsDecoy`: Osprey scores an entrapment target as a target
  and its entrapment decoy as a decoy (so every target, real or entrapment, has a paired decoy); this
  flag carries the entrapment annotation forward so follow-on tools can measure performance against the
  entrapment set. It does not affect competition — it is provenance for downstream analysis.
- `PairID` links a pair: the target precursor and its decoy precursor **at the same charge** share one
  `PairID`. Each `PairID` therefore has exactly two rows (one `IsDecoy=0`, one `IsDecoy=1`), both with
  the same `IsEntrapment`. Entrapment quartets thus produce two pairs: `target`↔`decoy` and
  `p_target`↔`p_decoy`.
- `Method` names how the decoy sequence was constructed from its target: **`reverse`** (the common case)
  or **`cycle`** (the collision/palindrome fallback), extensible to other strings (`shuffle`, `mutate`, …).
  Set on decoy rows (`IsDecoy=1`); **`NULL` on target rows**. Consumers should treat it as an open
  vocabulary (compare case-insensitively; tolerate unknown values).
- A `RefSpectra` **absent** from the table is treated as an unpaired target (no decoy competition).

That is all a consumer needs: `IsDecoy` per precursor, `PairID` to recover the pair, and `Method` for
provenance / diagnostics.

## Why this shape (rejected alternatives)

- **A flag column on `RefSpectra`.** Altering `RefSpectra` risks Skyline's column validation and edits a
  table Skyline actively manages. A separate table is safer and strictly additive.
- **Osprey's `id | 0x80000000` decoy-id convention.** Requires controlling id assignment and breaks on
  large id spaces (this library uses ids up to ~276 k). Explicit pairing is id-scheme-agnostic.
- **Sequence-based keys.** `RefSpectra.id` is exact and cheap. A target *sequence* with multiple charges
  becomes multiple precursors, each paired at its own charge — ids capture that directly; sequences don't.

## How Carafe populates it (maps from today's manifest)

Carafe already knows every target, its decoy, and the pairing (`peptide_pair_index`). When writing the
`.blib`, in the **same transaction** as the library:

1. While writing `RefSpectra`, remember each row's assigned `id`, keyed by `(sequence, charge, isDecoy)`.
2. For each `peptide_pair_index`, and for each charge present for that pair, emit **two** `DecoyPairs`
   rows — the target precursor's id (`IsDecoy=0`) and the decoy precursor's id (`IsDecoy=1`) — sharing a
   fresh `PairID` (a running counter, or `pair_index * MAX_CHARGE + charge`). Set `Method` on the decoy
   row to how that decoy was constructed (`reverse` / `cycle`); leave the target row's `Method` NULL.

Only pairs where **both** the target and decoy precursor exist at that charge are emitted (skip a charge
present on only one side).

*(Further optional provenance, not required for scoring: the source `pair_index` from the manifest as a
direct back-reference. The decoy construction method is already captured by `Method` above.)*

## How the Osprey tool consumes it

- Load `RefSpectra` + `DecoyPairs`. `IsDecoy` sets `PercolatorEntry.IsDecoy`; `PairID` groups the
  target/decoy precursors for competition, reporting, and (later) reconciliation.
- **Fallback when the table is absent:** join the side-car `osprey_library_db_pairing.tsv` on `sequence`
  (target/decoy label + `peptide_pair_index`), or fall back to in-memory decoy generation. So the tool
  works with old and new blibs.

## Skyline-neutrality — verify once, and a caveat

- **Verify:** on a test `.blib`, add the table, load in Skyline, and confirm library matching, RT, and peak
  picking are unchanged (they should be — Skyline queries only its known tables).
- ⚠️ **Caveat:** operations that *rewrite* the library (Skyline "Minimize library", or any re-export) may
  not carry the extra table forward. Treat `DecoyPairs` as **Carafe-authored provenance** — regenerated
  whenever Carafe writes the `.blib` — not something Skyline round-trips. The Osprey tool should read it
  from the Carafe-written `.blib` (or fall back to the manifest) rather than assume Skyline preserves it.

## Naming note

`DecoyPairs` is unprefixed for readability. If collision-avoidance with a possible future BiblioSpec table
matters, prefix it (e.g. `CarafeDecoyPairs`) — the consumer only needs one agreed name.
