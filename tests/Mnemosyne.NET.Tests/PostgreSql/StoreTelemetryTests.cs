using System.Diagnostics;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql;
using Mnemosyne.NET.PostgreSql.Tests;
using Npgsql;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.PostgreSql;

/// <summary>The non-parallel collection the telemetry tests run in.</summary>
[CollectionDefinition(nameof(StoreTelemetryCollection), DisableParallelization = true)]
public sealed class StoreTelemetryCollection;

/// <summary>The helper that wraps every store operation, tested without a database.</summary>
/// <remarks>
/// Listeners are global. Each test filters what it hears to its own trace through <see cref="TelemetryCapture"/>, so a
/// test running at the same time cannot add to it. The class also runs in a non-parallel collection, which xunit runs
/// alone after the parallel ones, so that the test without a listener really runs with none attached.
/// </remarks>
[Collection(nameof(StoreTelemetryCollection))]
public sealed class StoreTelemetryTests : IAsyncDisposable
{
    // Port 1 refuses the connection at once, so Npgsql fails without waiting for a timeout.
    private readonly NpgsqlDataSource _unreachable =
        new NpgsqlDataSourceBuilder("Host=127.0.0.1;Port=1;Username=u;Password=p;Database=d;Timeout=2;Pooling=false").Build();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheSourceAndTheMeterUseThePublicNames()
    {
        Assert.Equal("Mnemosyne.NET", MnemosyneTelemetry.ActivitySourceName);
        Assert.Equal("Mnemosyne.NET", MnemosyneTelemetry.MeterName);
        Assert.Equal(MnemosyneTelemetry.ActivitySourceName, StoreTelemetry.Source.Name);
        Assert.Equal(MnemosyneTelemetry.MeterName, StoreTelemetry.Meter.Name);
    }

    [Fact]
    public async Task ASuccessIsAnOkOperation()
    {
        using var capture = new TelemetryCapture();

        var result = await StoreTelemetry.RunAsync("get", activity => ValueTask.FromResult(activity is null ? 0 : 42), _ => null);

        Assert.Equal(42, result);
        var activity = Assert.Single(capture.Activities);
        Assert.Equal("mnemosyne.get", activity.OperationName);
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        Assert.Null(activity.GetTagItem("error.type"));
        AssertOperation(capture, "get", "ok");
    }

    [Fact]
    public async Task ACaptureIgnoresAnAmbientActivity()
    {
        // Two captures under one ambient activity, as a test host might set, must still hear only their own calls.
        using var ambient = new Activity("host").Start();
        using (var first = new TelemetryCapture())
        {
            using var second = new TelemetryCapture();
            await StoreTelemetry.RunAsync("get", _ => ValueTask.FromResult(1), _ => null);

            Assert.Empty(first.Activities);
            Assert.NotEqual(ambient.TraceId, Assert.Single(second.Activities).TraceId);
        }

        Assert.Same(ambient, Activity.Current);
    }

    [Fact]
    public async Task AFailureResultSetsTheErrorType()
    {
        using var capture = new TelemetryCapture();

        var result = await StoreTelemetry.RunAsync("erase", _ => ValueTask.FromResult(7), _ => MnemosyneErrorCode.Conflict);

        Assert.Equal(7, result);
        var activity = Assert.Single(capture.Activities);
        Assert.Equal("mnemosyne.erase", activity.OperationName);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("Conflict", activity.GetTagItem("error.type"));
        AssertOperation(capture, "erase", "Conflict");
    }

    [Fact]
    public async Task AnExceptionIsRecordedAndRethrown()
    {
        using var capture = new TelemetryCapture();
        var sentinel = Guid.NewGuid().ToString("N");
        var thrown = new InvalidOperationException($"subject-{sentinel} asked about scope-{sentinel}");

        var caught = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await StoreTelemetry.RunAsync<int>("prune", _ => throw thrown, _ => null));

