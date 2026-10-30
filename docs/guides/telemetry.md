---
title: Telemetry
sidebar_position: 3
---

# Telemetry

Mnemosyne emits OpenTelemetry traces and metrics for every store operation, every recall and every call to an evaluator or a capture guard. It uses only the in-box `System.Diagnostics` types, so it adds no dependency and stays native AOT clean. Your host decides whether to collect the signals and where to send them.

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

Every store method is one operation, and so is each recall, each judge call and each guard call. The operation name appears in the activity name, as `mnemosyne.<operation>`, and in the `mnemosyne.operation` tag on the metrics.

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
| `evaluate` | facade | one call to the registered `IReasonletEvaluator` |
| `guard` | facade | one call to the registered `ICaptureGuard` |

The facade instruments recall, judge and guard calls only. Remember, recording runs and feedback, and erase are instrumented by the store operations they call, and wrapping them again would count each twice. A judge call happens inside a remember or `RecordExecutionAsync`, before the store records the run, and is not a store operation, so it is counted on its own. A guard call happens inside a remember, after the capture is validated and before anything else touches it, and is counted on its own the same way. A recall's `mnemosyne.recall` activity is the parent of the `mnemosyne.search` activity the store emits for it.

## Activities

There is one activity per operation, with these tags:

| Activity | Tags |
|---|---|
| `mnemosyne.migrate` | none |
| `mnemosyne.remember` | on success: `mnemosyne.capture_kind` (`NewReasonlet`, `ExecutionOnly` or `NewVersion`), `mnemosyne.reasonlet.id`, `mnemosyne.reasonlet.version`, and `mnemosyne.verdict_source` of the run it records: `evaluator` when the capture comes with an evaluator's verdict, else `agent` |
| `mnemosyne.get` | none |
| `mnemosyne.execution.append` | `mnemosyne.verdict_source`, always: `evaluator` when the run comes with an evaluator's verdict, else `agent` |
| `mnemosyne.feedback.append` | on success, when the feedback gives a `Worked` verdict: `mnemosyne.verdict_source` `feedback`, whether or not it changes the run's verdict |
| `mnemosyne.search` | `mnemosyne.search.limit`, always; on success `mnemosyne.search.returned`, and `mnemosyne.search.top_similarity` when at least one candidate is returned |
| `mnemosyne.erase` | `mnemosyne.erase.by` (`scope` or `subject`), always; on success `mnemosyne.erased.reasonlets` and `mnemosyne.erased.executions` |
| `mnemosyne.prune` | `mnemosyne.pruned.reasonlets` and `mnemosyne.pruned.executions`, always; they count the committed batches, including when the prune fails |
| `mnemosyne.recall` | see below |
| `mnemosyne.evaluate` | see below |
| `mnemosyne.guard` | see below |

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

### The evaluate activity

| Tag | Set when |
|---|---|
| `mnemosyne.evaluator` | the judge answered with a valid verdict: the judge's name, from `ReasonletEvaluation.Evaluator` |
| `mnemosyne.evaluation.success` | the judge answered with a valid verdict: its success, a bool |
| `mnemosyne.evaluation.score` | the judge answered with a valid verdict: its score |

The activity records how the judge call itself ended. A judge that is down, because it threw, timed out or answered `Unavailable`, shows as a failure here, even though the remember or the run record then succeeds with the agent's verdict. A timeout, whether the judge's own or `MnemosyneOptions.EvaluationTimeout`, is reported as `Unavailable`. A failed judge call has no verdict to name a judge from, so it carries no `mnemosyne.evaluator` tag, on the activity or on the metrics.

An answer that is blank or longer than 32,000 code points is not judged, so no `mnemosyne.evaluate` activity starts for it. With an evaluator registered, it counts once on `mnemosyne.evaluations` with outcome `skipped` and no other tag, and nothing else: no duration and no `evaluate` operation.

### The guard activity

| Tag | Set when |
|---|---|
| `mnemosyne.guard` | the guard answered with a valid verdict: the guard's name, from `CaptureVerdict.Guard` |
| `mnemosyne.guard.blocked` | the guard answered with a valid verdict: whether it blocked the capture, a bool |
| `mnemosyne.guard.detector` | the guard blocked the capture: the detector's name, from `CaptureVerdict.Detector` |

