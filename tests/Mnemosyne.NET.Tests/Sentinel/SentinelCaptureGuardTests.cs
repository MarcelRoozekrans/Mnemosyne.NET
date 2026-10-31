using AI.Sentinel;
using AI.Sentinel.Detection;
using AI.Sentinel.Domain;
using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.Sentinel;
using Mnemosyne.NET.Tests.Abstractions;
using Xunit;

namespace Mnemosyne.NET.Tests.Sentinel;

public sealed class SentinelCaptureGuardTests
{
    private const string Canary = "canary-7f3a";

    private static DetectionResult Detection(string id, Severity severity, string reason = "reason") =>
        DetectionResult.WithSeverity(new DetectorId(id), severity, reason);

    private static PipelineResult Result(params DetectionResult[] detections) => new(new ThreatRiskScore(50), detections);

    private static ReasonletCapture Capture() =>
        TestData.Capture() with { Question = "the question", Intent = "the intent", Name = "the name", Answer = null };

    private static async Task<CaptureVerdict> Inspect(
        FakeDetectionPipeline pipeline,
        ReasonletCapture? capture = null,
        SentinelCaptureGuardOptions? options = null,
        SentinelOptions? sentinelOptions = null,
        CancellationToken? ct = null)
    {
        var guard = new SentinelCaptureGuard(pipeline, sentinelOptions ?? new SentinelOptions(), options);
        var result = await guard.InspectAsync(capture ?? Capture(), ct ?? TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : "");
        return result.Value;
    }

    [Fact]
    public async Task CleanResultAllows()
    {
        var verdict = await Inspect(new FakeDetectionPipeline());

        Assert.Equal(CaptureVerdict.Allow("ai-sentinel"), verdict);
        Assert.True(verdict.Validate().IsSuccess);
    }

    [Fact]
    public async Task DetectionBelowThresholdAllows()
    {
        var verdict = await Inspect(new FakeDetectionPipeline { Result = Result(Detection("SEC-01", Severity.Medium)) });

        Assert.Equal(CaptureVerdict.Allow("ai-sentinel"), verdict);
        Assert.True(verdict.Validate().IsSuccess);
    }

    [Fact]
    public async Task DetectionAtThresholdBlocks()
    {
        var verdict = await Inspect(new FakeDetectionPipeline { Result = Result(Detection("SEC-02", Severity.High)) });

        Assert.Equal(CaptureVerdict.Block("ai-sentinel", "SEC-02"), verdict);
        Assert.True(verdict.Validate().IsSuccess);
    }

    [Fact]
    public async Task DetectionAboveThresholdBlocks()
    {
        var verdict = await Inspect(new FakeDetectionPipeline { Result = Result(Detection("SEC-03", Severity.Critical)) });

        Assert.Equal(CaptureVerdict.Block("ai-sentinel", "SEC-03"), verdict);
        Assert.True(verdict.Validate().IsSuccess);
    }

    [Fact]
    public async Task NoneSeverityNeverBlocks()
    {
        var verdict = await Inspect(
            new FakeDetectionPipeline { Result = Result(Detection("SEC-04", Severity.None)) },
            options: new SentinelCaptureGuardOptions { BlockAtSeverity = Severity.Low });

        Assert.Equal(CaptureVerdict.Allow("ai-sentinel"), verdict);
        Assert.True(verdict.Validate().IsSuccess);
    }

    [Fact]
    public async Task HighestBlockingDetectionIsNamed()
    {
        var verdict = await Inspect(new FakeDetectionPipeline
        {
            Result = Result(Detection("A", Severity.High), Detection("B", Severity.Critical), Detection("C", Severity.Critical)),
        });

        Assert.Equal(CaptureVerdict.Block("ai-sentinel", "B"), verdict);
        Assert.True(verdict.Validate().IsSuccess);
    }

    [Fact]
    public async Task MessagesAreQuestionIntentNameInOrder()
    {
        var pipeline = new FakeDetectionPipeline();
        var capture = Capture() with { Answer = Canary };

        await Inspect(pipeline, capture);

        var messages = pipeline.LastContext!.Messages;
        Assert.Equal(3, messages.Count);
        Assert.All(messages, m => Assert.Equal(ChatRole.User, m.Role));
        Assert.Equal(["the question", "the intent", "the name"], messages.Select(m => m.Text));
        Assert.DoesNotContain(messages, m => m.Text.Contains(Canary, StringComparison.Ordinal));
    }

    [Fact]
    public async Task BlankIntentAndNameAreSkipped()
    {
        var pipeline = new FakeDetectionPipeline();

        await Inspect(pipeline, Capture() with { Intent = null, Name = "  " });

        Assert.Equal(["the question"], pipeline.LastContext!.Messages.Select(m => m.Text));
    }

