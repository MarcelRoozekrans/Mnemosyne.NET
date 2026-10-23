# Project Roadmap

Design: [roadmap design](../superpowers/specs/2026-10-21-roadmap-design.md), building on
[the Reasonlets design](../superpowers/specs/2026-10-21-mnemosyne-reasonlets-design.md).

## Milestone 1: Store and reuse [status: active]
**Goal:** Store successful procedures and find the best one for a new question (handover phases 1+2), shipped as a NuGet preview with a live docsite.
**Started:** 2026-10-21
**Definition of Done:**
- [x] A procedure can be stored with intent, embedding, execution and feedback, scoped and with provenance
- [x] For a new question, the selector returns ranked candidates with a score breakdown, filtered by scope
- [x] Telemetry for capture, recall and erasure, erasure by scope or subject, and retention work against PostgreSQL + pgvector
- [x] CI is green, including the Native AOT gate and integration tests against pgvector
- [ ] A preview is on NuGet via release-please, and the docsite is live with getting-started docs and a sample

### Phase 1.1: Repo scaffold, CI and release pipeline [status: complete]
**Goal:** Solution, central package management, CI with a Native AOT gate, release-please, Renovate and Codecov, matching the sibling repos.
**Surface:** Infra
**HelpWanted:** no
**Plan:** `docs/superpowers/plans/2026-10-21-phase-1.1-scaffold.md`
**Completed:** 2026-10-21

### Phase 1.2: Abstractions [status: complete]
**Goal:** Records, the scope and provenance model, and the store and selector interfaces in `Mnemosyne.NET.Abstractions`.
**Surface:** Backend
**HelpWanted:** no
**Plan:** `docs/superpowers/plans/2026-10-22-phase-1.2-abstractions.md`
**Completed:** 2026-10-22

### Phase 1.3: Graph JSON schema v1 [status: complete]
**Goal:** A minimal, versioned node-and-edge schema that Mnemosyne owns and callers may extend.
**Surface:** Backend
**HelpWanted:** no
**Plan:** `docs/superpowers/plans/2026-10-22-phase-1.3-graph-schema.md`
**Completed:** 2026-10-22

### Phase 1.4: PostgreSQL + pgvector store [status: complete]
**Goal:** `Mnemosyne.NET.PostgreSql` on ZeroAlloc.ORM and pgvector with embedded SQL migrations, implementing the store, including erasure.
**Surface:** Data
**HelpWanted:** no
**Plan:** `docs/superpowers/plans/2026-10-22-phase-1.4-postgres-store.md`
**Completed:** 2026-10-22

### Phase 1.5: Telemetry and retention [status: complete]
**Goal:** OpenTelemetry activities and events for capture, recall, erasure and retention, and a retention policy. Erasure itself moved to phase 1.4.
**Surface:** Data
**HelpWanted:** no
**Plan:** _to be written_

### Phase 1.6: Selector and ranking [status: complete]
**Goal:** Scope and intent filter, pgvector similarity search, and ranking (0.50 similarity, 0.30 success rate, 0.20 freshness) with a score breakdown.
**Surface:** Backend
**HelpWanted:** no
**Plan:** _to be written_

### Phase 1.7: Docsite skeleton and first sample [status: complete]
**Goal:** Docusaurus docsite deployed from CI, getting-started docs, and a store-and-recall sample.
**Surface:** Docs
**HelpWanted:** no
**Plan:** _to be written_

## Milestone 2: Evaluation [status: pending]
**Goal:** Answer "did it work?" without the caller hand-filling feedback, so the success-rate term in ranking is real (handover phase 3).
**Definition of Done:**
- [ ] Runs are scored by a pluggable evaluator, with explicit feedback overriding it
- [ ] An LLM-as-judge default and a Minos.NET evaluator are shipped
- [ ] Success rate in ranking comes from evaluated executions
- [ ] Docs and a sample cover evaluation

### Phase 2.1: Evaluator contract and feedback API [status: pending]
**Goal:** `IReasonletEvaluator` and an explicit feedback API that takes precedence over automatic scoring.
**Surface:** Backend
**HelpWanted:** no
**Plan:** _to be written_

### Phase 2.2: LLM-as-judge evaluator [status: pending]
**Goal:** A default evaluator that judges question, procedure and answer through an `IChatClient`.
**Surface:** Backend
**HelpWanted:** no
**Plan:** _to be written_

### Phase 2.3: Minos.NET evaluator [status: pending]
**Goal:** An evaluator adapter that scores runs through Minos.NET.
**Surface:** Backend
**HelpWanted:** no
**Plan:** _to be written_

### Phase 2.4: Metrics feeding ranking [status: pending]
**Goal:** Metrics computed from evaluated executions, with a minimum run count, feeding the success-rate term.
**Surface:** Data
**HelpWanted:** no
**Plan:** _to be written_

### Phase 2.5: Evaluation docs and sample [status: pending]
**Goal:** Docsite pages and a sample for evaluation and feedback.
**Surface:** Docs
**HelpWanted:** no
**Plan:** _to be written_

