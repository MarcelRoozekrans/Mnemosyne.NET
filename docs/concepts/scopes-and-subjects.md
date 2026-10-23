---
title: Scopes and subjects
sidebar_position: 3
---

# Scopes and subjects

Two identifiers decide whose data a Reasonlet is. The **scope** keeps tenants apart. The **subject** names the person a record is about, so that person's data can be erased.

## Scopes

Every Reasonlet, every run and every piece of feedback belongs to exactly one scope. A scope is a string you choose, wrapped in `ReasonletScope`:

```csharp
var scope = new ReasonletScope("tenant-42");
```

What a scope stands for is up to you. It is usually a tenant, but it can be an application or a single user. Pick the boundary that procedures must never cross.

A scope isolates completely:

- **Recall searches one scope.** A query names its scope, and only Reasonlets in that scope are candidates.
- **Lookups stay inside the scope.** Recording a run, adding feedback or naming a parent works only on records in the same scope. A record that exists only in another scope answers `NotFound`, exactly as if it didn't exist.
- **Lineages stay inside the scope.** A version's parent is always in its own scope.

A scope must not be blank. Mnemosyne doesn't check who may use which scope: your application decides that before it calls Mnemosyne.

## Subjects

A scope often holds procedures captured from many people's sessions. When one of those people asks to have their data erased, you need to find their records. That is what the subject is for.

The subject is the `SubjectId` on a capture's or a run's `ReasonletProvenance`:

```csharp
var provenance = new ReasonletProvenance
{
    Source = "support-agent",
    SubjectId = "user-1234",
};
```

Set it whenever a record is about a person, for example because the question or the graph holds something they said. It is optional; leave it out for records that are not about anyone.

## Erasure

`EraseAsync` erases by scope, or by subject within a scope.

```csharp
// Everything in the scope: every Reasonlet, run and piece of feedback.
await memory.EraseAsync(new ReasonletErasure(scope, SubjectId: null), ct);

// Only what belongs to one person in the scope.
await memory.EraseAsync(new ReasonletErasure(scope, SubjectId: "user-1234"), ct);
```

**By scope** removes everything in the scope. Use it when a tenant leaves.

**By subject** removes that subject's data from the scope and keeps everyone else's:

- the Reasonlets whose provenance names the subject, with every run and piece of feedback on them, including other people's runs of them;
- the subject's own runs of Reasonlets that are kept.

When an erased Reasonlet had versions below it that are kept, their link to it is cut, and each becomes the root of its own lineage. [Versions and lineage](versions.md#what-erasure-does-to-a-lineage) shows what that does to versions and ranking.

The result counts the Reasonlets and runs removed. The whole erase runs in one transaction.

### What an erase leaves behind

- **Counters.** A Reasonlet's run and success counts include runs whose records were pruned earlier. An erase subtracts the runs it removes, but it can't find runs that were already pruned, so those stay in the counts. They are bare numbers with no link to a person.
- **Data at rest.** An erase deletes rows; it doesn't scrub the disk. Deleted data can stay in PostgreSQL's data files, its write-ahead log, replicas and backups until they are overwritten or rotated. For erasure requests under data-protection law, encrypt the storage and rotate backups on a schedule that meets your retention promise. The [PostgreSQL store README](https://github.com/MarcelRoozekrans/Mnemosyne.NET/tree/main/src/Mnemosyne.NET.PostgreSql#what-erasure-leaves-on-disk) covers this in detail.

## Telemetry stays clean

Scopes often name tenants and subjects name people, and telemetry is exported and kept elsewhere. So no activity or metric from Mnemosyne carries a scope, a subject, a question or an intent. Reasonlet ids and versions are opaque, so they do appear.
