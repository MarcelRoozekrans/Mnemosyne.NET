using System.Diagnostics;
using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql.Tests;
using Mnemosyne.NET.Tests.Core;
using Xunit;
using Result = ZeroAlloc.Results.Result<Mnemosyne.NET.Abstractions.ReasonletReceipt, Mnemosyne.NET.Abstractions.MnemosyneError>;

namespace Mnemosyne.NET.Tests.Chat;

/// <summary>The signals the capture processor emits around each item, and the queue counter.</summary>
/// <remarks>The class shares the recall telemetry's non-parallel collection, so its capture hears only its own trace.</remarks>
[Collection(nameof(RecallTelemetryCollection))]
public sealed class CaptureTelemetryTests
{
    private const string Run = "mnemosyne.capture.run";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ReasonletCaptureKind.NewReasonlet, "new_reasonlet")]
    [InlineData(ReasonletCaptureKind.NewVersion, "new_version")]
    [InlineData(ReasonletCaptureKind.ExecutionOnly, "execution_only")]
    [InlineData((ReasonletCaptureKind)99, "99")]
    public async Task AStoredCaptureIsCountedByKind(ReasonletCaptureKind kind, string outcome)
    {
        using var capture = new TelemetryCapture();

        await Process(CaptureTestData.Memory(kind), CaptureTestData.Item());

        var activity = Single(capture);
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        Assert.Equal([new("mnemosyne.outcome", outcome)], AllTags(activity));
        AssertRun(capture, [new("mnemosyne.outcome", outcome)]);
    }

    [Theory]
    [InlineData(true, "no_question")]
    [InlineData(false, "no_tool_calls")]
    public async Task ASkipCarriesItsReason(bool noQuestion, string reason)
    {
        using var capture = new TelemetryCapture();
        IReadOnlyList<ChatMessage> messages = noQuestion
            ? [new ChatMessage(ChatRole.Assistant, "hi")]
            : [new ChatMessage(ChatRole.User, "q")];

        await Process(CaptureTestData.Memory(), CaptureTestData.Item(messages));

        var activity = Single(capture);
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        Assert.Equal([new("mnemosyne.outcome", "skipped"), new("mnemosyne.skip_reason", reason)], AllTags(activity));
        AssertRun(capture, [new("mnemosyne.outcome", "skipped"), new("mnemosyne.skip_reason", reason)]);
    }

    [Fact]
    public async Task AnErrorResultIsCountedByCode()
    {
        var memory = new FakeReasonletMemory
        {
            RememberHandler = (_, _) => Task.FromResult(Result.Failure(new MnemosyneError(MnemosyneErrorCode.Blocked, "no"))),
        };
        using var capture = new TelemetryCapture();

        await Process(memory, CaptureTestData.Item());

        var activity = Single(capture);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal([new("error.type", "Blocked"), new("mnemosyne.outcome", "Blocked")], AllTags(activity));
        AssertRun(capture, [new("mnemosyne.outcome", "Blocked")]);
    }

    [Fact]
    public async Task AnExceptionRecordsOnlyTheType()
    {
        var memory = new FakeReasonletMemory { RememberHandler = (_, _) => throw new HttpRequestException("refused") };
        using var capture = new TelemetryCapture();

        await Process(memory, CaptureTestData.Item());

        var activity = Single(capture);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(
            [new("error.type", "System.Net.Http.HttpRequestException"), new("mnemosyne.outcome", "exception")],
            AllTags(activity));
        var recorded = Assert.Single(activity.Events);
        Assert.Equal("exception", recorded.Name);
        Assert.Equal([new KeyValuePair<string, object?>("exception.type", "System.Net.Http.HttpRequestException")], recorded.Tags);
        AssertRun(capture, [new("error.type", "System.Net.Http.HttpRequestException"), new("mnemosyne.outcome", "exception")]);
    }

    [Theory]
    [InlineData(CaptureTelemetry.EnqueuedOutcome)]
    [InlineData(CaptureTelemetry.DroppedFullOutcome)]
    [InlineData(CaptureTelemetry.DroppedShutdownOutcome)]
    public void TheQueueIsCountedByOutcome(string outcome)
    {
        using var capture = new TelemetryCapture();

        CaptureTelemetry.Queue(outcome);

        var count = Assert.Single(capture.Of("mnemosyne.capture.queue"));
        Assert.Equal(1, count.Value);
        Assert.Equal([new KeyValuePair<string, object?>("mnemosyne.outcome", outcome)], count.Tags);
    }

    [Fact]
    public void TheQueueOutcomesHaveTheirNames()
    {
        Assert.Equal("enqueued", CaptureTelemetry.EnqueuedOutcome);
        Assert.Equal("dropped_full", CaptureTelemetry.DroppedFullOutcome);
        Assert.Equal("dropped_shutdown", CaptureTelemetry.DroppedShutdownOutcome);
    }

    [Fact]
    public async Task NoSignalCarriesContent()
    {
        const string Canary = "canary-7f3a91";
        ChatMessage[] messages =
        [
            new(ChatRole.User, $"question {Canary}"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", $"tool.{Canary}", new Dictionary<string, object?> { [$"arg{Canary}"] = 1 })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", $"result {Canary}")]),
            new(ChatRole.Assistant, $"answer {Canary}"),
        ];
        var item = CaptureTestData.Item(messages) with
        {
            Scope = new ReasonletScope($"scope-{Canary}"),
            SubjectId = $"subject-{Canary}",
            ResponseId = $"resp-{Canary}",
        };
        var throwing = new FakeReasonletMemory { RememberHandler = (_, _) => throw new InvalidDataException(Canary) };
        var failing = new FakeReasonletMemory
        {
            RememberHandler = (_, _) => Task.FromResult(Result.Failure(new MnemosyneError(MnemosyneErrorCode.Unavailable, Canary, Canary))),
        };
        using var telemetry = new TelemetryCapture();

        await Process(CaptureTestData.Memory(), item);
        await Process(throwing, item);
        await Process(failing, item);
        await Process(CaptureTestData.Memory(), item with { Messages = [new ChatMessage(ChatRole.User, $"question {Canary}")] });

        Assert.Equal(4, telemetry.Activities.Count);
        var texts = new List<string>();
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

        Assert.All(texts, text => Assert.DoesNotContain(Canary, text, StringComparison.Ordinal));
    }

    private static async Task Process(FakeReasonletMemory memory, ReasonletCaptureItem item) =>
        await new ReasonletCaptureProcessor(memory, new ReasonletCaptureOptions()).ProcessAsync(item, Ct);

    private static Activity Single(TelemetryCapture capture)
    {
        var activity = Assert.Single(capture.Activities);
        Assert.Equal(Run, activity.OperationName);
        Assert.Equal(MnemosyneTelemetry.ActivitySourceName, activity.Source.Name);
        return activity;
    }

    private static KeyValuePair<string, object?>[] AllTags(Activity activity) =>
        [.. activity.TagObjects.OrderBy(tag => tag.Key, StringComparer.Ordinal)];

    /// <summary>Asserts one run count with these tags and one duration with none.</summary>
    private static void AssertRun(TelemetryCapture capture, KeyValuePair<string, object?>[] tags)
    {
        var runs = Assert.Single(capture.Of("mnemosyne.capture.runs"));
        Assert.Equal(1, runs.Value);
        Assert.Equal(tags, runs.Tags.OrderBy(tag => tag.Key, StringComparer.Ordinal));
        var duration = Assert.Single(capture.Of("mnemosyne.capture.duration"));
        Assert.True(duration.Value > 0);
        Assert.Empty(duration.Tags);
    }
}
