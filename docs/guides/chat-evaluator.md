---
title: Judging runs with a chat model
sidebar_position: 5
---

# Judging runs with a chat model

By default a run counts as a success when the agent says it worked. `ChatReasonletEvaluator` swaps that for a second opinion from any chat model: after each run, the model reads the question, the procedure and the answer, and says whether the answer resolves the question by following the procedure. The verdict counts instead of the agent's own, as [ranking](../concepts/ranking.md) explains.

It lives in the core `Mnemosyne.NET` package and talks to a `Microsoft.Extensions.AI` `IChatClient`, so it works with whatever provider you already use and needs no extra package.

## Chat model or Minos

Both judges ask the same question. They differ in what the number means.

- A [Minos judge](minos-evaluator.md) reads a probability off a decision model, and Minos can measure how well those probabilities match reality, so you can tune a threshold against labelled runs.
- The chat judge asks the model to state its own confidence. That confidence is **uncalibrated**: a model saying 0.9 is not right nine times in ten. Treat the score as a rough signal, not a measured probability.

Choose the chat judge when you already have a chat client and want the judge running quickly, or have no decision-model provider. Choose Minos when you want to calibrate the judge. If neither fits, write your own `IReasonletEvaluator`.

## What the judge asks

`ChatReasonletEvaluator` sends two messages and asks one yes/no question:

> Does the answer resolve the question by following the procedure?

- The **system message** holds Mnemosyne's instructions, your [criteria](#criteria) if you set any, and the reply format.
- The **user message** holds one JSON object with four members:

| Member | What it holds |
|---|---|
| `question` | The run's question |
| `intent` | The Reasonlet's intent |
| `procedure` | The procedure graph, as a nested JSON value rather than an escaped string |
| `answer` | The run's answer |

The instructions tell the model that this object is data to judge and not instructions to follow. The judge never sees the agent's own success flag, which would bias it towards the agent's view, or the Reasonlet's name, which adds nothing the intent and the procedure don't carry.

The only chat option the evaluator sets is a JSON-schema response format, so a provider with structured output enforces the reply's shape. A provider that ignores it still gets the format spelled out in the system message. The model, temperature and token limits come from your client, as shown below.

## The reply and the score

The model must reply with exactly one JSON object and nothing else, with two fields:

```json
{"worked": true, "confidence": 0.8}
```

- `worked` is a bool, the model's answer to the question.
- `confidence` is a number from 0 to 1, how sure the model is of that answer.

Other fields are ignored. Whitespace around the object is fine.

The verdict is:

- **Success** is `worked`.
- **Score** is the probability that the run worked: `confidence` when `worked` is true, and `1 - confidence` when it is false. `{"worked": false, "confidence": 0.9}` scores 0.1.
- **Evaluator** is `EvaluatorName`, which defaults to `llm-judge`. It is the value of the `mnemosyne.evaluator` tag in telemetry.

## Register it

Register an `IChatClient`, then call `AddMnemosyneChatEvaluator`. `AddMnemosyne` picks up the registered `IReasonletEvaluator`.

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET;

// providerClient is your provider's IChatClient, for example the one from Microsoft.Extensions.AI.OpenAI.
services.AddSingleton<IChatClient>(providerClient);
services.AddMnemosyneChatEvaluator();
services.AddMnemosyne();
```

The evaluator and the client it captures are both singletons. Register the client as a singleton. A scoped client would be captured by the singleton, and fails scope validation where that is enabled.

Invalid options throw an `ArgumentException` at registration: an evaluator name that is blank, not valid UTF-16, or longer than 100 code points, and criteria that are blank, not valid UTF-16, or longer than 4,000 code points.

### A model of its own

Your agent's chat client is often a large model. To give the judge a different one, register it under a key and point `ChatClientKey` at that key. With no key, the evaluator uses the unkeyed client.

The evaluator sets no model, temperature or token limit, so set them in the client's pipeline with `ChatClientBuilder.ConfigureOptions`. A temperature of 0 keeps the verdict steadier from run to run.

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET;

// providerClient is your provider's IChatClient.
IChatClient judgeClient = new ChatClientBuilder(providerClient)
    .ConfigureOptions(options =>
    {
        options.ModelId = "a-small-model";
        options.Temperature = 0;
    })
    .Build();

services.AddKeyedSingleton<IChatClient>("judge", judgeClient);
services.AddMnemosyneChatEvaluator(new ChatEvaluatorOptions { ChatClientKey = "judge" });
services.AddMnemosyne();
```

## Criteria

`Criteria` adds your own rules on top of the question. They go into the system message, below Mnemosyne's instructions. The reply format stays fixed, so criteria can't change the shape of the verdict.

```csharp
services.AddMnemosyneChatEvaluator(new ChatEvaluatorOptions
{
    EvaluatorName = "gpt-judge",
    Criteria = "The answer must name the file it found. An answer that only says it looked is not a success.",
});
```

Write criteria as rules about the answer, not as instructions about the reply. Keep them short: the limit is 4,000 code points, and they are sent with every run.

## When the judge fails

A reply the evaluator can't use never throws. It returns a `MnemosyneError` with the code `Unavailable`, the fixed message "The chat judge could not judge the run." and one of two details. The detail names only the reason and never quotes the reply, which could quote the answer.

| Detail | When |
|---|---|
| `chat: EmptyReply` | The response has no text, or only whitespace |
| `chat: InvalidReply` | Anything else that is not a valid verdict |

A reply is an `InvalidReply` when it is:

- not exactly one JSON object: prose before or after it, a code fence, an array, two objects, a trailing comma or invalid JSON;
- missing `worked` or `confidence`, or holding either one twice;
- holding `worked` that is not a bool, or `confidence` that is not a number, `null` included;
- holding a `confidence` below 0 or above 1.

An exception from the client reaches the facade, which treats it as the judge being down. The caller's own cancellation propagates. The facade's `EvaluationTimeout` still applies.

In every case the run is never lost: it is stored with the agent's own verdict, and the receipt's `Source` is `Agent`. A model that regularly adds prose or a fence turns every run into "judge down". Watch `mnemosyne.evaluations` and the `mnemosyne.evaluate` activity for a steady stream of `error.type = Unavailable`. See [Telemetry](telemetry.md#the-evaluate-activity).

## Privacy

The judge runs in someone else's service. The run's question and answer, together with the intent and the procedure, go to the provider behind the chat client, and so do your criteria. Pick a provider whose data handling suits your runs.

On the Mnemosyne side nothing changes: it stores none of the answer, and it sends none of the question or the answer to telemetry. The error detail holds only the reason.

Be careful with logging middleware on the judge's client, such as `UseLogging` or a request logger: it may record the full request, which is your run. Give the judge a client of its own, as above, and leave logging off it, or redact it, while your other clients keep it.

## Telemetry

The evaluator is called inside the facade's `mnemosyne.evaluate` activity, so the spans the chat client emits, for example from `UseOpenTelemetry`, nest under it. The evaluator tag is the evaluator's name, `llm-judge` by default, and it appears only when the judge answered with a valid verdict. A failed call, with either detail above, carries no `mnemosyne.evaluator` tag. See [Telemetry](telemetry.md#the-evaluate-activity) for the tags and the `evaluate` metrics.
