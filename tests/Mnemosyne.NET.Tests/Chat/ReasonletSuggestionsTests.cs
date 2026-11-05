using System.Diagnostics;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql.Tests;
using Mnemosyne.NET.Tests.Core;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.Chat;

/// <summary>The public recall step that every integration shares, and the signals it emits.</summary>
/// <remarks>The class shares the recall telemetry's non-parallel collection, because it asserts the recall counter.</remarks>
[Collection(nameof(RecallTelemetryCollection))]
public sealed class ReasonletSuggestionsTests
{
    private const string Recalls = "mnemosyne.chat.recalls";
    private const string Integration = "mnemosyne.integration";

    private static readonly ReasonletScope Scope = new("support");

    private readonly FakeReasonletMemory _memory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AMatchIsSuggestedWithItsIdAndText()
    {
        var match = CaptureTestData.Match();
        _memory.Returns(match);
        using var telemetry = new TelemetryCapture();

        var suggestion = await RecallAsync();

        Assert.Equal(new ReasonletSuggestion(match.Reasonlet.Id, ReasonletPromptFormatter.Format(match)), suggestion);
        var query = Assert.Single(_memory.Queries);
        Assert.Equal(Scope, query.Scope);
        Assert.Equal(CaptureTestData.Question, query.Question);
        Assert.Equal(1, query.Top);
        AssertRecall(telemetry, "injected");
    }

    [Fact]
    public async Task ACustomFormatterAndFloorAreUsed()
    {
        _memory.Returns(CaptureTestData.Match());

        var suggestion = await RecallAsync(new ReasonletSuggestionOptions { Formatter = m => $"use {m.Reasonlet.Name}", MinSimilarity = 0.7 });

        Assert.Equal($"use {CaptureTestData.Match().Reasonlet.Name}", suggestion!.Text);
        Assert.Equal(0.7, Assert.Single(_memory.Queries).MinSimilarity);
    }

    [Fact]
    public async Task InvalidOptionsThrowAnArgumentException()
    {
        var timeout = await Assert.ThrowsAsync<ArgumentException>(
            async () => await RecallAsync(new ReasonletSuggestionOptions { RecallTimeout = TimeSpan.FromSeconds(-2) }));
        var clock = await Assert.ThrowsAsync<ArgumentException>(
            async () => await RecallAsync(new ReasonletSuggestionOptions { TimeProvider = null! }));
        var floor = await Assert.ThrowsAsync<ArgumentException>(
            async () => await RecallAsync(new ReasonletSuggestionOptions { MinSimilarity = 2 }));

        Assert.Equal("options", timeout.ParamName);
        Assert.Equal("options", clock.ParamName);
        Assert.Equal("options", floor.ParamName);
        Assert.Empty(_memory.Queries);
    }

    [Fact]
    public async Task NoMatchSuggestsNothing()
    {
        using var telemetry = new TelemetryCapture();

        Assert.Null(await RecallAsync());

        AssertRecall(telemetry, "no_match");
    }

    [Fact]
    public async Task AnErrorResultSuggestsNothing()
    {
        _memory.Fails(new MnemosyneError(MnemosyneErrorCode.Unavailable, "down"));
        using var telemetry = new TelemetryCapture();

        Assert.Null(await RecallAsync());

        AssertRecall(telemetry, "Unavailable");
    }

