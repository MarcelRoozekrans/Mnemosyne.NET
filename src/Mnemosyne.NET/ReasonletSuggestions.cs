using Mnemosyne.NET.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.NET;

/// <summary>
/// The recall step that every integration shares: it recalls the best known procedure for a new question under a timeout,
/// renders it, and counts the outcome on the <c>mnemosyne.chat.recalls</c> counter with a <c>mnemosyne.chat.recall</c>
/// activity.
/// </summary>
/// <remarks>
/// <para>
/// Memory never breaks the caller: a recall that fails, throws, times out or finds nothing, and a formatter that fails,
/// only leave the suggestion out. The caller's own cancellation propagates.
/// </para>
/// <para>
/// Every signal carries <c>mnemosyne.integration</c>, the <c>integration</c> the caller passes, such as
/// <see cref="ChatIntegration"/> or <see cref="AgentIntegration"/>. The outcome is <c>injected</c>, <c>no_match</c>,
/// <c>timeout</c>, the error code's name for an error result, <c>exception</c> with <c>error.type</c> set to the full type
/// name for an exception, or <c>formatter_failed</c>. No signal carries the scope, the subject, the question, the
/// suggested text, a tool or argument name, or any error or exception message.
/// </para>
/// </remarks>
internal static class ReasonletSuggestions
{
    /// <summary>The integration name of the chat middleware that <c>UseReasonlets</c> adds.</summary>
    public const string ChatIntegration = "chat";

    /// <summary>The integration name of the agent context provider.</summary>
    public const string AgentIntegration = "agent";

    /// <summary>The skip reason of a call that has no scope to recall in.</summary>
    public const string NoScopeReason = "no_scope";

    /// <summary>The skip reason of a call that is not a new user question, such as the continuation of a tool loop.</summary>
    public const string ContinuationReason = "continuation";

    /// <summary>
    /// Recalls the top match for <paramref name="question"/> under the recall timeout and renders it, counting the outcome.
    /// </summary>
    /// <param name="memory">The memory to recall from.</param>
    /// <param name="scope">The scope to recall in.</param>
    /// <param name="question">The new question.</param>
    /// <param name="options">The validated settings.</param>
    /// <param name="integration">The integration that calls, tagged on every signal as <c>mnemosyne.integration</c>.</param>
    /// <param name="ct">The caller's cancellation.</param>
    /// <returns>The match's id and text, or null when nothing is to be suggested.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="memory"/>, <paramref name="question"/>, <paramref name="options"/> or <paramref name="integration"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="options"/> is invalid.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> is cancelled.</exception>
    public static async ValueTask<ReasonletSuggestion?> RecallAsync(
        IReasonletMemory memory, ReasonletScope scope, string question, ReasonletSuggestionOptions options, string integration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(integration);
        var validation = options.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(options));
        }

        using var telemetry = ChatTelemetry.Start(integration);
        var timeout = options.RecallTimeout;
        using var timer = timeout == Timeout.InfiniteTimeSpan ? null : new CancellationTokenSource(timeout, options.TimeProvider);
        using var linked = timer is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        var token = linked?.Token ?? ct;
        var query = new ReasonletQuery { Scope = scope, Question = question, Top = 1, MinSimilarity = options.MinSimilarity };
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
            text = (options.Formatter ?? ReasonletPromptFormatter.Format)(match);
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
        return new ReasonletSuggestion(match.Reasonlet.Id, text);
    }

    /// <summary>
    /// Counts one call that skipped the recall: <c>mnemosyne.chat.recalls</c> with outcome <c>skipped</c>, the skip reason
    /// and the integration, and no activity.
    /// </summary>
    /// <param name="reason">Why the recall was skipped, such as <see cref="NoScopeReason"/> or <see cref="ContinuationReason"/>.</param>
    /// <param name="integration">The integration that skipped, tagged as <c>mnemosyne.integration</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reason"/> or <paramref name="integration"/> is null.</exception>
    public static void CountSkipped(string reason, string integration)
    {
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(integration);
        ChatTelemetry.Skipped(reason, integration);
    }

    /// <summary>
    /// Counts one recall that could not run at all, such as a memory that failed to resolve or a scope resolver that threw:
    /// <c>mnemosyne.chat.recalls</c> with outcome <c>exception</c>, <c>error.type</c> set to the exception's full type name
    /// and the integration, with a <c>mnemosyne.chat.recall</c> activity marked as thrown. Nothing of the exception but its
    /// type is recorded.
    /// </summary>
    /// <param name="exception">The exception that stopped the recall.</param>
    /// <param name="integration">The integration whose recall failed, tagged as <c>mnemosyne.integration</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> or <paramref name="integration"/> is null.</exception>
    public static void CountFailed(Exception exception, string integration)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(integration);
        using var telemetry = ChatTelemetry.Start(integration);
        telemetry.Threw(exception);
    }
}
