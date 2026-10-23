using System.Text.Json;
using Minos;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Minos;
using Mnemosyne.NET.Tests.Abstractions;
using Xunit;

namespace Mnemosyne.NET.Tests.Minos;

public sealed class MinosReasonletEvaluatorTests
{
    private const string NameSentinel = "NAME-SENTINEL-5c1e";
    private const string MessageSentinel = "MESSAGE-SENTINEL-9b2d";

    // An outcome whose values appear nowhere else, so finding either in the state means the outcome leaked.
    private static readonly ReasonletOutcome SentinelOutcome = new(true, TimeSpan.FromSeconds(86_399.25), 0.123456789);

    private static ReasonletGraph Graph() => new()
    {
        Nodes =
        [
            new ReasonletNode
            {
                Id = "n1",
                Type = "step",
                Name = "Look it up",
                Tool = "search",
                Properties = new Dictionary<string, JsonElement>
                {
                    ["tool"] = JsonDocument.Parse("""{"name":"search","args":["a","b"]}""").RootElement.Clone(),
                },
            },
            new ReasonletNode { Id = "n2", Type = "step" },
        ],
        Edges = [new ReasonletEdge { From = "n1", To = "n2" }],
        SchemaVersion = ReasonletGraph.CurrentSchemaVersion,
    };

    private static ReasonletEvaluationRequest Request() => new()
    {
        Graph = Graph(),
        Name = NameSentinel,
        Intent = "find the invoice",
        Question = "Where is invoice 42?",
        Answer = "It is in the March folder.",
        Outcome = SentinelOutcome,
    };

    private static MinosReasonletEvaluator Evaluator(IDecisionClient client, MinosEvaluatorOptions? options = null) =>
        new(client, options ?? new MinosEvaluatorOptions());

    // ---- The verdict ----

