using ZeroAlloc.ORM;

namespace Mnemosyne.NET.PostgreSql.Rows;

/// <summary>A run in a subject export: an execution with every input to its verdict and its feedback, read by position.</summary>
/// <param name="Id">The id of the run.</param>
/// <param name="ReasonletId">The Reasonlet that was executed.</param>
/// <param name="Scope">The scope.</param>
/// <param name="ExecutedAt">When the run was recorded.</param>
/// <param name="AgentSuccess">The agent's own flag.</param>
/// <param name="DurationTicks">The run's duration in ticks.</param>
/// <param name="Score">The agent's score, or null.</param>
/// <param name="EvaluatedSuccess">The evaluator's verdict, or null when none judged the run.</param>
/// <param name="EvaluationScore">The evaluator's score, or null when none judged the run.</param>
/// <param name="Evaluator">The evaluator's name, or null when none judged the run.</param>
/// <param name="FeedbackWorked">A human's verdict from feedback, or null.</param>
/// <param name="Success">The effective success.</param>
/// <param name="Source">The provenance source.</param>
/// <param name="RunId">The provenance run id, or null.</param>
/// <param name="SubjectId">The provenance subject id, or null.</param>
/// <param name="Tags">The provenance tags as a JSON object, or null.</param>
/// <param name="Utility">The feedback utility, or null when there is no feedback row.</param>
/// <param name="Correctness">The feedback correctness, or null when there is no feedback row.</param>
/// <param name="Reusability">The feedback reusability, or null when there is no feedback row.</param>
/// <param name="FeedbackRecordedAt">When the feedback was recorded, or null when there is no feedback row.</param>
internal sealed record SubjectRunRow(
    Guid Id,
    Guid ReasonletId,
    string Scope,
    DateTimeOffset ExecutedAt,
    bool AgentSuccess,
    long DurationTicks,
    double? Score,
    bool? EvaluatedSuccess,
    double? EvaluationScore,
    string? Evaluator,
    bool? FeedbackWorked,
    bool Success,
    string Source,
    string? RunId,
    string? SubjectId,
    [Json] string? Tags,
    double? Utility,
    double? Correctness,
    double? Reusability,
    DateTimeOffset? FeedbackRecordedAt);
