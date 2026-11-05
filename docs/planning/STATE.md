# Session State — 2026-11-05

**Date:** 2026-11-05

## Current Position
- **Milestone:** 3, Off-the-shelf integrations. Active since 2026-10-30. Design: `docs/superpowers/specs/2026-10-30-milestone-3-design.md`.
- **Phase:** 3.6, the Agent Framework AIContextProvider. **Active** on branch `feat/phase-3.6-agent-framework-context-provider`, cut from `main` at 0731a61 and pushed.
  - Design approved 2026-11-05: `docs/superpowers/specs/2026-11-05-phase-3.6-agent-context-provider-design.md`. It carries an amendment for the public recall step.
  - Plan approved 2026-11-05: `docs/superpowers/plans/2026-11-05-phase-3.6-agent-context-provider.md`, with 9 tasks.
  - Marcel chose **subagent-driven** execution. No plan task has started.
- **Previous phase:** 3.5 merged as PR #48 on 2026-11-04 and was released in 0.6.0 (#49). The changelog lists it.

## Decisions taken in the 3.6 brainstorm (all in the spec)
- **Unit of capture: one agent run is one procedure.** Thalos and Daedalus were checked: one turn is one run, with no tool approvals and no clarifying questions. Thalos also makes a fresh Agent Framework session every turn. Joining runs is a documented limit.
- **Default scope is per agent (`agent.Name`), with no default subject.** The host's `ScopeResolver` can override it, and the Thalos adapter will map the owner.
- **Approach A: a public core API.** It is made of `ReasonletRun`, `IReasonletCaptureSink`, a public `FindProcedureFunction` with `Lookups`, and the public recall step `ReasonletSuggestions`. `UseReasonlets` moves onto the same API, and the provider is thin.
- **#45:** the store deduplicates parentless captures by content hash, with an advisory lock and migration `0005` (index on scope and content hash).
- **#47:** `find_procedure` drops `intent` from its output.
- **#46:** stays internal and documented. Close it at PR time with a comment that links `docs/guides/chat-evaluator.md`.
- **Agent Framework facts, from a probe against 1.27:**
  - the run-end hook sees the whole tool loop for normal and streamed runs;
  - both hooks get the same session object;
  - an abandoned stream never reaches the run-end hook, and a failed run is skipped by the base class.
- **Versions:** the package floor is `Microsoft.Agents.AI.Abstractions` 1.24.0, held there in Renovate. It targets net10.0 only.

## Open Decisions
- Rulings from phase 3.2 that are still open:
  - a remember call cancelled before it starts emits `mnemosyne.guard` with outcome `exception`;
  - `Abandon` takes `Task?`.

## Blockers
- None.

## Notes
- **Thalos:** Thalos.NET.Sentinel still pins AI.Sentinel 2.3.1; its move to 4.x is MarcelRoozekrans/Thalos.NET#457. The Thalos `IAgentContextProviderSource` adapter and the Daedalus wiring are PRs in those repos after 3.6 is released.
- **AI.Sentinel#550:** still open. Once it lands, map an incomplete scan to `Unavailable`.
- **Git trap:** `push.default` is `upstream`, so push with an explicit `branch:branch` refspec.
- **Tests:** the repo refuses `dotnet test --filter`. Build first, then run the test exe with `-class <FQN>`.

## Recommended Next Step
1. On branch `feat/phase-3.6-agent-framework-context-provider`, run `superpowers:subagent-driven-development` on `docs/superpowers/plans/2026-11-05-phase-3.6-agent-context-provider.md`, starting at Task 1.
   - Sonnet implementers, except Tasks 4 and 6, which are marked opus.
   - Finish with an Opus whole-branch review, then `pre-push-review`.
2. Open the PR against main, with `Closes #45` and `Closes #47`. Comment on and close #46. Wait for every check, both codecov checks included. Marcel merges.
