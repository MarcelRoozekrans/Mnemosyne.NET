# Mnemosyne.NET.Abstractions

Records and interfaces for Reasonlets, procedural memory for .NET agents.

Mnemosyne.NET follows [SemVer](https://semver.org) from 1.0.0. An analyzer baseline locks the public API, and the [compatibility policy](https://marcelroozekrans.github.io/Mnemosyne.NET/concepts/compatibility) says what a release may change.

The package holds the record types (scope, provenance, outcome, graph, capture, query, receipt) and the contracts.
`IReasonletMemory` is the caller-facing facade: remember, recall, record executions and feedback, erase, and list a data subject's Reasonlets and runs with `ListSubjectReasonletsAsync` and `ListSubjectExecutionsAsync`.
A recall returns `ReasonletMatch` records, built from the `ReasonletCandidate` records a store supplies, with a `ReasonletEvidence` on each match. All three are init-only records, and the candidate and the match carry a `LineageId`: the id of the root of the version tree, its own id for a root.
The ports are `IReasonletStore`, `IReasonletSelector`, `IIntentExtractor` and `IReasonletEvaluator`; they carry no dependencies beyond ZeroAlloc.Results.
Implementations come in `Mnemosyne.NET` and `Mnemosyne.NET.PostgreSql`.

The JSON wire format of a graph is documented by the [published JSON Schema](https://github.com/MarcelRoozekrans/Mnemosyne.NET/blob/main/schemas/reasonlet-graph.v1.schema.json). The schema cannot express every rule, so `ReasonletGraph.Validate()` remains the full check.

Documentation: [Reasonlets](https://marcelroozekrans.github.io/Mnemosyne.NET/concepts/reasonlets) and [Getting started](https://marcelroozekrans.github.io/Mnemosyne.NET/getting-started).

See the [repository README](https://github.com/MarcelRoozekrans/Mnemosyne.NET#readme).