        Assert.Same(thrown, caught);
        var activity = Assert.Single(capture.Activities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("System.InvalidOperationException", activity.GetTagItem("error.type"));
        var recorded = Assert.Single(activity.Events);
        Assert.Equal("exception", recorded.Name);
        Assert.Equal([new("exception.type", "System.InvalidOperationException")], recorded.Tags);
        AssertOperation(capture, "prune", "exception");

        // Neither the message nor the stack trace reaches any signal.
        var emitted = activity.TagObjects.Concat(recorded.Tags).Concat(capture.Measurements.SelectMany(m => m.Tags))
            .SelectMany(tag => new[] { tag.Key, Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) })
            .Append(activity.DisplayName)
            .Append(recorded.Name);
        Assert.DoesNotContain(emitted, text => text is not null && text.Contains(sentinel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnExceptionThatAnAwaitedBodyThrowsIsRecordedToo()
    {
        using var capture = new TelemetryCapture();

        await Assert.ThrowsAsync<TimeoutException>(
            async () => await StoreTelemetry.RunAsync<int>("search", async _ =>
            {
                await Task.Yield();
                throw new TimeoutException();
            }, _ => null));

        var activity = Assert.Single(capture.Activities);
        Assert.Equal("System.TimeoutException", activity.GetTagItem("error.type"));
        Assert.Equal([new("exception.type", "System.TimeoutException")], Assert.Single(activity.Events).Tags);
        AssertOperation(capture, "search", "exception");
    }

    [Fact]
    public async Task WithoutAListenerTheBodyStillRuns()
    {
        Activity? seen = new("placeholder");

        var result = await StoreTelemetry.RunAsync(
            "get",
            activity =>
            {
                seen = activity;
                return ValueTask.FromResult("done");
            },
            _ => MnemosyneErrorCode.NotFound);

        Assert.Equal("done", result);
        Assert.Null(seen);
    }

    [Fact]
    public void ResultsAdaptToAnErrorCode()
    {
        Assert.Null(StoreTelemetry.ErrorOf(Result<int, MnemosyneError>.Success(1)));
        Assert.Equal(
            MnemosyneErrorCode.NotFound,
            StoreTelemetry.ErrorOf(Result<int, MnemosyneError>.Failure(new MnemosyneError(MnemosyneErrorCode.NotFound, "x"))));
        Assert.Null(StoreTelemetry.ErrorOf(UnitResult<MnemosyneError>.Success()));
        Assert.Equal(
            MnemosyneErrorCode.Unavailable,
            StoreTelemetry.ErrorOf(UnitResult<MnemosyneError>.Failure(new MnemosyneError(MnemosyneErrorCode.Unavailable, "x"))));
    }

    [Theory]
    [InlineData(MnemosyneErrorCode.Validation, "Validation")]
    [InlineData(MnemosyneErrorCode.NotFound, "NotFound")]
    [InlineData(MnemosyneErrorCode.Conflict, "Conflict")]
    [InlineData(MnemosyneErrorCode.Unavailable, "Unavailable")]
    [InlineData((MnemosyneErrorCode)99, "99")]
    public void AnErrorCodeIsTaggedByItsName(MnemosyneErrorCode code, string name) =>
        Assert.Equal(name, StoreTelemetry.ErrorName(code));

    [Theory]
    [InlineData(ReasonletCaptureKind.NewReasonlet, "NewReasonlet")]
    [InlineData(ReasonletCaptureKind.NewVersion, "NewVersion")]
    [InlineData(ReasonletCaptureKind.ExecutionOnly, "ExecutionOnly")]
    [InlineData((ReasonletCaptureKind)0, "0")]
    public void ACaptureKindIsTaggedByItsName(ReasonletCaptureKind kind, string name) =>
        Assert.Equal(name, StoreTelemetry.CaptureKindName(kind));

    [Fact]
    public async Task AStoreFailureIsAnOperationWithItsCode()
    {
        using var capture = new TelemetryCapture();
        var store = new ReasonletPostgreSqlStore(_unreachable, new MnemosynePostgreSqlOptions { Dimensions = 3 });

        var result = await store.AppendFeedbackAsync(new ReasonletScope("s"), ExecutionId.New(), new ReasonletFeedback(0.5, 0.5, 0.5), Ct);

        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
        var activity = Assert.Single(capture.Activities);
        Assert.Equal("mnemosyne.feedback.append", activity.OperationName);
        Assert.Equal("Unavailable", activity.GetTagItem("error.type"));
        AssertOperation(capture, "feedback.append", "Unavailable");
    }

    [Fact]
    public async Task ANullArgumentIsAnExceptionOfTheOperation()
    {
        using var capture = new TelemetryCapture();
        var store = new ReasonletPostgreSqlStore(_unreachable, new MnemosynePostgreSqlOptions { Dimensions = 3 });

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.RememberAsync(null!, Ct));

        var activity = Assert.Single(capture.Activities);
        Assert.Equal("mnemosyne.remember", activity.OperationName);
        Assert.Equal("System.ArgumentNullException", activity.GetTagItem("error.type"));
        Assert.Null(activity.GetTagItem("mnemosyne.capture_kind"));
        AssertOperation(capture, "remember", "exception");
        Assert.Empty(capture.Of("mnemosyne.reasonlets.remembered"));
    }

    [Fact]
    public async Task CancellationIsAnExceptionAndIsRethrown()
    {
        using var capture = new TelemetryCapture();
        var store = new ReasonletPostgreSqlStore(_unreachable, new MnemosynePostgreSqlOptions { Dimensions = 3 });
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.MigrateAsync(cancelled.Token));

        var activity = Assert.Single(capture.Activities);
        Assert.Equal("mnemosyne.migrate", activity.OperationName);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        var recorded = Assert.Single(activity.Events);
        Assert.Equal("exception", recorded.Name);
        var type = Assert.Single(recorded.Tags);
        Assert.Equal("exception.type", type.Key);
        Assert.IsType<string>(type.Value);
        Assert.EndsWith("CanceledException", (string)type.Value!, StringComparison.Ordinal);
        Assert.Equal(type.Value, activity.GetTagItem("error.type"));
        AssertOperation(capture, "migrate", "exception");
    }

