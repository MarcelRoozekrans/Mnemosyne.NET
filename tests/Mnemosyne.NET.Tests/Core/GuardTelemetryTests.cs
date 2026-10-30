using System.Diagnostics;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql.Tests;
using Mnemosyne.NET.Tests.Abstractions;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Core;

/// <summary>The signals the facade emits around each capture guard call.</summary>
/// <remarks>
/// Listeners are global. Each test filters what it hears to its own trace through <see cref="TelemetryCapture"/>, and the
/// class runs in the recall telemetry's non-parallel collection, so that the tests without a listener really run with
/// none attached.
/// </remarks>
[Collection(nameof(RecallTelemetryCollection))]
public sealed class GuardTelemetryTests
{
    private const string Guard = "mnemosyne.guard";

    private readonly FakeStore _store = new();
    private readonly FakeSelector _selector = new();
    private readonly FakeEmbeddingGenerator _generator = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ReasonletMemory Memory(ICaptureGuard? guard, MnemosyneOptions? options = null, TimeProvider? clock = null) =>
        new(_store, _selector, _generator, null, null, options ?? new MnemosyneOptions(), clock, guard);

    [Fact]
    public async Task AnAllowTagsTheGuard()
    {
        var guard = new FakeCaptureGuard { Respond = () => CaptureVerdict.Allow("g") };
        using var capture = new TelemetryCapture();

        Assert.True((await Memory(guard).RememberAsync(TestData.Capture(), Ct)).IsSuccess);

        var activity = Single(capture);
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        Assert.Equal([new("mnemosyne.guard", "g"), new("mnemosyne.guard.blocked", false)], AllTags(activity));
        Assert.Empty(activity.Events);
        AssertMeasurements(capture, "allowed", "ok", "g");
    }

    [Fact]
    public async Task ABlockAddsTheEvent()
    {
        var guard = new FakeCaptureGuard { Respond = () => CaptureVerdict.Block("g", "pii.email") };
        using var capture = new TelemetryCapture();

        var result = await Memory(guard).RememberAsync(TestData.Capture(), Ct);
        Assert.True(result.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Blocked, result.Error.Code);

        var activity = Single(capture);
        Assert.Equal(
            [new("mnemosyne.guard", "g"), new("mnemosyne.guard.blocked", true), new("mnemosyne.guard.detector", "pii.email")],
            AllTags(activity));
        var recorded = Assert.Single(activity.Events);
        Assert.Equal("mnemosyne.capture.blocked", recorded.Name);
        Assert.Equal(
            [new("mnemosyne.guard", "g"), new("mnemosyne.guard.detector", "pii.email")],
            recorded.Tags.OrderBy(tag => tag.Key, StringComparer.Ordinal));
        AssertMeasurements(capture, "blocked", "ok", "g");
    }

    [Fact]
    public async Task AnUnavailableGuardIsAFailure()
    {
        var guard = new FakeCaptureGuard
        {
            Respond = () => new MnemosyneError(MnemosyneErrorCode.Unavailable, "down"),
        };
        using var capture = new TelemetryCapture();

        Assert.Equal(MnemosyneErrorCode.Unavailable, (await Memory(guard).RememberAsync(TestData.Capture(), Ct)).Error.Code);

        var activity = Single(capture);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal([new("error.type", "Unavailable")], AllTags(activity));
        Assert.Empty(activity.Events);
        AssertMeasurements(capture, "Unavailable", "Unavailable", guard: null);
    }

    [Fact]
    public async Task AThrowingGuardRecordsOnlyTheType()
    {
        var guard = new FakeCaptureGuard { Respond = () => throw new HttpRequestException("refused") };
        using var capture = new TelemetryCapture();

        Assert.Equal(MnemosyneErrorCode.Unavailable, (await Memory(guard).RememberAsync(TestData.Capture(), Ct)).Error.Code);

        var activity = Single(capture);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal([new("error.type", "System.Net.Http.HttpRequestException")], AllTags(activity));
        var recorded = Assert.Single(activity.Events);
        Assert.Equal("exception", recorded.Name);
        Assert.Equal([new KeyValuePair<string, object?>("exception.type", "System.Net.Http.HttpRequestException")], recorded.Tags);
        AssertMeasurements(capture, "exception", "exception", guard: null);
    }

