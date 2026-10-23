using System.Reflection;
using Minos;
using Mnemosyne.NET.Minos;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Minos;

/// <summary>
/// A hand-written <see cref="IDecisionClient"/>, as Minos's testing guide describes. The typed calls are extension methods
/// over <see cref="EvaluateAsync"/>, so this one method sees every request the evaluator sends.
/// </summary>
internal sealed class FakeDecisionClient : IDecisionClient
{
    private readonly Func<DecisionRequest, CancellationToken, Result<DecisionResponse, DecisionError>> _reply;
    private readonly List<DecisionRequest> _requests = [];

    private FakeDecisionClient(Func<DecisionRequest, CancellationToken, Result<DecisionResponse, DecisionError>> reply) => _reply = reply;

    /// <summary>Gets every request the code under test sent.</summary>
    public IReadOnlyList<DecisionRequest> Requests => _requests;

    /// <summary>Gets the token of the last call.</summary>
    public CancellationToken LastToken { get; private set; }

    /// <summary>Answers the judge's Noul with <paramref name="probability"/>, reusing the generated set's definition.</summary>
    public static FakeDecisionClient Answering(double probability) =>
        new((_, _) => Result<DecisionResponse, DecisionError>.Success(new DecisionResponse(
            MinosJudgeQuestions.Definition,
            [QuestionAnswer.Noul(probability)],
            model: "fake")));

    /// <summary>
    /// Answers the judge's Noul with <paramref name="probability"/> unchecked. <see cref="QuestionAnswer.Noul"/> rejects a value
    /// outside [0, 1], but Minos does not range-check what it reads off the wire, so this builds the answer through the internal
    /// constructor a provider's parser uses, to reach the evaluator's trust-boundary check.
    /// </summary>
    public static FakeDecisionClient AnsweringUnchecked(double probability)
    {
        var constructor = typeof(QuestionAnswer).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var answer = (QuestionAnswer)constructor.Invoke(["resolves", QuestionKind.Noul, -1, probability, 0.0, null, 0, 0]);
        return new((_, _) => Result<DecisionResponse, DecisionError>.Success(new DecisionResponse(
            MinosJudgeQuestions.Definition,
            [answer],
            model: "fake")));
    }

    /// <summary>Fails every call with <paramref name="kind"/> and <paramref name="message"/>.</summary>
    public static FakeDecisionClient Failing(DecisionErrorKind kind, string message = "The fake failed on purpose.") =>
        new((_, _) => Result<DecisionResponse, DecisionError>.Failure(new DecisionError(kind, message)));

    /// <summary>Throws <paramref name="exception"/> from every call.</summary>
    public static FakeDecisionClient Throwing(Exception exception) => new((_, _) => throw exception);

    /// <summary>Honours the token: a cancelled call throws, as a real client does.</summary>
    public static FakeDecisionClient HonouringCancellation() => new((_, ct) =>
    {
        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException("The token was not cancelled.");
    });

    public ValueTask<Result<DecisionResponse, DecisionError>> EvaluateAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        _requests.Add(request);
        LastToken = cancellationToken;
        return ValueTask.FromResult(_reply(request, cancellationToken));
    }

    // The fake offers no services.
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
