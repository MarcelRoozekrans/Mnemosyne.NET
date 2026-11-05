# Session State — 2026-11-05

**Date:** 2026-11-05

## Current Position
- **Milestone:** 3, Off-the-shelf integrations. Active since 2026-10-30. Design: `docs/superpowers/specs/2026-10-30-milestone-3-design.md`.
- **Phase:** 3.7, integration docs and samples. **Active**, on branch `feat/phase-3.7-integration-docs-samples`, cut from `main` at 727b534. Design approved, plan written, execution not started.
  - Design: `docs/superpowers/specs/2026-11-05-phase-3.7-integration-docs-samples-design.md`. Plan: `docs/superpowers/plans/2026-11-05-phase-3.7-integration-docs-samples.md`, 9 tasks.
  - Marcel chose subagent-driven execution on 2026-11-05.
- **Previous phase:** 3.6 merged as PR #54 (727b534) and is in release-please PR #56 (0.7.0), which is open.

## Decisions taken during 3.7 brainstorming
- **Scripted fake chat client** for the new samples, not a real model: offline, self-checking, gated in CI like the first two samples.
- **Two samples:** a chat pipeline sample (UseReasonlets, review gate, real AI.Sentinel adapter blocking a card number) and an Agent Framework sample.
- **Three guides** in `docs/guides`: chat-pipeline (7), capture-and-review (8), agent-framework (9).
- **Capture watcher, not a store poll.** The samples learn that a background capture finished from the `mnemosyne.capture.runs` counter, because a blocked or execution-only capture leaves nothing in the store to poll. This replaces the bounded poll that the design doc still mentions in one sentence; amend the design if it matters.
- **No API change in this phase.** A needed change is filed as an issue.

## Open Decisions
- Rulings from phase 3.2 that are still open:
  - a remember call cancelled before it starts emits `mnemosyne.guard` with outcome `exception`;
  - `Abandon` takes `Task?`.
- Unverified: that AI.Sentinel's PII detector blocks a Luhn-valid card number offline. Task 3 step 1 of the plan confirms it before the sample is written.

## Blockers
- None.

## Notes
- **Thalos:** the Thalos `IAgentContextProviderSource` adapter and the Daedalus wiring are PRs in those repos after 3.6 is released. Thalos.NET.Sentinel still pins AI.Sentinel 2.3.1 (MarcelRoozekrans/Thalos.NET#457).
- **AI.Sentinel#550:** still open. Once it lands, map an incomplete scan to `Unavailable`.
- **Git trap:** `push.default` is `upstream` and this branch tracks `origin/main`, so push with an explicit `branch:branch` refspec.
- **Tests:** the repo refuses `dotnet test --filter`. Build first, then run the test exe with `-class <FQN>`.
- **Docker:** the samples need Docker and PostgreSQL with pgvector. `docker` is installed in the dev environment.

## Recommended Next Step
1. Resume with `resume-work`, then run `superpowers:subagent-driven-development` on the plan, starting at Task 1. Create the ledger first.
2. When all 9 tasks are done: whole-branch review, pre-push review, push with the explicit refspec, open the PR, wait for codecov, then `complete-phase`.
