# Milestone 4 audit: 1.0

**Date:** 2026-11-07
**Verdict:** PASS, with one warning

| Criterion | Evidence | Result |
|---|---|---|
| All planned phases complete | ROADMAP shows phases 4.1 to 4.8 `[status: complete]`; phase 4.8 closed by #89 | Met |
| All tests passing | On `main` at 41bc4eb, `Mnemosyne.NET.Tests` passed 1645 of 1645 and `Mnemosyne.NET.PostgreSql.Tests` passed 345 of 345 against pgvector. Release build has 0 errors | Met |
| CI green, including the Native AOT gate | For 62dadcc on `main`: CI, Docs, baseline-checks and Release Please succeeded. The run for 41bc4eb, a planning-only commit, was still in progress when checked | Met |
| #55, #57 and #58 closed | All three are CLOSED on GitHub | Met |
| Versioning and lineage API leaves room for variants | Phase 4.2, plus `docs/concepts/variants.md` | Met |
| BenchmarkDotNet suite runs in CI | `benchmarks/Mnemosyne.NET.Benchmarks` and `.github/workflows/benchmarks.yml`; the workflow's latest completed run succeeded | Met |
| PublicApiAnalyzers baselines and package validation fail CI on a breaking change; schema compatibility policy published | `Shipped` baselines hold the 1.0.0 surface (#87), package validation against baseline 1.0.0 is in `Directory.Build.targets` (#88), policy in `docs/concepts/compatibility.md` | Met |
| Docsite complete: concepts, variants vision, Daedalus and Thalos guide | `docs/concepts/`, `docs/concepts/variants.md`, `docs/guides/daedalus-and-thalos.md` | Met |
| Data protection guide published | `docs/guides/data-protection.md`, `docs/guides/data-lifecycle.md` | Met |
| 1.0.0 on NuGet, released by release-please | GitHub release and tag `v1.0.0` (2026-11-07), cut by release-please; all six packages published per STATE.md | Met |
| Documentation | A design and a plan exist for each of phases 4.1 to 4.8 under `docs/superpowers/` | Met |
| Pre-push reviews on file | No `pre-push-review` report is dated within milestone 4. The newest are from phase 3.7 | Warning |
| Release tags correctly | Skipped: CONVENTIONS says milestone completion does not tag a release, and release-please owns tags | n/a |

## Notes

- The pre-push review gap is a warning, not a failure. Milestone 4 was reviewed per PR instead; its API-hiding rulings are in `2026-11-06-phase-4.4-public-api-review-notes.md`.
- The NuGet publication of all six packages is taken from STATE.md and the v1.0.0 release; this audit did not query nuget.org.
- Open rulings carried past 1.0: a remember call cancelled before it starts emits `mnemosyne.guard` with outcome `exception`, and `Abandon` takes `Task?`.
