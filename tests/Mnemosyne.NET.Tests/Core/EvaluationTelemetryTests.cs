using System.Diagnostics;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql.Tests;
using Mnemosyne.NET.Tests.Abstractions;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Core;

/// <summary>The signals the facade emits around each judge call.</summary>
/// <remarks>
/// Listeners are global. Each test filters what it hears to its own trace through <see cref="TelemetryCapture"/>, and the
/// class runs in the recall telemetry's non-parallel collection, so that the tests without a listener really run with
/// none attached.
/// </remarks>
[Collection(nameof(RecallTelemetryCollection))]
public sealed class EvaluationTelemetryTests
{
    private const string Evaluate = "mnemosyne.evaluate";

    private readonly FakeStore _store = new();
    private readonly FakeSelector _selector = new();
    private readonly FakeEmbeddingGenerator _generator = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Paths() => ["record", "remember"];

    public static TheoryData<string, string> Outages() => new()
    {
        { "record", "unavailable" },
        { "record", "timeout" },
        { "remember", "unavailable" },
        { "remember", "timeout" },
    };

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task AJudgedRunTagsTheVerdict(string path)
    {
        var evaluator = new FakeEvaluator { Respond = () => new ReasonletEvaluation(false, 0.25, "llm-judge") };
        using var capture = new TelemetryCapture();

        Assert.True(await JudgeThroughAsync(path, evaluator, Ct));

        var activity = Single(capture);
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        Assert.Equal(
            [
                new("mnemosyne.evaluation.score", 0.25),
                new("mnemosyne.evaluation.success", false),
                new("mnemosyne.evaluator", "llm-judge"),
            ],
            AllTags(activity));
        Assert.Empty(activity.Events);
        AssertMeasurements(capture, "ok", "llm-judge");
    }

