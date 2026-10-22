# Mnemosyne.NET.PostgreSql

PostgreSQL + pgvector store for Reasonlets: an `IReasonletStore` that keeps procedures, their runs and their feedback, and finds the nearest procedure to an embedding.

## Requirements

- **PostgreSQL 15 or later.** Every foreign key carries the scope, and the parent link uses `ON DELETE SET NULL (parent_id)`, a form PostgreSQL 15 introduced. `MigrateAsync` fails with a clear error on an older server.
- **pgvector 0.8.0 or later.** Search uses iterative HNSW scans, which arrived in 0.8.0; `MigrateAsync` fails with a clear error on an older pgvector.

### Connections and privileges

- **`search_path`.** The store's tables are always named in full, but the `vector` type and its operators are not: they resolve through the connection's `search_path`. The schema the `vector` extension lives in (normally `public`) must be on the `search_path` of every connection the data source opens.
- **`current_schema()`.** ZeroAlloc.ORM records the applied migrations in a `__zaorm_migrations` table in the connection's `current_schema()`, the first existing schema on the `search_path`. Keep it the same across runs. If it changes, `MigrateAsync` finds no history there and runs the migrations again, which fails because the tables exist.
- **Privileges.** The role that runs `MigrateAsync` needs `CREATE` on the database, to create the `mnemosyne` schema, and `CREATE` on `current_schema()`, for the history table. The first migration runs `CREATE EXTENSION IF NOT EXISTS vector`, and `vector` is not a trusted extension, so that needs a superuser. A DBA can instead create the extension once, ahead of time; the migration then finds it and needs no superuser. The role the store runs as needs the `TEMPORARY` privilege on the database, which PostgreSQL grants to `PUBLIC` by default: an erase by subject keeps the executions it removes in a temporary table for the length of its transaction.

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
- A kept Reasonlet that loses executions to the erase has them subtracted from its run counts. The counts are not recounted from the executions left, because they also hold runs whose records were pruned. When a removed run was the latest run or latest success, that time falls back to the latest one left. When none is left, `last_executed_at` falls back to the Reasonlet's `created_at`, and `last_success_at` to null, meaning unknown, even when `successes` still counts pruned successes. Keeping the stored time would keep the erased subject's own run time.
- The whole erase is one transaction. It covers what exists when it takes its locks: a Reasonlet remembered while the erase runs is not erased by it, so run the erase again once the subject has stopped writing. A deadlock with a concurrent remember answers `Conflict`; retry it. An erase can also deadlock, rarely, with another erase: this happens when a third erase cuts a lineage between the moment one of them reads its depths and the moment it takes its locks, so the two order the rows they share by different depths. It answers `Conflict` too; retry it. A prune never waits for a Reasonlet lock, so it cannot deadlock with an erase.
- Counts of runs that were pruned before the erase stay in `runs` and `successes`, because nothing can identify those runs any more. They are bare integers with no link to a person, so they are anonymous totals, not personal data.

### What erasure leaves on disk

`EraseAsync` deletes rows; it does not scrub storage. A deleted row's bytes stay in the data files until something overwrites them. `VACUUM`, including autovacuum, only marks the space as reusable; it does not clear it, and a checkpoint does not either. Old bytes can persist in:

- heap pages, and the TOAST table that holds long text such as graphs and tags;
- the HNSW index;
- the btree indexes, such as `(scope, subject_id)`, which holds the subject id itself.

The write-ahead log, streaming replicas and backups keep the data for their own retention, whatever the store does. For GDPR erasure, treat data at rest as a storage concern:

- encrypt the storage or disk the database, its WAL archive and its backups live on;
- rotate backups and archived WAL on a schedule that meets your retention promise, because a restore brings erased data back.

`VACUUM FULL` or `pg_repack` rewrites a table and its indexes into new files and releases the old pages to the filesystem. That removes them from PostgreSQL's files, but it is not a guaranteed scrub: the filesystem and the device may keep the released blocks until they are reused. `VACUUM FULL` also locks the table exclusively while it runs.

## Retention

`PruneAsync` keeps the store bounded. The host runs it, for example from a scheduled job; the store never prunes on its own. `IReasonletMemory` does not expose it, because pruning is administration. It takes a `ReasonletRetention` policy:

| Property | Meaning |
|---|---|
| `Scope` | The scope to prune. Null prunes every scope. |
| `ExecutionsOlderThan` | Delete run records, together with their feedback, older than this. Null keeps all runs. |
| `ReasonletsUnusedFor` | Delete Reasonlets that have not been used for this long. Null keeps all Reasonlets. |

A policy with both windows null, a window that is not positive, or an invalid scope answers a validation error. The result counts the Reasonlets and executions deleted.

```csharp
using Mnemosyne.NET.Abstractions;

var pruned = await store.PruneAsync(
    new ReasonletRetention
    {
        ExecutionsOlderThan = TimeSpan.FromDays(90),
        ReasonletsUnusedFor = TimeSpan.FromDays(180),
    },
    CancellationToken.None);
```

### What counts as used

- "Used" means executed or remembered, not recalled. Recall is the hot path, and recording it would add a write to every search.
- Use is judged per lineage. A Reasonlet is used when it, or any version below it, was remembered or last ran inside the window. A superseded version stays while a later version is in use, and a lineage goes only once its whole subtree is idle.
- An ancestor stays when another branch below it is in use. Pruning never cuts a parent link: no kept version loses its parent.
- `PendingReview` Reasonlets are judged like `Active` ones.
- A deleted Reasonlet takes its runs, feedback and metrics with it. Its runs are counted in the result.

### How a prune runs

