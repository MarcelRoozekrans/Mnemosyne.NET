# Session State — 2026-10-31

**Date:** 2026-10-31

## Current Position
- **Milestone:** 3 — Off-the-shelf integrations. It has been active since 2026-10-30, and its design is `docs/superpowers/specs/2026-10-30-milestone-3-design.md`.
- **Phase:** 3.4 — AI.Sentinel adapter, active on branch `feat/phase-3.4-ai-sentinel-adapter`, which is cut from `main` at fd2eed8 and pushed.
- **Design:** `docs/superpowers/specs/2026-10-30-phase-3.4-ai-sentinel-adapter-design.md`. Marcel approved it, and it passed a builder check.
- **Plan:** `docs/superpowers/plans/2026-10-30-phase-3.4-ai-sentinel-adapter.md`. It has six tasks, none started. Marcel approved the plan and chose **subagent-driven** execution.
- **Last completed task:** the design, the plan, and marking 3.4 active in ROADMAP.md and MILESTONE.md.
- **Next task:** Task 1 of the plan, the dependency bumps.

## Decisions from the 3.4 brainstorm
- **Version:** AI.Sentinel 4.1.1, not 2.x (Marcel). It forces `Microsoft.Extensions.AI.Abstractions` 10.11.0 and DI 10.0.14.
- **Threshold:** a Mnemosyne-side `BlockAtSeverity`, default High (Marcel). It is stricter than Sentinel's chat default, which quarantines only Critical.
- **What is scanned:** the question, the intent and the name only (Marcel). The answer, the graph and the provenance tags are never sent. Leaving out the graph and the tags was my call.
- **Pipeline:** the host's default `IDetectionPipeline`, or a keyed one through `PipelineName` (Marcel). Mnemosyne never configures Sentinel.
- **Thalos:** Thalos.NET.Sentinel still pins AI.Sentinel 2.3.1. Its move to 4.x is tracked as MarcelRoozekrans/Thalos.NET#457, and it is needed before Daedalus wires both adapters.

## Open Decisions
- None blocking.
- Earlier rulings still open from phase 3.2:
  - a pre-cancelled remember emits `mnemosyne.guard` with outcome `exception`;
  - `Abandon` takes `Task?`.

## Blockers
- None.
- **Watch:** plan Task 2 probes AI.Sentinel under Native AOT. If AI.Sentinel's own code fails `-warnaserror`, stop and report to Marcel. The fix belongs upstream in AI.Sentinel; do not suppress the warnings.

## Notes
- **Release:** release-please PR #29 (0.4.0) lists phase 3.3 under BREAKING CHANGES.
- **Git trap:** `push.default` is `upstream`. Push with an explicit `branch:branch` refspec.
- **Tests:** the repo refuses `dotnet test --filter`. Build first, then run the test exe with `-class <FQN>`.

## Recommended Next Step
Check out `feat/phase-3.4-ai-sentinel-adapter` and run the plan subagent-driven, starting at Task 1. After the last task, run the whole-branch review and pre-push review, mark 3.4 complete, and open the PR. Do not merge it.
