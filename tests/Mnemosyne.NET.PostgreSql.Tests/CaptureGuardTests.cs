using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.PostgreSql.Tests;

/// <summary>A capture guard in front of the facade over PostgreSQL: a blocked capture stores nothing, an allowed one is stored.</summary>
public sealed class CaptureGuardTests(PgVectorFixture fixture) : IClassFixture<PgVectorFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ABlockedCaptureLeavesNoRow()
    {
        var (memory, scope) = await CreateAsync();
        var before = await CountAsync(scope);

        var result = await memory.RememberAsync(Capture(scope, "What is the secret?"), Ct);

        Assert.True(result.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Blocked, result.Error.Code);
        Assert.Equal(before, await CountAsync(scope));
        Assert.Equal(0, await CountAsync(scope));
    }

    [Fact]
    public async Task AnAllowedCaptureIsStored()
    {
        var (memory, scope) = await CreateAsync();

        var result = await memory.RememberAsync(Capture(scope, "How do I find it?"), Ct);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(ReasonletCaptureKind.NewReasonlet, result.Value.Kind);
        Assert.Equal(1, await CountAsync(scope));
    }

    private async Task<(ReasonletMemory Memory, ReasonletScope Scope)> CreateAsync()
    {
        var store = fixture.CreateStore();
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        var options = new MnemosyneOptions();
        var memory = new ReasonletMemory(
            store, new ReasonletSelector(options.Ranking, TimeProvider.System), new ConstantEmbeddings(), null, null, options, TimeProvider.System, new SecretGuard());
        return (memory, PgVectorFixture.NewScope());
    }

    private static ReasonletCapture Capture(ReasonletScope scope, string question) => new()
    {
        Scope = scope,
        Question = question,
        Graph = Entries.Graph(),
        Outcome = new ReasonletOutcome(true, TimeSpan.FromSeconds(1), null),
        Provenance = new ReasonletProvenance { Source = "guard-test" },
    };

    private async Task<long> CountAsync(ReasonletScope scope) => await Entries.ScalarAsync<long>(
        fixture.DataSource, "SELECT count(*) FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value);

    /// <summary>A guard that blocks every capture whose question mentions a secret.</summary>
    private sealed class SecretGuard : ICaptureGuard
    {
        public ValueTask<Result<CaptureVerdict, MnemosyneError>> InspectAsync(ReasonletCapture capture, CancellationToken ct) =>
            ValueTask.FromResult<Result<CaptureVerdict, MnemosyneError>>(
                capture.Question.Contains("secret", StringComparison.OrdinalIgnoreCase)
                    ? CaptureVerdict.Block("test-guard", "secret")
                    : CaptureVerdict.Allow("test-guard"));
    }

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
