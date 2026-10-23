# Mnemosyne.NET

Reasonlet selection and ranking: find the best known procedure for a question.

An agent asks a question and gets back the few Reasonlets most worth trying, best first, each with a score breakdown of similarity, success rate and freshness. The agent still makes the final call. This package holds:

- `ReasonletMemory`, the default `IReasonletMemory` facade. It ties together a store, a selector, an embedding generator and an optional intent extractor.
- `ReasonletSelector`, the default `IReasonletSelector`, with its `ReasonletRankingOptions`.
- `MnemosyneOptions` and the `AddMnemosyne` registration.

```bash
dotnet add package Mnemosyne.NET
dotnet add package Mnemosyne.NET.PostgreSql
```

## Setup and recall

Register a store, an embedding generator and the facade. The store here is `Mnemosyne.NET.PostgreSql`, which the host migrates at startup. The embedding generator is any `IEmbeddingGenerator<string, Embedding<float>>` from Microsoft.Extensions.AI.

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET;
using Mnemosyne.NET.Abstractions;
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

## Documentation

- [Getting started](https://marcelroozekrans.github.io/Mnemosyne.NET/getting-started): a complete console app, and what `AddMnemosyne` registers.
- [Ranking](https://marcelroozekrans.github.io/Mnemosyne.NET/concepts/ranking): what recall does, the cut-off, the score, every option, how recall fails, and [a worked example](https://marcelroozekrans.github.io/Mnemosyne.NET/concepts/ranking#a-worked-example).
- [Reasonlets](https://marcelroozekrans.github.io/Mnemosyne.NET/concepts/reasonlets): what a capture holds, how intent and name are filled in, and what recall returns.
- [Versions and lineage](https://marcelroozekrans.github.io/Mnemosyne.NET/concepts/versions): how a capture becomes a new Reasonlet, a new version or a run.
- [Telemetry](https://marcelroozekrans.github.io/Mnemosyne.NET/guides/telemetry): the recall activity and metrics, alongside the store's.

See the [repository README](https://github.com/MarcelRoozekrans/Mnemosyne.NET#readme).
