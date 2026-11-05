using System.Diagnostics;
using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql.Tests;
using Mnemosyne.NET.Tests.Core;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Chat;

/// <summary>The <c>mnemosyne.chat.recall</c> activity and the <c>mnemosyne.chat.recalls</c> counter the middleware emits.</summary>
/// <remarks>The class shares the recall telemetry's non-parallel collection, so its capture hears only its own trace.</remarks>
[Collection(nameof(RecallTelemetryCollection))]
public sealed class ChatTelemetryTests
{
    private const string Recall = "mnemosyne.chat.recall";
    private const string Recalls = "mnemosyne.chat.recalls";

    private static readonly ReasonletScope Scope = new("support");

    private readonly FakeReasonletMemory _memory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnInjectIsCounted()
    {
        _memory.Returns(CaptureTestData.Match());
        using var telemetry = new TelemetryCapture();

        await Call(new ReasonletChatOptions { DefaultScope = Scope });

        var activity = Single(telemetry);
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        Assert.Equal([new("mnemosyne.integration", "chat"), new("mnemosyne.outcome", "injected")], Tags(activity));
        AssertCounted(telemetry, [new("mnemosyne.integration", "chat"), new("mnemosyne.outcome", "injected")]);
    }

    [Fact]
    public async Task NoMatchIsCounted()
    {
        using var telemetry = new TelemetryCapture();

        await Call(new ReasonletChatOptions { DefaultScope = Scope });

        var activity = Single(telemetry);
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        Assert.Equal([new("mnemosyne.integration", "chat"), new("mnemosyne.outcome", "no_match")], Tags(activity));
        AssertCounted(telemetry, [new("mnemosyne.integration", "chat"), new("mnemosyne.outcome", "no_match")]);
    }

    [Fact]
    public async Task ATimeoutIsCounted()
    {
        _memory.RecallHandler = (_, _) => new TaskCompletionSource<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>>().Task;
        var clock = new ManualTimeProvider();
        using var telemetry = new TelemetryCapture();

        var call = Call(new ReasonletChatOptions { DefaultScope = Scope }, clock);
        clock.Fire();
        await call.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var activity = Single(telemetry);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal([new("error.type", "timeout"), new("mnemosyne.integration", "chat"), new("mnemosyne.outcome", "timeout")], Tags(activity));
        AssertCounted(telemetry, [new("mnemosyne.integration", "chat"), new("mnemosyne.outcome", "timeout")]);
    }

    [Fact]
    public async Task AnErrorResultIsCountedByCode()
    {
        _memory.Fails(new MnemosyneError(MnemosyneErrorCode.Validation, "bad"));
        using var telemetry = new TelemetryCapture();

        await Call(new ReasonletChatOptions { DefaultScope = Scope });

        var activity = Single(telemetry);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal([new("error.type", "Validation"), new("mnemosyne.integration", "chat"), new("mnemosyne.outcome", "Validation")], Tags(activity));
        AssertCounted(telemetry, [new("mnemosyne.integration", "chat"), new("mnemosyne.outcome", "Validation")]);
    }

    [Fact]
    public async Task AnExceptionRecordsOnlyTheType()
    {
        _memory.Throws(new HttpRequestException("refused"));
        using var telemetry = new TelemetryCapture();

        await Call(new ReasonletChatOptions { DefaultScope = Scope });

        var activity = Single(telemetry);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(
            [new("error.type", "System.Net.Http.HttpRequestException"), new("mnemosyne.integration", "chat"), new("mnemosyne.outcome", "exception")],
            Tags(activity));
        var recorded = Assert.Single(activity.Events);
        Assert.Equal("exception", recorded.Name);
        Assert.Equal([new KeyValuePair<string, object?>("exception.type", "System.Net.Http.HttpRequestException")], recorded.Tags);
        AssertCounted(telemetry, [new("error.type", "System.Net.Http.HttpRequestException"), new("mnemosyne.integration", "chat"), new("mnemosyne.outcome", "exception")]);
    }

    [Fact]
    public async Task AFormatterFailureIsCounted()
    {
        _memory.Returns(CaptureTestData.Match());
        using var telemetry = new TelemetryCapture();

        await Call(new ReasonletChatOptions { DefaultScope = Scope, Formatter = _ => throw new FormatException("bad") });

        var activity = Single(telemetry);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal([new("error.type", "formatter_failed"), new("mnemosyne.integration", "chat"), new("mnemosyne.outcome", "formatter_failed")], Tags(activity));
        Assert.Empty(activity.Events);
        AssertCounted(telemetry, [new("mnemosyne.integration", "chat"), new("mnemosyne.outcome", "formatter_failed")]);
    }

