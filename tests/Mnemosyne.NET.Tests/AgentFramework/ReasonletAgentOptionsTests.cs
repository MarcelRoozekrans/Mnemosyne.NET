using Microsoft.Agents.AI;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.AgentFramework;
using Xunit;

namespace Mnemosyne.NET.Tests.AgentFramework;

public sealed class ReasonletAgentOptionsTests
{
    [Fact]
    public void DefaultsAreRecallCaptureNoToolFiveSeconds()
    {
        var options = new ReasonletAgentOptions();

        Assert.True(options.Recall);
        Assert.True(options.Capture);
        Assert.False(options.ExposeFindProcedure);
        Assert.Null(options.MinSimilarity);
        Assert.Equal(TimeSpan.FromSeconds(5), options.RecallTimeout);
        Assert.Null(options.Formatter);
        Assert.Equal("mnemosyne.agent", options.Source);
        Assert.True(options.Validate().IsSuccess);
    }

    [Fact]
    public void DefaultScopeIsTheAgentName()
    {
        var scope = ReasonletAgentOptions.DefaultScope(new NamedAgent("support"), null);

        Assert.Equal(new ReasonletScope("support"), scope!.Scope);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void DefaultScopeIsNullForAnUnnamedAgent(string? name) =>
        Assert.Null(ReasonletAgentOptions.DefaultScope(new NamedAgent(name), null));

    [Fact]
    public void DefaultScopeHasNoSubject() =>
        Assert.Null(ReasonletAgentOptions.DefaultScope(new NamedAgent("support"), null)!.SubjectId);

    [Fact]
    public void TheDefaultResolverIsTheDefaultScope() =>
        Assert.Equal(new ReasonletScope("a"), new ReasonletAgentOptions().ScopeResolver(new NamedAgent("a"), null)!.Scope);

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void ValidateRejects(ReasonletAgentOptions options, string path)
    {
        var result = options.Validate();

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.StartsWith(path + ":", result.Error.Message, StringComparison.Ordinal);
    }

    public static TheoryData<ReasonletAgentOptions, string> InvalidOptions() => new()
    {
        { new ReasonletAgentOptions { ScopeResolver = null! }, "ScopeResolver" },
        { new ReasonletAgentOptions { RecallTimeout = TimeSpan.Zero }, "RecallTimeout" },
        { new ReasonletAgentOptions { RecallTimeout = TimeSpan.FromSeconds(-2) }, "RecallTimeout" },
        { new ReasonletAgentOptions { MinSimilarity = 1.5 }, "MinSimilarity" },
        { new ReasonletAgentOptions { MinSimilarity = -0.1 }, "MinSimilarity" },
        { new ReasonletAgentOptions { Source = "" }, "Source" },
        { new ReasonletAgentOptions { Source = " " }, "Source" },
    };

    [Fact]
    public void AnInfiniteTimeoutAndTheSimilarityBoundsPass()
    {
        Assert.True(new ReasonletAgentOptions { RecallTimeout = Timeout.InfiniteTimeSpan, MinSimilarity = 0 }.Validate().IsSuccess);
        Assert.True(new ReasonletAgentOptions { MinSimilarity = 1 }.Validate().IsSuccess);
    }

    private sealed class NamedAgent(string? name) : AIAgent
    {
        public override string? Name => name;

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(
            AgentSession session, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            System.Text.Json.JsonElement serializedState, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
