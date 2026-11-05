using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.AgentFramework;
using Mnemosyne.NET.PostgreSql.Tests;
using Mnemosyne.NET.Tests.Chat;
using Mnemosyne.NET.Tests.Core;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Tests.AgentFramework;

/// <summary>What the provider does after a run: the hand-off of the run to the capture sink.</summary>
/// <remarks>The class shares the recall telemetry's non-parallel collection, because it asserts the capture queue counter.</remarks>
[Collection(nameof(RecallTelemetryCollection))]
public sealed class ReasonletContextProviderCaptureTests : IAsyncDisposable
{
    private const string Queue = "mnemosyne.capture.queue";

    private readonly FakeReasonletMemory _memory = new();
    private readonly RecordingSink _sink = new();
    private readonly List<ServiceProvider> _containers = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RunIsHandedOffWithTheWholeToolLoop()
    {
        var match = CaptureTestData.Match();
        _memory.Returns(match);
        var provider = Provider(new ReasonletAgentOptions { ScopeResolver = (_, _) => new ReasonletAgentScope(new ReasonletScope("support"), "user-9") });

        await Agent(FakeAgentChatClient.ToolLoop(), provider).RunAsync(CaptureTestData.Question, cancellationToken: Ct);

        var run = Assert.Single(_sink.Runs);
        Assert.Equal("mnemosyne.agent", run.Source);
        Assert.Equal(new ReasonletScope("support"), run.Scope);
        Assert.Equal("user-9", run.SubjectId);
        Assert.Equal(match.Reasonlet.Id, run.InjectedParent);
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.Tool, ChatRole.Assistant], run.Messages.Select(m => m.Role));
        Assert.Equal(CaptureTestData.Question, run.Messages[0].Text);
        Assert.IsType<FunctionCallContent>(Assert.Single(run.Messages[1].Contents));
        Assert.IsType<FunctionResultContent>(Assert.Single(run.Messages[2].Contents));
        Assert.Equal("Refunded.", run.Messages[3].Text);
        Assert.All(run.Messages, message => Assert.DoesNotContain(ReasonletPromptFormatter.Format(match), message.Text, StringComparison.Ordinal));
        Assert.Empty(run.Lookups);
        Assert.True(run.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task TheRunsDurationIsMeasuredWithTheRegisteredTimeProvider()
    {
        var time = new StepTimeProvider(TimeSpan.FromSeconds(3));
        var services = Services();
        services.AddSingleton<TimeProvider>(time);
        var provider = Provider(services: services);

        await Agent(FakeAgentChatClient.Answering(), provider).RunAsync(CaptureTestData.Question, cancellationToken: Ct);

        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(_sink.Runs).Duration);
    }

    [Fact]
    public async Task StreamedRunIsHandedOffOnce()
    {
        var provider = Provider();

        await foreach (var update in Agent(FakeAgentChatClient.ToolLoop(), provider).RunStreamingAsync(CaptureTestData.Question, cancellationToken: Ct))
        {
            _ = update;
        }

        var run = Assert.Single(_sink.Runs);
        Assert.Equal(4, run.Messages.Count);
        Assert.Equal("Refunded.", run.Messages[^1].Text);
    }

    [Fact]
    public async Task AbandonedStreamIsNotCapturedAndTheNextRunStartsFresh()
    {
        var first = CaptureTestData.Match();
        var second = CaptureTestData.Match();
        _memory.RecallHandler = (query, _) => Task.FromResult(Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Success(
            [query.Question == "first" ? first : second]));
        var provider = Provider();
        var agent = Agent(FakeAgentChatClient.ToolLoop(), provider);
        var session = await agent.CreateSessionAsync(Ct);

        await foreach (var update in agent.RunStreamingAsync("first", session, cancellationToken: Ct))
        {
            _ = update;
            break;
        }

        Assert.Empty(_sink.Runs);
        await agent.RunAsync("second", session, cancellationToken: Ct);

        var run = Assert.Single(_sink.Runs);
        Assert.Equal(second.Reasonlet.Id, run.InjectedParent);
        Assert.Equal(0, provider.RunStateCount);
    }

    [Fact]
    public async Task FailedRunIsNotCaptured()
    {
        var provider = Provider();
        var agent = Agent(new ThrowingChatClient(), provider);

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync(CaptureTestData.Question, cancellationToken: Ct));

        Assert.Empty(_sink.Runs);
    }

    [Fact]
    public async Task FindProcedureLookupsReachTheRun()
    {
        var match = CaptureTestData.Match();
        _memory.Returns(match);
        var call = new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("l1", ReasonletTools.FindProcedureName, new Dictionary<string, object?> { ["question"] = "refund" })]));
        var client = new FakeAgentChatClient(call, new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));
        var provider = Provider(new ReasonletAgentOptions { ExposeFindProcedure = true });

        await Agent(client, provider).RunAsync(CaptureTestData.Question, cancellationToken: Ct);

        var run = Assert.Single(_sink.Runs);
        var lookup = Assert.Single(run.Lookups);
        Assert.Equal(match.Reasonlet.Id, Assert.Single(lookup).Reasonlet.Id);
    }

    [Fact]
    public async Task ConcurrentRunsOnTwoSessionsKeepTheirOwnParents()
    {
        var a = CaptureTestData.Match();
        var b = CaptureTestData.Match();
        _memory.RecallHandler = (query, _) => Task.FromResult(Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>.Success(
            [query.Question == "question a" ? a : b]));
        var client = new GatedChatClient(2);
        var provider = Provider(new ReasonletAgentOptions { ExposeFindProcedure = true });
        var agent = Agent(client, provider);
        var sessionA = await agent.CreateSessionAsync(Ct);
        var sessionB = await agent.CreateSessionAsync(Ct);

        var runA = agent.RunAsync("question a", sessionA, cancellationToken: Ct);
        var runB = agent.RunAsync("question b", sessionB, cancellationToken: Ct);
        await Task.WhenAll(runA, runB);

        Assert.True(client.BothWereInFlight);
        Assert.Equal(2, _sink.Runs.Count);
        var captured = _sink.Runs.ToDictionary(run => run.Messages[0].Text);
        Assert.Equal(a.Reasonlet.Id, captured["question a"].InjectedParent);
        Assert.Equal(b.Reasonlet.Id, captured["question b"].InjectedParent);
        Assert.Equal(a.Reasonlet.Id, Assert.Single(Assert.Single(captured["question a"].Lookups)).Reasonlet.Id);
        Assert.Equal(b.Reasonlet.Id, Assert.Single(Assert.Single(captured["question b"].Lookups)).Reasonlet.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RunWithoutUserTextIsCaptured(int kind)
    {
        ChatMessage message = kind switch
        {
            0 => new(ChatRole.User, [new DataContent(new byte[] { 1 }, "image/png")]),
            1 => new(ChatRole.User, [new ToolApprovalResponseContent("a1", false, new FunctionCallContent("c1", FakeAgentChatClient.HostToolName))]),
            _ => new(ChatRole.User, "  "),
        };
        var provider = Provider();

        await Agent(FakeAgentChatClient.Answering(), provider).RunAsync([message], cancellationToken: Ct);

        var run = Assert.Single(_sink.Runs);
        Assert.Null(run.InjectedParent);
        Assert.Same(message, run.Messages[0]);
        Assert.Empty(_memory.Queries);
    }

    [Fact]
    public async Task CaptureOffNeverResolvesTheSink()
    {
        var resolutions = 0;
        var services = Services();
        services.AddSingleton<IReasonletCaptureSink>(_ =>
        {
            resolutions++;
            return _sink;
        });
        var provider = Provider(new ReasonletAgentOptions { Capture = false }, services);

        await Agent(FakeAgentChatClient.Answering(), provider).RunAsync(CaptureTestData.Question, cancellationToken: Ct);

        Assert.Equal(0, resolutions);
        Assert.Empty(_sink.Runs);
        Assert.Equal(0, provider.RunStateCount);
    }

    [Fact]
    public async Task TheSinkIsResolvedOnTheFirstCaptureNotWhenBuilt()
    {
        var resolutions = 0;
        var services = Services();
        services.AddSingleton<IReasonletCaptureSink>(_ =>
        {
            resolutions++;
            return _sink;
        });
        var provider = Provider(services: services);
        Assert.Equal(0, resolutions);
        var agent = Agent(FakeAgentChatClient.Answering(), provider);

        await agent.RunAsync(CaptureTestData.Question, cancellationToken: Ct);
        await agent.RunAsync(CaptureTestData.Question, cancellationToken: Ct);

        Assert.Equal(1, resolutions);
        Assert.Equal(2, _sink.Runs.Count);
    }

    [Fact]
    public async Task SinkReturningFalseDoesNotFailTheRun()
    {
        _sink.Accept = false;
        using var telemetry = new TelemetryCapture();

        var response = await Agent(FakeAgentChatClient.Answering(), Provider()).RunAsync(CaptureTestData.Question, cancellationToken: Ct);

        Assert.Equal("Done.", response.Text);
        Assert.Single(_sink.Runs);
        Assert.Empty(telemetry.Of(Queue));
    }

    [Fact]
    public async Task SinkThrowingDoesNotFailTheRunAndIsCounted()
    {
        _sink.Throw = new InvalidOperationException("secret detail");
        using var telemetry = new TelemetryCapture();

        var response = await Agent(FakeAgentChatClient.Answering(), Provider()).RunAsync(CaptureTestData.Question, cancellationToken: Ct);

        Assert.Equal("Done.", response.Text);
        var count = Assert.Single(telemetry.Of(Queue));
        Assert.Equal("exception", count.Tag("mnemosyne.outcome"));
        Assert.Equal(typeof(InvalidOperationException).FullName, count.Tag("error.type"));
        Assert.DoesNotContain(telemetry.Measurements.SelectMany(m => m.Tags), tag => tag.Value?.ToString()?.Contains("secret", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task MissingSinkIsCountedAndTheRunGoesOn()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletMemory>(_memory);
        var provider = Provider(services: services);
        using var telemetry = new TelemetryCapture();

        var response = await Agent(FakeAgentChatClient.Answering(), provider).RunAsync(CaptureTestData.Question, cancellationToken: Ct);

        Assert.Equal("Done.", response.Text);
        var count = Assert.Single(telemetry.Of(Queue));
        Assert.Equal("exception", count.Tag("mnemosyne.outcome"));
        Assert.Equal(typeof(InvalidOperationException).FullName, count.Tag("error.type"));
    }

    [Fact]
    public async Task CancellationFromTheSinkPropagates()
    {
        using var cts = new CancellationTokenSource();
        var provider = Provider();
        _sink.Throw = new OperationCanceledException(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await provider.StoreAsync(
            Stub.Agent, null, [new(ChatRole.User, "q")], [new(ChatRole.Assistant, "a")], cts.Token));
    }

    [Fact]
    public async Task ARunWithoutASessionIsCapturedWithNoParentAndNoLookups()
    {
        using var telemetry = new TelemetryCapture();
        var provider = Provider(new ReasonletAgentOptions { ScopeResolver = (_, _) => new ReasonletAgentScope(new ReasonletScope("s"), "subject") });
        ChatMessage[] response = [new(ChatRole.Assistant, "a") { MessageId = "resp-1" }];

        await provider.StoreAsync(Stub.Agent, null, [new(ChatRole.User, "q")], response, Ct);

        var run = Assert.Single(_sink.Runs);
        Assert.Equal(new ReasonletScope("s"), run.Scope);
        Assert.Equal("subject", run.SubjectId);
        Assert.Null(run.InjectedParent);
        Assert.Empty(run.Lookups);
        Assert.Equal(TimeSpan.Zero, run.Duration);
        Assert.Equal("resp-1", run.RunId);
        Assert.Equal(2, run.Messages.Count);
        Assert.Empty(telemetry.Of("mnemosyne.chat.recalls"));
    }

    [Fact]
    public async Task ARunWithoutAnyResponseHasNoRunId()
    {
        await Provider().StoreAsync(Stub.Agent, null, [new(ChatRole.User, "q")], null, Ct);

        Assert.Null(Assert.Single(_sink.Runs).RunId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARunWithoutStateAndWithoutScopeIsNotCapturedAndNothingIsCounted(bool throws)
    {
        using var telemetry = new TelemetryCapture();
        var provider = Provider(new ReasonletAgentOptions
        {
            ScopeResolver = (_, _) => throws ? throw new InvalidOperationException("x") : null,
        });

        await provider.StoreAsync(Stub.Agent, null, [new(ChatRole.User, "q")], [], Ct);

        Assert.Empty(_sink.Runs);
        Assert.Empty(telemetry.Measurements);
    }

    [Fact]
    public async Task ASessionWithoutStateIsCapturedThroughTheResolver()
    {
        var provider = Provider();
        var agent = Agent(FakeAgentChatClient.Answering(), provider);
        var session = await agent.CreateSessionAsync(Ct);

        await provider.StoreAsync(agent, session, [new(ChatRole.User, "q")], [new(ChatRole.Assistant, "a")], Ct);

        Assert.Equal(new ReasonletScope("support"), Assert.Single(_sink.Runs).Scope);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var container in _containers)
        {
            await container.DisposeAsync();
        }
    }

    private ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletMemory>(_memory);
        services.AddSingleton<IReasonletCaptureSink>(_sink);
        return services;
    }

    private ReasonletContextProvider Provider(ReasonletAgentOptions? options = null, ServiceCollection? services = null)
    {
        var container = (services ?? Services()).BuildServiceProvider();
        _containers.Add(container);
        return new(container, options);
    }

    private static ChatClientAgent Agent(IChatClient client, ReasonletContextProvider provider) =>
        new(client, new ChatClientAgentOptions
        {
            Name = "support",
            ChatOptions = new ChatOptions { Tools = [FakeAgentChatClient.HostTool()] },
            AIContextProviders = [provider],
        });

    private sealed class RecordingSink : IReasonletCaptureSink
    {
        private readonly ConcurrentQueue<ReasonletCaptureRun> _runs = new();

        public bool Accept { get; set; } = true;

        public Exception? Throw { get; set; }

        public IReadOnlyList<ReasonletCaptureRun> Runs => [.. _runs];

        public bool TryCapture(ReasonletCaptureRun run)
        {
            _runs.Enqueue(run);
            return Throw is null ? Accept : throw Throw;
        }
    }

    /// <summary>Counts a time step on every read, so a duration is exactly one step.</summary>
    private sealed class StepTimeProvider(TimeSpan step) : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Add(ref _ticks, step.Ticks) - step.Ticks;
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("model down");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("model down");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Calls <c>find_procedure</c> once, but only after <c>parties</c> runs have each made their first call, so that the
    /// runs are in flight together; then answers.
    /// </summary>
    private sealed class GatedChatClient(int parties) : IChatClient
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public bool BothWereInFlight { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            if (list.Any(m => m.Role == ChatRole.Tool))
            {
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."));
            }

            if (Interlocked.Increment(ref _arrived) == parties)
            {
                BothWereInFlight = true;
                _gate.SetResult();
            }

            await _gate.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            var question = list.Last(m => m.Role == ChatRole.User).Text;
            return new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent("l1", ReasonletTools.FindProcedureName, new Dictionary<string, object?> { ["question"] = question })]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static class Stub
    {
        public static AIAgent Agent { get; } = new ChatClientAgent(FakeAgentChatClient.Answering(), new ChatClientAgentOptions { Name = "support" });
    }
}
