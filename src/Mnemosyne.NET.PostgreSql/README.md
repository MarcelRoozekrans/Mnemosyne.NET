# Mnemosyne.NET.PostgreSql

PostgreSQL + pgvector store for Reasonlets: an `IReasonletStore` that keeps procedures, their runs and their feedback, and finds the nearest procedure to an embedding.

## Requirements

- **PostgreSQL 15 or later.** Every foreign key carries the scope, and the parent link uses `ON DELETE SET NULL (parent_id)`, a form PostgreSQL 15 introduced. `MigrateAsync` fails with a clear error on an older server.
- **pgvector 0.8.0 or later.** Search uses iterative HNSW scans, which arrived in 0.8.0; `MigrateAsync` fails with a clear error on an older pgvector.

### Connections and privileges

- **`search_path`.** The store's tables are always named in full, but the `vector` type and its operators are not: they resolve through the connection's `search_path`. The schema the `vector` extension lives in (normally `public`) must be on the `search_path` of every connection the data source opens.
- **`current_schema()`.** ZeroAlloc.ORM records the applied migrations in a `__zaorm_migrations` table in the connection's `current_schema()`, the first existing schema on the `search_path`. Keep it the same across runs. If it changes, `MigrateAsync` finds no history there and runs the migrations again, which fails because the tables exist.
- **Privileges.** The role that runs `MigrateAsync` needs `CREATE` on the database, to create the `mnemosyne` schema, and `CREATE` on `current_schema()`, for the history table. The first migration runs `CREATE EXTENSION IF NOT EXISTS vector`, and `vector` is not a trusted extension, so that needs a superuser. A DBA can instead create the extension once, ahead of time; the migration then finds it and needs no superuser.

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

All tables live in the fixed `mnemosyne` schema. It is not configurable, and every statement names it in full. The `search_path` still matters for the `vector` type and the migration history; see [Connections and privileges](#connections-and-privileges).

The first run also builds the HNSW index with `HnswM` and `HnswEfConstruction` and records them. A later run with different values fails with a validation error naming both, because the options apply only when the index is built; to change them, rebuild the index yourself.

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
- A kept Reasonlet that loses executions to the erase gets its run counts recounted from the executions left.
- The whole erase is one transaction. It covers what exists when it takes its locks: a Reasonlet remembered while the erase runs is not erased by it, so run the erase again once the subject has stopped writing. A deadlock with a concurrent remember answers `Conflict`; retry it.

### What erasure leaves on disk

`EraseAsync` deletes rows; it does not scrub storage. A deleted row's bytes stay in the data files until something overwrites them. `VACUUM`, including autovacuum, only marks the space as reusable; it does not clear it, and a checkpoint does not either. Old bytes can persist in:

- heap pages, and the TOAST table that holds long text such as graphs and tags;
- the HNSW index;
- the btree indexes, such as `(scope, subject_id)`, which holds the subject id itself.

The write-ahead log, streaming replicas and backups keep the data for their own retention, whatever the store does. For GDPR erasure, treat data at rest as a storage concern:

- encrypt the storage or disk the database, its WAL archive and its backups live on;
- rotate backups and archived WAL on a schedule that meets your retention promise, because a restore brings erased data back.

`VACUUM FULL` or `pg_repack` rewrites a table and its indexes into new files and releases the old pages to the filesystem. That removes them from PostgreSQL's files, but it is not a guaranteed scrub: the filesystem and the device may keep the released blocks until they are reused. `VACUUM FULL` also locks the table exclusively while it runs.

## Searching

`SearchAsync` returns up to `limit` candidates of one scope, sorted exactly by distance; HNSW recall is approximate, so a near neighbour can occasionally be missed. It uses iterative HNSW scans, so a scope with a small share of the table usually still gets its full limit. The scan stops after `hnsw.max_scan_tuples` index tuples (20,000 by default), though, and, like that cap, `hnsw.scan_mem_multiplier` can end it early once the scan uses its memory budget, so a very small scope in a very large table can come back with fewer candidates than the limit. Candidates at exactly the same distance come back in an unspecified order.

The embedding must have a nonzero, finite norm: cosine similarity is undefined for an all-zero vector, so `SearchAsync` and `RememberAsync` answer a validation error for one.

Each candidate's metrics (runs, successes, last success) come from a counter row the store keeps in step with every execution it records, so search does not aggregate executions.
