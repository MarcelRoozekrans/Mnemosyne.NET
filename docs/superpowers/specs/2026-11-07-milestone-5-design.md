# Milestone 5 Design — Variants

**Date:** 2026-11-07
**Milestone:** 5
**Stage:** milestone (one milestone, multiple phases)

Builds on the [milestone 4 design](2026-11-06-milestone-4-design.md) and on `docs/concepts/variants.md`, which published the direction.

## Goal

A new version of a procedure gets a fair chance to prove itself. Today the default ranking scores every version on its own, so an older version with a long record usually beats a new, better one that has few runs. This milestone adds an opt-in variant selector that groups candidates by lineage, picks one variant per lineage from how each has performed, and tries new versions before the best known one dominates. It ships without changing the 1.0 public API.

## Decisions

| Decision | Detail | By |
|---|---|---|
| Shape | A `VariantSelector` that decorates `IReasonletSelector`, in the core package, enabled with an opt-in option on `AddMnemosyne`. No new port, no change to the default `ReasonletSelector`. | user, approach 1 |
| Policy | Thompson sampling on each version's success and failure counts, with the parent's metrics as a prior while a version is new. | my call |
| Reproducibility | Randomness comes from an injectable source and the option can fix a seed. With the same seed, query and metrics the selection is the same. | my call |
| Minimum trials | An option guarantees each new version a minimum number of runs before the policy may drop it. Off by default. | my call |
| Telemetry | The recall activity records the chosen variant, and whether the pick was an exploration. It carries ids and counts only, never content. | my call |
| Public API | Additive only: new options and a new selector type, declared in `PublicAPI.Unshipped.txt`. Package validation against 1.0.0 must stay green. | user |
| Release | release-please owns releases; the milestone ships in minor releases. | conventions |

## Definition of Done

- [ ] All planned phases complete
- [ ] All tests passing, including integration tests against PostgreSQL + pgvector
- [ ] Variant selection works behind the existing selector interface, opt-in, and package validation against 1.0.0 stays green
- [ ] A new version with few runs gets tried before the best known one dominates, shown by a simulation test
- [ ] Selection is deterministic when seeded, and the recall activity records the chosen variant and whether it was an exploration
- [ ] Docs and a self-checking sample cover variants

## Phases

1. **Phase 5.1: Variant selection** — `Surface: Backend`
   - **Goal:** A `VariantSelector` over `IReasonletSelector` that groups candidates by lineage and picks the best variant per lineage, opt-in through a service extension, with telemetry. Grooming moved the seedable random source to 5.2.
2. **Phase 5.2: Exploration** — `Surface: Backend`
   - **Goal:** Thompson sampling with the parent prior, a seed and an optional minimum number of trials, proven by a simulation in which a stronger new version overtakes an incumbent.
3. **Phase 5.3: Docs and sample** — `Surface: Docs`
   - **Goal:** Update `variants.md`, add a variants guide and a self-checking sample that CI runs.

## Approaches considered

- **Selector decorator (chosen).** Opt-in, additive, no new port.
- **New `IVariantSelector` port.** Cleaner concept, but adds public API right after the freeze and a 1.x compatibility cost.
- **Exploration bonus in the default score.** Smallest change, but it changes behaviour for every 1.0 user and mixes "pick a sibling" with "rank procedures".

## Dependencies on Prior Milestones

- Milestone 4: `LineageId` on candidates and matches, `ParentMetrics`, the `IReasonletSelector` port and the 1.0 API baseline.

## External Constraints

- 1.x compatibility: nothing shipped in 1.0.0 may change or be removed.
- Native AOT: the selector must pass the AOT gate, so no reflection.

## Risk Areas

| Risk | Impact | Mitigation |
|---|---|---|
| Random choice makes a query return different procedures on repeat | Surprising agents, flaky tests and benchmarks | Seedable source, off by default, telemetry marks explorations |
| Exploration serves a worse variant | Lower success while exploring | Parent prior and minimum trials bound it; the simulation measures the cost |
| Grouping by lineage hides an unrelated procedure that shares a lineage | A good match is dropped | Group only within one scope, and keep the per-lineage best of the default ranking as a fallback |
| Selector needs data the candidate does not carry | A breaking change to add it | `ReasonletCandidate` is init-only and `Runs`, `JudgedRuns` and the parent metrics are already there; check in 5.1 |

## Open Questions

- None blocking. The sample's domain is left to phase 5.3.
