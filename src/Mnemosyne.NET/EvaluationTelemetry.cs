using System.Diagnostics;
using System.Diagnostics.Metrics;
using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET;

/// <summary>
/// The OpenTelemetry signals of one judge call: a <c>mnemosyne.evaluate</c> activity, the <c>mnemosyne.evaluations</c>
/// counter and the <c>mnemosyne.evaluation.duration</c> histogram, and the shared operation count and duration under
/// operation <c>evaluate</c>. It shares the recall's source and meter, under the names on <see cref="MnemosyneTelemetry"/>.
/// </summary>
/// <remarks>
/// <para>
/// Start one with <see cref="Start"/> before the judge is called, report how the call ended with exactly one of
/// <see cref="Judged"/>, <see cref="Failed"/> or <see cref="Threw"/>, and dispose it, which counts, times and stops it.
/// The outcome follows the facade's rules: <c>ok</c> when the judge answered, the error code's name for an error result,
/// and <c>exception</c>, with <c>error.type</c> set to the full type name, for an exception. A timeout is reported as
/// Unavailable, since the facade treats it as one. The activity records how the judge call itself ended, so a judge
/// that is down shows as a failure here even though the run is then stored with the agent's verdict.
/// </para>
/// <para>
/// No signal carries the question, the answer, or any error or exception message. The judge's name, which the
/// evaluator contract limits to naming the judge, is tagged only from an evaluation that passed validation.
/// </para>
/// </remarks>
internal sealed class EvaluationTelemetry : IDisposable
{
    private const string ActivityName = "mnemosyne.evaluate";
    private const string Operation = "evaluate";
    private const string EvaluatorTag = "mnemosyne.evaluator";
    private const string SuccessTag = "mnemosyne.evaluation.success";
    private const string ScoreTag = "mnemosyne.evaluation.score";

    private static readonly Counter<long> Evaluations =
        RecallTelemetry.Meter.CreateCounter<long>("mnemosyne.evaluations", "{evaluation}", "Judge calls, by evaluator and outcome.");

    private static readonly Histogram<double> Duration =
        RecallTelemetry.Meter.CreateHistogram<double>("mnemosyne.evaluation.duration", "s", "How long judge calls take, by evaluator.");

    private readonly Activity? _activity;
    private readonly long _started;
    private string _outcome = RecallTelemetry.ExceptionOutcome;
    private string? _evaluator;

    private EvaluationTelemetry(Activity? activity, long started)
    {
        _activity = activity;
        _started = started;
    }

    /// <summary>Starts the <c>mnemosyne.evaluate</c> activity, null when no one listens, and the clock.</summary>
    internal static EvaluationTelemetry Start() =>
        new(RecallTelemetry.Source.StartActivity(ActivityName), Stopwatch.GetTimestamp());

    /// <summary>Reports a valid verdict: outcome <c>ok</c>, tagged with the judge's name, its success and its score.</summary>
    internal void Judged(ReasonletEvaluation evaluation)
    {
        _outcome = RecallTelemetry.Ok;
        _evaluator = evaluation.Evaluator;
        _activity?.SetTag(EvaluatorTag, evaluation.Evaluator);
        _activity?.SetTag(SuccessTag, evaluation.Success);
        _activity?.SetTag(ScoreTag, evaluation.Score);
    }

    /// <summary>Reports an error result, or a timeout as Unavailable: the outcome and <c>error.type</c> are the code's name.</summary>
    internal void Failed(MnemosyneErrorCode code)
    {
        _outcome = RecallTelemetry.ErrorName(code);
        RecallTelemetry.Failed(_activity, _outcome);
    }

    /// <summary>
    /// Reports an exception: outcome <c>exception</c>, and an <c>exception</c> event and <c>error.type</c> that carry only
    /// its full type name.
    /// </summary>
    internal void Threw(Exception ex)
    {
        _outcome = RecallTelemetry.ExceptionOutcome;
        RecallTelemetry.Threw(_activity, ex);
    }

    /// <summary>Counts and times the judge call, under the judge's name when it answered, and stops the activity.</summary>
    public void Dispose()
    {
        var elapsed = Stopwatch.GetElapsedTime(_started).TotalSeconds;
        if (_evaluator is { } evaluator)
        {
            Evaluations.Add(1, new TagList { { EvaluatorTag, evaluator }, { RecallTelemetry.OutcomeTag, _outcome } });
            Duration.Record(elapsed, new TagList { { EvaluatorTag, evaluator } });
        }
        else
        {
            Evaluations.Add(1, new TagList { { RecallTelemetry.OutcomeTag, _outcome } });
            Duration.Record(elapsed);
        }

        RecallTelemetry.CountOperation(Operation, _outcome, _started);
        _activity?.Dispose();
    }
}
