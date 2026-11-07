# Milestone 5: Variants

**Status:** active
**Started:** 2026-11-07

## Goal
A new version of a procedure gets a fair chance to prove itself. An opt-in variant selector groups candidates by lineage, picks one variant per lineage from how each has performed, and tries new versions before the best known one dominates.
- It decorates the existing `IReasonletSelector`, so the 1.0 public API does not change.
- Thompson sampling on each version's success and failure counts, with the parent's metrics as a prior while a version is new.
- Selection is deterministic when seeded, and telemetry records the chosen variant and whether it was an exploration.

See the [milestone design](../superpowers/specs/2026-11-07-milestone-5-design.md) and the [roadmap](ROADMAP.md#milestone-5-variants-status-active). Earlier milestones are archived in [milestones/milestone-1.md](milestones/milestone-1.md), [milestones/milestone-2.md](milestones/milestone-2.md), [milestones/milestone-3.md](milestones/milestone-3.md) and [milestones/milestone-4.md](milestones/milestone-4.md).

## Definition of Done
- [ ] All planned phases complete
- [ ] All tests passing, including integration tests against PostgreSQL + pgvector
- [ ] Variant selection works behind the existing selector interface, opt-in, and package validation against 1.0.0 stays green
- [ ] A new version with few runs gets tried before the best known one dominates, shown by a simulation test
- [ ] Selection is deterministic when seeded, and the recall activity records the chosen variant and whether it was an exploration
- [ ] Docs and a self-checking sample cover variants

## Phases
1. Phase 5.1 — Variant selection [complete]
2. Phase 5.2 — Exploration [complete]
3. Phase 5.3 — Docs and sample [pending]

## Audit History
| Date | Verdict | Gaps |
|---|---|---|
