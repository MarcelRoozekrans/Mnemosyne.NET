# Mnemosyne.NET.Abstractions

Records and interfaces for Reasonlets, procedural memory for .NET agents.

The package holds the record types (scope, provenance, outcome, graph, capture, query, receipt) and the contracts.
`IReasonletMemory` is the caller-facing facade: remember, recall, record executions and feedback, erase.
The ports are `IReasonletStore`, `IReasonletSelector` and `IIntentExtractor`; they carry no dependencies beyond ZeroAlloc.Results.
Implementations come in `Mnemosyne.NET` and `Mnemosyne.NET.PostgreSql`.

The JSON wire format of a graph is documented by the [published JSON Schema](https://github.com/MarcelRoozekrans/Mnemosyne.NET/blob/main/schemas/reasonlet-graph.v1.schema.json). The schema cannot express every rule, so `ReasonletGraph.Validate()` remains the full check.

See the [repository README](https://github.com/MarcelRoozekrans/Mnemosyne.NET#readme).
