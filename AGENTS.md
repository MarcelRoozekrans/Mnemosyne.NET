# AGENTS.md: Mnemosyne.NET

Procedural memory for .NET agents: store, find and reuse the problem-solving procedures that worked,
as Reasonlets. Six NuGet packages ship from `src/`, sharing one version: `Abstractions` (records and
interfaces), `Mnemosyne.NET` (facade and ranking), `PostgreSql` (pgvector store), `Minos`, `Sentinel`
and `AgentFramework` (adapters). The docs site under `docs/` is a Docusaurus build, not .NET.

## Layout

- Only `src/*` is packed. `tests/`, `benchmarks/` and `samples/` are `IsPackable=false`.
- `tests/Mnemosyne.NET.AotSmoke` is a native AOT app, not a test project.
- `tests/Mnemosyne.NET.Tests` compiles in `samples/Shared/*.cs` and
  `tests/Mnemosyne.NET.PostgreSql.Tests/TelemetryCapture.cs` by link. Moving those files breaks it.
- `schemas/reasonlet-graph.v1.schema.json` and `tests/Mnemosyne.NET.Tests/Graph/GoldenV1/*.json` are
  frozen: graph schema v1 never changes, a new format is a new `schemaVersion` and a new file.

## Build and test

Prerequisites: `global.json` pins SDK 11.0.100 with `rollForward: latestFeature`, so an installed 10.x
SDK alone fails. Every `src/*` project and the test projects multi-target `net10.0;net11.0`, so tests
need both the .NET 10 and .NET 11 runtimes. `AotSmoke` and the samples target `net11.0` only, the
benchmarks `net10.0` only. The `PostgreSql.Tests` suite needs Docker for Testcontainers.

CI job `build-test` in `ci.yml`, on ubuntu and windows:

```bash
dotnet restore Mnemosyne.NET.slnx
dotnet build Mnemosyne.NET.slnx --no-restore -c Release
dotnet test tests/Mnemosyne.NET.Tests --no-build -c Release --verbosity normal
# ubuntu leg only: needs Docker with Linux containers
dotnet test tests/Mnemosyne.NET.PostgreSql.Tests --no-build -c Release --verbosity normal
```

CI loops over `tests/*.Tests/*.csproj`, and the windows leg skips every project that declares
`<RequiresDocker>true</RequiresDocker>`. It also sets `TestingPlatformCommandLineArguments` to collect
Cobertura coverage with `coverage.config.xml`. The `coverage-gate` step of ci-baseline then enforces
the 80 percent floor on the ubuntu leg. That gate is an action, so the commands above do not
reproduce it.

CI job `pack-validate`:

```bash
dotnet tool restore
PACKAGE_VERSION=$(dotnet dotnet-gitversion /output json | jq -r '.SemVer')
dotnet pack Mnemosyne.NET.slnx -c Release -o artifacts/packages -p:Version="$PACKAGE_VERSION"
```

CI job `aot-publish` needs a pgvector Postgres 17 service and `MNEMOSYNE_AOT_PG`:

```bash
dotnet publish tests/Mnemosyne.NET.AotSmoke -c Release -r linux-x64 -p:PublishAot=true -warnaserror
tests/Mnemosyne.NET.AotSmoke/bin/Release/net11.0/linux-x64/publish/Mnemosyne.NET.AotSmoke
dotnet run --project samples/Mnemosyne.Samples.Variants -c Release
```

The same job runs the StoreAndRecall, Evaluation, ChatPipeline and AgentFramework samples, each on a
database of its own named by `MNEMOSYNE_SAMPLE_PG`. A sample exits 1 when what the docs describe
differs, and that blocks a release. The `Variants` sample needs no database.

CI job `benchmarks` in `benchmarks.yml` reports and never gates:

```bash
dotnet run -c Release --project benchmarks/Mnemosyne.NET.Benchmarks -- --filter "${FILTER:-*}" --job short
```