    [Fact]
    public async Task TheProbabilityIsTheScore()
    {
        var result = await Evaluator(FakeDecisionClient.Answering(0.8)).EvaluateAsync(Request(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(new ReasonletEvaluation(true, 0.8, "minos"), result.Value);
        Assert.True(result.Value.Validate().IsSuccess);
    }

    [Theory]
    [InlineData(0.4999, false)]
    [InlineData(0.5, true)]
    [InlineData(0.0, false)]
    [InlineData(1.0, true)]
    public async Task TheDefaultThresholdIsInclusive(double probability, bool success)
    {
        var result = await Evaluator(FakeDecisionClient.Answering(probability)).EvaluateAsync(Request(), CancellationToken.None);

        Assert.Equal(new ReasonletEvaluation(success, probability, "minos"), result.Value);
    }

    [Fact]
    public async Task ACustomThresholdApplies()
    {
        var options = new MinosEvaluatorOptions { SuccessThreshold = 0.9 };

        var below = await Evaluator(FakeDecisionClient.Answering(0.8), options).EvaluateAsync(Request(), CancellationToken.None);
        var at = await Evaluator(FakeDecisionClient.Answering(0.9), options).EvaluateAsync(Request(), CancellationToken.None);

        Assert.Equal(new ReasonletEvaluation(false, 0.8, "minos"), below.Value);
        Assert.Equal(new ReasonletEvaluation(true, 0.9, "minos"), at.Value);
    }

    [Fact]
    public async Task TheEvaluatorNameComesFromTheOptions()
    {
        var options = new MinosEvaluatorOptions { EvaluatorName = "minos-strict" };

        var result = await Evaluator(FakeDecisionClient.Answering(0.8), options).EvaluateAsync(Request(), CancellationToken.None);

        Assert.Equal("minos-strict", result.Value.Evaluator);
    }

    // ---- The state ----

    [Fact]
    public async Task TheStateHasExactlyTheFourFieldsInOrder()
    {
        var client = FakeDecisionClient.Answering(0.8);
        var request = Request();

        await Evaluator(client).EvaluateAsync(request, CancellationToken.None);

        var state = SentState(client);
        Assert.Equal(JsonValueKind.Object, state.ValueKind);
        Assert.Equal(
            ["question", "intent", "procedure", "answer"],
            state.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(request.Question, state.GetProperty("question").GetString());
        Assert.Equal(request.Intent, state.GetProperty("intent").GetString());
        Assert.Equal(request.Answer, state.GetProperty("answer").GetString());
    }

    [Fact]
    public async Task TheProcedureIsTheCanonicalGraphAsANestedObject()
    {
        var client = FakeDecisionClient.Answering(0.8);
        var request = Request();

        await Evaluator(client).EvaluateAsync(request, CancellationToken.None);

        var procedure = SentState(client).GetProperty("procedure");
        Assert.Equal(JsonValueKind.Object, procedure.ValueKind);
        using var canonical = JsonDocument.Parse(ReasonletGraphJson.Serialize(request.Graph));
        Assert.True(JsonElement.DeepEquals(canonical.RootElement, procedure), procedure.GetRawText());
    }

    [Fact]
    public async Task TheStateLeavesOutTheOutcomeAndTheName()
    {
        var client = FakeDecisionClient.Answering(0.8);

        await Evaluator(client).EvaluateAsync(Request(), CancellationToken.None);

        var raw = SentState(client).GetRawText();
        Assert.DoesNotContain(NameSentinel, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("0.123456789", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("86399", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("outcome", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("success", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheRequestAsksTheGeneratedQuestions()
    {
        var client = FakeDecisionClient.Answering(0.8);

        await Evaluator(client).EvaluateAsync(Request(), CancellationToken.None);

        var sent = Assert.Single(client.Requests);
        Assert.Same(MinosJudgeQuestions.Definition, sent.Definition);
    }

    // ---- Errors ----

    // The mapping the design fixes: the judge being unable to judge is Unavailable, a bug is Validation.
    private static readonly Dictionary<DecisionErrorKind, MnemosyneErrorCode> ExpectedCodes = new()
    {
        [DecisionErrorKind.Unauthorized] = MnemosyneErrorCode.Unavailable,
        [DecisionErrorKind.Validation] = MnemosyneErrorCode.Unavailable,
        [DecisionErrorKind.RateLimited] = MnemosyneErrorCode.Unavailable,
        [DecisionErrorKind.Overloaded] = MnemosyneErrorCode.Unavailable,
        [DecisionErrorKind.Server] = MnemosyneErrorCode.Unavailable,
        [DecisionErrorKind.Http] = MnemosyneErrorCode.Unavailable,
        [DecisionErrorKind.Network] = MnemosyneErrorCode.Unavailable,
        [DecisionErrorKind.Timeout] = MnemosyneErrorCode.Unavailable,
        [DecisionErrorKind.InvalidResponse] = MnemosyneErrorCode.Unavailable,
        [DecisionErrorKind.Unsupported] = MnemosyneErrorCode.Unavailable,
        [DecisionErrorKind.InvalidQuestions] = MnemosyneErrorCode.Validation,
        [DecisionErrorKind.Disposed] = MnemosyneErrorCode.Validation,
        [DecisionErrorKind.Refused] = MnemosyneErrorCode.Unavailable,
    };

    public static TheoryData<DecisionErrorKind> AllKinds => new(Enum.GetValues<DecisionErrorKind>());

    [Fact]
    public void TheExpectedMappingCoversEveryKind()
    {
        // A new kind in a later Minos fails here, so its mapping is a decision and not an accident.
        Assert.Equal(13, Enum.GetValues<DecisionErrorKind>().Length);
        Assert.Equal(Enum.GetValues<DecisionErrorKind>().Order(), ExpectedCodes.Keys.Order());
        Assert.Equal(2, ExpectedCodes.Values.Count(c => c == MnemosyneErrorCode.Validation));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public async Task EachKindMapsToItsCode(DecisionErrorKind kind)
    {
        var result = await Evaluator(FakeDecisionClient.Failing(kind)).EvaluateAsync(Request(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(new MnemosyneError(ExpectedCodes[kind], "The Minos judge could not judge the run.", $"minos: {kind}"), result.Error);
    }

    [Fact]
    public async Task AnUnknownKindIsUnavailable()
    {
        var result = await Evaluator(FakeDecisionClient.Failing((DecisionErrorKind)99)).EvaluateAsync(Request(), CancellationToken.None);

        Assert.Equal(new MnemosyneError(MnemosyneErrorCode.Unavailable, "The Minos judge could not judge the run.", "minos: 99"), result.Error);
    }

    [Fact]
    public async Task MinosMessageTextNeverEscapes()
    {
        foreach (var kind in Enum.GetValues<DecisionErrorKind>())
        {
            var client = FakeDecisionClient.Failing(kind, $"It quoted the request: {MessageSentinel}");

            var result = await Evaluator(client).EvaluateAsync(Request(), CancellationToken.None);

            Assert.DoesNotContain(MessageSentinel, result.Error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(MessageSentinel, result.Error.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(MessageSentinel, result.Error.ToString(), StringComparison.Ordinal);
        }
    }

    // ---- Propagation ----

    [Fact]
    public async Task AnExceptionFromTheClientPropagates()
    {
        var thrown = new HttpRequestException("down");

        var caught = await Assert.ThrowsAsync<HttpRequestException>(
            async () => await Evaluator(FakeDecisionClient.Throwing(thrown)).EvaluateAsync(Request(), CancellationToken.None));

        Assert.Same(thrown, caught);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await Evaluator(FakeDecisionClient.HonouringCancellation()).EvaluateAsync(Request(), cts.Token));
    }

    [Fact]
    public async Task TheTokenReachesTheClient()
    {
        using var cts = new CancellationTokenSource();
        var client = FakeDecisionClient.Answering(0.8);

        await Evaluator(client).EvaluateAsync(Request(), cts.Token);

        Assert.Equal(cts.Token, client.LastToken);
    }

    // ---- Arguments ----

    [Fact]
    public void ANullClientThrows() =>
        Assert.Equal("client", Assert.Throws<ArgumentNullException>(() => new MinosReasonletEvaluator(null!, new MinosEvaluatorOptions())).ParamName);

    [Fact]
    public void NullOptionsThrow() =>
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(() => new MinosReasonletEvaluator(FakeDecisionClient.Answering(0.8), null!)).ParamName);

    [Fact]
    public void InvalidOptionsThrow()
    {
        var thrown = Assert.Throws<ArgumentException>(
            () => new MinosReasonletEvaluator(FakeDecisionClient.Answering(0.8), new MinosEvaluatorOptions { SuccessThreshold = 2 }));

        Assert.Equal("options", thrown.ParamName);
        Assert.StartsWith("SuccessThreshold: must be a finite number between 0 and 1", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANullRequestThrows() =>
        Assert.Equal("request", (await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await Evaluator(FakeDecisionClient.Answering(0.8)).EvaluateAsync(null!, CancellationToken.None))).ParamName);

    private static JsonElement SentState(FakeDecisionClient client)
    {
        var sent = Assert.Single(client.Requests);
        Assert.True(sent.State.TryGetJson(out var state));
        return state;
    }
}
