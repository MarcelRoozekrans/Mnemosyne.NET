# Milestone 2: Evaluation

**Status:** complete
**Started:** 2026-10-23
**Completed:** 2026-10-30

## Goal
Answer "did it work?" without the caller filling in feedback by hand, so that the success-rate term in ranking reflects real outcomes (handover phase 3).
- A pluggable evaluator judges each recorded run, and a person's explicit feedback overrides it.
- Mnemosyne ships two judges: a Minos.NET judge, which is the default, and a judge that asks a chat model.
- Ranking's success rate comes from evaluated runs.

See the [roadmap](ROADMAP.md#milestone-2-evaluation-status-complete). Milestone 1 is archived in [milestones/milestone-1.md](milestones/milestone-1.md).

## Definition of Done
- [x] Runs are scored by a pluggable evaluator, with explicit feedback overriding it
- [x] A Minos.NET default evaluator and an LLM-as-judge evaluator are shipped
- [x] Success rate in ranking comes from evaluated executions
- [x] Docs and a sample cover evaluation

## Phases
1. Phase 2.1 — Evaluator contract and feedback API [complete]
2. Phase 2.2 — Minos.NET evaluator [complete]
3. Phase 2.3 — LLM-as-judge evaluator [complete]
4. Phase 2.4 — Metrics feeding ranking [complete]
5. Phase 2.5 — Evaluation docs and sample [complete]

## Audit History
| Date | Verdict | Gaps |
|---|---|---|
| 2026-10-30 | PASS | None; warning: no pre-push review reports, reviewed per task and per branch instead ([audit](../superpowers/reviews/2026-10-30-milestone-2-audit.md)) |
