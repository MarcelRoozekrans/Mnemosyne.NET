# Milestone 3: Off-the-shelf integrations

**Status:** active
**Started:** 2026-10-30

## Goal
Any `IChatClient` app or Microsoft Agent Framework agent gets recall, capture and learning without writing Mnemosyne-specific code.
- `UseReasonlets()` recalls the best procedure for a new question and offers it to the model as a suggestion, then captures the run's tool calls in the background, judged by the milestone 2 evaluator.
- A captured procedure is the tool shape only: the tools called, in order, with argument names but not values.
- A `find_procedure` tool lets the model look procedures up itself, and an `AIContextProvider` gives Agent Framework agents the same over a whole thread.
- Captured content passes a redaction hook, AI.Sentinel by default; a flagged capture is dropped, never stored. An opt-in review gate keeps new procedures out of recall until approved.

See the [milestone design](../superpowers/specs/2026-10-30-milestone-3-design.md) and the [roadmap](ROADMAP.md#milestone-3-off-the-shelf-integrations-status-active). Earlier milestones are archived in [milestones/milestone-1.md](milestones/milestone-1.md) and [milestones/milestone-2.md](milestones/milestone-2.md).

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
1. Phase 3.1 — Capture [complete]
2. Phase 3.2 — Redaction hook [complete]
3. Phase 3.3 — Human review gate [complete]
4. Phase 3.4 — AI.Sentinel adapter [complete]
5. Phase 3.5 — UseReasonlets middleware and find_procedure tool [pending]
6. Phase 3.6 — Agent Framework AIContextProvider [pending]
7. Phase 3.7 — Integration docs and samples [pending]

## Audit History
| Date | Verdict | Gaps |
|---|---|---|
