using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.NET;

/// <summary>
/// The chat middleware that <c>UseReasonlets</c> adds: it recalls the best known procedure for a new question and offers it
/// to the model as a system message, can expose the <c>find_procedure</c> tool, and hands each finished call to the
/// background capture.
/// </summary>
/// <remarks>
/// <para>
/// A call resolves its scope from <see cref="ReasonletChatOptionsExtensions.GetReasonletScope"/>, else
/// <see cref="ReasonletChatOptions.DefaultScope"/>; with neither it passes straight through with the caller's own messages
/// and options. Otherwise the middleware works on copies: the injected suggestion goes into a new message list, and the
/// inner client gets a clone of the options without <see cref="ReasonletChatOptionsExtensions.ScopeKey"/>, with the tool
/// added to a new tool list. The caller's messages and options are never changed.
/// </para>
/// <para>
/// Memory never breaks the call: a recall that fails, throws, times out or finds nothing, and a formatter that fails, only
/// leave the suggestion out, a memory that cannot be resolved leaves the suggestion and the tool out, and a full queue,
/// or a hand-off that throws, only drops the capture. Each shows up in
/// <see cref="ChatTelemetry"/> or the queue's counter. The caller's own cancellation propagates, from the recall as from the
/// inner call. A call that throws or is cancelled is not captured.
/// </para>
/// <para>
/// A call whose options hold <see cref="BypassKey"/>, as every judge call of <see cref="ChatReasonletEvaluator"/> does,
/// passes straight through with the caller's messages and a clone of its options without the key.
/// </para>
/// </remarks>
internal sealed class ReasonletChatClient : DelegatingChatClient
{
    /// <summary>
    /// The internal key that marks a call Mnemosyne makes itself, such as a judge call, which the middleware passes straight
    /// through: no recall, no tool, no capture and no count. The key never reaches the inner client.
    /// </summary>
    internal const string BypassKey = "mnemosyne.bypass";

    // Resolved on first use rather than when the pipeline is built: the memory can depend on a client that has this
    // middleware, as the chat evaluator does when it judges with the host's unkeyed client. A resolution that throws is
    // not cached, so a later call tries again.
    private readonly Lazy<IReasonletMemory> _memory;
    private readonly Lazy<ReasonletCaptureQueue?> _queue;
    private readonly ReasonletChatOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Creates the middleware over a memory and a queue that already exist.</summary>
    /// <param name="inner">The client to call.</param>
    /// <param name="memory">The memory to recall from, and that the tool looks up in.</param>
    /// <param name="queue">The capture queue, or null when nothing is captured.</param>
    /// <param name="options">The validated settings.</param>
    /// <param name="time">The clock for the recall timeout and the call's duration.</param>
    public ReasonletChatClient(IChatClient inner, IReasonletMemory memory, ReasonletCaptureQueue? queue, ReasonletChatOptions options, TimeProvider time)
        : this(inner, new Lazy<IReasonletMemory>(memory), new Lazy<ReasonletCaptureQueue?>(queue), options, time)
    {
    }

    /// <summary>Creates the middleware over a memory and a queue that are resolved on first use.</summary>
    /// <remarks>
    /// Give lazies that do not cache an exception, such as <see cref="LazyThreadSafetyMode.PublicationOnly"/> ones: a
    /// resolution that throws is then tried again on the next call. Either one that throws leaves the call unbroken.
    /// </remarks>
    /// <param name="inner">The client to call.</param>
    /// <param name="memory">Resolves the memory to recall from, and that the tool looks up in.</param>
    /// <param name="queue">Resolves the capture queue, or null when nothing is captured.</param>
    /// <param name="options">The validated settings.</param>
    /// <param name="time">The clock for the recall timeout and the call's duration.</param>
    public ReasonletChatClient(
        IChatClient inner, Lazy<IReasonletMemory> memory, Lazy<ReasonletCaptureQueue?> queue, ReasonletChatOptions options, TimeProvider time)
        : base(inner)
    {
        _memory = memory;
        _queue = queue;
        _options = options;
        _time = time;
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (WithoutBypass(options) is { } bypassed)
        {
            return await base.GetResponseAsync(messages, bypassed, cancellationToken).ConfigureAwait(false);
        }

        var call = await PrepareAsync(messages, options, cancellationToken).ConfigureAwait(false);
        if (call is null)
        {
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }

        var started = _time.GetTimestamp();
        var response = await base.GetResponseAsync(call.Messages, call.Options, cancellationToken).ConfigureAwait(false);
        HandOff(call, () => response, _time.GetElapsedTime(started));
        return response;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The capture happens only after the inner stream is read to the end. A consumer that stops early, a stream that throws
    /// and a cancelled call are not captured.
    /// </remarks>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (WithoutBypass(options) is { } bypassed)
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, bypassed, cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }

