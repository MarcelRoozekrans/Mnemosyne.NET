---
title: Variants
sidebar_position: 7
---

# Variants

A variant is one version of a procedure, seen next to the other versions of its lineage. This page says what Mnemosyne ships for variants and where it stops. [Choosing between variants](../guides/variants.md) shows how to switch it on.

## What a variant is

When an agent improves a procedure, Mnemosyne stores the improvement as a new version with the old one as its parent. Versions that descend from one root form a lineage, and every version in it is a variant of the others. Two versions of the same parent are siblings, the common case, and each is a variant of the other. [Versions and lineage](versions.md) describes how a version is made and numbered.

## What ships

- **A lineage id on every result.** `ReasonletCandidate`, which a store supplies, and `ReasonletMatch`, which a recall returns, both carry a `LineageId`: the id of the root of the version tree. All variants of one procedure report the same one. [The lineage id](versions.md#the-lineage-id) has the rules.
- **A selector port.** `IReasonletSelector` ranks the candidates a store returns, and the default `ReasonletSelector` ranks them by similarity, success rate and freshness. A host that registers its own selector before `AddMnemosyne` replaces it. [Ranking](ranking.md) has the formula.
- **Opaque version labels.** A label such as `1.2` is for display and for ordering inside one lineage. It is not a value to parse. [Compatibility](compatibility.md#version-labels) says why.
- **Variant selection.** `VariantSelector` wraps the registered selector and returns one version per lineage. `AddMnemosyneVariantSelection` switches it on. It is off by default.

Without variant selection, the default ranking treats every version as a match of its own. Two variants of one procedure can both appear in a recall, each with its own score, and nothing merges them. [Versions of one lineage](ranking.md#versions-of-one-lineage) says the same from the ranking side.

## Grouping

With variant selection on, the wrapped selector still ranks the candidates. `VariantSelector` then keeps the best-ranked version of each lineage and drops the others. The query's `Top` applies after the grouping, so variants of one procedure do not crowd out other procedures.

Grouping picks the best score. A new version with no runs takes its parent's rate as its prior, so it scores about as its parent does and rarely wins a run of its own.

## Exploration

Set `Exploration` to `VariantExploration.Thompson` to give a new version a fair try. For each lineage, `VariantSelector` picks one version before the wrapped selector scores anything:

- Each version draws a value from a Beta distribution whose mean is its smoothed success rate. The prior is 0.5 for a root, and the parent's head start for a child. See [Ranking](ranking.md).
- The highest draw wins. A version with few runs has a wide distribution, so it sometimes wins, and it wins less often as its runs pile up.
- `MinTrials` forces the issue. When any version of a lineage has fewer runs than `MinTrials`, the lineage skips the draw and serves its least-run version.

The wrapped selector then scores and orders only the picks.

## The two switches

- **`AddMnemosyneVariantSelection()`** turns on grouping. Nothing else changes.
- **`AddMnemosyneVariantSelection(options)`** with `Exploration = VariantExploration.Thompson` also turns on exploration.

Variant selection is off until a host calls one of them, and exploration is off until a host sets it.

## Seeded mode

Set `Seed` and the same question over the same data picks the same versions. Each lineage draws from its own generator, seeded from the seed, the question and the lineage id, so the result does not depend on the order of the candidates or on how many calls run at once. Without a seed, draws use `Random.Shared` and differ from call to call. The seed is for tests and demonstrations. [The variants sample](../samples.md#variants) uses one.

## Limits

- The lineage id follows `ParentId` inside one scope. Two procedures that were never linked by a parent are different lineages even when they solve the same problem.
- An erase can cut a link. A kept child becomes the root of a lineage of its own, and its `LineageId` changes to its own id. See [What erasure does to a lineage](versions.md#what-erasure-does-to-a-lineage).
- Variant selection does not merge lineages, and it does not change the default ranking. It does not reorder what a custom selector returns: the wrapped selector orders what it is given, and grouping keeps that order.
- Exploration costs recalls. A lineage that explores sometimes serves a version with a lower success rate, and the [`explored` tag](../guides/telemetry.md#the-recall-activity) counts how often.
