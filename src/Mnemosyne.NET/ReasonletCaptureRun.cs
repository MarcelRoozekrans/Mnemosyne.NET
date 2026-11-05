using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET;

/// <summary>
/// One finished run of an LLM app to capture, unlike <c>Abstractions.ReasonletRun</c>, which records an execution of an
/// existing Reasonlet. An integration hands it to <see cref="IReasonletCaptureSink"/>: the chat middleware hands one per
/// call, the agent context provider one per agent run.
/// </summary>
public sealed record ReasonletCaptureRun
{
    /// <summary>Creates a run.</summary>
    /// <param name="scope">The scope to capture into.</param>
    /// <param name="messages">The request messages plus the response messages, without any injected suggestion.</param>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> is null.</exception>
    public ReasonletCaptureRun(ReasonletScope scope, IReadOnlyList<ChatMessage> messages)
    {
        Scope = scope;
        Messages = messages;
    }

    /// <summary>The scope to capture into.</summary>
    public ReasonletScope Scope { get; init; }

    /// <summary>The request messages plus the response messages, without any injected suggestion.</summary>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    public IReadOnlyList<ChatMessage> Messages
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <summary>The subject recorded in the provenance, or null.</summary>
    public string? SubjectId { get; init; }

    /// <summary>The Reasonlet the injected suggestion came from, or null when nothing was injected.</summary>
    public ReasonletId? InjectedParent { get; init; }

    /// <summary>The matches each <c>find_procedure</c> lookup of the run returned, in call order.</summary>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    public IReadOnlyList<IReadOnlyList<ReasonletMatch>> Lookups
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    } = [];

    /// <summary>How long the run took.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>The id recorded as the run id in the provenance, such as the response id, or null.</summary>
    public string? RunId { get; init; }

    /// <summary>The finish reason of the run's last response, or null.</summary>
    public ChatFinishReason? FinishReason { get; init; }

    /// <summary>
    /// The source recorded in the provenance, or null for <see cref="ReasonletCaptureOptions.Source"/>.
    /// </summary>
    public string? Source { get; init; }
}
