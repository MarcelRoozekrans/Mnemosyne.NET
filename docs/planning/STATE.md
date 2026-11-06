# Session State — 2026-11-06

**Date:** 2026-11-06

## Current Position
- **Milestone:** 4, 1.0, active since 2026-11-06. Design: `docs/superpowers/specs/2026-11-06-milestone-4-design.md`, approved. Milestone 3 is archived in `docs/planning/milestones/milestone-3.md`.
- **Branch:** `chore/start-milestone-4`, PR #62 open, Marcel merges. Another worktree may hold the branch: if `git checkout` refuses, reset a local branch to `origin/chore/start-milestone-4` and push `HEAD:chore/start-milestone-4`. Push with `git push origin chore/start-milestone-4:chore/start-milestone-4`, because `push.default` is `upstream`.
- **Phases:** 4.1 Test and sample hygiene, 4.2 Versioning and lineage API, 4.3 Benchmarks in CI, 4.4 Public API review and freeze, 4.5 Docs completeness, 4.6 Data protection guide, 4.7 1.0.0 release. All `pending`.
- **Groomed:** all of milestone 4, phases 4.1 to 4.7, design approved and plan written for each. Nothing is built yet.

## Decisions taken in the milestone 4 brainstorm
- **Follow-ups:** #58 goes into phase 4.4, because it changes shipped behaviour and must settle before the freeze. #55 and #57 are phase 4.1, first. #55 and #57 close in 4.1, #58 in 4.4.
- **API lock:** PublicApiAnalyzers with `PublicAPI.Shipped.txt` baselines per package, plus `EnablePackageValidation` against the last release, both in CI. The graph schema gets a documented v1 compatibility policy.
- **Order:** hygiene, versioning, benchmarks, API freeze, docs, data protection guide, release. Versioning changes the API, so the freeze comes after it.
- **Variants** stay in milestone 5. Milestone 4 only makes the lineage and versioning API fit them.
- **Build command:** `dotnet build Mnemosyne.NET.slnx -c Release`. **Test command:** `dotnet test tests/Mnemosyne.NET.Tests -c Release`, the fast tier without Docker. Both are in CONVENTIONS.md. Phases that touch PostgreSQL add the Docker project to their own plan steps.
- **Phase 4.1:** an unreachable or failing database ends as `FAILED:` with exit 1 in all four samples, and a capture timeout names the run or act in all four.

## Decisions taken while grooming 4.2
- **Lineage id plus proof.** `LineageId`, the root's id, goes on `ReasonletCandidate` and `ReasonletMatch`, computed at search by one recursive query after the nearest-neighbour search. No schema change. A test-only `LineageVariantSelector` proves the selector seam. No `GetLineageAsync`.
- **Init-only records.** `ReasonletCandidate`, `ReasonletMatch` and `ReasonletEvidence` become init-only now, a breaking change. The other positional records (`ReasonletReceipt`, `ReasonletReview`, `ReasonletStoreEntry`, `ReasonletRunReceipt`, `ReasonletSuggestion`, `ReasonletAgentScope`, `ReasonletCallScope`, `ReasonletErasure`) are left to the 4.4 API review.
- **Version labels are opaque.** Documented, not changed.
- **For 4.4:** it builds on 4.2. Name 4.2's interfaces from its plan (`LineageId` on candidate and match, the init-only types) in 4.4's `**Builds on:**` line, and append a `guard 4.4:` check to 4.2's `## Checks`, for example a test that constructs a `ReasonletMatch` with `LineageId`.

## Decisions taken while grooming 4.3
- **Report-only benchmarks.** `benchmarks/Mnemosyne.NET.Benchmarks`, not packable, outside the AOT gate. Three classes: ranking, chat middleware overhead, PostgreSQL search with lineage depth 1 and 20. The search class runs only when `MNEMOSYNE_BENCH_PG` is set, and the CI job supplies a pgvector service container. The workflow never fails on a number.
- **4.3 builds on 4.2.** Its `**Builds on:**` line names `LineageId` and the init-only candidate and match. `guard 4.3:` is already in 4.2's `## Checks`. Phase 4.4 must keep the benchmark project compiling, since it is in the solution.

