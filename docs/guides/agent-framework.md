---
title: Agent Framework
sidebar_position: 9
---

# Agent Framework

`ReasonletContextProvider` is a context provider for [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) agents. Before a run it recalls the best known procedure for the user's question and adds it to the run's instructions. After a successful run it hands the run to a background worker that remembers it. This guide shows the setup, how the scope and subject are chosen, what a run does, the optional `find_procedure` tool and the limits.

The code below comes from [`samples/Mnemosyne.Samples.AgentFramework`](https://github.com/MarcelRoozekrans/Mnemosyne.NET/tree/main/samples/Mnemosyne.Samples.AgentFramework). The sample runs offline, and [Samples](../samples.md#agent-framework) shows its output. The provider does for an agent what `UseReasonlets` does for a chat client, and this page does not repeat the parts they share. The [chat pipeline guide](chat-pipeline.md) covers recall, injection and background capture, and the [capture and review guide](capture-and-review.md) covers what the worker does with a capture.

## Set it up

The package is `Mnemosyne.NET.AgentFramework`. A host registers the store, an embedding generator, the facade and the capture queue, the same as for the middleware:

```csharp
services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString)); // a factory, so the container disposes it
services.AddMnemosynePostgreSql(new MnemosynePostgreSqlOptions { Dimensions = HashingEmbeddingGenerator.Dimensions });
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HashingEmbeddingGenerator());
services.AddMnemosyne();
services.AddReasonletCapture();
```

The `HashingEmbeddingGenerator` is the sample's offline stand-in for a real embedding model. The sample leaves the review gate off, so a captured procedure is recallable at once. See [the review gate](capture-and-review.md#the-review-gate) for the other setting.

Then add one provider to the agent's `AIContextProviders`. The sample's line is:

```csharp
AIContextProviders = [new ReasonletContextProvider(provider, new ReasonletAgentOptions { ScopeResolver = (_, _) => new ReasonletAgentScope(scope) })],
```

Here `provider` is the built `IServiceProvider`. The rest of the agent is a `ChatClientAgent` with a name, a scripted client and two tools. The sample passes a scripted client where an application passes the client of its model provider. Build one provider per agent: one instance serves all sessions of the agent at once.

The constructor takes the service provider and optional `ReasonletAgentOptions`. Invalid options throw `ArgumentException` when the provider is built. The provider resolves `IReasonletMemory` and `IReasonletCaptureSink` on the first run that needs them, not when it is built. A memory that judges with the agent's own chat client therefore forms no cycle, and a missing registration does not break the agent's construction. A resolution that fails is counted, the run goes on, and the next run tries again. The sample does not check any of this.

### Options

`ReasonletAgentOptions` has eight settings. The sample sets only `ScopeResolver`:

| Option | Default | What it does |
|---|---|---|
| `ScopeResolver` | `ReasonletAgentOptions.DefaultScope` | Finds the run's scope, and optionally its subject, from the agent and the session |
| `Recall` | `true` | Recalls a procedure and adds it to the run's instructions |
| `Capture` | `true` | Captures the finished run |
| `ExposeFindProcedure` | `false` | Adds the `find_procedure` tool to the run |
| `MinSimilarity` | `null` | The lowest similarity to suggest. Null keeps the selector's own cut-off |
| `RecallTimeout` | 5 seconds | How long a recall may take before the run goes on without it |
| `Formatter` | `null` | Writes a match as the suggestion text. Null uses the built-in format |
| `Source` | `mnemosyne.agent` | The source recorded on a captured run. It must not be blank |

`MinSimilarity` must be in [0, 1]. `RecallTimeout` must be positive, or `Timeout.InfiniteTimeSpan`.

## Scope and subject

Every run needs a scope. Before each run the provider calls `ScopeResolver` with the agent and the session. The resolver returns a `ReasonletAgentScope`, a record of a `ReasonletScope` and an optional `SubjectId`. [Scopes and subjects](../concepts/scopes-and-subjects.md) explains what each is for.

The default resolver, `ReasonletAgentOptions.DefaultScope`, uses the agent's name as the scope and sets no subject. An agent without a name has no scope. The sample replaces the resolver with one that returns a fixed scope, so that it can erase the scope at the end. It does not run the default, so the default is not sample-checked.

Renaming an agent starts a new scope, and its procedures stay under the old name. If you rename agents, or want several agents to share procedures, return a stable scope from your own resolver. The same resolver can set the subject, which ends up in the capture's provenance. This is a sketch, not code from the sample:

```csharp
var options = new ReasonletAgentOptions
{
    ScopeResolver = (agent, session) => new ReasonletAgentScope(new ReasonletScope("tenant-42"), SubjectId: "user-7"),
};
```

A resolver that returns null skips memory for the run: no recall, no capture, no tool. That counts on `mnemosyne.chat.recalls` as `skipped` with the reason `no_scope`, but only when `Recall` or `Capture` is on. A resolver that throws is counted as `exception` and treated as no scope. The run is not failed. [Telemetry](telemetry.md#chat-pipeline-agent-provider-and-capture) has the instruments.

## What a run does

The sample runs the agent twice, through a scripted client that plays the model. In both runs the model calls `orders_get` and `payments_refund`, then answers.

### At the start of a run

With `Recall` on, the question is the text of the last user message among the run's new messages. A run whose last user message has no text, such as one that holds only an image or answers an approval request, is not recalled for and is counted with the skip reason `continuation`. The sample always asks with text.

For a question the provider asks the memory for the best match, as the middleware does (see [What a call does](chat-pipeline.md#what-a-call-does)). It renders the match and returns it as the run's instructions. It never adds a message, so the text reaches every model call of the run and is never saved into the conversation's history. No match, an error, a timeout or an exception leaves the instructions out. With `Capture` on and a session, the provider also keeps the id of the suggested Reasonlet, the scope and the start time against the session, for the end of the run.

The sample checks this in two runs. Run 1 asks "How do I refund an order?" and nothing is known yet:

```text
Run 1. Ask "How do I refund an order?" and let the capture run in the background.
  Capture outcome: new_reasonlet. Matches for the same question: 1 ("orders_get → payments_refund", 1 run).
```

The sample asserts that the reply is the scripted answer, that the capture outcome is `new_reasonlet`, that recall for the same question returns one match with one run, and that the system text of the first call to the model does not contain the Reasonlet's name.

Run 2 asks "How do I refund an order for a customer?" on a fresh session:

```text
Run 2. Ask "How do I refund an order for a customer?" on a fresh session.
  The first call to the model carried the recalled Reasonlet "orders_get → payments_refund" in its instructions, and no message did.
  Capture outcome: execution_only. Runs of the Reasonlet: 2.
```

The sample asserts that the system text of the first call contains the name and that no user or assistant message of the run does. That is the point of returning instructions and not a message. It also asserts that the capture outcome is `execution_only`, that recall for the second question returns the same single Reasonlet and that it now has 2 runs.

### At the end of a run

With `Capture` on, the provider hands the finished run to the registered `IReasonletCaptureSink` and does not wait for the worker. The sample waits for each capture with a listener on the `mnemosyne.capture.runs` counter. The default sink is the capture queue that `AddReasonletCapture` registers.

The run is a `ReasonletCaptureRun`, the record a sink takes. The provider fills it from what Agent Framework gives the end hook:

- **Messages.** The run's request messages as the agent received them, followed by all the response messages. The injected instructions are not among them, because they were never messages. The provider passes on what Agent Framework calls the run's request messages. If that holds the whole history and not only the new messages, so does the capture. The sample does not check that, and a question is picked as in [From transcript to graph](capture-and-review.md#from-transcript-to-graph), from the last user message with text.
- **Parent.** The Reasonlet that was suggested to the run, if any. A rerun is then recorded as an execution of it and not as a new Reasonlet. The sample's Run 2 shows this: its capture is `execution_only`, which means the worker saw the Reasonlet as the parent. In the code that is the injected parent, since the run made no lookups.
- **Lookups.** The matches of the `find_procedure` lookups the model made, if the tool was on.
- **Duration, source and run id.** The run's duration, `ReasonletAgentOptions.Source`, and the id of the last response message.
- **Scope and subject.** What the resolver returned at the start.

The worker then turns the transcript into a graph and remembers it. The graph, the guard and the review gate are in [Capture, redaction and review](capture-and-review.md). A run is not captured when the response ends on an unanswered tool call or an approval request, because that is one trip of a tool loop the caller drives and not a finished run.

A run without a session has no stored state at its end, so the provider asks the resolver again and captures the run with no parent and no lookups. `ChatClientAgent` always passes a session. The sample does not check this case.

Memory never breaks a run. A missing scope, a recall that fails or times out, a memory or sink that cannot be resolved, a full queue and a failed capture are counted and the run goes on. Only the caller's own cancellation propagates. Recall is awaited before the model is called, so a slow memory delays the run by up to `RecallTimeout`. The capture is the part that does not wait.

## find_procedure on an agent

Set `ExposeFindProcedure` and each run gets its own `find_procedure` tool, bound to the run's scope. The tool is the same one the middleware offers, so [find_procedure](chat-pipeline.md#find_procedure) owns what it takes, what it returns, what it never shows and how it fails. The sample does not use it, so the sample checks nothing here.

What is specific to the provider:

- The provider adds no tool when one named `find_procedure` is already there. It sees the agent's tools, the run's tools and the tools added by providers listed before it in `AIContextProviders`. A tool added by a provider listed after it is not seen, so list such a provider first.
- The lookups are removed from the capture, and they can set the capture's parent when no recalled Reasonlet was injected. The worker picks the parent as [Background capture](chat-pipeline.md#background-capture) describes.
- The tool is added even when `Recall` is off. With `Capture` off, the provider keeps no state for the run, and the lookups are not used for anything.

## Provider or middleware

Use the provider for agents and `UseReasonlets` for plain chat clients. The provider sits on the agent, so it sees a run as one unit: one question, the whole tool loop and the final answer. The middleware sits on a chat client and sees one call at a time, which is a run only when the client is outside the tool loop. [Order](chat-pipeline.md#order) explains where `UseReasonlets` has to sit for that.

Do not put `UseReasonlets` on the agent's chat client and add this provider as well. A `ChatClientAgent` runs the tool loop around its chat client, which this repository does not control. The middleware would then sit inside the loop, where each round trip is a separate call, as [Order](chat-pipeline.md#order) describes. On the first round trip the request ends in the user's question, so the middleware recalls again and adds its own system message: the suggestion reaches the model twice, once as the provider's instructions and once as that message. It skips the round trips that end in a tool call. The final round trip is a continuation, so the middleware captures it with no injected recall and no parent. That is a second capture of the same run, beside the provider's capture of the whole run. Nothing in the provider detects the combination. The sample does not build it, so this advice comes from the code and is not checked by the sample.

## Limits

- One run is one procedure. A conversation that spans several runs is not followed as one procedure.
- A run that ends on an unanswered tool call or an approval request is not captured. A clarifying question asked in plain text is a finished run and is captured as its own procedure. The run that answers it is a separate capture, and the two are never joined.
- A failed run is not captured. The provider's source says Agent Framework does not call the end hook for a failed run. A streamed run that is not read to the end is expected to go uncaptured for the same reason, since the end hook is not called. Both come from Agent Framework and no code in this repository enforces them. The sample checks neither.
- A capture needs a successful tool call. A run with no tools stores nothing, as in [Limits](chat-pipeline.md#limits) of the chat pipeline guide.
- One run at a time per session. A new run on a session replaces what an earlier run left behind. This also covers a stream that was abandoned.
- A tool named `find_procedure` in a run's messages is removed from captures, whether it is yours or Mnemosyne's.
- A judge or guard that calls a chat model must use a client without `UseReasonlets`. The provider sits on the agent, not on the chat client, so judge calls never reach it. [Judging runs with a chat model](chat-evaluator.md#on-a-client-with-usereasonlets) owns the rule.
- Everyone who uses a scope can recall what the others captured. A captured Reasonlet is named after its tools, such as `orders_get → payments_refund` in the sample, and holds tool names and argument names, never argument values.

The sample checks its own receipts and exits with code 1 if one differs. See [Samples](../samples.md#agent-framework) for the output and what CI does with it.
