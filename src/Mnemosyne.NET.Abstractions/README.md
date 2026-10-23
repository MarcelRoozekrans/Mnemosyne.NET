# Mnemosyne.NET.Abstractions

Records and interfaces for Reasonlets, procedural memory for .NET agents.

The package holds the record types (scope, provenance, outcome, graph, capture, query, receipt) and the contracts.
`IReasonletMemory` is the caller-facing facade: remember, recall, record executions and feedback, erase.
The ports are `IReasonletStore`, `IReasonletSelector`, `IIntentExtractor` and `IReasonletEvaluator`; they carry no dependencies beyond ZeroAlloc.Results.
Implementations come in `Mnemosyne.NET` and `Mnemosyne.NET.PostgreSql`.

The JSON wire format of a graph is documented by the [published JSON Schema](https://github.com/MarcelRoozekrans/Mnemosyne.NET/blob/main/schemas/reasonlet-graph.v1.schema.json). The schema cannot express every rule, so `ReasonletGraph.Validate()` remains the full check.

`Mnemosyne.NET.Abstractions.Validation.Check` holds the validation helpers the records use, for packages that extend Mnemosyne: each returns success or a `Validation` failure whose message reads `path: reason`, and `Check.All` and `Check.Nested` combine them.

Documentation: [Reasonlets](https://marcelroozekrans.github.io/Mnemosyne.NET/concepts/reasonlets) and [Getting started](https://marcelroozekrans.github.io/Mnemosyne.NET/getting-started).

See the [repository README](https://github.com/MarcelRoozekrans/Mnemosyne.NET#readme).
