---
title: PostgreSQL store
sidebar_position: 1
---

# PostgreSQL store

`Mnemosyne.NET.PostgreSql` is the store: an `IReasonletStore` on PostgreSQL with pgvector. It keeps the procedures, their runs and their feedback, and finds the procedures nearest to a question's embedding. This page covers what the store needs from the database, how to set it up and migrate it, and what its errors mean.

## Requirements

- **PostgreSQL 15 or later.** Every foreign key carries the scope, and a version's link to its parent uses `ON DELETE SET NULL (parent_id)`, a form PostgreSQL 15 introduced.
- **pgvector 0.8.0 or later.** Search uses iterative HNSW index scans, which arrived in 0.8.0.

`MigrateAsync` checks both and fails with a `Validation` error that names the version it found. The `pgvector/pgvector:pg17` Docker image meets both.

## Install

```bash
dotnet add package Mnemosyne.NET.PostgreSql
```

This package depends only on `Mnemosyne.NET.Abstractions`, so it doesn't bring in recall. To recall and rank through `IReasonletMemory`, also add the core package, register an embedding generator and call `AddMnemosyne`, as [Getting started](../getting-started.md) shows:

```bash
dotnet add package Mnemosyne.NET
```

## Set up the store

Register an `NpgsqlDataSource` and the store. The store takes the data source from the container.

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

- `AddMnemosynePostgreSql` registers `ReasonletPostgreSqlStore` as a singleton, and `IReasonletStore` as that same instance.
- The options are an object, not a configure delegate, because `Dimensions` is a `required` member and a delegate can't set one. They are checked when you register, and a bad value throws `ArgumentOutOfRangeException`.
- The store holds at most one connection at a time from the data source. Most operations use one; a prune opens several in sequence, one per batch.

### Slim and native AOT hosts

A host that builds its data source with `NpgsqlSlimDataSourceBuilder`, which native AOT needs, must call `EnableArrays()`. Embeddings are sent as a `float[]` and erasure sends `Guid[]` arrays, and neither works without it:

```csharp
var dataSource = new NpgsqlSlimDataSourceBuilder(connectionString)
    .EnableArrays()
    .Build();
```

## Options

| Option | Default | Allowed |
|---|---|---|
| `Dimensions` (required) | none | 1 to 2000 |
| `HnswM` | 16 | 2 to 100 |
| `HnswEfConstruction` | 64 | 4 to 1000, and at least `2 * HnswM` |

- **`Dimensions`** is the length of every embedding. Pick your embedding model's output size; every embedding passed to the store must match it. 2000 is the most a pgvector HNSW index can hold. The store reports the value as `IReasonletStore.Dimensions`, and the core facade checks each generated embedding against it before it calls the store.
- **`HnswM`** and **`HnswEfConstruction`** are the HNSW index's `m` and `ef_construction`. The defaults are pgvector's own. `HnswEfConstruction` must be at least `2 * HnswM`, because pgvector refuses to build the index otherwise, so raise it along with `HnswM`.

## Migrations

Call `MigrateAsync` once at startup. The store never migrates on its own, and every other store method answers `Unavailable`, with the message "schema not migrated: call MigrateAsync", until it has run.

- **It is safe to run again.** Applied migrations are skipped.
- **It fixes the dimension.** The first run records `Dimensions`. A later run with a different value fails with a `Validation` error and changes nothing, because mixing embedding models would corrupt similarity. To switch models, use a new database.
- **It builds the index once.** The first run builds the HNSW index with `HnswM` and `HnswEfConstruction` and records them. A later run with different values fails with a `Validation` error naming both, because the options only apply when the index is built. To change them, rebuild the index yourself.
- **The schema is fixed.** All tables live in the `mnemosyne` schema, which isn't configurable, and every statement names it in full.

### Upgrading

A new release can bring a new migration, which `MigrateAsync` applies. Until it has run, the new release's store answers `Unavailable`, with "schema not migrated: call MigrateAsync", rather than failing on a missing column.

**Stop every instance of the old release before you migrate.** An old release doesn't know the new schema. Migration `0002_evaluation`, which stores each run's verdict, renames the runs' `success` column to `agent_success` and adds the evaluator's verdict, the human's verdict and the effective success. An old instance still running once it has applied fails loudly on its next run record, rather than writing a run without its verdict.

Each migration applies in one transaction, whole or not at all. `0002_evaluation` rewrites every row of `mnemosyne.reasonlet_executions` and holds that table under an exclusive lock while it does, so on a large table, plan for the pause.

## Connections and privileges

### `search_path`

The store's tables are always named in full, but the `vector` type and its operators are not: they resolve through the connection's `search_path`. The schema the `vector` extension lives in, normally `public`, must be on the `search_path` of every connection the data source opens.

