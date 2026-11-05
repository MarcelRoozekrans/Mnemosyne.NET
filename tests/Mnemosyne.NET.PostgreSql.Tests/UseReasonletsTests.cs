using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Mnemosyne.NET.Abstractions;
using Xunit;

namespace Mnemosyne.NET.PostgreSql.Tests;

/// <summary>
/// The chat middleware end to end: <c>UseReasonlets</c> over a scripted model and function invocation, with the real facade
/// and the real PostgreSQL store. Each test drains the capture queue by disposing the service provider before it asserts.
/// </summary>
public sealed class UseReasonletsTests(PgVectorFixture fixture) : IClassFixture<PgVectorFixture>
{
    private const string Injected = "A procedure that worked for a similar question before.";
    private const string Question = "Refund order 7";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FirstRunStoresANewReasonlet()
    {
        var scope = PgVectorFixture.NewScope();
        var model = new ScriptedModel();

        await RunAsync(scope, requireReview: false, model, Refund);

        Assert.Equal(1L, await CountAsync(scope));
        Assert.DoesNotContain(model.Requests[0], m => m.Text.StartsWith(Injected, StringComparison.Ordinal));

        // Named after the tool shape, never after the user's question.
        Assert.Equal("orders.get → payments.refund", await Entries.ScalarAsync<string>(
            fixture.DataSource, "SELECT name FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value));
    }

    [Fact]
    public async Task SameQuestionAgainInjectsAndRecordsAnExecution()
    {
        var scope = PgVectorFixture.NewScope();
        var model = new ScriptedModel();

        await RunAsync(scope, requireReview: false, model, Refund, Refund);

        // Requests: run 1 has three inner calls, run 2 starts at the fourth.
        Assert.DoesNotContain(model.Requests[0], m => m.Text.StartsWith(Injected, StringComparison.Ordinal));
        Assert.Contains(model.Requests[3], m => m.Text.StartsWith(Injected, StringComparison.Ordinal));
        Assert.Equal(1L, await CountAsync(scope));
        Assert.Equal(2, await Entries.ScalarAsync<int>(
            fixture.DataSource,
            "SELECT m.runs FROM mnemosyne.reasonlet_metrics m JOIN mnemosyne.reasonlets r ON r.id = m.reasonlet_id WHERE r.scope = $1",
            scope.Value));
    }

    [Fact]
    public async Task ChangedShapeRecordsANewVersion()
    {
        var scope = PgVectorFixture.NewScope();
        var model = new ScriptedModel();

        await RunAsync(scope, requireReview: false, model, Refund, LookupOnly);

        Assert.Equal(2L, await CountAsync(scope));
        // The first version is the one without a parent; the second names it as its parent.
        var first = await Entries.ScalarAsync<Guid>(
            fixture.DataSource, "SELECT id FROM mnemosyne.reasonlets WHERE scope = $1 AND parent_id IS NULL", scope.Value);
        var parent = await Entries.ScalarAsync<Guid>(
            fixture.DataSource, "SELECT parent_id FROM mnemosyne.reasonlets WHERE scope = $1 AND parent_id IS NOT NULL", scope.Value);
        Assert.Equal(first, parent);
    }

    [Fact]
    public async Task WithReviewGatePendingCaptureIsNotInjected()
    {
        var scope = PgVectorFixture.NewScope();
        var model = new ScriptedModel();

        await RunAsync(scope, requireReview: true, model, Refund, Refund);

        Assert.All(model.Requests, request => Assert.DoesNotContain(request, m => m.Text.StartsWith(Injected, StringComparison.Ordinal)));

        // Nothing was recalled, so the second run carries no parent. Its shape is the stored pending one, so it is
        // recorded as a second execution of that Reasonlet rather than stored again.
        Assert.Equal(1L, await Entries.ScalarAsync<long>(
            fixture.DataSource,
            "SELECT count(*) FROM mnemosyne.reasonlets WHERE scope = $1 AND status = $2",
            scope.Value,
            (short)ReasonletStatus.PendingReview));
        Assert.Equal(1L, await CountAsync(scope));
        Assert.Equal(2L, await Entries.ScalarAsync<long>(
            fixture.DataSource, "SELECT count(*) FROM mnemosyne.reasonlet_executions WHERE scope = $1", scope.Value));
    }

    /// <summary>The model calls orders.get, then payments.refund, then answers.</summary>
    private static readonly string[] Refund = ["orders.get", "payments.refund"];

    /// <summary>The model calls orders.get, then answers.</summary>
    private static readonly string[] LookupOnly = ["orders.get"];

    /// <summary>
    /// Runs each script as one chat call with the same question. Every run gets its own service provider, which is disposed
    /// to drain the capture queue, so the next run recalls what the previous one captured.
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
            services.AddReasonletCapture(new ReasonletCaptureOptions { Source = "use-reasonlets-test" });
            var provider = services.BuildServiceProvider();
            try
            {
                var client = new ChatClientBuilder(model)
                    .UseReasonlets(new ReasonletChatOptions { DefaultScope = scope })
                    .UseFunctionInvocation()
                    .Build(provider);
                var options = new ChatOptions
                {
                    Tools = [AIFunctionFactory.Create(() => "order", "orders.get"), AIFunctionFactory.Create(() => "refunded", "payments.refund")],
                }.WithReasonletScope(scope);

                model.Script = run;
                var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, Question)], options, Ct);
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

    /// <summary>
    /// A model that calls the tools of <see cref="Script"/> in turn, one per request, then answers with plain text. It
    /// records the messages of every request.
    /// </summary>
    private sealed class ScriptedModel : IChatClient
    {
        private int _step;

        public string[] Script { get; set; } = [];

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Requests.Add([.. messages]);
            if (_step < Script.Length)
            {
                var name = Script[_step++];
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call-{Requests.Count}", name)])));
            }

            _step = 0;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>An embedding generator that gives every text the same vector, so a repeated question has similarity 1.</summary>
    private sealed class ConstantEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(_ => new Embedding<float>(new float[] { 1f, 0.5f, -0.25f }))));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