    [Fact]
    public async Task NoScopeIsCountedAsASkip()
    {
        using var telemetry = new TelemetryCapture();

        await Call(new ReasonletChatOptions());

        Assert.Empty(telemetry.Activities);
        AssertCounted(telemetry, [new("mnemosyne.integration", "chat"), new("mnemosyne.outcome", "skipped"), new("mnemosyne.skip_reason", "no_scope")]);
    }

    [Fact]
    public async Task AContinuationIsCountedAsASkip()
    {
        using var telemetry = new TelemetryCapture();

        await Call(new ReasonletChatOptions { DefaultScope = Scope }, messages: [new(ChatRole.Assistant, "go on")]);

        Assert.Empty(telemetry.Activities);
        AssertCounted(telemetry, [new("mnemosyne.integration", "chat"), new("mnemosyne.outcome", "skipped"), new("mnemosyne.skip_reason", "continuation")]);
    }

    [Fact]
    public async Task ACancelledCallerCountsNothingAndIsNotAnError()
    {
        using var cts = new CancellationTokenSource();
        _memory.RecallHandler = async (_, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Success([]);
        };
        using var telemetry = new TelemetryCapture();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Call(new ReasonletChatOptions { DefaultScope = Scope }, ct: cts.Token));

        var activity = Single(telemetry);
        Assert.NotEqual(ActivityStatusCode.Error, activity.Status);
        Assert.Empty(activity.Events);
        Assert.Equal([new("mnemosyne.integration", "chat")], Tags(activity));
        Assert.Empty(telemetry.Of(Recalls));
    }

    [Fact]
    public async Task NoSignalCarriesContent()
    {
        const string Canary = "canary-5b2e70";
        var match = CaptureTestData.Match();
        var scope = new ReasonletScope($"scope-{Canary}");
        var options = new ChatOptions().WithReasonletScope(scope, $"subject-{Canary}");
        ChatMessage[] question = [new(ChatRole.User, $"question {Canary}")];
        using var telemetry = new TelemetryCapture();

        _memory.Returns(match);
        await Call(new ReasonletChatOptions { Formatter = _ => $"inject {Canary}", ExposeFindProcedure = true }, null, question, options);
        await Call(new ReasonletChatOptions { Formatter = _ => throw new FormatException(Canary) }, null, question, options);
        _memory.Fails(new MnemosyneError(MnemosyneErrorCode.Unavailable, Canary, Canary));
        await Call(new ReasonletChatOptions(), null, question, options);
        _memory.Throws(new InvalidDataException(Canary));
        await Call(new ReasonletChatOptions(), null, question, options);
        await Call(new ReasonletChatOptions(), null, [new(ChatRole.Assistant, $"answer {Canary}")], options);

        Assert.Equal(4, telemetry.Activities.Count);
        var texts = new List<string>();
        foreach (var activity in telemetry.Activities)
        {
            texts.Add(activity.DisplayName);
            texts.AddRange(activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"));
            texts.Add(activity.StatusDescription ?? string.Empty);
            foreach (var recorded in activity.Events)
            {
                texts.Add(recorded.Name);
                texts.AddRange(recorded.Tags.Select(tag => $"{tag.Key}={tag.Value}"));
            }
        }

        Assert.Equal(5, telemetry.Of(Recalls).Count);
        foreach (var measurement in telemetry.Measurements)
        {
            texts.Add(measurement.Instrument);
            texts.AddRange(measurement.Tags.Select(tag => $"{tag.Key}={tag.Value}"));
        }

        Assert.All(texts, text => Assert.DoesNotContain(Canary, text, StringComparison.Ordinal));
    }

    private Task<ChatResponse> Call(
        ReasonletChatOptions options,
        TimeProvider? time = null,
        IEnumerable<ChatMessage>? messages = null,
        ChatOptions? chatOptions = null,
        CancellationToken? ct = null) =>
        new ReasonletChatClient(FakeChatClient.Replying("done"), _memory, null, options, time ?? TimeProvider.System)
            .GetResponseAsync(messages ?? [new(ChatRole.User, CaptureTestData.Question)], chatOptions, ct ?? Ct);

    private static Activity Single(TelemetryCapture telemetry)
    {
        var activity = Assert.Single(telemetry.Activities);
        Assert.Equal(Recall, activity.OperationName);
        Assert.Equal(MnemosyneTelemetry.ActivitySourceName, activity.Source.Name);
        return activity;
    }

    private static List<KeyValuePair<string, object?>> Tags(Activity activity) =>
        [.. activity.TagObjects.OrderBy(tag => tag.Key, StringComparer.Ordinal)];

    private static void AssertCounted(TelemetryCapture telemetry, KeyValuePair<string, object?>[] tags)
    {
        var count = Assert.Single(telemetry.Of(Recalls));
        Assert.Equal(1, count.Value);
        Assert.Equal(tags, count.Tags.OrderBy(tag => tag.Key, StringComparer.Ordinal));
    }
}
