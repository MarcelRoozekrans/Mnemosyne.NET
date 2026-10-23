# Milestone 1: Store and reuse

**Status:** active
**Started:** 2026-10-21

## Goal
Store successful procedures and find the best one for a new question, covering
handover phases 1 and 2. A caller stores the procedure it followed, with its
intent, embedding, execution and feedback, under a scope and with provenance.
For a new question the selector returns ranked candidates with a score
breakdown. Erasure and retention are in from the start, because they shape the
schema, and capture, recall and erasure emit OpenTelemetry signals. The milestone ships a NuGet preview and a live docsite
with getting-started docs and a sample. See the
[roadmap design](../superpowers/specs/2026-10-21-roadmap-design.md).

## Definition of Done
- [ ] All planned phases complete
- [ ] All tests passing, including integration tests against PostgreSQL + pgvector
- [ ] CI green, including the Native AOT gate
- [ ] A procedure can be stored with intent, embedding, execution and feedback, scoped and with provenance
- [ ] For a new question, the selector returns ranked candidates with a score breakdown, filtered by scope
- [ ] Telemetry, erasure by scope or subject, and retention work
- [ ] A preview is on NuGet via release-please, and the docsite is live with getting-started docs and a sample

## Phases
1. Phase 1.1 — Repo scaffold, CI and release pipeline [complete]
2. Phase 1.2 — Abstractions [complete]
3. Phase 1.3 — Graph JSON schema v1 [complete]
4. Phase 1.4 — PostgreSQL + pgvector store [complete]
5. Phase 1.5 — Telemetry and retention [complete]
6. Phase 1.6 — Selector and ranking [pending]
7. Phase 1.7 — Docsite skeleton and first sample [pending]

## Audit History
| Date | Verdict | Gaps |
|---|---|---|
