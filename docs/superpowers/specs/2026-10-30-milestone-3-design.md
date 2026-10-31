# Milestone 3 Design — Off-the-shelf integrations

**Date:** 2026-10-30
**Milestone:** 3
**Stage:** milestone (one milestone, multiple phases)

Builds on the [roadmap design](2026-10-21-roadmap-design.md), milestone 3 section.

## Goal

Any `IChatClient` app or Microsoft Agent Framework agent gets recall, capture and
learning without writing Mnemosyne-specific code. `builder.UseReasonlets()` recalls
the best procedure for a new question and offers it to the model, then captures the
run's tool calls in the background as a new Reasonlet, a new version, or an execution
of the recalled one, judged by the milestone 2 evaluator. A `find_procedure` tool lets
the model look procedures up itself, and an `AIContextProvider` gives Agent Framework
agents the same over a whole thread. Captured content passes a redaction hook first,
AI.Sentinel by default, and an opt-in review gate keeps new procedures out of recall
until a person approves them.

## Decisions

| Decision | Detail | By |
|---|---|---|
| Graph content | A captured graph holds the tool shape only: the tools called, in order, with argument names but not values. Reruns with other inputs are executions of the same version, so lineage does not fill with near-duplicates. The redaction hook still guards the question, the intent and anything else captured as text. | user |
| Capture trigger | The middleware captures every run that made at least one tool call, in the background. The evaluator's verdict becomes its success; failed procedures are stored but sink in ranking, and retention prunes them. A run without a recall is a new Reasonlet; a run that followed a recalled one carries it as `ParentId`, and `RememberAsync` already decides between execution and new version. | user |
| Recall injection | Above the similarity cut-off, the best match's steps go into one system message, marked as a suggestion. Only on a new user question, not on turns that carry tool results. The recalled Reasonlet is the run's `ParentId`. | user |
| Flagged captures | A capture the redaction hook, and so AI.Sentinel, flags is dropped, never written. An OpenTelemetry event records that it was blocked and which detector fired, without the content. This settles the roadmap's "quarantined, not stored". | user |
| Packages | `UseReasonlets()` and `find_procedure` live in the core `Mnemosyne.NET` package, which already references `Microsoft.Extensions.AI.Abstractions`. The Agent Framework provider is its own package, and so is `Mnemosyne.NET.Sentinel`; the core packages take no Agent Framework or Sentinel dependency. | user (core); roadmap (separate packages) |
| Sequencing | Foundations first: capture, redaction, review and Sentinel land before anything captures automatically. `find_procedure` merges into the middleware phase, since both are thin layers over `RecallAsync` in the same package. | user |
| Review gate | Opt-in and off by default, so off-the-shelf use stays one line. When on, a new Reasonlet or a new version is stored as `ReasonletStatus.PendingReview`, which already exists, and stays out of recall until approved; an execution of an approved version needs no review. | roadmap (opt-in); my call (what it covers) |

## Definition of Done

- [ ] All planned phases complete
- [ ] All tests passing, including integration tests against PostgreSQL + pgvector
- [ ] CI green, including the Native AOT gate for every new package
- [ ] A tool-calling chat transcript becomes a tool-shape graph, stored as a new Reasonlet, a new version or an execution of the recalled one
- [ ] Captured content passes a redaction hook; a flagged capture is dropped and leaves a telemetry event without the content
- [ ] With the review gate on, captured procedures stay out of recall until approved, and can be approved or rejected
- [ ] `Mnemosyne.NET.Sentinel` runs AI.Sentinel's detectors as the redaction hook
- [ ] `UseReasonlets()` recalls, injects the best match, and captures and judges in the background in an `IChatClient` pipeline; the `find_procedure` tool is shipped
- [ ] An Agent Framework `AIContextProvider` gives an agent the same recall and capture over its thread
- [ ] Docs and samples cover every integration

## Phases

1. **Phase 3.1: Capture** — `Surface: Backend`
   - **Goal:** Turn a chat transcript's tool calls into a tool-shape graph, and capture it as a new Reasonlet, a new version or an execution of the recalled one.
2. **Phase 3.2: Redaction hook** — `Surface: Backend`
   - **Goal:** A hook that runs on captured content before it is stored and can block a capture, with a telemetry event for each blocked one.
3. **Phase 3.3: Human review gate** — `Surface: Backend`
   - **Goal:** Opt-in pending-review state for captured procedures, kept out of recall, with approve and reject.
4. **Phase 3.4: AI.Sentinel adapter** — `Surface: Backend`
   - **Goal:** `Mnemosyne.NET.Sentinel`: AI.Sentinel's detectors as the default redaction hook.
5. **Phase 3.5: UseReasonlets middleware and find_procedure tool** — `Surface: Backend`
   - **Goal:** `IChatClient` middleware that recalls and injects before the call and captures in the background after it, plus an `AIFunction` the model calls to look up a procedure.
6. **Phase 3.6: Agent Framework AIContextProvider** — `Surface: Backend`
   - **Goal:** A thread-aware `AIContextProvider` in its own package, usable by Thalos.NET and Daedalus.NET.
7. **Phase 3.7: Integration docs and samples** — `Surface: Docs`
   - **Goal:** Docsite pages and samples for each integration.

## Dependencies on Prior Milestones

- Milestone 1: `RememberAsync` already chooses between new Reasonlet, new version and execution by comparing the graph with the parent's, and `ReasonletStatus.PendingReview` already exists. Phase 3.1 adds the transcript-to-graph step, not the storage.
- Milestone 2: the middleware's learn step needs the evaluator; a captured run's answer reaches it and is never stored.
- External: `Microsoft.Agents.AI` 1.24 and `AI.Sentinel` 4.x on NuGet. Thalos.NET.Sentinel's move to 4.x is tracked in MarcelRoozekrans/Thalos.NET#457. The Thalos `IAgentContextProviderSource` adapter and the Daedalus wiring are PRs in those repos after the provider is released, not phases here.

## External Constraints

- Every package stays Native AOT and trim compatible, checked by the CI gate. The Agent Framework and AI.Sentinel packages have to pass it too, or the adapter has to keep their non-AOT parts out of its path.
- The public API is not frozen until milestone 4; the integrations, Daedalus included, are meant to push on it first.

## Risk Areas

| Risk | Impact | Mitigation |
|---|---|---|
| `UseReasonlets()` sees the tool loop only when it sits outside `UseFunctionInvocation()` | Inside it, the middleware sees single model calls and captures nothing useful | Document the pipeline order, and detect a transcript without tool results and skip capture rather than storing an empty graph |
| An `IChatClient` sees one call, so multi-turn procedures are invisible | The middleware captures only what one call's tool loop did | Document the limit; the `AIContextProvider` sees the thread and covers multi-turn |
| Recall and background capture add an embedding, a query and a judge call | Latency and cost on chat calls | Recall only on a new user question; capture and judging run in the background; benchmark in milestone 4 |
| Background capture fails or outlives the request | Lost captures, or work after the host shuts down | Capture failures are logged and counted, never thrown into the chat call; shutdown behaviour is settled in phase 3.5 |
| An injected procedure misleads the model | Worse answers than without memory | Inject only above the cut-off, mark it as a suggestion, and let the evaluator's verdicts sink bad procedures |
| AI.Sentinel or Agent Framework is not AOT-clean | The AOT gate fails for the adapter package | Check both early in their phases; keep the adapters thin |

## Open Questions

None blocking the milestone. Per-phase detail is settled when each phase starts:
how the middleware gets the scope for a call, how steps render in the system message,
the approve and reject API, and how the provider maps an agent thread to a capture.
