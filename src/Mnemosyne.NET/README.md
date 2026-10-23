# Mnemosyne.NET

Reasonlet selection and ranking: find the best known procedure for a question.

An agent asks a question and gets back the few Reasonlets most worth trying, best first, each with a score breakdown. The agent still makes the final call. This package holds:

- `ReasonletMemory`, the default `IReasonletMemory` facade. It ties together a store, a selector, an embedding generator and an optional intent extractor.
- `ReasonletSelector`, the default `IReasonletSelector`, with its `ReasonletRankingOptions`.
- `MnemosyneOptions` and the `AddMnemosyne` registration.

## Setup

Register a store, an embedding generator and the facade. The store here is the one from `Mnemosyne.NET.PostgreSql`, a separate package that depends on `Mnemosyne.NET.Abstractions` only, so reference both it and this package. The embedding generator is any `IEmbeddingGenerator<string, Embedding<float>>` from Microsoft.Extensions.AI, so a model your host already uses for chat or search works.

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET;
using Mnemosyne.NET.PostgreSql;
using Npgsql;

// connectionString and embeddingGenerator come from your host. The store's Dimensions must match the
// generator's output.
services.AddSingleton(NpgsqlDataSource.Create(connectionString));
services.AddMnemosynePostgreSql(new MnemosynePostgreSqlOptions { Dimensions = 1536 });
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(embeddingGenerator);
services.AddMnemosyne(new MnemosyneOptions
{
    Ranking = new ReasonletRankingOptions { MinSimilarity = 0.6 },
});
```

The host still runs `MigrateAsync` on the store at startup; see the [PostgreSQL store README](https://github.com/MarcelRoozekrans/Mnemosyne.NET/tree/main/src/Mnemosyne.NET.PostgreSql#readme).

`AddMnemosyne` registers:

- `IReasonletMemory`, as a singleton `ReasonletMemory`;
- `MnemosyneOptions`;
- the default `IReasonletSelector`, built from `MnemosyneOptions.Ranking`;
- `TimeProvider.System`.

`AddMnemosyne` does not register a store or an embedding generator: the container must also hold an `IReasonletStore` and an `IEmbeddingGenerator<string, Embedding<float>>`, or the first resolve of `IReasonletMemory` throws `InvalidOperationException`. The selector and the clock are added only when the host has not registered its own, so you can replace either. An `IIntentExtractor` is optional: register one and the facade uses it. Call `AddMnemosyne()` with no argument for the defaults.

The options are passed as an object, not a configure delegate, because their members are init-only. They are validated when you register, and `AddMnemosyne` throws `ArgumentException` for a bad value.

Then recall:

```csharp
using Mnemosyne.NET.Abstractions;

// provider is the built IServiceProvider, and ct the request's CancellationToken.
var memory = provider.GetRequiredService<IReasonletMemory>();
var recalled = await memory.RecallAsync(
    new ReasonletQuery { Scope = new ReasonletScope("tenant-42"), Question = "How do I export the monthly report?" },
    ct);