    [Theory]
    [MemberData(nameof(Outages))]
    public async Task AnUnavailableOrTimedOutJudgeIsOutcomeUnavailable(string path, string outage)
    {
        var evaluator = new FakeEvaluator
        {
            Respond = outage == "timeout"
                ? () => throw new TaskCanceledException("timed out")
                : () => Result<ReasonletEvaluation, MnemosyneError>.Failure(new(MnemosyneErrorCode.Unavailable, "down")),
        };
        using var capture = new TelemetryCapture();

        // The judge is down, so the run is still stored, unjudged; the activity shows the failure all the same.
        Assert.True(await JudgeThroughAsync(path, evaluator, Ct));

        var activity = Single(capture);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal([new("error.type", "Unavailable")], AllTags(activity));
        Assert.Empty(activity.Events);
        AssertMeasurements(capture, "Unavailable", evaluator: null);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task AThrowingJudgeIsAnExceptionThoughTheRunIsStored(string path)
    {
        var evaluator = new FakeEvaluator { Respond = () => throw new HttpRequestException("refused") };
        using var capture = new TelemetryCapture();

        Assert.True(await JudgeThroughAsync(path, evaluator, Ct));

        var activity = Single(capture);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal([new("error.type", "System.Net.Http.HttpRequestException")], AllTags(activity));
        AssertExceptionEvent(activity, "System.Net.Http.HttpRequestException");
        AssertMeasurements(capture, "exception", evaluator: null);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task AnyOtherJudgeErrorIsOutcomeItsCodeAndThrows(string path)
    {
        var evaluator = new FakeEvaluator
        {
            Respond = () => Result<ReasonletEvaluation, MnemosyneError>.Failure(new(MnemosyneErrorCode.Conflict, "bug")),
        };
        using var capture = new TelemetryCapture();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await JudgeThroughAsync(path, evaluator, Ct));

        var activity = Single(capture);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal([new("error.type", "Conflict")], AllTags(activity));
        Assert.Empty(activity.Events);
        AssertMeasurements(capture, "Conflict", evaluator: null);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task AnInvalidEvaluationIsAnExceptionWithoutTheJudgesName(string path)
    {
        // The name fails validation, so it is not trusted to name only the judge, and no signal carries it.
        var evaluator = new FakeEvaluator { Respond = () => new ReasonletEvaluation(true, 2, "judge") };
        using var capture = new TelemetryCapture();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await JudgeThroughAsync(path, evaluator, Ct));

        var activity = Single(capture);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal([new("error.type", "System.InvalidOperationException")], AllTags(activity));
        AssertExceptionEvent(activity, "System.InvalidOperationException");
        AssertMeasurements(capture, "exception", evaluator: null);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task TheCallersCancellationIsAnException(string path)
    {
        using var cts = new CancellationTokenSource();
        var evaluator = new FakeEvaluator();
        evaluator.Respond = () =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };
        using var capture = new TelemetryCapture();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await JudgeThroughAsync(path, evaluator, cts.Token));

        var activity = Single(capture);
        Assert.Equal([new("error.type", "System.OperationCanceledException")], AllTags(activity));
        AssertExceptionEvent(activity, "System.OperationCanceledException");
        AssertMeasurements(capture, "exception", evaluator: null);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task AnExpiredTimeoutIsOutcomeUnavailable(string path)
    {
        var clock = new ManualTimeProvider();
        var memory = new ReasonletMemory(_store, _selector, _generator, null, new WaitingEvaluator(clock.Fire), new MnemosyneOptions(), clock);
        using var capture = new TelemetryCapture();

        var succeeded = path == "record"
            ? (await memory.RecordExecutionAsync(Run() with { Question = "question", Answer = "answer" }, Ct)).IsSuccess
            : (await memory.RememberAsync(TestData.Capture() with { Answer = "answer" }, Ct)).IsSuccess;

        Assert.True(succeeded);
        var activity = Single(capture);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal([new("error.type", "Unavailable")], AllTags(activity));
        Assert.Empty(activity.Events);
        AssertMeasurements(capture, "Unavailable", evaluator: null);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task ATimeoutThatExpiresWhileTheCallWaitsForAStuckJudgeEndsTheWait(string path)
    {
        // The judge is entered and stuck, ignoring its token, before the clock fires, so the facade must really be
        // waiting when the timeout expires; nothing cancels inside the judge.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ManualTimeProvider();
        var evaluator = new IgnoringEvaluator(() => entered.TrySetResult());
        var memory = new ReasonletMemory(_store, _selector, _generator, null, evaluator, new MnemosyneOptions(), clock);
        var bound = TimeSpan.FromSeconds(10);
        using var capture = new TelemetryCapture();

        var call = SourceAsync(memory, path);
        await entered.Task.WaitAsync(bound, Ct);
        Assert.False(call.IsCompleted);

        clock.Fire();
        var source = await call.WaitAsync(bound, Ct);

        Assert.Equal(ReasonletVerdictSource.Agent, source);
        Assert.False(evaluator.Completion.Task.IsCompleted);
        var activity = Single(capture);
        Assert.Equal([new("error.type", "Unavailable")], AllTags(activity));
        AssertMeasurements(capture, "Unavailable", evaluator: null);
    }

    private async Task<ReasonletVerdictSource> SourceAsync(ReasonletMemory memory, string path) =>
        path == "record"
            ? (await memory.RecordExecutionAsync(Run() with { Question = "question", Answer = "answer" }, Ct)).Value.Source
            : (await memory.RememberAsync(TestData.Capture() with { Answer = "answer" }, Ct)).Value.Source;

    public static TheoryData<string, string> UnusableAnswers() => new()
    {
        { "record", "blank" },
        { "record", "oversized" },
        { "remember", "blank" },
        { "remember", "oversized" },
    };

    [Theory]
    [MemberData(nameof(UnusableAnswers))]
    public async Task AnUnusableAnswerCountsASkipAndStartsNoEvaluation(string path, string answer)
    {
        const string Sentinel = "skipped answer sentinel";
        var text = answer == "blank" ? "   " : Sentinel + new string('a', ReasonletMemory.MaxAnswerLength);
        var evaluator = new FakeEvaluator();
        var memory = Memory(evaluator);
        using var capture = new TelemetryCapture();

        var succeeded = path == "record"
            ? (await memory.RecordExecutionAsync(Run() with { Question = "question", Answer = text }, Ct)).IsSuccess
            : (await memory.RememberAsync(TestData.Capture() with { Answer = text }, Ct)).IsSuccess;

        Assert.True(succeeded);
        Assert.Empty(evaluator.Requests);
        Assert.Empty(capture.Activities);
        var skipped = Assert.Single(capture.Measurements);
        Assert.Equal("mnemosyne.evaluations", skipped.Instrument);
        Assert.Equal(1, skipped.Value);
        Assert.Equal([new KeyValuePair<string, object?>("mnemosyne.outcome", "skipped")], skipped.Tags);
        Assert.All(skipped.Tags, tag => Assert.DoesNotContain(Sentinel, tag.Value?.ToString() ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnusableAnswerWithoutAnEvaluatorCountsNothing()
    {
        using var capture = new TelemetryCapture();

        var memory = new ReasonletMemory(_store, _selector, _generator, null, null, new MnemosyneOptions());
        Assert.True((await memory.RecordExecutionAsync(Run() with { Question = "question", Answer = " " }, Ct)).IsSuccess);

        Assert.Empty(capture.Measurements);
    }

    [Fact]
    public async Task ARunThatIsNotJudgedEmitsNoEvaluation()
    {
        using var capture = new TelemetryCapture();

        Assert.True((await Memory(new FakeEvaluator()).RecordExecutionAsync(Run(), Ct)).IsSuccess);
        Assert.True((await Memory(new FakeEvaluator()).RememberAsync(TestData.Capture(), Ct)).IsSuccess);

        Assert.Empty(capture.Activities);
        Assert.Empty(capture.Measurements);
    }

    public static TheoryData<string> Responses() => ["verdict", "unavailable", "timeout", "exception", "conflict", "invalid"];

    [Theory]
    [MemberData(nameof(Responses))]
    public async Task WithoutAListenerJudgingStillWorks(string response)
    {
        var evaluator = new FakeEvaluator
        {
            Respond = response switch
            {
                "verdict" => () => FakeEvaluator.Verdict,
                "unavailable" => () => Result<ReasonletEvaluation, MnemosyneError>.Failure(new(MnemosyneErrorCode.Unavailable, "down")),
                "timeout" => () => throw new TaskCanceledException("timed out"),
                "exception" => () => throw new HttpRequestException("refused"),
                "conflict" => () => Result<ReasonletEvaluation, MnemosyneError>.Failure(new(MnemosyneErrorCode.Conflict, "bug")),
                _ => () => new ReasonletEvaluation(true, 2, "judge"),
            },
        };

        if (response is "conflict" or "invalid")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await JudgeThroughAsync("record", evaluator, Ct));
        }
        else
        {
            Assert.True(await JudgeThroughAsync("record", evaluator, Ct));
            Assert.Equal(response == "verdict" ? FakeEvaluator.Verdict : null, _store.Executed!.Value.Evaluation);
        }
    }

    [Fact]
    public async Task WithoutAListenerTheCallersCancellationStillPropagates()
    {
        using var cts = new CancellationTokenSource();
        var evaluator = new FakeEvaluator();
        evaluator.Respond = () =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await JudgeThroughAsync("record", evaluator, cts.Token));
    }

    private ReasonletMemory Memory(IReasonletEvaluator evaluator) =>
        new(_store, _selector, _generator, null, evaluator, new MnemosyneOptions());

    private static ReasonletRun Run() => new()
    {
        Scope = new ReasonletScope("tenant"),
        Id = ReasonletId.New(),
        Outcome = new ReasonletOutcome(true, TimeSpan.FromSeconds(2), 0.25),
        Provenance = TestData.Provenance(),
    };

    /// <summary>Records a judged run, or remembers a judged capture, and returns whether the call succeeded.</summary>
    private async Task<bool> JudgeThroughAsync(string path, IReasonletEvaluator evaluator, CancellationToken ct)
    {
        var memory = Memory(evaluator);
        return path == "record"
            ? (await memory.RecordExecutionAsync(Run() with { Question = "question", Answer = "answer" }, ct)).IsSuccess
            : (await memory.RememberAsync(TestData.Capture() with { Answer = "answer" }, ct)).IsSuccess;
    }

    /// <summary>The one activity of the capture, which must be an evaluation.</summary>
    private static Activity Single(TelemetryCapture capture)
    {
        var activity = Assert.Single(capture.Activities);
        Assert.Equal(Evaluate, activity.OperationName);
        Assert.Equal(Evaluate, activity.DisplayName);
        Assert.Equal(MnemosyneTelemetry.ActivitySourceName, activity.Source.Name);
        return activity;
    }

    /// <summary>Every tag of an activity, sorted by key.</summary>
    private static KeyValuePair<string, object?>[] AllTags(Activity activity) =>
        [.. activity.TagObjects.OrderBy(tag => tag.Key, StringComparer.Ordinal)];

    private static void AssertExceptionEvent(Activity activity, string type)
    {
        var recorded = Assert.Single(activity.Events);
        Assert.Equal("exception", recorded.Name);
        Assert.Equal([new KeyValuePair<string, object?>("exception.type", type)], recorded.Tags);
    }

    /// <summary>
    /// Asserts one evaluation count and duration, tagged with the judge's name when it answered, and one operation count
    /// and duration under operation <c>evaluate</c>.
    /// </summary>
    private static void AssertMeasurements(TelemetryCapture capture, string outcome, string? evaluator)
    {
        KeyValuePair<string, object?>[] named = evaluator is null ? [] : [new("mnemosyne.evaluator", evaluator)];

        var evaluations = Assert.Single(capture.Of("mnemosyne.evaluations"));
        Assert.Equal(1, evaluations.Value);
        Assert.Equal(
            [.. named, new("mnemosyne.outcome", outcome)],
            evaluations.Tags.OrderBy(tag => tag.Key, StringComparer.Ordinal));
        var evaluationDuration = Assert.Single(capture.Of("mnemosyne.evaluation.duration"));
        Assert.True(evaluationDuration.Value > 0);
        Assert.Equal(named, evaluationDuration.Tags);

        var count = Assert.Single(capture.Of("mnemosyne.operations"));
        Assert.Equal(1, count.Value);
        Assert.Equal(
            [new("mnemosyne.operation", "evaluate"), new("mnemosyne.outcome", outcome)],
            count.Tags.OrderBy(tag => tag.Key, StringComparer.Ordinal));
        var duration = Assert.Single(capture.Of("mnemosyne.operation.duration"));
        Assert.True(duration.Value > 0);
        Assert.Equal([new KeyValuePair<string, object?>("mnemosyne.operation", "evaluate")], duration.Tags);
    }
}
