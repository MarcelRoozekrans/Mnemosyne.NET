# Session State — 2026-11-06

**Date:** 2026-11-06

## Current Position
- **Milestone:** 4, 1.0, active since 2026-11-06. Design: `docs/superpowers/specs/2026-11-06-milestone-4-design.md`, approved.
- **Phases 4.1 to 4.6:** complete, on `main`, closed in ROADMAP.md and MILESTONE.md.
- **Phase 4.7 — Subject export (#69):** `pending`, groomed. Design `docs/superpowers/specs/2026-11-06-phase-4.7-subject-export-design.md`, approved 2026-11-06. Plan `docs/superpowers/plans/2026-11-06-phase-4.7-subject-export.md`, five tasks, reviewed. Marcel chose subagent-driven execution. Nothing is built yet.
- **Phase 4.8 — 1.0.0 release:** was 4.7, renumbered with its plan and spec (`phase-4.8-…` files). Unchanged otherwise.
- **Released:** 0.8.0 is on nuget.org. Release PR #77 (0.8.1) lists the #58 fix; Marcel decides whether to ship it or let 1.0.0 carry it.
- **Branch:** the docs for 4.7 sit on `chore/state-subject-export-handoff`. They must reach `main` before the feature branch starts, so open a docs PR for it first (Marcel merges). Then start `feat/phase-4.7-subject-export` from the latest `main`. Push with explicit `branch:branch` refspecs, because `push.default` is `upstream`.

## Decisions
- **Export set = erase set (Marcel, 2026-11-06).** The subject's Reasonlets in any status, all runs on them, and the subject's own runs on other owners' Reasonlets, which carry only the `ReasonletId`.
- **Two paged calls (Marcel, option A):** `ListSubjectReasonletsAsync` and `ListSubjectRunsAsync` on the store port and the facade, shaped like `ListPendingAsync`.
- **My calls, in the design:** no embeddings; reads bypass the capture guard and the review gate; no export file format; record names `ReasonletRecord` and `ReasonletExecutionRecord`; the run record carries the evaluator's verdict and the human feedback.
- **Phase 4.8 (1.0.0 release):**
  - the `Release-As: 1.0.0` footer goes in the squash message of the final phase PR, proven first by a release-please dry run, and the squash-merge message setting is checked;
  - the release PR moves the PublicAPI Unshipped lines to Shipped;
  - stage B, after 1.0.0 is on nuget.org, adds `EnablePackageValidation` with baseline 1.0.0 in its own PR;
  - the phase completes after stage B;
  - Marcel merges the phase PRs and the release PR, and nothing is tagged by hand.

## Open Decisions
- Rulings from phase 3.2 that are still open:
  - a remember call cancelled before it starts emits `mnemosyne.guard` with outcome `exception`;
  - `Abandon` takes `Task?`.
- Two more API-hiding proposals are in the 4.4 review notes, waiting on Marcel's call. Decide them with 4.7, before the freeze becomes 1.0.

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
1. Run `resume-work`, then `start-next-phase`. Phase 4.7 is pending with a design and a plan, so it sets the phase `active`, runs `list-phase-assumptions`, and on Marcel's confirmation executes the plan with `superpowers:subagent-driven-development`.
2. After the last task, tick the plan, run `complete-phase` for 4.7, then continue with 4.8.