    [Fact]
    public async Task ATimeoutIsUnavailable()
    {
        var clock = new ManualTimeProvider();
        var guard = new FakeCaptureGuard { Hang = true, OnCall = clock.Fire };
        using var capture = new TelemetryCapture();

        var result = await Memory(guard, new MnemosyneOptions { GuardTimeout = TimeSpan.FromSeconds(1) }, clock)
            .RememberAsync(TestData.Capture(), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
        var activity = Single(capture);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal([new("error.type", "Unavailable")], AllTags(activity));
        Assert.Empty(activity.Events);
        AssertMeasurements(capture, "Unavailable", "Unavailable", guard: null);
    }

    [Fact]
    public async Task NoSignalOrErrorCarriesCaptureText()
    {
        const string Canary = "canary-7f3a91";
        var capture = TestData.Capture() with
        {
            Question = $"question {Canary}",
            Answer = $"answer {Canary}",
            Intent = $"intent {Canary}",
            Name = $"name {Canary}",
        };
        var errors = new List<MnemosyneError>();
        using var telemetry = new TelemetryCapture();

        var throwing = new FakeCaptureGuard { Respond = () => throw new InvalidDataException(Canary) };
        errors.Add((await Memory(throwing).RememberAsync(capture, Ct)).Error);
        var unavailable = new FakeCaptureGuard
        {
            Respond = () => new MnemosyneError(MnemosyneErrorCode.Unavailable, $"guard said {Canary}", $"detail {Canary}"),
        };
        errors.Add((await Memory(unavailable).RememberAsync(capture, Ct)).Error);

        var blocking = new FakeCaptureGuard { Respond = () => CaptureVerdict.Block("g", "pii.email") };
        var blocked = (await Memory(blocking).RememberAsync(capture, Ct)).Error;
        Assert.Equal(MnemosyneErrorCode.Blocked, blocked.Code);
        errors.Add(blocked);

        var texts = new List<string>();
        var buggy = new FakeCaptureGuard
        {
            Respond = () => new MnemosyneError(MnemosyneErrorCode.Validation, $"guard said {Canary}", $"detail {Canary}"),
        };
        var bug = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Memory(buggy).RememberAsync(capture, Ct));
        texts.Add(bug.Message);

        Assert.Equal(4, telemetry.Activities.Count);
        foreach (var activity in telemetry.Activities)
        {
            texts.AddRange(activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"));
            texts.Add(activity.StatusDescription ?? string.Empty);
            foreach (var recorded in activity.Events)
            {
                texts.Add(recorded.Name);
                texts.AddRange(recorded.Tags.Select(tag => $"{tag.Key}={tag.Value}"));
            }
        }

        Assert.NotEmpty(telemetry.Measurements);
        foreach (var measurement in telemetry.Measurements)
        {
            texts.Add(measurement.Instrument);
            texts.AddRange(measurement.Tags.Select(tag => $"{tag.Key}={tag.Value}"));
        }

        foreach (var error in errors)
        {
            texts.Add(error.Message);
            texts.Add(error.Detail ?? string.Empty);
        }

        Assert.All(texts, text => Assert.DoesNotContain(Canary, text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnInvalidVerdictIsRecordedAsAnException()
    {
        var guard = new FakeCaptureGuard { Respond = () => CaptureVerdict.Block("g", " ") };
        using var capture = new TelemetryCapture();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Memory(guard).RememberAsync(TestData.Capture(), Ct));

        var activity = Single(capture);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal([new("error.type", "System.InvalidOperationException")], AllTags(activity));
        AssertMeasurements(capture, "exception", "exception", guard: null);
    }

    [Fact]
    public async Task ACancelledCallerIsAnException()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var before = new TelemetryCapture();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await Memory(new FakeCaptureGuard()).RememberAsync(TestData.Capture(), cancelled.Token));

        var activity = Single(before);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.DoesNotContain(AllTags(activity), tag => tag.Key == "mnemosyne.guard");
        AssertMeasurements(before, "exception", "exception", guard: null);

        using var cts = new CancellationTokenSource();
        var hanging = new FakeCaptureGuard { Hang = true };
        using var during = new TelemetryCapture();
        var pending = Memory(hanging, clock: new ManualTimeProvider()).RememberAsync(TestData.Capture(), cts.Token).AsTask();
        Assert.Equal(1, hanging.Calls);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(30), Ct));

