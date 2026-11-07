---
title: Samples
sidebar_position: 3
---

# Samples

All five samples run through one shared runner. It gives each database sample two minutes and the variants sample one, and turns a failed check, a database error or a timeout into a `FAILED:` line and exit code 1.

## Store and recall

[`samples/Mnemosyne.Samples.StoreAndRecall`](https://github.com/MarcelRoozekrans/Mnemosyne.NET/tree/main/samples/Mnemosyne.Samples.StoreAndRecall) is a console app that walks through the whole loop. It needs only Docker and the .NET 10 SDK. It needs no API key and no embedding model.

### What it does

1. Wires up the PostgreSQL store, an embedding generator and the Mnemosyne facade, and runs the migrations.
2. Remembers three procedures in a fresh scope, and records how they ran:
   - **"refund an order"**, proven: 20 successful runs out of 20;
   - **"refund a partial order"**, a close match for the question it will recall, but with 5 successes in 10 runs;
   - **a new version of "refund an order"**: the same question with an improved graph, which adds a refund-window check and a customer email, remembered with `ParentId` set to the original, and one successful run.
3. Recalls "How do I refund an order?" and prints the top three matches with their score breakdown.
4. Checks the full order (new version, original, close match) and the totals shown below, and exits with code 1 if not.
5. Erases the scope, so a run leaves nothing behind.

### Run it

Clone the repository, then from the sample's folder:

```bash
cd samples/Mnemosyne.Samples.StoreAndRecall
docker compose up -d --wait
dotnet run
docker compose down
```

`docker compose up -d --wait` starts PostgreSQL 17 with pgvector and returns once the database accepts connections.

The sample connects to `Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=mnemosyne_sample`, which matches the compose file. To use another server, set the `MNEMOSYNE_SAMPLE_PG` environment variable to its connection string. The server needs PostgreSQL 15 or later with pgvector 0.8.0 or later. The sample migrates the `mnemosyne` schema with 256 dimensions, so give it a database of its own.

### The output

The heart of the output is the ranked list:

```text
  #  Reasonlet                Version  Similarity  Success  Freshness   Total
  1  refund an order          1.1           0.816    0.848      1.000   0.863
  2  refund an order          1.0           0.816    0.833      1.000   0.858
  3  refund a partial order   1.0           0.904    0.500      1.000   0.802
```

- The new version ranks first after a single run, because it inherits its parent's record.
- The close match has the highest similarity, but its 5-in-10 record ranks it last.

[Ranking](concepts/ranking.md#a-worked-example) works through every number in this table.

The sample checks this order itself, and the repository's CI runs it on every pull request to `main` and every push to `main`. If a change to the ranking broke what this page shows, CI would fail.

### The embedding generator is a stand-in

To run offline, the sample ships `HashingEmbeddingGenerator`. It lowercases the text, splits it into words, hashes each word into one of 256 buckets and normalises the result. It is deterministic, so the ranking check is stable.

**It is not a real model.** It sees only shared words: "refund an order" and "return a purchase" look unrelated to it. Don't judge recall quality by it.

In an application, register a real model through Microsoft.Extensions.AI, and set the store's `Dimensions` to the model's output size. With [Ollama](https://ollama.com) and the OllamaSharp package:

```csharp
using OllamaSharp;

// ollama pull nomic-embed-text; it returns 768 dimensions.
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    new OllamaApiClient(new Uri("http://localhost:11434"), "nomic-embed-text"));
services.AddMnemosynePostgreSql(new MnemosynePostgreSqlOptions { Dimensions = 768 });
```

With OpenAI and the Microsoft.Extensions.AI.OpenAI package:

```csharp
using OpenAI;

// text-embedding-3-small returns 1536 dimensions.
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    new OpenAIClient(apiKey).GetEmbeddingClient("text-embedding-3-small").AsIEmbeddingGenerator());
services.AddMnemosynePostgreSql(new MnemosynePostgreSqlOptions { Dimensions = 1536 });
```

A real model gives different similarities, so the numbers above change, and you should [tune the cut-off](concepts/ranking.md#the-cut-off) for it. A database migrated with one dimension count can't take another, so use a new database when you switch models.

## Evaluation

[`samples/Mnemosyne.Samples.Evaluation`](https://github.com/MarcelRoozekrans/Mnemosyne.NET/tree/main/samples/Mnemosyne.Samples.Evaluation) is a console app that shows how a run's verdict changes the ranking. It needs only Docker and the .NET 10 SDK. It needs no API key and no model.

### What it does

1. Registers the PostgreSQL store, an embedding generator, a rule-based evaluator and the facade, and runs the migrations. The evaluator calls an answer a success when it holds a refund reference like `REF-4821` and the word "notified".
2. Remembers two procedures for near-identical questions, so their similarity is equal and the success rate decides:
   - **"refund by hand"**: 8 runs, none with an answer, so the agent's verdict counts for each;
   - **"refund through the API"**: 5 runs, each with an answer that meets the rule, so the evaluator judges each.
3. Recalls "How do I refund an order?" three times. Between the recalls, one run on "refund through the API" is recorded and then given feedback.
4. Checks every receipt, the order after each act, and the totals, evidence and success rates shown below, and exits with code 1 if not.
5. Erases the scope, so a run leaves nothing behind.

### Run it

Clone the repository, then from the sample's folder:

```bash
cd samples/Mnemosyne.Samples.Evaluation
docker compose up -d --wait
dotnet run
docker compose down
```

The database listens on port 5433, so it can run next to the store-and-recall sample's. To use another server, set the `MNEMOSYNE_SAMPLE_PG` environment variable to its connection string. The server needs PostgreSQL 15 or later with pgvector 0.8.0 or later.

### The output

Three recalls, one after each act:

```text
Act 1. Every answer for "refund through the API" meets the rule, so its runs count in full.
  #  Reasonlet                Similarity  Success   Total  Runs  Judged
  1  refund through the API        0.926    0.750   0.888     5       5
  2  refund by hand                0.926    0.722   0.880     8       0

Act 2. One more run on "refund through the API": the agent reports success, and the answer has a refund reference but does not say the customer was notified.
  Receipt: source Evaluator, success False, execution <execution id>. The agent said true; the evaluator said false.
  #  Reasonlet                Similarity  Success   Total  Runs  Judged
  1  refund by hand                0.926    0.722   0.880     8       0
  2  refund through the API        0.926    0.682   0.867     6       6

Act 3. A person checks and says the customer was in fact notified: feedback with Worked true on that run overrides the evaluator.
  #  Reasonlet                Similarity  Success   Total  Runs  Judged
  1  refund through the API        0.926    0.773   0.895     6       6
  2  refund by hand                0.926    0.722   0.880     8       0
```

The execution id in the act 2 receipt is different on every run, so it is shown as a placeholder.

- **Act 1.** Five judged successes give a success rate of 0.750. "Refund by hand" has 8 runs and none judged, so it ranks second.
- **Act 2.** The agent said the run worked, but the evaluator overruled it. The failed judged run drops the rate to 0.682, and "refund by hand" ranks first.
- **Act 3.** Feedback with `Worked` true overrides the evaluator, so the run counts as a success and the rate rises to 0.773.

[Evaluation](concepts/evaluation.md) explains who decides whether a run worked, and [Ranking](concepts/ranking.md#success-rate) works through the success rate.

The sample checks all of this itself, and the repository's CI runs it. If a change to judging or ranking broke what this page shows, CI would fail.

### The evaluator is a stand-in

A word check is not how to judge real answers. In an application, implement `IReasonletEvaluator` over whatever decides whether an answer worked. The [custom evaluator guide](guides/custom-evaluator.md) shows the contract.

The sample also uses the same offline embedding stand-in as store and recall, so [the same caveat applies](#the-embedding-generator-is-a-stand-in).

## Chat pipeline

[`samples/Mnemosyne.Samples.ChatPipeline`](https://github.com/MarcelRoozekrans/Mnemosyne.NET/tree/main/samples/Mnemosyne.Samples.ChatPipeline) is a console app that shows `UseReasonlets` with the review gate and AI.Sentinel. It needs only Docker and the .NET 10 SDK. It needs no API key and no model.

### What it does

1. Registers the PostgreSQL store, an embedding generator, AI.Sentinel with `AddMnemosyneSentinel`, the facade with `RequireReview = true` and the capture queue, and runs the migrations. Each chat client is `UseReasonlets` outside `UseFunctionInvocation`.
2. Asks three questions through a scripted client that plays the model:
   - **Act 1**, "How do I refund an order?": the model calls `orders_get` and `payments_refund`. The capture is stored as a pending review, and recall for the same question finds nothing;
   - **Act 2**, "How do I refund an order for a customer?", after a person approves the Reasonlet: the first call to the model carries the recalled Reasonlet in its system text;
   - **Act 3**, a question with a card number, through a client with another tool shape: AI.Sentinel blocks the capture.
3. Waits for each capture to finish with a listener on the `mnemosyne.capture.runs` counter, so it never sleeps for a fixed time.
4. Checks every reply, outcome and count shown below, and exits with code 1 if not.
5. Erases the scope, so a run leaves nothing behind.

### Run it

Clone the repository, then from the sample's folder:

```bash
cd samples/Mnemosyne.Samples.ChatPipeline
docker compose up -d --wait
dotnet run
docker compose down
```

The database listens on port 5434, so it can run next to the other samples' databases. To use another server, set the `MNEMOSYNE_SAMPLE_PG` environment variable to its connection string. The server needs PostgreSQL 15 or later with pgvector 0.8.0 or later.

### The output

```text
Act 1. Ask "How do I refund an order?" and let the capture run in the background.
  Capture outcome: new_reasonlet. Pending reviews: 1 ("orders_get → payments_refund"). Matches for the same question: 0.

Act 2. A person approves the Reasonlet. Then ask "How do I refund an order for a customer?".
  The first call to the model carried the recalled Reasonlet "orders_get → payments_refund" in its system text.
  Capture outcome: execution_only. Pending reviews: 0.

Act 3. Ask "Which invoice was paid with card 4111 1111 1111 1111?" through a client with a different tool shape (invoices_get).
  Capture outcome: Blocked. Pending reviews: 0. Matches for the card question: 0.
```

- **Act 1.** The capture is a new procedure, so `RequireReview` stores it as pending. A pending Reasonlet is never recalled, so the same question finds no match.
- **Act 2.** Once approved, the Reasonlet is recalled for a differently worded question and offered to the model. The run only adds an execution to it, so there is nothing new to review.
- **Act 3.** The question holds a card number. The capture is blocked at the guard's default of `High`, so the store never sees it.

The sample checks the `Blocked` outcome, that no review is pending and that recall for the question finds nothing. It also checks the other receipts of acts 1 and 2, and exits with code 1 if one differs.

The rest was observed by hand and is not checked. AI.Sentinel's PII detector rates a card number Critical. This tool shape is new, so without the guard it would be a second pending review. The detector matches the digit pattern and does not check the Luhn sum: 16-digit numbers blocked whether or not the sum was valid, and numbers of 12, 15 and 17 digits did not block.

The repository's CI runs the sample on every pull request to `main` and every push to `main`, so a change that broke these receipts would fail CI.

### The model is a stand-in

`ScriptedChatClient` is not a model. It replays a fixed script of tool calls and an answer. In an application, register a real chat model through Microsoft.Extensions.AI and build the pipeline on it, for example with an Ollama or OpenAI `IChatClient`:

```csharp
IChatClient client = new ChatClientBuilder(realClient)
    .UseReasonlets(new ReasonletChatOptions { DefaultScope = new ReasonletScope("support") })
    .UseFunctionInvocation()
    .Build(provider);
```

The sample also uses the same offline embedding stand-in as store and recall, so [the same caveat applies](#the-embedding-generator-is-a-stand-in).

## Agent Framework

[`samples/Mnemosyne.Samples.AgentFramework`](https://github.com/MarcelRoozekrans/Mnemosyne.NET/tree/main/samples/Mnemosyne.Samples.AgentFramework) is a console app that shows `ReasonletContextProvider` on a Microsoft Agent Framework agent. It needs only Docker and the .NET 10 SDK. It needs no API key and no model.

### What it does

1. Registers the PostgreSQL store, an embedding generator, the facade with `AddMnemosyne` and the capture queue. The review gate is off. Then it runs the migrations.
2. Builds a `ChatClientAgent` with a `ReasonletContextProvider` in its `AIContextProviders`, and runs it twice through a scripted client that plays the model:
   - **Run 1**, "How do I refund an order?": the model calls `orders_get` and `payments_refund`. The capture stores a new Reasonlet, and the first call to the model carried no recalled Reasonlet;
   - **Run 2**, "How do I refund an order for a customer?" on a fresh session: the provider recalls the Reasonlet and adds it to the run's instructions. The sample checks that the name is in the instructions of the first call and in no message of any role.
3. After each run, waits for its capture with a listener on the `mnemosyne.capture.runs` counter, so it never sleeps for a fixed time. It checks every reply, outcome and count shown below along the way, and exits with code 1 if not.
4. Erases the scope, so a run leaves nothing behind.

### Run it

Clone the repository, then from the sample's folder:

```bash
cd samples/Mnemosyne.Samples.AgentFramework
docker compose up -d --wait
dotnet run
docker compose down
```

The database listens on port 5435, so it can run next to the other samples' databases. To use another server, set the `MNEMOSYNE_SAMPLE_PG` environment variable to its connection string. The server needs PostgreSQL 15 or later with pgvector 0.8.0 or later.

### The output

```text
Run 1. Ask "How do I refund an order?" and let the capture run in the background.
  Capture outcome: new_reasonlet. Matches for the same question: 1 ("orders_get → payments_refund", 1 run).

Run 2. Ask "How do I refund an order for a customer?" on a fresh session.
  The first call to the model carried the recalled Reasonlet "orders_get → payments_refund" in its instructions, and no message did.
  Capture outcome: execution_only. Runs of the Reasonlet: 2.
```

- **Run 1.** Nothing is known yet, so the first call carries no recalled Reasonlet. The capture is a new procedure, and it is recallable at once because the review gate is off.
- **Run 2.** The provider recalls the Reasonlet for a differently worded question and returns it as the run's instructions. The sample finds it in the instructions of the first call and in no message of any role. The run only adds an execution to the Reasonlet, which now has 2 runs.

The sample scopes its Reasonlets with a `ScopeResolver` that returns a fixed scope. Without one, the provider uses the agent's name as the scope.

The sample checks all of this itself and exits with code 1 if a receipt differs. What it asserts about the provider is the recall, which must find the Reasonlet for the second question, and the instructions, which must carry its name on the first call and in no message. A change to the provider that broke either would fail the run.

The repository's CI runs the sample on every pull request to `main` and every push to `main`, so a change that broke the recall or the instructions would fail CI.

### The model is a stand-in

`ScriptedChatClient` is not a model. It replays a fixed script of tool calls and an answer. In an application, build the agent on a real chat client from Microsoft.Extensions.AI, for example an Ollama or OpenAI `IChatClient`:

```csharp
var agent = new ChatClientAgent(realClient, new ChatClientAgentOptions
{
    Name = "support-agent",
    AIContextProviders = [new ReasonletContextProvider(provider, new ReasonletAgentOptions())],
});
```

The sample also uses the same offline embedding stand-in as store and recall, so [the same caveat applies](#the-embedding-generator-is-a-stand-in).

## Variants

[`samples/Mnemosyne.Samples.Variants`](https://github.com/MarcelRoozekrans/Mnemosyne.NET/tree/main/samples/Mnemosyne.Samples.Variants) is a console app that shows how a new version of a Reasonlet overtakes a proven one. It needs only the .NET 10 SDK. It needs no Docker, no database, no API key and no model.

### What it does

1. Builds the candidates by hand: an incumbent with 200 runs at a 70% success rate, its new version with no runs, and an unrelated procedure.
2. **Act 1**: `ReasonletSelector` alone over 300 rounds. The new version ties with its parent, the lower id wins, and the new version is never chosen.
3. **Act 2**: `VariantSelector` without exploration returns one match per lineage, so two matches.
4. **Act 3**: `VariantSelector` with `VariantExploration.Thompson` and a seed tries the new version early and then prefers it. The sample prints the counts.
5. Exits with code 1 and names the failed check if any receipt is wrong.

The outcomes come from a seeded `Random` and the selector has a fixed seed, so the counts are the same on every run.

### Run it

From the sample's folder:

```bash
dotnet run
```

### The output

```text
Act 1. ReasonletSelector alone: the incumbent has 200 runs, its new version has none.
  New version chosen in 0 of 300 rounds.
Act 2. VariantSelector without exploration: one match per lineage.
  rebase, original
  squash merge
Act 3. VariantSelector with Thompson sampling, seed 11.
  New version chosen in 11 of the first 20 rounds and 100 of the last 100.
All checks passed.
```

The sample checks these counts itself. Act 1's 0 and Act 2's matches are exact. Act 3 checks at least 1 of the first 20 rounds and at least 60 of the last 100. The repository's CI runs it on every pull request to `main` and every push to `main`. If a change to variant selection broke what this page shows, CI would fail.

[Choosing between variants](guides/variants.md) explains what the sample shows.
