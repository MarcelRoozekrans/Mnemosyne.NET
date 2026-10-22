using Mnemosyne.NET.Abstractions;
using Xunit;

namespace Mnemosyne.NET.PostgreSql.Tests;

public sealed class GetTests(PgVectorFixture fixture) : IClassFixture<PgVectorFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetReturnsEveryFieldOfAStoredReasonlet()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var entry = Entries.Entry(scope);
        var before = DateTimeOffset.UtcNow;

        var receipt = (await store.RememberAsync(entry, Ct)).Value;
        var result = await store.GetAsync(scope, receipt.Id, Ct);

        Assert.True(result.IsSuccess);
        var definition = result.Value;
        Assert.Equal(receipt.Id, definition.Id);
        Assert.Equal(scope, definition.Scope);
        Assert.Equal("Find it", definition.Name);
        Assert.Equal("find-it", definition.Intent);
        Assert.Equal("1.0", definition.Version);
        Assert.Equal(entry.Capture.Graph.ComputeContentHash().Value, definition.Graph.ComputeContentHash().Value);
        Assert.Equal(ReasonletGraphJson.Serialize(entry.Capture.Graph), ReasonletGraphJson.Serialize(definition.Graph));
        Assert.Equal(ReasonletStatus.Active, definition.Status);
        Assert.Null(definition.ParentId);
        Assert.Equal("test-source", definition.Provenance.Source);
        Assert.Equal("run-1", definition.Provenance.RunId);
        Assert.Equal("subject-1", definition.Provenance.SubjectId);
        Assert.Equal(
            entry.Capture.Provenance.Tags!.OrderBy(t => t.Key, StringComparer.Ordinal),
            definition.Provenance.Tags!.OrderBy(t => t.Key, StringComparer.Ordinal));
        Assert.InRange(definition.CreatedAt, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.True(definition.Validate().IsSuccess);
    }

    [Fact]
    public async Task GetReturnsNullProvenanceMembersAndTheParent()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var root = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("root")), Ct)).Value;
        var bare = new ReasonletProvenance { Source = "bare" };
        var child = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("child"), root.Id, provenance: bare), Ct)).Value;

        var definition = (await store.GetAsync(scope, child.Id, Ct)).Value;

        Assert.Equal(root.Id, definition.ParentId);
        Assert.Equal("1.1", definition.Version);
        Assert.Equal(bare, definition.Provenance);
    }

    [Fact]
    public async Task EmptyTagsComeBackEmptyRatherThanNull()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var provenance = new ReasonletProvenance { Source = "s", Tags = new Dictionary<string, string>() };
        var receipt = (await store.RememberAsync(Entries.Entry(scope, provenance: provenance), Ct)).Value;

        var definition = (await store.GetAsync(scope, receipt.Id, Ct)).Value;

        Assert.NotNull(definition.Provenance.Tags);
        Assert.Empty(definition.Provenance.Tags);
    }

    [Fact]
    public async Task AReasonletInAnotherScopeIsNotFound()
    {
        var store = await MigratedStoreAsync();
        var receipt = (await store.RememberAsync(Entries.Entry(PgVectorFixture.NewScope()), Ct)).Value;

        var result = await store.GetAsync(PgVectorFixture.NewScope(), receipt.Id, Ct);

        Assert.Equal(MnemosyneErrorCode.NotFound, result.Error.Code);
    }

    [Fact]
    public async Task AnUnknownIdIsNotFound()
    {
        var store = await MigratedStoreAsync();

        var result = await store.GetAsync(PgVectorFixture.NewScope(), ReasonletId.New(), Ct);

        Assert.Equal(MnemosyneErrorCode.NotFound, result.Error.Code);
    }

    [Fact]
    public async Task ADefaultIdIsAValidationErrorNotNotFound()
    {
        var store = await MigratedStoreAsync();

        var result = await store.GetAsync(PgVectorFixture.NewScope(), default, Ct);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal("id: must not be the default id", result.Error.Message);
    }

    [Fact]
    public async Task ADefaultScopeIsAValidationError()
    {
        var store = await MigratedStoreAsync();

        var result = await store.GetAsync(default, ReasonletId.New(), Ct);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal("scope.Value: must not be blank", result.Error.Message);
    }

    [Fact]
    public async Task AStoredGraphThatNoLongerReadsIsABugAndThrows()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var receipt = (await store.RememberAsync(Entries.Entry(scope), Ct)).Value;
        await Entries.ExecuteAsync(fixture.DataSource, "UPDATE mnemosyne.reasonlets SET graph = '{}' WHERE id = $1", receipt.Id.Value);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.GetAsync(scope, receipt.Id, Ct));

        Assert.Contains(receipt.Id.Value.ToString(), thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationThrows()
    {
        var store = await MigratedStoreAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.GetAsync(PgVectorFixture.NewScope(), ReasonletId.New(), cancelled.Token));
    }

    private async Task<ReasonletPostgreSqlStore> MigratedStoreAsync()
    {
        var store = fixture.CreateStore();
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        return store;
    }
}