if (recalled.IsSuccess)
{
    foreach (var match in recalled.Value)
    {
        Console.WriteLine($"{match.Reasonlet.Name} {match.Reasonlet.Version}: {match.Score.Total:F3}");
    }
}
```

## What recall does

1. **Validate** the query. An invalid query answers `Validation`.
2. **Resolve the intent filter**, as described under [Intent](#intent).
3. **Embed** the question with the embedding generator. It must be the generator that made the stored embeddings.
4. **Search** the store for a candidate pool. The store returns only `Active` Reasonlets, nearest first. The pool is `CandidatePool` candidates, 20 by default, raised to the query's `Top` when that is larger. A pool larger than `Top` lets reliability and freshness reorder results that similarity alone would cut.
5. **Rank** the pool with the selector and return up to `Top` matches, 3 by default, best first.

The result is empty, and still a success, when no candidate passes the [cut-off](#the-cut-off). The agent then works from scratch.

## Ranking

The default selector scores each candidate that passes the cut-off:

```
total = SimilarityWeight * similarity + SuccessRateWeight * successRate + FreshnessWeight * freshness
```

Each part of the score is in [0, 1], and every match carries all four in its `ReasonletScore`: `Similarity`, `SuccessRate`, `Freshness` and `Total`.

- **Similarity** is the cosine similarity between the question's embedding and the Reasonlet's, from the store.
- **Success rate** is smoothed, so a handful of runs cannot swing it to 0 or 1:

  ```
  successRate = (successes + PriorWeight * prior) / (runs + PriorWeight)
  ```

  - `PriorWeight` is how many pseudo-runs the prior is worth.
  - For a Reasonlet without a parent, `prior` is 0.5.
  - **A new version inherits upward only.** For a Reasonlet with a parent, `prior` is `max(parentRate, 0.5)`, where `parentRate` is the parent's own success rate, smoothed with the 0.5 prior. A version of a proven procedure therefore starts from its parent's record, and its own runs take over as they accumulate. A version of a failing procedure, a fix, starts at 0.5 like any new procedure, so its parent's failures do not hold it back. This goes one level, to the parent only, because the parent is the version this one replaced.
  - When the parent has been erased, the parent link is cut and the version is treated as a root, with the 0.5 prior.
  - Runs count execution outcomes. Every remember records a run with the capture's outcome, and so does each `RecordExecutionAsync`. Feedback does not count yet.
- **Freshness** decays since the Reasonlet's last run:

  ```
  freshness = 0.5 ^ ((now - LastExecutedAt) / FreshnessHalfLife)
  ```

  "Now" comes from the registered `TimeProvider`. An age below zero, which clock skew between the host and the database can cause, counts as zero, so freshness is 1.

**Order.** Matches sort by total descending, then by similarity descending, then by id ascending, so the order is deterministic.

**Example.** With the defaults, take three Reasonlets that all ran just now:

| Reasonlet | Similarity | Runs | Success rate | Total |
|---|---|---|---|---|
| A new version of B, with only its own remember run, a success | 0.86 | 1 of 1 | (1 + 5 × 42.5/45) / 6 = 0.954 | 0.916 |
| B, the proven original | 0.85 | 40 of 40 | 42.5 / 45 = 0.944 | 0.908 |
| A close match that fails half its runs | 0.92 | 5 of 10 | 7.5 / 15 = 0.5 | 0.81 |

The new version ranks first. It is the refinement of a proven procedure, so it is not ranked below the original it replaced.

### Ranking options

`MnemosyneOptions.Ranking` holds the selector's settings, a `ReasonletRankingOptions`:

| Option | Default | Limits |
|---|---|---|
| `SimilarityWeight` | 0.5 | 0 to 1 |
| `SuccessRateWeight` | 0.3 | 0 to 1 |
| `FreshnessWeight` | 0.2 | 0 to 1 |
| `MinSimilarity` | 0.5 | 0 to 1 |
| `PriorWeight` | 5 | finite and above 0 |
| `FreshnessHalfLife` | 30 days | positive |

The three weights must sum to 1, within 1e-9.

### The cut-off

A candidate whose similarity is below the cut-off is dropped before ranking. The cut-off is `Ranking.MinSimilarity`, 0.5 by default, and a query can override it with `ReasonletQuery.MinSimilarity`, from 0 to 1.

Similarity scales differ between embedding models: one model's 0.5 is a weak match, another's a strong one. **Tune the cut-off for your model.** Recall a few questions you know the answer to, and a few you know have none, and set the cut-off between them. The recall telemetry's candidate and returned counts show how much the cut-off drops.

## Facade options

| Option | Default | Limits | Meaning |
|---|---|---|---|
| `CandidatePool` | 20 | 1 to 200 | How many candidates recall asks the store for before ranking. A query whose `Top` is larger raises it to `Top`. |
| `MinIntentConfidence` | 0.7 | 0 to 1 | The confidence an extracted intent needs before recall filters on it. |
| `Ranking` | the defaults above | must be valid | The selector's settings. |

## Intent

**Recall** filters candidates on an intent only when it is sure of one:

1. The query's `Intent`, when it has one.
2. Else, when an `IIntentExtractor` is registered, the extractor's answer, but only when its confidence is at least `MinIntentConfidence`, 0.7 by default.
3. Else no filter: recall searches every intent.

A wrong filter hides every right answer, so recall filters only on an intent it is confident about.

**Remember** always stores an intent:

1. The capture's `Intent`, when it has one.
2. Else the extractor's answer, whatever its confidence, because a stored intent is better than none.
3. Else `unspecified`.

## Remember defaults

`RememberAsync` fills in what the capture leaves out:

- **Intent**, as described under [Intent](#intent).
- **Name**: the capture's `Name`, else the first line of the question that is not blank, trimmed and cut to 200 code points, then trimmed again. A cut never splits a surrogate pair.

The stored capture carries the same resolved intent and name. The question is embedded with the embedding generator, and the store then decides whether the capture makes a new Reasonlet, a new version or only an execution, as `IReasonletMemory.RememberAsync` describes.

## Failures

The facade answers with a result for an expected failure and throws only for a bug or a cancellation.

A dependency is **down** when it throws, or when it times out: an `OperationCanceledException` while your token is not cancelled, such as an `HttpClient` timeout. An extractor that answers `Unavailable` is down too.

| What happens | Recall | Remember |
|---|---|---|
| The query or capture is invalid | `Validation` | `Validation` |
| The embedding generator is down, or returns no embedding | `Unavailable` | `Unavailable` |
| The generator returns an embedding the store cannot use: empty, with a NaN or infinite value, with a zero norm, or a length the store rejects | `Unavailable` | `Unavailable` |
| The intent extractor is down | searches every intent; the recall succeeds | `Unavailable`, so a wrong intent is never stored |
| The extractor fails with any other error code, or returns an invalid intent | throws `InvalidOperationException` | throws `InvalidOperationException` |
| The store returns an invalid candidate | throws `InvalidOperationException` | not applicable |
| The store or the selector fails otherwise | its error, unchanged | the store's error, unchanged |
| Your token is cancelled | the `OperationCanceledException` propagates | the `OperationCanceledException` propagates |

- **Why recall falls back.** A missing intent filter only widens the search, so recall still finds the right procedure, ranked among more candidates. The recall activity records it with `mnemosyne.recall.intent_fallback`. Remember has no such fallback: a guessed intent would be stored for good.
- **Why a rejected embedding is `Unavailable`.** The generator made the embedding, not you, so nothing in your input is invalid. The facade checks for an empty, non-finite or zero-norm embedding before it calls the store. It cannot know the store's dimension, so it maps the store's `Validation` on the embedding to `Unavailable`; every other store `Validation`, such as a bad limit, is returned unchanged. The error's message is fixed, and its detail names the reason: `length`, `non-finite` or `norm`, or the store's own message about the embedding.
- **No user text in errors.** For an exception, the error's detail names its type. The error never carries the exception's message or the extractor's own text, because either can quote the question.
- **Adapter bugs throw.** The extractor is given a valid question, so any error code other than `Unavailable` from it is a bug in the adapter, as is an invalid intent or an invalid candidate from the store.

`RecallAsync` reports every exception through the returned task, a null query included, rather than throwing synchronously, so that its telemetry records it. The exception surfaces when you await the task.

## Telemetry

Each recall emits OpenTelemetry signals on the same `ActivitySource` and `Meter` as the store, both named `Mnemosyne.NET`. The names are constants on `MnemosyneTelemetry` in Abstractions: add `MnemosyneTelemetry.ActivitySourceName` as a source and `MnemosyneTelemetry.MeterName` as a meter in your OpenTelemetry setup, as the store's README shows.

The facade instruments recall only. Remember and the other members delegate to the store, which already instruments them, and wrapping them again would count each twice.

### The recall activity

Each recall emits one `mnemosyne.recall` activity. The store's own activities, such as `mnemosyne.search`, nest inside it.

| Tag | Set when |
|---|---|
| `mnemosyne.recall.top` | always |
| `mnemosyne.recall.intent_filtered` | always, a bool: whether recall filtered on an intent. A recall that fails before the intent is resolved reports whether the query carried one. |
| `mnemosyne.recall.intent_fallback` | only when the intent extractor was down and recall searched every intent: `true`. It carries no error or exception message. |
| `mnemosyne.recall.candidates` | after the search: how many candidates the store returned |
| `mnemosyne.recall.returned` | on success: how many matches recall returned |
| `mnemosyne.recall.chosen.id` | on success, when at least one match is returned: the first match's id |
| `mnemosyne.recall.chosen.version` | on success, when at least one match is returned: the first match's version |
| `mnemosyne.recall.chosen.score` | on success, when at least one match is returned: the first match's total |

- A failure result sets `error.type` to the `MnemosyneErrorCode` name and the status to Error.
- When an exception propagates, cancellation included, the activity gets an `exception` event with `exception.type` only, `error.type` is set to the full exception type name, and the status is Error. The message and the stack trace are never recorded.

### Metrics

| Instrument | Type | Tags |
|---|---|---|
| `mnemosyne.operations` | counter | `mnemosyne.operation` (`recall`), `mnemosyne.outcome` (`ok`, the error code, or `exception`) |
| `mnemosyne.operation.duration` | histogram, seconds | `mnemosyne.operation` (`recall`) |
| `mnemosyne.recall.returned` | histogram, matches | none; recorded for each successful recall |
| `mnemosyne.recall.empty` | counter | none; counts successful recalls that returned nothing |

`mnemosyne.operations` and `mnemosyne.operation.duration` are the store's instruments too: the meters share one name, so the facade's `recall` and the store's operations aggregate as one instrument each.

### Privacy

No signal carries user data: no question, intent, scope, subject or tag text, and no error or exception message. Reasonlet ids, versions and scores are allowed.

See the [repository README](https://github.com/MarcelRoozekrans/Mnemosyne.NET#readme).
