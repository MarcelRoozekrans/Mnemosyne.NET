using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql;
using Mnemosyne.NET.PostgreSql.Tests;
using Mnemosyne.NET.Tests.Abstractions;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Core;

/// <summary>The non-parallel collection the recall telemetry tests run in.</summary>
[CollectionDefinition(nameof(RecallTelemetryCollection), DisableParallelization = true)]
public sealed class RecallTelemetryCollection;

/// <summary>The signals <see cref="ReasonletMemory.RecallAsync"/> emits.</summary>
/// <remarks>
/// Listeners are global. Each test filters what it hears to its own trace through <see cref="TelemetryCapture"/>, so a
/// test running at the same time cannot add to it. The class also runs in a non-parallel collection, which xunit runs
/// alone after the parallel ones, so that the tests without a listener really run with none attached.
/// </remarks>
[Collection(nameof(RecallTelemetryCollection))]
public sealed class RecallTelemetryTests
{
    private const string Recall = "mnemosyne.recall";

    private readonly FakeStore _store = new();
    private readonly FakeSelector _selector = new();
    private readonly FakeEmbeddingGenerator _generator = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ReasonletMemory Memory(IIntentExtractor? extractor = null, IReasonletStore? store = null, IReasonletEvaluator? evaluator = null) =>
        new(store ?? _store, _selector, _generator, extractor, evaluator, new MnemosyneOptions());

    private static ReasonletCandidate Candidate(double similarity = 0.9) =>
        new(TestData.Definition(), similarity, new ReasonletMetrics(0, 0, null, DateTimeOffset.UnixEpoch));

    [Fact]
    public void TheSourceAndTheMeterUseThePublicNames()
    {
        Assert.Equal(MnemosyneTelemetry.ActivitySourceName, RecallTelemetry.Source.Name);
        Assert.Equal(MnemosyneTelemetry.MeterName, RecallTelemetry.Meter.Name);
    }

