using System.Text.Json;
using Minos;
using Mnemosyne.NET.Abstractions;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Minos;

/// <summary>
/// An <see cref="IReasonletEvaluator"/> that judges a run by asking a Minos.NET decision model one yes/no question: does the
/// answer resolve the question by following the procedure?
/// </summary>
/// <remarks>
/// <para>
/// The score is the answer's probability, and the run succeeds when it reaches
/// <see cref="MinosEvaluatorOptions.SuccessThreshold"/>. The judge sees the question, the intent, the procedure and the
/// answer, which leave the process for the decision-model provider the host chose. It never sees the agent's own outcome
/// or the Reasonlet's name.
/// </para>
/// <para>
/// A failed call is <see cref="MnemosyneErrorCode.Unavailable"/> when the judge could not judge, and
/// <see cref="MnemosyneErrorCode.Validation"/> for <see cref="DecisionErrorKind.InvalidQuestions"/>, which is a bug in
/// Mnemosyne's own question set. A probability outside [0, 1] or NaN is a response the provider got wrong, so it is
/// <see cref="MnemosyneErrorCode.Unavailable"/> with the detail <c>minos: InvalidResponse</c>. The error's detail names only
/// the kind, never Minos's message, which could quote the request. Exceptions and cancellation propagate.
/// </para>
/// </remarks>
public sealed class MinosReasonletEvaluator : IReasonletEvaluator
{
    private const string FailureMessage = "The Minos judge could not judge the run.";

    private readonly IDecisionClient _client;
    private readonly MinosEvaluatorOptions _options;

    /// <summary>Initializes a new instance of the <see cref="MinosReasonletEvaluator"/> class.</summary>
    /// <param name="client">The Minos client that asks the decision model.</param>
    /// <param name="options">The evaluator's settings.</param>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="options"/> fails <see cref="MinosEvaluatorOptions.Validate"/>.</exception>
    public MinosReasonletEvaluator(IDecisionClient client, MinosEvaluatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        var validation = options.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(options));
        }

        _client = client;
        _options = options;
    }

    /// <inheritdoc/>
    public async ValueTask<Result<ReasonletEvaluation, MnemosyneError>> EvaluateAsync(ReasonletEvaluationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var procedure = JsonDocument.Parse(ReasonletGraphJson.Serialize(request.Graph));
        var state = new MinosJudgeState(request.Question, request.Intent, procedure.RootElement, request.Answer);

        var result = await _client
            .EvaluateAsync<MinosJudgeQuestions, MinosJudgeState>(state, MinosJudgeJsonContext.Default.MinosJudgeState, ct)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return Result<ReasonletEvaluation, MnemosyneError>.Failure(ToError(result.Error.Kind));
        }

        // Minos does not range-check a probability read off the wire, and this is the trust boundary.
        var probability = result.Value.Resolves.Probability;
        if (!IsProbability(probability))
        {
            return Result<ReasonletEvaluation, MnemosyneError>.Failure(ToError(DecisionErrorKind.InvalidResponse));
        }

        return Result<ReasonletEvaluation, MnemosyneError>.Success(
            new ReasonletEvaluation(probability >= _options.SuccessThreshold, probability, _options.EvaluatorName));
    }

    /// <summary>Returns whether a value is a probability: a number in [0, 1]. NaN is not one.</summary>
    internal static bool IsProbability(double value) => value >= 0 && value <= 1;

    /// <summary>Maps a Minos failure to a fixed error whose detail names only the kind.</summary>
    private static MnemosyneError ToError(DecisionErrorKind kind)
    {
        var code = kind is DecisionErrorKind.InvalidQuestions
            ? MnemosyneErrorCode.Validation
            : MnemosyneErrorCode.Unavailable;
        return new MnemosyneError(code, FailureMessage, $"minos: {kind}");
    }
}
