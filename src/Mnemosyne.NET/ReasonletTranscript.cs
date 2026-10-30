using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.NET;

/// <summary>Turns a chat transcript's tool calls into a Reasonlet: the tool shape of the last question's run.</summary>
/// <remarks>
/// <para>
/// The <b>question</b> is the last <see cref="ChatRole.User"/> message whose text is not blank. The <b>run</b> is every
/// message after it; earlier turns are context and contribute nothing.
/// </para>
/// <para>
/// A <b>call</b> is a <see cref="FunctionCallContent"/> in a <see cref="ChatRole.Assistant"/> message of the run. Results
/// pair with calls by position: a <see cref="FunctionResultContent"/> decides the earliest undecided call before it with
/// the same call id, so a provider that reuses ids across rounds still pairs correctly, and a result with no such call,
/// or a second result for a decided call, is ignored. A call counts when its result's
/// <see cref="FunctionResultContent.Exception"/> is null. Calls that failed or never got a result are left out.
/// </para>
/// <para>
/// Each counted call is a step keyed by its tool name and the sorted set of its argument names. Argument values, call
/// ids, message ids and timestamps are never read. A counted call that retries an earlier failed one, with the same tool
/// and argument names, takes the failed call's place, so a run that retried a failed call has the same shape as a clean
/// one, also when the failed call was one of several made together. Steps from one assistant message are sorted by tool
/// name, then by argument names, because the order a model lists parallel calls in carries no meaning. Consecutive equal
/// steps then collapse into one, so paging through results three times is one step.
/// </para>
/// <para>
/// Step <c>n</c> becomes the node <c>step-n</c> of type <c>tool</c>, with the tool name and a <c>mnemosyne.arguments</c> property
/// holding the argument names as a JSON array, empty when there are none. Edges chain the steps in order. The result is
/// deterministic: two runs with the same shape have byte-identical canonical graphs, so
/// <see cref="IReasonletMemory.RememberAsync"/> records a rerun under a recalled parent as an execution, not a new version.
/// </para>
/// <para>
/// Nothing here validates the graph against <see cref="ReasonletGraphLimits"/>; <see cref="IReasonletMemory.RememberAsync"/>
/// does, and answers <see cref="MnemosyneErrorCode.Validation"/> for a run with too many steps or an overlong tool name.
/// </para>
/// </remarks>
public static class ReasonletTranscript
{
    private const string ArgumentsProperty = "mnemosyne.arguments";

    /// <summary>Builds the tool-shape graph of the last question's run.</summary>
    /// <param name="messages">The transcript, oldest first. No element may be null.</param>
    /// <returns>The graph, or why there is nothing to capture.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> or one of its elements is null.</exception>
    public static Result<ReasonletGraph, TranscriptSkipReason> ToGraph(IEnumerable<ChatMessage> messages)
    {
        var list = Materialize(messages);
        var question = QuestionIndex(list);
        return question < 0 ? TranscriptSkipReason.NoQuestion : BuildGraph(list, question);
    }

    /// <summary>Builds a capture from the last question's run.</summary>
    /// <remarks>
    /// <see cref="ReasonletCapture.Question"/> is the question's text. <see cref="ReasonletCapture.Answer"/> is the text of
    /// the run's last assistant message whose text is not blank, or null when there is none; it reaches the evaluator and is
    /// never stored. The graph is <see cref="ToGraph"/>'s, and every other member comes from <paramref name="details"/>.
    /// The text is copied as it is: nothing here redacts it.
    /// </remarks>
    /// <param name="messages">The transcript, oldest first. No element may be null.</param>
    /// <param name="details">What the caller adds: scope, provenance, outcome and the optional parent, intent and name.</param>
    /// <returns>The capture, or why there is nothing to capture.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/>, one of its elements or <paramref name="details"/> is null.</exception>
    public static Result<ReasonletCapture, TranscriptSkipReason> ToCapture(
        IEnumerable<ChatMessage> messages, TranscriptCaptureDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var list = Materialize(messages);
        var question = QuestionIndex(list);
        if (question < 0)
        {
            return TranscriptSkipReason.NoQuestion;
        }

        var graph = BuildGraph(list, question);
        if (graph.IsFailure)
        {
            return graph.Error;
        }

        return new ReasonletCapture
        {
            Scope = details.Scope,
            Question = list[question].Text,
            Graph = graph.Value,
            Outcome = details.Outcome,
            Provenance = details.Provenance,
            Intent = details.Intent,
            Name = details.Name,
            Answer = Answer(list, question),
            ParentId = details.ParentId,
        };
    }

    /// <summary>Returns the text of the run's last assistant message with non-blank text, or null.</summary>
    private static string? Answer(IReadOnlyList<ChatMessage> messages, int question)
    {
        for (var i = messages.Count - 1; i > question; i--)
        {
            if (messages[i].Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(messages[i].Text))
            {
                return messages[i].Text;
            }
        }

        return null;
    }

    /// <summary>Materializes the transcript once, rejecting null elements.</summary>
    private static List<ChatMessage> Materialize(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var list = new List<ChatMessage>(messages);
        foreach (var message in list)
        {
            ArgumentNullException.ThrowIfNull(message, nameof(messages));
        }

        return list;
    }

