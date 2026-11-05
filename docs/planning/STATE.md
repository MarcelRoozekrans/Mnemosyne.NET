# Session State — 2026-11-05

**Date:** 2026-11-05

## Current Position
- **Milestone:** 3, Off-the-shelf integrations. Active since 2026-10-30. Design: `docs/superpowers/specs/2026-10-30-milestone-3-design.md`.
- **Phase:** 3.6, the Agent Framework AIContextProvider. **Complete** on branch `feat/phase-3.6-agent-framework-context-provider`. PR #54 against main is open and waits for Marcel to merge it.
  - All 9 plan tasks are done, each with a task review and a fix round for its findings, Minors included. An Opus whole-branch review followed, then one fix wave with a scoped re-review. The pre-push review passed: `docs/superpowers/reviews/pre-push-review-2026-11-05-1123.md`.
  - main at 0.6.1 is merged into the branch. Unit 1565, PostgreSQL 313, the AOT smoke publishes without warnings and exits 0, the pack gives 6 nupkg, and the docsite builds.
  - #45 and #47 close with the PR. #46 is closed with a comment linking `docs/guides/chat-evaluator.md`. #55 is filed for the duplicated PostgreSQL test helpers.
- **Next phase:** 3.7, integration docs and samples. It is pending, with no design yet.
- **Previous phase:** 3.5 merged as PR #48 and was released in 0.6.0.

## Decisions taken during 3.6 execution
All of these are in the spec's 2026-11-05 amendments.
- **`ReasonletCaptureRun`** is the public capture record, because `ReasonletRun` is already the Abstractions execution record.
- **Public counting entry points:** `ReasonletSuggestions.CountFailed` and `ReasonletCaptures.CountFailed`. With them the provider counts on the core's instruments without `InternalsVisibleTo` from the core.
- **Agent tests** live in `tests/Mnemosyne.NET.Tests/AgentFramework`.
- **Testing through internal methods:**
  - The provider's hooks hand off to internal methods that the tests call directly.
  - This covers a null session without the experimental (MAAI001) `InvokingContext` constructor.
- **A run without stored state** is captured with no parent and no lookups. The end hook counts nothing for a missing scope.
- **`UseReasonlets` resolves `IReasonletCaptureSink`,** not the concrete queue.
- **`TryCapture` copies the message list** when the run is handed over.
- **Capture contents:** a capture holds only this run's request messages (the base filter keeps only the caller's own input) plus all response messages. Earlier history is not captured.

## Open Decisions
- Rulings from phase 3.2 that are still open:
  - a remember call cancelled before it starts emits `mnemosyne.guard` with outcome `exception`;
  - `Abandon` takes `Task?`.

## Blockers
- None.

## Notes
- **Thalos:** the Thalos `IAgentContextProviderSource` adapter and the Daedalus wiring are PRs in those repos after 3.6 is released. Thalos.NET.Sentinel still pins AI.Sentinel 2.3.1 (MarcelRoozekrans/Thalos.NET#457).
- **AI.Sentinel#550:** still open. Once it lands, map an incomplete scan to `Unavailable`.
- **Git trap:** `push.default` is `upstream`, so push with an explicit `branch:branch` refspec.
- **Tests:** the repo refuses `dotnet test --filter`. Build first, then run the test exe with `-class <FQN>`.

## Recommended Next Step
1. PR #54 is green on every check at ebc7847, both codecov checks included. Marcel merges.
2. After the merge, check that the PR's `feat:` and `fix:` entries appear in the release-please PR.
3. Start phase 3.7, integration docs and samples: run `start-next-phase`, which brainstorms its design.
