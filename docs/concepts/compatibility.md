---
title: Compatibility
sidebar_position: 6
---

# Compatibility

This page says what you can rely on across releases: the public API of the six packages, and the graph document format. Both follow [Semantic Versioning](https://semver.org).

## Before 1.0.0

The packages are at 0.x. Until 1.0.0 a minor release may break the public API, and release-please bumps the minor version for a breaking change. The baseline diff in the pull request is still the review gate: a reviewer sees every changed line. The rules below, where only a major release breaks, start at 1.0.0.

## The promise

From 1.0.0, each package follows SemVer on its own version line.

- A **patch** release fixes bugs. It changes no public member.
- A **minor** release adds. It may add public types and members, and it never removes or changes one that shipped.
- A **major** release may break. It is the only release that removes a public member, changes a signature, or changes behavior a caller could rely on.

Package validation, which compares each package with its previous release at pack time, arrives with 1.0.0 as well. Until then the baseline files described below are the gate.

## What counts as public

Everything a package exposes as `public` or `protected` is public API: types, members, constructors, and the init-only properties of the records. Internal types are not.

Each of the six packable projects records its public surface in a baseline pair under its project folder:

- `PublicAPI.Shipped.txt` lists what a release has shipped.
- `PublicAPI.Unshipped.txt` lists what has been added since.

The packages are `Mnemosyne.NET.Abstractions`, `Mnemosyne.NET`, `Mnemosyne.NET.PostgreSql`, `Mnemosyne.NET.Minos`, `Mnemosyne.NET.Sentinel` and `Mnemosyne.NET.AgentFramework`.

The PublicApiAnalyzers rules run as build errors. A public member without a baseline line fails the build, and so does a baseline line without a member.

## How the baselines move

1. A change that adds public API adds its lines to `PublicAPI.Unshipped.txt`.
2. When a release is cut, the unshipped lines move to `PublicAPI.Shipped.txt`, and `Unshipped` is empty again.
3. A shipped line is never edited or deleted in a minor or patch release.

## How a breaking change is made

From 1.0.0, a breaking change goes into a major release. The pull request that makes it shows the baseline diff, so a reviewer sees each removed or changed line in `PublicAPI.Shipped.txt` and the commit says `BREAKING CHANGE:` in its footer. From 1.0.0, a breaking change that is not in a major release is a bug, and the fix is to revert it.

## Graph schema v1

A Reasonlet graph is stored and exchanged as a JSON document. Its format is `schemaVersion` 1, described by [`schemas/reasonlet-graph.v1.schema.json`](https://github.com/MarcelRoozekrans/Mnemosyne.NET/blob/main/schemas/reasonlet-graph.v1.schema.json).

Version 1 is frozen.

- A document that is valid under v1 today still deserializes, and round-trips to an equal graph, in every later release that reads v1.
- The content hash of such a document does not change, because stored hashes depend on it.
- Any change to the format is a new `schemaVersion` with its own schema file: version 2 gets `reasonlet-graph.v2.schema.json` and leaves the v1 file as it is. A document with a `schemaVersion` the release does not know is rejected, and so is a document with an unknown member.
- A minor release that adds version 2 reads both versions. Version 1 stays readable for at least one major release after that, so only a later major may stop reading it.

The golden documents in `tests/Mnemosyne.NET.Tests/Graph/GoldenV1` pin the format: a minimal graph, one with every optional member, and one at the documented limits. A test fails if any of them stops deserializing, stops round-tripping, or changes its content hash.

## Version labels

The version label of a Reasonlet, such as `1.2`, is opaque. It orders versions inside one lineage and is for display. Don't parse it, and don't compare labels across lineages. [Versions and lineage](versions.md#the-lineage-id) explains why.
