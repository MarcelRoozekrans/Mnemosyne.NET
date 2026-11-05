using System.Diagnostics;
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

/// <summary>What the provider does before a run: the scope, the recall, the injection and the <c>find_procedure</c> tool.</summary>
/// <remarks>The class shares the recall telemetry's non-parallel collection, because it asserts the recall counter.</remarks>
[Collection(nameof(RecallTelemetryCollection))]
public sealed class ReasonletContextProviderTests : IAsyncDisposable
{
    private const string Recalls = "mnemosyne.chat.recalls";
    private const string Integration = "mnemosyne.integration";
    private const string Outcome = "mnemosyne.outcome";

    private readonly FakeReasonletMemory _memory = new();
    private readonly List<ServiceProvider> _containers = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MatchIsInjectedAsInstructionsOnEveryInnerCall()
    {
        var match = CaptureTestData.Match();
        _memory.Returns(match);
        var text = ReasonletPromptFormatter.Format(match);
        var client = FakeAgentChatClient.ToolLoop();
        using var telemetry = new TelemetryCapture();

        var response = await Agent(client, Provider()).RunAsync(CaptureTestData.Question, cancellationToken: Ct);

        Assert.Equal("Refunded.", response.Text);
        Assert.Equal(2, client.Options.Count);
        Assert.All(client.Options, options => Assert.Equal(text, options!.Instructions));
        Assert.All(client.Requests.SelectMany(request => request), message => Assert.DoesNotContain(text, message.Text, StringComparison.Ordinal));
        AssertRecall(telemetry, "injected");
    }

    [Fact]
    public async Task MatchIsInjectedAsInstructionsOnEveryInnerCallWhenStreaming()
    {
        var match = CaptureTestData.Match();
        _memory.Returns(match);
        var text = ReasonletPromptFormatter.Format(match);
        var client = FakeAgentChatClient.ToolLoop();
        using var telemetry = new TelemetryCapture();

        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in Agent(client, Provider()).RunStreamingAsync(CaptureTestData.Question, cancellationToken: Ct))
        {
            updates.Add(update);
        }

