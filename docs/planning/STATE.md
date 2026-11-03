# Session State — 2026-11-03

**Date:** 2026-11-03

## Current Position
- **Milestone:** 3, Off-the-shelf integrations. Active since 2026-10-30. Design: `docs/superpowers/specs/2026-10-30-milestone-3-design.md`.
- **Phase:** 3.5, UseReasonlets middleware and the find_procedure tool. It is **in design** on branch `feat/phase-3.5-reasonlets-find-procedure`, cut from `main` at 87e89f2.
- **Previous phase:** 3.4, the AI.Sentinel adapter, merged as PR #30 on 2026-10-31 and released in 0.5.0 (#31).

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
1. Brainstorm the phase 3.5 design (UseReasonlets `IChatClient` middleware plus the `find_procedure` `AIFunction`), then write its plan.
