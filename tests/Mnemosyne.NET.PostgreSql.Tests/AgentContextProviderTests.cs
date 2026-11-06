using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.AgentFramework;
using Xunit;

namespace Mnemosyne.NET.PostgreSql.Tests;

/// <summary>
/// The Agent Framework context provider end to end: a <c>ChatClientAgent</c> over a scripted model, with the real facade and
/// the real PostgreSQL store. Each run gets its own service provider, which is disposed to drain the capture queue before the
/// test asserts. Each test names its agent after a scope of its own, which is the provider's default scope.
/// </summary>
public sealed class AgentContextProviderTests(PgVectorFixture fixture) : IClassFixture<PgVectorFixture>
{
    private const string Question = "Refund order 7";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The model calls orders_get, then payments_refund, then answers.</summary>
    private static readonly string[] Refund = ["orders_get", "payments_refund"];

    [Fact]
    public async Task AgentRunBecomesAReasonletInTheAgentScope()
    {
        var scope = PgVectorFixture.NewScope();

        await RunAsync(scope, requireReview: false, new ScriptedModel(), Refund);

        Assert.Equal(1L, await CountAsync(scope));
        Assert.Equal("mnemosyne.agent", await Entries.ScalarAsync<string>(
            fixture.DataSource, "SELECT source FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value));

        // Named after the tool shape, never after the user's question.
        Assert.Equal("orders_get → payments_refund", await Entries.ScalarAsync<string>(
            fixture.DataSource, "SELECT name FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value));
    }

    [Fact]
    public async Task SecondRunRecallsInjectsAndRecordsAnExecution()
    {
        var scope = PgVectorFixture.NewScope();
        var model = new ScriptedModel();

        await RunAsync(scope, requireReview: false, model, Refund, Refund);

        // Requests: run 1 has three inner calls, run 2 starts at the fourth.
        Assert.Equal(6, model.Instructions.Count);
        Assert.All(model.Instructions.Take(3), instructions => Assert.True(string.IsNullOrEmpty(instructions)));
        Assert.All(model.Instructions.Skip(3), instructions => Assert.False(string.IsNullOrEmpty(instructions)));
        Assert.Equal(1L, await CountAsync(scope));
        Assert.Equal(2L, await Entries.ScalarAsync<long>(
            fixture.DataSource, "SELECT count(*) FROM mnemosyne.reasonlet_executions WHERE scope = $1", scope.Value));
    }

    [Fact]
    public async Task TwoAgentsGetTwoScopes()
    {
        var first = PgVectorFixture.NewScope();
        var second = PgVectorFixture.NewScope();
        var secondModel = new ScriptedModel();

        await RunAsync(first, requireReview: false, new ScriptedModel(), Refund);
        await RunAsync(second, requireReview: false, secondModel, Refund);

        // Nothing of the first scope was recalled into the second agent's run.
        Assert.Equal(3, secondModel.Instructions.Count);
        Assert.All(secondModel.Instructions, instructions => Assert.True(string.IsNullOrEmpty(instructions)));

        // The same shape and question in another scope is stored again, never recalled across scopes.
        Assert.Equal(1L, await CountAsync(first));
        Assert.Equal(1L, await CountAsync(second));
        Assert.Equal(1L, await Entries.ScalarAsync<long>(
            fixture.DataSource, "SELECT count(*) FROM mnemosyne.reasonlet_executions WHERE scope = $1", first.Value));
    }

    [Fact]
    public async Task ParentlessRepeatWithReviewGateStaysOneRow()
    {
        var scope = PgVectorFixture.NewScope();
        var model = new ScriptedModel();

        await RunAsync(scope, requireReview: true, model, Refund, Refund, Refund);

        // A pending Reasonlet is never recalled, so no run carries a parent, and every repeat of the stored shape is an execution.
        Assert.All(model.Instructions, instructions => Assert.True(string.IsNullOrEmpty(instructions)));
        Assert.Equal(1L, await Entries.ScalarAsync<long>(
            fixture.DataSource,
            "SELECT count(*) FROM mnemosyne.reasonlets WHERE scope = $1 AND status = $2",
            scope.Value,
            (short)ReasonletStatus.PendingReview));
        Assert.Equal(1L, await CountAsync(scope));
        Assert.Equal(3L, await Entries.ScalarAsync<long>(
            fixture.DataSource, "SELECT count(*) FROM mnemosyne.reasonlet_executions WHERE scope = $1", scope.Value));
    }

    /// <summary>
    /// Runs each script as one agent run with the same question. Every run gets its own service provider, which is disposed to
    /// drain the capture queue, so the next run recalls what the previous one captured.
    /// </summary>
    private async Task RunAsync(ReasonletScope scope, bool requireReview, ScriptedModel model, params string[][] runs)
    {
        var store = fixture.CreateStore();
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);

        foreach (var run in runs)
        {
            var services = new ServiceCollection();
            services.AddSingleton(fixture.DataSource);
            services.AddMnemosynePostgreSql(new MnemosynePostgreSqlOptions { Dimensions = 3 });
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new ConstantEmbeddings());
            services.AddMnemosyne(new MnemosyneOptions { RequireReview = requireReview });
            services.AddReasonletCapture();
            var provider = services.BuildServiceProvider();
            try
            {
                var agent = new ChatClientAgent(model, new ChatClientAgentOptions
                {
                    Name = scope.Value,
                    ChatOptions = new ChatOptions
                    {
                        Tools = [AIFunctionFactory.Create(() => "order", "orders_get"), AIFunctionFactory.Create(() => "refunded", "payments_refund")],
                    },
                    AIContextProviders = [new ReasonletContextProvider(provider)],
                });

                model.Script = run;
                var response = await agent.RunAsync(Question, cancellationToken: Ct);
                Assert.Equal("Done.", response.Text);
            }
            finally
            {
                // Disposing the provider drains the capture queue, and it runs when an assert fails too.
                await provider.DisposeAsync();
            }
        }
    }

    private Task<long> CountAsync(ReasonletScope scope) =>
        Entries.ScalarAsync<long>(fixture.DataSource, "SELECT count(*) FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value);
}
