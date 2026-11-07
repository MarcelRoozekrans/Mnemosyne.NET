# Milestone 5 audit: Variants

**Date:** 2026-11-07
**Verdict:** PASS, with one warning

| Criterion | Evidence | Result |
|---|---|---|
| All planned phases complete | ROADMAP shows phases 5.1 to 5.3 `[status: complete]`. Their PRs #93, #95 and #96 are merged on `main`, and every plan checkbox is ticked | Met |
| All tests passing, including PostgreSQL + pgvector | On `main` at 8d91efa, `Mnemosyne.NET.Tests` passed 1754 of 1754 and `Mnemosyne.NET.PostgreSql.Tests` passed 345 of 345 against pgvector. For 8d91efa, the CI, Docs, baseline-checks and Release Please runs succeeded | Met |
| Variant selection works behind the existing selector interface, opt-in, and package validation against 1.0.0 stays green | `VariantSelector` implements `IReasonletSelector` and is registered only by `AddMnemosyneVariantSelection`. `PublicAPI.Shipped.txt` is unchanged, and the new members are in `Unshipped`. `dotnet pack src/Mnemosyne.NET` passes validation against baseline 1.0.0, and CI `pack-validate` passed on #93, #95 and #96 | Met |
| A new version with few runs gets tried before the best known one dominates, shown by a simulation test | `VariantSimulationTests`: an incumbent with 200 runs at 0.7 and a new version with no runs at 0.9. With Thompson sampling, the new version is chosen at least once in the first 20 rounds and in at least 60 of the last 100, for seeds 11, 23 and 42; observed 10 to 18 and 100. With `None` it is never chosen | Met |
| Selection is deterministic when seeded, and the recall activity records the chosen variant and whether it was an exploration | `VariantExplorationTests` covers a seeded run repeating, independence from lineage and variant order, and concurrent calls picking the same variants. The recall activity carries the `mnemosyne.recall.chosen.id` tag, plus `mnemosyne.recall.variant.lineages`, `.dropped` and `.explored`; `VariantTelemetryTests` and `VariantExplorationTests` cover them | Met, see note |
| Docs and a self-checking sample cover variants | `docs/concepts/variants.md` is rewritten and `docs/guides/variants.md` is new. Telemetry, ranking, samples and versions pages link to them. `samples/Mnemosyne.Samples.Variants` exits 1 on a wrong receipt and runs in CI `aot-publish`, which `RepoConventionsTests` pins | Met |
| Documentation | Each of phases 5.1 to 5.3 has an approved design under `docs/superpowers/specs/` and a ticked plan under `docs/superpowers/plans/` | Met |
| Pre-push reviews on file | No `pre-push-review` report is dated within milestone 5. Each phase instead had a task review per plan task and a final whole-branch review with one fix wave, run by the unattended session | Warning |
| Release tags correctly | Skipped: CONVENTIONS says milestone completion does not tag a release, and release-please owns tags | n/a |

## Notes

- **Exploration is recorded per recall, not per variant.** The `explored` tag counts the lineages whose pick was an exploration, so a recall with one lineage records 0 or 1. Tags carry counts only, by design. The chosen Reasonlet's id is the existing `mnemosyne.recall.chosen.id` tag.
- **Released:** 1.1.0, cut by release-please (#94), carries both features. The 5.3 docs commit does not trigger a release.
- **Run log:** the unattended run's decisions are in each PR's `## Unattended calls` section.
