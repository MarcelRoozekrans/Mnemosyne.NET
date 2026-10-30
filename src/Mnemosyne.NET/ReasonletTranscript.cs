using System.Buffers;
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
/// A <b>call</b> is a <see cref="FunctionCallContent"/> in a <see cref="ChatRole.Assistant"/> message of the run. It counts
/// when a later message of the run holds a <see cref="FunctionResultContent"/> with the same call id whose
/// <see cref="FunctionResultContent.Exception"/> is null; when several results share a call id, the first one decides.
/// Calls that failed or never got a result are left out, so a run that retried a failed call has the same shape as a
/// clean one.
/// </para>
/// <para>
/// Each counted call is a step keyed by its tool name and the sorted set of its argument names. Argument values, call
/// ids, message ids and timestamps are never read. Steps from one assistant message are sorted by tool name, then by
/// argument names, because the order a model lists parallel calls in carries no meaning. Consecutive equal steps then
/// collapse into one, so paging through results three times is one step.
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
    internal static List<ChatMessage> Materialize(IEnumerable<ChatMessage> messages)
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
    internal static int QuestionIndex(IReadOnlyList<ChatMessage> messages)
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
    internal static Result<ReasonletGraph, TranscriptSkipReason> BuildGraph(IReadOnlyList<ChatMessage> messages, int question)
    {
        var succeeded = new Dictionary<string, bool>(StringComparer.Ordinal);
        for (var i = question + 1; i < messages.Count; i++)
        {
            foreach (var result in messages[i].Contents.OfType<FunctionResultContent>())
            {
                succeeded.TryAdd(result.CallId, result.Exception is null);
            }
        }

        var steps = new List<Step>();
        for (var i = question + 1; i < messages.Count; i++)
        {
            if (messages[i].Role != ChatRole.Assistant)
            {
                continue;
            }

            var batch = new List<Step>();
            foreach (var call in messages[i].Contents.OfType<FunctionCallContent>())
            {
                if (succeeded.TryGetValue(call.CallId, out var ok) && ok)
                {
                    batch.Add(Step.Of(call));
                }
            }

            batch.Sort();
            foreach (var step in batch)
            {
                if (steps.Count == 0 || steps[^1].CompareTo(step) != 0)
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

    private static string NodeId(int index) => $"step-{index + 1}";

    /// <summary>Writes the argument names as a JSON array without reflection, so it stays trim and AOT safe.</summary>
    private static JsonElement ArgumentsElement(string[] names)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var name in names)
            {
                writer.WriteStringValue(name);
            }

            writer.WriteEndArray();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>One step: a tool and its sorted argument names, ordered by tool, then by names, a shorter prefix first.</summary>
    private sealed record Step(string Tool, string[] Arguments) : IComparable<Step>
    {
        public static Step Of(FunctionCallContent call)
        {
            var names = call.Arguments is { } args ? args.Keys.ToArray() : [];
            Array.Sort(names, StringComparer.Ordinal);
            return new Step(call.Name, names);
        }

        public int CompareTo(Step? other)
        {
            if (other is null)
            {
                return 1;
            }

            var byTool = string.CompareOrdinal(Tool, other.Tool);
            if (byTool != 0)
            {
                return byTool;
            }

            var shared = Math.Min(Arguments.Length, other.Arguments.Length);
            for (var i = 0; i < shared; i++)
            {
                var byName = string.CompareOrdinal(Arguments[i], other.Arguments[i]);
                if (byName != 0)
                {
                    return byName;
                }
            }

            return Arguments.Length.CompareTo(other.Arguments.Length);
        }
    }
}
