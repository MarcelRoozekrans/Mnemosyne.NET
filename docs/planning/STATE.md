# Session State — 2026-11-07

**Date:** 2026-11-07

## Current Position
- **Milestone:** 4, 1.0, active since 2026-11-06. Design: `docs/superpowers/specs/2026-11-06-milestone-4-design.md`, approved.
- **Phases 4.1 to 4.7:** complete, on `main`. 4.7 (Subject export, #69) merged as PR #79 on 2026-11-07. #78 tracks the follow-up indexes for paging a heavy subject's runs, planned for 1.1.
- **Phase 4.8 — 1.0.0 release:** `pending`, groomed. Design `docs/superpowers/specs/2026-11-06-phase-4.8-1-0-release-design.md`, plan `docs/superpowers/plans/2026-11-06-phase-4.8-1-0-release.md`. Attended: Marcel merges the PRs.
- **Released:** 0.8.1 (#77) is merged and carries the #58 fix. The 4.7 export is not in a release yet.
- **Branch:** start 4.8 from the latest `main`. Push with explicit `branch:branch` refspecs, because `push.default` is `upstream`.

## Decisions
- **Naming to confirm before the freeze.** The final review of 4.7 recommends `ListSubjectExecutionsAsync` for `ListSubjectRunsAsync`, since the rest of the API says Execution. The names stayed as approved. A rename is mechanical now and a breaking change after 1.0.0; ask Marcel first thing in 4.8, before any Shipped move.
- **Export set = erase set (Marcel, 2026-11-06).** The subject's Reasonlets in any status, all runs on them, and the subject's own runs on other owners' Reasonlets, which carry only the `ReasonletId`.
- **Two paged calls (Marcel, option A):** `ListSubjectReasonletsAsync` and `ListSubjectRunsAsync` on the store port and the facade, shaped like `ListPendingAsync`.
- **My calls, in the design:** no embeddings; reads bypass the capture guard and the review gate; no export file format; record names `ReasonletRecord` and `ReasonletExecutionRecord`; the run record carries the evaluator's verdict and the human feedback.
- **Phase 4.8 (1.0.0 release), unchanged:**
  - the `Release-As: 1.0.0` footer goes in the squash message of the final phase PR, proven first by a release-please dry run, and the squash-merge message setting is checked;
  - the release PR moves the PublicAPI Unshipped lines to Shipped;
  - stage B, after 1.0.0 is on nuget.org, adds `EnablePackageValidation` with baseline 1.0.0 in its own PR;
  - the phase completes after stage B;
  - Marcel merges the phase PRs and the release PR, and nothing is tagged by hand.

## Open Decisions
- Rulings from phase 3.2 that are still open:
  - a remember call cancelled before it starts emits `mnemosyne.guard` with outcome `exception`;
  - `Abandon` takes `Task?`.
- Two more API-hiding proposals are in the 4.4 review notes, waiting on Marcel's call. Decide them at the start of 4.8, before the freeze becomes 1.0.

## Blockers
- None.

## Notes
- **Tests:** the repo refuses `dotnet test --filter`. Build first, then run the test exe with `-class <FQN>`.
- **Docker:** the samples and the PostgreSQL tests need Docker with pgvector.
- **gitleaks** scans each PR commit. A fake credential fixture needs an inline `// gitleaks:allow` on each new line that holds it.
- **commitlint** limits body and footer lines to 100 characters.
- **Codecov** aims for 100% patch coverage; record `with` copies need a test.
- **Squash merges** take the PR title. When a PR carries several conventional commits, put a `BEGIN_COMMIT_OVERRIDE` block in its body for release-please.
- **Commits:** no Claude signature or session link, no nested parentheses in commit bodies, conventions in `docs/planning/CONVENTIONS.md`.
- **Unattended env:** start the next session without `SUPERPOWERS_UNATTENDED`, or its AskUserQuestion calls are answered by a hook.

## Recommended Next Step
1. Run `resume-work`, then `start-next-phase`. Ask Marcel to confirm the `ListSubjectRunsAsync` name and the two API-hiding proposals first.
2. Phase 4.8 is groomed: set it active, run `list-phase-assumptions`, and execute its plan.