    /// <summary>Returns the index of the last user message with non-blank text, or -1 when there is none.</summary>
    private static int QuestionIndex(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.User && !string.IsNullOrWhiteSpace(messages[i].Text))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Builds the graph from the messages after <paramref name="question"/>.</summary>
    private static Result<ReasonletGraph, TranscriptSkipReason> BuildGraph(IReadOnlyList<ChatMessage> messages, int question)
    {
        var steps = new List<Step>();
        foreach (var batch in CountedSteps(messages, question))
        {
            batch.Sort(Step.Compare);
            foreach (var step in batch)
            {
                if (steps.Count == 0 || Step.Compare(steps[^1], step) != 0)
                {
                    steps.Add(step);
                }
            }
        }

        if (steps.Count == 0)
        {
            return TranscriptSkipReason.NoToolCalls;
        }

        var nodes = new ReasonletNode[steps.Count];
        var edges = new ReasonletEdge[steps.Count - 1];
        for (var i = 0; i < steps.Count; i++)
        {
            nodes[i] = new ReasonletNode
            {
                Id = NodeId(i),
                Type = "tool",
                Tool = steps[i].Tool,
                Properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    [ArgumentsProperty] = ArgumentsElement(steps[i].Arguments),
                },
            };
            if (i > 0)
            {
                edges[i - 1] = new ReasonletEdge { From = NodeId(i - 1), To = NodeId(i) };
            }
        }

        return new ReasonletGraph { Nodes = nodes, Edges = edges, SchemaVersion = ReasonletGraph.CurrentSchemaVersion };
    }

    /// <summary>
    /// Pairs the run's calls with their results by position and returns the counted steps of each assistant message that
    /// made calls, in message order and not yet sorted. A counted retry of an earlier failed call is placed in that call's
    /// message instead of its own.
    /// </summary>
    private static List<List<Step>> CountedSteps(IReadOnlyList<ChatMessage> messages, int question)
    {
        var batches = new List<List<Step>>();
        var pending = new Dictionary<string, Queue<(int Batch, Step Step)>>(StringComparer.Ordinal);
        var failed = new List<(int Batch, Step Step)>();
        for (var i = question + 1; i < messages.Count; i++)
        {
            var batch = -1;
            foreach (var content in messages[i].Contents)
            {
                if (content is FunctionCallContent call && messages[i].Role == ChatRole.Assistant)
                {
                    if (batch < 0)
                    {
                        batch = batches.Count;
                        batches.Add([]);
                    }

                    if (!pending.TryGetValue(call.CallId, out var queue))
                    {
                        queue = new Queue<(int, Step)>();
                        pending.Add(call.CallId, queue);
                    }

                    queue.Enqueue((batch, Step.Of(call)));
                }
                else if (content is FunctionResultContent result
                    && pending.TryGetValue(result.CallId, out var calls)
                    && calls.TryDequeue(out var decided))
                {
                    if (result.Exception is not null)
                    {
                        failed.Add(decided);
                    }
                    else
                    {
                        batches[RetrySlot(failed, decided)].Add(decided.Step);
                    }
                }
            }
        }

        return batches;
    }

    /// <summary>
    /// Returns the message a counted call belongs to: the earliest message of a failed call it retries, or its own. Every
    /// failed call with the same key up to its own message counts as retried by it and is removed, so a later call of
    /// the same tool is not mistaken for another retry.
    /// </summary>
    private static int RetrySlot(List<(int Batch, Step Step)> failed, (int Batch, Step Step) counted)
    {
        var slot = counted.Batch;
        for (var j = failed.Count - 1; j >= 0; j--)
        {
            if (failed[j].Batch <= counted.Batch && Step.Compare(failed[j].Step, counted.Step) == 0)
            {
                slot = Math.Min(slot, failed[j].Batch);
                failed.RemoveAt(j);
            }
        }

        return slot;
    }

    private static string NodeId(int index) => $"step-{index + 1}";

    /// <summary>
    /// Writes the argument names as a JSON array and parses it, without reflection, so it stays trim and AOT safe. Quotes,
    /// backslashes, control characters and every surrogate are written as <c>\uXXXX</c> escapes, so a name keeps its exact
    /// UTF-16, and a lone surrogate reaches graph validation instead of being replaced. The canonical form unescapes them,
    /// so the escapes never reach the hash.
    /// </summary>
    private static JsonElement ArgumentsElement(string[] names)
    {
        var json = new StringBuilder("[");
        for (var i = 0; i < names.Length; i++)
        {
            json.Append(i == 0 ? "\"" : ",\"");
            foreach (var c in names[i])
            {
                if (c < 0x20 || c == '"' || c == '\\' || char.IsSurrogate(c))
                {
                    json.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                }
                else
                {
                    json.Append(c);
                }
            }

            json.Append('"');
        }

        using var document = JsonDocument.Parse(json.Append(']').ToString());
        return document.RootElement.Clone();
    }

    /// <summary>One step: a tool and its sorted argument names.</summary>
    private readonly struct Step(string tool, string[] arguments)
    {
        public string Tool { get; } = tool;

        public string[] Arguments { get; } = arguments;

        public static Step Of(FunctionCallContent call)
        {
            var names = call.Arguments is { } args ? args.Keys.ToArray() : [];
            Array.Sort(names, StringComparer.Ordinal);
            return new Step(call.Name, names);
        }

        /// <summary>Orders by tool, then by argument names compared one by one, a shorter prefix first. All ordinal.</summary>
        public static int Compare(Step x, Step y)
        {
            var byTool = string.CompareOrdinal(x.Tool, y.Tool);
            if (byTool != 0)
            {
                return byTool;
            }

            var shared = Math.Min(x.Arguments.Length, y.Arguments.Length);
            for (var i = 0; i < shared; i++)
            {
                var byName = string.CompareOrdinal(x.Arguments[i], y.Arguments[i]);
                if (byName != 0)
                {
                    return byName;
                }
            }

            return x.Arguments.Length.CompareTo(y.Arguments.Length);
        }
    }
}
