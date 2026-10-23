using System.Globalization;
using Mnemosyne.NET.Abstractions;
using Npgsql;
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
        var success = (await store.AppendExecutionAsync(scope, id, new ReasonletOutcome(true, TimeSpan.Zero, null), Entries.Provenance(), Ct)).Value;
        Assert.True((await store.AppendExecutionAsync(scope, id, new ReasonletOutcome(false, TimeSpan.Zero, null), Entries.Provenance(), Ct)).IsSuccess);

        // The store never leaves a Reasonlet without a metrics row; search still reads a missing one as no runs.
        await Entries.ExecuteAsync(fixture.DataSource, "DELETE FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1", bare.Value);

        var result = await store.SearchAsync(scope, new float[] { 1, 0, 0 }, null, 10, Ct);

        var measured = result.Value[0].Metrics;
        Assert.Equal(3, measured.Runs);
        Assert.Equal(2, measured.Successes);
        Assert.Equal(
            await Entries.ScalarAsync<DateTime>(fixture.DataSource, "SELECT executed_at FROM mnemosyne.reasonlet_executions WHERE id = $1", success.Value),
            measured.LastSuccessAt!.Value.UtcDateTime);
        var missing = result.Value[1].Metrics;
        var created = await Entries.ScalarAsync<DateTime>(
            fixture.DataSource, "SELECT created_at FROM mnemosyne.reasonlets WHERE id = $1", bare.Value);
        Assert.Equal(0, missing.Runs);
        Assert.Equal(0, missing.Successes);
        Assert.Null(missing.LastSuccessAt);
        Assert.Equal(created, missing.LastExecutedAt.UtcDateTime);
    }

    [Fact]
    public async Task SearchReturnsLastExecutedAt()
    {
        var (store, scope) = await MigratedAsync();
        var id = await RememberAsync(store, scope, new float[] { 1, 0, 0 }, "measured");
        Assert.True((await store.AppendExecutionAsync(scope, id, new ReasonletOutcome(false, TimeSpan.Zero, null), Entries.Provenance(), Ct)).IsSuccess);

        var result = await store.SearchAsync(scope, new float[] { 1, 0, 0 }, null, 10, Ct);

        Assert.Equal(
            await Entries.ScalarAsync<DateTime>(fixture.DataSource, "SELECT last_executed_at::timestamp FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1", id.Value),
            result.Value.Single().Metrics.LastExecutedAt.UtcDateTime);
    }

    [Fact]
    public async Task SearchReturnsTheParentsMetricsAndNullForARoot()
    {
        var (store, scope) = await MigratedAsync();
        var root = await RememberAsync(store, scope, new float[] { 1, 0, 0 }, "root");
        Assert.True((await store.AppendExecutionAsync(scope, root, new ReasonletOutcome(false, TimeSpan.Zero, null), Entries.Provenance(), Ct)).IsSuccess);
        var child = await store.RememberAsync(
            Entries.Entry(scope, Entries.Graph("child"), root, embedding: new float[] { 0, 1, 0 }), Ct);
        Assert.True(child.IsSuccess);

        var result = await store.SearchAsync(scope, new float[] { 1, 0, 0 }, null, 10, Ct);

        var rootHit = result.Value.Single(c => c.Reasonlet.Id == root);
        var childHit = result.Value.Single(c => c.Reasonlet.Id == child.Value.Id);
        Assert.Null(rootHit.ParentMetrics);
        var parent = childHit.ParentMetrics!.Value;
        Assert.Equal(
            await Entries.ScalarAsync<int>(fixture.DataSource, "SELECT runs FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1", root.Value), parent.Runs);
        Assert.Equal(
            await Entries.ScalarAsync<int>(fixture.DataSource, "SELECT successes FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1", root.Value), parent.Successes);
        Assert.Equal(
            await Entries.ScalarAsync<DateTime>(fixture.DataSource, "SELECT last_success_at::timestamp FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1", root.Value),
            parent.LastSuccessAt!.Value.UtcDateTime);
        Assert.Equal(
            await Entries.ScalarAsync<DateTime>(fixture.DataSource, "SELECT last_executed_at::timestamp FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1", root.Value),
            parent.LastExecutedAt.UtcDateTime);
        Assert.NotEqual(childHit.Metrics, parent);
    }

    [Fact]
    public async Task SearchReturnsNullParentMetricsAfterTheParentIsErased()
    {
        var (store, scope) = await MigratedAsync();
        var parentProvenance = new ReasonletProvenance { Source = "test", SubjectId = "erased" };
        var parent = await store.RememberAsync(
            Entries.Entry(scope, Entries.Graph("parent"), embedding: new float[] { 1, 0, 0 }, provenance: parentProvenance), Ct);
        var child = await store.RememberAsync(
            Entries.Entry(scope, Entries.Graph("child"), parent.Value.Id, new float[] { 0, 1, 0 }), Ct);
        Assert.True(child.IsSuccess);
        Assert.True((await store.EraseAsync(new ReasonletErasure(scope, "erased"), Ct)).IsSuccess);

        var result = await store.SearchAsync(scope, new float[] { 1, 0, 0 }, null, 10, Ct);

        var hit = Assert.Single(result.Value);
        Assert.Equal(child.Value.Id, hit.Reasonlet.Id);
        Assert.Null(hit.ParentMetrics);
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
        AssertValidation("embedding: must have a nonzero, finite norm, or cosine similarity is undefined", (await store.SearchAsync(scope, new float[] { 0, 0, 0 }, null, 10, Ct)).Error);
        AssertValidation("embedding: must have a nonzero, finite norm, or cosine similarity is undefined", (await store.SearchAsync(scope, new float[] { 0, 1e20f, 0 }, null, 10, Ct)).Error);
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

    [Fact]
    public async Task AScopeWithASmallShareOfTheTableStillGetsItsLimitThroughTheIndex()
    {
        // enable_seqscan is off and the scoped btree indexes are dropped, so the planner has only the HNSW index left. A
        // plain index scan would collect about ef_search nearest rows of the whole table and filter them by scope
        // afterwards. UNIQUE (scope, id) goes too, with the foreign keys that need it, in this throwaway database.
        var dataSource = await fixture.CreateEmptyDatabaseAsync("-c enable_seqscan=off");
        var store = new ReasonletPostgreSqlStore(dataSource, new MnemosynePostgreSqlOptions { Dimensions = 3 });
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        await Entries.ExecuteAsync(dataSource, "ALTER TABLE mnemosyne.reasonlets DROP CONSTRAINT reasonlets_scope_id_key CASCADE");
        foreach (var index in new[] { "scope_status_intent", "scope_parent_id", "scope_subject_id" })
        {
            await Entries.ExecuteAsync(dataSource, $"DROP INDEX mnemosyne.reasonlets_{index}");
        }

        var random = new Random(42);
        var dominant = PgVectorFixture.NewScope();
        var minority = PgVectorFixture.NewScope();
        await SeedAsync(dataSource, dominant, 2000, random);
        var minorityVectors = await SeedAsync(dataSource, minority, 60, random);
        float[] query = [1f, 0.3f, -0.2f];

        var result = await store.SearchAsync(minority, query, null, 50, Ct);

        Assert.True(result.IsSuccess);
        var expected = minorityVectors.OrderByDescending(v => Cosine(v.Vector, query)).Take(50).Select(v => v.Id).ToArray();
        Assert.Equal(expected, result.Value.Select(c => c.Reasonlet.Id.Value));
    }

    private static async Task<List<(Guid Id, float[] Vector)>> SeedAsync(NpgsqlDataSource dataSource, ReasonletScope scope, int count, Random random)
    {
        var rows = Enumerable.Range(0, count)
            .Select(_ => (Id: Guid.NewGuid(), Vector: new[] { Next(random), Next(random), Next(random) }))
            .ToList();
        await using var command = dataSource.CreateCommand(
            "INSERT INTO mnemosyne.reasonlets (id, scope, name, intent, version, graph, content_hash, status, source, created_at, embedding) "
            + "SELECT i, $2, 'n', 'intent', '1.0', $3, repeat('0', 64), 1, 'seed', now(), v::vector FROM unnest($1::uuid[], $4::text[]) AS t (i, v)");
        command.Parameters.Add(new NpgsqlParameter { Value = rows.Select(r => r.Id).ToArray() });
        command.Parameters.Add(new NpgsqlParameter { Value = scope.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = ReasonletGraphJson.Serialize(Entries.Graph()) });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = rows.Select(r => string.Create(CultureInfo.InvariantCulture, $"[{r.Vector[0]:R},{r.Vector[1]:R},{r.Vector[2]:R}]")).ToArray(),
        });
        await command.ExecuteNonQueryAsync(Ct);
        return rows;
    }

    private static float Next(Random random) => (float)((random.NextDouble() * 2) - 1);

    private static double Cosine(float[] a, float[] b) =>
        ((a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]))
        / (Math.Sqrt((a[0] * a[0]) + (a[1] * a[1]) + (a[2] * a[2])) * Math.Sqrt((b[0] * b[0]) + (b[1] * b[1]) + (b[2] * b[2])));

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

    private static void AssertValidation(string message, MnemosyneError error)
    {
        Assert.Equal(MnemosyneErrorCode.Validation, error.Code);
        Assert.Equal(message, error.Message);
    }
}
