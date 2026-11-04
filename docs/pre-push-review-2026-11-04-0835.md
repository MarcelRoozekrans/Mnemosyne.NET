# Pre-push review: phase 3.5 UseReasonlets

| Metric | Value |
|---|---|
| Date | 2026-11-04 08:35 |
| Branch | work-3.5, pushed to `feat/phase-3.5-reasonlets-find-procedure` |
| Base Branch | main (87e89f2) |
| Commits Reviewed | 26 |
| Files Changed | 56 |
| Lines Added | 6630 |
| Lines Removed | 54 |
| Verdict | **PASS** |

## Plan adherence

The review was done against the plan `docs/superpowers/plans/2026-11-04-phase-3.5-use-reasonlets.md` and the spec `docs/superpowers/specs/2026-11-03-phase-3.5-use-reasonlets-design.md`.

All 9 of 9 planned tasks are implemented. Each one passed a task review for spec compliance and quality. A whole-branch review followed, then one fix wave and a scoped re-review, which addressed every finding.

Where the build departed from the plan, the controller made and recorded a ruling. These rulings are Info items:

- **Exit codes.** The AOT smoke uses exit codes 40-43. The plan said 7, but codes up to 39 were already taken.
- **Success rate.** It is rendered `F0` plus `%`. The spec example reads "82%", and invariant `P0` would render "82 %".
- **Pending tool calls.** A run that ends on an unanswered tool call or an approval request is skipped with skip reason `pending_tool_calls`. This corrects the spec's claim about pipeline order, and the spec carries a dated amendment.
- **DI cycle.** The memory and the capture queue resolve lazily, and the chat evaluator's judge calls bypass the middleware. Without this, `UseReasonlets` combined with `AddMnemosyneChatEvaluator` on one client hung at startup.
- **Capture names.** Automatic captures are named after their tool shape, so that no question text from one user reaches another user's injected message.
- **Queue disposal.** The capture queue also implements `IDisposable`, so a synchronous provider dispose drains it instead of throwing.
- **Skipped scope.** `no_scope` is counted whenever Recall or Capture is on.

Three findings were filed for a design decision rather than fixed here: #45, #46 and #47.

## Code quality

- **Security:**
  - No secrets, no injection surface.
  - Telemetry tags carry no content, and canary tests in `ChatTelemetryTests` and `CaptureTelemetryTests` cover this.
  - Cross-user text was removed from injected names. The remaining path through extracted intent is filed as #47.
- **YAGNI:** nothing beyond the spec, apart from the robustness fixes listed under the rulings above.
- **Debug and temporary code:** none. No `Console.Write`, `Debugger`, TODO or FIXME was added to `src`. The only console output is in the AOT smoke program, which is its purpose.
- **Suppression:** no `#pragma`, `NoWarn` or `SuppressMessage` was added.
- **Error handling:** every memory path on the chat call is guarded and counted, including recall, the formatter, lazy resolution and hand-off. Caller cancellation propagates.
- **Test coverage:** every new type has unit tests. The pipeline-order tests use a real `FunctionInvokingChatClient`, and there are PostgreSQL integration tests and an AOT smoke.

## Commit hygiene

- **Messages:** all subjects are conventional commits, and every type is in `.commitlintrc.yml`.
  - No Claude trailers and no session links.
  - No nested parentheses in commit bodies.
  - Two bodies have paragraph lines over 100 characters. `body-max-line-length` is off in `.commitlintrc.yml`, and no footer line exceeds 100.
- **Secrets scan** of the added lines: clean. The only domain hit is the project's public docs site.
- **Unintended files:** none. Only `.cs`, `.md`, `.csproj`, `.props` and `.yml` files changed.
- **Merge conflict markers:** none.
- **Large files:** none. The largest changed file is `tests/Mnemosyne.NET.AotSmoke/Program.cs` at 50 KB of source.

## Regression tests

| Suite | Command | Result |
|---|---|---|
| Build | `dotnet build Mnemosyne.NET.slnx` | 0 warnings, 0 errors |
| Unit | `tests/Mnemosyne.NET.Tests/bin/Debug/net10.0/Mnemosyne.NET.Tests` | 1444 total, 0 failed, 0 skipped |
| Integration (PostgreSQL + pgvector, Docker) | `tests/Mnemosyne.NET.PostgreSql.Tests/bin/Debug/net10.0/Mnemosyne.NET.PostgreSql.Tests` | 301 total, 0 failed, 0 skipped |
| AOT | `dotnet publish tests/Mnemosyne.NET.AotSmoke -c Release -r linux-x64 -p:PublishAot=true -warnaserror`, then run the binary | no warnings, exit 0 (PostgreSQL part skipped: `MNEMOSYNE_AOT_PG` unset) |

The fix wave ran the unit suite five times in a row to check for flakiness. All five runs passed.

## Verdict

**PASS:** 0 blockers, 0 warnings, Info items only.