        Assert.Equal("Refunded.", updates.ToAgentResponse().Text);
        Assert.Equal(2, client.Options.Count);
        Assert.All(client.Options, options => Assert.Equal(text, options!.Instructions));
        Assert.All(client.Requests.SelectMany(request => request), message => Assert.DoesNotContain(text, message.Text, StringComparison.Ordinal));
        AssertRecall(telemetry, "injected");
    }

    [Fact]
    public async Task InstructionsJoinTheAgentsOwn()
    {
        var match = CaptureTestData.Match();
        _memory.Returns(match);
        var client = FakeAgentChatClient.Answering();

        await Agent(client, Provider(), instructions: "Be brief.").RunAsync(CaptureTestData.Question, cancellationToken: Ct);

        var instructions = Assert.Single(client.Options)!.Instructions!;
        Assert.StartsWith("Be brief.", instructions, StringComparison.Ordinal);
        Assert.EndsWith(ReasonletPromptFormatter.Format(match), instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecallUsesTheLastUserMessageAndTheAgentScope()
    {
        _memory.Returns(CaptureTestData.Match());
        var client = FakeAgentChatClient.Answering();
        var provider = Provider(new ReasonletAgentOptions { MinSimilarity = 0.7 });

        await Agent(client, provider).RunAsync(
            [new(ChatRole.System, "sys"), new(ChatRole.User, "earlier"), new(ChatRole.Assistant, "noted"), new(ChatRole.User, CaptureTestData.Question)],
            cancellationToken: Ct);

        var query = Assert.Single(_memory.Queries);
        Assert.Equal(new ReasonletScope("support"), query.Scope);
        Assert.Equal(CaptureTestData.Question, query.Question);
        Assert.Equal(1, query.Top);
        Assert.Equal(0.7, query.MinSimilarity);
    }

    [Fact]
    public async Task ACustomResolverAndFormatterAreUsed()
    {
        _memory.Returns(CaptureTestData.Match());
        var client = FakeAgentChatClient.Answering();
        AgentSession? seen = null;
        var provider = Provider(new ReasonletAgentOptions
        {
            ScopeResolver = (_, session) =>
            {
                seen = session;
                return new ReasonletAgentScope(new ReasonletScope("tenant-1"), "subject-1");
            },
            Formatter = m => $"use {m.Reasonlet.Name}",
        });
        var agent = Agent(client, provider);
        var session = await agent.CreateSessionAsync(Ct);

        await agent.RunAsync(CaptureTestData.Question, session, cancellationToken: Ct);

        Assert.Same(session, seen);
        Assert.Equal(new ReasonletScope("tenant-1"), Assert.Single(_memory.Queries).Scope);
        Assert.Equal($"use {CaptureTestData.Match().Reasonlet.Name}", Assert.Single(client.Options)!.Instructions);
    }

    [Theory]
    [InlineData("no_match")]
    [InlineData("error")]
    [InlineData("timeout")]
    [InlineData("exception")]
    public async Task NoInjectionOnNoMatchErrorTimeoutOrException(string outcome)
    {
        var options = new ReasonletAgentOptions();
        switch (outcome)
        {
            case "error":
                _memory.Fails(new MnemosyneError(MnemosyneErrorCode.Unavailable, "down"));
                break;
            case "timeout":
                _memory.RecallHandler = (_, _) => new TaskCompletionSource<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>>().Task;
                options = new ReasonletAgentOptions { RecallTimeout = TimeSpan.FromMilliseconds(50) };
                break;
            case "exception":
                _memory.Throws(new HttpRequestException("refused"));
                break;
        }

        var client = FakeAgentChatClient.ToolLoop();
        using var telemetry = new TelemetryCapture();

        var response = await Agent(client, Provider(options)).RunAsync(CaptureTestData.Question, cancellationToken: Ct)
            .WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal("Refunded.", response.Text);
        Assert.All(client.Options, o => Assert.Null(o!.Instructions));
        AssertRecall(telemetry, outcome == "error" ? "Unavailable" : outcome);
    }

    [Fact]
    public async Task TheRegisteredTimeProviderTimesTheRecall()
    {
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _memory.RecallHandler = (_, _) =>
        {
            asked.TrySetResult();
            return new TaskCompletionSource<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>>().Task;
        };
        var clock = new ManualTimeProvider();
        var client = FakeAgentChatClient.Answering();
        var services = Services(_memory);
        services.AddSingleton<TimeProvider>(clock);
        await using var container = services.BuildServiceProvider();
        using var telemetry = new TelemetryCapture();

        var run = Agent(client, new ReasonletContextProvider(container)).RunAsync(CaptureTestData.Question, cancellationToken: Ct);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        clock.Fire();
        await run.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal([TimeSpan.FromSeconds(5)], clock.DueTimes.Take(1));
        Assert.Null(Assert.Single(client.Options)!.Instructions);
        AssertRecall(telemetry, "timeout");
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        _memory.RecallHandler = (_, _) =>
        {
            cts.Cancel();
            return new TaskCompletionSource<Result<IReadOnlyList<ReasonletMatch>, MnemosyneError>>().Task;
        };
        var client = FakeAgentChatClient.Answering();
        using var telemetry = new TelemetryCapture();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Agent(client, Provider()).RunAsync(CaptureTestData.Question, cancellationToken: cts.Token).WaitAsync(TimeSpan.FromSeconds(30), Ct));

        Assert.Empty(client.Requests);
        Assert.Empty(telemetry.Of(Recalls));
    }

    [Fact]
    public async Task CallerCancellationInTheScopeResolverPropagates()
    {
        using var cts = new CancellationTokenSource();
        var provider = Provider(new ReasonletAgentOptions
        {
            ScopeResolver = (_, _) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            },
        });
        var client = FakeAgentChatClient.Answering();
        using var telemetry = new TelemetryCapture();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Agent(client, provider).RunAsync(CaptureTestData.Question, cancellationToken: cts.Token));

        Assert.Empty(client.Requests);
        Assert.Empty(telemetry.Of(Recalls));
    }

    // The capture half, that such a run is still handed to capture, is asserted with the capture tests.
    [Theory]
    [InlineData("image")]
    [InlineData("approval")]
    [InlineData("blank")]
    public async Task RunWithoutUserTextIsNotRecalledForButIsCaptured(string kind)
    {
        _memory.Returns(CaptureTestData.Match());
        var client = FakeAgentChatClient.Answering();
        ChatMessage message = kind switch
        {
            "image" => new(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")]),
            "approval" => new(ChatRole.User, [new ToolApprovalResponseContent("a1", false, new FunctionCallContent("c1", FakeAgentChatClient.HostToolName))]),
            _ => new(ChatRole.User, "  "),
        };
        using var telemetry = new TelemetryCapture();

        await Agent(client, Provider()).RunAsync([new(ChatRole.User, CaptureTestData.Question), new(ChatRole.Assistant, "ok"), message], cancellationToken: Ct);

        Assert.Empty(_memory.Queries);
        Assert.All(client.Options, o => Assert.Null(o!.Instructions));
        var count = Assert.Single(telemetry.Of(Recalls));
        Assert.Equal("skipped", count.Tag(Outcome));
        Assert.Equal("continuation", count.Tag("mnemosyne.skip_reason"));
        Assert.Equal("agent", count.Tag(Integration));
    }

    [Fact]
    public async Task ARunWithoutUserTextAndRecallOffIsNotCounted()
    {
        var client = FakeAgentChatClient.Answering();
        using var telemetry = new TelemetryCapture();

        await Agent(client, Provider(new ReasonletAgentOptions { Recall = false })).RunAsync(
            [new(ChatRole.User, [new DataContent(new byte[] { 1 }, "image/png")])], cancellationToken: Ct);

        Assert.Empty(telemetry.Of(Recalls));
    }

    [Fact]
    public async Task UnnamedAgentSkipsWithNoScope()
    {
        _memory.Returns(CaptureTestData.Match());
        var client = FakeAgentChatClient.Answering();
        using var telemetry = new TelemetryCapture();

        await Agent(client, Provider(new ReasonletAgentOptions { ExposeFindProcedure = true }), name: null)
            .RunAsync(CaptureTestData.Question, cancellationToken: Ct);

        Assert.Empty(_memory.Queries);
        var options = Assert.Single(client.Options)!;
        Assert.Null(options.Instructions);
        Assert.DoesNotContain(options.Tools!, tool => tool.Name == ReasonletTools.FindProcedureName);
        var count = Assert.Single(telemetry.Of(Recalls));
        Assert.Equal("skipped", count.Tag(Outcome));
        Assert.Equal("no_scope", count.Tag("mnemosyne.skip_reason"));
        Assert.Equal("agent", count.Tag(Integration));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task NoScopeIsCountedWhenRecallOrCaptureIsOn(bool recall, bool capture, bool counted)
    {
        var client = FakeAgentChatClient.Answering();
        using var telemetry = new TelemetryCapture();

        await Agent(client, Provider(new ReasonletAgentOptions { Recall = recall, Capture = capture }), name: null)
            .RunAsync(CaptureTestData.Question, cancellationToken: Ct);

        Assert.Equal(counted ? 1 : 0, telemetry.Of(Recalls).Count);
    }

    [Fact]
    public async Task ThrowingScopeResolverSkipsTheRun()
    {
        _memory.Returns(CaptureTestData.Match());
        var client = FakeAgentChatClient.ToolLoop();
        var provider = Provider(new ReasonletAgentOptions
        {
            ExposeFindProcedure = true,
            ScopeResolver = (_, _) => throw new InvalidDataException("secret support"),
        });
        using var telemetry = new TelemetryCapture();

        var response = await Agent(client, provider).RunAsync(CaptureTestData.Question, cancellationToken: Ct);

        Assert.Equal("Refunded.", response.Text);
        Assert.Empty(_memory.Queries);
        Assert.All(client.Options, o =>
        {
            Assert.Null(o!.Instructions);
            Assert.DoesNotContain(o.Tools!, tool => tool.Name == ReasonletTools.FindProcedureName);
        });
        var count = Assert.Single(telemetry.Of(Recalls));
        Assert.Equal("exception", count.Tag(Outcome));
        Assert.Equal("System.IO.InvalidDataException", count.Tag("error.type"));
        Assert.Equal("agent", count.Tag(Integration));
        var activity = Assert.Single(telemetry.Activities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        AssertNoPrivateTags(telemetry, "secret", "support");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindProcedureIsAddedOnlyWhenSwitchedOn(bool expose)
    {
        var client = FakeAgentChatClient.Answering();
        var agent = Agent(client, Provider(new ReasonletAgentOptions { ExposeFindProcedure = expose }));

        await agent.RunAsync(CaptureTestData.Question, cancellationToken: Ct);
        await agent.RunAsync(CaptureTestData.Question, cancellationToken: Ct);

        var lookups = client.Options.Select(o => o!.Tools!.SingleOrDefault(tool => tool.Name == ReasonletTools.FindProcedureName)).ToList();
        Assert.Equal(2, lookups.Count);
        if (expose)
        {
            Assert.All(lookups, lookup => Assert.IsType<FindProcedureFunction>(lookup));
            Assert.NotSame(lookups[0], lookups[1]);
        }
        else
        {
            Assert.All(lookups, Assert.Null);
        }
    }

    [Fact]
    public async Task FindProcedureIsTheSameToolAcrossTheToolLoop()
    {
        var client = FakeAgentChatClient.ToolLoop();

        await Agent(client, Provider(new ReasonletAgentOptions { ExposeFindProcedure = true })).RunAsync(CaptureTestData.Question, cancellationToken: Ct);

        var lookups = client.Options.Select(o => o!.Tools!.Single(tool => tool.Name == ReasonletTools.FindProcedureName)).ToList();
        Assert.Equal(2, lookups.Count);
        Assert.Same(lookups[0], lookups[1]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FindProcedureIsNotAddedWhenTheAgentHasOne(bool onTheAgent)
    {
        var client = FakeAgentChatClient.Answering();
        var host = AIFunctionFactory.Create(() => "host", ReasonletTools.FindProcedureName);
        var provider = Provider(new ReasonletAgentOptions { ExposeFindProcedure = true });
        var agent = Agent(client, provider, tools: onTheAgent ? [FakeAgentChatClient.HostTool(), host] : null);
        var run = onTheAgent ? null : new ChatClientAgentRunOptions(new ChatOptions { Tools = [host] });

        await agent.RunAsync(CaptureTestData.Question, options: run, cancellationToken: Ct);

        var tool = Assert.Single(Assert.Single(client.Options)!.Tools!, t => t.Name == ReasonletTools.FindProcedureName);
        Assert.Same(host, tool);
    }

    [Fact]
    public async Task AFindProcedureFromAnEarlierProviderIsKept()
    {
        var client = FakeAgentChatClient.Answering();
        var host = AIFunctionFactory.Create(() => "host", ReasonletTools.FindProcedureName);
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = "support",
            AIContextProviders = [new ToolProvider(host), Provider(new ReasonletAgentOptions { ExposeFindProcedure = true })],
        });

        await agent.RunAsync(CaptureTestData.Question, cancellationToken: Ct);

        Assert.Same(host, Assert.Single(Assert.Single(client.Options)!.Tools!, t => t.Name == ReasonletTools.FindProcedureName));
    }

    [Fact]
    public async Task CancellationWhileResolvingTheMemoryPropagatesAndCountsNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletMemory>(_ => throw new OperationCanceledException(cts.Token));
        await using var container = services.BuildServiceProvider();
        var provider = new ReasonletContextProvider(container);
        var agent = Agent(FakeAgentChatClient.Answering(), provider);
        using var telemetry = new TelemetryCapture();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await provider.ProvideAsync(agent, null, [new(ChatRole.User, CaptureTestData.Question)], null, cts.Token));

        Assert.Empty(telemetry.Of(Recalls));
    }

    [Fact]
    public async Task MissingMemoryIsCountedAndTheRunGoesOn()
    {
        var client = FakeAgentChatClient.ToolLoop();
        await using var container = new ServiceCollection().BuildServiceProvider();
        var provider = new ReasonletContextProvider(container, new ReasonletAgentOptions { ExposeFindProcedure = true });
        using var telemetry = new TelemetryCapture();

        var response = await Agent(client, provider).RunAsync(CaptureTestData.Question, cancellationToken: Ct);

        Assert.Equal("Refunded.", response.Text);
        Assert.All(client.Options, o =>
        {
            Assert.Null(o!.Instructions);
            Assert.DoesNotContain(o.Tools!, tool => tool.Name == ReasonletTools.FindProcedureName);
        });
        var count = Assert.Single(telemetry.Of(Recalls));
        Assert.Equal("exception", count.Tag(Outcome));
        Assert.Equal(typeof(InvalidOperationException).FullName, count.Tag("error.type"));
        Assert.Equal("agent", count.Tag(Integration));
    }

    [Fact]
    public async Task AContinuationWithTheToolOnAndAMissingMemoryCountsBoth()
    {
        // As UseReasonlets does: the skip is counted for the recall, and the memory is still resolved for the tool.
        var client = FakeAgentChatClient.Answering();
        await using var container = new ServiceCollection().BuildServiceProvider();
        var provider = new ReasonletContextProvider(container, new ReasonletAgentOptions { ExposeFindProcedure = true });
        using var telemetry = new TelemetryCapture();

        await Agent(client, provider).RunAsync([new(ChatRole.User, [new DataContent(new byte[] { 1 }, "image/png")])], cancellationToken: Ct);

        var counts = telemetry.Of(Recalls);
        Assert.Equal(2, counts.Count);
        Assert.Single(counts, c => (string?)c.Tag("mnemosyne.skip_reason") == "continuation");
        Assert.Single(counts, c => (string?)c.Tag(Outcome) == "exception");
        Assert.DoesNotContain(Assert.Single(client.Options)!.Tools!, tool => tool.Name == ReasonletTools.FindProcedureName);
    }

    [Fact]
    public async Task TheMemoryIsResolvedOnTheFirstRunAndAFailureIsRetried()
    {
        _memory.Returns(CaptureTestData.Match());
        var resolutions = 0;
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletMemory>(_ => ++resolutions == 1 ? throw new InvalidOperationException("not yet") : _memory);
        await using var container = services.BuildServiceProvider();
        var client = FakeAgentChatClient.Answering();
        var provider = new ReasonletContextProvider(container);
        Assert.Equal(0, resolutions);
        var agent = Agent(client, provider);

        await agent.RunAsync(CaptureTestData.Question, cancellationToken: Ct);
        await agent.RunAsync(CaptureTestData.Question, cancellationToken: Ct);
        await agent.RunAsync(CaptureTestData.Question, cancellationToken: Ct);

        Assert.Equal(2, resolutions);
        Assert.Equal(2, _memory.Queries.Count);
        Assert.Null(client.Options[0]!.Instructions);
        Assert.NotNull(client.Options[1]!.Instructions);
    }

    [Fact]
    public async Task RecallOffNeverResolvesTheMemory()
    {
        var resolutions = 0;
        var services = new ServiceCollection();
        services.AddSingleton<IReasonletMemory>(_ =>
        {
            resolutions++;
            return _memory;
        });
        await using var container = services.BuildServiceProvider();
        var client = FakeAgentChatClient.Answering();
        using var telemetry = new TelemetryCapture();

        await Agent(client, new ReasonletContextProvider(container, new ReasonletAgentOptions { Recall = false }))
            .RunAsync(CaptureTestData.Question, cancellationToken: Ct);

        Assert.Equal(0, resolutions);
        Assert.Null(Assert.Single(client.Options)!.Instructions);
        Assert.Empty(telemetry.Of(Recalls));
    }

    [Fact]
    public async Task RecallOffWithTheToolOnStillOffersTheTool()
    {
        var client = FakeAgentChatClient.Answering();

        await Agent(client, Provider(new ReasonletAgentOptions { Recall = false, ExposeFindProcedure = true }))
            .RunAsync(CaptureTestData.Question, cancellationToken: Ct);

        Assert.Empty(_memory.Queries);
        Assert.Contains(Assert.Single(client.Options)!.Tools!, tool => tool is FindProcedureFunction);
    }

    [Fact]
    public async Task AContextWithoutMessagesOrToolsIsAContinuationThatGetsTheTool()
    {
        var client = FakeAgentChatClient.Answering();
        var provider = Provider(new ReasonletAgentOptions { ExposeFindProcedure = true });
        var agent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = "support",
            AIContextProviders = [new EmptyingProvider(), provider],
        });
        using var telemetry = new TelemetryCapture();

        await agent.RunAsync(CaptureTestData.Question, cancellationToken: Ct);

        Assert.Empty(_memory.Queries);
        Assert.Contains(Assert.Single(client.Options)!.Tools!, tool => tool is FindProcedureFunction);
        Assert.Equal("continuation", Assert.Single(telemetry.Of(Recalls)).Tag("mnemosyne.skip_reason"));
    }

    [Fact]
    public async Task ANullSessionIsRecalledForAndGetsTheToolButStoresNothing()
    {
        var match = CaptureTestData.Match();
        _memory.Returns(match);
        var provider = Provider(new ReasonletAgentOptions { ExposeFindProcedure = true });
        var agent = Agent(FakeAgentChatClient.Answering(), provider);

        var context = await provider.ProvideAsync(agent, null, [new(ChatRole.User, CaptureTestData.Question)], null, Ct);

        Assert.Equal(ReasonletPromptFormatter.Format(match), context.Instructions);
        Assert.IsType<FindProcedureFunction>(Assert.Single(context.Tools!));
        Assert.Equal(0, provider.RunStateCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheStartOfARunOnASessionLeavesItsStateWhenCaptureIsOn(bool capture)
    {
        var match = CaptureTestData.Match();
        _memory.Returns(match);
        var provider = Provider(new ReasonletAgentOptions { ExposeFindProcedure = true, Capture = capture });
        var agent = Agent(FakeAgentChatClient.Answering(), provider);
        var session = await agent.CreateSessionAsync(Ct);

        // The start hook alone: the end of a whole run takes the state away again.
        var context = await provider.ProvideAsync(agent, session, [new(ChatRole.User, CaptureTestData.Question)], null, Ct);

        Assert.Equal(capture, provider.TryGetRunState(session, out var state));
        if (capture)
        {
            Assert.Equal(new ReasonletAgentScope(new ReasonletScope("support")), state!.Scope);
            Assert.Equal(match.Reasonlet.Id, state.Parent);
            Assert.Same(Assert.Single(context.Tools!), state.Lookup);
        }
    }

    [Fact]
    public async Task ARunWithoutScopeForgetsTheSessionsEarlierState()
    {
        var unnamed = false;
        var provider = Provider(new ReasonletAgentOptions
        {
            ScopeResolver = (agent, session) => unnamed ? null : ReasonletAgentOptions.DefaultScope(agent, session),
        });
        var agent = Agent(FakeAgentChatClient.Answering(), provider);
        var session = await agent.CreateSessionAsync(Ct);
        IEnumerable<ChatMessage> messages = [new(ChatRole.User, CaptureTestData.Question)];
        await provider.ProvideAsync(agent, session, messages, null, Ct);
        Assert.True(provider.TryGetRunState(session, out _));
        unnamed = true;

        await provider.ProvideAsync(agent, session, messages, null, Ct);

        Assert.False(provider.TryGetRunState(session, out _));
    }

    [Fact]
    public void StateKeysAreEmpty() => Assert.Empty(Provider().StateKeys);

    [Fact]
    public void ANullServiceProviderThrows() =>
        Assert.Throws<ArgumentNullException>("services", () => new ReasonletContextProvider(null!));

    [Fact]
    public void InvalidOptionsThrow()
    {
        using var container = new ServiceCollection().BuildServiceProvider();

        var error = Assert.Throws<ArgumentException>(() =>
            new ReasonletContextProvider(container, new ReasonletAgentOptions { RecallTimeout = TimeSpan.Zero }));

        Assert.Equal("options", error.ParamName);
        Assert.Contains(nameof(ReasonletAgentOptions.RecallTimeout), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructionNeedsNoRegistrations()
    {
        using var container = new ServiceCollection().BuildServiceProvider();

        Assert.NotNull(new ReasonletContextProvider(container));
    }

    private static ServiceCollection Services(IReasonletMemory memory)
    {
        var services = new ServiceCollection();
        services.AddSingleton(memory);
        return services;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var container in _containers)
        {
            await container.DisposeAsync();
        }
    }

    private ReasonletContextProvider Provider(ReasonletAgentOptions? options = null)
    {
        var container = Services(_memory).BuildServiceProvider();
        _containers.Add(container);
        return new(container, options);
    }

    private static ChatClientAgent Agent(
        IChatClient client, ReasonletContextProvider provider, string? name = "support", IList<AITool>? tools = null, string? instructions = null) =>
        new(client, new ChatClientAgentOptions
        {
            Name = name,
            ChatOptions = new ChatOptions { Instructions = instructions, Tools = tools ?? [FakeAgentChatClient.HostTool()] },
            AIContextProviders = [provider],
        });

    private static void AssertRecall(TelemetryCapture telemetry, string outcome)
    {
        var count = Assert.Single(telemetry.Of(Recalls));
        Assert.Equal(1, count.Value);
        Assert.Equal(outcome, count.Tag(Outcome));
        Assert.Equal("agent", count.Tag(Integration));
        Assert.Equal("agent", Assert.Single(telemetry.Activities).GetTagItem(Integration));
    }

    private static void AssertNoPrivateTags(TelemetryCapture telemetry, params string[] secrets)
    {
        var values = telemetry.Measurements.SelectMany(m => m.Tags.Select(tag => tag.Value?.ToString()))
            .Concat(telemetry.Activities.SelectMany(a => a.TagObjects.Select(tag => tag.Value?.ToString())))
            .Concat(telemetry.Activities.Select(a => a.StatusDescription));
        foreach (var value in values)
        {
            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, value ?? string.Empty, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>A provider ahead of the Reasonlet one that hands on a context with no messages and no tools.</summary>
    private sealed class EmptyingProvider : AIContextProvider
    {
        public EmptyingProvider()
            : base(null, null, null)
        {
        }

        public override IReadOnlyList<string> StateKeys => [];

        protected override ValueTask<AIContext> InvokingCoreAsync(InvokingContext context, CancellationToken cancellationToken) =>
            new(new AIContext());
    }

    /// <summary>A provider ahead of the Reasonlet one that adds a tool.</summary>
    private sealed class ToolProvider(AITool tool) : AIContextProvider(null, null, null)
    {
        public override IReadOnlyList<string> StateKeys => ["tool-provider"];

        protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken) =>
            new(new AIContext { Tools = [tool] });
    }
}
