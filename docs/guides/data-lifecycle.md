---
title: Data lifecycle
sidebar_position: 2
---

# Data lifecycle

Stored procedures don't live forever. A tenant leaves, a person asks to have their data erased, or procedures stop being used and only take up space. Mnemosyne has two tools for this:

- **Erasure** removes data on request: everything in a scope, or everything about one subject in a scope. It is `EraseAsync`, on `IReasonletMemory` and on the store.
- **Retention** removes data that is old or unused, on a schedule. It is `PruneAsync`, on the store only.

[Scopes and subjects](../concepts/scopes-and-subjects.md) explains what scopes and subjects are. This page covers what each tool removes, what it keeps, and how it behaves alongside other writes.

## Erasure

`EraseAsync` takes a `ReasonletErasure`: a scope, and optionally a subject.

```csharp
using Mnemosyne.NET.Abstractions;

// Everything in the scope: a tenant leaves.
var erased = await memory.EraseAsync(new ReasonletErasure(scope, SubjectId: null), ct);

// Only what is about one person in the scope.
erased = await memory.EraseAsync(new ReasonletErasure(scope, SubjectId: "user-1234"), ct);

if (erased.IsSuccess)
{
    Console.WriteLine($"Removed {erased.Value.Reasonlets} Reasonlets and {erased.Value.Executions} runs.");
}
```

A blank scope, or a subject that is given but blank, answers `Validation`.

### What an erase removes

**By scope**, it removes every Reasonlet in the scope, with every run and piece of feedback.

**By subject**, it removes:

1. the subject's Reasonlets in the scope, those whose provenance names the subject, with every run and piece of feedback on them, other subjects' runs included;
2. then the subject's own runs, with their feedback, on Reasonlets that are kept.

So run records follow their procedure: a run of an erased Reasonlet goes with it, whoever it belongs to. Other subjects' Reasonlets survive.

