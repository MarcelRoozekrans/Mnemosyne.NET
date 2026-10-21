# Roadmap Design — Mnemosyne.NET

**Date:** 2026-10-21
**Author:** Claude, with the user
**Stage:** roadmap (whole project)

Builds on [the Reasonlets design](2026-10-21-mnemosyne-reasonlets-design.md), which
holds the data model, ranking formula, package split and stack decisions. This
document only sets the shape of the whole project up to and past 1.0. Stack and
release conventions are in [CONVENTIONS.md](../../planning/CONVENTIONS.md).

Decisions are marked **user** or **my call**, as in the Reasonlets design.

## Goal

Mnemosyne.NET is procedural memory for .NET agents: it keeps how a problem was
solved (a **Reasonlet**: question → procedure → answer), judges whether it
worked, and hands the best known procedure back when a similar question comes
in. The project is done when it ships a **true 1.0** on NuGet (**user**): a
library someone outside this family of repos can pick up off the shelf, with a
docsite, runnable examples, and benchmarks that run in CI. Variant selection
(handover phase 4) may come after 1.0, but 1.0 already makes the whole idea
clear, in the docs and in the API shape, so variants are an addition and not a
redesign (**user**). Executing procedures is never this repo's job: Daedalus,
or any other caller, decides for itself how to run a procedure (**user**).

## Target Users / Stakeholders

- **Any .NET app on `Microsoft.Extensions.AI`**: wants procedural memory with
  one line, `builder.UseReasonlets()`, or a recall tool its model can call.
  Served by the off-the-shelf integrations (**user**: "usable off the shelf").
- **Microsoft Agent Framework users, Thalos.NET and through it Daedalus.NET**:
  want memory as an `AIContextProvider`, the same hook Thalos already uses
  for `Thalos.NET.Memory` and `Thalos.NET.Skills` (**user** chose to build
  it; Thalos usage verified in `Thalos.NET/src/Thalos.NET/Runtime/IAgentContextProviderSource.cs`).
- **Builders who want control**, Daedalus first: use the store, selector and
  evaluator directly and decide what counts as a procedure and when to store.
- **Out of scope**: anyone wanting an answer cache, a workflow engine, or a
  graph executor.

## Cross-Cutting Decisions

| Decision | Detail | By |
|---|---|---|
| Scoping | Every Reasonlet belongs to a scope (tenant, app or user, caller-defined). The scope is in the M1 data model and in the store and selector signatures, because adding it after 1.0 would break both. | user |
| Redaction | A hook runs on captured content before it is stored (M3), so tool arguments carrying user data or secrets can be stripped. | user |
| Graph JSON | Mnemosyne owns a minimal, versioned schema of nodes and edges, defined in M1. It is part of the 1.0 contract and gets a compatibility policy in M4. Callers may add their own properties; executing the graph stays the caller's job. | user |
| AOT | All packages are Native AOT and trim compatible, checked by a CI gate from M1 phase 1 rather than discovered at M4. | my call |
| Data access | Because of the AOT gate, the store uses Npgsql directly, not EF Core, whose runtime is not AOT-safe. Migrations are embedded SQL scripts with an applier the host can call, or the host runs the scripts itself in a shared database. This replaces the Reasonlets design's "match how Daedalus migrates": Daedalus uses EF Core migrations. | my call |
| Daedalus | Daedalus may adopt the M1 preview through the store and selector directly, as the Reasonlets design planned, and move to the provider after M3. Each Daedalus change is a PR in that repo. Daedalus counts as one of the integrations that push on the API before the M4 freeze. | my call |

## Top-Level Success Criteria

- `Mnemosyne.NET` 1.0.0 is on NuGet, released by release-please, with the
  public API reviewed and every package Native AOT and trim compatible
  (because Daedalus is going Native AOT; **my call**).
- A new user can go from the docsite's getting-started page to a working
  store-and-recall sample against PostgreSQL + pgvector without reading source.
- An `IChatClient` app gains recall and capture with `UseReasonlets()` and no
  Mnemosyne-specific code beyond configuration; an Agent Framework agent gains
  the same through an `AIContextProvider`.
- Benchmarks for ranking and search run in CI and a regression is visible
  there, not only on a developer machine.
- The docsite explains the full model, variants included, with the post-1.0
  parts marked as such.

## Milestones

Every milestone ends with a docs-and-samples phase covering what that
milestone shipped (**user**: "grow docs and samples with each milestone").
The docsite exists from milestone 1 on.

### Milestone 1: Store and reuse

**Goal:** Store successful procedures and find the best one for a new question (handover phases 1+2), shipped as a NuGet preview with a live docsite.

