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
