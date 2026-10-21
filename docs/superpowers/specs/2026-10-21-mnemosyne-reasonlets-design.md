# Mnemosyne.NET: Reasonlets, procedural memory

Date: 2026-10-21. Source: the user's HANDOVER.md, "Daedalus Reasonlets /
MetaCache Architecture", taken over as the design. The user skipped the
design questions, so the decisions below are marked as either **theirs** or
**my call**.

## Decisions

| | Decision | By |
|---|---|---|
| Name | **Mnemosyne.NET**: its own repo and NuGet library, next to Minos.NET, Rag.NET, Thalos.NET and Daedalus.NET. The concept inside it keeps the handover's name, **Reasonlet**. | user |
| First build | **Phase 1 + 2**: store successful procedures, find and reuse them. Phases 3–5 later. | user |
| Repo | Public, `MarcelRoozekrans/Mnemosyne.NET`, MIT. | user |
| Where it is built | Its own Remote Control session on the agents VM. | user |
| Storage | PostgreSQL + pgvector, nothing else: no Neo4j, event sourcing, CQRS or workflow engine. | handover |
| Graphs | Stored as JSON, not normalised into tables. | handover |
| Ranking | 0.50 similarity + 0.30 success rate + 0.20 freshness. | handover |
| Stack | .NET 10, the same repo conventions as Thalos.NET: slnx, central package management, Directory.Build.props, GitVersion, release-please, Renovate extending `local>MarcelRoozekrans/renovate-config`, Codecov. | my call |
| Packages | `Mnemosyne.NET.Abstractions` (records and interfaces), `Mnemosyne.NET` (selector and ranking), `Mnemosyne.NET.PostgreSql` (store, pgvector, migrations). | my call |
| Embeddings | Through `Microsoft.Extensions.AI` `IEmbeddingGenerator<string, Embedding<float>>`, so the caller picks the model; Rag.NET and Daedalus already work this way, check before building. | my call |
| First consumer | Daedalus.NET, wired in after phase 1+2 is released, in a Daedalus PR. Not part of this repo's first build. | my call |

## Why

Rag.NET stores knowledge, Minos.NET produces decisions, Daedalus executes
workflows. None of them keeps **how** a problem was solved, so every run
re-plans from scratch. Example: "help me troubleshoot Azure OpenAI" makes
Daedalus look up docs, check the endpoint, the deployment and the API version,
then recommend. The answer is returned; the procedure is lost.

A **Reasonlet** is that procedure as a reusable artifact:

```text
Question → Procedure → Answer
```

stored after a successful run, and found again for a similar question.

| Library | Memory kind | Question it answers |
|---|---|---|
| Rag.NET | Knowledge | What do we know? |
| Minos.NET | Decisions | What decision should be made? |
| Mnemosyne.NET | Procedures | How did we solve this before, and did it work? |

## Goals: phase 1 + 2

**Phase 1, store.** After a successful run, the caller stores the procedure it
followed, with its intent, an embedding, the execution record and feedback.

**Phase 2, reuse.** For a new question, find stored procedures and return the
best one, ranked:

```text
User query → intent extraction → Reasonlet search → candidate set → ranking → best procedure
```

`Final score = 0.50 · similarity + 0.30 · success rate + 0.20 · freshness`

A storage layer must answer four questions; phase 1+2 covers the first three,
the fourth only as far as "latest version":
1. What is this procedure?
2. When should I use it?
3. Does it work?
4. Which version should I select?

## Non-goals for this build

- Phase 3, evaluation loops: Minos-driven scoring of a run. The feedback
  record exists; producing it automatically does not.
- Phase 4, variant selection: lineage is stored, but selecting between
  sibling versions is not built.
- Phase 5, the Reason Graph Runtime: executing graphs with parallel branches.
  Mnemosyne stores the graph JSON; the caller executes it.
- Executing procedures at all. Daedalus executes; Mnemosyne remembers.

## Anti-goals

- Overengineering: the handover is explicit about PostgreSQL + pgvector only.
- A graph schema frozen too early: it is expected to change fast, so it stays
  JSON with a version field.
- Answer caching. A Reasonlet stores the procedure, not the answer.

## Data model, MVP

Five tables, from the handover:

```sql
reasonlets              -- definition: id, name, intent, version, graph_json, created_at
reasonlet_embeddings    -- reasonlet_id, vector (pgvector)
reasonlet_executions    -- id, reasonlet_id, executed_at, success, duration, score
reasonlet_feedback      -- execution_id, utility, correctness, reusability
reasonlet_lineage       -- child_id, parent_id
```

Records, as in the handover:

```csharp
public sealed record ReasonletDefinition(Guid Id, string Name, string Intent, string Version, string GraphJson);
public sealed record ReasonletEmbedding(Guid ReasonletId, ReadOnlyMemory<float> Vector);
public sealed record ReasonletExecution(Guid Id, Guid ReasonletId, DateTime ExecutedAt, bool Success, double Duration, double Score);
public sealed record ReasonletMetrics(Guid ReasonletId, int Runs, int Successes, double AvgScore, double AvgDuration);
public sealed record ReasonletIntent(Guid ReasonletId, string Intent, double Confidence);
public sealed record ReasonletLineage(Guid ChildId, Guid ParentId);
public sealed record Feedback(Guid ExecutionId, double Utility, double Correctness, double Reusability);
```

The handover also lists a metrics store and an intent catalog. For the MVP,
**my call**: metrics are computed from `reasonlet_executions` with a query or
view, not a table of their own; the intent is a column on `reasonlets`, used
as the first filter. Both can become tables when a measurement says so.

Graph JSON, example from the handover:

```json
{ "nodes": [ { "id": "retrieve", "type": "retrieve" }, { "id": "score", "type": "decision" } ],
  "edges": [ { "from": "retrieve", "to": "score" } ] }
```

Lineage is "git for procedures":

```text
AzureTroubleshooting 1.0 ├─ 1.1 ├─ 1.2 └─ 2.0
```

## Interfaces

From the handover; the members are left to the builder:

```csharp
public interface IReasonletStore { }       // phase 1: save definition, embedding, execution, feedback, lineage
public interface IReasonletSelector { }    // phase 2: query → ranked candidates → best
public interface IReasonletEvaluator { }   // phase 3: declared now only if cheap, otherwise later
public interface IReasonletVersioning { }  // phase 4: later
```

## What is left to the builder

- Interface members, method names, async and cancellation shape, following the
  Thalos.NET and Minos.NET style.
- Intent extraction for phase 2: a simple, swappable default. An LLM or a Minos
  call fits behind an interface; an embedding-only match is enough to start.
- Freshness: the decay function and its time scale; document the choice.
- Similarity: cosine via pgvector; index type, HNSW or IVFFlat, and the
  embedding dimension as configuration.
- Migrations: tool of choice, matching how Daedalus migrates PostgreSQL.
- Tests: unit tests for ranking, integration tests against PostgreSQL with
  pgvector in a container, for example Testcontainers. On the shared agents VM,
  run test suites one at a time; see `~/Projects/Prive/ZeroAlloc/CLAUDE.md`.
- After the first CI run: Codecov badge, and the Renovate app bypass on the
  Main ruleset like the other repos.

## Future vision

```text
                    Daedalus
      ┌──────────────────┼──────────────────┐
   Rag.NET           Minos.NET         Mnemosyne.NET
  Knowledge          Decisions      Procedural memory
      └──────────────────┬──────────────────┘
                   Execution graph → Result
```

The system then stores not only facts, embeddings and decisions, but proven
problem-solving strategies. That is the MetaCache / Reasonlet idea.
