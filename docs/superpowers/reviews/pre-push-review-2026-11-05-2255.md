# Pre-push review — phase 3.7, integration docs and samples

| Metric | Value |
|---|---|
| Date | 2026-11-05 22:55 |
| Branch | `feat/phase-3.7-integration-docs-samples` (local `worktree-bridge-cse_01RwoARe4X9M8b24VfhYXqbn`) |
| Base Branch | `main` at the branch point 727b534 (phase 3.6 merge) |
| Commits Reviewed | 21 (`727b534..HEAD`) |
| Files Changed | 30 |
| Lines Added | 2223 |
| Lines Removed | 38 |
| Verdict | **PASS** |

Base note: the local `main` ref is stale (cdc8905, older than 727b534), so `main...HEAD` would show 201 files from earlier phases. The review uses `727b534..HEAD`, the real branch point.

## Plan adherence

- Plan: `docs/superpowers/plans/2026-11-05-phase-3.7-integration-docs-samples.md`. Spec: `docs/superpowers/specs/2026-11-05-phase-3.7-integration-docs-samples-design.md`. All plan checkboxes are ticked.
- Delivered: `samples/Shared/ScriptedChatClient.cs` and `CaptureRunWatcher.cs` with their two test files and the csproj links; the ChatPipeline and AgentFramework samples (csproj, Program.cs, README, compose.yaml) and their slnx entries; two CI steps per sample in `.github/workflows/ci.yml`; the guides `chat-pipeline.md`, `capture-and-review.md`, `agent-framework.md`; `docs/samples.md`; links from `docs/index.md`, `docs/getting-started.md`, `docs/concepts/reasonlets.md` and the three package READMEs; roadmap and milestone updates.
- Missing items: none found.
- Unplanned changes: none of significance. The branch also carries the earlier planning commits (spec, plan, roadmap, state).
- No `.cs` under `src/` changed. The three `src/` files in the diff are package READMEs.

## Code quality

No Blocker or Warning findings.

- **Security and privacy:** the samples use the throwaway local credentials of the compose files and CI service containers (`postgres`/`postgres`), as the existing samples do. No other secrets.
- **YAGNI:** the samples are small and single purpose. `ScriptedChatClient` and `CaptureRunWatcher` have tests. The test project compiles them in by link because the samples are Exe projects, as it already does for `HashingEmbeddingGenerator`.
- **Debug code:** `Console.WriteLine` appears in the two sample `Program.cs` files, but it is the samples' narrated output, not debug code. No `TODO`, `FIXME`, `HACK`, `Debugger.`, `#pragma warning disable`, `SuppressMessage` or `NoWarn` in added lines.
- **Error handling:** `CaptureRunWatcher.NextOutcomeAsync` cancels the read itself on timeout and maps the closed channel to `ObjectDisposedException`. Sound.
- **Docs spot check:** the CI step comments, sample READMEs and the compose files agree with the code (database names, 256 dimensions, ports, scripted tool names `orders_get`, `payments_refund`).
- Info: `docs/planning/STATE.md` is stale. The controller updates it before pushing.
- Info (accepted, not counted): the roughly 90-line runner block duplicated across the four samples, and the AI.Sentinel 4.2.0 follow-up with stale 4.1.1 comments in three .cs files. The controller files both as issues.

## Commit hygiene

- **Messages:** all 21 conventional (`feat`, `fix`, `docs`, `ci`, `test`, `chore`) with free scopes.
- **Claude signatures:** scanned every commit with `git log 727b534..HEAD --format=%B` for `Co-Authored-By`, `Generated with`, `claude.ai` and `session_`. No hits.
- **Nested parentheses in bodies:** scanned for `(` inside a parenthesised group. No hits.
- **Secrets scan:** no matches for key, token, private key or password patterns beyond the local `postgres` test credentials above.
- **Merge conflict markers:** none.
- **Unintended files:** none. No `.env`, `bin`, `obj`, `build` or `node_modules` paths in the diff.
- **Large files:** largest added file is the plan document at 262 lines.

## Regression tests

| Suite | Command | Result |
|---|---|---|
| Release build | `dotnet build Mnemosyne.NET.slnx -c Release` | 0 warnings, 0 errors |
| Unit | `tests/Mnemosyne.NET.Tests/bin/Release/net10.0/Mnemosyne.NET.Tests` | 1582 total, 0 failed, 0 skipped |
| PostgreSQL + pgvector (Docker, Testcontainers) | `tests/Mnemosyne.NET.PostgreSql.Tests/bin/Release/net10.0/Mnemosyne.NET.PostgreSql.Tests` | 313 total, 0 failed, 0 skipped |
| Docsite | `npm run build` (repo root) | success, static files generated |

The four sample runs were done earlier by the implementers against Docker (exit 0 each) and were not repeated. No web UI is affected, so there is no browser regression test. `git status` showed no build artifacts, since they are ignored.

## Summary

0 blockers, 0 warnings, 3 info items (stale STATE.md, the two ledger-recorded deferrals).
