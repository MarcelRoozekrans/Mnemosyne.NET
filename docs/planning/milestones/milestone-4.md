# Milestone 4: 1.0

**Status:** complete
**Started:** 2026-11-06
**Completed:** 2026-11-07

## Goal
Ship `Mnemosyne.NET` 1.0.0 on NuGet, released by release-please, with the public API and graph schema locked by a mechanical gate.
- The versioning and lineage API leaves room for variant selection without a breaking change.
- Benchmarks run in CI, and the docsite explains the whole model, including the data protection guide.
- Milestone 3's open follow-ups are closed first: #55, #57 and #58.

See the [milestone design](../superpowers/specs/2026-11-06-milestone-4-design.md) and the [roadmap](ROADMAP.md#milestone-4-10-status-active). Earlier milestones are archived in [milestones/milestone-1.md](milestones/milestone-1.md), [milestones/milestone-2.md](milestones/milestone-2.md) and [milestones/milestone-3.md](milestones/milestone-3.md).

## Definition of Done
- [x] All planned phases complete
- [x] All tests passing, including integration tests against PostgreSQL + pgvector
- [x] CI green, including the Native AOT gate for every package
- [x] #55, #57 and #58 closed
- [x] The versioning and lineage API lets variant selection be added without a breaking change
- [x] A BenchmarkDotNet suite for ranking, search and middleware overhead runs in CI
- [x] PublicApiAnalyzers baselines and package validation fail CI on a breaking change, and the graph schema compatibility policy is published
- [x] The docsite is complete: concepts, the variants vision, the Daedalus and Thalos guide
- [x] The data protection guide is published
- [x] 1.0.0 is on NuGet, released by release-please

## Phases
1. Phase 4.1 — Test and sample hygiene [complete]
2. Phase 4.2 — Versioning and lineage API [complete]
3. Phase 4.3 — Benchmarks in CI [complete]
4. Phase 4.4 — Public API review and freeze [complete]
5. Phase 4.5 — Docs completeness [complete]
6. Phase 4.6 — Data protection guide [complete]
7. Phase 4.7 — Subject export [complete]
8. Phase 4.8 — 1.0.0 release [complete]

## Audit History
| Date | Verdict | Gaps |
|---|---|---|
| 2026-11-07 | PASS | Warning: no pre-push review report dated within the milestone; unit 1645 and PostgreSQL 345 passed, CI green on main ([audit](../superpowers/reviews/2026-11-07-milestone-4-audit.md)) |