    [Fact]
    public void TheSharedInstrumentsAreDescribedTheSameInBothAssemblies()
    {
        // Both assemblies create these instruments under one meter name, so they aggregate as one and must match.
        Assert.NotNull(RecallTelemetry.Meter);
        Assert.NotNull(StoreTelemetry.Meter);
        var published = new List<Instrument>();
        using (var listener = new MeterListener())
        {
            listener.InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == MnemosyneTelemetry.MeterName)
                {
                    published.Add(instrument);
                }
            };
            listener.Start();
        }

        AssertShared(published, "mnemosyne.operations", "{operation}", "Mnemosyne operations, by operation and outcome.");
        AssertShared(published, "mnemosyne.operation.duration", "s", "How long Mnemosyne operations take, by operation.");
    }

    [Fact]
    public async Task ASuccessfulRecallTagsTheChosenMatch()
    {
        var candidates = new[] { Candidate(0.9), Candidate(0.8) };
        _store.SearchResult = Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>.Success(candidates);
        var chosen = candidates[1].Reasonlet with { Version = "2.1.0" };
        _selector.Result = Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Success(
            [new(chosen, new ReasonletScore(0.8, 1, 1, 0.77)), new(candidates[0].Reasonlet, new ReasonletScore(0.9, 0, 0, 0.45))]);
        using var capture = new TelemetryCapture();

        var result = await Memory().RecallAsync(TestData.Query() with { Intent = "caller", Top = 4 }, Ct);

        Assert.Equal(2, result.Value.Count);
        var activity = Single(capture);
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        Assert.Equal(
            [
                new("mnemosyne.recall.candidates", 2),
                new("mnemosyne.recall.chosen.id", chosen.Id.Value.ToString()),
                new("mnemosyne.recall.chosen.score", 0.77),
                new("mnemosyne.recall.chosen.version", "2.1.0"),
                new("mnemosyne.recall.intent_filtered", true),
                new("mnemosyne.recall.returned", 2),
                new("mnemosyne.recall.top", 4),
            ],
            AllTags(activity));
        Assert.Empty(activity.Events);
        AssertOperation(capture, "ok");
        var returned = Assert.Single(capture.Of("mnemosyne.recall.returned"));
        Assert.Equal(2, returned.Value);
        Assert.Empty(returned.Tags);
        Assert.Empty(capture.Of("mnemosyne.recall.empty"));
    }

    [Fact]
    public async Task AnEmptyRecallHasNoChosenTagsAndIsCounted()
    {
        using var capture = new TelemetryCapture();

        var result = await Memory().RecallAsync(TestData.Query(), Ct);

        Assert.Empty(result.Value);
        var activity = Single(capture);
        Assert.Equal(
            [
                new("mnemosyne.recall.candidates", 0),
                new("mnemosyne.recall.intent_filtered", false),
                new("mnemosyne.recall.returned", 0),
                new("mnemosyne.recall.top", 3),
            ],
            AllTags(activity));
        AssertOperation(capture, "ok");
        Assert.Equal(0, Assert.Single(capture.Of("mnemosyne.recall.returned")).Value);
        var empty = Assert.Single(capture.Of("mnemosyne.recall.empty"));
        Assert.Equal(1, empty.Value);
        Assert.Empty(empty.Tags);
    }

    [Theory]
    [InlineData(0.9, true)]
    [InlineData(0.1, false)]
    public async Task IntentFilteredFollowsTheResolvedFilter(double confidence, bool filtered)
    {
        using var capture = new TelemetryCapture();

        await Memory(FakeIntentExtractor.Answering("extracted", confidence)).RecallAsync(TestData.Query(), Ct);

        Assert.Equal(filtered, Single(capture).GetTagItem("mnemosyne.recall.intent_filtered"));
    }

    [Fact]
    public async Task AnInvalidQueryIsARecallWithOutcomeValidation()
    {
        using var capture = new TelemetryCapture();

        var result = await Memory().RecallAsync(TestData.Query() with { Top = 0, Intent = "caller" }, Ct);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        var activity = Single(capture);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(
            [new("error.type", "Validation"), new("mnemosyne.recall.intent_filtered", true), new("mnemosyne.recall.top", 0)],
            AllTags(activity));
        AssertOperation(capture, "Validation");
        Assert.Empty(capture.Of("mnemosyne.recall.returned"));
        Assert.Empty(capture.Of("mnemosyne.recall.empty"));
    }

    [Fact]
    public async Task AStoreValidationFailureIsOutcomeValidation()
    {
        _store.SearchResult = Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>.Failure(new(MnemosyneErrorCode.Validation, "bad"));
        using var capture = new TelemetryCapture();

        var result = await Memory().RecallAsync(TestData.Query(), Ct);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        var activity = Single(capture);
        Assert.Equal("Validation", activity.GetTagItem("error.type"));
        Assert.Null(activity.GetTagItem("mnemosyne.recall.candidates"));
        AssertOperation(capture, "Validation");
    }

    [Fact]
    public async Task ASelectorFailureKeepsTheCandidateCount()
    {
        _store.SearchResult = Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>.Success([Candidate()]);
        _selector.Result = Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Failure(new(MnemosyneErrorCode.Conflict, "x"));
        using var capture = new TelemetryCapture();

        await Memory().RecallAsync(TestData.Query(), Ct);

        var activity = Single(capture);
        Assert.Equal(1, activity.GetTagItem("mnemosyne.recall.candidates"));
        Assert.Null(activity.GetTagItem("mnemosyne.recall.returned"));
        AssertOperation(capture, "Conflict");
    }

    [Fact]
    public async Task AGeneratorExceptionIsOutcomeUnavailable()
    {
        _generator.Respond = () => throw new InvalidCastException("boom");
        using var capture = new TelemetryCapture();

        var result = await Memory().RecallAsync(TestData.Query(), Ct);

        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
        var activity = Single(capture);
        Assert.Equal("Unavailable", activity.GetTagItem("error.type"));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Empty(activity.Events);
        AssertOperation(capture, "Unavailable");
    }

    [Fact]
    public async Task AGeneratorTimeoutIsOutcomeUnavailable()
    {
        // A cancellation exception while the caller's token is not cancelled is a dependency timeout, not the caller's.
        _generator.Respond = () => throw new TaskCanceledException("timed out");
        using var capture = new TelemetryCapture();

        var result = await Memory().RecallAsync(TestData.Query(), Ct);

        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
        var activity = Single(capture);
        Assert.Equal("Unavailable", activity.GetTagItem("error.type"));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Empty(activity.Events);
        AssertOperation(capture, "Unavailable");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnExtractorOutageFallsBackAndIsTagged(bool throws)
    {
        var extractor = new FakeIntentExtractor
        {
            Respond = throws
                ? () => throw new HttpRequestException("down")
                : () => Result<ReasonletIntent, MnemosyneError>.Failure(new(MnemosyneErrorCode.Unavailable, "down")),
        };
        _store.SearchResult = Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>.Success([Candidate()]);
        using var capture = new TelemetryCapture();

        var result = await Memory(extractor).RecallAsync(TestData.Query(), Ct);

        Assert.Single(result.Value);
        var activity = Single(capture);
        Assert.Equal(false, activity.GetTagItem("mnemosyne.recall.intent_filtered"));
        Assert.Equal(true, activity.GetTagItem("mnemosyne.recall.intent_fallback"));
        Assert.Null(activity.GetTagItem("error.type"));
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        Assert.Empty(activity.Events);
        AssertOperation(capture, "ok");
    }

    [Fact]
    public async Task ARecallThatResolvedItsIntentHasNoFallbackTag()
    {
        using var capture = new TelemetryCapture();

        await Memory(FakeIntentExtractor.Answering("extracted", 0.1)).RecallAsync(TestData.Query(), Ct);

        Assert.Null(Single(capture).GetTagItem("mnemosyne.recall.intent_fallback"));
    }

    [Fact]
    public async Task AnEmbeddingOfTheWrongLengthIsOutcomeUnavailable()
    {
        _store.Dimensions = 4;
        using var capture = new TelemetryCapture();

        var result = await Memory().RecallAsync(TestData.Query(), Ct);

        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
        Assert.Equal("Unavailable", Single(capture).GetTagItem("error.type"));
        AssertOperation(capture, "Unavailable");
    }

    [Fact]
    public async Task AThrownCancellationIsAnException()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _generator.Respond = () => throw new OperationCanceledException(cts.Token);
        using var capture = new TelemetryCapture();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await Memory().RecallAsync(TestData.Query(), cts.Token));

        var activity = Single(capture);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("System.OperationCanceledException", activity.GetTagItem("error.type"));
        var recorded = Assert.Single(activity.Events);
        Assert.Equal("exception", recorded.Name);
        Assert.Equal([new KeyValuePair<string, object?>("exception.type", "System.OperationCanceledException")], recorded.Tags);
        AssertOperation(capture, "exception");
    }

    [Fact]
    public async Task AStoreBugIsAnException()
    {
        _store.SearchResult = Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>.Success([null!]);
        using var capture = new TelemetryCapture();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Memory().RecallAsync(TestData.Query(), Ct));

        var activity = Single(capture);
        Assert.Equal("System.InvalidOperationException", activity.GetTagItem("error.type"));
        Assert.Equal(1, activity.GetTagItem("mnemosyne.recall.candidates"));
        AssertOperation(capture, "exception");
    }

    [Fact]
    public async Task ANullQueryIsAnExceptionWithoutQueryTags()
    {
        using var capture = new TelemetryCapture();

        await Assert.ThrowsAsync<ArgumentNullException>("query", async () => await Memory().RecallAsync(null!, Ct));

        var activity = Single(capture);
        Assert.Equal([new("error.type", "System.ArgumentNullException")], AllTags(activity));
        AssertOperation(capture, "exception");
    }

    [Fact]
    public async Task TheStoresActivitiesNestUnderRecall()
    {
        var store = new TracingStore(_store);
        using var capture = new TelemetryCapture();

        await Memory(store: store).RecallAsync(TestData.Query(), Ct);

        var activities = capture.Activities;
        Assert.Equal(2, activities.Count);
        Assert.Equal("mnemosyne.search", activities[0].OperationName);
        Assert.Equal(Recall, activities[1].OperationName);
        Assert.Equal(activities[1].SpanId, activities[0].ParentSpanId);
    }

    [Fact]
    public async Task NoFacadeSignalCarriesUserData()
    {
        // One token in every field that holds user data. Ids are GUIDs written with dashes, so none can contain it.
        var token = Guid.NewGuid().ToString("N");
        var scope = new ReasonletScope($"scope-{token}");
        var query = TestData.Query() with { Scope = scope, Question = $"question-{token}", Intent = $"intent-{token}" };
        var definition = TestData.Definition() with { Scope = scope, Name = $"name-{token}", Intent = $"intent-{token}" };
        _store.SearchResult = Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>.Success(
            [new ReasonletCandidate(definition, 0.9, new ReasonletMetrics(0, 0, null, DateTimeOffset.UnixEpoch))]);
        using var capture = new TelemetryCapture();

        Assert.Single((await Memory().RecallAsync(query, Ct)).Value);

        // A dependency exception whose message quotes the question, through an extractor and the generator.
        // The extractor's outage falls back to an unfiltered search, so that recall succeeds.
        var extractor = new FakeIntentExtractor { Respond = () => throw new InvalidCastException($"extractor: {token}") };
        Assert.Single((await Memory(extractor).RecallAsync(query with { Intent = null }, Ct)).Value);
        _generator.Respond = () => throw new InvalidCastException($"generator: {token}");
        Assert.Equal(MnemosyneErrorCode.Unavailable, (await Memory().RecallAsync(query, Ct)).Error.Code);

        // An invalid query and an extractor bug, both with messages that quote user data.
        Assert.Equal(MnemosyneErrorCode.Validation, (await Memory().RecallAsync(query with { Top = 0 }, Ct)).Error.Code);
        var buggy = new FakeIntentExtractor
        {
            Respond = () => Result<ReasonletIntent, MnemosyneError>.Failure(new(MnemosyneErrorCode.Conflict, token)),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Memory(buggy).RecallAsync(query with { Intent = null }, Ct));
        _generator.Respond = new FakeEmbeddingGenerator().Respond;

        // A judged run and a judged remember, with the token in the run's question and answer and the capture's answer.
        var judge = new FakeEvaluator();
        var run = new ReasonletRun
        {
            Scope = scope,
            Id = ReasonletId.New(),
            Outcome = new ReasonletOutcome(true, TimeSpan.FromSeconds(1), null),
            Provenance = TestData.Provenance(),
            Question = $"question-{token}",
            Answer = $"answer-{token}",
        };
        var capture0 = TestData.Capture() with { Scope = scope, Question = $"question-{token}", Answer = $"answer-{token}" };
        Assert.True((await Memory(evaluator: judge).RecordExecutionAsync(run, Ct)).IsSuccess);
        Assert.True((await Memory(evaluator: judge).RememberAsync(capture0, Ct)).IsSuccess);

        // A judge that throws, one that is down and one that fails otherwise, each quoting the token, and one whose
        // invalid name holds it.
        judge.Respond = () => throw new HttpRequestException($"judge: {token}");
        Assert.True((await Memory(evaluator: judge).RecordExecutionAsync(run, Ct)).IsSuccess);
        judge.Respond = () => Result<ReasonletEvaluation, MnemosyneError>.Failure(new(MnemosyneErrorCode.Unavailable, token, token));
        Assert.True((await Memory(evaluator: judge).RememberAsync(capture0, Ct)).IsSuccess);
        judge.Respond = () => Result<ReasonletEvaluation, MnemosyneError>.Failure(new(MnemosyneErrorCode.Conflict, token, token));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Memory(evaluator: judge).RecordExecutionAsync(run, Ct));
        judge.Respond = () => new ReasonletEvaluation(true, 0.5, new string('x', 80) + token);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Memory(evaluator: judge).RememberAsync(capture0, Ct));

        var activities = capture.Activities;
        Assert.Equal(11, activities.Count);
        var measurements = capture.Measurements;
        Assert.NotEmpty(measurements);
        var emitted = new List<string?>();
        foreach (var activity in activities)
        {
            emitted.Add(activity.DisplayName);
            emitted.Add(activity.OperationName);
            emitted.Add(activity.StatusDescription);
            emitted.Add(activity.TraceStateString);
            foreach (var link in activity.Links)
            {
                emitted.Add(link.Context.TraceState);
                emitted.AddRange(Flatten(link.Tags ?? []));
            }

            emitted.AddRange(Flatten(activity.TagObjects));
            emitted.AddRange(activity.Baggage.SelectMany(b => new[] { b.Key, b.Value }));
            foreach (var recorded in activity.Events)
            {
                emitted.Add(recorded.Name);
                emitted.AddRange(Flatten(recorded.Tags));
            }
        }

        foreach (var measurement in measurements)
        {
            emitted.Add(measurement.Instrument);
            emitted.AddRange(Flatten(measurement.Tags));
        }

        Assert.DoesNotContain(emitted, text => text is not null && text.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutAListenerTheRecallStillRuns()
    {
        _store.SearchResult = Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>.Success([Candidate()]);

        var result = await Memory().RecallAsync(TestData.Query(), Ct);

        Assert.Single(result.Value);
    }

    [Fact]
    public async Task WithoutAListenerAFailureIsReturned()
    {
        var result = await Memory().RecallAsync(TestData.Query() with { Top = 0 }, Ct);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
    }

    [Fact]
    public async Task WithoutAListenerAnExceptionStillPropagates()
    {
        var thrown = new InvalidOperationException("bug");

        var caught = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await RecallTelemetry.RunAsync(_ => throw thrown));

        Assert.Same(thrown, caught);
    }

    [Fact]
    public async Task WithoutAListenerTheBodyGetsNoActivity()
    {
        Activity? seen = new("placeholder");

        var result = await RecallTelemetry.RunAsync(activity =>
        {
            seen = activity;
            return ValueTask.FromResult(Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Success([]));
        });

        Assert.Empty(result.Value);
        Assert.Null(seen);
    }

    [Theory]
    [InlineData(MnemosyneErrorCode.Validation, "Validation")]
    [InlineData(MnemosyneErrorCode.NotFound, "NotFound")]
    [InlineData(MnemosyneErrorCode.Conflict, "Conflict")]
    [InlineData(MnemosyneErrorCode.Unavailable, "Unavailable")]
    [InlineData((MnemosyneErrorCode)99, "99")]
    public void AnErrorCodeIsTaggedByItsName(MnemosyneErrorCode code, string name) =>
        Assert.Equal(name, RecallTelemetry.ErrorName(code));

    private static void AssertShared(List<Instrument> published, string name, string unit, string description)
    {
        var instruments = published.Where(instrument => instrument.Name == name).ToList();
        Assert.Contains(instruments, instrument => instrument.Meter == RecallTelemetry.Meter);
        Assert.Contains(instruments, instrument => instrument.Meter == StoreTelemetry.Meter);
        Assert.All(instruments, instrument =>
        {
            Assert.Equal(unit, instrument.Unit);
            Assert.Equal(description, instrument.Description);
        });
    }

    private static IEnumerable<string?> Flatten(IEnumerable<KeyValuePair<string, object?>> tags) =>
        tags.SelectMany(tag => new[] { tag.Key, Convert.ToString(tag.Value, CultureInfo.InvariantCulture) });

    /// <summary>The one activity of the capture, which must be a recall.</summary>
    private static Activity Single(TelemetryCapture capture)
    {
        var activity = Assert.Single(capture.Activities);
        Assert.Equal(Recall, activity.OperationName);
        Assert.Equal(Recall, activity.DisplayName);
        Assert.Equal(MnemosyneTelemetry.ActivitySourceName, activity.Source.Name);
        return activity;
    }

    /// <summary>Every tag of an activity, sorted by key.</summary>
    private static KeyValuePair<string, object?>[] AllTags(Activity activity) =>
        [.. activity.TagObjects.OrderBy(tag => tag.Key, StringComparer.Ordinal)];

    /// <summary>Asserts one recall count and one duration, with the tags the spec gives them.</summary>
    private static void AssertOperation(TelemetryCapture capture, string outcome)
    {
        var count = Assert.Single(capture.Of("mnemosyne.operations"));
        Assert.Equal(1, count.Value);
        Assert.Equal(
            [new("mnemosyne.operation", "recall"), new("mnemosyne.outcome", outcome)],
            count.Tags.OrderBy(tag => tag.Key, StringComparer.Ordinal));
        var duration = Assert.Single(capture.Of("mnemosyne.operation.duration"));
        Assert.True(duration.Value > 0);
        Assert.Equal([new KeyValuePair<string, object?>("mnemosyne.operation", "recall")], duration.Tags);
    }

    /// <summary>A store that traces its search on the shared source, as the PostgreSQL store does.</summary>
    private sealed class TracingStore(IReasonletStore inner) : IReasonletStore
    {
        private static readonly ActivitySource Source = new(MnemosyneTelemetry.ActivitySourceName);

        public int Dimensions => inner.Dimensions;

        public async ValueTask<Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>> SearchAsync(
            ReasonletScope scope, ReadOnlyMemory<float> embedding, string? intent, int limit, CancellationToken ct)
        {
            using var activity = Source.StartActivity("mnemosyne.search");
            return await inner.SearchAsync(scope, embedding, intent, limit, ct);
        }

        public ValueTask<Result<ReasonletReceipt, MnemosyneError>> RememberAsync(ReasonletStoreEntry entry, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<Result<ReasonletDefinition, MnemosyneError>> GetAsync(ReasonletScope scope, ReasonletId id, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<Result<ReasonletRunReceipt, MnemosyneError>> AppendExecutionAsync(
            ReasonletScope scope, ReasonletId id, ReasonletOutcome outcome, ReasonletProvenance provenance, ReasonletEvaluation? evaluation, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<UnitResult<MnemosyneError>> AppendFeedbackAsync(
            ReasonletScope scope, ExecutionId execution, ReasonletFeedback feedback, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<Result<ReasonletErasureResult, MnemosyneError>> EraseAsync(ReasonletErasure erasure, CancellationToken ct) =>
            throw new NotSupportedException();

        public ValueTask<Result<ReasonletPruneResult, MnemosyneError>> PruneAsync(ReasonletRetention retention, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