- It reads the database clock once and measures both windows back from it, so a long prune has a fixed target and ends.
- It lists the idle Reasonlets once, then works through the list. A Reasonlet that becomes used after the list is read is kept, as described below. One that becomes idle while the prune runs, for example because an erase removed the version that kept it, waits for the next prune.
- Run records go first, then idle Reasonlets.
- Work is done in batches, each its own transaction at READ COMMITTED. A batch deletes at most 1,000 rows of each kind itself: up to 1,000 Reasonlets with up to 1,000 of their runs. The metrics and feedback rows that cascade from them come on top. When a batch's Reasonlets hold more than 1,000 runs, those runs go first, in batches of their own.
- A batch that hits a deadlock or serialization failure is retried up to three times, then the prune answers `Conflict`. Any other failure ends the prune at once.
- Before a batch deletes Reasonlets it locks them, then their runs, then their metrics, and checks again that they are still idle. A Reasonlet that ran or gained a version in the meantime survives and is not counted. An append or remember that arrives after the locks waits, then answers `NotFound`, as after an erase.
- A batch never waits for a Reasonlet lock. It skips a Reasonlet that another transaction holds, for example one being run or getting a new version, and keeps it and all its ancestors until a later prune. So a prune never deadlocks with another writer.
- The idle list is read into one array, so a single prune can handle at most about 60 million idle Reasonlets. On a store that size, prune by scope, or more often.
- A failed prune leaves the batches it already committed deleted, and its result carries no counts. It is safe to run again: a rerun deletes only what is still older than its new cutoff. The telemetry counts every committed batch, including on the failure path.

### Counters survive pruning

- A pruned run still happened, so the `runs` and `successes` counters in a Reasonlet's metrics keep it.
- Pruning never changes `last_executed_at` or `last_success_at`: they keep the time of a run whose record is gone.
- An erase can lower them, or set `last_success_at` to null, as described under [Erasure](#erasure). `last_executed_at` is what retention reads, and it is never null.

## Telemetry

Every store operation emits OpenTelemetry signals through one `ActivitySource` and one `Meter`. Both are named `Mnemosyne.NET`; the names are constants on `MnemosyneTelemetry` in Abstractions. A host enables them in its own SDK setup:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(MnemosyneTelemetry.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(MnemosyneTelemetry.MeterName));
```

Nothing is created without a listener, so an unwired store pays almost nothing. The instrumentation uses only the in-box `System.Diagnostics` types and is native AOT clean.

### Activities

There is one activity per operation.

| Activity | Tags |
|---|---|
| `mnemosyne.migrate` | |
| `mnemosyne.remember` | on success: `mnemosyne.capture_kind` (`NewReasonlet`, `ExecutionOnly` or `NewVersion`), `mnemosyne.reasonlet.id`, `mnemosyne.reasonlet.version` |
| `mnemosyne.get` | |
| `mnemosyne.execution.append` | |
| `mnemosyne.feedback.append` | |
| `mnemosyne.search` | `mnemosyne.search.limit`, always; on success `mnemosyne.search.returned`, and `mnemosyne.search.top_similarity` only when a candidate is returned |
| `mnemosyne.erase` | `mnemosyne.erase.by` (`scope` or `subject`), always; on success `mnemosyne.erased.reasonlets` and `mnemosyne.erased.executions` |
| `mnemosyne.prune` | `mnemosyne.pruned.reasonlets`, `mnemosyne.pruned.executions`, always; they count the committed batches, including when the prune fails |

- A failure result sets `error.type` to the `MnemosyneErrorCode` name and the status to Error.
- When an exception propagates, cancellation included, the activity gets an `exception` event with `exception.type` only, `error.type` is set to the full exception type name, and the status is Error. The message and stack trace are never recorded, because an unmapped PostgreSQL error can quote stored values.

### Metrics

| Instrument | Type | Tags |
|---|---|---|
| `mnemosyne.operations` | counter | `mnemosyne.operation`, `mnemosyne.outcome` (`ok`, the error code, or `exception`) |
| `mnemosyne.operation.duration` | histogram, seconds | `mnemosyne.operation` |
| `mnemosyne.reasonlets.remembered` | counter | `mnemosyne.capture_kind` |
| `mnemosyne.search.returned` | histogram | none |
| `mnemosyne.erased.reasonlets`, `mnemosyne.erased.executions` | counters | none |
| `mnemosyne.pruned.reasonlets`, `mnemosyne.pruned.executions` | counters | none |

### Privacy

No signal carries user data: no question, intent, name, graph, tags, scope, subject or run id. Reasonlet ids and versions are opaque, so they are allowed. Scope often names a tenant and subject names a person, and telemetry is exported, sampled and kept elsewhere. A host that needs tenant breakdowns can add them in its own wrapper.

## Searching

`SearchAsync` returns up to `limit` candidates of one scope, sorted exactly by distance; HNSW recall is approximate, so a near neighbour can occasionally be missed. It uses iterative HNSW scans, so a scope with a small share of the table usually still gets its full limit. The scan stops after `hnsw.max_scan_tuples` index tuples (20,000 by default), though, and, like that cap, `hnsw.scan_mem_multiplier` can end it early once the scan uses its memory budget, so a very small scope in a very large table can come back with fewer candidates than the limit. Candidates at exactly the same distance come back in an unspecified order.

The embedding must have a nonzero, finite norm: cosine similarity is undefined for an all-zero vector, so `SearchAsync` and `RememberAsync` answer a validation error for one.

Each candidate's metrics (runs, successes, last success) come from a counter row the store keeps in step with every execution it records, so search does not aggregate executions.
