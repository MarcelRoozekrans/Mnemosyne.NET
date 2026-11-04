# Mnemosyne.NET

Reasonlet selection and ranking: find the best known procedure for a question.

An agent asks a question and gets back the few Reasonlets most worth trying, best first, each with a score breakdown of similarity, success rate and freshness. The agent still makes the final call. This package holds:

- `ReasonletMemory`, the default `IReasonletMemory` facade. It ties together a store, a selector, an embedding generator, an optional intent extractor and an optional evaluator.
- `ReasonletSelector`, the default `IReasonletSelector`, with its `ReasonletRankingOptions`.
- `MnemosyneOptions` and the `AddMnemosyne` registration.
- `ReasonletTranscript`, which turns a `Microsoft.Extensions.AI` chat transcript into a capture.

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

## Capture from a chat transcript

`ReasonletTranscript.ToCapture` turns the messages of a tool-calling chat into a `ReasonletCapture`. The question is the last user message with text, and the answer is the last assistant text after it. The graph is the run's tool shape: one `tool` node per successful call, in order, with the argument names, not their values, in a `mnemosyne.arguments` property. Failed calls are left out, and a retry takes the failed call's place. Calls made together are sorted, and repeats in a row merge into one step. So a rerun with other inputs has the same graph, and with the recalled Reasonlet as its parent it is recorded as an execution, not a new version. Graphs you write by hand without `mnemosyne.arguments` have a different shape from captured ones.

```csharp
// messages is the chat's IList<ChatMessage>, after the model's tool loop has run.
var capture = ReasonletTranscript.ToCapture(
    messages,
    new TranscriptCaptureDetails
    {
        Scope = scope,
        Provenance = new ReasonletProvenance { Source = "support-agent" },
        Outcome = new ReasonletOutcome(Success: true, Duration: elapsed, Score: null),
        ParentId = match?.Reasonlet.Id, // the Reasonlet recall offered, if the run followed one
    });

if (capture.IsFailure)
{
    // NoQuestion or NoToolCalls: nothing to remember.
    return;
}

var remembered = await memory.RememberAsync(capture.Value, ct);
```

A `UseReasonlets()` chat client middleware that does this for you is planned for a later release.

## Judging runs

