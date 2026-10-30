using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using Xunit;

namespace Mnemosyne.NET.PostgreSql.Tests;

/// <summary>A captured chat transcript through the facade over PostgreSQL: new Reasonlet, then an execution, then a new version.</summary>
public sealed class TranscriptCaptureTests(PgVectorFixture fixture) : IClassFixture<PgVectorFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ACapturedTranscriptIsANewReasonletThenAnExecutionThenANewVersion()
    {
        var store = fixture.CreateStore();
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        var options = new MnemosyneOptions();
        var memory = new ReasonletMemory(store, new ReasonletSelector(options.Ranking, TimeProvider.System), new ConstantEmbeddings(), null, null, options);
        var scope = PgVectorFixture.NewScope();

        var first = await RememberAsync(memory, scope, null,
            User("Refund order 7"),
            Call("a1", "orders.get", ("id", "7")), Result("a1"),
            Call("a2", "payments.refund", ("orderId", "7"), ("amount", "20")), Result("a2"),
            new ChatMessage(ChatRole.Assistant, "Refunded 20."));
        Assert.Equal(ReasonletCaptureKind.NewReasonlet, first.Kind);

        var rerun = await RememberAsync(memory, scope, first.Id,
            User("Refund order 9"),
            Call("b1", "orders.get", ("id", "9")), Failed("b1"),
            Call("b2", "orders.get", ("id", "9")), Result("b2"),
            Call("b3", "payments.refund", ("amount", "5"), ("orderId", "9")), Result("b3"));
        Assert.Equal(ReasonletCaptureKind.ExecutionOnly, rerun.Kind);
        Assert.Equal(first.Id, rerun.Id);

        var extended = await RememberAsync(memory, scope, first.Id,
            User("Refund order 11 and tell the customer"),
            Call("c1", "orders.get", ("id", "11")), Result("c1"),
            Call("c2", "payments.refund", ("orderId", "11"), ("amount", "8")), Result("c2"),
            Call("c3", "notify.send", ("to", "customer@example.com")), Result("c3"));
        Assert.Equal(ReasonletCaptureKind.NewVersion, extended.Kind);
        Assert.Equal("1.1", extended.Version);
    }

    private static async Task<ReasonletReceipt> RememberAsync(
        IReasonletMemory memory, ReasonletScope scope, ReasonletId? parent, params ChatMessage[] messages)
    {
        var capture = ReasonletTranscript.ToCapture(
            messages,
            new TranscriptCaptureDetails
            {
                Scope = scope,
                Provenance = new ReasonletProvenance { Source = "transcript-test" },
                Outcome = new ReasonletOutcome(true, TimeSpan.FromSeconds(1), null),
                ParentId = parent,
            });
        Assert.True(capture.IsSuccess);

        var receipt = await memory.RememberAsync(capture.Value, Ct);
        Assert.True(receipt.IsSuccess, receipt.IsFailure ? receipt.Error.Message : null);
        return receipt.Value;
    }

    private static ChatMessage User(string text) => new(ChatRole.User, text);

    private static ChatMessage Call(string id, string name, params (string Name, string Value)[] args) => new(
        ChatRole.Assistant,
        [new FunctionCallContent(id, name, args.ToDictionary(a => a.Name, a => (object?)a.Value))]);

    private static ChatMessage Result(string id) => new(ChatRole.Tool, [new FunctionResultContent(id, "ok")]);

    private static ChatMessage Failed(string id) =>
        new(ChatRole.Tool, [new FunctionResultContent(id, null) { Exception = new TimeoutException() }]);

    /// <summary>An embedding generator that gives every text the same vector.</summary>
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
