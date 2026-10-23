namespace Mnemosyne.NET.Abstractions;

/// <summary>Which verdict decides whether a run counts as a success.</summary>
/// <remarks>
/// The effective success of a run is a human's <see cref="ReasonletFeedback.Worked"/> verdict, else the evaluator's
/// <see cref="ReasonletEvaluation.Success"/>, else the agent's own <see cref="ReasonletOutcome.Success"/>. The values are
/// numbered from 1, as every Mnemosyne enum is; 0 is undefined.
/// </remarks>
public enum ReasonletVerdictSource
{
    /// <summary>The agent's own <see cref="ReasonletOutcome.Success"/> counts, because no evaluator judged the run.</summary>
    Agent = 1,

    /// <summary>The evaluator's <see cref="ReasonletEvaluation.Success"/> counts.</summary>
    Evaluator = 2,

    /// <summary>
    /// A human's <see cref="ReasonletFeedback.Worked"/> verdict counts. Feedback comes after a run is recorded, so a
    /// receipt of a record or a remember never carries this source.
    /// </summary>
    Feedback = 3,
}
