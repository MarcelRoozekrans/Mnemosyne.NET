# Benchmarks

BenchmarkDotNet benchmarks for the parts of Mnemosyne where speed matters. They report numbers and assert nothing.

## Run them

From the repository root:

```bash
dotnet run -c Release --project benchmarks/Mnemosyne.NET.Benchmarks -- --filter '*Ranking*'
dotnet run -c Release --project benchmarks/Mnemosyne.NET.Benchmarks -- --filter '*Middleware*'
dotnet run -c Release --project benchmarks/Mnemosyne.NET.Benchmarks -- --filter '*Search*'
```

Add `--job short` for a quick pass. Results land in `BenchmarkDotNet.Artifacts`, or in the folder given to `--artifacts`.

## What they measure

- `RankingBenchmarks`: scoring and ordering candidates in memory.
- `MiddlewareBenchmarks`: what the chat middleware adds to a call, over a client that does nothing.
- `SearchBenchmarks`: the PostgreSQL similarity search over 1000 stored Reasonlets with the lineage id resolved for each hit, with version chains of depth 1 and 20.

## The database

`SearchBenchmarks` needs PostgreSQL 15 or later with pgvector 0.8.0 or later. Set `MNEMOSYNE_BENCH_PG` to its connection string, for example:

```bash
export MNEMOSYNE_BENCH_PG='Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=mnemosyne_bench'
```

Use a database of its own: the benchmark migrates the `mnemosyne` schema with 256 dimensions. Without the variable the search benchmark is skipped and the other two run.

## In CI

The `Benchmarks` workflow runs on pull requests that touch `src`, `benchmarks` or `Directory.Packages.props`, and on demand. It writes the report to the job summary and uploads the results as an artifact.

It reports and does not gate. A hosted runner is shared, so a number from one run says little about a number from another. To compare two versions, run both on the same machine, one after the other.
