using System.Diagnostics;
using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using Npgsql;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.PostgreSql.Tests;

/// <summary>The non-parallel collection the telemetry tests run in.</summary>
[CollectionDefinition(nameof(TelemetryCollection), DisableParallelization = true)]
public sealed class TelemetryCollection;

/// <summary>The signals every store operation emits, against a real database.</summary>
/// <remarks>
/// Listeners are global, so isolation takes two steps. Each test captures through <see cref="TelemetryCapture"/>, which
/// keeps only its own trace: activities by their trace id, and measurements by the trace that is current when they are
/// recorded, which flows into every store call the test awaits. That filter alone keeps a test exact while other
/// classes run, measurements included, so no assertion has to work from a before-and-after delta. The class also
/// runs in a non-parallel collection, which xunit runs alone after the parallel ones, so a filter bug could not
/// hide behind an empty process either.
/// </remarks>
[Collection(nameof(TelemetryCollection))]
public sealed class TelemetryTests(PgVectorFixture fixture) : IClassFixture<PgVectorFixture>
{
    private static readonly ReasonletOutcome Success = new(true, TimeSpan.FromMilliseconds(10), 0.5);
    private static readonly ReasonletFeedback Feedback = new(0.5, 0.5, 0.5);
    private static readonly TimeSpan FiveDays = TimeSpan.FromDays(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MigrateEmitsOneActivity()
    {
        var store = fixture.CreateStore();
        using var capture = new TelemetryCapture();

        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);

        var activity = Single(capture, "mnemosyne.migrate");
        Assert.Empty(MnemosyneTags(activity));
        AssertOperation(capture, "migrate", "ok");
    }

    [Fact]
    public async Task RememberTagsTheCaptureKindTheIdAndTheVersion()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        using var capture = new TelemetryCapture();

