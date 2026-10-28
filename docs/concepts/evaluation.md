---
title: Evaluation
sidebar_position: 5
---

# Evaluation

A stored procedure is only worth recalling if it works. Evaluation is how Mnemosyne finds out. This page follows one run from the moment it is recorded to the moment it moves a ranking. The maths of that move lives on the [ranking](ranking.md) page.

## Why evaluate

Every run needs a verdict: did it work? By default the agent gives it, in `ReasonletOutcome.Success`. The agent grades its own work, and a model is an easy marker of its own homework. A procedure that fails quietly can keep a good record for a long time.

So Mnemosyne lets a second opinion in. An evaluator, which is a judge you register, can look at the run's answer. A person can overrule everyone afterwards. Ranking trusts a judged run more than an agent-only one, as [ranking](ranking.md#judged-and-agent-only-runs) explains.

## Who decides whether a run worked

A run has up to three verdicts. The first one that exists counts:

| Order | Verdict | Where it comes from |
|---|---|---|
| 1 | A human's | `ReasonletFeedback.Worked`, recorded with `RecordFeedbackAsync`. |
| 2 | An evaluator's | `ReasonletEvaluation.Success`, from the registered `IReasonletEvaluator`. |
| 3 | The agent's | `ReasonletOutcome.Success`, which every run has. |

A run with a human or an evaluator verdict is **judged**. A run with only the agent's is **agent-only**. How much each counts is in [the success rate](ranking.md#success-rate).

## When the evaluator is asked

The facade asks the evaluator when all of these hold:

- An `IReasonletEvaluator` is registered. Without one, the agent's verdict counts for every run.
- The run arrives with a question and an answer.
- The answer is usable: not blank, and at most 32,000 Unicode code points.

It asks in two places. `RememberAsync` judges the run it records, when the capture carries an `Answer`. `RecordExecutionAsync` judges the run it records, when the `ReasonletRun` carries a `Question` and an `Answer`.

On a `ReasonletRun`, `Question` and `Answer` are set together or not at all. Setting one without the other fails validation.

```csharp
var receipt = await memory.RecordExecutionAsync(new ReasonletRun
{
    Scope = scope,
    Id = id,
    Outcome = new ReasonletOutcome(true, TimeSpan.FromSeconds(2), null),
    Provenance = provenance,
    Question = "How do I refund an order?",
    Answer = "Refund REF-4821 was issued and the customer was notified by email.",
}, ct);
```

**The answer is never stored.** It goes to the evaluator and nowhere else. It is not in the store, an error, an exception message or a telemetry signal, and `ToString` prints it as `[redacted]`.

## What comes back

Both calls return a receipt that says which verdict counted when the run was stored. `RecordExecutionAsync` returns a `ReasonletRunReceipt`, and the receipt from remember carries the same two fields.

| Field | Meaning |
|---|---|
| `Source` | `Evaluator` when the judge answered, `Agent` otherwise. |
| `Success` | The verdict of that source. |

`Source` is never `Feedback`. Feedback comes after the run is stored, so no receipt can carry it.

```csharp
if (receipt.Value.Source == ReasonletVerdictSource.Evaluator && !receipt.Value.Success)
{
    // The judge disagreed with the agent, or confirmed a failure.
}
```

## Feedback

A person can overrule both the agent and the evaluator, later, with `RecordFeedbackAsync`. It takes the scope, the run's `ExecutionId` from its receipt, and a `ReasonletFeedback`.

```csharp
await memory.RecordFeedbackAsync(
    scope, receipt.Value.Execution, new ReasonletFeedback(Utility: 0.9, Correctness: 1, Reusability: 0.8, Worked: true), ct);
```

- **`Worked` is the verdict.** When it is set, it counts instead of the evaluator's and the agent's. When the new verdict differs from the old one, the success count moves with it.
- **A verdict can be changed but not withdrawn.** Feedback with `Worked` null keeps the earlier verdict. To change it, record the other value.
- **The three scores are stored but not ranked.** `Utility`, `Correctness` and `Reusability` are each from 0 to 1. Feedback replaces the run's earlier scores. Ranking ignores them.
- **Feedback makes an agent-only run judged.** That holds even when the person agrees with the agent: the run then counts in full.
- **It can fail.** An execution that does not exist in the scope gives `NotFound`, also when it exists only in another scope.

## When the judge is down

A judge that fails never fails the call. The run is stored with the agent's own verdict, and the receipt's `Source` is `Agent`. This happens when:

| Situation | Result |
|---|---|
| The evaluator returns `Unavailable`. | The judge is down. The agent's verdict counts. |
| It throws, or times out on its own. | The same. |
| It outlasts `MnemosyneOptions.EvaluationTimeout`. | The same. The facade cancels the token it gave the judge and stops waiting, even for a judge that ignores the token. The default is 30 seconds. |
| The answer is blank or longer than 32,000 code points. | The judge is not asked. The answer is the agent's own output, so it doesn't fail the call. |

Two things still stop the call:

- **An adapter bug.** Any other error code from the evaluator, or a verdict that fails `ReasonletEvaluation.Validate`, throws `InvalidOperationException`, and the run is not stored. It names only the code or the failing field, never the answer.
- **Your own cancellation.** It throws `OperationCanceledException`.

[When recall fails](ranking.md#when-recall-fails) has the full table, and [settings](ranking.md#settings) lists `EvaluationTimeout`. The [custom evaluator guide](../guides/custom-evaluator.md#errors) says what your own evaluator should return.

## Reading the evidence

Runs without a judge stay agent-only, so a high success rate can rest on little. Each `ReasonletMatch` carries a `ReasonletEvidence`:

| Field | Meaning |
|---|---|
| `Runs` | The Reasonlet's own number of runs. |
| `JudgedRuns` | How many of them a human or an evaluator judged. |
| `UsesParentPrior` | Whether the rate was smoothed toward the parent's. |

A match with `JudgedRuns` of 0 rests on the agent's word alone. Treat its high success rate with care: show a caveat, ask for a check, or pick another match. See [the evidence on each match](ranking.md#the-evidence-on-each-match).

## Choosing a judge

| Judge | Pick it when | Guide |
|---|---|---|
| Minos | You use Minos.NET decision models. | [Judging runs with Minos](../guides/minos-evaluator.md) |
| A chat model | You already have a chat model and want it to judge. | [Judging runs with a chat model](../guides/chat-evaluator.md) |
| Your own | A rule, a classifier or a service of yours fits better, or you want no model. | [Writing your own evaluator](../guides/custom-evaluator.md) |

## See it run

The [evaluation sample](https://github.com/MarcelRoozekrans/Mnemosyne.NET/tree/main/samples/Mnemosyne.Samples.Evaluation) judges runs offline with a rule-based evaluator, then lets a person overrule one, and prints how the ranking moves each time. It needs only Docker and the .NET 10 SDK.
