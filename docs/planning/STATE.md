# Session State — 2026-11-06

**Date:** 2026-11-06

## Current Position
- **Milestone:** 3, Off-the-shelf integrations, complete on 2026-11-06. Audit: `docs/superpowers/reviews/2026-11-06-milestone-3-audit.md`, PASS. No tag: release-please owns releases.
- **Phase:** 3.7, integration docs and samples, merged as PR #59 and marked complete. Pre-push review: `docs/superpowers/reviews/pre-push-review-2026-11-05-2255.md`.
- **Next:** milestone 4 (1.0) is pending. Run `new-milestone` once the close PR for milestone 3 is merged.
- The 3.7 README and docs changes ship with the next release: PR #59 was titled `docs:`, so release-please opened no release PR for it.

## Delivered in 3.7
- Two offline, self-checking, CI-gated samples: `samples/Mnemosyne.Samples.ChatPipeline` (port 5434) and `samples/Mnemosyne.Samples.AgentFramework` (port 5435). Shared stand-ins in `samples/Shared`: `ScriptedChatClient` and `CaptureRunWatcher`, both unit-tested.
- Three guides in `docs/guides`: `chat-pipeline.md` (7), `capture-and-review.md` (8), `agent-framework.md` (9), wired into `getting-started.md`, `index.md`, `samples.md` and the package READMEs.
- No package code changed.

## Decisions taken during 3.7
- **Scripted fake chat client** for the samples, not a real model.
- **Capture watcher, not a store poll.** The samples learn that a capture finished from the `mnemosyne.capture.runs` counter, because a blocked or execution-only capture leaves nothing in the store to poll. The spec is amended.
- **A guide quotes only what a sample asserts.** Anything observed by hand is marked as such.
- **AI.Sentinel's PII detector does not check Luhn.** It blocks any 16-digit card-like number; 12, 15 and 17 digits did not block. The sample uses a Luhn-valid number and the docs say the rest was observed by hand.
- **Named-only Sentinel registration fails on 4.2.0.** Registering only a named pipeline throws on resolving the guard; the default `AddAISentinel()` must be registered first. The README says so.
- **The execution_only outcome does not prove the parent.** A parentless capture of a graph the scope already holds is also an execution. The guides say so.

## Open Decisions
- Rulings from phase 3.2 that are still open:
  - a remember call cancelled before it starts emits `mnemosyne.guard` with outcome `exception`;
  - `Abandon` takes `Task?`.

## Blockers
- None.

## Notes
- **Issues filed from 3.7:** #57 (share the self-check runner across the four samples) and #58 (adopt the AI.Sentinel fix for an incomplete scan and fail closed, plus the stale 4.1.1 references). #55 is the PostgreSQL test helper duplication from 3.6.
- **AI.Sentinel#550** is closed upstream (2026-11-04, completed). The repo pins 4.2.0 without the fix; #58 covers the bump.
- **Thalos:** the Thalos `IAgentContextProviderSource` adapter and the Daedalus wiring are PRs in those repos now that 3.6 is released (0.7.0). Thalos.NET.Sentinel still pins AI.Sentinel 2.3.1 (MarcelRoozekrans/Thalos.NET#457).
- **Git trap:** `push.default` is `upstream` and this branch tracks `origin/main`, so push with an explicit `branch:branch` refspec.
- **Tests:** the repo refuses `dotnet test --filter`. Build first, then run the test exe with `-class <FQN>`.
- **Docker:** the samples need Docker and PostgreSQL with pgvector.

## Recommended Next Step
1. Marcel merges the PR that closes milestone 3.
2. Run `new-milestone` for milestone 4, 1.0.
