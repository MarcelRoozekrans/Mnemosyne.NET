using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Tests.Abstractions;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Core;

public sealed class ReasonletMemoryTests
{
    private const string Secret = "secret question text";

    private readonly FakeStore _store = new();
    private readonly FakeSelector _selector = new();
    private readonly FakeEmbeddingGenerator _generator = new();

    private const string AnswerSentinel = "answer sentinel 7f3a";

    private ReasonletMemory Memory(IIntentExtractor? extractor = null, MnemosyneOptions? options = null, IReasonletEvaluator? evaluator = null) =>
        new(_store, _selector, _generator, extractor, evaluator, options ?? new MnemosyneOptions());

    private static ReasonletCandidate Candidate(double similarity = 0.9) =>
        new(TestData.Definition(), similarity, new ReasonletMetrics(0, 0, null, DateTimeOffset.UnixEpoch));

    private static MnemosyneError Unavailable() => new(MnemosyneErrorCode.Unavailable, "down", "detail");

    private static void AssertUnavailableWithoutMessage(MnemosyneError error, string type)
    {
        Assert.Equal(MnemosyneErrorCode.Unavailable, error.Code);
        Assert.Contains(type, error.Detail);
        Assert.DoesNotContain("secret", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", error.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static MnemosyneError SentinelError(MnemosyneErrorCode code) => new(code, $"extractor said: {Secret}", $"detail: {Secret}");

    private static void AssertRewrappedExtractorFailure(MnemosyneError error)
    {
        Assert.Equal(MnemosyneErrorCode.Unavailable, error.Code);
        Assert.Equal("The intent extractor failed.", error.Message);
        Assert.DoesNotContain("secret", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", error.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Construction ----

    [Fact]
    public void NullRequiredArgumentsAreRefused()
    {
        var options = new MnemosyneOptions();
        Assert.Throws<ArgumentNullException>("store", () => new ReasonletMemory(null!, _selector, _generator, null, null, options));
        Assert.Throws<ArgumentNullException>("selector", () => new ReasonletMemory(_store, null!, _generator, null, null, options));
        Assert.Throws<ArgumentNullException>("embeddings", () => new ReasonletMemory(_store, _selector, null!, null, null, options));
        Assert.Throws<ArgumentNullException>("options", () => new ReasonletMemory(_store, _selector, _generator, null, null, null!));
    }

    [Fact]
    public void InvalidOptionsAreRefused()
    {
        var error = Assert.Throws<ArgumentException>("options", () => Memory(options: new MnemosyneOptions { CandidatePool = 0 }));
        Assert.Contains("CandidatePool", error.Message);
    }

    // ---- Recall: intent resolution ----

    [Fact]
    public async Task TheCallersIntentWinsAndTheExtractorIsNotCalled()
    {
        var extractor = FakeIntentExtractor.Answering("extracted", 1.0);

        var result = await Memory(extractor).RecallAsync(TestData.Query() with { Intent = "caller" }, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("caller", _store.Searched!.Value.Intent);
        Assert.Empty(extractor.Questions);
    }

    [Fact]
    public async Task AnExtractorAnswerAtTheThresholdIsUsedAsTheFilter()
    {
        var extractor = FakeIntentExtractor.Answering("extracted", 0.7);

        await Memory(extractor).RecallAsync(TestData.Query(), CancellationToken.None);

        Assert.Equal("extracted", _store.Searched!.Value.Intent);
        Assert.Equal(["question"], extractor.Questions);
    }

    [Fact]
    public async Task AnExtractorAnswerBelowTheThresholdSearchesUnfiltered()
    {
        await Memory(FakeIntentExtractor.Answering("extracted", 0.69)).RecallAsync(TestData.Query(), CancellationToken.None);

        Assert.Null(_store.Searched!.Value.Intent);
    }

    [Fact]
    public async Task TheThresholdComesFromTheOptions()
    {
        var options = new MnemosyneOptions { MinIntentConfidence = 0.5 };

        await Memory(FakeIntentExtractor.Answering("extracted", 0.5), options).RecallAsync(TestData.Query(), CancellationToken.None);

        Assert.Equal("extracted", _store.Searched!.Value.Intent);
    }

    [Fact]
    public async Task WithoutAnExtractorRecallSearchesUnfiltered()
    {
        await Memory().RecallAsync(TestData.Query(), CancellationToken.None);

        Assert.Null(_store.Searched!.Value.Intent);
    }

    // ---- Recall: embedding, search and ranking ----

    [Fact]
    public async Task RecallEmbedsTheQuestionAndSearchesTheScope()
    {
        var query = TestData.Query() with { Scope = new ReasonletScope("tenant"), Question = "how?" };

        await Memory().RecallAsync(query, CancellationToken.None);

        Assert.Equal(["how?"], _generator.Inputs);
        Assert.Equal(new ReasonletScope("tenant"), _store.Searched!.Value.Scope);
        Assert.Equal(FakeEmbeddingGenerator.Vector, _store.Searched.Value.Embedding);
    }

    [Theory]
    [InlineData(3, 20, 20)]
    [InlineData(50, 20, 50)]
    [InlineData(3, 1, 3)]
    [InlineData(50, 200, 200)]
    public async Task TheSearchLimitIsTheCandidatePoolRaisedToTop(int top, int pool, int limit)
    {
        await Memory(options: new MnemosyneOptions { CandidatePool = pool }).RecallAsync(TestData.Query() with { Top = top }, CancellationToken.None);

        Assert.Equal(limit, _store.Searched!.Value.Limit);
    }

    [Fact]
    public async Task TheSelectorRanksTheCandidatesAndItsMatchesAreReturned()
    {
        var candidates = new[] { Candidate(0.9), Candidate(0.8) };
        _store.SearchResult = Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>.Success(candidates);
        var matches = new List<ReasonletMatch> { new(candidates[1].Reasonlet, new ReasonletScore(0.8, 1, 1, 0.9)) };
        _selector.Result = Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Success(matches);
        var query = TestData.Query();

        var result = await Memory().RecallAsync(query, CancellationToken.None);

        Assert.Same(matches, result.Value);
        Assert.Same(query, _selector.Query);
        Assert.Same(candidates, _selector.Candidates);
    }

    [Fact]
    public async Task AnInvalidQueryFailsBeforeAnyDependencyIsCalled()
    {
        var extractor = new FakeIntentExtractor();

        var result = await Memory(extractor).RecallAsync(TestData.Query() with { Top = 0 }, CancellationToken.None);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.StartsWith("Top:", result.Error.Message);
        Assert.Empty(extractor.Questions);
        Assert.Equal(0, _generator.Calls);
        Assert.Equal(0, _store.Calls);
        Assert.Equal(0, _selector.Calls);
    }

    [Fact]
    public async Task ANullQueryIsRefused() =>
        await Assert.ThrowsAsync<ArgumentNullException>("query", async () => await Memory().RecallAsync(null!, CancellationToken.None));

    [Fact]
    public async Task ACandidateTheStoreGotWrongThrowsAndNamesThePath()
    {
        _store.SearchResult = Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>.Success([Candidate(), Candidate(double.NaN)]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Memory().RecallAsync(TestData.Query(), CancellationToken.None));

        Assert.Contains("candidates[1].Similarity", error.Message);
        Assert.Equal(0, _selector.Calls);
    }

    [Fact]
    public async Task ANullCandidateThrowsAndNamesThePath()
    {
        _store.SearchResult = Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>.Success([null!]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Memory().RecallAsync(TestData.Query(), CancellationToken.None));

        Assert.Contains("candidates[0]: must not be null", error.Message);
    }

    [Fact]
    public async Task AStoreSearchErrorPassesThrough()
    {
        _store.SearchResult = Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>.Failure(Unavailable());

        var result = await Memory().RecallAsync(TestData.Query(), CancellationToken.None);

        Assert.Equal(Unavailable(), result.Error);
        Assert.Equal(0, _selector.Calls);
    }

    [Fact]
    public async Task ASelectorErrorPassesThrough()
    {
        _selector.Result = Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Failure(Unavailable());

        var result = await Memory().RecallAsync(TestData.Query(), CancellationToken.None);

        Assert.Equal(Unavailable(), result.Error);
    }

    // ---- Recall: failures of the generator and the extractor ----

    [Fact]
    public async Task AGeneratorExceptionAtRecallIsUnavailableWithoutItsMessage()
    {
        _generator.Respond = () => throw new HttpRequestException(Secret);

        var result = await Memory().RecallAsync(TestData.Query() with { Question = Secret }, CancellationToken.None);

        AssertUnavailableWithoutMessage(result.Error, nameof(HttpRequestException));
        Assert.Equal(0, _store.Calls);
    }

    public static TheoryData<string> ExtractorOutages() => ["exception", "unavailable", "timeout"];

    [Theory]
    [MemberData(nameof(ExtractorOutages))]
    public async Task AnExtractorOutageAtRecallSearchesUnfiltered(string outage)
    {
        var extractor = new FakeIntentExtractor
        {
            Respond = outage switch
            {
                "exception" => () => throw new HttpRequestException(Secret),
                "unavailable" => () => Result<ReasonletIntent, MnemosyneError>.Failure(SentinelError(MnemosyneErrorCode.Unavailable)),
                _ => () => throw new TaskCanceledException(Secret),
            },
        };
        var candidate = Candidate();
        _store.SearchResult = Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>.Success([candidate]);

        var result = await Memory(extractor).RecallAsync(TestData.Query() with { Question = Secret }, CancellationToken.None);

        Assert.Equal(candidate.Reasonlet, Assert.Single(result.Value).Reasonlet);
        Assert.Null(_store.Searched!.Value.Intent);
        Assert.Equal(1, _generator.Calls);
    }

    [Theory]
    [InlineData(MnemosyneErrorCode.Validation)]
    [InlineData(MnemosyneErrorCode.NotFound)]
    [InlineData(MnemosyneErrorCode.Conflict)]
    public async Task AnyOtherExtractorErrorAtRecallThrows(MnemosyneErrorCode code)
    {
        var extractor = new FakeIntentExtractor { Respond = () => Result<ReasonletIntent, MnemosyneError>.Failure(SentinelError(code)) };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Memory(extractor).RecallAsync(TestData.Query(), CancellationToken.None));

        Assert.Contains(code.ToString(), error.Message);
        Assert.DoesNotContain("secret", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _generator.Calls);
    }

    [Fact]
    public async Task AnInvalidExtractorAnswerAtRecallThrows()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Memory(FakeIntentExtractor.Answering("extracted", double.NaN)).RecallAsync(TestData.Query(), CancellationToken.None));

        Assert.Contains("Confidence", error.Message);
    }

    [Fact]
    public async Task GeneratorCancellationAtRecallPropagatesWhenTheTokenIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _generator.Respond = () => throw new OperationCanceledException(cts.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await Memory().RecallAsync(TestData.Query(), cts.Token));
    }

    [Fact]
    public async Task AGeneratorTimeoutAtRecallIsUnavailableWithoutItsMessage()
    {
        _generator.Respond = () => throw new TaskCanceledException(Secret);

        var result = await Memory().RecallAsync(TestData.Query() with { Question = Secret }, CancellationToken.None);

        AssertUnavailableWithoutMessage(result.Error, nameof(TaskCanceledException));
        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task ExtractorCancellationAtRecallPropagatesWhenTheTokenIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var extractor = new FakeIntentExtractor { Respond = () => throw new TaskCanceledException() };

        await Assert.ThrowsAsync<TaskCanceledException>(async () => await Memory(extractor).RecallAsync(TestData.Query(), cts.Token));
    }

    [Fact]
    public async Task NoEmbeddingIsUnavailable()
    {
        _generator.Respond = () => new GeneratedEmbeddings<Embedding<float>>();

        var result = await Memory().RecallAsync(TestData.Query(), CancellationToken.None);

        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task ANullEmbeddingListIsUnavailable()
    {
        _generator.Respond = () => null;

        var result = await Memory().RecallAsync(TestData.Query(), CancellationToken.None);

        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
    }

    // ---- Embeddings the store cannot use ----

    public static TheoryData<float[], string> UnusableEmbeddings() => new()
    {
        { [], "length" },
        { [0.6f, float.NaN, 0f], "non-finite" },
        { [0.6f, float.PositiveInfinity, 0f], "non-finite" },
        { [0f, 0f, 0f], "norm" },
        { [float.MaxValue, 0f, 0f], "norm" },
    };

    private static void AssertUnusableEmbedding(MnemosyneError error, string reason)
    {
        Assert.Equal(MnemosyneErrorCode.Unavailable, error.Code);
        Assert.Equal("The embedding generator returned an embedding the store cannot use.", error.Message);
        Assert.StartsWith(reason + ":", error.Detail);
        Assert.DoesNotContain("secret", error.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(UnusableEmbeddings))]
    public async Task AnUnusableEmbeddingAtRecallIsUnavailableBeforeTheStore(float[] vector, string reason)
    {
        _generator.Respond = () => new([new Embedding<float>(vector)]);

        var result = await Memory().RecallAsync(TestData.Query() with { Question = Secret }, CancellationToken.None);

        AssertUnusableEmbedding(result.Error, reason);
        Assert.Equal(0, _store.Calls);
    }

    [Theory]
    [MemberData(nameof(UnusableEmbeddings))]
    public async Task AnUnusableEmbeddingAtRememberIsUnavailableBeforeTheStore(float[] vector, string reason)
    {
        _generator.Respond = () => new([new Embedding<float>(vector)]);

        var result = await Memory().RememberAsync(TestData.Capture() with { Question = Secret }, CancellationToken.None);

        AssertUnusableEmbedding(result.Error, reason);
        Assert.Equal(0, _store.Calls);
    }

    public static TheoryData<float[]> WrongLengths() => new() { new[] { 0.6f, 0.8f }, new[] { 0.6f, 0.8f, 0f, 0f } };

    [Theory]
    [MemberData(nameof(WrongLengths))]
    public async Task AWrongLengthEmbeddingIsUnavailableAtRecallAndRememberWithoutCallingTheStore(float[] vector)
    {
        _generator.Respond = () => new([new Embedding<float>(vector)]);

        var recalled = await Memory().RecallAsync(TestData.Query() with { Question = Secret }, CancellationToken.None);
        var remembered = await Memory().RememberAsync(TestData.Capture() with { Question = Secret }, CancellationToken.None);

        var expected = $"length: the embedding has {vector.Length} values, but the store's dimension is 3";
        foreach (var error in new[] { recalled.Error, remembered.Error })
        {
            AssertUnusableEmbedding(error, "length");
            Assert.Equal(expected, error.Detail);
        }

        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task TheLengthCheckUsesTheStoresDimension()
    {
        _store.Dimensions = 2;
        _generator.Respond = () => new([new Embedding<float>(new[] { 0.6f, 0.8f })]);

        Assert.True((await Memory().RecallAsync(TestData.Query(), CancellationToken.None)).IsSuccess);
        Assert.Equal([0.6f, 0.8f], _store.Searched!.Value.Embedding);
    }

    [Theory]
    [InlineData(MnemosyneErrorCode.Validation, "embedding: must have 4 values, the store's dimension, but has 3")]
    [InlineData(MnemosyneErrorCode.Validation, "limit: must be from 1 to 200")]
    [InlineData(MnemosyneErrorCode.Unavailable, "embedding: down")]
    public async Task EveryStoreErrorPassesThrough(MnemosyneErrorCode code, string message)
    {
        var error = new MnemosyneError(code, message);
        _store.SearchResult = Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>.Failure(error);
        _store.RememberResult = Result<ReasonletReceipt, MnemosyneError>.Failure(error);

        Assert.Equal(error, (await Memory().RecallAsync(TestData.Query(), CancellationToken.None)).Error);
        Assert.Equal(error, (await Memory().RememberAsync(TestData.Capture(), CancellationToken.None)).Error);
    }

    [Fact]
    public async Task QueryValidationStillAnswersValidation()
    {
        _generator.Respond = () => new([new Embedding<float>(new[] { float.NaN })]);

        var result = await Memory().RecallAsync(TestData.Query() with { Top = 0 }, CancellationToken.None);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal(0, _generator.Calls);
    }

    // ---- Remember ----

    [Fact]
    public async Task RememberBuildsAnEntryWithTheResolvedValues()
    {
        var capture = TestData.Capture() with { Intent = "caller intent", Name = "caller name" };

        var result = await Memory().RememberAsync(capture, CancellationToken.None);

        Assert.Same(FakeStore.Receipt, result.Value);
        var entry = _store.Remembered!;
        Assert.True(entry.Validate().IsSuccess);
        Assert.Equal("caller intent", entry.Intent);
        Assert.Equal("caller name", entry.Name);
        Assert.Equal(capture, entry.Capture);
        Assert.Equal(FakeEmbeddingGenerator.Vector, entry.Embedding.ToArray());
        Assert.Equal([capture.Question], _generator.Inputs);
    }

    [Fact]
    public async Task TheCapturesIntentWinsAndTheExtractorIsNotCalled()
    {
        var extractor = FakeIntentExtractor.Answering("extracted", 1.0);

        await Memory(extractor).RememberAsync(TestData.Capture() with { Intent = "caller" }, CancellationToken.None);

        Assert.Equal("caller", _store.Remembered!.Intent);
        Assert.Empty(extractor.Questions);
    }

    [Fact]
    public async Task RememberUsesTheExtractorAtAnyConfidence()
    {
        await Memory(FakeIntentExtractor.Answering("extracted", 0.1)).RememberAsync(TestData.Capture(), CancellationToken.None);

        var entry = _store.Remembered!;
        Assert.Equal("extracted", entry.Intent);
        Assert.Equal("extracted", entry.Capture.Intent);
        Assert.True(entry.Validate().IsSuccess);
    }

    [Fact]
    public async Task WithoutAnExtractorTheIntentIsUnspecified()
    {
        await Memory().RememberAsync(TestData.Capture(), CancellationToken.None);

        Assert.Equal("unspecified", _store.Remembered!.Intent);
        Assert.Equal("unspecified", _store.Remembered.Capture.Intent);
    }

    [Fact]
    public async Task TheNameFallsBackToTheFirstLine()
    {
        await Memory().RememberAsync(TestData.Capture() with { Question = "line one\nline two" }, CancellationToken.None);

        Assert.Equal("line one", _store.Remembered!.Name);
        Assert.Equal("line one", _store.Remembered.Capture.Name);
        Assert.True(_store.Remembered.Validate().IsSuccess);
    }

    [Fact]
    public async Task AQuestionStartingWithANewlineUsesTheFirstNonEmptyLine()
    {
        await Memory().RememberAsync(TestData.Capture() with { Question = "\n \r\n  real line \r\nmore" }, CancellationToken.None);

        Assert.Equal("real line", _store.Remembered!.Name);
    }

    [Fact]
    public async Task ALongFirstLineIsCutTo200CodePointsAtACodePoint()
    {
        // 199 letters, then a surrogate pair that straddles UTF-16 index 200, then more text.
        var question = new string('a', 199) + "\U0001F600" + "xyz";

        await Memory().RememberAsync(TestData.Capture() with { Question = question }, CancellationToken.None);

        Assert.Equal(new string('a', 199) + "\U0001F600", _store.Remembered!.Name);
    }

    [Fact]
    public void ALineOf200CodePointsIsKeptWhole()
    {
        var line = new string('a', 200);

        Assert.Equal(line, ReasonletMemory.DefaultName(line));
        Assert.Equal(new string('a', 200), ReasonletMemory.DefaultName(line + "b"));
    }

    [Fact]
    public void ACutThatEndsInWhitespaceIsTrimmed() =>
        Assert.Equal(new string('a', 199), ReasonletMemory.DefaultName(new string('a', 199) + " b"));

    [Fact]
    public void ALoneSurrogateCountsAsOneCodePoint()
    {
        var name = ReasonletMemory.DefaultName(new string('a', 199) + "\uD800" + "b");

        Assert.Equal(new string('a', 199) + "\uD800", name);
    }

    [Fact]
    public async Task AnInvalidCaptureFailsBeforeAnyDependencyIsCalled()
    {
        var extractor = new FakeIntentExtractor();

        var result = await Memory(extractor).RememberAsync(TestData.Capture() with { Question = " " }, CancellationToken.None);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.StartsWith("Question:", result.Error.Message);
        Assert.Empty(extractor.Questions);
        Assert.Equal(0, _generator.Calls);
        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task ANullCaptureIsRefused() =>
        await Assert.ThrowsAsync<ArgumentNullException>("capture", async () => await Memory().RememberAsync(null!, CancellationToken.None));

    [Fact]
    public async Task AStoreRememberErrorPassesThrough()
    {
        _store.RememberResult = Result<ReasonletReceipt, MnemosyneError>.Failure(Unavailable());

        var result = await Memory().RememberAsync(TestData.Capture(), CancellationToken.None);

        Assert.Equal(Unavailable(), result.Error);
    }

    [Fact]
    public async Task AGeneratorExceptionAtRememberIsUnavailableWithoutItsMessage()
    {
        _generator.Respond = () => throw new HttpRequestException(Secret);

        var result = await Memory().RememberAsync(TestData.Capture() with { Question = Secret }, CancellationToken.None);

        AssertUnavailableWithoutMessage(result.Error, nameof(HttpRequestException));
        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task AnExtractorExceptionAtRememberIsUnavailableWithoutItsMessage()
    {
        var extractor = new FakeIntentExtractor { Respond = () => throw new HttpRequestException(Secret) };

        var result = await Memory(extractor).RememberAsync(TestData.Capture() with { Question = Secret }, CancellationToken.None);

        AssertUnavailableWithoutMessage(result.Error, nameof(HttpRequestException));
        Assert.Equal(0, _generator.Calls);
    }

    [Fact]
    public async Task AnUnavailableExtractorAtRememberIsRewrappedWithoutItsText()
    {
        var extractor = new FakeIntentExtractor { Respond = () => Result<ReasonletIntent, MnemosyneError>.Failure(SentinelError(MnemosyneErrorCode.Unavailable)) };

        var result = await Memory(extractor).RememberAsync(TestData.Capture(), CancellationToken.None);

        AssertRewrappedExtractorFailure(result.Error);
        Assert.Equal(0, _store.Calls);
    }

    [Theory]
    [InlineData(MnemosyneErrorCode.Validation)]
    [InlineData(MnemosyneErrorCode.NotFound)]
    [InlineData(MnemosyneErrorCode.Conflict)]
    public async Task AnyOtherExtractorErrorAtRememberThrows(MnemosyneErrorCode code)
    {
        var extractor = new FakeIntentExtractor { Respond = () => Result<ReasonletIntent, MnemosyneError>.Failure(SentinelError(code)) };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Memory(extractor).RememberAsync(TestData.Capture(), CancellationToken.None));

        Assert.Contains(code.ToString(), error.Message);
        Assert.DoesNotContain("secret", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task AnInvalidExtractorAnswerAtRememberThrows()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Memory(FakeIntentExtractor.Answering(" ", 0.9)).RememberAsync(TestData.Capture(), CancellationToken.None));

        Assert.Contains("Intent", error.Message);
    }

    [Fact]
    public async Task GeneratorCancellationAtRememberPropagatesWhenTheTokenIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _generator.Respond = () => throw new OperationCanceledException(cts.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await Memory().RememberAsync(TestData.Capture(), cts.Token));
    }

    [Fact]
    public async Task AGeneratorTimeoutAtRememberIsUnavailableWithoutItsMessage()
    {
        _generator.Respond = () => throw new TaskCanceledException(Secret);

        var result = await Memory().RememberAsync(TestData.Capture() with { Question = Secret }, CancellationToken.None);

        AssertUnavailableWithoutMessage(result.Error, nameof(TaskCanceledException));
        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task ExtractorCancellationAtRememberPropagatesWhenTheTokenIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var extractor = new FakeIntentExtractor { Respond = () => throw new OperationCanceledException(cts.Token) };

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await Memory(extractor).RememberAsync(TestData.Capture(), cts.Token));
    }

    [Fact]
    public async Task AnExtractorTimeoutAtRememberIsUnavailableWithoutItsMessage()
    {
        var extractor = new FakeIntentExtractor { Respond = () => throw new TaskCanceledException(Secret) };

        var result = await Memory(extractor).RememberAsync(TestData.Capture() with { Question = Secret }, CancellationToken.None);

        AssertUnavailableWithoutMessage(result.Error, nameof(TaskCanceledException));
        Assert.Equal(0, _generator.Calls);
    }

    [Fact]
    public async Task NoEmbeddingAtRememberIsUnavailable()
    {
        _generator.Respond = () => new GeneratedEmbeddings<Embedding<float>>();

        var result = await Memory().RememberAsync(TestData.Capture(), CancellationToken.None);

        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
        Assert.Equal(0, _store.Calls);
    }

    // ---- Delegation ----

    private static ReasonletRun Run() => new()
    {
        Scope = new ReasonletScope("tenant"),
        Id = ReasonletId.New(),
        Outcome = new ReasonletOutcome(false, TimeSpan.FromSeconds(2), 0.25),
        Provenance = TestData.Provenance(),
    };

    [Fact]
    public async Task RecordExecutionReachesTheStoreWithTheSameArguments()
    {
        using var cts = new CancellationTokenSource();
        var run = Run();

        var result = await Memory().RecordExecutionAsync(run, cts.Token);

        Assert.Equal(_store.ExecutionResult.Value, result.Value);
        Assert.Equal((run.Scope, run.Id, run.Outcome, run.Provenance, (ReasonletEvaluation?)null, cts.Token), _store.Executed);
    }

    [Fact]
    public async Task ARunWithAnAnswerButNoEvaluatorReachesTheStoreUnjudged()
    {
        var run = Run() with { Question = "question", Answer = Secret };

        var result = await Memory().RecordExecutionAsync(run, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(_store.Executed!.Value.Evaluation);
        Assert.Equal(run.Outcome, _store.Executed!.Value.Outcome);
        Assert.Null(_store.Got);
    }

    [Fact]
    public async Task AnInvalidExecutionIsRefusedWithoutCallingTheStore()
    {
        var result = await Memory().RecordExecutionAsync(Run() with { Id = default }, CancellationToken.None);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.StartsWith("Id:", result.Error.Message);
        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task AQuestionWithoutAnAnswerIsRefusedWithoutCallingTheStore()
    {
        var result = await Memory().RecordExecutionAsync(Run() with { Question = "question" }, CancellationToken.None);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.StartsWith("Answer:", result.Error.Message);
        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task ANullRunIsRefused() =>
        await Assert.ThrowsAsync<ArgumentNullException>("run", async () =>
            await Memory().RecordExecutionAsync(null!, CancellationToken.None));

    [Fact]
    public async Task ANullProvenanceIsRefusedAsAValidationError()
    {
        var result = await Memory().RecordExecutionAsync(Run() with { Provenance = null! }, CancellationToken.None);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.StartsWith("Provenance:", result.Error.Message);
        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task RememberStripsTheAnswerBeforeTheStoreSeesIt()
    {
        var capture = TestData.Capture() with { Answer = Secret };

        var result = await Memory().RememberAsync(capture, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(_store.Remembered!.Capture.Answer);
        Assert.Null(_store.Remembered!.Evaluation);
        Assert.Equal(capture.Question, _store.Remembered!.Capture.Question);
    }

    [Fact]
    public async Task RecordFeedbackReachesTheStoreWithTheSameArguments()
    {
        using var cts = new CancellationTokenSource();
        var scope = new ReasonletScope("tenant");
        var execution = ExecutionId.New();
        var feedback = new ReasonletFeedback(0.1, 0.2, 0.3);
        _store.FeedbackResult = UnitResult<MnemosyneError>.Failure(Unavailable());

        var result = await Memory().RecordFeedbackAsync(scope, execution, feedback, cts.Token);

        Assert.Equal(Unavailable(), result.Error);
        Assert.Equal((scope, execution, feedback, cts.Token), _store.Fed);
    }

    [Fact]
    public async Task InvalidFeedbackIsRefusedWithoutCallingTheStore()
    {
        var result = await Memory().RecordFeedbackAsync(new ReasonletScope("s"), ExecutionId.New(), new ReasonletFeedback(2, 0, 0), CancellationToken.None);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.StartsWith("feedback.Utility:", result.Error.Message);
        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task EraseReachesTheStoreWithTheSameArguments()
    {
        using var cts = new CancellationTokenSource();
        var erasure = new ReasonletErasure(new ReasonletScope("tenant"), "subject");

        var result = await Memory().EraseAsync(erasure, cts.Token);

        Assert.Same(_store.EraseResult.Value, result.Value);
        Assert.Equal((erasure, cts.Token), _store.Erased);
    }

    [Fact]
    public async Task AnInvalidErasureIsRefusedWithoutCallingTheStore()
    {
        var result = await Memory().EraseAsync(new ReasonletErasure(new ReasonletScope(" "), null), CancellationToken.None);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.StartsWith("Scope.Value:", result.Error.Message);
        Assert.Equal(0, _store.Calls);
    }

    [Fact]
    public async Task ANullErasureIsRefused() =>
        await Assert.ThrowsAsync<ArgumentNullException>("erasure", async () => await Memory().EraseAsync(null!, CancellationToken.None));

    // ---- Judging ----

    private static MnemosyneError JudgeError(MnemosyneErrorCode code) => new(code, $"judge said: {AnswerSentinel}", $"detail: {AnswerSentinel}");

    public static TheoryData<string> Paths() => ["record", "remember"];

    /// <summary>Records a run, or remembers a capture, with the answer sentinel, and returns whether the call succeeded.</summary>
    private async Task<bool> JudgeThroughAsync(string path, IReasonletEvaluator evaluator, CancellationToken ct)
    {
        var memory = Memory(evaluator: evaluator);
        return path == "record"
            ? (await memory.RecordExecutionAsync(Run() with { Question = "question", Answer = AnswerSentinel }, ct)).IsSuccess
            : (await memory.RememberAsync(TestData.Capture() with { Answer = AnswerSentinel }, ct)).IsSuccess;
    }

    private bool Stored(string path) => path == "record" ? _store.Executed is not null : _store.Remembered is not null;

    private ReasonletEvaluation? StoredEvaluation(string path) => path == "record" ? _store.Executed!.Value.Evaluation : _store.Remembered!.Evaluation;

    [Fact]
    public async Task AJudgedRunReachesTheStoreWithTheEvaluation()
    {
        using var cts = new CancellationTokenSource();
        var evaluator = new FakeEvaluator();
        var definition = TestData.Definition();
        _store.GetResult = definition;
        var run = Run() with { Question = "how?", Answer = AnswerSentinel };

        var result = await Memory(evaluator: evaluator).RecordExecutionAsync(run, cts.Token);

        Assert.Equal(_store.ExecutionResult.Value, result.Value);
        Assert.Equal((run.Scope, run.Id, cts.Token), _store.Got);
        var request = Assert.Single(evaluator.Requests);
        Assert.Same(definition.Graph, request.Graph);
        Assert.Equal(definition.Name, request.Name);
        Assert.Equal(definition.Intent, request.Intent);
        Assert.Equal("how?", request.Question);
        Assert.Equal(AnswerSentinel, request.Answer);
        Assert.Equal(run.Outcome, request.Outcome);
        Assert.Equal((run.Scope, run.Id, run.Outcome, run.Provenance, (ReasonletEvaluation?)FakeEvaluator.Verdict, cts.Token), _store.Executed);
    }

    [Fact]
    public async Task ARunWithoutAnAnswerIsNotJudged()
    {
        var evaluator = new FakeEvaluator();

        var result = await Memory(evaluator: evaluator).RecordExecutionAsync(Run(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(evaluator.Requests);
        Assert.Null(_store.Got);
        Assert.Null(_store.Executed!.Value.Evaluation);
    }

    [Theory]
    [InlineData(MnemosyneErrorCode.NotFound)]
    [InlineData(MnemosyneErrorCode.Unavailable)]
    public async Task AStoreReadErrorIsReturnedBeforeTheJudgeIsCalled(MnemosyneErrorCode code)
    {
        var evaluator = new FakeEvaluator();
        var error = new MnemosyneError(code, "store said no");
        _store.GetResult = error;

        var result = await Memory(evaluator: evaluator).RecordExecutionAsync(Run() with { Question = "question", Answer = AnswerSentinel }, CancellationToken.None);

        Assert.Equal(error, result.Error);
        Assert.Empty(evaluator.Requests);
        Assert.Null(_store.Executed);
    }

    [Fact]
    public async Task AnInvalidReasonletFromTheStoreThrowsBeforeTheJudgeIsCalled()
    {
        var evaluator = new FakeEvaluator();
        _store.GetResult = TestData.Definition() with { Name = " " };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Memory(evaluator: evaluator).RecordExecutionAsync(Run() with { Question = "question", Answer = AnswerSentinel }, CancellationToken.None));

        Assert.Equal("The store returned an invalid Reasonlet: reasonlet.Name: must not be blank", error.Message);
        Assert.Empty(evaluator.Requests);
        Assert.Null(_store.Executed);
    }

    [Fact]
    public async Task RememberJudgesAfterResolvingTheIntentAndNameAndBeforeEmbedding()
    {
        var extractor = FakeIntentExtractor.Answering("extracted", 0.1);
        int? embeddingsAtJudging = null;
        var evaluator = new FakeEvaluator();
        evaluator.Respond = () =>
        {
            embeddingsAtJudging = _generator.Calls;
            return FakeEvaluator.Verdict;
        };
        var capture = TestData.Capture() with { Question = "first line\nsecond line", Answer = AnswerSentinel };

        var result = await Memory(extractor, evaluator: evaluator).RememberAsync(capture, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, embeddingsAtJudging);
        Assert.Equal(1, _generator.Calls);
        var request = Assert.Single(evaluator.Requests);
        Assert.Same(capture.Graph, request.Graph);
        Assert.Equal("first line", request.Name);
        Assert.Equal("extracted", request.Intent);
        Assert.Equal(capture.Question, request.Question);
        Assert.Equal(AnswerSentinel, request.Answer);
        Assert.Equal(capture.Outcome, request.Outcome);
        Assert.Equal(FakeEvaluator.Verdict, _store.Remembered!.Evaluation);
        Assert.Null(_store.Remembered!.Capture.Answer);
    }

    [Fact]
    public async Task RememberWithoutAnAnswerIsNotJudged()
    {
        var evaluator = new FakeEvaluator();

        var result = await Memory(evaluator: evaluator).RememberAsync(TestData.Capture(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(evaluator.Requests);
        Assert.Null(_store.Remembered!.Evaluation);
    }

    [Fact]
    public async Task RememberThatFailsBeforeJudgingNeverCallsTheJudge()
    {
        var evaluator = new FakeEvaluator();
        var extractor = new FakeIntentExtractor { Respond = () => Result<ReasonletIntent, MnemosyneError>.Failure(Unavailable()) };

        var result = await Memory(extractor, evaluator: evaluator).RememberAsync(TestData.Capture() with { Answer = AnswerSentinel }, CancellationToken.None);

        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
        Assert.Empty(evaluator.Requests);
    }

    public static TheoryData<string, string> JudgeOutages() => new()
    {
        { "record", "unavailable" },
        { "record", "timeout" },
        { "record", "exception" },
        { "remember", "unavailable" },
        { "remember", "timeout" },
        { "remember", "exception" },
    };

    [Theory]
    [MemberData(nameof(JudgeOutages))]
    public async Task AJudgeOutageStoresTheRunUnjudged(string path, string outage)
    {
        var evaluator = new FakeEvaluator
        {
            Respond = outage switch
            {
                "unavailable" => () => Result<ReasonletEvaluation, MnemosyneError>.Failure(JudgeError(MnemosyneErrorCode.Unavailable)),
                "timeout" => () => throw new TaskCanceledException(AnswerSentinel),
                _ => () => throw new HttpRequestException(AnswerSentinel),
            },
        };

        var succeeded = await JudgeThroughAsync(path, evaluator, CancellationToken.None);

        Assert.True(succeeded);
        Assert.Single(evaluator.Requests);
        Assert.Null(StoredEvaluation(path));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task JudgeCancellationPropagatesWhenTheTokenIsCancelled(string path)
    {
        using var cts = new CancellationTokenSource();
        var evaluator = new FakeEvaluator();
        evaluator.Respond = () =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await JudgeThroughAsync(path, evaluator, cts.Token));

        Assert.False(Stored(path));
    }

    public static TheoryData<string, MnemosyneErrorCode> OtherJudgeErrors() => new()
    {
        { "record", MnemosyneErrorCode.Validation },
        { "record", MnemosyneErrorCode.NotFound },
        { "record", MnemosyneErrorCode.Conflict },
        { "remember", MnemosyneErrorCode.Validation },
        { "remember", MnemosyneErrorCode.NotFound },
        { "remember", MnemosyneErrorCode.Conflict },
    };

    [Theory]
    [MemberData(nameof(OtherJudgeErrors))]
    public async Task AnyOtherJudgeErrorThrowsNamingOnlyTheCode(string path, MnemosyneErrorCode code)
    {
        var evaluator = new FakeEvaluator { Respond = () => Result<ReasonletEvaluation, MnemosyneError>.Failure(JudgeError(code)) };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await JudgeThroughAsync(path, evaluator, CancellationToken.None));

        Assert.Equal(
            $"The evaluator failed with {code}; an evaluator given a valid request may only fail with Unavailable.",
            error.Message);
        Assert.False(Stored(path));
    }

    public static TheoryData<string, ReasonletEvaluation, string> InvalidEvaluations() => new()
    {
        { "record", new ReasonletEvaluation(true, 1.5, "judge"), "Score: must be a finite number between 0 and 1" },
        { "record", new ReasonletEvaluation(true, double.NaN, "judge"), "Score: must be a finite number between 0 and 1" },
        { "record", default, "Evaluator: must not be blank" },
        { "remember", new ReasonletEvaluation(false, 0.5, " "), "Evaluator: must not be blank" },
        { "remember", new ReasonletEvaluation(false, 0.5, new string('j', 101)), "Evaluator: must not be longer than 100 code points" },
    };

    [Theory]
    [MemberData(nameof(InvalidEvaluations))]
    public async Task AnInvalidEvaluationThrowsNamingOnlyThePath(string path, ReasonletEvaluation evaluation, string reason)
    {
        var evaluator = new FakeEvaluator { Respond = () => evaluation };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await JudgeThroughAsync(path, evaluator, CancellationToken.None));

        Assert.Equal($"The evaluator returned an invalid evaluation: {reason}", error.Message);
        Assert.False(Stored(path));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task TheCallersTokenReachesTheJudge(string path)
    {
        using var cts = new CancellationTokenSource();
        var evaluator = new FakeEvaluator();

        var succeeded = await JudgeThroughAsync(path, evaluator, cts.Token);

        Assert.True(succeeded);
        Assert.Equal(cts.Token, Assert.Single(evaluator.Tokens));
    }

    /// <summary>Every text a store argument carries, field by field, so a leak nested in a graph or a provenance shows.</summary>
    private static IEnumerable<string?> Texts(ReasonletScope scope, ReasonletProvenance provenance, ReasonletEvaluation? evaluation)
    {
        yield return scope.Value;
        yield return provenance.Source;
        yield return provenance.RunId;
        yield return provenance.SubjectId;
        foreach (var (key, value) in provenance.Tags ?? new Dictionary<string, string>())
        {
            yield return key;
            yield return value;
        }

        yield return evaluation?.Evaluator;
    }

    private static IEnumerable<string?> Texts(ReasonletStoreEntry entry)
    {
        var capture = entry.Capture;
        foreach (var text in Texts(capture.Scope, capture.Provenance, entry.Evaluation))
        {
            yield return text;
        }

        yield return capture.Question;
        yield return capture.Name;
        yield return capture.Intent;
        yield return capture.Answer;
        yield return entry.Name;
        yield return entry.Intent;
        foreach (var node in capture.Graph.Nodes)
        {
            yield return node.Id;
            yield return node.Type;
            yield return node.Name;
            yield return node.Tool;
        }

        foreach (var edge in capture.Graph.Edges)
        {
            yield return edge.From;
            yield return edge.To;
            yield return edge.Condition;
        }

        // The canonical JSON covers the graph's properties, which the fields above do not.
        yield return ReasonletGraphJson.Serialize(capture.Graph);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task TheAnswerNeverReachesAnyStoreArgument(string path)
    {
        var evaluator = new FakeEvaluator();

        var succeeded = await JudgeThroughAsync(path, evaluator, CancellationToken.None);

        Assert.True(succeeded);
        Assert.Equal(AnswerSentinel, Assert.Single(evaluator.Requests).Answer);
        IEnumerable<string?> texts;
        if (path == "record")
        {
            var (scope, _, _, provenance, evaluation, _) = _store.Executed!.Value;
            Assert.Equal(FakeEvaluator.Verdict, evaluation);
            texts = Texts(scope, provenance, evaluation).Append(_store.Got!.Value.Scope.Value);
        }
        else
        {
            Assert.Null(_store.Remembered!.Capture.Answer);
            Assert.Equal(FakeEvaluator.Verdict, _store.Remembered!.Evaluation);
            texts = Texts(_store.Remembered!);
        }

        Assert.All(texts, text => Assert.DoesNotContain(AnswerSentinel, text ?? string.Empty, StringComparison.Ordinal));
    }
}
