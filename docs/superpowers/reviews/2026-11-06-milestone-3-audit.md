# Milestone 3 audit: Off-the-shelf integrations

**Date:** 2026-11-06
**Verdict:** PASS

| Criterion | Evidence | Result |
|---|---|---|
| All planned phases complete | ROADMAP shows phases 3.1 to 3.7 `[status: complete]`, merged on `main` as PRs #25, #27, #28, #30, #48, #54 and #59 | Met |
| All tests passing | On `chore/complete-phase-3.7`, cut from `main` at 81fca35, `Mnemosyne.NET.Tests` passed 1582 of 1582 and `Mnemosyne.NET.PostgreSql.Tests` passed 313 of 313 against pgvector | Met |
| CI green, including the Native AOT gate | For 81fca35 on `main`: `build-test` on ubuntu and windows, `aot-publish`, `pack-validate`, `build`, `deploy` and the baseline trivy, nuget-audit and gitleaks checks succeeded. The baseline CodeQL run had no conclusion when checked | Met |
| A tool-calling chat transcript becomes a tool-shape graph | Phase 3.1, capture, in release v0.3.0 | Met |
| Captured content passes a redaction hook; a flagged capture is dropped and leaves a telemetry event without the content | Phase 3.2, capture guard | Met |
| With the review gate on, captured procedures stay out of recall until approved | Phase 3.3, human review gate with approve and reject | Met |
| `Mnemosyne.NET.Sentinel` runs AI.Sentinel's detectors as the redaction hook | Phase 3.4, package `src/Mnemosyne.NET.Sentinel` | Met |
| `UseReasonlets()` recalls, injects, captures and judges; `find_procedure` ships | Phase 3.5, in release v0.6.0 | Met |
| An Agent Framework `AIContextProvider` gives an agent the same recall and capture | Phase 3.6, package `src/Mnemosyne.NET.AgentFramework`, in release v0.7.0 | Met |
| Docs and samples cover every integration | Phase 3.7: guides `chat-pipeline.md`, `capture-and-review.md` and `agent-framework.md`, and samples `Mnemosyne.Samples.ChatPipeline` and `Mnemosyne.Samples.AgentFramework`, both self-checking and CI-gated | Met |
| Documentation | A spec and a plan exist for every phase 3.1 to 3.7 under `docs/superpowers/` | Met |
| Pre-push reviews on file | `docs/superpowers/reviews/pre-push-review-2026-11-05-2255.md`, phase 3.7, verdict PASS. Phases 3.2 to 3.5 have their own PASS reports | Met |
| Release tags correctly | Skipped: CONVENTIONS says milestone completion does not tag a release, and release-please owns tags | n/a |

## Notes

- The README and docs changes of phase 3.7 ship with the next release: PR #59 was titled `docs:`, so release-please opened no release PR for it.
- Follow-ups outside the definition of done: #57 (share the self-check runner across the four samples) and #58 (adopt the AI.Sentinel fix for an incomplete scan, plus stale 4.1.1 references).
