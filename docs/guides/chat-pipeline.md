---
title: Chat pipeline
sidebar_position: 7
---

# Chat pipeline

`UseReasonlets` is chat middleware for `Microsoft.Extensions.AI`. For each new question it recalls a known procedure and offers it to the model as a system message. When the call has finished, it hands the exchange to a background worker that remembers it. This guide shows the setup, what a call does, the optional `find_procedure` tool and the limits.

The code below comes from [`samples/Mnemosyne.Samples.ChatPipeline`](https://github.com/MarcelRoozekrans/Mnemosyne.NET/tree/main/samples/Mnemosyne.Samples.ChatPipeline). The sample runs offline, and [Samples](../samples.md#chat-pipeline) shows its output.

## Set it up

A host registers the store, an embedding generator, the capture guard, the facade and the capture queue:

```csharp
services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString)); // a factory, so the container disposes it
services.AddMnemosynePostgreSql(new MnemosynePostgreSqlOptions { Dimensions = HashingEmbeddingGenerator.Dimensions });
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashingEmbeddingGenerator());
services.AddAISentinel();
services.AddMnemosyneSentinel();
services.AddMnemosyne(new MnemosyneOptions { RequireReview = true });
services.AddReasonletCapture();
```

- `AddMnemosynePostgreSql` is the store, and the `HashingEmbeddingGenerator` is the sample's offline stand-in for a real embedding model.
- `AddMnemosyne` is the memory. The sample turns on `RequireReview`, so a new Reasonlet waits for a person.
- `AddReasonletCapture` is the capture queue and its worker.
- `AddAISentinel` and `AddMnemosyneSentinel` add a guard on what is stored. Only the sample's third act needs them. See "Judges and guards" below.

Then build the client. `UseReasonlets` goes outside `UseFunctionInvocation`:

```csharp
new ChatClientBuilder(scripted)
    .UseReasonlets(new ReasonletChatOptions { DefaultScope = scope })
    .UseFunctionInvocation()
    .Build(provider);
```

The sample passes a scripted client where an application passes the client of its model provider.

Building the pipeline without a registered `IReasonletMemory`, or with capture on and without `AddReasonletCapture`, throws `InvalidOperationException`, so the mistake shows when the client is built. Only a container that supports `IServiceProviderIsService` is checked. Invalid options throw `ArgumentException`.

### Order

`UseReasonlets` must be outside `UseFunctionInvocation`, so that one call to it spans the whole tool loop. Placed inside, each round trip of the loop is a separate call. The first trip ends in a tool call, and capture skips it with the skip reason `pending_tool_calls`. Only the final trip is captured, and it is a continuation, so it carries no injected recall. The injected recall's id and the `find_procedure` lookups are lost, and the capture has no parent.

A response that ends in an unanswered tool call is never captured. This holds when the host drives the tool loop itself: only the round trip that ends in a final answer is captured.

### Scope

Every call needs a scope. The middleware takes `ChatOptions.WithReasonletScope(scope, subjectId)` first, then `ReasonletChatOptions.DefaultScope`. A call with neither passes through untouched: no recall, no capture, no tool. The sample sets `DefaultScope`. A host with more than one tenant sets the scope per call:

```csharp
var options = new ChatOptions().WithReasonletScope(new ReasonletScope("tenant-42"), subjectId: "user-7");
var response = await client.GetResponseAsync(messages, options);
```

`WithReasonletScope` changes the options you give it and returns them, and creates a new `ChatOptions` when given null. The subject id ends up in the capture's provenance. An invalid scope throws `ArgumentException`.

## What a call does

A call is a new question when its last message is a user message with text. A request that ends in tool results or an assistant message is a continuation and gets no recall.

For a new question the middleware:

1. Asks the memory for the best match. `MinSimilarity` is the cut-off, or the selector's own when it is null. [Ranking](../concepts/ranking.md) explains what the match is and how it is scored.
2. Writes the match into one system message, placed after your leading system messages, in a copy of the message list. Your list is not changed. `Formatter` replaces the text.
3. Calls the inner client, tool loop included.
4. Puts the finished exchange in the capture queue and returns the response. It does not wait for the memory.

The sample checks the injection. In act 2, a person approves the Reasonlet that act 1 captured. The question "How do I refund an order for a customer?" then recalls it, and the sample checks that the first call to the model carried its name in the system text. In act 1 the same check finds no recalled Reasonlet in the first call.

The built-in text holds the Reasonlet's name, its similarity, success rate and run count, and its steps with their tool and argument names. It never holds argument values or the text of the question that produced it. Captures from the middleware are named after their tools, such as `orders_get → payments_refund`, so the name carries no question text either. A Reasonlet you remember yourself keeps the name you give it, or the first line of its question when you give none, and that name is what other users of the scope see.

### When it skips

A recall is skipped, and counted on `mnemosyne.chat.recalls` as `skipped` with a `mnemosyne.skip_reason`, in two cases:

| Skip reason | When |
|---|---|
| `no_scope` | The call has no scope, and recall or capture is on |
| `continuation` | The request does not end in a new question, and recall is on |

Capture has its own skip reasons, on `mnemosyne.capture.runs`: `no_question` when no user message has text, `no_tool_calls` when the question's run has no successful tool call, and `pending_tool_calls` when the response ends in a tool call with no answer. A skip is not an error. See [Telemetry](telemetry.md#chat-pipeline-agent-provider-and-capture) for the instruments.

### When recall fails

Memory failures never break the chat call. An error, a timeout, an exception or a formatter that throws means no injection, and they show up only in telemetry. A recall that hits `RecallTimeout` (5 seconds by default) stops waiting, even if the memory ignores the token, and the chat call proceeds. Cancelling the call's own token still throws.

## Background capture

The middleware puts the exchange in a bounded queue, `ReasonletCaptureOptions.Capacity` items, 256 by default. One worker remembers the items one at a time. A full queue drops the new item and counts it as `dropped_full`.

The worker turns the exchange into a Reasonlet. The parent is the injected recall, else the lookup match whose graph equals the run, else the top match of the first `find_procedure` lookup that found anything. The `find_procedure` calls and their results are taken out of the transcript first. [Capture, redaction and review](capture-and-review.md) covers what happens next: the graph, the guard and the review gate.

The sample waits for each capture with a listener on the `mnemosyne.capture.runs` counter, so it never sleeps for a fixed time. It checks the outcome of each:

- act 1 is `new_reasonlet`: a new procedure, stored as pending because `RequireReview` is on. A pending Reasonlet is never recalled, so the same question finds no match;
- act 2 is `execution_only`: the run follows a Reasonlet that is already known, so it adds an execution and nothing is left to review;
- act 3 is `Blocked`, explained below.

The queue drains when the host stops. Without a host, disposing the service provider does the same. The drain is bounded by `ReasonletCaptureOptions.DrainTimeout`, 30 seconds by default, and items left over are counted as `dropped_shutdown`. The worker starts on the first enqueue, so a console app without a host captures too.

The capture's duration is how long the inner call took. For a streamed call it runs from the first read of the stream to the last, so it includes the time your code spends between reads.

## find_procedure

Set `ExposeFindProcedure` and the model gets a `find_procedure` tool, bound to the call's scope, that looks up procedures that worked before for a question. The sample does not use it, so the sample checks nothing here. The snippet is a sketch of the same pipeline with the tool on.

```csharp
services.AddChatClient(inner)
    .UseReasonlets(new ReasonletChatOptions { DefaultScope = new("support"), ExposeFindProcedure = true })
    .UseFunctionInvocation();
```

- **When to expose it.** The injected recall offers one match, the best (the recall asks for `Top = 1`) for the user's question. Expose the tool when the model should be able to look for more, for example with a different wording or for a sub-task. It is off by default because it adds a tool the model sees.
- **What it takes.** A `question`, and optionally `top`, from 1 to 5, which defaults to 3.
- **What it returns.** A JSON array of matches, each with its id, name, version, similarity, success rate, runs and steps. A step is a tool name with its argument names.
- **What it never shows.** The intent, because it is text extracted from one user's question and the users of a scope would see each other's. Argument values are not there either.
- **When it fails.** It returns `{"error":"unavailable"}`. The model learns only that the tool is unavailable, never why, so no store detail reaches the prompt. Arguments it cannot use give `{"error":"invalid_arguments"}`.

A tool of that name already in `ChatOptions.Tools` is kept, and the middleware adds none.

To manage the tool list yourself, create the function with `ReasonletTools.CreateFindProcedure(memory, scope)`. It returns a `FindProcedureFunction`, whose `Lookups` property reports the matches of each lookup the model made. The middleware does not see lookups made through your own instance, so they are removed from captures but do not set a parent.

## Judges and guards

A judge or guard uses a client without `UseReasonlets`. A judge that calls the same client would otherwise be recalled for and captured like a user's question. The chat evaluator's own judge calls bypass the middleware, so it can share the unkeyed client. A guard or judge from another library carries no such mark and needs a keyed client without the middleware. [Judging runs with a chat model](chat-evaluator.md#on-a-client-with-usereasonlets) owns this rule and explains it.

The AI.Sentinel guard in the sample is the other kind of check: it scans what is about to be stored, not a chat call. In act 3 the question is "Which invoice was paid with card 4111 1111 1111 1111?", sent through a client with another tool shape. AI.Sentinel blocks the capture, so the store never sees it. The sample checks the `Blocked` outcome, that no review is pending and that recall for the question finds nothing.

The rest was observed by hand, not checked by the sample. AI.Sentinel's PII detector rates a card number Critical, above the guard's default of `High`. This tool shape is new, so without the guard it would be a second pending review. The detector matches the digit pattern of a card number and does not check the Luhn sum: 16-digit numbers blocked whether or not the sum was valid, and numbers of 12, 15 and 17 digits did not block.

## Limits

- One call to the client is one question and one answer, tool loop included. Conversations that run over several calls are not followed yet.
- A call that threw or was cancelled is not captured, and neither is a stream that is not read to the end.
- A call with no scope passes through untouched.
- A capture needs a successful tool call. A chat with no tools stores nothing.
- Agent Framework agents should use the [Agent Framework context provider](agent-framework.md), not `UseReasonlets`.

The sample checks its own receipts and exits with code 1 if one differs, and the repository's CI runs it. See [Samples](../samples.md#chat-pipeline) for the output and what CI does with it.
