# Mnemosyne.NET.AgentFramework

Reasonlet memory for [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) agents. The package depends on `Mnemosyne.NET` and `Microsoft.Agents.AI.Abstractions` 1.24.0.

```bash
dotnet add package Mnemosyne.NET.AgentFramework
```

`ReasonletContextProvider` is an Agent Framework `AIContextProvider`. Before each run it recalls the best known procedure for the user's question and adds it to the run's instructions. After a successful run it hands the run to the background capture, which remembers it. It does for an agent what `UseReasonlets` does for an `IChatClient`.

## Setup

Register the memory and the capture queue, then add one provider to the agent's `AIContextProviders`. Build one provider per agent.

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET;
using Mnemosyne.NET.AgentFramework;

services.AddMnemosyne();         // the memory, selector and clock; you register the store and the embedding generator
services.AddReasonletCapture();  // the capture queue and its worker

// provider is the built IServiceProvider and chatClient is the agent's IChatClient.
var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "support-agent",
    AIContextProviders = [new ReasonletContextProvider(provider, new ReasonletAgentOptions())],
});
```

The provider resolves `IReasonletMemory` and `IReasonletCaptureSink` on the first run that needs them, not when it is built. A memory that judges with the agent's own chat client therefore forms no cycle. A resolution that fails is counted, the run goes on, and the next run tries again.

## Options

`ReasonletAgentOptions`:

| Option | Default | What it does |
|---|---|---|
| `ScopeResolver` | `ReasonletAgentOptions.DefaultScope` | Finds the run's scope, and optionally its subject, from the agent and the session |
| `Recall` | `true` | Recalls a procedure and adds it to the run's instructions |
| `Capture` | `true` | Captures the finished run |
| `ExposeFindProcedure` | `false` | Adds the `find_procedure` tool to the run, whether `Recall` is on or off |
| `MinSimilarity` | `null` | The lowest similarity to suggest; null keeps the selector's own cut-off |
| `RecallTimeout` | 5 seconds | How long a recall may take before the run goes on without it |
| `Formatter` | `null` | Writes a match as the suggestion text; null uses the built-in format |
| `Source` | `mnemosyne.agent` | The source recorded on a captured run |

Invalid options throw `ArgumentException` when the provider is built.

## Scope and subject

The default resolver uses the agent's name as the scope, with no subject. An agent without a name has no scope, so memory is skipped for its runs. The skip is counted with the reason `no_scope` only when `Recall` or `Capture` is on.

Renaming an agent starts a new scope. Its procedures stay under the old name. If you rename agents, or want several agents to share procedures, return a stable scope from your own resolver. The resolver also sets the subject, which ends up in the capture's provenance:

```csharp
using Mnemosyne.NET.Abstractions;

var options = new ReasonletAgentOptions
{
    ScopeResolver = (agent, session) => new ReasonletAgentScope(new ReasonletScope("support"), SubjectId: "user-7"),
};

// The options go to the provider's constructor.
var contextProvider = new ReasonletContextProvider(provider, options);
```

A resolver that returns null skips memory for the run. A resolver that throws is counted as `exception` and treated as no scope. The run is never failed.

## What a run does

**Recall.** The question is the text of the last user message among the run's new messages. The top match is rendered and returned as the run's instructions. It is never added as a message, so it reaches every model call of the run and is never saved into the conversation's history. A last user message with no text, such as one that only holds an image or answers an approval request, is not recalled for and is counted with the skip reason `continuation`. No match, an error, a timeout or a formatter that throws leaves the instructions out.

**Capture.** After a successful run the provider hands the run to the capture queue and does not wait for it. The capture takes this run's new request messages as the agent received them, without the injected instructions, plus all response messages. Earlier history is not captured. The recalled procedure is the capture's parent, so a changed shape becomes a new version of it and not a new Reasonlet. A rerun with the same graph is recorded as an execution of the Reasonlet either way. The graph is the run's tool shape, as `ReasonletTranscript.ToCapture` builds it. The provider keeps the run's state, such as the suggested Reasonlet and the lookups, only with `Capture` on and a session. A run without a session is captured without a parent and without lookups.

**find_procedure.** With `ExposeFindProcedure` on, each run gets its own `find_procedure` tool, bound to the run's scope. Its lookups are removed from the capture and can set the parent. The tool is added even when `Recall` is off. The provider does not add the tool when one named `find_procedure` is already present. It sees the agent's tools, the run's tools and the tools added by providers listed before it in `AIContextProviders`, so list a provider that adds tools first.

## What users of a scope see

Everyone who uses a scope can recall what the others captured. A captured Reasonlet is named after its tools, such as `orders_get → payments_refund`, and holds tool names and argument names, never argument values. The suggestion text and the `find_procedure` result show tool shapes and argument names. They never show intent or question text.

## Failures and telemetry

Memory never breaks a run. A recall is awaited, for at most `RecallTimeout`, and then the run goes on without it. A missing scope, a recall that fails or times out, a memory or sink that cannot be resolved, a full queue and a failed capture are counted, and the run goes on. Only the caller's own cancellation propagates. Capture does not slow the run, because the provider hands the run to the queue and does not wait.

The provider uses the same instruments as `UseReasonlets`: `mnemosyne.chat.recall`, `mnemosyne.chat.recalls`, `mnemosyne.capture.queue` and the capture counters. Recall signals carry the tag `mnemosyne.integration` with `agent`, where the chat middleware's carry `chat`. Captures carry the source `mnemosyne.agent`, so the two paths stay apart in the store. No signal carries the scope, the subject, the question, the suggested text, a tool or argument name, or an exception message.

## Limits

- One run is one procedure. A conversation that spans several runs is not followed as one procedure.
- A run that ends on an unanswered tool call or an approval request is not captured. A clarifying question asked in plain text is a normal finished run and is captured as its own procedure. Either way, the run that answers is a separate capture, and the two are never joined.
- A failed run is not captured, because Agent Framework does not call the provider's end hook for it. A streamed run that is not read to the end is not captured either.
- One run at a time per session. A new run on a session replaces whatever an earlier run left behind.
- Do not use `UseReasonlets` on the agent's chat client together with this provider. The middleware would sit inside the agent's tool loop, where each round trip is a separate call. Nothing detects the combination. Use the provider for agents and `UseReasonlets` for plain chat clients.
- A tool named `find_procedure` on the host side is removed from captures, whether it is yours or Mnemosyne's.
- A judge or guard that calls a chat model must use a client without `UseReasonlets`. The provider sits on the agent, not on the chat client, so judge calls never reach it.

## Documentation

- [Agent Framework](https://marcelroozekrans.github.io/Mnemosyne.NET/guides/agent-framework): the setup, scope and subject, what a run does, `find_procedure` and the limits.
- [Capture, redaction and review](https://marcelroozekrans.github.io/Mnemosyne.NET/guides/capture-and-review): what the worker does with a captured run.

See the [repository README](https://github.com/MarcelRoozekrans/Mnemosyne.NET#readme).