## Milestone 3: Off-the-shelf integrations [status: pending]
**Goal:** Any `IChatClient` app or Agent Framework agent gets recall, capture and learning without writing Mnemosyne-specific code.
**Definition of Done:**
- [ ] `UseReasonlets()` recalls, captures and learns in an `IChatClient` pipeline
- [ ] The `find_procedure` tool and the `AIContextProvider` are shipped
- [ ] Captured content passes a redaction hook, AI.Sentinel by default, and an opt-in review gate
- [ ] Docs and samples cover every integration

### Phase 3.1: Capture [status: pending]
**Goal:** Turn a tool-call trace into graph JSON, stored as a new Reasonlet or a new version of the recalled one.
**Surface:** Backend
**HelpWanted:** no
**Plan:** _to be written_

### Phase 3.2: Redaction hook [status: pending]
**Goal:** A hook that runs on captured content before it is stored.
**Surface:** Backend
**HelpWanted:** no
**Plan:** _to be written_

### Phase 3.3: Human review gate [status: pending]
**Goal:** Opt-in pending-review state for captured procedures, with approve and reject.
**Surface:** Backend
**HelpWanted:** no
**Plan:** _to be written_

### Phase 3.4: AI.Sentinel adapter [status: pending]
**Goal:** `Mnemosyne.NET.Sentinel`: Sentinel detectors as the default redaction hook.
**Surface:** Backend
**HelpWanted:** no
**Plan:** _to be written_

### Phase 3.5: UseReasonlets middleware [status: pending]
**Goal:** `IChatClient` middleware for recall before the call and background capture after it.
**Surface:** Backend
**HelpWanted:** no
**Plan:** _to be written_

### Phase 3.6: find_procedure recall tool [status: pending]
**Goal:** An `AIFunction` the model calls to look up a procedure when it wants one.
**Surface:** Backend
**HelpWanted:** no
**Plan:** _to be written_

### Phase 3.7: Agent Framework AIContextProvider [status: pending]
**Goal:** A thread-aware `AIContextProvider` for Microsoft Agent Framework, usable by Thalos.NET and Daedalus.NET.
**Surface:** Backend
**HelpWanted:** no
**Plan:** _to be written_

### Phase 3.8: Integration docs and samples [status: pending]
**Goal:** Docsite pages and samples for each integration.
**Surface:** Docs
**HelpWanted:** no
**Plan:** _to be written_

## Milestone 4: 1.0 [status: pending]
**Goal:** Ship 1.0.0: stable public API, versioning ready for variants, benchmarks in CI, complete docs.
**Definition of Done:**
- [ ] Versioning and lineage API lets variant selection be added without a breaking change
- [ ] BenchmarkDotNet suite runs in CI
- [ ] Public API and graph schema compatibility policy reviewed
- [ ] Docsite complete, including the data protection guide
- [ ] 1.0.0 on NuGet

### Phase 4.1: Versioning and lineage API [status: pending]
**Goal:** Settle the versioning and lineage API so variant selection fits later.
**Surface:** Backend
**HelpWanted:** no
**Plan:** _to be written_

### Phase 4.2: Benchmarks in CI [status: pending]
**Goal:** BenchmarkDotNet suite for ranking, search and middleware overhead, running in CI.
**Surface:** Infra
**HelpWanted:** no
**Plan:** _to be written_

### Phase 4.3: Public API review [status: pending]
**Goal:** Review the public API and set the graph schema compatibility policy.
**Surface:** Refactor
**HelpWanted:** no
**Plan:** _to be written_

### Phase 4.4: Docs completeness [status: pending]
**Goal:** Concepts, the variants vision, and the Daedalus and Thalos guide.
**Surface:** Docs
**HelpWanted:** no
**Plan:** _to be written_

### Phase 4.5: Data protection guide [status: pending]
**Goal:** How scope, provenance, redaction, erasure, retention and telemetry support a deployer's GDPR duties, and a short note on where the EU AI Act does and does not apply.
**Surface:** Docs
**HelpWanted:** no
**Plan:** _to be written_

### Phase 4.6: 1.0.0 release [status: pending]
**Goal:** Release 1.0.0 through release-please.
**Surface:** Infra
**HelpWanted:** no
**Plan:** _to be written_

## Milestone 5: Variants [status: pending]
**Goal:** Choose between sibling versions of a procedure, post-1.0 (handover phase 4).
**Definition of Done:**
- [ ] Variant selection works behind an interface without breaking the 1.0 API
- [ ] New versions get explored before the best one dominates
- [ ] Docs and a sample cover variants

### Phase 5.1: Variant selection strategy [status: pending]
**Goal:** A variant selection strategy behind an interface.
**Surface:** Backend
**HelpWanted:** no
**Plan:** _to be written_

### Phase 5.2: Exploration versus exploitation [status: pending]
**Goal:** Give new versions a chance to prove themselves before the best known version dominates.
**Surface:** Backend
**HelpWanted:** no
**Plan:** _to be written_

### Phase 5.3: Variant docs and sample [status: pending]
**Goal:** Docsite pages and a sample for variants.
**Surface:** Docs
**HelpWanted:** no
**Plan:** _to be written_
