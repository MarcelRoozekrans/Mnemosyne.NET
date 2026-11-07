# Milestone 4 Design — 1.0

**Date:** 2026-11-06
**Milestone:** 4
**Stage:** milestone (one milestone, multiple phases)

Builds on the [roadmap design](2026-10-21-roadmap-design.md), milestone 4 section, and the [milestone 3 design](2026-10-30-milestone-3-design.md).

## Goal

Ship `Mnemosyne.NET` 1.0.0 on NuGet, released by release-please. By then the public API and the graph schema are locked by a mechanical gate, the versioning and lineage API leaves room for variant selection without a breaking change, benchmarks run in CI, and the docsite explains the whole model, including the data protection guide. Milestone 3's open follow-ups are closed first, so 1.0 does not carry known duplication or a known fail-open in the Sentinel adapter.

## Decisions

| Decision | Detail | By |
|---|---|---|
| Follow-ups | #58 (AI.Sentinel bump, incomplete scan fails closed) goes into the API review phase, because it changes shipped behaviour and must settle before the freeze. #55 (shared PostgreSQL test stubs) and #57 (shared sample runner) become a small Refactor phase that comes first. | user |
| API lock | PublicApiAnalyzers with `PublicAPI.Shipped.txt` baselines per package, plus `EnablePackageValidation` against the last release, both enforced in CI. The graph schema gets a documented v1 compatibility policy. | user |
| Phase order | Hygiene, then versioning and lineage, then benchmarks, then the API review and freeze, then docs, then release. Versioning changes the API, so the freeze comes after it; benchmarks measure the code as it will ship. | my call |
| Variants | Variant selection itself stays in milestone 5, after 1.0. Milestone 4 only makes the lineage and versioning API fit it. | roadmap |
| Release | release-please owns the 1.0.0 release. The config has `bump-minor-pre-major`, so the phase checks how the 1.0.0 bump is triggered, for example a `Release-As` footer, and does not tag by hand. | my call |

## Definition of Done

- [ ] All planned phases complete
- [ ] All tests passing, including integration tests against PostgreSQL + pgvector
- [ ] CI green, including the Native AOT gate for every package
- [ ] #55, #57 and #58 closed
- [ ] The versioning and lineage API lets variant selection be added without a breaking change
- [ ] A BenchmarkDotNet suite for ranking, search and middleware overhead runs in CI
- [ ] PublicApiAnalyzers baselines and package validation fail CI on a breaking change, and the graph schema compatibility policy is published
- [ ] The docsite is complete: concepts, the variants vision, the Daedalus and Thalos guide
- [ ] The data protection guide is published
- [ ] 1.0.0 is on NuGet, released by release-please

## Phases

1. **Phase 4.1: Test and sample hygiene** — `Surface: Refactor`
   - **Goal:** Share the scripted model and embedding stubs across the PostgreSQL tests (#55) and the self-check runner across the four samples (#57).
2. **Phase 4.2: Versioning and lineage API** — `Surface: Backend`
   - **Goal:** Settle the versioning and lineage API so variant selection fits later.
3. **Phase 4.3: Benchmarks in CI** — `Surface: Infra`
   - **Goal:** BenchmarkDotNet suite for ranking, search and middleware overhead, running in CI.
4. **Phase 4.4: Public API review and freeze** — `Surface: Refactor`
   - **Goal:** Review the public API, adopt the AI.Sentinel fix (#58), add the PublicApiAnalyzers baselines and package validation, and set the graph schema compatibility policy.
5. **Phase 4.5: Docs completeness** — `Surface: Docs`
   - **Goal:** Concepts, the variants vision, and the Daedalus and Thalos guide.
6. **Phase 4.6: Data protection guide** — `Surface: Docs`
   - **Goal:** How scope, provenance, redaction, erasure, retention and telemetry support a deployer's GDPR duties, and a short note on where the EU AI Act does and does not apply.
7. **Phase 4.7: Subject export** — `Surface: Backend`
8. **Phase 4.8: 1.0.0 release** — `Surface: Infra`
   - **Goal:** Release 1.0.0 through release-please.

## Dependencies on Prior Milestones

- Depends on: milestone 3's integrations, which have now exercised the API; the milestone 3 audit passed on 2026-11-06.
- External: Thalos and Daedalus wiring PRs live in those repos, not here. Thalos.NET.Sentinel still pins AI.Sentinel 2.3.1 (Thalos.NET#457).

## External Constraints

- main is protected and every change needs a PR; release-please decides the version from commit types.
- Native AOT stays a CI gate for every package.

## Risk Areas

| Risk | Impact | Mitigation |
|---|---|---|
| Versioning change in 4.2 needs a schema or store signature change after 1.0 is nearly set | Breaking change late | 4.2 comes before the freeze; the design names what variants need |
| The AI.Sentinel jump from 4.2.0 to 5.x changes the adapter API | 4.4 grows | Scope the bump in that phase's design; the adapter is small |
| Benchmarks are noisy on shared CI runners | Flaky or useless gate | Run them as a report, not a pass or fail threshold, unless 4.3 finds a stable one |
| release-please will not cut 1.0.0 from the current config | Release blocked | 4.8 verifies the trigger before the last PR |

## Open Questions

- None for the milestone. Per-phase designs settle the details.
