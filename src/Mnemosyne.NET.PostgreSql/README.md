# Mnemosyne.NET.PostgreSql

PostgreSQL + pgvector store for Reasonlets: an `IReasonletStore` that keeps procedures, their runs and their feedback, and finds the nearest procedure to an embedding. It needs PostgreSQL 15 or later with pgvector 0.8.0 or later.

```bash
dotnet add package Mnemosyne.NET.PostgreSql
```

## Setup

Register an `NpgsqlDataSource` and the store, then run the migrations at startup:

```csharp
using Mnemosyne.NET.PostgreSql;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddMnemosynePostgreSql(new MnemosynePostgreSqlOptions { Dimensions = 1536 });

var app = builder.Build();

// The host runs the migrations; the store never does so on its own.
var store = app.Services.GetRequiredService<ReasonletPostgreSqlStore>();
var migrated = await store.MigrateAsync(CancellationToken.None);
if (migrated.IsFailure)
{
    throw new InvalidOperationException(migrated.Error.Message);
}
```

## Upgrading

A new release can bring a schema migration, which `MigrateAsync` applies. Until it has run, the new store answers `Unavailable` with "schema not migrated: call MigrateAsync" rather than failing on a missing column.

**Stop every instance of the old release before you migrate.** The old release does not know the new schema. Migration 0002, which stores each run's verdict, renames `success` to `agent_success` and adds a new `NOT NULL` `success` column for the effective success. An old instance that is still running omits `agent_success` from its insert, so it fails loudly on its next run record instead of writing a run without its verdict inputs. Each migration file applies in one transaction, whole or not at all; 0002 rewrites every row of `mnemosyne.reasonlet_executions` and holds that table locked while it does.

## Run verdicts

Each row of `mnemosyne.reasonlet_executions` keeps every input to the run's verdict, and the verdict itself:

| Column | Holds |
|---|---|
| `agent_success` | the agent's own `ReasonletOutcome.Success` |
| `evaluated_success`, `evaluation_score`, `evaluator` | an evaluator's verdict, its score and the judge's name; all three null unless an evaluator judged the run |
| `feedback_worked` | a human's `ReasonletFeedback.Worked`, null until one is recorded |
| `success` | the effective success: `coalesce(feedback_worked, evaluated_success, agent_success)` |

A database `CHECK` holds that rule, so `success` can't drift from its inputs. The success counters that ranking reads count the effective success, and feedback that changes it moves them in the same transaction. The run's answer, which only the evaluator sees, is never stored.

The `mnemosyne.execution.append` activity carries `mnemosyne.verdict_source`, `evaluator` or `agent`, and so does a successful `mnemosyne.remember`, for the run it records. A successful `mnemosyne.feedback.append` that gives a `Worked` verdict carries it as `feedback`. The receipts of `AppendExecutionAsync` and `RememberAsync` carry the same source as a `ReasonletVerdictSource`, with the run's effective success.

This package depends on `Mnemosyne.NET.Abstractions` only, so it does not bring in recall. To recall and rank through `IReasonletMemory`, add the core `Mnemosyne.NET` package as well.

## Documentation

- [PostgreSQL store](https://marcelroozekrans.github.io/Mnemosyne.NET/guides/postgresql): requirements, options, migrations, `search_path` and privileges, native AOT hosts, search behaviour, errors, and what erasure leaves on disk.
- [Data lifecycle](https://marcelroozekrans.github.io/Mnemosyne.NET/guides/data-lifecycle): erasure by scope and by subject, retention with `PruneAsync`, and what the counters keep.
- [Telemetry](https://marcelroozekrans.github.io/Mnemosyne.NET/guides/telemetry): the activity source, the meter, every activity and metric, and the privacy rule.
- [Getting started](https://marcelroozekrans.github.io/Mnemosyne.NET/getting-started): wiring the store, an embedding generator and the facade together.

See the [repository README](https://github.com/MarcelRoozekrans/Mnemosyne.NET#readme).