            yield break;
        }

        var call = await PrepareAsync(messages, options, cancellationToken).ConfigureAwait(false);
        if (call is null)
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }

            yield break;
        }

        var updates = new List<ChatResponseUpdate>();
        var started = _time.GetTimestamp();
        await foreach (var update in base.GetStreamingResponseAsync(call.Messages, call.Options, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
            yield return update;
        }

        HandOff(call, updates.ToChatResponse, _time.GetElapsedTime(started));
    }

    /// <summary>
    /// Runs the steps before the inner call: resolves the scope, recalls and injects for a new question, and builds the
    /// options the inner client gets.
    /// </summary>
    /// <returns>The prepared call, or null when there is no scope and the call passes straight through.</returns>
    private async ValueTask<PreparedCall?> PrepareAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken ct)
    {
        var scope = options.GetReasonletScope()
            ?? (_options.DefaultScope is { } fallback ? new ReasonletCallScope(fallback, null) : null);
        if (scope is null)
        {
            // Counted whenever the call would have done something: with both switches off it was never going to.
            if (_options.Recall || _options.Capture)
            {
                ChatTelemetry.Skipped(ChatTelemetry.NoScopeReason);
            }

            return null;
        }

        // A copy of the list, not of the messages: a caller that adds to its own list while a stream is read must not
        // change what is captured.
        List<ChatMessage> request = [.. messages];
        var sent = request;
        ReasonletId? parent = null;
        var question = request.Count > 0 && request[^1] is { } last && last.Role == ChatRole.User && !string.IsNullOrWhiteSpace(last.Text)
            ? last.Text
            : null;
        var wantsTool = _options.ExposeFindProcedure && !HasFindProcedure(options);
        var memory = (_options.Recall && question is not null) || wantsTool ? ResolveMemory(ct) : null;
        if (_options.Recall)
        {
            if (question is null)
            {
                ChatTelemetry.Skipped(ChatTelemetry.ContinuationReason);
            }
            else if (memory is not null && await RecallAsync(memory, scope.Scope, question, ct).ConfigureAwait(false) is { } suggestion)
            {
                parent = suggestion.Id;
                sent = Inject(request, suggestion.Text);
            }
        }

        var lookup = wantsTool && memory is not null ? new FindProcedureFunction(memory, scope.Scope) : null;
        return new PreparedCall(scope, request, sent, InnerOptions(options, lookup), parent, lookup);
    }

    /// <summary>
    /// Runs the step after an inner call that returned: hands it to the capture queue when capture is on. Nothing here
    /// throws into the call: a failure to build or offer the item is counted on the queue's counter as <c>exception</c>.
    /// </summary>
    /// <param name="call">The prepared call.</param>
    /// <param name="response">Builds the call's response; for a stream, from its updates.</param>
    /// <param name="duration">How long the inner call took.</param>
    private void HandOff(PreparedCall call, Func<ChatResponse> response, TimeSpan duration)
    {
        try
        {
            if (!_options.Capture || _queue.Value is not { } queue)
            {
                return;
            }

            // A full or closed queue counts the drop itself; the call goes on either way.
            queue.TryEnqueue(CaptureItem(call, response(), duration));
        }
        catch (Exception ex)
        {
            CaptureTelemetry.QueueFailed(ex);
        }
    }

    /// <summary>
    /// Builds the capture item of a call that returned: the caller's messages without the suggestion, then the response's,
    /// with the injected parent, the tool's lookups, the duration and the response's id and finish reason.
    /// </summary>
    internal static ReasonletCaptureItem CaptureItem(PreparedCall call, ChatResponse response, TimeSpan duration) => new(
        call.Scope.Scope,
        call.Scope.SubjectId,
        [.. call.Request, .. response.Messages],
        call.Parent,
        call.Lookup?.Lookups ?? [],
        duration,
        response.ResponseId,
        response.FinishReason);

    /// <summary>
    /// Recalls the top match for <paramref name="question"/> under the recall timeout and renders it, counting the outcome.
    /// </summary>
    /// <returns>The match's id and text, or null when nothing is to be injected.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> is cancelled.</exception>
    private async ValueTask<(ReasonletId Id, string Text)?> RecallAsync(IReasonletMemory memory, ReasonletScope scope, string question, CancellationToken ct)
    {
        using var telemetry = ChatTelemetry.Start();
        var timeout = _options.RecallTimeout;
        using var timer = timeout == Timeout.InfiniteTimeSpan ? null : new CancellationTokenSource(timeout, _time);
        using var linked = timer is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        var token = linked?.Token ?? ct;
        var query = new ReasonletQuery { Scope = scope, Question = question, Top = 1, MinSimilarity = _options.MinSimilarity };
        Task<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>>? pending = null;
        Result<IReadOnlyList<ReasonletMatch>, MnemosyneError> recalled;
        try
        {
            ct.ThrowIfCancellationRequested();

            // A hard stop: the wait ends when the token is cancelled, even when the memory ignores the token it was given.
            pending = memory.RecallAsync(query, token).AsTask();
            recalled = await pending.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ReasonletMemory.Abandon(pending);
            telemetry.Cancelled();
            throw;
        }
        catch (OperationCanceledException) when (timer is { IsCancellationRequested: true })
        {
            ReasonletMemory.Abandon(pending);
            telemetry.TimedOut();
            return null;
        }
        catch (Exception ex)
        {
            telemetry.Threw(ex);
            return null;
        }

        if (recalled.IsFailure)
        {
            telemetry.Failed(recalled.Error.Code);
            return null;
        }

        if (recalled.Value.Count == 0)
        {
            telemetry.NoMatch();
            return null;
        }

        var match = recalled.Value[0];
        string? text;
        try
        {
            text = (_options.Formatter ?? ReasonletPromptFormatter.Format)(match);
        }
        catch (Exception)
        {
            text = null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            telemetry.FormatterFailed();
            return null;
        }

        telemetry.Injected();
        return (match.Reasonlet.Id, text);
    }

    /// <summary>Copies the request with the suggestion as a system message after the leading system messages.</summary>
    private static List<ChatMessage> Inject(IReadOnlyList<ChatMessage> request, string text)
    {
        // The last message is the user's question, so the scan always stops inside the list.
        Debug.Assert(request[^1].Role == ChatRole.User, "Only a new user question is injected into.");
        var at = 0;
        while (request[at].Role == ChatRole.System)
        {
            at++;
        }

        var sent = new List<ChatMessage>(request.Count + 1);
        sent.AddRange(request);
        sent.Insert(at, new ChatMessage(ChatRole.System, text));
        return sent;
    }

    /// <summary>
    /// Resolves the memory for this call. A resolution that throws does not break the call: it is counted once on the
    /// recall counter as <c>exception</c>, with a failed <c>mnemosyne.chat.recall</c> activity, and the call goes on
    /// without recall, inject and tool. The caller's own cancellation propagates. A failure is not cached: the next call
    /// tries again.
    /// </summary>
    /// <returns>The memory, or null when resolving it threw.</returns>
    private IReasonletMemory? ResolveMemory(CancellationToken ct)
    {
        try
        {
            return _memory.Value;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            using var telemetry = ChatTelemetry.Start();
            telemetry.Threw(ex);
            return null;
        }
    }

    /// <summary>
    /// For a call marked with <see cref="BypassKey"/>, a clone of its options without the key; null for any other call.
    /// </summary>
    private static ChatOptions? WithoutBypass(ChatOptions? options)
    {
        if (options?.AdditionalProperties is not { } properties || !properties.ContainsKey(BypassKey))
        {
            return null;
        }

        var clone = options.Clone();
        clone.AdditionalProperties!.Remove(BypassKey);
        return clone;
    }

    private static bool HasFindProcedure(ChatOptions? options)
    {
        if (options?.Tools is not { } tools)
        {
            return false;
        }

        foreach (var tool in tools)
        {
            if (tool.Name == ReasonletTools.FindProcedureName)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The options the inner client gets: none when the caller passed none and no tool is added, else a clone without the
    /// scope key and with the tool, if any, added to a new tool list.
    /// </summary>
    private static ChatOptions? InnerOptions(ChatOptions? options, FindProcedureFunction? lookup)
    {
        if (options is null)
        {
            return lookup is null ? null : new ChatOptions { Tools = [lookup] };
        }

        var clone = options.Clone();
        clone.AdditionalProperties?.Remove(ReasonletChatOptionsExtensions.ScopeKey);
        if (lookup is not null)
        {
            clone.Tools = [.. clone.Tools ?? [], lookup];
        }

        return clone;
    }

    /// <summary>One call between its preparation and its hand-off.</summary>
    /// <param name="Scope">The call's scope and subject.</param>
    /// <param name="Request">The caller's messages, as they were given.</param>
    /// <param name="Messages">The messages the inner client gets: the request, or a copy with the suggestion.</param>
    /// <param name="Options">The options the inner client gets.</param>
    /// <param name="Parent">The injected match's id, or null.</param>
    /// <param name="Lookup">This call's <c>find_procedure</c> tool, or null when none was added.</param>
    internal sealed record PreparedCall(
        ReasonletCallScope Scope,
        IReadOnlyList<ChatMessage> Request,
        IReadOnlyList<ChatMessage> Messages,
        ChatOptions? Options,
        ReasonletId? Parent,
        FindProcedureFunction? Lookup);
}
