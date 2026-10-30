# Session State — 2026-10-30

**Date:** 2026-10-30

## Current Position
- **Milestone:** 3 — Off-the-shelf integrations, active since 2026-10-30. Design: `docs/superpowers/specs/2026-10-30-milestone-3-design.md`.
- **Phase:** 3.2 — Redaction hook, active on branch `feat/phase-3.2-redaction-hook`, cut from `main` after PR #25 (phase 3.1) merged.
- **Last completed task:** the phase 3.2 design was approved, `docs/superpowers/specs/2026-10-30-phase-3.2-redaction-hook-design.md`, and so was its plan, `docs/superpowers/plans/2026-10-30-phase-3.2-redaction-hook.md`. No plan task has started.
- **Next task:** execute the plan from Task 1, subagent-driven.

## Open Decisions
- None. Marcel chose all of these in the brainstorm, and the design records them:
  - AI.Sentinel does the detecting; core only asks.
  - The guard allows or blocks, and never rewrites.
  - It runs first in `RememberAsync`, before the extractor, the embedder and the judge.
  - A block answers the new `MnemosyneErrorCode.Blocked = 5`.
  - A guard that is down fails closed, as `Unavailable`.
  - Only captures are guarded, not `RecordExecutionAsync`.
  - It is built into the facade, not added as a decorator.
- Execution method: subagent-driven, with a fresh implementer and reviewer per task. Task 4 is marked for Opus.
- The guard is the last optional constructor parameter, after `timeProvider`, so existing callers keep compiling.

## Blockers
- None.

## Recommended Next Step
Run `start-next-phase`. Phase 3.2 is active, and its plan exists with nothing checked off, so it routes to `list-phase-assumptions` and then execution. Marcel already approved the plan and chose subagent-driven development, so start Task 1 directly with `superpowers:subagent-driven-development`. Focused test runs use the built xunit exe with `-class <FQN>`; the repo refuses `dotnet test --filter`.