    [Fact]
    public async Task AFailedPruneStillCountsWhatItCommitted()
    {
        // Nothing was committed against an unreachable server, so the counts are zero, but they are reported.
        using var capture = new TelemetryCapture();
        var store = new ReasonletPostgreSqlStore(_unreachable, new MnemosynePostgreSqlOptions { Dimensions = 3 });

        var result = await store.PruneAsync(new ReasonletRetention { ExecutionsOlderThan = TimeSpan.FromDays(1) }, Ct);

        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
        var activity = Assert.Single(capture.Activities);
        Assert.Equal(0, activity.GetTagItem("mnemosyne.pruned.reasonlets"));
        Assert.Equal(0, activity.GetTagItem("mnemosyne.pruned.executions"));
        Assert.Equal(0, Assert.Single(capture.Of("mnemosyne.pruned.reasonlets")).Value);
        Assert.Equal(0, Assert.Single(capture.Of("mnemosyne.pruned.executions")).Value);
    }

    public ValueTask DisposeAsync() => _unreachable.DisposeAsync();

    /// <summary>Asserts one operation count and one duration, with the tags the spec gives them.</summary>
    private static void AssertOperation(TelemetryCapture capture, string operation, string outcome)
    {
        var count = Assert.Single(capture.Of("mnemosyne.operations"));
        Assert.Equal(1, count.Value);
        Assert.Equal(
            [new("mnemosyne.operation", operation), new("mnemosyne.outcome", outcome)],
            count.Tags.OrderBy(tag => tag.Key, StringComparer.Ordinal));
        var duration = Assert.Single(capture.Of("mnemosyne.operation.duration"));
        Assert.True(duration.Value >= 0);
        Assert.Equal([new KeyValuePair<string, object?>("mnemosyne.operation", operation)], duration.Tags);
    }
}
