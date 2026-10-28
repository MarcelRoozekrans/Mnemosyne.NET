# Evaluation

A console walkthrough of how Mnemosyne.NET judges runs. It needs only Docker and the .NET 10 SDK, and no API key and no model.

It registers a rule-based evaluator, records runs for two procedures, and recalls the same question three times. The verdict on one run changes, and the ranking follows. The output shows each step and the ranked matches with the evidence behind their success rate.

## Run it

From this folder:

```bash
docker compose up -d --wait
dotnet run
docker compose down
```

`docker compose up -d --wait` starts PostgreSQL 17 with pgvector and returns once it accepts connections. It listens on port 5433, so it can run next to the [store-and-recall sample](../Mnemosyne.Samples.StoreAndRecall)'s database. The sample connects to `Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=mnemosyne_evaluation_sample`, which matches the compose file. To use another server, set `MNEMOSYNE_SAMPLE_PG` to its connection string. It needs PostgreSQL 15 or later with pgvector 0.8.0 or later.

The sample migrates the `mnemosyne` schema with 256 dimensions, so give it a database of its own. Each run works in a fresh scope and erases it at the end.

## The evaluator

`ChecklistEvaluator` implements `IReasonletEvaluator`. An answer works when it contains a refund reference like `REF-4821` and the word "notified". It looks at the answer alone and ignores whether the agent reported success. It never prints the question or the answer, and never quotes them in an error.

Registering it before `AddMnemosyne()` is all the wiring there is:

```csharp
services.AddSingleton<IReasonletEvaluator, ChecklistEvaluator>();
services.AddMnemosyne();
```

The facade then judges every run recorded with both a question and an answer. A run without them keeps the agent's own verdict.

## What it does

1. Registers the store, an embedding generator, the evaluator and the facade, then runs `MigrateAsync`.
2. Remembers two procedures for near-identical questions, so their similarity is equal and the success rate decides:
   - "refund by hand": 8 runs, none with an answer, so the agent's verdict counts for each;
   - "refund through the API": 5 runs, each with an answer that meets the rule, so the evaluator judges each.
3. Act 1: recalls. "Refund through the API" ranks first.
4. Act 2: records one more run on "refund through the API". The agent reports success, but the answer lacks "notified". The receipt says the evaluator decided and the run failed. Recall again: "refund by hand" ranks first.
5. Act 3: records feedback on that run with `Worked: true`, as a person who checked and found the customer was notified. A person's verdict overrides the evaluator's. Recall again: "refund through the API" ranks first.
6. Checks every receipt, the order after each act and the totals shown below, and exits 1 if not. CI runs the sample, so this is tested.
7. Erases the scope with `EraseAsync`.

The output, without the wiring lines. The execution id in the act 2 receipt is different on every run, so it is shown as a placeholder:

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

Reading the success rates:

- **Runs** counts every run; **Judged** counts those an evaluator or a person decided. "Refund by hand" has 8 runs and none judged, so each carries only the agent's verdict and counts as half a run, the default `AgentVerdictWeight` of 0.5. Its rate is (4 + 5 × 0.5) / (4 + 5) = 0.722.
- **Judged runs count in full.** Five judged successes give (5 + 5 × 0.5) / (5 + 5) = 0.750, which beats the agent-only record.
- **The evaluator can overrule the agent.** In act 2 the agent said the run worked, the answer failed the rule, and the failed judged run drops the rate to (5 + 2.5) / (6 + 5) = 0.682 and the procedure below "refund by hand".
- **A person overrules the evaluator.** In act 3 the run counts as a success, giving (6 + 5 × 0.5) / (6 + 5) = 0.773.

The [ranking concepts page](https://marcelroozekrans.github.io/Mnemosyne.NET/concepts/ranking) explains the ranking in full.

## The embedding generator is a stand-in

`HashingEmbeddingGenerator` is the file the store-and-recall sample uses too: both link `samples/Shared/HashingEmbeddingGenerator.cs`. It lets the sample run offline: it hashes words into 256 buckets, so it sees only shared words. It is deterministic, so the checks are stable, but **it is not a real model**. In an application, register a real one through Microsoft.Extensions.AI; the [store-and-recall README](../Mnemosyne.Samples.StoreAndRecall/README.md) shows Ollama and OpenAI.

## The evaluator is a stand-in too

A word check is not how to judge real answers. In an application, implement `IReasonletEvaluator` over whatever decides whether an answer worked: a test, a schema check, a model asked to grade. Keep the contract: honour the token, return `Unavailable` when the backend fails, and never put the question or the answer in an error.