        var created = (await store.RememberAsync(Entries.Entry(scope), Ct)).Value;
        var executed = (await store.RememberAsync(Entries.Entry(scope, parentId: created.Id), Ct)).Value;
        var versioned = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("b"), created.Id), Ct)).Value;

        var activities = capture.Activities;
        Assert.Equal(3, activities.Count);
        Assert.All(activities, a => Assert.Equal("mnemosyne.remember", a.OperationName));
        AssertRemembered(activities[0], "NewReasonlet", created);
        AssertRemembered(activities[1], "ExecutionOnly", executed);
        AssertRemembered(activities[2], "NewVersion", versioned);
        Assert.Equal(
            ["NewReasonlet", "ExecutionOnly", "NewVersion"],
            capture.Of("mnemosyne.reasonlets.remembered").Select(m => Assert.IsType<string>(m.Tag("mnemosyne.capture_kind"))));
        Assert.All(capture.Of("mnemosyne.reasonlets.remembered"), m => Assert.Equal(1, m.Value));
        Assert.All(capture.Of("mnemosyne.reasonlets.remembered"), m => Assert.Single(m.Tags));
        Assert.Equal(3, capture.Of("mnemosyne.operations").Count(m => Equals(m.Tag("mnemosyne.outcome"), "ok")));
    }

    [Fact]
    public async Task RememberTagsTheStatus()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        using var capture = new TelemetryCapture();

        var active = (await store.RememberAsync(Entries.Entry(scope), Ct)).Value;
        var pending = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("b")) with { ReviewRequired = true }, Ct)).Value;

        var execution = (await store.RememberAsync(
            Entries.Entry(scope, Entries.Graph("b"), pending.Id) with { ReviewRequired = true }, Ct)).Value;

        var activities = capture.Activities;
        Assert.Equal("active", activities[0].GetTagItem("mnemosyne.reasonlet.status"));
        Assert.Equal("pending_review", activities[1].GetTagItem("mnemosyne.reasonlet.status"));
        Assert.Equal(ReasonletCaptureKind.ExecutionOnly, execution.Kind);
        Assert.Equal("pending_review", activities[2].GetTagItem("mnemosyne.reasonlet.status"));
        Assert.Equal(ReasonletStatus.PendingReview, pending.Status);
        Assert.Equal(ReasonletStatus.Active, active.Status);
    }

    [Fact]
    public async Task RememberTagsTheVerdictSourceOfTheRunItRecords()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var evaluation = new ReasonletEvaluation(true, 0.5, "judge");
        using var capture = new TelemetryCapture();

        var created = (await store.RememberAsync(Entries.Entry(scope) with { Evaluation = evaluation }, Ct)).Value;
        var executed = (await store.RememberAsync(Entries.Entry(scope, parentId: created.Id) with { Evaluation = evaluation }, Ct)).Value;
        var versioned = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("b"), created.Id) with { Evaluation = evaluation }, Ct)).Value;

        var activities = capture.Activities;
        Assert.Equal(3, activities.Count);
        AssertRemembered(activities[0], "NewReasonlet", created, "evaluator");
        AssertRemembered(activities[1], "ExecutionOnly", executed, "evaluator");
        AssertRemembered(activities[2], "NewVersion", versioned, "evaluator");
    }

    [Fact]
    public async Task AFailedRememberHasNoVerdictSource()
    {
        var store = await MigratedStoreAsync();
        using var capture = new TelemetryCapture();

        var missing = await store.RememberAsync(Entries.Entry(PgVectorFixture.NewScope(), parentId: ReasonletId.New()), Ct);

        Assert.Equal(MnemosyneErrorCode.NotFound, missing.Error.Code);
        Assert.Null(Single(capture, "mnemosyne.remember").GetTagItem("mnemosyne.verdict_source"));
    }

    [Fact]
    public async Task AFailedRememberCarriesTheErrorAndNoReceipt()
    {
        var store = await MigratedStoreAsync();
        using var capture = new TelemetryCapture();

        var result = await store.RememberAsync(Entries.Entry(PgVectorFixture.NewScope(), parentId: ReasonletId.New()), Ct);

        Assert.Equal(MnemosyneErrorCode.NotFound, result.Error.Code);
        var activity = Single(capture, "mnemosyne.remember");
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal([new("error.type", "NotFound")], AllTags(activity));
        Assert.Empty(capture.Of("mnemosyne.reasonlets.remembered"));
        AssertOperation(capture, "remember", "NotFound");
    }

    [Fact]
    public async Task GetEmitsOneActivity()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var receipt = (await store.RememberAsync(Entries.Entry(scope), Ct)).Value;
        using var capture = new TelemetryCapture();

        Assert.True((await store.GetAsync(scope, receipt.Id, Ct)).IsSuccess);

        Assert.Empty(AllTags(Single(capture, "mnemosyne.get")));
        AssertOperation(capture, "get", "ok");
    }

    [Fact]
    public async Task AValidationFailureIsStillAnOperation()
    {
        var store = await MigratedStoreAsync();
        using var capture = new TelemetryCapture();

        var result = await store.GetAsync(PgVectorFixture.NewScope(), default, Ct);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        var activity = Single(capture, "mnemosyne.get");
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("Validation", activity.GetTagItem("error.type"));
        AssertOperation(capture, "get", "Validation");
    }

    [Fact]
    public async Task AppendExecutionEmitsOneActivity()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var receipt = (await store.RememberAsync(Entries.Entry(scope), Ct)).Value;
        using var capture = new TelemetryCapture();

        Assert.True((await store.AppendExecutionAsync(scope, receipt.Id, Success, Entries.Provenance(), null, Ct)).IsSuccess);

        Assert.Equal([new("mnemosyne.verdict_source", "agent")], AllTags(Single(capture, "mnemosyne.execution.append")));
        AssertOperation(capture, "execution.append", "ok");
    }

    [Fact]
    public async Task AJudgedAppendHasVerdictSourceEvaluator()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var receipt = (await store.RememberAsync(Entries.Entry(scope), Ct)).Value;
        using var capture = new TelemetryCapture();

        var evaluation = new ReasonletEvaluation(false, 0.2, "judge");
        Assert.True((await store.AppendExecutionAsync(scope, receipt.Id, Success, Entries.Provenance(), evaluation, Ct)).IsSuccess);

        Assert.Equal([new("mnemosyne.verdict_source", "evaluator")], AllTags(Single(capture, "mnemosyne.execution.append")));
        AssertOperation(capture, "execution.append", "ok");
    }

    [Fact]
    public async Task AFailedAppendStillHasItsVerdictSource()
    {
        var store = await MigratedStoreAsync();
        using var capture = new TelemetryCapture();

        var missing = await store.AppendExecutionAsync(PgVectorFixture.NewScope(), ReasonletId.New(), Success, Entries.Provenance(), null, Ct);

        Assert.Equal(MnemosyneErrorCode.NotFound, missing.Error.Code);
        Assert.Equal(
            [new("error.type", "NotFound"), new("mnemosyne.verdict_source", "agent")],
            AllTags(Single(capture, "mnemosyne.execution.append")));
    }

    [Fact]
    public async Task FeedbackThatGivesAVerdictHasVerdictSourceFeedback()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var receipt = (await store.RememberAsync(Entries.Entry(scope), Ct)).Value;
        using var capture = new TelemetryCapture();

        // The run succeeded. Agreeing with it, overruling it and repeating the overrule each give a verdict, so each is
        // tagged whether or not it flips the effective success; a scores-only update and a failed call give none.
        Assert.True((await store.AppendFeedbackAsync(scope, receipt.Execution, Feedback with { Worked = true }, Ct)).IsSuccess);
        Assert.True((await store.AppendFeedbackAsync(scope, receipt.Execution, Feedback with { Worked = false }, Ct)).IsSuccess);
        Assert.True((await store.AppendFeedbackAsync(scope, receipt.Execution, Feedback with { Worked = false }, Ct)).IsSuccess);
        Assert.True((await store.AppendFeedbackAsync(scope, receipt.Execution, Feedback, Ct)).IsSuccess);
        var missing = await store.AppendFeedbackAsync(scope, ExecutionId.New(), Feedback with { Worked = true }, Ct);

        Assert.Equal(MnemosyneErrorCode.NotFound, missing.Error.Code);
        var activities = capture.Activities;
        Assert.Equal(5, activities.Count);
        KeyValuePair<string, object?>[] tagged = [new("mnemosyne.verdict_source", "feedback")];
        Assert.Equal(tagged, AllTags(activities[0]));
        Assert.Equal(tagged, AllTags(activities[1]));
        Assert.Equal(tagged, AllTags(activities[2]));
        Assert.Empty(AllTags(activities[3]));
        Assert.Equal([new("error.type", "NotFound")], AllTags(activities[4]));
    }

    [Fact]
    public async Task AppendFeedbackEmitsOneActivity()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var receipt = (await store.RememberAsync(Entries.Entry(scope), Ct)).Value;
        using var capture = new TelemetryCapture();

        Assert.True((await store.AppendFeedbackAsync(scope, receipt.Execution, Feedback, Ct)).IsSuccess);
        var missing = await store.AppendFeedbackAsync(scope, ExecutionId.New(), Feedback, Ct);

        Assert.Equal(MnemosyneErrorCode.NotFound, missing.Error.Code);
        var activities = capture.Activities;
        Assert.Equal(2, activities.Count);
        Assert.All(activities, a => Assert.Equal("mnemosyne.feedback.append", a.OperationName));
        Assert.Empty(AllTags(activities[0]));
        Assert.Equal(ActivityStatusCode.Unset, activities[0].Status);
        Assert.Equal([new("error.type", "NotFound")], AllTags(activities[1]));
        Assert.Equal(ActivityStatusCode.Error, activities[1].Status);
        Assert.Equal(
            ["ok", "NotFound"],
            capture.Of("mnemosyne.operations").Select(m => Assert.IsType<string>(m.Tag("mnemosyne.outcome"))));
    }

    [Fact]
    public async Task SearchTagsTheLimitTheCountAndTheTopSimilarity()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        await store.RememberAsync(Entries.Entry(scope), Ct);
        await store.RememberAsync(Entries.Entry(scope, Entries.Graph("b"), embedding: new float[] { 0f, 1f, 0f }), Ct);
        using var capture = new TelemetryCapture();

        var candidates = (await store.SearchAsync(scope, new float[] { 1f, 0.5f, -0.25f }, null, 5, Ct)).Value;

        Assert.Equal(2, candidates.Count);
        var activity = Single(capture, "mnemosyne.search");
        Assert.Equal(
            [new("mnemosyne.search.limit", 5), new("mnemosyne.search.returned", 2), new("mnemosyne.search.top_similarity", candidates[0].Similarity)],
            MnemosyneTags(activity));
        var returned = Assert.Single(capture.Of("mnemosyne.search.returned"));
        Assert.Equal(2, returned.Value);
        Assert.Empty(returned.Tags);
        AssertOperation(capture, "search", "ok");
    }

    [Fact]
    public async Task ASearchThatFindsNothingHasNoTopSimilarity()
    {
        var store = await MigratedStoreAsync();
        using var capture = new TelemetryCapture();

        var candidates = (await store.SearchAsync(PgVectorFixture.NewScope(), new float[] { 1f, 0f, 0f }, null, 3, Ct)).Value;

        Assert.Empty(candidates);
        Assert.Equal(
            [new("mnemosyne.search.limit", 3), new("mnemosyne.search.returned", 0)],
            MnemosyneTags(Single(capture, "mnemosyne.search")));
        Assert.Equal(0, Assert.Single(capture.Of("mnemosyne.search.returned")).Value);
    }

    [Fact]
    public async Task AFailedSearchTagsOnlyTheLimit()
    {
        var store = await MigratedStoreAsync();
        using var capture = new TelemetryCapture();

        var result = await store.SearchAsync(PgVectorFixture.NewScope(), new float[] { 1f, 0f }, null, 3, Ct);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal(
            [new("error.type", "Validation"), new("mnemosyne.search.limit", 3)],
            AllTags(Single(capture, "mnemosyne.search")));
        Assert.Empty(capture.Of("mnemosyne.search.returned"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EraseTagsWhatItErasedAndCountsIt(bool bySubject)
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var receipt = (await store.RememberAsync(Entries.Entry(scope), Ct)).Value;
        await store.AppendExecutionAsync(scope, receipt.Id, Success, Entries.Provenance(), null, Ct);
        using var capture = new TelemetryCapture();

        var result = (await store.EraseAsync(new ReasonletErasure(scope, bySubject ? "subject-1" : null), Ct)).Value;

        Assert.Equal((1, 2), (result.Reasonlets, result.Executions));
        Assert.Equal(
            [new("mnemosyne.erase.by", bySubject ? "subject" : "scope"), new("mnemosyne.erased.executions", 2), new("mnemosyne.erased.reasonlets", 1)],
            MnemosyneTags(Single(capture, "mnemosyne.erase")));
        AssertCounter(capture, "mnemosyne.erased.reasonlets", 1);
        AssertCounter(capture, "mnemosyne.erased.executions", 2);
        AssertOperation(capture, "erase", "ok");
    }

    [Fact]
    public async Task AFailedEraseTagsOnlyHowItErases()
    {
        var store = await MigratedStoreAsync();
        using var capture = new TelemetryCapture();

        var result = await store.EraseAsync(new ReasonletErasure(PgVectorFixture.NewScope(), " "), Ct);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal(
            [new("error.type", "Validation"), new("mnemosyne.erase.by", "subject")],
            AllTags(Single(capture, "mnemosyne.erase")));
        Assert.Empty(capture.Of("mnemosyne.erased.reasonlets"));
        Assert.Empty(capture.Of("mnemosyne.erased.executions"));
    }

    [Fact]
    public async Task PruneTagsWhatItPrunedAndCountsIt()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        await ChainAsync(store, scope, 2);
        using var capture = new TelemetryCapture();

        var result = (await store.PruneAsync(new ReasonletRetention { Scope = scope, ReasonletsUnusedFor = FiveDays }, Ct)).Value;

        Assert.Equal((2, 2), (result.Reasonlets, result.Executions));
        Assert.Equal(
            [new("mnemosyne.pruned.executions", 2), new("mnemosyne.pruned.reasonlets", 2)],
            MnemosyneTags(Single(capture, "mnemosyne.prune")));
        AssertCounter(capture, "mnemosyne.pruned.reasonlets", 2);
        AssertCounter(capture, "mnemosyne.pruned.executions", 2);
        AssertOperation(capture, "prune", "ok");
    }

    [Fact]
    public async Task PruneFailureStillCountsCommittedBatches()
    {
        // A chain of four in batches of two, as in PruneTests: the first batch commits two Reasonlets with their two
        // runs, then every try of the next batch fails. The result carries no counts, but the signals count what
        // committed.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        await ChainAsync(store, scope, 4);
        store.PruneBatchSizeOverride = 2;
        store.PruneRetryDelay = (_, _) => Task.CompletedTask;
        var calls = 0;
        store.BeforePruneBatchCommit = () => ++calls > 1 ? throw new PostgresException("injected", "ERROR", "ERROR", "40001") : Task.CompletedTask;
        using var capture = new TelemetryCapture();

        var result = await store.PruneAsync(new ReasonletRetention { Scope = scope, ReasonletsUnusedFor = FiveDays }, Ct);

        Assert.Equal(MnemosyneErrorCode.Conflict, result.Error.Code);
        var activity = Single(capture, "mnemosyne.prune");
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(
            [new("error.type", "Conflict"), new("mnemosyne.pruned.executions", 2), new("mnemosyne.pruned.reasonlets", 2)],
            AllTags(activity));
        AssertCounter(capture, "mnemosyne.pruned.reasonlets", 2);
        AssertCounter(capture, "mnemosyne.pruned.executions", 2);
        AssertOperation(capture, "prune", "Conflict");
    }

    [Fact]
    public async Task NoSignalCarriesUserData()
    {
        // One token in every field that holds user data. Ids are GUIDs written with dashes, so none can contain it.
        var token = Guid.NewGuid().ToString("N");
        var scope = new ReasonletScope($"scope-{token}");
        var subject = $"subject-{token}";
        var intent = $"intent-{token}";
        var provenance = new ReasonletProvenance
        {
            Source = $"source-{token}",
            RunId = $"run-{token}",
            SubjectId = subject,
            Tags = new Dictionary<string, string> { [$"key-{token}"] = $"value-{token}" },
        };
        var graph = Entries.Graph(token);
        Assert.Contains(graph.Nodes, node => node.Name!.Contains(token, StringComparison.Ordinal));
        var capture0 = Entries.Entry(scope, graph, provenance: provenance).Capture with { Question = $"question-{token}" };
        var entry = new ReasonletStoreEntry(capture0, intent, $"name-{token}", new float[] { 1f, 0.5f, -0.25f });
        var store = await MigratedStoreAsync();
        using var capture = new TelemetryCapture();

        var receipt = (await store.RememberAsync(entry, Ct)).Value;
        Assert.True((await store.GetAsync(scope, receipt.Id, Ct)).IsSuccess);
        var execution = (await store.AppendExecutionAsync(scope, receipt.Id, Success, provenance, null, Ct)).Value.Execution;
        Assert.True((await store.AppendFeedbackAsync(scope, execution, Feedback, Ct)).IsSuccess);
        Assert.Single((await store.SearchAsync(scope, entry.Embedding, intent, 5, Ct)).Value);
        Assert.True((await store.EraseAsync(new ReasonletErasure(scope, subject), Ct)).IsSuccess);
        Assert.True((await store.PruneAsync(new ReasonletRetention { Scope = scope, ExecutionsOlderThan = FiveDays }, Ct)).IsSuccess);

        // A failure result too: the same user data, with an embedding of the wrong dimension.
        var invalid = await store.RememberAsync(entry with { Embedding = new float[] { 1f, 0.5f } }, Ct);
        Assert.Equal(MnemosyneErrorCode.Validation, invalid.Error.Code);

        // The facade over the store, judging: the token in the capture's answer, the run's question and answer, and the
        // message of a judge that throws.
        var judge = new Judge();
        var memory = new ReasonletMemory(
            store, new ReasonletSelector(new MnemosyneOptions().Ranking, TimeProvider.System), new ConstantEmbeddings(), null, judge, new MnemosyneOptions());
        var judged = (await memory.RememberAsync(capture0 with { Answer = $"answer-{token}" }, Ct)).Value;
        var run = new ReasonletRun
        {
            Scope = scope,
            Id = judged.Id,
            Outcome = Success,
            Provenance = provenance,
            Question = $"question-{token}",
            Answer = $"answer-{token}",
        };
        Assert.True((await memory.RecordExecutionAsync(run, Ct)).IsSuccess);
        judge.Throw = new HttpRequestException($"judge: {token}");
        Assert.True((await memory.RecordExecutionAsync(run, Ct)).IsSuccess);

        // A judge that is down with the token in its error's message and detail, and one whose invalid name holds it.
        judge.Throw = null;
        judge.Answer = Result<ReasonletEvaluation, MnemosyneError>.Failure(new(MnemosyneErrorCode.Unavailable, $"down: {token}", $"detail: {token}"));
        Assert.True((await memory.RecordExecutionAsync(run, Ct)).IsSuccess);
        judge.Answer = new ReasonletEvaluation(true, 0.5, new string('x', 80) + token);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await memory.RecordExecutionAsync(run, Ct));

        var activities = capture.Activities;
        Assert.Equal(21, activities.Count);
        Assert.Equal("Validation", activities[7].GetTagItem("error.type"));

        // The throwing judge: its evaluate activity shows the failure, and the run falls back to the agent's verdict.
        Assert.Equal("mnemosyne.evaluate", activities[14].OperationName);
        Assert.Equal("System.Net.Http.HttpRequestException", activities[14].GetTagItem("error.type"));
        Assert.Equal("exception", Assert.Single(activities[14].Events).Name);
        Assert.Equal("mnemosyne.execution.append", activities[15].OperationName);
        Assert.Equal("agent", activities[15].GetTagItem("mnemosyne.verdict_source"));

        // The judge that is down: Unavailable on its evaluate activity, and the agent's verdict on the append.
        Assert.Equal("mnemosyne.evaluate", activities[17].OperationName);
        Assert.Equal("Unavailable", activities[17].GetTagItem("error.type"));
        Assert.Equal("agent", activities[18].GetTagItem("mnemosyne.verdict_source"));

        // The invalid name: an exception on the evaluate activity, with no evaluator tag, and nothing appended.
        Assert.Equal("mnemosyne.evaluate", activities[20].OperationName);
        Assert.Equal("System.InvalidOperationException", activities[20].GetTagItem("error.type"));
        Assert.Null(activities[20].GetTagItem("mnemosyne.evaluator"));
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

        // The scan read the evaluate activity's fields, its exception events and the evaluation instruments, not only
        // the store's tags.
        Assert.Contains("mnemosyne.evaluator", emitted);
        Assert.Contains("mnemosyne.evaluation.score", emitted);
        Assert.Contains("exception.type", emitted);
        Assert.Contains("mnemosyne.evaluations", emitted);
        Assert.DoesNotContain(emitted, text => text is not null && text.Contains(token, StringComparison.Ordinal));
    }

    /// <summary>An evaluator that answers <see cref="Answer"/>, or throws <see cref="Throw"/> when it is set.</summary>
    private sealed class Judge : IReasonletEvaluator
    {
        public Exception? Throw { get; set; }

        public Result<ReasonletEvaluation, MnemosyneError> Answer { get; set; } = new ReasonletEvaluation(true, 0.9, "judge");

        public ValueTask<Result<ReasonletEvaluation, MnemosyneError>> EvaluateAsync(
            ReasonletEvaluationRequest request, CancellationToken ct) =>
            Throw is { } thrown ? throw thrown : ValueTask.FromResult(Answer);
    }

    /// <summary>An embedding generator that answers the same three-value embedding for every text.</summary>
    private sealed class ConstantEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                values.Select(_ => new Embedding<float>(new float[] { 1f, 0.5f, -0.25f }))));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static IEnumerable<string?> Flatten(IEnumerable<KeyValuePair<string, object?>> tags) =>
        tags.SelectMany(tag => new[] { tag.Key, Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) });

    /// <summary>The one activity of the capture, which must have the expected name.</summary>
    private static Activity Single(TelemetryCapture capture, string name)
    {
        var activity = Assert.Single(capture.Activities);
        Assert.Equal(name, activity.OperationName);
        Assert.Equal(name, activity.DisplayName);
        Assert.Equal(MnemosyneTelemetry.ActivitySourceName, activity.Source.Name);
        return activity;
    }

    /// <summary>Every tag of an activity, sorted by key.</summary>
    private static KeyValuePair<string, object?>[] AllTags(Activity activity) =>
        [.. activity.TagObjects.OrderBy(tag => tag.Key, StringComparer.Ordinal)];

    /// <summary>The <c>mnemosyne.*</c> tags of an activity, sorted by key.</summary>
    private static KeyValuePair<string, object?>[] MnemosyneTags(Activity activity) =>
        [.. AllTags(activity).Where(tag => tag.Key.StartsWith("mnemosyne.", StringComparison.Ordinal))];

    private static void AssertRemembered(Activity activity, string kind, ReasonletReceipt receipt, string source = "agent")
    {
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        Assert.Equal(
            [
                new("mnemosyne.capture_kind", kind),
                new("mnemosyne.reasonlet.id", receipt.Id.Value.ToString()),
                new("mnemosyne.reasonlet.status", "active"),
                new("mnemosyne.reasonlet.version", receipt.Version),
                new("mnemosyne.verdict_source", source),
            ],
            AllTags(activity));
    }

    private static void AssertCounter(TelemetryCapture capture, string instrument, int value)
    {
        var measurement = Assert.Single(capture.Of(instrument));
        Assert.Equal(value, measurement.Value);
        Assert.Empty(measurement.Tags);
    }

    /// <summary>Asserts one operation count and one duration, with the tags the spec gives them.</summary>
    private static void AssertOperation(TelemetryCapture capture, string operation, string outcome)
    {
        var count = Assert.Single(capture.Of("mnemosyne.operations"));
        Assert.Equal(1, count.Value);
        Assert.Equal(
            [new("mnemosyne.operation", operation), new("mnemosyne.outcome", outcome)],
            count.Tags.OrderBy(tag => tag.Key, StringComparer.Ordinal));
        var duration = Assert.Single(capture.Of("mnemosyne.operation.duration"));
        Assert.True(duration.Value > 0);
        Assert.Equal([new KeyValuePair<string, object?>("mnemosyne.operation", operation)], duration.Tags);
    }

    private async Task<ReasonletPostgreSqlStore> MigratedStoreAsync()
    {
        var store = fixture.CreateStore();
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        return store;
    }

    /// <summary>Remembers a chain of <paramref name="length"/> versions, then makes the chain and its runs ten days old.</summary>
    private async Task ChainAsync(ReasonletPostgreSqlStore store, ReasonletScope scope, int length)
    {
        ReasonletId? parent = null;
        for (var i = 0; i < length; i++)
        {
            parent = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph($"v{i}"), parent), Ct)).Value.Id;
        }

        await Entries.ExecuteAsync(
            fixture.DataSource, "UPDATE mnemosyne.reasonlets SET created_at = now() - interval '10 days' WHERE scope = $1", scope.Value);
        await Entries.ExecuteAsync(
            fixture.DataSource,
            "UPDATE mnemosyne.reasonlet_metrics SET last_executed_at = now() - interval '10 days', "
            + "last_success_at = now() - interval '10 days' WHERE scope = $1",
            scope.Value);
        await Entries.ExecuteAsync(
            fixture.DataSource, "UPDATE mnemosyne.reasonlet_executions SET executed_at = now() - interval '10 days' WHERE scope = $1", scope.Value);
    }
}
