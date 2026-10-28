---
title: Writing your own evaluator
sidebar_position: 6
---

# Writing your own evaluator

By default a run counts as a success when the agent says it worked. An evaluator swaps that for a second opinion, as [ranking](../concepts/ranking.md#judged-and-agent-only-runs) explains. Mnemosyne ships two: [Minos](minos-evaluator.md) and [a chat model](chat-evaluator.md). When neither fits, you implement `IReasonletEvaluator` yourself. It has one method:

```csharp
ValueTask<Result<ReasonletEvaluation, MnemosyneError>> EvaluateAsync(ReasonletEvaluationRequest request, CancellationToken ct);
```

[Evaluation](../concepts/evaluation.md) explains the whole loop, from the request to a person's feedback. The facade calls it for each run that arrives with a question and a usable answer. A rule, a classifier, a call to a service of your own: anything that can say whether the run worked will do. The [worked example](#the-worked-example) at the end is a complete one.

## The request

`ReasonletEvaluationRequest` is the run to judge, and the procedure it followed. It arrives valid, so you do not need to re-check it.

| Member | What it holds |
| --- | --- |
| `Graph` | The procedure that was followed. |
| `Name` | The procedure's name. Not blank. |
| `Intent` | The procedure's intent. Not blank. |
| `Question` | The run's question. Not blank. |
| `Answer` | The run's answer. Not blank, valid UTF-16, and at most `EvaluationLimits.MaxRunTextLength` code points: 32,000. |
| `Outcome` | The agent's own view of the run: success, duration and an optional score. |

The facade judges only a usable answer. A blank or over-long answer is not judged at all, so your evaluator never sees one: the run is stored with the agent's verdict.

`Outcome` is context. Your verdict replaces the agent's, so you may read it, weigh it or ignore it. The sample ignores it, so it can overrule an agent that reports success.

The answer reaches your evaluator and nothing else. The facade never stores it or puts it in an error, an exception message or a signal, and `ToString` on the request prints it as `[redacted]`. Keep that promise in your own code. See [errors](#errors).

## The verdict

`ReasonletEvaluation` has three members.

| Member | Rule |
| --- | --- |
| `Success` | Whether the run worked. |
| `Score` | How well it worked, from 0 to 1. |
| `Evaluator` | A short id for the judge, such as `minos` or `my-classifier`. Not blank, valid UTF-16, and at most `EvaluationLimits.MaxEvaluatorLength` code points: 100. |

`Evaluator` is stored with the run and appears in [telemetry](telemetry.md), so it names the judge and nothing about the run. Use a fixed string.

`ReasonletEvaluation.Validate()` checks all of that. The default value fails it, because it names no evaluator. The facade calls `Validate` on every verdict you return. A verdict that fails is a bug in your evaluator, so the facade throws `InvalidOperationException`, whose message names only the failing path. The run is not stored.

There is no reason field. A reason would tend to quote the answer, and the answer must never be stored or logged. If you want to explain a verdict to yourself, do it in your own telemetry, without the question or the answer.

## Errors

Return an error only when the judge's backend fails, such as a model that is down or a service that times out. Use `MnemosyneErrorCode.Unavailable`.

| What your evaluator does | What the facade does |
| --- | --- |
| Returns `Unavailable` | Treats the judge as down. The run is stored, and the agent's verdict counts. The call does not fail. |
| Throws an exception that is not the caller's cancellation | The same: the judge is down, and the agent's verdict counts. This includes an `OperationCanceledException` thrown while the caller's token is not cancelled. |
| Outlasts `EvaluationTimeout` | The same, see [cancellation](#cancellation). |
| Returns any other error code | Throws `InvalidOperationException`, which names only the code. |
| Returns a verdict that fails `Validate` | Throws `InvalidOperationException`. |

Since the request is valid, there is nothing else to fail on. `Validation`, `NotFound` and `Conflict` from an evaluator are bugs, not outcomes.

The facade keeps only the error code. Your error's text is dropped, so it cannot leak. Still, never quote the question or the answer in an error message, an exception or a log line: both can hold customer data.

[When recall fails](../concepts/ranking.md#when-recall-fails) lists what each outcome means for a caller.

## Cancellation

Honour the token. Throw `OperationCanceledException`, as `ct.ThrowIfCancellationRequested()` does. Never return cancellation as an error result.

The facade passes your evaluator the caller's token, linked with a timer for `MnemosyneOptions.EvaluationTimeout`. It is 30 seconds by default. When the time is up, the facade cancels the token and stops waiting, even for a judge that ignores it. That call is abandoned: it may run on, but its outcome is discarded. The run is stored with the agent's verdict, and the evaluate activity shows `Unavailable`.

So honouring the token saves your resources, but it is not what protects the caller from a slow judge. The timeout does that. The caller's own cancellation is different: it propagates as an `OperationCanceledException`, and the run is not stored.

See [settings](../concepts/ranking.md#settings) for `EvaluationTimeout`.

## Register it

Register the evaluator as a singleton. The facade is a singleton and holds it, so a scoped evaluator fails scope validation when the facade is resolved.

```csharp
services.AddSingleton<IReasonletEvaluator, ChecklistEvaluator>();
services.AddMnemosyne();
```

The order does not matter. `AddMnemosyne` looks the evaluator up when the facade is built, so you can register it before or after. Without one, the agent's outcome counts for every run.

## Test it

Test the evaluator alone, with no store, no facade and no database. Build a request and call `EvaluateAsync`.

`ChecklistEvaluator` is `internal`, so a test project outside its assembly needs the evaluator to be public, or needs `InternalsVisibleTo`. Your own evaluator is yours to make public.

```csharp
var evaluator = new ChecklistEvaluator();
var request = new ReasonletEvaluationRequest
{
    Graph = new ReasonletGraph
    {
        Nodes = [new ReasonletNode { Id = "answer", Type = "answer" }],
        Edges = [],
        SchemaVersion = ReasonletGraph.CurrentSchemaVersion,
    },
    Name = "refund by hand",
    Intent = "refund an order",
    Question = "How do I refund an order?",
    Answer = "Refund REF-4821 was issued and the customer was notified by email.",
    Outcome = new ReasonletOutcome(true, TimeSpan.FromSeconds(2), null),
};

var result = await evaluator.EvaluateAsync(request, CancellationToken.None);

Assert.True(result.IsSuccess);
Assert.True(result.Value.Success);
Assert.True(result.Value.Validate().IsSuccess);
```

Three checks are worth having in every evaluator's tests:

- A passing and a failing run give the verdicts you expect.
- Every verdict you can return passes `Validate()`. This is the one the facade enforces.
- A cancelled token throws `OperationCanceledException`.

If your evaluator can fail, also test that its failure is an `Unavailable` result, and that nothing in it quotes the question or the answer.

## Telemetry

Each call to your evaluator runs inside a `mnemosyne.evaluate` activity, with a count and a duration. You write no code for it. When the judge answers with a valid verdict, the activity carries your `Evaluator` id, `Success` and `Score`. When it is down, it shows the failure and no evaluator tag. [Telemetry](telemetry.md#the-evaluate-activity) lists every signal.

## The worked example

`ChecklistEvaluator` is the evaluator of the [evaluation sample](https://github.com/MarcelRoozekrans/Mnemosyne.NET/tree/main/samples/Mnemosyne.Samples.Evaluation). It is rule-based, so the sample judges runs offline with no model. An answer works when it holds a refund reference like `REF-4821` and the word "notified".

```csharp
using System.Text.RegularExpressions;
using Mnemosyne.NET.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.Samples.Evaluation;

/// <summary>
/// A rule-based evaluator, so the sample judges runs offline with no model. An answer works when it contains a refund
/// reference matching <c>REF-\d+</c> and the word "notified", which may be in any case. It judges the answer alone
/// and ignores the agent's own outcome, so it can overrule an agent that reports success.
/// </summary>
/// <remarks>
/// It never prints the question or the answer and never quotes them in an error: both can hold customer data.
/// </remarks>
internal sealed partial class ChecklistEvaluator : IReasonletEvaluator
{
    /// <summary>The name the facade records as the verdict's evaluator.</summary>
    public const string Id = "sample-checklist";

    /// <inheritdoc />
    public ValueTask<Result<ReasonletEvaluation, MnemosyneError>> EvaluateAsync(ReasonletEvaluationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var met = RefundReference().IsMatch(request.Answer)
            && request.Answer.Contains("notified", StringComparison.OrdinalIgnoreCase);
        return ValueTask.FromResult(
            Result<ReasonletEvaluation, MnemosyneError>.Success(new ReasonletEvaluation(Success: met, Score: met ? 1.0 : 0.0, Evaluator: Id)));
    }

    [GeneratedRegex(@"REF-\d+", RegexOptions.CultureInvariant)]
    private static partial Regex RefundReference();
}
```

How it follows the contract:

- It judges the answer alone and ignores `request.Outcome`.
- It returns `Success` and a `Score` of 1 or 0, with a fixed `Evaluator` id, `sample-checklist`, so the verdict passes `Validate`.
- It has no backend, so it never returns `Unavailable`. An evaluator that calls a service would.
- It never prints the question or the answer, and puts neither in an error.
- It checks the token first, so a cancelled call throws.

The sample registers it as shown in [Register it](#register-it), then records runs and recalls, so you can watch the verdicts move the ranking. Its `README.md` explains how to run it.
