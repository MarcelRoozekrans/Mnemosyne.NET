using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET;

/// <summary>One finished chat call, handed from the middleware to the capture worker.</summary>
/// <param name="Scope">The scope to capture into.</param>
/// <param name="SubjectId">The subject recorded in the provenance, or null.</param>
/// <param name="Messages">The request messages plus the response messages, without the injected suggestion.</param>
/// <param name="InjectedParent">The Reasonlet the injected suggestion came from, or null when nothing was injected.</param>
/// <param name="Lookups">The matches each <c>find_procedure</c> lookup of the call returned, in call order.</param>
/// <param name="Duration">
/// How long the inner call took. For a streamed call it runs from the first read of the stream to its end, so it includes
/// the time the consumer spends between reads.
/// </param>
/// <param name="ResponseId">The response id, recorded as the run id, or null.</param>
/// <param name="FinishReason">The response's finish reason, or null.</param>
/// <param name="EndsWithPendingCall">
/// Whether the run ended on a call nobody answered, decided when the call was handed off. It is decided then because a
/// function invocation layer outside the middleware marks the calls it invokes as informational only afterwards, while the
/// item may still wait in the queue.
/// </param>
internal sealed record ReasonletCaptureItem(
    ReasonletScope Scope,
    string? SubjectId,
    IReadOnlyList<ChatMessage> Messages,
    ReasonletId? InjectedParent,
    IReadOnlyList<IReadOnlyList<ReasonletMatch>> Lookups,
    TimeSpan Duration,
    string? ResponseId,
    ChatFinishReason? FinishReason,
    bool EndsWithPendingCall);
