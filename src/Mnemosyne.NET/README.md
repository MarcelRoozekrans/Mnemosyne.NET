# Mnemosyne.NET

Reasonlet selection and ranking: find the best known procedure for a question.

An agent asks a question and gets back the few Reasonlets most worth trying, best first, each with a score breakdown of similarity, success rate and freshness. The agent still makes the final call. This package holds:

- `ReasonletMemory`, the default `IReasonletMemory` facade. It ties together a store, a selector, an embedding generator, an optional intent extractor and an optional evaluator.
- `ReasonletSelector`, the default `IReasonletSelector`, with its `ReasonletRankingOptions`.
- `MnemosyneOptions` and the `AddMnemosyne` registration.

```bash
dotnet add package Mnemosyne.NET
dotnet add package Mnemosyne.NET.PostgreSql
```

## Setup and recall

Register a store, an embedding generator and the facade. The store here is `Mnemosyne.NET.PostgreSql`, which the host migrates at startup; see [Migrations](https://marcelroozekrans.github.io/Mnemosyne.NET/guides/postgresql#migrations). The embedding generator is any `IEmbeddingGenerator<string, Embedding<float>>` from Microsoft.Extensions.AI.

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql;
using Npgsql;

// connectionString and embeddingGenerator come from your host. The store's Dimensions must match the
// generator's output.
services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
services.AddMnemosynePostgreSql(new MnemosynePostgreSqlOptions { Dimensions = 1536 });
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(embeddingGenerator);
services.AddMnemosyne(new MnemosyneOptions
{
    Ranking = new ReasonletRankingOptions { MinSimilarity = 0.6 },
});

// Later, with the built IServiceProvider and the request's CancellationToken:
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

## Judging runs

By default a run counts as a success when the agent says it worked. Register an `IReasonletEvaluator` and give the run its question and answer, and the evaluator's verdict counts instead. The answer goes to the evaluator and is never stored. A judge that is down, or slower than `MnemosyneOptions.EvaluationTimeout` (30 seconds by default), doesn't fail the call: the run is stored with the agent's verdict. So is a run whose answer is blank or longer than 32,000 code points, which is not judged. The receipt says which verdict counted. A person's verdict, recorded with feedback, overrides both. The `Mnemosyne.NET.Minos` package is a ready-made judge that asks a Minos.NET decision model: see [Judging runs with Minos](https://marcelroozekrans.github.io/Mnemosyne.NET/guides/minos-evaluator).

```csharp
// myEvaluator is your IReasonletEvaluator; register it as a singleton.
services.AddSingleton<IReasonletEvaluator>(myEvaluator);

// After the agent follows a recalled Reasonlet:
var recorded = await memory.RecordExecutionAsync(
    new ReasonletRun
    {
        Scope = scope,
        Id = match.Reasonlet.Id,
        Outcome = new ReasonletOutcome(Success: true, Duration: elapsed, Score: null),
        Provenance = new ReasonletProvenance { Source = "support-agent" },
        Question = question,
        Answer = answer,
    },
    ct);

if (recorded.IsSuccess)
{
    // Evaluator when the judge answered; Agent when it was down or the answer was not usable.
    Console.WriteLine($"Run {recorded.Value.Execution}: {recorded.Value.Source} says {recorded.Value.Success}");

    // Later, when a person says whether it worked:
    var feedback = await memory.RecordFeedbackAsync(
        scope, recorded.Value.Execution, new ReasonletFeedback(Utility: 0.2, Correctness: 0.1, Reusability: 0.5, Worked: false), ct);
    if (feedback.IsFailure)
    {
        Console.Error.WriteLine($"Feedback failed: {feedback.Error.Code}: {feedback.Error.Message}");
    }
}
```

Feedback with `Worked` null keeps an earlier verdict, so a verdict can be changed but not withdrawn.

## Documentation

- [Getting started](https://marcelroozekrans.github.io/Mnemosyne.NET/getting-started): a complete console app, and what `AddMnemosyne` registers.
- [Ranking](https://marcelroozekrans.github.io/Mnemosyne.NET/concepts/ranking): what recall does, the cut-off, the score, every option, how recall fails, and [a worked example](https://marcelroozekrans.github.io/Mnemosyne.NET/concepts/ranking#a-worked-example).
- [Reasonlets](https://marcelroozekrans.github.io/Mnemosyne.NET/concepts/reasonlets): what a capture holds, how intent and name are filled in, and what recall returns.
- [Versions and lineage](https://marcelroozekrans.github.io/Mnemosyne.NET/concepts/versions): how a capture becomes a new Reasonlet, a new version or a run.
- [Judging runs with Minos](https://marcelroozekrans.github.io/Mnemosyne.NET/guides/minos-evaluator): a ready-made evaluator, its threshold, failures and privacy.
- [Telemetry](https://marcelroozekrans.github.io/Mnemosyne.NET/guides/telemetry): the recall and evaluate activities and metrics, alongside the store's.

See the [repository README](https://github.com/MarcelRoozekrans/Mnemosyne.NET#readme).