When an erased Reasonlet has kept versions below it, their `parent_id` is cut, and each becomes the root of a lineage of its own. [Versions and lineage](../concepts/versions.md#what-erasure-does-to-a-lineage) shows what that does to version numbers and ranking.

The result counts the Reasonlets removed and every run removed, each once. A cut parent link is not counted.

### What happens to the counters of kept Reasonlets

Each Reasonlet keeps counters that ranking reads: `runs`, `successes`, `last_executed_at` and `last_success_at`. When an erase by subject removes runs from a Reasonlet it keeps:

- **The removed runs are subtracted** from `runs` and `successes`. The counters are not recounted from the runs left, because they also hold runs whose records were pruned earlier.
- **The times fall back.** When a removed run was the latest run or the latest success, that time falls back to the latest one left.
- **With no run left,** `last_executed_at` falls back to the Reasonlet's creation time, and `last_success_at` to null, meaning unknown, even when `successes` still counts pruned successes. Keeping the stored time would keep the erased subject's own run time.
- **Pruned runs stay counted.** Runs pruned before the erase stay in `runs` and `successes`, because nothing can identify them any more. They are bare integers with no link to a person, so they are anonymous totals, not personal data.
- **A removed run counts with its effective verdict.** A run a person's `Worked` feedback overrode is subtracted as the success or failure that feedback made it, not as the agent or the evaluator saw it. Feedback itself also moves these counters, as described under [What counters keep](#what-counters-keep).

### Transactions and concurrency

The whole erase is one transaction. It covers what exists when it takes its locks: a Reasonlet remembered while the erase runs is not erased by it. So when a subject asks to be erased, stop writing for that subject first, or run the erase again once it has stopped.

An erase can answer `Conflict`. Retry it when it does:

- **A deadlock with a concurrent remember.** An erase takes its locks in the same order as a remember, so normally it waits behind one, but a remember that races its lock walk can still deadlock with it.
- **A deadlock with another erase.** This is rare. It happens when a third erase cuts a lineage between the moment one erase reads the depths of its rows and the moment it locks them, so two erases order the rows they share differently. This is a known limitation, tracked as [issue #7](https://github.com/MarcelRoozekrans/Mnemosyne.NET/issues/7).

A prune never waits for a Reasonlet lock, so it can't deadlock with an erase.

An erase by subject needs the database `TEMPORARY` privilege, which PostgreSQL grants to `PUBLIC` by default. See [Privileges](postgresql.md#privileges).

### What an erase doesn't remove

An erase deletes rows; it doesn't scrub storage. Deleted data can stay in PostgreSQL's data files, its write-ahead log, replicas and backups. [What erasure leaves on disk](postgresql.md#what-erasure-leaves-on-disk) explains where, and what to do about it.

## Retention

`PruneAsync` keeps the store bounded by deleting old run records and Reasonlets nobody uses. The host runs it, for example from a scheduled job; Mnemosyne never prunes on its own.

It is on the store, `ReasonletPostgreSqlStore` and the `IReasonletStore` port, not on `IReasonletMemory`, because pruning is administration, not something an agent does.

### The policy

`PruneAsync` takes a `ReasonletRetention`:

| Property | Meaning |
|---|---|
| `Scope` | The scope to prune. Null prunes every scope. |
| `ExecutionsOlderThan` | Delete run records, with their feedback, older than this. Null keeps every run. |
| `ReasonletsUnusedFor` | Delete Reasonlets that haven't been used for this long. Null keeps every Reasonlet. |

At least one window must be set, and a window that is set must be positive. A policy that breaks either rule, or has an invalid scope, answers `Validation`.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql;

var store = provider.GetRequiredService<ReasonletPostgreSqlStore>();
var pruned = await store.PruneAsync(
    new ReasonletRetention
    {
        ExecutionsOlderThan = TimeSpan.FromDays(90),
        ReasonletsUnusedFor = TimeSpan.FromDays(180),
    },
    ct);
if (pruned.IsSuccess)
{
    Console.WriteLine($"Pruned {pruned.Value.Reasonlets} Reasonlets and {pruned.Value.Executions} runs.");
}
```

The result counts the Reasonlets deleted, and every run deleted: the runs older than the window plus the runs of the deleted Reasonlets, each once.

### What counts as used

- **Used means executed or remembered, not recalled.** Recall is the hot path, and recording it would add a write to every search.
- **Use is judged per lineage.** A Reasonlet is used when it, or any version below it, was remembered or last ran inside the window. A superseded version stays while a later version is in use, and a lineage goes only once its whole subtree is idle.
- **Ancestors stay.** A Reasonlet with another branch below it that is in use is kept. Pruning never cuts a parent link: no kept version loses its parent.
- **`PendingReview` Reasonlets** are judged like `Active` ones.
- **A deleted Reasonlet takes its runs, feedback and metrics with it.** Its runs are counted in the result.

### How a prune runs

- **One clock.** It reads the database clock once and measures both windows back from it, so a long prune has a fixed target and ends.
- **Runs first.** Old run records go first, then idle Reasonlets.
- **One list.** It lists the idle Reasonlets once, then works through the list. A Reasonlet that becomes used after the list is read is kept. One that becomes idle while the prune runs, for example because an erase removed the version that kept it in use, waits for the next prune.
- **Small batches.** Work is done in batches, each its own transaction at READ COMMITTED. A batch deletes at most 1,000 rows of each kind itself: up to 1,000 Reasonlets with up to 1,000 of their runs. The metrics and feedback rows that cascade from them come on top. When a batch's Reasonlets hold more than 1,000 runs, those runs go first, in batches of their own.
- **Retries.** A batch that hits a deadlock or a serialization failure is retried up to 3 times, after a short jittered wait that grows with each try: 50 ms times the try number, give or take half. After that the prune answers `Conflict`. Any other failure ends the prune at once.
- **Re-checked before deleting.** A batch locks its Reasonlets, then their runs, then their metrics, and checks again that they are still idle. A Reasonlet that ran or gained a version in the meantime survives and is not counted. A run or remember that arrives after the locks waits, then answers `NotFound`, as after an erase.
- **Never waits for a Reasonlet.** A batch locks Reasonlets with `SKIP LOCKED`: it skips one that another transaction holds, for example one being run or getting a new version, and keeps it and all its ancestors until a later prune. So a prune doesn't deadlock with other writers.
- **Size limit.** The idle list is read into one array, so a single prune can handle at most about 60 million idle Reasonlets. On a store that size, prune by scope, or more often.
- **Safe to rerun.** A failed prune leaves the batches it already committed deleted, and its result carries no counts. Running it again deletes only what is still older than its new cutoff. The [telemetry](telemetry.md) counts every committed batch, including when the prune fails.

## What counters keep

Pruning deletes run records, but a pruned run still happened. So:

- **`runs` and `successes` keep pruned runs.** A Reasonlet's track record survives the pruning of its old runs, and ranking keeps using it.
- **`last_executed_at` and `last_success_at` never change on a prune.** They keep the time of a run whose record is gone.
- **An erase or a verdict lowers them.** An erase subtracts the runs it removes, as described under [What happens to the counters of kept Reasonlets](#what-happens-to-the-counters-of-kept-reasonlets). Feedback whose `Worked` verdict turns a success into a failure lowers `successes` by one, and when that run held `last_success_at`, the time falls back to the latest success left, or to null when none is left. Feedback that turns a failure into a success raises `successes` by one and moves `last_success_at` forward to that run's time when it is later. Neither touches `runs` or `last_executed_at`. `last_executed_at` is what retention reads, and it is never null.

The counters are bare numbers with no link to a person, which is why keeping them after a prune or an erase is safe.
