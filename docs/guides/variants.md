---
title: Choosing between variants
sidebar_position: 11
---

# Choosing between variants

By default a recall ranks every version as a match of its own, so the versions of one procedure can fill the result. This guide shows how to return one version per lineage and, if you want, how to let a new version earn runs. [Variants](../concepts/variants.md) explains the ideas.

## When to switch it on

Switch it on when agents improve procedures, so lineages hold more than one version. Without it, a recall of three can show three versions of one procedure. With it, a recall shows three procedures.

Leave it off when each procedure has one version, or when you want every version ranked as its own match.

## Register it

Call it after `AddMnemosyne`. It wraps the selector that is registered at that point, and it throws `InvalidOperationException` when none is. If you register your own `IReasonletSelector`, call it after that too, so your selector is grouped as well.

```csharp
using Mnemosyne.NET;

// Grouping only: one version per lineage, the best-ranked.
services.AddMnemosyneVariantSelection();

// Grouping and exploration, with a fixed seed for a test.
// Use one of the two calls, not both.
services.AddMnemosyneVariantSelection(new VariantSelectionOptions
{
    Exploration = VariantExploration.Thompson,
    MinTrials = 5,
    Seed = 11,
});
```

A second call changes nothing, whichever overload either call used, so the first call's options win. Invalid options throw `ArgumentException`.

## The options

- **`Exploration`**: `VariantExploration.None`, the default, returns the best-ranked version of each lineage. `VariantExploration.Thompson` draws a plausible success rate for each version and returns the highest draw.
- **`MinTrials`**: from 0 to 1000, default 0. When a lineage has a version with fewer runs than this, the lineage skips the draw and serves its least-run version. 0 turns it off.
- **`Seed`**: default null. With a seed, the same question over the same data picks the same versions. Without one, every draw differs.

With `None`, `MinTrials` and `Seed` do nothing.

## What exploration costs

An exploring lineage sometimes serves a version that is not the one with the highest success rate. That is the price of finding out whether a new version is better. The price falls as the new version collects runs, because its distribution narrows.

Read the cost from the recall activity. The `mnemosyne.recall.variant.explored` tag counts the lineages whose pick was not the version with the highest smoothed success rate. It counts picks before `Top` and before the wrapped selector's cut-off, so it can be larger than the number of matches returned. The other two tags, `lineages` and `dropped`, are in [Telemetry](telemetry.md#the-recall-activity). All three hold counts only.

Exploration only helps if runs are recorded. A version that is served but never reported on does not move its own numbers.

## Seeded mode for tests

A seed makes picks reproducible per question and lineage. The order of the candidates and the number of concurrent calls do not change them. Use it in tests and demos. In production leave it off, so draws vary.

The [variants sample](../samples.md#variants) runs all of this offline and checks the counts.

## What it does not do

- It does not merge lineages. Two procedures with no parent link stay separate.
- It does not change the default ranking. The wrapped selector still scores and cuts.
- It does not touch a custom selector's own ordering. The wrapped selector receives a query whose `Top` equals the number of candidates, or, with exploration, the number of picks. It can exceed the usual limit of 50 when `CandidatePool` does.
