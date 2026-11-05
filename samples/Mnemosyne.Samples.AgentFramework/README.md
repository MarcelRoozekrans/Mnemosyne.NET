# Agent Framework

A console walkthrough of `ReasonletContextProvider` on a Microsoft Agent Framework agent. It needs only Docker and the .NET 10 SDK, and no API key and no model.

A scripted chat client plays the model. It calls two host tools and then gives a fixed answer. The agent is a `ChatClientAgent` with one `ReasonletContextProvider` in its `AIContextProviders`. The sample runs it twice and prints what Mnemosyne did with each run.

## Run it

From this folder:

```bash
docker compose up -d --wait
dotnet run
docker compose down
```

`docker compose up -d --wait` starts PostgreSQL 17 with pgvector and returns once it accepts connections. It listens on port 5435, so it can run next to the other samples' databases. The sample connects to `Host=localhost;Port=5435;Username=postgres;Password=postgres;Database=mnemosyne_agent_sample`, which matches the compose file. To use another server, set `MNEMOSYNE_SAMPLE_PG` to its connection string. It needs PostgreSQL 15 or later with pgvector 0.8.0 or later.

The sample migrates the `mnemosyne` schema with 256 dimensions, so give it a database of its own. Each run works in a fresh scope and erases it at the end.

## What it does

1. Registers the store, an embedding generator, the facade with `AddMnemosyne` and the capture queue with `AddReasonletCapture`. The review gate is off, so a captured procedure is recallable at once.
2. Runs `MigrateAsync`.
3. Builds the agent with the two host tools in its `ChatOptions.Tools` and a `ReasonletContextProvider`. The provider's `ScopeResolver` returns a fixed scope, so the sample can erase it. The default resolver would use the agent's name.
4. Run 1: asks "How do I refund an order?". The scripted model calls `orders_get` and `payments_refund`. The capture stores a new Reasonlet. The first call to the model carried no recalled Reasonlet, since nothing was known yet.
5. Run 2: asks "How do I refund an order for a customer?" on a fresh session. The provider recalls the Reasonlet and adds it to the run's instructions. The sample checks that the Reasonlet's name is in the system text of the first call, and that no user or assistant message of the run holds it. The capture only records an execution, so the Reasonlet now has 2 runs.
6. After each run, waits for its capture with `CaptureRunWatcher`, which listens to the `mnemosyne.capture.runs` counter, so the sample never sleeps for a fixed time. It checks every receipt along the way and exits 1 if one differs.
7. Erases the scope with `EraseAsync`, whatever happened above.

The output, without the wiring lines:

```text
Run 1. Ask "How do I refund an order?" and let the capture run in the background.
  Capture outcome: new_reasonlet. Matches for the same question: 1 ("orders_get → payments_refund", 1 run).

Run 2. Ask "How do I refund an order for a customer?" on a fresh session.
  The first call to the model carried the recalled Reasonlet "orders_get → payments_refund" in its instructions, and no message did.
  Capture outcome: execution_only. Runs of the Reasonlet: 2.
```

## The stand-ins

`ScriptedChatClient` is not a model. It understands nothing and replays a fixed script: the tool calls, then the answer. In an application, register a real chat model through Microsoft.Extensions.AI, for example an Ollama or OpenAI `IChatClient`, and build the agent on it.

`HashingEmbeddingGenerator` lets the sample run offline: it hashes words into 256 buckets, so it sees only shared words. It is deterministic, so the checks are stable, but **it is not a real model**. The second question in run 2 shares most of its words with the first for that reason. The [store-and-recall README](../Mnemosyne.Samples.StoreAndRecall/README.md) shows how to register a real embedding model.

The host tools return fixed strings.