    [Fact]
    public async Task AnExceptionSuggestsNothing()
    {
        _memory.Throws(new HttpRequestException("refused"));
        using var telemetry = new TelemetryCapture();

        Assert.Null(await RecallAsync());

        AssertRecall(telemetry, "exception");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACancellationThatIsNeitherTheCallersNorTheTimeoutIsAnException(bool infinite)
    {
        _memory.Throws(new OperationCanceledException("own"));
        using var telemetry = new TelemetryCapture();

        Assert.Null(await RecallAsync(new ReasonletSuggestionOptions { RecallTimeout = infinite ? Timeout.InfiniteTimeSpan : TimeSpan.FromMinutes(5) }));

        AssertRecall(telemetry, "exception");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFormatterFailureSuggestsNothing(bool throws)
    {
        _memory.Returns(CaptureTestData.Match());
        using var telemetry = new TelemetryCapture();
        Func<ReasonletMatch, string> formatter = throws ? _ => throw new FormatException("bad") : _ => "  ";

        Assert.Null(await RecallAsync(new ReasonletSuggestionOptions { Formatter = formatter }));

        AssertRecall(telemetry, "formatter_failed");
    }

    [Fact]
    public async Task ATimeoutSuggestsNothing()
    {
        _memory.RecallHandler = (_, _) => new TaskCompletionSource<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>>().Task;
        var clock = new ManualTimeProvider();
        using var telemetry = new TelemetryCapture();

        var call = RecallAsync(new ReasonletSuggestionOptions { RecallTimeout = TimeSpan.FromSeconds(2), TimeProvider = clock }).AsTask();
        clock.Fire();

        Assert.Null(await call.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        Assert.Equal([TimeSpan.FromSeconds(2)], clock.DueTimes.Take(1));
        AssertRecall(telemetry, "timeout");
    }

    [Fact]
    public async Task TimeoutAbandonsAMemoryThatIgnoresTheToken()
    {
        var never = new TaskCompletionSource<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>>();
        _memory.RecallHandler = (_, _) => never.Task;
        var clock = new ManualTimeProvider();

        var call = RecallAsync(new ReasonletSuggestionOptions { TimeProvider = clock }).AsTask();
        Assert.False(call.IsCompleted);
        clock.Fire();

        Assert.Null(await call.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        Assert.False(never.Task.IsCompleted);
    }

    [Fact]
    public async Task AnInfiniteTimeoutStartsNoTimer()
    {
        _memory.Returns(CaptureTestData.Match());
        var clock = new ManualTimeProvider();

        Assert.NotNull(await RecallAsync(new ReasonletSuggestionOptions { RecallTimeout = Timeout.InfiniteTimeSpan, TimeProvider = clock }));

        Assert.Empty(clock.DueTimes);
    }

    [Fact]
    public async Task CallerCancellationIsRethrown()
    {
        using var cts = new CancellationTokenSource();
        _memory.RecallHandler = (_, _) =>
        {
            cts.Cancel();
            return new TaskCompletionSource<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>>().Task;
        };
        using var telemetry = new TelemetryCapture();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ReasonletSuggestions.RecallAsync(_memory, Scope, CaptureTestData.Question, new ReasonletSuggestionOptions(), "chat", cts.Token)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(30), Ct));

        Assert.Empty(telemetry.Of(Recalls));
        var activity = Assert.Single(telemetry.Activities);
        Assert.NotEqual(ActivityStatusCode.Error, activity.Status);
    }

    [Fact]
    public async Task RecallTagsTheIntegration()
    {
        _memory.Returns(CaptureTestData.Match());
        using var telemetry = new TelemetryCapture();

        await RecallAsync(integration: ReasonletSuggestions.AgentIntegration);

        var activity = Assert.Single(telemetry.Activities);
        Assert.Equal("agent", activity.GetTagItem(Integration));
        Assert.Equal("agent", Assert.Single(telemetry.Of(Recalls)).Tag(Integration));
    }

    [Fact]
    public void CountSkippedTagsReasonAndIntegration()
    {
        using var telemetry = new TelemetryCapture();

        ReasonletSuggestions.CountSkipped(ReasonletSuggestions.ContinuationReason, ReasonletSuggestions.AgentIntegration);

        var count = Assert.Single(telemetry.Of(Recalls));
        Assert.Equal(1, count.Value);
        Assert.Equal("skipped", count.Tag("mnemosyne.outcome"));
        Assert.Equal("continuation", count.Tag("mnemosyne.skip_reason"));
        Assert.Equal("agent", count.Tag(Integration));
        Assert.Empty(telemetry.Activities);
    }

    [Fact]
    public void CountFailedTagsExceptionTypeAndIntegration()
    {
        using var telemetry = new TelemetryCapture();

        ReasonletSuggestions.CountFailed(new InvalidDataException("secret"), ReasonletSuggestions.AgentIntegration);

        var count = Assert.Single(telemetry.Of(Recalls));
        Assert.Equal(1, count.Value);
        Assert.Equal("exception", count.Tag("mnemosyne.outcome"));
        Assert.Equal("System.IO.InvalidDataException", count.Tag("error.type"));
        Assert.Equal("agent", count.Tag(Integration));
        var activity = Assert.Single(telemetry.Activities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("agent", activity.GetTagItem(Integration));
    }

    [Fact]
    public void TheConstantsAreTheDocumentedValues()
    {
        Assert.Equal("chat", ReasonletSuggestions.ChatIntegration);
        Assert.Equal("agent", ReasonletSuggestions.AgentIntegration);
        Assert.Equal("no_scope", ReasonletSuggestions.NoScopeReason);
        Assert.Equal("continuation", ReasonletSuggestions.ContinuationReason);
    }

    [Fact]
    public void OptionsDefaultToTheChatDefaults()
    {
        var options = new ReasonletSuggestionOptions();

        Assert.Null(options.MinSimilarity);
        Assert.Equal(TimeSpan.FromSeconds(5), options.RecallTimeout);
        Assert.Null(options.Formatter);
        Assert.Same(TimeProvider.System, options.TimeProvider);
        Assert.True(options.Validate().IsSuccess);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(-0.1)]
    public void MinSimilarityOutsideUnitIntervalFails(double value)
    {
        var result = new ReasonletSuggestionOptions { MinSimilarity = value }.Validate();

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.StartsWith("MinSimilarity:", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARecallTimeoutOutOfRangeFails()
    {
        foreach (var bad in new[] { TimeSpan.Zero, TimeoutCheck.Max + TimeSpan.FromMilliseconds(1) })
        {
            var result = new ReasonletSuggestionOptions { RecallTimeout = bad }.Validate();

            Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
            Assert.StartsWith("RecallTimeout:", result.Error.Message, StringComparison.Ordinal);
        }

        Assert.True(new ReasonletSuggestionOptions { RecallTimeout = Timeout.InfiniteTimeSpan }.Validate().IsSuccess);
        Assert.True(new ReasonletSuggestionOptions { RecallTimeout = TimeoutCheck.Max }.Validate().IsSuccess);
    }

    [Fact]
    public void ANullTimeProviderFails()
    {
        var result = new ReasonletSuggestionOptions { TimeProvider = null! }.Validate();

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.StartsWith("TimeProvider:", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecallRejectsNullArguments()
    {
        var options = new ReasonletSuggestionOptions();
        await Assert.ThrowsAsync<ArgumentNullException>("memory", async () => await ReasonletSuggestions.RecallAsync(null!, Scope, "q", options, "chat", Ct));
        await Assert.ThrowsAsync<ArgumentNullException>("question", async () => await ReasonletSuggestions.RecallAsync(_memory, Scope, null!, options, "chat", Ct));
        await Assert.ThrowsAsync<ArgumentNullException>("options", async () => await ReasonletSuggestions.RecallAsync(_memory, Scope, "q", null!, "chat", Ct));
        await Assert.ThrowsAsync<ArgumentNullException>("integration", async () => await ReasonletSuggestions.RecallAsync(_memory, Scope, "q", options, null!, Ct));
        Assert.Empty(_memory.Queries);
    }

    [Fact]
    public void CountMethodsRejectNullArguments()
    {
        Assert.Throws<ArgumentNullException>("reason", () => ReasonletSuggestions.CountSkipped(null!, "chat"));
        Assert.Throws<ArgumentNullException>("integration", () => ReasonletSuggestions.CountSkipped("no_scope", null!));
        Assert.Throws<ArgumentNullException>("exception", () => ReasonletSuggestions.CountFailed(null!, "chat"));
        Assert.Throws<ArgumentNullException>("integration", () => ReasonletSuggestions.CountFailed(new InvalidOperationException(), null!));
    }

    private ValueTask<ReasonletSuggestion?> RecallAsync(
        ReasonletSuggestionOptions? options = null, string integration = ReasonletSuggestions.ChatIntegration) =>
        ReasonletSuggestions.RecallAsync(_memory, Scope, CaptureTestData.Question, options ?? new ReasonletSuggestionOptions(), integration, Ct);

    private static void AssertRecall(TelemetryCapture telemetry, string outcome)
    {
        var count = Assert.Single(telemetry.Of(Recalls));
        Assert.Equal(1, count.Value);
        Assert.Equal(outcome, count.Tag("mnemosyne.outcome"));
        Assert.Equal("chat", count.Tag(Integration));
        Assert.Equal("chat", Assert.Single(telemetry.Activities).GetTagItem(Integration));
    }
}
