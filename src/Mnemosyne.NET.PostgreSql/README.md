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

This package depends on `Mnemosyne.NET.Abstractions` only, so it does not bring in recall. To recall and rank through `IReasonletMemory`, add the core `Mnemosyne.NET` package as well.

## Documentation

- [PostgreSQL store](https://marcelroozekrans.github.io/Mnemosyne.NET/guides/postgresql): requirements, options, migrations, `search_path` and privileges, native AOT hosts, search behaviour, errors, and what erasure leaves on disk.
- [Data lifecycle](https://marcelroozekrans.github.io/Mnemosyne.NET/guides/data-lifecycle): erasure by scope and by subject, retention with `PruneAsync`, and what the counters keep.
- [Telemetry](https://marcelroozekrans.github.io/Mnemosyne.NET/guides/telemetry): the activity source, the meter, every activity and metric, and the privacy rule.
- [Getting started](https://marcelroozekrans.github.io/Mnemosyne.NET/getting-started): wiring the store, an embedding generator and the facade together.

See the [repository README](https://github.com/MarcelRoozekrans/Mnemosyne.NET#readme).
