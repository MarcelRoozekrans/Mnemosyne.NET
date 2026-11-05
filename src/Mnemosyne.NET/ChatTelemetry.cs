using System.Diagnostics;
using System.Diagnostics.Metrics;
using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET;

/// <summary>
/// The OpenTelemetry signals of an integration's recall, the chat middleware's or an agent's: a <c>mnemosyne.chat.recall</c>
/// activity and the <c>mnemosyne.chat.recalls</c> counter for each recall, and the same counter for each call that skips
/// it. It shares the recall's source and meter. Every activity and count carries <c>mnemosyne.integration</c>, the
/// integration that made the call.
/// </summary>
/// <remarks>
/// <para>
/// Start one with <see cref="Start"/> before the recall, report how it ended with exactly one of <see cref="Injected"/>,
/// <see cref="NoMatch"/>, <see cref="TimedOut"/>, <see cref="Failed"/>, <see cref="Threw"/>, <see cref="FormatterFailed"/> or
/// <see cref="Cancelled"/>, and dispose it, which counts it and stops the activity. The outcome is <c>injected</c>,
/// <c>no_match</c>, <c>timeout</c>, the error code's name for an error result, <c>exception</c> with <c>error.type</c> set to
/// the full type name for an exception, or <c>formatter_failed</c>. A call that skips the recall is counted by
/// <see cref="Skipped"/> as <c>skipped</c> with <c>mnemosyne.skip_reason</c> <c>no_scope</c> or <c>continuation</c>, and
/// has no activity. A call without a scope counts as <c>no_scope</c> when recall or capture is on, and a request that
/// does not end in a new question counts as <c>continuation</c> only when recall is on.
/// </para>
/// <para>
/// No signal carries the scope, the subject, the question, the injected text, a tool or argument name, or any error or
/// exception message.
/// </para>
/// </remarks>
internal sealed class ChatTelemetry : IDisposable
{
    private const string ActivityName = "mnemosyne.chat.recall";
    private const string SkipReasonTag = "mnemosyne.skip_reason";
    private const string IntegrationTag = "mnemosyne.integration";
    private const string ErrorTypeTag = "error.type";
    private const string SkippedOutcome = "skipped";
    private const string InjectedOutcome = "injected";
    private const string NoMatchOutcome = "no_match";
    private const string TimeoutOutcome = "timeout";
    private const string FormatterFailedOutcome = "formatter_failed";

    private static readonly Counter<long> Recalls =
        RecallTelemetry.Meter.CreateCounter<long>("mnemosyne.chat.recalls", "{recall}", "Recalls of an integration, by outcome.");

    private readonly Activity? _activity;
    private readonly string _integration;
    private string _outcome = RecallTelemetry.ExceptionOutcome;
    private string? _errorType;
    private bool _cancelled;

    private ChatTelemetry(Activity? activity, string integration)
    {
        _activity = activity;
        _integration = integration;
    }

    /// <summary>
    /// Starts the <c>mnemosyne.chat.recall</c> activity, null when no one listens, tagged with
    /// <paramref name="integration"/>.
    /// </summary>
    internal static ChatTelemetry Start(string integration)
    {
        var activity = RecallTelemetry.Source.StartActivity(ActivityName);
        activity?.SetTag(IntegrationTag, integration);
        return new(activity, integration);
    }

    /// <summary>Counts one call of <paramref name="integration"/> that skipped the recall, tagged with <paramref name="reason"/>.</summary>
    internal static void Skipped(string reason, string integration) =>
        Recalls.Add(1, new TagList { { RecallTelemetry.OutcomeTag, SkippedOutcome }, { SkipReasonTag, reason }, { IntegrationTag, integration } });

    /// <summary>Reports a match that was injected.</summary>
    internal void Injected() => SetOutcome(InjectedOutcome);

    /// <summary>Reports a recall that returned nothing.</summary>
    internal void NoMatch() => SetOutcome(NoMatchOutcome);

    /// <summary>Reports a recall cut short by the recall timeout: outcome and <c>error.type</c> <c>timeout</c>.</summary>
    internal void TimedOut() => Fail(TimeoutOutcome);

    /// <summary>Reports an error result: the outcome and <c>error.type</c> are the code's name.</summary>
    internal void Failed(MnemosyneErrorCode code) => Fail(RecallTelemetry.ErrorName(code));

    /// <summary>Reports a formatter that threw or wrote nothing: outcome and <c>error.type</c> <c>formatter_failed</c>.</summary>
    internal void FormatterFailed() => Fail(FormatterFailedOutcome);

    /// <summary>Reports an exception: outcome <c>exception</c>, and <c>error.type</c> and an event that carry only its full type name.</summary>
    internal void Threw(Exception ex)
    {
        SetOutcome(RecallTelemetry.ExceptionOutcome);
        _errorType = ex.GetType().FullName!;
        RecallTelemetry.Threw(_activity, ex);
    }

    /// <summary>
    /// Reports a recall cut short by the caller's own cancellation: nothing is counted and the activity is not marked as
    /// failed, since the chat call itself is cancelled.
    /// </summary>
    internal void Cancelled() => _cancelled = true;

    /// <summary>Counts the recall, unless the caller cancelled it, and stops the activity.</summary>
    public void Dispose()
    {
        if (!_cancelled)
        {
            var tags = new TagList { { RecallTelemetry.OutcomeTag, _outcome }, { IntegrationTag, _integration } };
            if (_errorType is not null)
            {
                tags.Add(ErrorTypeTag, _errorType);
            }

            Recalls.Add(1, tags);
        }

        _activity?.Dispose();
    }

    private void SetOutcome(string outcome)
    {
        _outcome = outcome;
        _activity?.SetTag(RecallTelemetry.OutcomeTag, outcome);
    }

    private void Fail(string outcome)
    {
        SetOutcome(outcome);
        RecallTelemetry.Failed(_activity, outcome);
    }
}
