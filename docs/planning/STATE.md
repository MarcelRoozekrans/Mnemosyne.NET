# Session State — 2026-11-04

**Date:** 2026-11-04

## Current Position
- **Milestone:** 3, Off-the-shelf integrations. Active since 2026-10-30. Design: `docs/superpowers/specs/2026-10-30-milestone-3-design.md`.
- **Phase:** 3.5, UseReasonlets middleware and the find_procedure tool. **Complete** on branch `feat/phase-3.5-reasonlets-find-procedure`, cut from `main` at 87e89f2. The PR against main is open and waits for Marcel to merge it.
  - All 9 plan tasks are done, each with a task review. A whole-branch review, one fix wave and a scoped re-review followed, and the pre-push review passed.
  - Unit 1444, integration 301, and the AOT smoke publishes without warnings and exits 0.
- **Previous phase:** 3.4, the AI.Sentinel adapter, merged as PR #30 on 2026-10-31 and released in 0.5.0 (#31).

## Decisions taken during 3.5 execution
- **Unanswered tool calls are not captured.** A response that ends on a `FunctionCallContent` with no result, or on an `InputRequestContent` approval request, is skipped with skip reason `pending_tool_calls`.
  - This holds in every pipeline order and for host-driven tool loops, so each run is captured once with its full shape.
  - The spec's claim that the inside order skips with `NoToolCalls` was wrong, and the spec now carries an amendment dated 2026-11-04.
  - Whether a call is pending is decided at hand-off, because `FunctionInvokingChatClient` sets `InformationalOnly` after the middleware returns.
- **Lazy resolution.** `UseReasonlets` resolves the memory and the capture queue on the first call, which breaks the DI cycle that hung startup when the chat evaluator judges with the same client.
  - The chat evaluator marks its judge calls with an internal key, and the middleware passes those calls through untouched.
  - A memory or queue that fails to resolve is counted, and the call goes on.
- **Automatic captures are named after their tool shape.** No question text reaches another user's injected message.
- **The capture queue is also `IDisposable`.** A synchronous `ServiceProvider.Dispose` drains it instead of throwing.
- **`no_scope` is counted** whenever Recall or Capture is on.
- **Follow-ups that need a decision:**
  - #45: a parentless capture of a stored shape creates a duplicate Reasonlet;
  - #46: judge calls from other libraries go through `UseReasonlets`;
  - #47: extracted intent can carry question text into `find_procedure` results.

## Decisions taken during 3.4 execution
- **ZeroAlloc analyzer switch.** `ZeroAllocAuthorizationDisableBundledAnalyzer=true` is set in the Sentinel csproj.
  - The bundled ZeroAlloc.Authorization generator reaches the project transitively, even though AI.Sentinel excludes Analyzers.
  - Without the switch, it would put a public `GeneratedAuthorizationRegistration` into the package's API, where it would collide with the host's own registration.
  - Tracked upstream as ZeroAlloc-Net/ZeroAlloc.Authorization#181. Remove the switch only when the fix stops the type from being emitted into libraries that have no policies.
- **Named pipelines need the default pipeline.** AI.Sentinel 4.1.1 requires the host to register the default pipeline as well as any named one. The README says so, and the test registers both.
- **A throwing detector does not fail closed.** AI.Sentinel 4.1.1 skips a detector that throws and counts it as clean, so the guard allows the capture.
  - This is documented in the README and the XML remarks, and pinned by `AThrowingDetectorIsSkippedAndTheCaptureIsAllowed`.
  - Upstream fix: MarcelRoozekrans/AI.Sentinel#550. Once it lands, map an incomplete scan to `Unavailable` and flip that test.
- **Medium-severity PII.** AI.Sentinel rates email, phone and date of birth as Medium. With the default threshold of High, they are stored. This is documented, and tests cover both thresholds.

## Open Decisions
- Rulings from phase 3.2 that are still open:
  - a remember call that is cancelled before it starts emits `mnemosyne.guard` with outcome `exception`;
  - `Abandon` takes `Task?`.

## Resolved Decisions
- **AI.Sentinel#550 timing:** Marcel chose to ship 3.4 with the documented gap. It shipped in 0.5.0. #550 was still open on 2026-11-03 (AI.Sentinel 4.2.0 does not fix it); the follow-up is still to map an incomplete scan to `Unavailable` once #550 lands.

## Blockers
- None.

## Notes
- **Thalos:** Thalos.NET.Sentinel still pins AI.Sentinel 2.3.1. Its move to 4.x is MarcelRoozekrans/Thalos.NET#457, and it is needed before Daedalus wires both adapters.
- **Release:** 0.5.0 is released (349fd1a on main) and includes phase 3.4.
- **Git trap:** `push.default` is `upstream`, so push with an explicit `branch:branch` refspec.
- **Tests:** the repo refuses `dotnet test --filter`. Build first, then run the test exe with `-class <FQN>`.

## Recommended Next Step
1. Marcel merges the phase 3.5 PR. After the merge, check that its `feat:` and `fix:` commits appear in the release-please PR.
2. Start phase 3.6, the Agent Framework AIContextProvider: brainstorm its design. It builds on the same recall, capture queue and processor. Decide #45-#47 first or alongside it, since 3.6 inherits all three.
