# Session State — 2026-11-07

**Date:** 2026-11-07

## Current Position
- **Milestone:** 4, 1.0, active since 2026-11-06. Design: `docs/superpowers/specs/2026-11-06-milestone-4-design.md`, approved.
- **Phases 4.1 to 4.8:** complete, on `main`. 4.8 closed on 2026-11-07.
- **Released:** 1.0.0 is on nuget.org for all six packages, tag `v1.0.0`, cut by release-please. 0.9.0 went out the same day.
- **API settled before the freeze (Marcel, 2026-11-07):** `ListSubjectRunsAsync` is now `ListSubjectExecutionsAsync`; `ReasonletCaptures`, `ReasonletSuggestions`, `Check`, `EvaluationLimits` and `ReasonletGraphLimits` are internal (PR #83). The `Shipped` baselines hold the 1.0.0 surface (#87). Package validation against baseline 1.0.0 is on (#88).
- **Branch:** `main` is current. Push with explicit `branch:branch` refspecs, because `push.default` is `upstream`.

## Decisions
- **Export set = erase set (Marcel, 2026-11-06).** The subject's Reasonlets in any status, all runs on them, and the subject's own runs on other owners' Reasonlets, which carry only the `ReasonletId`.
- **Two paged calls (Marcel, option A):** `ListSubjectReasonletsAsync` and `ListSubjectExecutionsAsync` on the store port and the facade, shaped like `ListPendingAsync`.
- **My calls, in the design:** no embeddings; reads bypass the capture guard and the review gate; no export file format; record names `ReasonletRecord` and `ReasonletExecutionRecord`; the run record carries the evaluator's verdict and the human feedback.
- **Phase 4.8 (1.0.0 release), as done:**
  - the `Release-As: 1.0.0` footer goes in the squash message of the final phase PR, proven first by a release-please dry run, and the squash-merge message setting is checked;
  - the release PR moves the PublicAPI Unshipped lines to Shipped;
  - stage B, after 1.0.0 is on nuget.org, adds `EnablePackageValidation` with baseline 1.0.0 in its own PR;
  - the phase completes after stage B;
  - Marcel merges the phase PRs and the release PR, and nothing is tagged by hand.

## Open Decisions
- Rulings from phase 3.2 that are still open: a remember call cancelled before it starts emits `mnemosyne.guard` with outcome `exception`, and `Abandon` takes `Task?`.

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
- **Merging:** main needs one approving review, which an agent cannot give. Marcel merges, or approves `gh pr merge --admin`. A force-push needs his go-ahead too.
- **Squash message:** the repo squashes with the commit messages as the body, so a footer such as `Release-As:` lives in a single-commit PR.
- **Unattended env:** start the next session without `SUPERPOWERS_UNATTENDED`, or its AskUserQuestion calls are answered by a hook.

## Recommended Next Step
1. Run `audit-milestone` for milestone 4, then `complete-milestone`. It creates no tag, because release-please owns releases.
2. Then `new-milestone` for milestone 5, Variants. Note #78, the follow-up indexes for paging a heavy subject's runs, is planned for 1.1.
