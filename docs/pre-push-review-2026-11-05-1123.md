# Pre-push review — phase 3.6, Agent Framework context provider

| Metric | Value |
|---|---|
| Date | 2026-11-05 11:23 |
| Branch | `feat/phase-3.6-agent-framework-context-provider` (local `phase-3.6-work`) |
| Base Branch | `main` (origin/main 8e3c2e4, merged in as 84155ac) |
| Commits Reviewed | 25 non-merge commits |
| Files Changed | 69 |
| Lines Added | 4160 |
| Lines Removed | 337 |
| Verdict | **PASS** |

## Plan adherence

- Plan: `docs/superpowers/plans/2026-11-05-phase-3.6-agent-context-provider.md`. Spec: `docs/superpowers/specs/2026-11-05-phase-3.6-agent-context-provider-design.md`, with its 2026-11-05 amendments.
- Planned tasks implemented: 9 of 9. Each passed a task review (spec and quality). An Opus whole-branch review followed, then one fix wave with a scoped re-review.
- Deviations from the plan text, each recorded as a ruling in the spec amendments:
  - the capture record is `ReasonletCaptureRun`, because `ReasonletRun` already exists in `Mnemosyne.NET.Abstractions`;
  - two public counting entry points, `ReasonletSuggestions.CountFailed` and `ReasonletCaptures.CountFailed`, so the new package uses the core's instruments without `InternalsVisibleTo`;
  - the provider's agent tests live in `tests/Mnemosyne.NET.Tests/AgentFramework`, not in a new test project;
  - a run without stored state counts nothing at the end hook, because the start hook already counted it;
  - `UseReasonlets` resolves `IReasonletCaptureSink`, not the concrete queue;
  - `TryCapture` copies the messages.
- Unplanned but in scope:
  - `docs/guides/telemetry.md`, `docs/concepts/versions.md`, `docs/guides/data-lifecycle.md` and `docs/guides/postgresql.md` were updated to match the new behaviour;
  - the internal `ReasonletCaptureProcessor` constructor lost an unused parameter.

## Code quality

No findings remain. The per-task reviews and the whole-branch review covered the seven rules. Their findings were fixed on the branch, including Minors, or dropped with a reason in the SDD ledger:

- **Security and privacy:** no signal carries scope, subject, question, injected text, tool or argument names, or exception messages, and tests assert this. The #45 advisory-lock key is hashed in SQL from bound parameters.
- **Error handling:** memory never breaks a run. Recall, scope, resolution and hand-off failures are counted, and only the caller's own cancellation propagates.
- **Debug code:** none. A grep of added `src` lines for `Console.Write`, `Debugger.`, TODO, FIXME and HACK found nothing.
- **Warning suppressions:** none (`#pragma warning disable`, `SuppressMessage`, `NoWarn`).

## Commit hygiene

- **Messages:** conventional. No Claude trailers or session links, and no nested parentheses in bodies. `Closes #45.` and `Closes #47.` are in the fix commits. The repository squash-merges, so the per-commit types do not reach the changelog.
- **Secrets scan:** no matches.
- **Merge conflict markers:** none.
- **Unintended files:** none. Outside `src/`, `tests/` and `docs/`, only `Directory.Packages.props`, `Mnemosyne.NET.slnx`, `README.md` and `renovate.json` changed, all as planned.
- **Large files:** no file over 800 added lines.

## Regression tests

Run after merging origin/main into the branch:

| Suite | Command | Result |
|---|---|---|
| Release build | `dotnet build Mnemosyne.NET.slnx -c Release` | 0 warnings |
| Unit | `tests/Mnemosyne.NET.Tests/bin/Debug/net10.0/Mnemosyne.NET.Tests` | 1563 total, 0 failed, 0 skipped |
| PostgreSQL + pgvector (Docker) | `tests/Mnemosyne.NET.PostgreSql.Tests/bin/Debug/net10.0/Mnemosyne.NET.PostgreSql.Tests` | 313 total, 0 failed, 0 skipped |
| Native AOT smoke | `dotnet publish tests/Mnemosyne.NET.AotSmoke -c Release`, then run the binary without `MNEMOSYNE_AOT_PG` | 0 IL or AOT warnings, exit 0 |

No web UI is affected, so there is no browser regression test.

## Follow-ups for the PR

- Close #46 with a comment linking `docs/guides/chat-evaluator.md`.
- File one issue: `ScriptedModel` and `ConstantEmbeddings` are copied across five PostgreSQL test classes. The pattern predates this branch and the fix is a test-helper refactor in another area.