**Phases:**

1. Repo scaffold, CI with AOT gate, release pipeline — `Surface: Infra`
2. Abstractions: records, scope model, store/selector interfaces — `Surface: Backend`
3. Graph JSON schema v1 — `Surface: Backend`
4. PostgreSQL + pgvector store and migrations — `Surface: Data`
5. Selector: scope and intent filter, similarity search, ranking — `Surface: Backend`
6. Docsite skeleton, getting-started docs and first sample — `Surface: Docs`

### Milestone 2: Evaluation

**Goal:** Answer "did it work?" without the caller hand-filling feedback, so the 30% success-rate term in ranking is real (handover phase 3).

**Phases:**

1. Evaluator contract and explicit feedback API — `Surface: Backend`
2. LLM-as-judge default evaluator — `Surface: Backend`
3. Minos.NET evaluator adapter — `Surface: Backend`
4. Metrics from executions feeding ranking — `Surface: Data`
5. Evaluation docs and sample — `Surface: Docs`

### Milestone 3: Off-the-shelf integrations

**Goal:** Any `IChatClient` app or Agent Framework agent gets recall, capture and learning without writing Mnemosyne-specific code.

**Phases:**

1. Capture: tool-call trace to graph JSON, new Reasonlet or new version — `Surface: Backend`
2. Redaction hook before capture — `Surface: Backend`
3. `UseReasonlets()` `IChatClient` middleware — `Surface: Backend`
4. `find_procedure` recall tool — `Surface: Backend`
5. Agent Framework `AIContextProvider` — `Surface: Backend`
6. Integration docs and samples — `Surface: Docs`

### Milestone 4: 1.0

**Goal:** Ship 1.0.0: stable public API, versioning ready for variants, benchmarks in CI, complete docs.

**Phases:**

1. Versioning and lineage API settled for later variant selection — `Surface: Backend`
2. BenchmarkDotNet suite running in CI — `Surface: Infra`
3. Public API review and graph schema compatibility policy — `Surface: Refactor`
4. Docs completeness: concepts, variants vision, Daedalus/Thalos guide — `Surface: Docs`
5. 1.0.0 release — `Surface: Infra`

### Milestone 5: Variants

**Goal:** Choose between sibling versions of a procedure, post-1.0 (handover phase 4).

**Phases:**

1. Variant selection strategy behind an interface — `Surface: Backend`
2. Exploration versus exploitation of new versions — `Surface: Backend`
3. Variant docs and sample — `Surface: Docs`

## Dependencies and Ordering Rationale

- M1 → M2: evaluation scores executions that M1's store records, and feeds the
  success-rate term in M1's ranking.
- M2 → M3: the middleware's learn step needs a judge; without M2 the
  integrations could recall but never tell a good run from a bad one.
- M3 → M4: the API is not stable until the integrations, Daedalus included,
  have pushed on it; freezing it, and benchmarking it, before M3 would mean
  doing both twice.
- M4 → M5: variants are post-1.0 by decision; M4 phase 1 makes sure they fit
  without a breaking change.
- External: the Thalos `IAgentContextProviderSource` adapter and the Daedalus
  wiring are PRs in those repos, after the M3 provider is released, not phases
  here (**my call**, matching how `Thalos.NET.Memory.RagNet` lives in Thalos).
  Minos.NET's API decides the shape of M2 phase 3.

## Risk Register

| Risk | Impact | Mitigation |
|---|---|---|
| An `IChatClient` only sees one call, so multi-turn procedures are invisible to the middleware | Middleware captures only single-turn procedures | Document the limit; the `AIContextProvider` sees the agent thread and covers multi-turn |
| Middleware adds an embedding and a DB query to every call, and a judge call after | Latency and cost on every chat call | Recall only on a new question; capture and judging run in the background; benchmark it in M4 |
| LLM-as-judge is noisy | Bad procedures rank high, good ones sink | Explicit feedback overrides the judge; success rate needs a minimum run count before it counts fully |
| Agent Framework is pre-stable and moves fast (Thalos is on 1.24) | Provider breaks on MAF upgrades | Keep the provider a thin adapter in its own package; Renovate keeps it current |
| Graph JSON schema churn | Stored procedures unreadable after a change | Versioned schema owned by Mnemosyne; compatibility policy in M4 |
| Captured traces leak user data across callers | Privacy incident | Scope on every query; redaction hook before capture; docs on what gets stored |

## Open Questions

None blocking the roadmap. Per-milestone detail (package names for the
integrations, judge prompt, benchmark corpus) is settled when each milestone
starts.
