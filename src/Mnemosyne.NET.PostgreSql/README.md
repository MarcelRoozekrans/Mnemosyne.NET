# Mnemosyne.NET.PostgreSql

PostgreSQL + pgvector store for Reasonlets: an `IReasonletStore` that keeps procedures, their runs and their feedback, and finds the nearest procedure to an embedding.

## Requirements

- PostgreSQL with **pgvector 0.8.0 or later**. Search uses iterative HNSW scans, which arrived in 0.8.0; `MigrateAsync` fails with a clear error on an older pgvector.
- The database user must be able to run `CREATE EXTENSION IF NOT EXISTS vector` and create the `mnemosyne` schema, at least for the first migration.

## Setup

Register an `NpgsqlDataSource` and the store. The store takes the data source from the container.

```csharp
using Mnemosyne.NET.PostgreSql;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
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

`IReasonletStore` resolves to the same singleton. The options are passed as an object, not a configure delegate, because their members are `required`. They are validated when you register, and `AddMnemosynePostgreSql` throws `ArgumentOutOfRangeException` for a bad value.

### Slim and native AOT hosts

A host that builds its data source with `NpgsqlSlimDataSourceBuilder` (which native AOT needs) must call `EnableArrays()`. The embedding is a `float[]` and erasure sends `Guid[]` arrays, and neither works without it:

```csharp
var dataSource = new NpgsqlSlimDataSourceBuilder(connectionString)
    .EnableArrays()
    .Build();
```

## Migrations

Call `MigrateAsync` once at startup. It is safe to run again: applied migrations are skipped. The first run records `Dimensions`, and a later run with a different value fails with a validation error, because mixing embedding models would corrupt similarity. A store method called before `MigrateAsync` fails with `Unavailable` ("schema not migrated").

All tables live in the fixed `mnemosyne` schema. It is not configurable, and every statement names it in full, so the host's `search_path` does not matter.

## Options

| Option | Default | Limits |
|---|---|---|
| `Dimensions` (required) | none | 1 to 2000 |
| `HnswM` | 16 | 2 to 100 |
| `HnswEfConstruction` | 64 | 4 to 1000, and at least `2 * HnswM` |

- `Dimensions` is fixed when the schema is created, and 2000 is the most a pgvector HNSW index can hold. Pick the dimension of your embedding model; every embedding passed to the store must match it.
- `HnswEfConstruction` must be at least `2 * HnswM` because pgvector refuses to build the index otherwise. Raise it along with `HnswM`.

## Erasure

`EraseAsync` takes a scope, and optionally a subject:

- **By scope** deletes every Reasonlet in the scope, with its executions and feedback.
- **By subject** deletes that subject's Reasonlets in the scope, then that subject's executions on Reasonlets that are kept.

The rules that follow from this:

- Other subjects' Reasonlets survive. When a Reasonlet is erased, the `parent_id` of its children is cut, so each child becomes the top of its own lineage.
- Run records follow their procedure. Every execution and feedback on an erased Reasonlet is removed with it, including other subjects' runs of it.
- The result counts the Reasonlets deleted and every execution removed. A cut parent link is not counted.
- The whole erase is one transaction. A deadlock with a concurrent remember answers `Conflict`; retry it.

## Searching

`SearchAsync` returns up to `limit` candidates of one scope, nearest first. It uses iterative HNSW scans, so a scope with a small share of the table still gets its full limit. Candidates at exactly the same distance come back in an unspecified order.
