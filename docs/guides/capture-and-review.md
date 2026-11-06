---
title: Capture, redaction and review
sidebar_position: 8
---

# Capture, redaction and review

A capture is a finished run on its way into memory. Before it is stored, a guard can refuse it, and a review gate can hold the result back until a person approves it. This guide follows a capture through those steps: how a chat transcript becomes a graph, what the store makes of it, what the capture guard checks, and how the review gate works.

The outputs below come from the chat pipeline sample, [`samples/Mnemosyne.Samples.ChatPipeline`](https://github.com/MarcelRoozekrans/Mnemosyne.NET/tree/main/samples/Mnemosyne.Samples.ChatPipeline). It runs offline, checks its own receipts, and [Samples](../samples.md#chat-pipeline) shows its output. The [chat pipeline guide](chat-pipeline.md) covers the middleware that feeds it, and this page does not repeat that.

The sample wires the guard and the gate with three lines:

```csharp
services.AddAISentinel();
services.AddMnemosyneSentinel();
services.AddMnemosyne(new MnemosyneOptions { RequireReview = true });
```

## From transcript to graph

`UseReasonlets` hands each finished exchange to a background worker, which calls `ReasonletTranscript.ToCapture`. Hosts that do not use the middleware can call it themselves. The graph is the tool shape of the last question's run:

- The question is the last user message with text. The run is every message after it.
- Each successful tool call is one `tool` node, in order, and edges chain them. Failed calls are left out, and a call that retries a failed one takes its place.
- A node holds the tool name and the argument names, in a `mnemosyne.arguments` property. Argument values are never read.
- Calls made together are sorted, and equal steps in a row merge into one.

So two runs with the same shape have the same graph, whatever the inputs. That is what lets a rerun count as an execution of a known procedure and not as a new one. [Reasonlets](../concepts/reasonlets.md#the-graph) explains the graph itself.

The question and the answer are copied as they are: nothing in the transcript step redacts them. The answer goes to an evaluator, if one is registered, and is never stored. The question is embedded for recall. The guard is the place to keep a secret out of a capture.

### The three outcomes

`RememberAsync` returns a receipt whose `Kind` says what the capture produced. [Versions and lineage](../concepts/versions.md#three-outcomes-of-a-capture) has the full table. In short:

| Capture | Kind |
|---|---|
| No parent, and the scope holds no Reasonlet with this graph | `NewReasonlet`, at version 1.0 |
| A parent, and a different graph | `NewVersion`, linked to the parent |
| A parent, and the graph of the parent | `ExecutionOnly`: a run is recorded and nothing new is stored |
| No parent, and the scope already holds this graph | `ExecutionOnly`, on the Reasonlet that holds it |

The worker counts the kind on `mnemosyne.capture.runs` ([Telemetry](telemetry.md#chat-pipeline-agent-provider-and-capture) lists its outcomes) as `new_reasonlet`, `new_version` or `execution_only`. The sample shows two of them. Act 1 is `new_reasonlet`. Act 2 asks a differently worded question that recalls the Reasonlet from act 1, and the capture is `execution_only`. The worker takes the recalled Reasonlet as the capture's parent, and the run has the parent's graph. The outcome would be the same without the parent, since the scope already holds that graph. [Background capture](chat-pipeline.md#background-capture) explains how the worker picks the parent. The sample does not produce `new_version`.

## The capture guard

An `ICaptureGuard` inspects a capture before it is stored. Register one and `RememberAsync` asks it first, once the capture has validated. The guard runs before the intent is resolved, the question is embedded and a judge is called, so a blocked capture costs none of those calls.

```csharp
ValueTask<Result<CaptureVerdict, MnemosyneError>> InspectAsync(ReasonletCapture capture, CancellationToken ct);
```

The guard must be registered as a singleton. It answers with a `CaptureVerdict`:

- `CaptureVerdict.Allow(guard)` lets the capture through.
- `CaptureVerdict.Block(guard, detector)` blocks it. Both names must be non-blank, valid UTF-16 and at most 100 code points. A guard must not quote the capture in either.

What the facade does with the answer:

| The guard | The remember |
|---|---|
| allows | goes on |
| blocks | fails with `Blocked`. Nothing is stored and no execution is recorded, even when the capture has a parent. The error detail is the guard name, a slash and the detector name |
| answers `Unavailable`, throws, or takes longer than `MnemosyneOptions.GuardTimeout` | fails with `Unavailable`. Nothing is stored |
| fails with any other code, or returns an invalid verdict | throws `InvalidOperationException`, because that is a bug in the guard |

`GuardTimeout` is 30 seconds by default. When it runs out, the facade cancels the token it gave the guard and stops waiting, even for a guard that ignores the token. The caller's own cancellation still throws `OperationCanceledException`. A guard that is down fails closed: a capture nobody inspected is never stored. A judge is the opposite, because a run whose judge is down is still stored with the agent's own verdict.

In the chat pipeline, a blocked capture never reaches the reply. The call returns its answer as usual, and the worker counts the capture as `Blocked`. The sample checks both. For an `Unavailable` guard the worker counts the error code's name too, and the capture is lost: nothing retries it.

### What is guarded

Only `RememberAsync` is guarded. `RecordExecutionAsync` is not: it records a run on a Reasonlet that is already stored, and takes no graph. It can take a question and an answer, to let an evaluator judge the run. They are never stored, but they reach the evaluator without passing the guard. A capture that would be `ExecutionOnly` still passes the guard first, because the guard runs before the store decides. The guard lives in the facade, so a caller that writes to `IReasonletStore` directly bypasses it. Reasonlets stored before a guard was registered are not scanned again.

[Telemetry](telemetry.md#the-guard-activity) lists the guard's activity, counter and tags. They name the guard and the detector, never the text that matched.

## The AI.Sentinel adapter

`Mnemosyne.NET.Sentinel` is a ready-made guard built on [AI.Sentinel](https://github.com/MarcelRoozekrans/AI.Sentinel). The host registers AI.Sentinel itself, then the adapter. The order of the two calls does not matter, and `AddMnemosyne` picks the guard up either way. The sample's three lines above do exactly that.

The guard runs AI.Sentinel's detection pipeline and blocks when a detector reaches `SentinelCaptureGuardOptions.BlockAtSeverity`. The default is `High`. It must be `Low`, `Medium`, `High` or `Critical`. `PipelineName` gives the guard its own named pipeline, and `GuardName` renames the guard in verdicts and telemetry, `ai-sentinel` by default.

This sketch lowers the threshold. It is not from the sample:

```csharp
using AI.Sentinel.Detection;

services.AddMnemosyneSentinel(new SentinelCaptureGuardOptions { BlockAtSeverity = Severity.Medium });
```

A lower threshold blocks every detection of that severity, not only the PII you meant to catch, so watch for false positives. The [adapter README](https://github.com/MarcelRoozekrans/Mnemosyne.NET/blob/main/src/Mnemosyne.NET.Sentinel/README.md) has the options table and the notes on a separate pipeline for the guard.

### What it scans

The pipeline sees the capture's question, its intent and its name, as one user message each, in that order. A blank intent or name is left out. It sees nothing else: not the answer, the graph or the provenance. The answer of a chat call is the business of AI.Sentinel's chat middleware on the host's client, not of this guard.

For a capture that the chat middleware built, the intent is empty at that point, because the facade resolves it after the guard runs. The name is the tool shape. So the question and the tool-shape name get scanned, and the question is the part that can carry a secret. That follows from the code, and the sample does not check it.

### Act 3

In act 3 the question carries a card number and goes through a client with a different tool shape:

```text
Act 3. Ask "Which invoice was paid with card 4111 1111 1111 1111?" through a client with a different tool shape (invoices_get).
  Capture outcome: Blocked. Pending reviews: 0. Matches for the card question: 0.
```

The sample checks the reply, the `Blocked` outcome, that no review is pending and that recall for the question finds nothing. [Chat pipeline](chat-pipeline.md#judges-and-guards) lists what was only observed by hand, such as the card number's severity. The adapter README lists which PII AI.Sentinel rates High and which Medium. Read it before relying on the default for emails or phone numbers.

### A known limit: a throwing detector

The guard fails closed when the pipeline as a whole throws, and when the facade's timeout runs out. It does not fail closed when a single detector throws. The pipeline catches that exception, skips the detector, calls `SentinelOptions.OnDetectorFailure` and counts the detector's result as clean. The result carries no failure signal, so the guard cannot tell. A detector that depends on an endpoint, such as a semantic one, is skipped while the endpoint is down, and the capture can be stored without that control.

This was reported as [AI.Sentinel#550](https://github.com/MarcelRoozekrans/AI.Sentinel/issues/550). The issue is closed: upstream closed it on 2026-11-04 as completed, with a fix that adds `PipelineResult.Failures` and `IsComplete`. The version this repository pins, AI.Sentinel 4.2.0, does not have those members, and the adapter does not read them. A unit test pins the gap: with a throwing detector, the capture is allowed and the store is called. Until the adapter moves to a release with the signal, set `OnDetectorFailure` in the host's AI.Sentinel options to learn about a skipped detector. Adopting the fix is tracked in [Mnemosyne.NET#58](https://github.com/MarcelRoozekrans/Mnemosyne.NET/issues/58). The sample does not exercise this.

## The review gate

By default a capture is stored as `Active` and recalled at once. With `MnemosyneOptions.RequireReview` on, a new Reasonlet and a new version are stored as `PendingReview`, and the receipt says so. A run recorded against an existing Reasonlet never changes its status. The option is off by default.

A pending Reasonlet is never recalled. A reviewer works through three calls on `IReasonletMemory`:

- `ListPendingAsync(scope, limit, after, ct)` returns the pending Reasonlets of a scope, with their definitions and their own metrics. `limit` is from 1 to 100, and `after` is the last id of the previous page.
- `ApproveAsync(scope, id, ct)` makes a pending Reasonlet `Active`, so the next recall can return it.
- `RejectAsync(scope, id, ct)` deletes a pending Reasonlet with its runs and feedback. It answers `Conflict` for an `Active` Reasonlet and for one with versions below it.

[The review gate](data-lifecycle.md#the-review-gate) in the data lifecycle guide owns the details: the paging caveat, the error codes, rolling upgrades and retention of pending Reasonlets.

### Act 1 and act 2

The sample uses the first two calls, and not `RejectAsync`. Act 1 captures a new procedure and stops at the gate. A person approves it, and act 2 recalls it:

```text
Act 1. Ask "How do I refund an order?" and let the capture run in the background.
  Capture outcome: new_reasonlet. Pending reviews: 1 ("orders_get → payments_refund"). Matches for the same question: 0.

Act 2. A person approves the Reasonlet. Then ask "How do I refund an order for a customer?".
  The first call to the model carried the recalled Reasonlet "orders_get → payments_refund" in its system text.
  Capture outcome: execution_only. Pending reviews: 0.
```

The sample checks each number and that the first call in act 1 carried no recalled Reasonlet. Between act 1 and the approval, the same question finds nothing, so an agent never follows a procedure nobody has looked at. Act 2's capture records a run on the approved Reasonlet and leaves nothing to review.

A review screen reads the pending list from the start each time, because ids follow creation order and a Reasonlet stored while a reviewer pages can land behind the cursor. The names of captures from the middleware are their tool shapes, such as `orders_get → payments_refund`, so a reviewer sees what the procedure does, not the question that produced it.

## Limits

- The guard is as good as its detectors. It sees the question, intent and name, and not the answer or the graph.
- A throwing detector is skipped and counted as clean. See the known limit above.
- A capture the guard cannot inspect is lost. The chat pipeline does not retry it.
- Only `RememberAsync` is guarded. `RecordExecutionAsync` and direct writes to the store are not, and the question and answer of a run pass to the evaluator unguarded.
- The gate holds back new Reasonlets and new versions. It does not review runs.
- No reviewer identity or comment is stored. Keep that in your own audit trail.
