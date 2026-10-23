# Mnemosyne.NET.Minos

A Reasonlet evaluator that judges each run with a [Minos.NET](https://github.com/MarcelRoozekrans/Minos.NET) decision model. `MinosReasonletEvaluator` asks one yes/no question, "Does the answer resolve the question by following the procedure?", and gets a calibrated probability back, so no free-text reply needs parsing.

```bash
dotnet add package Mnemosyne.NET.Minos
```

The package depends on `Mnemosyne.NET.Abstractions` and the provider-neutral `Minos.NET` core only. The host picks the decision-model provider with Minos.NET's own packages, such as `Minos.NET.SystemOne` or `Minos.NET.OpenAI`, and supplies the `IDecisionClient`.

## The verdict

- **Score** is the probability of yes.
- **Success** is `Probability >= SuccessThreshold`. The default threshold is 0.5, the cut-off of Minos's own `Noul.Value`.
- **Evaluator** is `EvaluatorName`, by default `minos`.

```csharp
using Minos;
using Mnemosyne.NET.Minos;

// client is the host's IDecisionClient.
var evaluator = new MinosReasonletEvaluator(client, new MinosEvaluatorOptions { SuccessThreshold = 0.6 });
```

## What the judge sees

The judge reads a JSON object with the run's `question`, the Reasonlet's `intent`, the `procedure` graph as its canonical JSON, nested as an object, and the run's `answer`. It never sees the agent's own success flag or the Reasonlet's name.

The question and the answer leave the process: they go to the decision-model provider the host chose. Mnemosyne itself still stores, logs and sends none of them to telemetry. Don't add Minos's recording stage to the judge's client unless its state redaction is configured.

## Failures

A failed Minos call never throws. It returns a `MnemosyneError` with the fixed message "The Minos judge could not judge the run." and a detail that names only the kind, such as `minos: RateLimited`, never Minos's message text.

- `InvalidQuestions` and `Disposed` are bugs, and map to `Validation`.
- Every other kind means the judge could not judge, and maps to `Unavailable`. The facade then keeps the agent's own verdict.

Exceptions and cancellation propagate.
