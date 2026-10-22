using Mnemosyne.NET.Abstractions;
using Xunit;

namespace Mnemosyne.NET.PostgreSql.Tests;

public sealed class SearchTests(PgVectorFixture fixture) : IClassFixture<PgVectorFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const double Tolerance = 1e-6;

    [Fact]
    public async Task ResultsComeBackNearestFirstWithClampedCosineSimilarity()
    {
        var (store, scope) = await MigratedAsync();
        var opposite = await RememberAsync(store, scope, new float[] { -1, 0, 0 }, "opposite");
        var identical = await RememberAsync(store, scope, new float[] { 1, 0, 0 }, "identical");
        var diagonal = await RememberAsync(store, scope, new float[] { 1, 1, 0 }, "diagonal");
        var orthogonal = await RememberAsync(store, scope, new float[] { 0, 1, 0 }, "orthogonal");

        var result = await store.SearchAsync(scope, new float[] { 1, 0, 0 }, null, 10, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(4, result.Value.Count);
        Assert.Equal(identical, result.Value[0].Reasonlet.Id);
        Assert.Equal(diagonal, result.Value[1].Reasonlet.Id);
        Assert.Equal(1.0, result.Value[0].Similarity, Tolerance);

        // cos 45 degrees = 1/sqrt 2. The cosine distance is 1 - cos, so the similarity is the cosine itself.
        Assert.Equal(Math.Sqrt(0.5), result.Value[1].Similarity, Tolerance);

        // Orthogonal: distance 1, similarity 0. Opposite: distance 2, so 1 - 2 = -1 clamps to 0.
        Assert.Equal(0.0, result.Value[2].Similarity, Tolerance);
        Assert.Equal(0.0, result.Value[3].Similarity, Tolerance);
        Assert.All(result.Value, c => Assert.InRange(c.Similarity, 0.0, 1.0));
        Assert.Equal(opposite, result.Value[3].Reasonlet.Id);
        Assert.Equal("identical", result.Value[0].Reasonlet.Name);
        Assert.Equal(ReasonletStatus.Active, result.Value[0].Reasonlet.Status);
        Assert.Equal("search", result.Value[0].Reasonlet.Graph.Nodes[0].Id);
    }

    [Fact]
    public async Task TheLimitCapsTheResults()
    {
        var (store, scope) = await MigratedAsync();
        var near = await RememberAsync(store, scope, new float[] { 1, 0, 0 }, "near");
        await RememberAsync(store, scope, new float[] { 0, 1, 0 }, "far");

        var result = await store.SearchAsync(scope, new float[] { 1, 0, 0 }, null, 1, Ct);

        Assert.Equal([near], result.Value.Select(c => c.Reasonlet.Id));
    }

    [Fact]
    public async Task OnlyActiveReasonletsAreReturned()
    {
        var (store, scope) = await MigratedAsync();
        var active = await RememberAsync(store, scope, new float[] { 1, 0, 0 }, "active");
        var pending = await RememberAsync(store, scope, new float[] { 1, 0, 0 }, "pending");
        await Entries.ExecuteAsync(
            fixture.DataSource, "UPDATE mnemosyne.reasonlets SET status = $1 WHERE id = $2", (short)ReasonletStatus.PendingReview, pending.Value);

        var result = await store.SearchAsync(scope, new float[] { 1, 0, 0 }, null, 10, Ct);

        Assert.Equal([active], result.Value.Select(c => c.Reasonlet.Id));
    }

    [Fact]
    public async Task ReasonletsFromAnotherScopeAreNeverReturned()
    {
        var (store, scope) = await MigratedAsync();
        var mine = await RememberAsync(store, scope, new float[] { 1, 0, 0 }, "mine");
        await RememberAsync(store, PgVectorFixture.NewScope(), new float[] { 1, 0, 0 }, "theirs");

        var result = await store.SearchAsync(scope, new float[] { 1, 0, 0 }, null, 10, Ct);

        Assert.Equal([mine], result.Value.Select(c => c.Reasonlet.Id));
    }

    [Fact]
    public async Task TheIntentFilterNarrowsAndANullIntentDoesNot()
    {
        var (store, scope) = await MigratedAsync();
        var first = await RememberAsync(store, scope, new float[] { 1, 0, 0 }, "first", "intent one");
        var second = await RememberAsync(store, scope, new float[] { 0, 1, 0 }, "second", "intent two");

        var filtered = await store.SearchAsync(scope, new float[] { 1, 0, 0 }, "intent two", 10, Ct);
        var unfiltered = await store.SearchAsync(scope, new float[] { 1, 0, 0 }, null, 10, Ct);
        var none = await store.SearchAsync(scope, new float[] { 1, 0, 0 }, "intent three", 10, Ct);

        Assert.Equal([second], filtered.Value.Select(c => c.Reasonlet.Id));
        Assert.Equal([first, second], unfiltered.Value.Select(c => c.Reasonlet.Id));
        Assert.True(none.IsSuccess);
        Assert.Empty(none.Value);
    }

    [Fact]
    public async Task MetricsCountRunsSuccessesAndTheLastSuccess()
    {
        var (store, scope) = await MigratedAsync();
        var id = await RememberAsync(store, scope, new float[] { 1, 0, 0 }, "measured");
        var bare = await RememberAsync(store, scope, new float[] { 0, 1, 0 }, "bare");

        // Remember records one successful execution of each; replace them so the numbers are exact.
        await Entries.ExecuteAsync(fixture.DataSource, "DELETE FROM mnemosyne.reasonlet_executions WHERE reasonlet_id = $1 OR reasonlet_id = $2", id.Value, bare.Value);
        await InsertExecutionAsync(scope, id, success: true, "2026-01-01T10:00:00Z");
        await InsertExecutionAsync(scope, id, success: false, "2026-03-01T10:00:00Z");
        await InsertExecutionAsync(scope, id, success: true, "2026-02-01T10:00:00Z");

        var result = await store.SearchAsync(scope, new float[] { 1, 0, 0 }, null, 10, Ct);

        var measured = result.Value[0].Metrics;
        Assert.Equal(3, measured.Runs);
        Assert.Equal(2, measured.Successes);
        Assert.Equal(DateTimeOffset.Parse("2026-02-01T10:00:00Z"), measured.LastSuccessAt);
        Assert.Equal(new ReasonletMetrics(0, 0, null), result.Value[1].Metrics);
    }

    [Fact]
    public async Task InvalidSearchInputsAreValidationErrors()
    {
        var (store, scope) = await MigratedAsync();
        ReadOnlyMemory<float> good = new float[] { 1, 0, 0 };

        AssertValidation("limit: must be from 1 to 200", (await store.SearchAsync(scope, good, null, 0, Ct)).Error);
        AssertValidation("limit: must be from 1 to 200", (await store.SearchAsync(scope, good, null, 201, Ct)).Error);
        AssertValidation("scope.Value: must not be blank", (await store.SearchAsync(new ReasonletScope(" "), good, null, 10, Ct)).Error);
        AssertValidation("intent: must not be blank", (await store.SearchAsync(scope, good, " ", 10, Ct)).Error);
        AssertValidation("embedding: must have 3 values, the store's dimension, but has 2", (await store.SearchAsync(scope, new float[] { 1, 0 }, null, 10, Ct)).Error);
        AssertValidation("embedding: must contain only finite numbers", (await store.SearchAsync(scope, new float[] { 1, float.NaN, 0 }, null, 10, Ct)).Error);
        AssertValidation("embedding: must contain only finite numbers", (await store.SearchAsync(scope, new float[] { 1, 0, float.PositiveInfinity }, null, 10, Ct)).Error);
        Assert.True((await store.SearchAsync(scope, good, null, 1, Ct)).IsSuccess);
        Assert.True((await store.SearchAsync(scope, good, null, 200, Ct)).IsSuccess);
    }

    [Fact]
    public async Task AnEmbeddingThatIsASliceOfAnArrayIsSearchedByItsSliceOnly()
    {
        var (store, scope) = await MigratedAsync();
        var id = await RememberAsync(store, scope, new float[] { 1, 0, 0 }, "target");

        var result = await store.SearchAsync(scope, new float[] { 9, 1, 0, 0, 9 }.AsMemory(1, 3), null, 10, Ct);

        Assert.Equal(1.0, result.Value.Single(c => c.Reasonlet.Id == id).Similarity, Tolerance);
    }

    [Fact]
    public async Task AStoreThatIsNotMigratedIsUnavailable()
    {
        var store = new ReasonletPostgreSqlStore(await fixture.CreateEmptyDatabaseAsync(), new MnemosynePostgreSqlOptions { Dimensions = 3 });

        var result = await store.SearchAsync(PgVectorFixture.NewScope(), new float[] { 1, 0, 0 }, null, 0, Ct);

        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
    }

    [Fact]
    public async Task CancellationThrows()
    {
        var (store, scope) = await MigratedAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.SearchAsync(scope, new float[] { 1, 0, 0 }, null, 10, cancelled.Token));
    }

    private async Task<(ReasonletPostgreSqlStore Store, ReasonletScope Scope)> MigratedAsync()
    {
        var store = fixture.CreateStore();
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        return (store, PgVectorFixture.NewScope());
    }

    private static async Task<ReasonletId> RememberAsync(
        ReasonletPostgreSqlStore store, ReasonletScope scope, float[] embedding, string name, string intent = "Find it")
    {
        // A distinct graph per name keeps each remember a new Reasonlet instead of a repeat of the last one.
        var entry = Entries.Entry(scope, Entries.Graph(name), embedding: embedding) with { Name = name, Intent = intent };
        var result = await store.RememberAsync(entry, Ct);
        Assert.True(result.IsSuccess);
        return result.Value.Id;
    }

    private Task InsertExecutionAsync(ReasonletScope scope, ReasonletId id, bool success, string executedAt) => Entries.ExecuteAsync(
        fixture.DataSource,
        "INSERT INTO mnemosyne.reasonlet_executions (id, reasonlet_id, scope, success, duration_ticks, source, executed_at) VALUES ($1, $2, $3, $4, 0, 'test', $5)",
        Guid.NewGuid(), id.Value, scope.Value, success, DateTimeOffset.Parse(executedAt));

    private static void AssertValidation(string message, MnemosyneError error)
    {
        Assert.Equal(MnemosyneErrorCode.Validation, error.Code);
        Assert.Equal(message, error.Message);
    }
}