The migration history is the other thing the `search_path` decides. The migration runner, ZeroAlloc.ORM, records the applied migrations in a `__zaorm_migrations` table in the connection's `current_schema()`, the first existing schema on the `search_path`. Keep it the same across runs. If it changes, `MigrateAsync` finds no history there and runs the migrations again, which fails because the tables already exist.

### Privileges

**The role that runs `MigrateAsync`** needs:

- `CREATE` on the database, to create the `mnemosyne` schema;
- `CREATE` on `current_schema()`, for the migration history table;
- superuser, the first time only, because the first migration runs `CREATE EXTENSION IF NOT EXISTS vector` and `vector` is not a trusted extension. A DBA can instead create the extension once, ahead of time. The migration then finds it and needs no superuser.

**The role the store runs as** needs the `TEMPORARY` privilege on the database. An erase by subject keeps the runs it removes in a temporary table for the length of its transaction. PostgreSQL grants `TEMPORARY` to `PUBLIC` by default, so this only matters if your DBA has revoked it.

If the store runs as a different role from the one that migrates, that role also needs `USAGE` on the `mnemosyne` schema and on the schema the `vector` extension lives in, plus `SELECT`, `INSERT`, `UPDATE` and `DELETE` on the tables in `mnemosyne`.

## Searching

Recall asks the store for the candidates nearest to the question's embedding, in one scope. The store answers with up to the requested number of `Active` Reasonlets, from 1 to 200, sorted exactly by distance.

- **HNSW is approximate.** A near neighbour can occasionally be missed.
- **Small scopes still fill up.** Iterative scans keep reading the index until they have enough rows that pass the scope filter, so a scope with a small share of the table usually still gets its full limit.
- **The scan has a budget.** It stops after `hnsw.max_scan_tuples` index tuples, 20,000 by default, and `hnsw.scan_mem_multiplier` can end it early once it uses its memory budget. So a very small scope in a very large table can come back with fewer candidates than the limit.
- **Exact ties have no order.** Candidates at exactly the same distance come back in an unspecified order.
- **The embedding needs a nonzero, finite norm.** Cosine similarity is undefined for an all-zero vector, and pgvector sums the squares in single precision, so they can overflow. The store answers `Validation` to a search or remember whose embedding has a zero norm or squares that overflow. Through `IReasonletMemory` you see `Unavailable` instead, because the facade checks the embedding it generated before it calls the store.

Each candidate's metrics, its runs, successes and last success, come from a counter row the store keeps in step with every run it records, so search doesn't aggregate runs.

## Errors

Like the rest of Mnemosyne, the store answers expected failures with a result and throws only for a bug or a cancellation. A database error becomes a result when its SQLSTATE says what to do about it:

| Error | SQLSTATE | What to do |
|---|---|---|
| `Conflict` | `40001` serialization failure, `40P01` deadlock, `23503` foreign-key violation from a concurrent erase, `55P03` lock not available | Retry the operation. |
| `Unavailable` | `08` connection errors, `53` insufficient resources, `57P` operator intervention, `57014` query cancelled or statement timeout, `25006` read-only transaction such as on a standby, or no SQLSTATE at all | The server can't serve now. Retry later. |

Feedback on a run that a concurrent erase or prune removes while the feedback waits for it answers `NotFound`, not `Conflict`, the same as feedback on a run that never existed. The run is gone, so a retry can't succeed.

Any other database error is a bug, and the exception propagates.

A mapped error's `Detail` carries the database's error message: the SQLSTATE and the server's message text. That text doesn't include row values unless the host turns on Npgsql's `IncludeErrorDetail` connection setting. Keep that setting off in production if your errors are logged where personal data shouldn't go.

## What erasure leaves on disk

`EraseAsync` deletes rows; it doesn't scrub storage. [Data lifecycle](data-lifecycle.md) describes what an erase removes. This section is about where the removed data can still be found.

A deleted row's bytes stay in the data files until something overwrites them. `VACUUM`, including autovacuum, only marks the space as reusable; it doesn't clear it, and a checkpoint doesn't either. Old bytes can persist in:

- heap pages, and the TOAST table that holds long text such as graphs and tags;
- the HNSW index;
- the btree indexes, such as the one on `(scope, subject_id)`, which holds the subject id itself.

The write-ahead log, streaming replicas and backups keep the data for their own retention, whatever the store does.

For erasure under data-protection law such as the GDPR, treat data at rest as a storage concern:

- encrypt the storage the database, its WAL archive and its backups live on;
- rotate backups and archived WAL on a schedule that meets your retention promise, because a restore brings erased data back.

`VACUUM FULL` or `pg_repack` rewrites a table and its indexes into new files and releases the old pages to the filesystem. That removes them from PostgreSQL's files, but it is not a guaranteed scrub: the filesystem and the device may keep the released blocks until they are reused. `VACUUM FULL` also locks the table exclusively while it runs.