        var waited = Single(during);
        Assert.Equal(ActivityStatusCode.Error, waited.Status);
        Assert.DoesNotContain(AllTags(waited), tag => tag.Key == "mnemosyne.guard");
        AssertMeasurements(during, "exception", "exception", guard: null);
    }

    [Fact]
    public async Task NoGuardEmitsNothing()
    {
        using var capture = new TelemetryCapture();

        Assert.True((await Memory(guard: null).RememberAsync(TestData.Capture(), Ct)).IsSuccess);

        Assert.DoesNotContain(capture.Activities, activity => activity.OperationName == Guard);
        Assert.Empty(capture.Of("mnemosyne.guard.inspections"));
        Assert.Empty(capture.Of("mnemosyne.guard.duration"));
    }

    /// <summary>The one activity of the capture, which must be a guard call.</summary>
    private static Activity Single(TelemetryCapture capture)
    {
        var activity = Assert.Single(capture.Activities);
        Assert.Equal(Guard, activity.OperationName);
        Assert.Equal(Guard, activity.DisplayName);
        Assert.Equal(MnemosyneTelemetry.ActivitySourceName, activity.Source.Name);
        return activity;
    }

    /// <summary>Every tag of an activity, sorted by key.</summary>
    private static KeyValuePair<string, object?>[] AllTags(Activity activity) =>
        [.. activity.TagObjects.OrderBy(tag => tag.Key, StringComparer.Ordinal)];

    /// <summary>
    /// Asserts one inspection count and duration, tagged with the guard's name when it answered, and one operation count
    /// and duration under operation <c>guard</c>.
    /// </summary>
    private static void AssertMeasurements(TelemetryCapture capture, string outcome, string operationOutcome, string? guard)
    {
        KeyValuePair<string, object?>[] named = guard is null ? [] : [new("mnemosyne.guard", guard)];

        var inspections = Assert.Single(capture.Of("mnemosyne.guard.inspections"));
        Assert.Equal(1, inspections.Value);
        Assert.Equal(
            [.. named, new("mnemosyne.outcome", outcome)],
            inspections.Tags.OrderBy(tag => tag.Key, StringComparer.Ordinal));
        var inspectionDuration = Assert.Single(capture.Of("mnemosyne.guard.duration"));
        Assert.True(inspectionDuration.Value > 0);
        Assert.Equal(named, inspectionDuration.Tags);

        var count = Assert.Single(capture.Of("mnemosyne.operations"));
        Assert.Equal(1, count.Value);
        Assert.Equal(
            [new("mnemosyne.operation", "guard"), new("mnemosyne.outcome", operationOutcome)],
            count.Tags.OrderBy(tag => tag.Key, StringComparer.Ordinal));
        var duration = Assert.Single(capture.Of("mnemosyne.operation.duration"));
        Assert.True(duration.Value > 0);
        Assert.Equal([new KeyValuePair<string, object?>("mnemosyne.operation", "guard")], duration.Tags);
    }
}
