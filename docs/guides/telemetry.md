---
title: Telemetry
sidebar_position: 3
---

# Telemetry

Mnemosyne emits OpenTelemetry traces and metrics for every store operation and every recall. It uses only the in-box `System.Diagnostics` types, so it adds no dependency and stays native AOT clean. Your host decides whether to collect the signals and where to send them.

## Turn it on

The store and the recall facade share one `ActivitySource` and one `Meter`, both named `Mnemosyne.NET`. The names are constants on `MnemosyneTelemetry`, in `Mnemosyne.NET.Abstractions`:

| Constant | Value |
|---|---|
| `MnemosyneTelemetry.ActivitySourceName` | `Mnemosyne.NET` |
| `MnemosyneTelemetry.MeterName` | `Mnemosyne.NET` |

Add both to your OpenTelemetry setup. With the `OpenTelemetry.Extensions.Hosting` package:

```csharp
using Mnemosyne.NET.Abstractions;

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(MnemosyneTelemetry.ActivitySourceName))
    .WithMetrics(metrics => metrics.AddMeter(MnemosyneTelemetry.MeterName));
```

Add the exporters you already use, such as OTLP, as usual.

No activity is created without a listener, and the store builds no tag for one, so a host that doesn't collect the signals pays very little for them.

## Operations

Every store method is one operation, and so is each recall. The operation name appears in the activity name, as `mnemosyne.<operation>`, and in the `mnemosyne.operation` tag on the metrics.

| Operation | Emitted by | What it is |
|---|---|---|
| `migrate` | store | `MigrateAsync` |
| `remember` | store | storing a capture |
| `get` | store | reading one Reasonlet |
| `execution.append` | store | recording a run |
| `feedback.append` | store | recording feedback on a run |
| `search` | store | finding the nearest candidates |
| `erase` | store | `EraseAsync` |
| `prune` | store | `PruneAsync` |
| `recall` | facade | `RecallAsync` |

The facade instruments recall only. Remember, recording runs and feedback, and erase are instrumented by the store operations they call, and wrapping them again would count each twice. A recall's `mnemosyne.recall` activity is the parent of the `mnemosyne.search` activity the store emits for it.

## Activities

There is one activity per operation, with these tags:

| Activity | Tags |
|---|---|
| `mnemosyne.migrate` | none |
| `mnemosyne.remember` | on success: `mnemosyne.capture_kind` (`NewReasonlet`, `ExecutionOnly` or `NewVersion`), `mnemosyne.reasonlet.id`, `mnemosyne.reasonlet.version` |
| `mnemosyne.get` | none |
| `mnemosyne.execution.append` | none |
| `mnemosyne.feedback.append` | none |
| `mnemosyne.search` | `mnemosyne.search.limit`, always; on success `mnemosyne.search.returned`, and `mnemosyne.search.top_similarity` when at least one candidate is returned |
| `mnemosyne.erase` | `mnemosyne.erase.by` (`scope` or `subject`), always; on success `mnemosyne.erased.reasonlets` and `mnemosyne.erased.executions` |
| `mnemosyne.prune` | `mnemosyne.pruned.reasonlets` and `mnemosyne.pruned.executions`, always; they count the committed batches, including when the prune fails |
| `mnemosyne.recall` | see below |

### The recall activity

| Tag | Set when |
|---|---|
| `mnemosyne.recall.top` | always: the query's `Top` |
| `mnemosyne.recall.intent_filtered` | always, a bool: whether recall filtered on an intent. A recall that fails before the intent is resolved reports whether the query carried one. |
| `mnemosyne.recall.intent_fallback` | only when the intent extractor was down and recall searched every intent: `true` |
| `mnemosyne.recall.candidates` | after the search: how many candidates the store returned |
| `mnemosyne.recall.returned` | on success: how many matches recall returned |
| `mnemosyne.recall.chosen.id` | on success, when at least one match is returned: the first match's id |
| `mnemosyne.recall.chosen.version` | on success, when at least one match is returned: the first match's version |
| `mnemosyne.recall.chosen.score` | on success, when at least one match is returned: the first match's total score |

The candidate and returned counts show how much the [cut-off](../concepts/ranking.md#the-cut-off) drops, which helps when you tune it for your embedding model.

### Failures on an activity

- **A failure result** sets `error.type` to the error code's name, such as `Conflict`, and the activity status to Error.
- **An exception**, cancellation included, adds an `exception` event that carries `exception.type` only. It sets `error.type` to the exception's full type name and the status to Error. The message and the stack trace are never recorded, because a database error can quote stored values and a facade error can quote the question.
- **An unmapped database error.** When the exception is an `NpgsqlException` with a SQLSTATE, such as `XX000`, the store's activity also gets `db.response.status_code` set to it, the OpenTelemetry database convention's name. A SQLSTATE is a five-character code from a fixed list, so it carries no user data, and it tells you what failed where the message can't.

## Metrics

| Instrument | Type and unit | Tags | Emitted by |
|---|---|---|---|
| `mnemosyne.operations` | counter, `{operation}` | `mnemosyne.operation`, `mnemosyne.outcome` | store and facade |
| `mnemosyne.operation.duration` | histogram, seconds | `mnemosyne.operation` | store and facade |
| `mnemosyne.reasonlets.remembered` | counter, `{capture}` | `mnemosyne.capture_kind` | store, on each successful remember |
| `mnemosyne.search.returned` | histogram, `{candidate}` | none | store, on each successful search |
| `mnemosyne.erased.reasonlets` | counter, `{reasonlet}` | none | store, on each successful erase |
| `mnemosyne.erased.executions` | counter, `{execution}` | none | store, on each successful erase |
| `mnemosyne.pruned.reasonlets` | counter, `{reasonlet}` | none | store, for each committed prune batch |
| `mnemosyne.pruned.executions` | counter, `{execution}` | none | store, for each committed prune batch |
| `mnemosyne.recall.returned` | histogram, `{match}` | none | facade, on each successful recall |
| `mnemosyne.recall.empty` | counter, `{recall}` | none | facade, on each successful recall that returned nothing |

`mnemosyne.operations` and `mnemosyne.operation.duration` come from both the store and the facade. The two meters share one name and the instruments match exactly, so each aggregates as one instrument, and `recall` sits next to the store's operations.

### Outcomes

`mnemosyne.outcome` on `mnemosyne.operations` is one of:

| Outcome | Meaning |
|---|---|
| `ok` | The operation succeeded. |
| `Validation`, `NotFound`, `Conflict` or `Unavailable` | The operation answered a failure result with that error code. |
| `exception` | An exception propagated, cancellation included. |

A rising `Conflict` count on `erase` or `prune` means concurrent writes are colliding; both are safe to retry. A rising `Unavailable` count means the database can't serve. [Data lifecycle](data-lifecycle.md) and [the store's errors](postgresql.md#errors) explain when each happens.

## Privacy

No signal carries user data: no question, intent, name, graph, tags, scope, subject or run id, and no error or exception message. Reasonlet ids and versions are opaque, so they appear, and so do scores.

Scope and subject are left out on purpose. A scope often names a tenant and a subject names a person, and telemetry is exported, sampled and kept in other systems with their own retention. If you need a breakdown by tenant, add it in your own wrapper, where you decide what may leave your application.
