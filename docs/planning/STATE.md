# Session State — 2026-11-06

**Date:** 2026-11-06

## Current Position
- **Milestone:** 4, 1.0, active since 2026-11-06. Design: `docs/superpowers/specs/2026-11-06-milestone-4-design.md`, approved. Milestone 3 is archived in `docs/planning/milestones/milestone-3.md`.
- **Branch:** `chore/start-milestone-4`, PR #62 open, Marcel merges. Push with `git push origin chore/start-milestone-4:chore/start-milestone-4`, because `push.default` is `upstream`.
- **Phases:** 4.1 Test and sample hygiene, 4.2 Versioning and lineage API, 4.3 Benchmarks in CI, 4.4 Public API review and freeze, 4.5 Docs completeness, 4.6 Data protection guide, 4.7 1.0.0 release. All `pending`.
- **Groomed:** phase 4.1 only, design approved and plan written. Phases 4.2 to 4.7 have no design and no plan.

## Grooming
Grooming: milestone 4, next phase 4.2

## Decisions taken in the milestone 4 brainstorm
- **Follow-ups:** #58 goes into phase 4.4, because it changes shipped behaviour and must settle before the freeze. #55 and #57 are phase 4.1, first. #55 and #57 close in 4.1, #58 in 4.4.
- **API lock:** PublicApiAnalyzers with `PublicAPI.Shipped.txt` baselines per package, plus `EnablePackageValidation` against the last release, both in CI. The graph schema gets a documented v1 compatibility policy.
- **Order:** hygiene, versioning, benchmarks, API freeze, docs, data protection guide, release. Versioning changes the API, so the freeze comes after it.
- **Variants** stay in milestone 5. Milestone 4 only makes the lineage and versioning API fit them.
- **Build command:** `dotnet build Mnemosyne.NET.slnx -c Release`. **Test command:** `dotnet test tests/Mnemosyne.NET.Tests -c Release`, the fast tier without Docker. Both are in CONVENTIONS.md. Phases that touch PostgreSQL add the Docker project to their own plan steps.
- **Phase 4.1:** an unreachable or failing database ends as `FAILED:` with exit 1 in all four samples, and a capture timeout names the run or act in all four.

## Open Decisions
- Phase 4.1 is `Surface: Refactor` and its pre-plan hook, `refactor-analysis`, was not run before the plan was written. The plan rests on the design's file list and a read of the code. Run `refactor-analysis` on 4.1 and adjust the plan if it finds more files, or tell Marcel it was judged unnecessary for a mechanical move. Phase 4.4 is also Refactor, so its pre-plan hook must run there.
- Rulings from phase 3.2 that are still open:
  - a remember call cancelled before it starts emits `mnemosyne.guard` with outcome `exception`;
  - `Abandon` takes `Task?`.

## Blockers
- None.

## Notes
- **groom-milestone is mid-run.** The next session re-enters it at step 3: groom 4.2 to 4.7 in roadmap order, one design and one plan per phase, one commit per phase, no execution. Brainstorm each phase interactively: lead with a concrete proposal and use AskUserQuestion with labelled options and one marked Recommended. The scheduled `**Builds on:**` lines matter here: 4.4 builds on 4.2's API, so name 4.2's interfaces from its plan, and add the guard check to 4.2's plan.
- **Inputs for 4.4:** #58 in full (bump AI.Sentinel past 4.2.0, an incomplete scan maps to `Unavailable`, update the test `AThrowingDetectorIsSkippedAndTheCaptureIsAllowed`, fix the stale 4.1.1 references, update the two "known gap" paragraphs). Repo has no PublicApiAnalyzers, package validation or BenchmarkDotNet yet.
- **Release:** `release-please-config.json` has `bump-minor-pre-major`. Phase 4.7 must check how a 1.0.0 bump is triggered, for example a `Release-As` footer, and never tags by hand.
- **Git trap:** `push.default` is `upstream`. Use explicit `branch:branch` refspecs.
- **Tests:** the repo refuses `dotnet test --filter`. Build first, then run the test exe with `-class <FQN>`.
- **Docker:** the samples and the PostgreSQL tests need Docker with pgvector.
- **Thalos:** the Thalos adapter and Daedalus wiring are PRs in those repos. Thalos.NET.Sentinel still pins AI.Sentinel 2.3.1 (MarcelRoozekrans/Thalos.NET#457).
- **Commits:** no Claude signature or session link, no nested parentheses in commit bodies, conventions in `docs/planning/CONVENTIONS.md`.

## Recommended Next Step
1. Run `resume-work`, which routes to `groom-milestone` because of the Grooming section, at step 3 for phase 4.2.
2. Groom 4.2 to 4.7. Then clear the Grooming section, commit and push, and tell Marcel milestone 4 is groomed.
3. Marcel merges PR #62 whenever ready; grooming commits ride on the same branch.
