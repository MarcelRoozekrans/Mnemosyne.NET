# Session State — 2026-11-07

**Date:** 2026-11-07

## Current Position
- **Milestone:** 5, Variants, complete on 2026-11-07 (audit PASS with one warning: no pre-push review report; `docs/superpowers/reviews/2026-11-07-milestone-5-audit.md`). No milestone is active.
- **Phases 5.1 to 5.3:** complete, built by an unattended run and merged as #93, #95 and #96.
- **Released:** 1.1.0 by release-please (#94), with `AddMnemosyneVariantSelection`, `VariantSelector`, `VariantSelectionOptions` and `VariantExploration`.
- **Branch:** this closure is on `chore/complete-milestone-5`, waiting for a merge. Push with explicit `branch:branch` refspecs, because `push.default` is `upstream`.

## Decisions
- **Variant selection (milestone 5):** opt-in, and it decorates the registered `IReasonletSelector`. Thompson sampling is a separate switch.
- **Beta prior:** the prior `SuccessRate` uses, 0.5 or the parent's rate when above 0.5, so the posterior mean equals the smoothed rate.
- **Seeded picks:** they hash the seed, the question and the lineage, and draw in id order inside a lineage.
- **Ranking options:** validated only under Thompson.
- **Inner selector's Top:** it receives `Top` equal to the candidate count, which can exceed 50. This is documented on `IReasonletSelector`.
- **Ties:** a fresh child ties its parent on score under the default ranking, and the lower id wins.

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
1. Merge `chore/complete-milestone-5`.
2. The roadmap has no milestone after 5. Either run `plan-roadmap` to extend it, or run `new-milestone` to brainstorm milestone 6 with Marcel. #78, the follow-up indexes for paging a heavy subject's runs, is still open for a 1.x release and is a candidate.