A block also adds a `mnemosyne.capture.blocked` event to the activity. It carries the `mnemosyne.guard` and `mnemosyne.guard.detector` tags and nothing else, so a trace shows which detector dropped a capture without showing what it found.

The activity records how the guard call itself ended. A guard that is down, because it threw, timed out or answered `Unavailable`, shows as a failure here, and the remember then fails with `Unavailable` too, since a capture that was not inspected is not stored. A timeout, whether the guard's own or `MnemosyneOptions.GuardTimeout`, is reported as `Unavailable`. A failed guard call has no verdict to name a guard from, so it carries no `mnemosyne.guard` tag, on the activity or on the metrics. A block is not a failure: the guard did its job, so the activity status stays unset and the operation outcome is `ok`.

A remember without a registered guard starts no `mnemosyne.guard` activity.

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
| `mnemosyne.evaluations` | counter, `{evaluation}` | `mnemosyne.evaluator` when the judge answered, `mnemosyne.outcome` | facade, on each judge call, and on each answer it skips as unusable |
| `mnemosyne.evaluation.duration` | histogram, seconds | `mnemosyne.evaluator` when the judge answered | facade, on each judge call |
| `mnemosyne.guard.inspections` | counter, `{inspection}` | `mnemosyne.guard` when the guard answered, `mnemosyne.outcome` | facade, on each guard call |
| `mnemosyne.guard.duration` | histogram, seconds | `mnemosyne.guard` when the guard answered | facade, on each guard call |

`mnemosyne.operations` and `mnemosyne.operation.duration` come from both the store and the facade. The two meters share one name and the instruments match exactly, so each aggregates as one instrument, and `recall`, `evaluate` and `guard` sit next to the store's operations.

### Outcomes

`mnemosyne.outcome` on `mnemosyne.operations`, `mnemosyne.evaluations` and `mnemosyne.guard.inspections` is one of:

| Outcome | Meaning |
|---|---|
| `ok` | The operation succeeded. |
| `Validation`, `NotFound`, `Conflict` or `Unavailable` | The operation answered a failure result with that error code. |
| `Blocked` | Never a metric outcome. It is the code the remember call returns when a guard blocks a capture; the block counts as `blocked` on `mnemosyne.guard.inspections` and `ok` on `mnemosyne.operations`. |
| `exception` | An exception propagated, cancellation included. |
| `allowed` or `blocked` | On `mnemosyne.guard.inspections` only: the guard answered with a valid verdict that let the capture through or stopped it. The shared `mnemosyne.operations` records `ok` for either, since the guard answered. |
| `skipped` | On `mnemosyne.evaluations` only: the answer was blank or too long, so the judge was not called and the run kept the agent's verdict. |

A rising `Conflict` count on `erase` or `prune` means concurrent writes are colliding; both are safe to retry. A rising `Unavailable` count means the database can't serve. On `evaluate`, a rising `Unavailable` or `exception` count means the judge is failing; while it is down, runs are stored with the agent's verdict. A `Validation`, `NotFound` or `Conflict` outcome on `evaluate` means the evaluator adapter has a bug: the facade throws `InvalidOperationException` for it. On `guard`, a rising `Unavailable` or `exception` count means the guard is failing, and remembers are failing with it; a rising `blocked` count on `mnemosyne.guard.inspections` is the guard doing its work, and the `mnemosyne.guard.detector` tag on the activity says which detector. Any outcome on `guard` other than `Unavailable`, `exception`, `ok` or `blocked`, which includes an error code of `Blocked`, means the guard adapter has a bug: the facade throws `InvalidOperationException` for it. [Data lifecycle](data-lifecycle.md) and [the store's errors](postgresql.md#errors) explain when each happens.

## Privacy

No signal carries user data: no question, answer, intent, name, graph, tags, scope, subject or run id, and no error or exception message. Reasonlet ids and versions are opaque, so they appear, and so do scores. An evaluator's name appears too: it names the judge, such as `llm-judge`, and nothing about the run. A guard's name and its detector's appear the same way, such as `redactor` and `pii.email`: they name the check, not the text it matched, and a guard's own error text never reaches a signal or the error the facade returns.

Scope and subject are left out on purpose. A scope often names a tenant and a subject names a person, and telemetry is exported, sampled and kept in other systems with their own retention. If you need a breakdown by tenant, add it in your own wrapper, where you decide what may leave your application.
