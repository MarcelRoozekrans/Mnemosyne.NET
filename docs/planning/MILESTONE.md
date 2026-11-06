# Milestone 4: 1.0

**Status:** active
**Started:** 2026-11-06

## Goal
Ship `Mnemosyne.NET` 1.0.0 on NuGet, released by release-please, with the public API and graph schema locked by a mechanical gate.
- The versioning and lineage API leaves room for variant selection without a breaking change.
- Benchmarks run in CI, and the docsite explains the whole model, including the data protection guide.
- Milestone 3's open follow-ups are closed first: #55, #57 and #58.

See the [milestone design](../superpowers/specs/2026-11-06-milestone-4-design.md) and the [roadmap](ROADMAP.md#milestone-4-10-status-active). Earlier milestones are archived in [milestones/milestone-1.md](milestones/milestone-1.md), [milestones/milestone-2.md](milestones/milestone-2.md) and [milestones/milestone-3.md](milestones/milestone-3.md).

## Definition of Done
- [ ] All planned phases complete
- [ ] All tests passing, including integration tests against PostgreSQL + pgvector
- [ ] CI green, including the Native AOT gate for every package
- [ ] #55, #57 and #58 closed
- [ ] The versioning and lineage API lets variant selection be added without a breaking change
- [ ] A BenchmarkDotNet suite for ranking, search and middleware overhead runs in CI
- [ ] PublicApiAnalyzers baselines and package validation fail CI on a breaking change, and the graph schema compatibility policy is published
- [ ] The docsite is complete: concepts, the variants vision, the Daedalus and Thalos guide
- [ ] The data protection guide is published
- [ ] 1.0.0 is on NuGet, released by release-please

## Phases
1. Phase 4.1 — Test and sample hygiene [complete]
2. Phase 4.2 — Versioning and lineage API [complete]
3. Phase 4.3 — Benchmarks in CI [complete]
4. Phase 4.4 — Public API review and freeze [complete]
5. Phase 4.5 — Docs completeness [complete]
6. Phase 4.6 — Data protection guide [complete]
7. Phase 4.7 — Subject export [pending]
8. Phase 4.8 — 1.0.0 release [pending]

## Audit History
| Date | Verdict | Gaps |
|---|---|---|