Without `MNEMOSYNE_BENCH_PG` the search benchmark is skipped. The docs build is job `build` in
`docs.yml`: `npm ci`, then `npm run build`, which fails on a broken link.

Inner loop, run on this repo and passing:

```bash
dotnet test tests/Mnemosyne.NET.Tests -c Release
tests/Mnemosyne.NET.Tests/bin/Release/net11.0/Mnemosyne.NET.Tests -class Mnemosyne.NET.Tests.RepoConventionsTests
```

The second line runs one class with the native xunit v3 runner, after a build. On Windows the binary
ends in `.exe`.

## Conventions and gotchas

- Never pass `--filter` to `dotnet test`. The test projects run on Microsoft.Testing.Platform, which
  ignores the VSTest filter and would run every test, so `Directory.Build.targets` fails the build
  with `MNEMO0001`. Select by project, or use the native runner shown above.
- Public API is gated by `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` in each `src/*`
  project, as build errors. A new public member adds a line to `Unshipped`. The maintainer moves
  those lines to `Shipped` in the release-please PR, so do not move them. Package validation also
  fails `pack` on a removed or changed public member.
- Package versions live in `Directory.Packages.props` only. The pins on
  `Microsoft.Testing.Extensions.CodeCoverage` and `xunit.v3` must move together.
- A new `src/*` project needs its own `README.md` and `Description`, a `PublicAPI` pair, an entry in
  `Mnemosyne.NET.slnx`, and a `ProjectReference` plus `TrimmerRootAssembly` in `AotSmoke`.
  `RepoConventionsTests` checks each of these.
- Dependency direction is tested: the store depends on Abstractions only, and each adapter depends on
  Abstractions or the core plus its own library. Do not add a reference between adapters.
- A test project that uses Testcontainers must declare `<RequiresDocker>true</RequiresDocker>`, or
  the windows CI leg runs it and fails.
- Packages must stay AOT and trim safe: `aot-publish` fails on any trim or AOT warning.
- `Mnemosyne.NET.Sentinel` sets `ZeroAllocAuthorizationDisableBundledAnalyzer` and
  `ZeroAllocGeneratedAccessibility=Internal`, so AI.Sentinel's generators add no public type.
  Keep both switches.
- `codecov.yml` sets the patch target to 100 percent with no threshold, so cover every new line.

<!-- org-core:start sha256=08a88996bc9165f888fc0260607cfd0670cae128c323e16707c6b452ee66222d (generated from docker-nuc scripts/agents-md/org-core-personal.md; do not edit here) -->
## Org rules

These rules hold in every public MarcelRoozekrans repo.

### Commits and releases

- Conventional Commits. release-please reads them on the default branch, and the changelog sections
  in `release-please-config.json` decide which types count. The standard: `feat`, `fix`, `perf`,
  `deps` and `revert` open or update the release PR; `docs`, `test`, `refactor`, `build`, `ci`,
  `chore` and `style` do not, unless the commit is breaking or has a `Release-As:` footer. The
  release happens when that PR merges. A breaking change needs `!` or a `BREAKING CHANGE:` footer.
- A commit body never contains nested parentheses. release-please cannot parse them and silently
  drops the whole commit from the changelog, while the workflow still reports success.
- After merging a commit that should release, check that it appears in the release-please PR.

### Branches and pull requests

- Every change goes through a branch and a pull request. Never push to the default branch directly.
- A PR body says what the diff does, and nothing the diff does not do.
- Fix review findings in the PR under review, including small ones. File an issue only for a finding
  that belongs to other code, needs a design decision, is its own piece of work, or waits on
  something external.

### CI

- The .NET repos call the shared workflows of `MarcelRoozekrans/ci-baseline`, pinned to `@v1`, never
  `@main`.
- Its coverage gate fails the build when line coverage drops below the floor of 80%, and Codecov
  fails a PR that lowers project coverage by more than 1 percentage point. New code comes with
  tests.
- A repo with a lower floor documents the reason and links the issue that raises it.
<!-- org-core:end -->
