using Mnemosyne.NET.Abstractions;
using Xunit;

namespace Mnemosyne.NET.PostgreSql.Tests;

/// <summary>Reading everything a subject left in a scope, against a real database.</summary>
public sealed class SubjectExportTests(PgVectorFixture fixture) : IClassFixture<PgVectorFixture>
{
    private const string Subject = "subject-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AllOfASubjectsReasonletsComeBackInEveryStatus()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var active = await RememberAsync(store, scope, "a");
        var pending = await RememberAsync(store, scope, "b", review: true);
        var version = await RememberAsync(store, scope, "c", parent: active.Id);

        var listed = (await store.ListSubjectReasonletsAsync(scope, Subject, 10, null, Ct)).Value;

        Assert.Equal(new[] { active.Id, pending.Id, version.Id }.OrderBy(i => i.Value), listed.Select(r => r.Definition.Id));
        foreach (var record in listed)
        {
            var expected = (await store.GetAsync(scope, record.Definition.Id, Ct)).Value;
            var actual = record.Definition;
            Assert.Equal(
                (expected.Name, expected.Intent, expected.Version, expected.Status, expected.ParentId, expected.CreatedAt),
                (actual.Name, actual.Intent, actual.Version, actual.Status, actual.ParentId, actual.CreatedAt));
            Assert.Equal(expected.Provenance.SubjectId, actual.Provenance.SubjectId);
            Assert.Equal(expected.Provenance.Tags, actual.Provenance.Tags);
            Assert.Equal(expected.Graph.Nodes.Count, actual.Graph.Nodes.Count);
        }

        Assert.Equal(ReasonletStatus.PendingReview, listed.Single(r => r.Definition.Id == pending.Id).Definition.Status);
    }

    [Fact]
    public async Task OnlyTheAskedScopeAndSubjectAreRead()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var other = PgVectorFixture.NewScope();
        var mine = await RememberAsync(store, scope, "a");
        await RememberAsync(store, other, "a");
        await RememberAsync(store, scope, "b", subject: "subject-2");

        var listed = (await store.ListSubjectReasonletsAsync(scope, Subject, 10, null, Ct)).Value;

        Assert.Equal([mine.Id], listed.Select(r => r.Definition.Id));
    }

    [Fact]
    public async Task ASubjectIdThatDiffersInCaseMatchesNothing()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        await RememberAsync(store, scope, "a");

        var listed = await store.ListSubjectReasonletsAsync(scope, "SUBJECT-1", 10, null, Ct);

        Assert.True(listed.IsSuccess);
        Assert.Empty(listed.Value);
    }

    [Theory]
    [InlineData(5, 2, new[] { 2, 2, 1, 0 })]
    [InlineData(4, 2, new[] { 2, 2, 0 })]
    public async Task PagesCoverTheSetWithoutRepeatOrGap(int count, int limit, int[] sizes)
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var all = new List<ReasonletId>();
        for (var i = 0; i < count; i++)
        {
            all.Add((await RememberAsync(store, scope, $"v{i}")).Id);
        }

        var seen = new List<ReasonletId>();
        var pageSizes = new List<int>();
        ReasonletId? after = null;
        while (true)
        {
            var page = (await store.ListSubjectReasonletsAsync(scope, Subject, limit, after, Ct)).Value;
            pageSizes.Add(page.Count);
            if (page.Count == 0)
            {
                break;
            }

            seen.AddRange(page.Select(r => r.Definition.Id));
            after = page[^1].Definition.Id;
        }

        Assert.Equal(sizes, pageSizes);
        Assert.Equal(all.OrderBy(i => i.Value), seen);
    }

    [Fact]
    public async Task AReasonletWithoutAMetricsRowReadsAsZeroRuns()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var bare = await RememberAsync(store, scope, "bare");
        await Entries.ExecuteAsync(fixture.DataSource, "DELETE FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1", bare.Id.Value);

        var record = (await store.ListSubjectReasonletsAsync(scope, Subject, 10, null, Ct)).Value.Single();

        var metrics = record.Metrics;
        Assert.Equal((0, 0, 0, 0), (metrics.Runs, metrics.Successes, metrics.JudgedRuns, metrics.JudgedSuccesses));
        Assert.Null(metrics.LastSuccessAt);
        Assert.Equal(record.Definition.CreatedAt, metrics.LastExecutedAt);
    }

    [Fact]
    public async Task ASubjectWithNothingReturnsAnEmptySuccess()
    {
        var store = await MigratedStoreAsync();

        var listed = await store.ListSubjectReasonletsAsync(PgVectorFixture.NewScope(), Subject, 10, null, Ct);

        Assert.True(listed.IsSuccess);
        Assert.Empty(listed.Value);
    }

    [Fact]
    public async Task ValidationAndCancellation()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();

        var results = new[]
        {
            await store.ListSubjectReasonletsAsync(default, Subject, 10, null, Ct),
            await store.ListSubjectReasonletsAsync(scope, " ", 10, null, Ct),
            await store.ListSubjectReasonletsAsync(scope, Subject, 0, null, Ct),
            await store.ListSubjectReasonletsAsync(scope, Subject, 101, null, Ct),
            await store.ListSubjectReasonletsAsync(scope, Subject, 10, default(ReasonletId), Ct),
        };

        Assert.All(results, r => Assert.Equal(MnemosyneErrorCode.Validation, r.Error.Code));
        Assert.True((await store.ListSubjectReasonletsAsync(scope, Subject, 100, null, Ct)).IsSuccess);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.ListSubjectReasonletsAsync(scope, Subject, 10, null, cancelled.Token));
    }

    [Fact]
    public async Task AnUnmigratedOrFailedStoreAnswersUnavailable()
    {
        var store = new ReasonletPostgreSqlStore(await fixture.CreateEmptyDatabaseAsync(), new MnemosynePostgreSqlOptions { Dimensions = 3 });

        var result = await store.ListSubjectReasonletsAsync(PgVectorFixture.NewScope(), Subject, 10, null, Ct);

        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
    }

    private async Task<ReasonletPostgreSqlStore> MigratedStoreAsync()
    {
        var store = fixture.CreateStore();
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        return store;
    }

    private static async Task<ReasonletReceipt> RememberAsync(
        ReasonletPostgreSqlStore store, ReasonletScope scope, string variant, ReasonletId? parent = null, bool review = false, string subject = Subject)
    {
        var provenance = Entries.Provenance() with { SubjectId = subject };
        var entry = Entries.Entry(scope, Entries.Graph(variant), parent, provenance: provenance) with { ReviewRequired = review };
        var result = await store.RememberAsync(entry, Ct);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }
}
