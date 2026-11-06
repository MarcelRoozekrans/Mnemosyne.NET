using Mnemosyne.NET.Abstractions;
using Xunit;

namespace Mnemosyne.NET.PostgreSql.Tests;

/// <summary>The lineage id that a search reports for each candidate, resolved by the real PostgreSQL store.</summary>
public sealed class LineageTests(PgVectorFixture fixture) : IClassFixture<PgVectorFixture>
{
    private static readonly float[] Query = [1, 0, 0];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARootReportsItsOwnId()
    {
        var (store, scope) = await MigratedAsync();
        var root = await RememberAsync(store, scope, null, "root");

        var result = await store.SearchAsync(scope, Query, null, 10, Ct);

        Assert.True(result.IsSuccess);
        var only = Assert.Single(result.Value);
        Assert.Equal((root, root), (only.Reasonlet.Id, only.LineageId));
    }

    [Fact]
    public async Task SiblingsAndAGrandchildReportTheRootsId()
    {
        var (store, scope) = await MigratedAsync();
        var root = await RememberAsync(store, scope, null, "root");
        var child = await RememberAsync(store, scope, root, "child");
        var grandchild = await RememberAsync(store, scope, child, "grandchild");
        var sibling = await RememberAsync(store, scope, root, "sibling");

        var result = await store.SearchAsync(scope, Query, null, 10, Ct);

        Assert.True(result.IsSuccess);
        var lineages = result.Value.ToDictionary(c => c.Reasonlet.Id, c => c.LineageId);
        Assert.Equal(4, lineages.Count);
        Assert.Equal([root, root, root, root], new[] { lineages[root], lineages[child], lineages[grandchild], lineages[sibling] });
    }

    [Fact]
    public async Task AVersionWhoseParentWasErasedReportsItsOwnId()
    {
        var (store, scope) = await MigratedAsync();
        var parentEntry = Entries.Entry(scope, Entries.Graph("parent"), embedding: Query, provenance: Entries.Provenance() with { SubjectId = "gone" });
        var parent = (await store.RememberAsync(parentEntry, Ct)).Value.Id;
        var child = await RememberAsync(store, scope, parent, "child");
        var before = Assert.Single((await store.SearchAsync(scope, Query, null, 10, Ct)).Value, c => c.Reasonlet.Id == child);
        Assert.Equal(parent, before.LineageId);
        Assert.True((await store.EraseAsync(new ReasonletErasure(scope, "gone"), Ct)).IsSuccess);

        var result = await store.SearchAsync(scope, Query, null, 10, Ct);

        Assert.True(result.IsSuccess);
        var only = Assert.Single(result.Value);
        Assert.Equal((child, child), (only.Reasonlet.Id, only.LineageId));
    }

    [Fact]
    public async Task TwoLineagesOfOneScopeStayApart()
    {
        var (store, scope) = await MigratedAsync();
        var rootA = await RememberAsync(store, scope, null, "root-a");
        var childA = await RememberAsync(store, scope, rootA, "child-a");
        var rootB = await RememberAsync(store, scope, null, "root-b");
        var childB = await RememberAsync(store, scope, rootB, "child-b");

        var result = await store.SearchAsync(scope, Query, null, 10, Ct);

        Assert.True(result.IsSuccess);
        var lineages = result.Value.ToDictionary(c => c.Reasonlet.Id, c => c.LineageId);
        Assert.Equal(4, lineages.Count);
        Assert.Equal([rootA, rootA, rootB, rootB], new[] { lineages[rootA], lineages[childA], lineages[rootB], lineages[childB] });
    }

    [Fact]
    public async Task ASearchWithNoRowsReturnsAnEmptySuccess()
    {
        var (store, scope) = await MigratedAsync();

        var result = await store.SearchAsync(scope, Query, null, 10, Ct);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact(Timeout = 30_000)]
    public async Task ALineageTwoHundredVersionsDeepReportsItsRootAndReturnsPromptly()
    {
        var (store, scope) = await MigratedAsync();
        var (root, _) = await Chains.InsertAsync(fixture.DataSource, scope, 200, ReasonletGraphJson.Serialize(Entries.Graph()));

        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await store.SearchAsync(scope, Query, null, 10, Ct);
        started.Stop();

        Assert.True(result.IsSuccess);
        Assert.Equal(10, result.Value.Count);
        Assert.All(result.Value, c => Assert.Equal(new ReasonletId(root), c.LineageId));
        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Fact(Timeout = 30_000)]
    public async Task ALineageFourThousandVersionsDeepIsOneLinearWalk()
    {
        // The same depth and bound as the RememberTests walk. A planner that flattens the LATERAL step into a join is quadratic here.
        var (store, scope) = await MigratedAsync();
        var (root, leaf) = await Chains.InsertAsync(fixture.DataSource, scope, 4000, ReasonletGraphJson.Serialize(Entries.Graph()));
        await Entries.ExecuteAsync(fixture.DataSource, "UPDATE mnemosyne.reasonlets SET embedding = '[0,1,0]' WHERE scope = $1 AND id <> $2", scope.Value, leaf);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await store.SearchAsync(scope, Query, null, 1, Ct);
        started.Stop();

        Assert.True(result.IsSuccess);
        var only = Assert.Single(result.Value);
        Assert.Equal((leaf, new ReasonletId(root)), (only.Reasonlet.Id.Value, only.LineageId));
        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AnIntentFilteredSearchStillReportsTheRoot()
    {
        var (store, scope) = await MigratedAsync();
        var root = await RememberAsync(store, scope, null, "root", "intent-x");
        var child = await RememberAsync(store, scope, root, "child", "intent-y");

        var result = await store.SearchAsync(scope, Query, "intent-y", 10, Ct);

        Assert.True(result.IsSuccess);
        var only = Assert.Single(result.Value);
        Assert.Equal((child, root), (only.Reasonlet.Id, only.LineageId));
    }

    [Fact(Timeout = 30_000)]
    public async Task AParentCycleDoesNotHangTheSearch()
    {
        // The store never creates a cycle, but hand-written SQL can. No row of a cycle has a null parent, so the lineage
        // query finds no root for it and each candidate falls back to its own id.
        var (store, scope) = await MigratedAsync();
        var a = await RememberAsync(store, scope, null, "a");
        var b = await RememberAsync(store, scope, a, "b");
        await Entries.ExecuteAsync(fixture.DataSource, "UPDATE mnemosyne.reasonlets SET parent_id = $1 WHERE id = $2", b.Value, a.Value);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await store.SearchAsync(scope, Query, null, 10, Ct);
        started.Stop();

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Count);
        Assert.All(result.Value, c => Assert.Equal(c.Reasonlet.Id, c.LineageId));
        Assert.InRange(started.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    private static async Task<ReasonletId> RememberAsync(ReasonletPostgreSqlStore store, ReasonletScope scope, ReasonletId? parent, string name, string intent = "i")
    {
        // A distinct graph per name keeps each remember a new Reasonlet instead of a repeat of the last one.
        var entry = Entries.Entry(scope, Entries.Graph(name), parent, Query) with { Name = name, Intent = intent };
        var result = await store.RememberAsync(entry, Ct);
        Assert.True(result.IsSuccess);
        return result.Value.Id;
    }

    private async Task<(ReasonletPostgreSqlStore Store, ReasonletScope Scope)> MigratedAsync()
    {
        var store = fixture.CreateStore();
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        return (store, PgVectorFixture.NewScope());
    }
}
