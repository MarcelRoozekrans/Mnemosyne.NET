# Mnemosyne.NET.Abstractions

Records and interfaces for Reasonlets, procedural memory for .NET agents.

The package holds the record types (scope, provenance, outcome, graph, capture, query, receipt) and the contracts.
`IReasonletMemory` is the caller-facing facade: remember, recall, record executions and feedback, erase.
The ports are `IReasonletStore`, `IReasonletSelector` and `IIntentExtractor`; they carry no dependencies beyond ZeroAlloc.Results.
Implementations come in `Mnemosyne.NET` and `Mnemosyne.NET.PostgreSql`.

See the [repository README](https://github.com/MarcelRoozekrans/Mnemosyne.NET#readme).
