---
title: Judging runs with Minos
sidebar_position: 4
---

# Judging runs with Minos

By default a run counts as a success when the agent says it worked. `Mnemosyne.NET.Minos` swaps that for a second opinion: after each run, a [Minos.NET](https://github.com/MarcelRoozekrans/Minos.NET) decision model reads the question, the procedure and the answer, and says whether the answer resolves the question by following the procedure. The verdict counts instead of the agent's own, as [ranking](../concepts/ranking.md) explains.

If you have no decision-model provider, or want a different judge, you can write your own `IReasonletEvaluator`.

## What the judge asks

`MinosReasonletEvaluator` asks one yes/no question of one Minos Noul:

> Does the answer resolve the question by following the procedure?

A [Noul](https://marcelroozekrans.github.io/Minos.NET/question-types) answers with a probability, so no free-text reply has to be parsed.

- **Score** is the Noul's probability.
- **Success** is `Probability >= SuccessThreshold`. The threshold defaults to 0.5, the cut-off of Minos's own `Noul.Value`.
- **Evaluator** is `EvaluatorName`, which defaults to `minos`. It is the value of the `mnemosyne.evaluator` tag in telemetry.

The judge reads one JSON object with four members:

| Member | What it holds |
|---|---|
| `question` | The run's question |
| `intent` | The Reasonlet's intent |
| `procedure` | The procedure graph, as a nested JSON value rather than an escaped string |
| `answer` | The run's answer |

It never sees the agent's own success flag, which would bias it towards the agent's view, or the Reasonlet's name, which adds nothing the intent and the procedure don't carry.

## Install

```bash
dotnet add package Mnemosyne.NET.Minos
dotnet add package Minos.NET.DependencyInjection
dotnet add package Minos.NET.SystemOne   # or Minos.NET.OpenAI
```

`Mnemosyne.NET.Minos` depends on the provider-neutral `Minos.NET` core only. You choose the decision-model provider:

- `Minos.NET.SystemOne` talks to TypeSafe, to OpenRouter, or to a server you run.
- `Minos.NET.OpenAI` talks to OpenAI's Decisions API.

[Providers](https://marcelroozekrans.github.io/Minos.NET/providers) in the Minos docs compares them.

## Register it

Register an `IDecisionClient` with Minos.NET.DependencyInjection, then call `AddMnemosyneMinosEvaluator`. `AddSystemOneDecisionClient` comes from Minos.NET.SystemOne, which brings in Minos.NET.DependencyInjection; the OpenAI package has `AddOpenAIDecisionClient`. `AddMnemosyne` picks up the registered `IReasonletEvaluator`.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Minos.SystemOne;
using Mnemosyne.NET;
using Mnemosyne.NET.Minos;

// The key comes from TYPESAFE_API_KEY. Use AddSystemOneDecisionClient(options => ...) to set it in code.
services.AddSystemOneDecisionClient();
services.AddMnemosyneMinosEvaluator(new MinosEvaluatorOptions { SuccessThreshold = 0.6 });
services.AddMnemosyne();
```

The evaluator and the client it captures are both singletons. Register the client as a singleton, which Minos's own registration does. A scoped client fails scope validation when the evaluator is resolved.

Invalid options throw an `ArgumentException` at registration: a threshold outside [0, 1], or an evaluator name that is blank or longer than 100 code points.

### A client of its own

To give the judge its own client, with its own key, model or retry settings, register it under a name and point `DecisionClientKey` at that name. Minos's named registrations use the name as the service key.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Minos.SystemOne;
using Mnemosyne.NET;
using Mnemosyne.NET.Minos;

// configuration is the host's IConfiguration.
services.AddSystemOneDecisionClient("judge", options => options.ApiKey = configuration["TypeSafe:ApiKey"]);
services.AddMnemosyneMinosEvaluator(new MinosEvaluatorOptions { DecisionClientKey = "judge" });
services.AddMnemosyne();
```

With no key, the evaluator uses the unkeyed client. [Dependency injection](https://marcelroozekrans.github.io/Minos.NET/dependency-injection) in the Minos docs lists every overload.

## Tuning the threshold

0.5 is a starting point, not a measurement. How far a model's probabilities can be trusted depends on the model and on your procedures, so measure it: label some runs by hand and let Minos report how its judgments compare.

- [Labelled datasets](https://marcelroozekrans.github.io/Minos.NET/datasets) describes the dataset format and `minos calibrate`, which runs a dataset through a provider and writes a calibration report.
- [Choosing a provider and a threshold](https://marcelroozekrans.github.io/Minos.NET/choosing-a-provider) shows how to read that report and pick the threshold your code acts on.

Set the result as `SuccessThreshold`. A higher threshold keeps doubtful runs from raising a procedure's rank, and a lower one is more forgiving.

## When the judge fails

A failed Minos call never throws. The evaluator returns a `MnemosyneError` with the fixed message "The Minos judge could not judge the run." and a detail that is only `minos: <Kind>`, such as `minos: RateLimited`. Minos's own message text is never kept, because it could quote the request.

Exceptions and cancellation propagate as for any evaluator, and the facade's `EvaluationTimeout` still applies.

All 13 `DecisionErrorKind` values map as follows:

| Kind | Meaning | Mnemosyne error | The facade |
|---|---|---|---|
| `Unauthorized` | The API key is missing, invalid or lacks access | `Unavailable` | Stores the run with the agent's verdict |
| `Validation` | The provider rejected the request as invalid | `Unavailable` | Stores the run with the agent's verdict |
| `RateLimited` | Too many requests | `Unavailable` | Stores the run with the agent's verdict |
| `Overloaded` | The service is overloaded | `Unavailable` | Stores the run with the agent's verdict |
| `Server` | The service failed with another 5xx status | `Unavailable` | Stores the run with the agent's verdict |
| `Http` | Any other unsuccessful HTTP status | `Unavailable` | Stores the run with the agent's verdict |
| `Network` | No response arrived | `Unavailable` | Stores the run with the agent's verdict |
| `Timeout` | The request exceeded the client time-out | `Unavailable` | Stores the run with the agent's verdict |
| `InvalidResponse` | A response could not be read as the expected JSON | `Unavailable` | Stores the run with the agent's verdict |
| `Unsupported` | The configured provider can't do this | `Unavailable` | Stores the run with the agent's verdict |
| `Refused` | The model refused to answer | `Unavailable` | Stores the run with the agent's verdict |
| `InvalidQuestions` | The question set breaks Minos's rules | `Validation` | Throws `InvalidOperationException` |
| `Disposed` | The client was disposed during the call | `Validation` | Throws `InvalidOperationException` |

Eleven kinds mean the judge could not judge, so a run is never lost: it is stored with the agent's own verdict, and the receipt's `Source` is `Agent`. `InvalidQuestions` and `Disposed` are bugs in how the host wired things, so they surface as an exception rather than hiding behind a fallback.

For the transient kinds, a client with the standard pipeline has already retried before one reaches Mnemosyne.

## Privacy

The judge runs in someone else's service. The run's question and answer, together with the intent and the procedure, go to the decision-model provider you chose. Pick one whose data handling suits your runs, and see Minos's [providers](https://marcelroozekrans.github.io/Minos.NET/providers) page for zero data retention and for running a server yourself.

On the Mnemosyne side nothing changes: it stores none of the answer, and it sends none of the question or the answer to telemetry. The error detail holds only the kind.

Don't put Minos.NET.Recording on the judge's client unless its state redaction is configured. Recording keeps the state of each decision, and here the state is your run. [Recording decisions](https://marcelroozekrans.github.io/Minos.NET/recording#redaction) explains the redactor. Giving the judge a client of its own, as above, lets you leave recording off it while your other Minos clients keep it.

## Telemetry

The evaluator is called inside the facade's `mnemosyne.evaluate` activity, so the spans Minos emits for the judge call nest under it. The evaluator tag is the evaluator's name, `minos` by default, and it appears only when the judge answered with a valid verdict. A failed Minos call, which covers all 13 kinds in the table above, carries no `mnemosyne.evaluator` tag. See [Telemetry](telemetry.md#the-evaluate-activity) for the tags and the `evaluate` metrics, and [Observability](https://marcelroozekrans.github.io/Minos.NET/observability) in the Minos docs for Minos's own spans and metrics.
