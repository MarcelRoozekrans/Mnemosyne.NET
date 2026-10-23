namespace Mnemosyne.NET.Abstractions;

/// <summary>The size limits of the text an evaluator judges and of the verdict it returns, public so an evaluator package can validate against the same bounds.</summary>
public static class EvaluationLimits
{
    /// <summary>
    /// The most code points a run's question may hold, and an answer may hold to be judged: a longer answer is stored
    /// unjudged. The answer goes to a model, so the cap bounds cost and abuse.
    /// </summary>
    public const int MaxRunTextLength = 32_000;

    /// <summary>The most code points an evaluator's name may hold.</summary>
    public const int MaxEvaluatorLength = 100;
}
