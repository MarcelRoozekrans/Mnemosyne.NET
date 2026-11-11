# Variants

A console walkthrough of how Mnemosyne.NET lets a new version of a Reasonlet overtake a proven one. It needs only the .NET 11 SDK: no Docker, no database, no model and no API key.

It builds the candidates by hand. The incumbent has 200 runs at a 70% success rate. Its new version has no runs yet, and would really succeed 90% of the time. An unrelated procedure sits beside them. The outcomes are drawn from a seeded `Random` and the selector has a fixed seed, so the counts are the same on every run.

## Run it

From this folder:

```bash
dotnet run
```

## What it does

1. Act 1: `ReasonletSelector` alone over 300 rounds. A new version with no runs scores as its parent does, so it ties and the lower id wins: the incumbent is chosen every round, and the new version never earns a run. The sample checks the new version was chosen 0 times.
2. Act 2: `VariantSelector` without exploration. It returns one match per lineage, and the unrelated procedure is still there. The sample checks for two matches.
3. Act 3: `VariantSelector` with `VariantExploration.Thompson` and `Seed = 11`. The new version is chosen at least once in the first 20 rounds, and in at least 60 of the last 100. The sample prints the observed counts.
4. Exits 1, naming the receipt, if any check fails. CI runs the sample, so this is tested.

The ids are fixed, the incumbent's the lower, as time-ordered ids would be for the older version. Timestamps are fixed too, so nothing depends on the clock.
