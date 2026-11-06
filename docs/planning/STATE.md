# Session State — 2026-11-06

**Date:** 2026-11-06

## Current Position
- **Milestone:** 4, 1.0, active since 2026-11-06. Design: `docs/superpowers/specs/2026-11-06-milestone-4-design.md`, approved.
- **Built and on `main`:** phases 4.1 to 4.6, from the unattended run. #63 landed 4.1. #71 re-landed 4.2 to 4.6 with one commit per phase, because the stacked PRs had merged into their base branches. The plans are ticked, but ROADMAP.md and MILESTONE.md still show them as `pending`: `start-next-phase` step 1 closes them.
- **Released:** 0.8.0 (#72) is on nuget.org for all five packages. It does not include the #58 fix.
- **Open:** PR #74 on `fix/phase-4.4-sentinel-fail-closed` closes #58 (Phase 4.4 Tasks 1 and 2, parked in the run until AI.Sentinel 5.x reached NuGet). It bumps AI.Sentinel to 5.1.1 and fails closed when a scan is incomplete. All checks are green, it is behind main but merges cleanly, and Marcel merges it. Renovate #75 makes the same AI.Sentinel bump; close it after #74 merges.
- **Branch:** start each phase from the latest `main`, one feature branch and PR per phase. Push with explicit `branch:branch` refspecs, because `push.default` is `upstream`.

## Decisions
- **#69 lands before 1.0 (Marcel, 2026-11-06, option A).** #69 is reading or exporting everything a subject owns, needed for GDPR Art. 15 and Art. 20. It adds members to `IReasonletStore`, and after 1.0.0 adding a member to that interface would be a breaking change. So it gets its own phase before the release, and the store port gets the read path now. The other options were rejected: B, a separate opt-in interface after 1.0, splits the port; C, waiting for 2.0, keeps the data protection guide's raw SQL stopgap until then.
- **Open questions for the #69 brainstorm,** from the issue:
  - what an export contains: the graph and metrics yes, embeddings probably not, since they are not personal text;
  - whether the read goes through the capture guard or the review gate;
  - the return shape, for example Reasonlets, versions and executions tied to a `ReasonletScope` and subject;
  - the facade call on `IReasonletMemory` next to the existing erase;
  - the PostgreSQL implementation against the `subject_id` columns;
  - the PublicAPI baseline lines, which go into `PublicAPI.Unshipped.txt`.
  - `docs/guides/data-protection.md` then replaces its SQL stopgap with the new call.
- **Phase 4.7 (1.0.0 release), unchanged and renumbered to 4.8 once the new phase is inserted:**
  - the `Release-As: 1.0.0` footer goes in the squash message of the final phase PR, proven first by a release-please dry run, and the squash-merge message setting is checked;
  - the release PR moves the PublicAPI Unshipped lines to Shipped;
  - stage B, after 1.0.0 is on nuget.org, adds `EnablePackageValidation` with baseline 1.0.0 in its own PR;
  - the phase completes after stage B;
  - Marcel merges the phase PRs and the release PR, and nothing is tagged by hand.
  - Its plan is `docs/superpowers/plans/2026-11-06-phase-4.7-1-0-release.md`. Rename references to 4.8 when the phase is inserted.

## Open Decisions
- Rulings from phase 3.2 that are still open:
  - a remember call cancelled before it starts emits `mnemosyne.guard` with outcome `exception`;
  - `Abandon` takes `Task?`.
- Two more API-hiding proposals are in the 4.4 review notes, waiting on Marcel's call. Decide them with the #69 phase, before the freeze becomes 1.0.

## Blockers
- #74 must merge before the 1.0.0 release, because the milestone's DoD needs #58 closed.

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
1. Run `resume-work`, then `start-next-phase`. Step 1 closes phases 4.1 to 4.6 from their ticked plans.
2. Run `insert-phase` after 4.6: **Phase 4.7 — Subject export**, Surface `Backend`, HelpWanted `no`, goal "Hosts can read and export everything a subject owns through the store port and facade (#69)". The release becomes 4.8.
3. Run `start-next-phase` on 4.7. It has no design spec, so it routes to brainstorming with the open questions above, then writing-plans, then execution.