By default a run counts as a success when the agent says it worked. Register an `IReasonletEvaluator` and give the run its question and answer, and the evaluator's verdict counts instead. The answer goes to the evaluator and is never stored. A judge that is down, or slower than `MnemosyneOptions.EvaluationTimeout` (30 seconds by default), doesn't fail the call: the run is stored with the agent's verdict. So is a run whose answer is blank or longer than 32,000 code points, which is not judged. The receipt says which verdict counted. A person's verdict, recorded with feedback, overrides both. The `Mnemosyne.NET.Minos` package is a ready-made judge that asks a Minos.NET decision model: see [Judging runs with Minos](https://marcelroozekrans.github.io/Mnemosyne.NET/guides/minos-evaluator). The core package also has `AddMnemosyneChatEvaluator`, which judges with any `Microsoft.Extensions.AI` `IChatClient`: see [Judging runs with a chat model](https://marcelroozekrans.github.io/Mnemosyne.NET/guides/chat-evaluator).

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

## Capture guard

Register an `ICaptureGuard` and `RememberAsync` asks it before anything else is done with a capture. The guard sees the capture after it validates, and before the intent is resolved, the embedding is made and the judge runs. It answers with a verdict: allow, or block with the name of the detector that fired.

A blocked capture is not stored. The remember fails with the error code `Blocked`. A guard that is down fails closed: if it throws, times out after `MnemosyneOptions.GuardTimeout` or answers `Unavailable`, the remember fails with `Unavailable` and nothing is stored. Only `RememberAsync` is guarded, not `RecordExecutionAsync`. Without a guard, every capture is remembered as before.

An AI.Sentinel adapter for the guard arrives in a later package.

## Human review gate

Set `MnemosyneOptions.RequireReview` and a new Reasonlet, or a new version of one, is stored as `PendingReview` instead of `Active`. A pending Reasonlet is never recalled, and the receipt of the remember says `PendingReview`. A capture that records only a run, with the same graph as its parent, changes nothing about the parent's status. The option is off by default, and with it off nothing changes.

A reviewer works through three calls on `IReasonletMemory`, so a review screen needs no access to the store:

```csharp
services.AddMnemosyne(new MnemosyneOptions { RequireReview = true });

// A review screen starts from the beginning each time: ids are creation order, and a Reasonlet stored while
// a reviewer pages can land behind the cursor.
var pending = await memory.ListPendingAsync(scope, limit: 20, after: null, ct);
if (pending.IsSuccess)
{
    foreach (var review in pending.Value)
    {
        // The definition to judge, and the Reasonlet's own track record.
        Console.WriteLine($"{review.Definition.Name}: {review.Metrics.Runs} runs, {review.Metrics.Successes} successes");
    }
}

// Approve makes it recallable from the next query. Reject deletes it, with its runs and feedback.
var approved = await memory.ApproveAsync(scope, id, ct);
var rejected = await memory.RejectAsync(scope, otherId, ct);
```

`limit` is from 1 to 100, and `after` is the last id of the previous page. Approving an `Active` Reasonlet succeeds and changes nothing. Rejecting an `Active` Reasonlet, or one that has versions below it, answers `Conflict`; approve or reject those versions first. A Reasonlet that is not in the scope answers `NotFound`. See [Data lifecycle](https://marcelroozekrans.github.io/Mnemosyne.NET/guides/data-lifecycle#the-review-gate) for the details.

## Chat pipeline

`UseReasonlets` is chat middleware for `Microsoft.Extensions.AI`. For each new question it recalls a known procedure and offers it to the model as a system message, and when the call has finished it hands the exchange to a background worker that remembers it.

```csharp
services.AddMnemosyne();                 // the memory
services.AddReasonletCapture();          // the capture queue and its worker
services.AddChatClient(inner)
    .UseReasonlets(new ReasonletChatOptions { DefaultScope = new("support") })
    .UseFunctionInvocation();
```

Building the pipeline without a registered `IReasonletMemory`, or with capture on and without `AddReasonletCapture`, throws `InvalidOperationException`, so the mistake shows at startup. Invalid options throw `ArgumentException`. Set `Capture = false` for recall only, or `Recall = false` for capture only.

The middleware resolves the memory and the capture queue on its first call, not when the client is built. So `AddMnemosyneChatEvaluator()` can judge with this same unkeyed client: the memory needs the evaluator, the evaluator needs the client, and the client needs the memory only once it is called. The middleware passes the evaluator's judge calls straight through, without recall, tool, capture or count. See [Judging runs with a chat model](https://marcelroozekrans.github.io/Mnemosyne.NET/guides/chat-evaluator#on-a-client-with-usereasonlets).

### Order

Put `UseReasonlets` outside `UseFunctionInvocation`, as above, so one call to it spans the whole tool loop. Placed inside, each round trip of the loop is a separate call. The first trip ends in a tool call and is skipped with the skip reason `pending_tool_calls`. Only the final trip is captured, and it is a continuation, so it carries no injected recall. The injected recall's id and the `find_procedure` lookups are lost, and the capture has no parent.

A response that ends in an unanswered tool call is never captured. This holds when the host drives the tool loop itself: only the round trip that ends in a final answer is captured.

### Scope

Every call needs a scope. The middleware takes `ChatOptions.WithReasonletScope(scope, subjectId)` first, then `ReasonletChatOptions.DefaultScope`. A call with neither passes through untouched: no recall, no capture, no tool.

```csharp
var options = new ChatOptions().WithReasonletScope(new ReasonletScope("tenant-42"), subjectId: "user-7");
var response = await client.GetResponseAsync(messages, options);
```

`WithReasonletScope` changes the options you give it and returns them, and creates a new `ChatOptions` when given null. The subject id ends up in the capture's provenance. An invalid scope throws `ArgumentException`.

### Recall and injection

A call is a new question when its last message is a user message with text. A request that ends in tool results or an assistant message is a continuation and gets no recall. The recall asks for the best match, with `MinSimilarity` as the cut-off, or the selector's own when it is null. A match becomes one system message, placed after your leading system messages, in a copy of the message list. Your list is not changed. `Formatter` replaces the text.

The built-in text holds the Reasonlet's name, its similarity, success rate and run count, and its steps with their tool and argument names. It never holds argument values or the text of the question that produced it. Captures from the middleware are named after their tools, such as `orders.get → payments.refund`, so the name carries no question text either. A Reasonlet you remember yourself keeps the name you give it, or the first line of its question when you give none, and that name is what other users of the scope see.

A recall that hits `RecallTimeout` stops waiting, even if the memory ignores the token, and the chat call proceeds. Memory failures never break the chat call: an error, a timeout, an exception or a formatter that throws means no injection, and they show up only in telemetry. Cancelling the call's own token still throws.

### find_procedure

Set `ExposeFindProcedure` and the model gets a `find_procedure` tool, bound to the call's scope, that looks up procedures that worked before for a question. It returns a JSON array of matches with their steps, and `{"error":"unavailable"}` when the lookup fails. A tool of that name already in `ChatOptions.Tools` is kept. It is off by default because it adds a tool the model sees.

To manage the tool list yourself, create the function with `ReasonletTools.CreateFindProcedure(memory, scope)`. The middleware does not see lookups made through your own instance, so they are removed from captures but do not set a parent.

### Limits

One call to the client is one question and one answer, tool loop included. Conversations that run over several calls are not followed. That is planned for phase 3.6. A call that threw or was cancelled is not captured, and neither is a stream that is not read to the end.

### Background capture

The middleware does not wait for the memory. It puts the exchange in a bounded queue, `ReasonletCaptureOptions.Capacity` items, 256 by default, and one worker remembers the items one at a time. A full queue drops the new item and counts it as `dropped_full`. The `find_procedure` calls and their results are taken out of the transcript first. The parent is the injected recall, else the lookup match whose graph equals the run, else the top match of the first lookup that found anything.

The queue drains when the host stops. Without a host, disposing the service provider does the same, synchronously or asynchronously. The drain is bounded by `ReasonletCaptureOptions.DrainTimeout`, 30 seconds by default. When it runs out, the remaining work is cancelled and the drain waits for the memory to honour that, which the built-in memory does. Items left over are counted as `dropped_shutdown`. The worker starts on the first enqueue, so a console app without a host captures too.

The capture's duration is how long the inner call took. For a streamed call it runs from the first read of the stream to the last, so it includes the time your code spends between reads.

### Telemetry

On the existing activity source and meter. The `outcome` tag carries the values below.

| Name | Kind | Outcomes |
|---|---|---|
| `mnemosyne.chat.recall` | activity | as the counter |
| `mnemosyne.chat.recalls` | counter | `injected`, `no_match`, `timeout`, the error code name, `exception`, `formatter_failed`, `skipped` |
| `mnemosyne.capture.queue` | counter | `enqueued`, `dropped_full`, `dropped_shutdown`, `exception` |
| `mnemosyne.capture.run` | activity | as the counter |
| `mnemosyne.capture.runs` | counter | `new_reasonlet`, `new_version`, `execution_only`, `skipped`, the error code name, `exception` |
| `mnemosyne.capture.duration` | histogram | none |

A skipped recall carries the tag `mnemosyne.skip_reason` with `no_scope` or `continuation`. A call without a scope counts as `no_scope` whenever `Recall` or `Capture` is on. A skipped capture carries the same tag with `no_question`, `no_tool_calls` or `pending_tool_calls`. On `mnemosyne.capture.queue`, `exception` counts a finished call whose capture item could not be built or offered, with `error.type` set to the exception's type name; the chat call still returns its response.

## Documentation

- [Getting started](https://marcelroozekrans.github.io/Mnemosyne.NET/getting-started): a complete console app, and what `AddMnemosyne` registers.
- [Ranking](https://marcelroozekrans.github.io/Mnemosyne.NET/concepts/ranking): what recall does, the cut-off, the score, every option, how recall fails, and [a worked example](https://marcelroozekrans.github.io/Mnemosyne.NET/concepts/ranking#a-worked-example).
- [Reasonlets](https://marcelroozekrans.github.io/Mnemosyne.NET/concepts/reasonlets): what a capture holds, how intent and name are filled in, and what recall returns.
- [Versions and lineage](https://marcelroozekrans.github.io/Mnemosyne.NET/concepts/versions): how a capture becomes a new Reasonlet, a new version or a run.
- [Judging runs with Minos](https://marcelroozekrans.github.io/Mnemosyne.NET/guides/minos-evaluator): a ready-made evaluator, its threshold, failures and privacy.
- [Judging runs with a chat model](https://marcelroozekrans.github.io/Mnemosyne.NET/guides/chat-evaluator): an evaluator for any chat client, its criteria, reply format and failures.
- [Data lifecycle](https://marcelroozekrans.github.io/Mnemosyne.NET/guides/data-lifecycle): the review gate, erasure and retention.
- [Telemetry](https://marcelroozekrans.github.io/Mnemosyne.NET/guides/telemetry): the recall, evaluate and guard activities and metrics, alongside the store's.

See the [repository README](https://github.com/MarcelRoozekrans/Mnemosyne.NET#readme).
