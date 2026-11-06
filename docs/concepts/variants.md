---
title: Variants
sidebar_position: 7
---

# Variants

A variant is one version of a procedure, seen next to the other versions of its lineage. This page says what Mnemosyne 1.0 ships for variants, what it leaves to the host, and which direction comes after 1.0.

## What a variant is

When an agent improves a procedure, Mnemosyne stores the improvement as a new version with the old one as its parent. Versions that descend from one root form a lineage, and every version in it is a variant of the others. Two versions of the same parent are siblings, the common case, and each is a variant of the other. [Versions and lineage](versions.md) describes how a version is made and numbered.

## What 1.0 ships

- **A lineage id on every result.** `ReasonletCandidate`, which a store supplies, and `ReasonletMatch`, which a recall returns, both carry a `LineageId`: the id of the root of the version tree. All variants of one procedure report the same one. [The lineage id](versions.md#the-lineage-id) has the rules.
- **A selector port.** `IReasonletSelector` ranks the candidates a store returns, and the default `ReasonletSelector` ranks them by similarity, success rate and freshness. A host that registers its own selector before `AddMnemosyne` replaces it. [Ranking](ranking.md) has the formula.
- **Opaque version labels.** A label such as `1.2` is for display and for ordering inside one lineage. It is not a value to parse. [Compatibility](compatibility.md#version-labels) says why.

The default ranking treats every version as a match of its own. Two variants of one procedure can both appear in a recall, each with its own score, and nothing merges them. [Versions of one lineage](ranking.md#versions-of-one-lineage) says the same from the ranking side.

:::note After 1.0
Two things are planned for after 1.0. They are a direction, not a commitment, and this page names no interface and gives no date for either.

- **Choosing between siblings.** A selection strategy that picks among the variants of one procedure by how each has performed, instead of ranking them all as independent matches.
- **Exploring a new version.** A new version has few runs, so a strategy that always takes the best-known variant never gives it a fair try. The aim is to try a new version before the best one dominates, so that an improvement can prove itself.

A host that wants either today builds it on the lineage id, as the next section shows.
:::

## What a host can do today

A recall already gives a host what it needs to group variants. This snippet groups a recall's matches by `LineageId` and keeps the best match of each group, so the agent sees one candidate per procedure:

```csharp
using Mnemosyne.NET.Abstractions;

static IReadOnlyList<ReasonletMatch> BestPerLineage(IEnumerable<ReasonletMatch> matches) =>
    matches
        .GroupBy(match => match.LineageId)
        .Select(group => group.OrderByDescending(match => match.Score.Total).First())
        .OrderByDescending(match => match.Score.Total)
        .ToList();

// memory is the IReasonletMemory and query the ReasonletQuery.
var recalled = await memory.RecallAsync(query, ct);
if (recalled.IsSuccess)
{
    foreach (var match in BestPerLineage(recalled.Value))
    {
        Console.WriteLine($"{match.Reasonlet.Name} {match.Reasonlet.Version}: {match.Score.Total:F3}");
    }
}
```

Recall returns up to three matches by default, so raise `Top` on the query when you want more variants to group. The same grouping fits in an `IReasonletSelector` that wraps the default one. The repository's tests hold such a selector, `LineageVariantSelector`, as an illustration. It is test code and not part of any package.

Grouping by lineage keeps variants of one procedure from crowding out other procedures. It picks the best score. It does not try a new version that has not yet earned one, which is the second item in the note above.

## Limits

- The lineage id follows `ParentId` inside one scope. Two procedures that were never linked by a parent are different lineages even when they solve the same problem.
- An erase can cut a link. A kept child becomes the root of a lineage of its own, and its `LineageId` changes to its own id. See [What erasure does to a lineage](versions.md#what-erasure-does-to-a-lineage).