## Decisions taken while grooming 4.4
- **Records:** `ReasonletReceipt`, `ReasonletReview`, `ReasonletStoreEntry` and `ReasonletRunReceipt` become init-only. `ReasonletSuggestion`, `ReasonletAgentScope`, `ReasonletCallScope` and `ReasonletErasure` stay positional.
- **Lock:** PublicApiAnalyzers with `PublicAPI.Shipped.txt` per packable `src` project, rules as errors, plus a CI step asserting each baseline is non-empty. **`EnablePackageValidation` is deferred to phase 4.7** with `PackageValidationBaselineVersion` 1.0.0, because no pre-1.0 baseline is meaningful. 4.7 must add it.
- **Graph schema v1 is frozen.** Any change means `schemaVersion` 2 with its own schema file. Policy page `docs/concepts/compatibility.md` (written in 4.4), golden-document test.
- **#58** is closed by 4.4: AI.Sentinel 5.x bump, an incomplete scan maps to `Unavailable` unless a detector blocked.
- Guard 4.4 is in 4.2's plan. Impact analysis for 4.4 exists. 4.5 may link to `compatibility.md`.

## Decisions taken while grooming 4.5 to 4.7
- **4.5:** three new pages: `concepts/overview.md` with the package map, `concepts/variants.md` with a 1.0 / post-1.0 split and no promised API names, `guides/daedalus-and-thalos.md` as the contract from our side with host facts taken from those repos' READMEs.
- **4.6:** `guides/data-protection.md`, organised by deployer duty mapped to features, with a not-legal-advice note, a "what Mnemosyne does not do" section and a short EU AI Act section that classifies nothing. Mechanics stay in their current pages.
- **4.7:** the `Release-As: 1.0.0` footer goes in the squash message of the final phase PR, proven first by a release-please dry run, and the squash-merge message setting is checked. Stage B, after 1.0.0 is on nuget.org, adds `EnablePackageValidation` with baseline 1.0.0 in its own PR. Phase 4.7 completes after stage B. Marcel merges the phase PRs and the release PR; no hand tags.

## Open Decisions
- Rulings from phase 3.2 that are still open:
  - a remember call cancelled before it starts emits `mnemosyne.guard` with outcome `exception`;
  - `Abandon` takes `Task?`.

## Blockers
- None.

## Notes
- **Inputs for 4.4, done:** #58 in full (bump AI.Sentinel past 4.2.0, an incomplete scan maps to `Unavailable`, update the test `AThrowingDetectorIsSkippedAndTheCaptureIsAllowed`, fix the stale 4.1.1 references, update the two "known gap" paragraphs). Repo has no PublicApiAnalyzers, package validation or BenchmarkDotNet yet.
- **Release:** `release-please-config.json` has `bump-minor-pre-major`. Phase 4.7 must check how a 1.0.0 bump is triggered, for example a `Release-As` footer, and never tags by hand.
- **Git trap:** `push.default` is `upstream`. Use explicit `branch:branch` refspecs.
- **Tests:** the repo refuses `dotnet test --filter`. Build first, then run the test exe with `-class <FQN>`.
- **Docker:** the samples and the PostgreSQL tests need Docker with pgvector.
- **Thalos:** the Thalos adapter and Daedalus wiring are PRs in those repos. Thalos.NET.Sentinel still pins AI.Sentinel 2.3.1 (MarcelRoozekrans/Thalos.NET#457).
- **Commits:** no Claude signature or session link, no nested parentheses in commit bodies, conventions in `docs/planning/CONVENTIONS.md`.

## Recommended Next Step
1. Marcel merges PR #62, which now carries the grooming of all seven phases.
2. Then `start-next-phase`, or `ready-check` to queue 4.1 to 4.6 for an unattended run. Phase 4.7 needs Marcel to merge PRs, so build it attended.
