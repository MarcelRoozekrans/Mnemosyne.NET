# Mnemosyne.NET.Sentinel

A capture guard that runs [AI.Sentinel](https://github.com/MarcelRoozekrans/AI.Sentinel)'s detectors over a capture before the Reasonlet is stored. `SentinelCaptureGuard` is an `ICaptureGuard`: it blocks the capture when a detector reaches the configured severity, and allows it otherwise.

```bash
dotnet add package Mnemosyne.NET.Sentinel
```

The package depends on `Mnemosyne.NET.Abstractions` and AI.Sentinel 4.x. The host registers AI.Sentinel itself.

## Registration

`AddMnemosyneSentinel` registers the guard as the singleton `ICaptureGuard`, which `AddMnemosyne` then picks up. The order of the two `Add` calls does not matter.

```csharp
using AI.Sentinel;
using Mnemosyne.NET;
using Mnemosyne.NET.Sentinel;

services.AddAISentinel();
services.AddMnemosyneSentinel();
services.AddMnemosyne();
```

The guard resolves AI.Sentinel's `IDetectionPipeline` and `SentinelOptions` when it is first resolved. A host that did not register AI.Sentinel fails then, not at registration. Invalid options throw an `ArgumentException` at registration.

## Options

| Option | Default | Meaning |
|---|---|---|
| `BlockAtSeverity` | `High` | The lowest detector severity that blocks a capture. Must be `Low`, `Medium`, `High` or `Critical`. |
| `PipelineName` | `null` | The AI.Sentinel pipeline whose detectors run. `null` uses the default pipeline. Must not be blank when set. |
| `GuardName` | `ai-sentinel` | The name the guard reports itself under. Must not be blank, must be valid UTF-16 and at most as long as the evaluator name limit. |

```csharp
using AI.Sentinel.Detection;

services.AddMnemosyneSentinel(new SentinelCaptureGuardOptions { BlockAtSeverity = Severity.Medium });
```

## What is scanned

The pipeline sees the capture's question, its intent and its name, as one user message each, in that order. A blank intent or name is left out. It sees nothing else: not the answer, the graph or the provenance tags.

The session is the run id of the capture's provenance, or `mnemosyne-capture` when it has none. The sender and receiver are the defaults of the `SentinelOptions` the host configured.

## The verdict

The guard allows the capture when no detection reaches `BlockAtSeverity`. Otherwise it blocks, and the verdict names the blocking detection with the highest severity. On a tie, the first one wins. The facade then fails the remember with `Blocked`.

The verdict carries the detector id only. The detection's reason is never read and never kept, because it can quote the capture. An id longer than the limit is cut, and an id that is not valid UTF-16 or is blank once cut is replaced by `invalid-detector-id`.

## Failures

The guard never returns an error. An exception from the pipeline reaches the facade, which treats the guard as down: the remember fails closed with `Unavailable`, because a capture that was not inspected is not stored. The caller's own cancellation propagates.

## Slow detectors and `GuardTimeout`

Semantic and LLM-escalating detectors can be slow. The facade stops waiting for the guard after `MnemosyneOptions.GuardTimeout`, 30 seconds by default, cancels the token it passed, and fails the remember with `Unavailable`. A slow guard therefore drops captures, so tune the timeout, or take the slow detectors out of the guard's pipeline as described next.

## A pipeline for the guard

Set `PipelineName` to give the guard its own pipeline, so it can run fewer detectors than the host's chat client does. AI.Sentinel 4.1.1 needs the host to register the default pipeline as well: the shared detector pool resolves the unnamed `SentinelOptions`, and a host that registers only a named pipeline fails at resolution.

```csharp
using AI.Sentinel;
using AI.Sentinel.Detectors.Operational;
using Mnemosyne.NET.Sentinel;

services.AddAISentinel(opts => { /* the host's own settings */ });
services.AddAISentinel("capture", opts =>
{
    opts.Configure<RepetitionLoopDetector>(c => c.Enabled = false);
});
services.AddMnemosyneSentinel(new SentinelCaptureGuardOptions { PipelineName = "capture" });
```

Every pipeline sees the same detector pool. A named pipeline starts from fresh `SentinelOptions` and tunes the pool with `Configure<T>`, where `c.Enabled = false` switches a detector off for that pipeline only. The guard resolves the keyed pipeline and options of that name. Replace `RepetitionLoopDetector` with the slow detectors of your own setup.

## The answer is not scanned

The guard protects what goes into the store. The agent's answer is protected by AI.Sentinel's chat middleware on the host's `IChatClient`, with `UseAISentinel()`. That is the host's wiring, not this package's.
