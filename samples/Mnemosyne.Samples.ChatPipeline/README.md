# Chat pipeline

A console walkthrough of `UseReasonlets` with AI.Sentinel and the review gate. It needs only Docker and the .NET 10 SDK, and no API key and no model.

A scripted chat client plays the model. It calls two host tools and then gives a fixed answer. The pipeline is `UseReasonlets` over `UseFunctionInvocation`. The sample asks three questions and prints what Mnemosyne did with each exchange.

## Run it

From this folder:

```bash
docker compose up -d --wait
dotnet run
docker compose down
```

`docker compose up -d --wait` starts PostgreSQL 17 with pgvector and returns once it accepts connections. It listens on port 5434, so it can run next to the other samples' databases. The sample connects to `Host=localhost;Port=5434;Username=postgres;Password=postgres;Database=mnemosyne_chat_sample`, which matches the compose file. To use another server, set `MNEMOSYNE_SAMPLE_PG` to its connection string. It needs PostgreSQL 15 or later with pgvector 0.8.0 or later.

The sample migrates the `mnemosyne` schema with 256 dimensions, so give it a database of its own. Each run works in a fresh scope and erases it at the end.

## What it does

1. Registers the store, an embedding generator, AI.Sentinel with `AddMnemosyneSentinel`, the facade with `RequireReview = true`, and the capture queue with `AddReasonletCapture`. Then it runs `MigrateAsync`.
2. Builds each chat client as `UseReasonlets(...)` outside `UseFunctionInvocation(...)`.
3. Act 1: asks "How do I refund an order?". The scripted model calls `orders_get` and `payments_refund`. The capture is stored as a pending review, and recall for the same question finds nothing, because a pending Reasonlet is never recalled.
4. Act 2: approves the Reasonlet and asks "How do I refund an order for a customer?". The first call to the model carries the recalled Reasonlet in its system text. The capture only records a run.
5. Act 3: asks a question with a card number through a client with another tool shape. AI.Sentinel blocks the capture, so nothing is stored. Without the guard, this shape would have become a new pending Reasonlet.
6. Waits for each capture with `CaptureRunWatcher`, which listens to the `mnemosyne.capture.runs` counter, so the sample never sleeps for a fixed time.
7. Checks every receipt and exits 1 if one differs.
8. Erases the scope with `EraseAsync`.

The output, without the wiring lines:

```text
Act 1. Ask "How do I refund an order?" and let the capture run in the background.
  Capture outcome: new_reasonlet. Pending reviews: 1 ("orders_get → payments_refund"). Matches for the same question: 0.

Act 2. A person approves the Reasonlet. Then ask "How do I refund an order for a customer?".
  The first call to the model carried the recalled Reasonlet "orders_get → payments_refund" in its system text.
  Capture outcome: execution_only. Pending reviews: 0.

Act 3. Ask "Which invoice was paid with card 4111 1111 1111 1111?" through a client with a different tool shape (invoices_get).
  Capture outcome: Blocked. Pending reviews: 0. Matches for the card question: 0.
```

The guard blocks at AI.Sentinel's `High` severity by default. Its PII detector rates a card number Critical. It matches the digit pattern and does not check the Luhn sum, so a 16-digit card-like number blocks whether or not its Luhn sum is valid. Numbers of 12, 15 and 17 digits did not block when tried.

## The stand-ins

`ScriptedChatClient` is not a model. It understands nothing and replays a fixed script: the tool calls, then the answer. In an application, register a real chat model through Microsoft.Extensions.AI, for example an Ollama or OpenAI `IChatClient`, and build the pipeline on it.

`HashingEmbeddingGenerator` lets the sample run offline: it hashes words into 256 buckets, so it sees only shared words. It is deterministic, so the checks are stable, but **it is not a real model**. The second question in act 2 shares most of its words with the first for that reason. The [store-and-recall README](../Mnemosyne.Samples.StoreAndRecall/README.md) shows how to register a real embedding model.

The host tools return fixed strings.