    [Fact]
    public async Task TagsAreNotScanned()
    {
        var pipeline = new FakeDetectionPipeline();
        var capture = Capture();
        capture = capture with
        {
            Provenance = capture.Provenance with { Tags = new Dictionary<string, string> { ["k"] = Canary } },
        };

        await Inspect(pipeline, capture);

        Assert.DoesNotContain(pipeline.LastContext!.Messages, m => m.Text.Contains(Canary, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SessionIdIsRunId()
    {
        var pipeline = new FakeDetectionPipeline();
        var capture = Capture();

        await Inspect(pipeline, capture with { Provenance = capture.Provenance with { RunId = "run-7" } });

        Assert.Equal("run-7", pipeline.LastContext!.SessionId.Value);
    }

    [Fact]
    public async Task WhitespaceRunIdFallsBack()
    {
        var pipeline = new FakeDetectionPipeline();
        var capture = Capture();

        await Inspect(pipeline, capture with { Provenance = capture.Provenance with { RunId = "  " } });

        Assert.Equal("mnemosyne-capture", pipeline.LastContext!.SessionId.Value);
    }

    [Fact]
    public async Task SenderAndReceiverComeFromSentinelOptions()
    {
        var pipeline = new FakeDetectionPipeline();
        var sentinelOptions = new SentinelOptions { DefaultSenderId = new AgentId("sender-x"), DefaultReceiverId = new AgentId("receiver-y") };

        await Inspect(pipeline, sentinelOptions: sentinelOptions);

        var ctx = pipeline.LastContext!;
        Assert.Equal("sender-x", ctx.SenderId.Value);
        Assert.Equal("receiver-y", ctx.ReceiverId.Value);
        Assert.Empty(ctx.History);
        Assert.Null(ctx.LlmId);
        Assert.Null(ctx.SystemPrompt);
    }

    [Fact]
    public async Task ReasonIsNeverInTheVerdict()
    {
        var verdict = await Inspect(new FakeDetectionPipeline { Result = Result(Detection("SEC-05", Severity.High, Canary)) });

        Assert.DoesNotContain(Canary, verdict.Guard, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, verdict.Detector, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LongDetectorIdIsTruncatedByCodePoints()
    {
        var id = string.Concat(Enumerable.Repeat("😀", 101));

        var verdict = await Inspect(new FakeDetectionPipeline { Result = Result(Detection(id, Severity.High)) });

        Assert.Equal(string.Concat(Enumerable.Repeat("😀", 100)), verdict.Detector);
        Assert.True(verdict.Validate().IsSuccess);
    }

    [Fact]
    public async Task InvalidDetectorIdIsReplaced()
    {
        var verdict = await Inspect(new FakeDetectionPipeline { Result = Result(Detection("bad\uD800id", Severity.High)) });

        Assert.Equal(CaptureVerdict.Block("ai-sentinel", "invalid-detector-id"), verdict);
        Assert.True(verdict.Validate().IsSuccess);
    }

    [Fact]
    public async Task WhitespacePrefixedDetectorIdIsReplaced()
    {
        var id = new string(' ', 100) + "x";

        var verdict = await Inspect(new FakeDetectionPipeline { Result = Result(Detection(id, Severity.High)) });

        Assert.Equal(CaptureVerdict.Block("ai-sentinel", "invalid-detector-id"), verdict);
        Assert.True(verdict.Validate().IsSuccess);
    }

    [Fact]
    public async Task PipelineExceptionPropagates()
    {
        var guard = new SentinelCaptureGuard(
            new FakeDetectionPipeline { Throws = new InvalidOperationException("boom") }, new SentinelOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await guard.InspectAsync(Capture(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CallersTokenReachesThePipeline()
    {
        var pipeline = new FakeDetectionPipeline();
        using var cts = new CancellationTokenSource();

        await Inspect(pipeline, ct: cts.Token);

        Assert.Equal(cts.Token, pipeline.LastToken);
    }

    [Fact]
    public void InvalidOptionsThrow() =>
        Assert.Throws<ArgumentException>(() => new SentinelCaptureGuard(
            new FakeDetectionPipeline(), new SentinelOptions(), new SentinelCaptureGuardOptions { BlockAtSeverity = Severity.None }));

    [Fact]
    public void NullArgumentsThrow()
    {
        Assert.Throws<ArgumentNullException>(() => new SentinelCaptureGuard(null!, new SentinelOptions()));
        Assert.Throws<ArgumentNullException>(() => new SentinelCaptureGuard(new FakeDetectionPipeline(), null!));
    }

    [Fact]
    public async Task NullCaptureThrows()
    {
        var guard = new SentinelCaptureGuard(new FakeDetectionPipeline(), new SentinelOptions());

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await guard.InspectAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CustomGuardNameIsUsed()
    {
        var verdict = await Inspect(
            new FakeDetectionPipeline { Result = Result(Detection("SEC-06", Severity.High)) },
            options: new SentinelCaptureGuardOptions { GuardName = "my-guard" });

        Assert.Equal(CaptureVerdict.Block("my-guard", "SEC-06"), verdict);
    }
}
