using AI.Sentinel.Detection;
using AI.Sentinel.Domain;

namespace Mnemosyne.NET.Tests.Sentinel;

/// <summary>An <see cref="IDetectionPipeline"/> that records what it was asked and returns a configured result.</summary>
internal sealed class FakeDetectionPipeline : IDetectionPipeline
{
    public PipelineResult Result { get; set; } = new(ThreatRiskScore.Zero, []);

    public Exception? Throws { get; set; }

    public SentinelContext? LastContext { get; private set; }

    public CancellationToken LastToken { get; private set; }

    public ValueTask<PipelineResult> RunAsync(SentinelContext ctx, CancellationToken ct)
    {
        LastContext = ctx;
        LastToken = ct;
        return Throws is null ? new ValueTask<PipelineResult>(Result) : throw Throws;
    }
}
