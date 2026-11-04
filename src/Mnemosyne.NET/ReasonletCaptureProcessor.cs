using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET;

/// <summary>Turns one finished chat call into a <see cref="IReasonletMemory.RememberAsync"/> capture.</summary>
/// <remarks>
/// <para>
/// It removes the <c>find_procedure</c> calls and their results, builds the capture with
/// <see cref="ReasonletTranscript.ToCapture"/>, picks the parent, names it after its tool shape, sets the outcome and
/// remembers it. The parent is the first
/// that applies: the injected one; the first lookup match, in lookup order, whose graph has the run graph's content hash;
/// the top match of the first lookup that returned any; none.
/// </para>
/// <para>
/// <see cref="ProcessAsync"/> never throws, except <see cref="OperationCanceledException"/> when the caller's token is
/// cancelled: an error result or any other exception is counted by <see cref="CaptureTelemetry"/> and the item ends.
/// </para>
/// </remarks>
/// <param name="memory">The memory to remember into.</param>
/// <param name="options">The capture options, for the provenance source.</param>
internal sealed class ReasonletCaptureProcessor(IReasonletMemory memory, ReasonletCaptureOptions options)
{
    /// <summary>Remembers one item, and counts, times and traces it.</summary>
    /// <param name="item">The finished call.</param>
    /// <param name="ct">Cancels the work.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> is cancelled.</exception>
    internal async ValueTask ProcessAsync(ReasonletCaptureItem item, CancellationToken ct)
    {
        using var telemetry = CaptureTelemetry.Start();
        try
        {
            var messages = Strip(item.Messages);
            if (EndsWithPendingCall(messages))
            {
                telemetry.SkippedPendingToolCalls();
                return;
            }

            var details = new TranscriptCaptureDetails
            {
                Scope = item.Scope,
                Provenance = new ReasonletProvenance { Source = options.Source, RunId = item.ResponseId, SubjectId = item.SubjectId },
                Outcome = new ReasonletOutcome(Succeeded(messages, item.FinishReason), item.Duration, null),
            };
            var built = ReasonletTranscript.ToCapture(messages, details);
            if (built.IsFailure)
            {
                telemetry.Skipped(built.Error);
                return;
            }

            var capture = built.Value with { ParentId = Parent(item, built.Value.Graph), Name = ShapeName(built.Value.Graph) };
            var receipt = await memory.RememberAsync(capture, ct).ConfigureAwait(false);
            if (receipt.IsFailure)
            {
                telemetry.Failed(receipt.Error.Code);
            }
            else
            {
                telemetry.Remembered(receipt.Value.Kind);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            telemetry.Cancelled();
            throw;
        }
        catch (Exception ex)
        {
            telemetry.Threw(ex);
        }
    }

    /// <summary>
    /// Names the capture after its tool shape, the step labels in order joined by an arrow, cut to the name limit. The name
    /// is offered to other users of the scope, so it is built from the graph alone and never from the user's question.
    /// </summary>
    private static string ShapeName(ReasonletGraph graph) =>
        ReasonletMemory.CutName(string.Join(" → ", ReasonletPromptFormatter.Steps(graph).Select(step => step.Label)));

    /// <summary>The run succeeded when its last assistant message has text and the model did not stop on a filter or the length.</summary>
    private static bool Succeeded(List<ChatMessage> messages, ChatFinishReason? finish)
    {
        if (finish == ChatFinishReason.ContentFilter || finish == ChatFinishReason.Length)
        {
            return false;
        }

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == ChatRole.Assistant)
            {
                return !string.IsNullOrWhiteSpace(messages[i].Text);
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the run stops on a call nobody answered: the last message holds a function call that is not informational
    /// only, or an input request such as a tool approval request, and any answer to it would have to come after it. Such a
    /// response is one trip of a tool loop that the caller drives, not a finished run.
    /// </summary>
    private static bool EndsWithPendingCall(List<ChatMessage> messages) =>
        messages.Count > 0
        && messages[^1].Contents.Any(content => content is FunctionCallContent { InformationalOnly: false } or InputRequestContent);

    private static ReasonletId? Parent(ReasonletCaptureItem item, ReasonletGraph graph)
    {
        if (item.InjectedParent is { } injected)
        {
            return injected;
        }

        string? hash = null;
        var hashed = false;
        ReasonletId? top = null;
        foreach (var lookup in item.Lookups)
        {
            foreach (var match in lookup)
            {
                if (!hashed)
                {
                    var own = graph.ComputeContentHash();
                    hash = own.IsSuccess ? own.Value : null;
                    hashed = true;
                }

                // A run graph without a hash equals no match, so only the top matches are left to look at.
                if (hash is null)
                {
                    break;
                }

                var candidate = match.Reasonlet.Graph.ComputeContentHash();
                if (candidate.IsSuccess && string.Equals(hash, candidate.Value, StringComparison.Ordinal))
                {
                    return match.Reasonlet.Id;
                }
            }

            if (top is null && lookup.Count > 0)
            {
                top = lookup[0].Reasonlet.Id;
            }
        }

        return top;
    }

    /// <summary>
    /// Copies the messages without the <c>find_procedure</c> calls and the results that answer them, by call id, and drops
    /// a message that becomes empty. A message that changes is a new one; the others are shared, and none is mutated.
    /// </summary>
    private static List<ChatMessage> Strip(IReadOnlyList<ChatMessage> messages)
    {
        var lookups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent { Name: ReasonletTools.FindProcedureName } call)
                {
                    lookups.Add(call.CallId);
                }
            }
        }

        var kept = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            var contents = message.Contents
                .Where(content => content is not FunctionCallContent { Name: ReasonletTools.FindProcedureName }
                    && !(content is FunctionResultContent result && lookups.Contains(result.CallId)))
                .ToList();
            if (contents.Count == message.Contents.Count)
            {
                kept.Add(message);
            }
            else if (contents.Count > 0)
            {
                var copy = message.Clone();
                copy.Contents = contents;
                kept.Add(copy);
            }
        }

        return kept;
    }
}
