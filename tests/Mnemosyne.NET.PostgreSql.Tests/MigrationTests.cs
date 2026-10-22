using Mnemosyne.NET.Abstractions;
using Npgsql;
using Xunit;

namespace Mnemosyne.NET.PostgreSql.Tests;

public sealed class MigrationTests(PgVectorFixture fixture) : IClassFixture<PgVectorFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MigrateCreatesTheTablesTheDimensionedEmbeddingAndTheHnswIndex()
    {
        var result = await fixture.CreateStore().MigrateAsync(Ct);

        Assert.True(result.IsSuccess);
        var tables = await ScalarAsync<long>(
            fixture.DataSource,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'mnemosyne' "
            + "AND table_name IN ('reasonlets', 'reasonlet_executions', 'reasonlet_feedback', 'settings')");
        Assert.Equal(4, tables);
        Assert.Equal("vector(3)", await EmbeddingTypeAsync(fixture.DataSource));
        var index = await HnswIndexAsync(fixture.DataSource);
        Assert.Contains("USING hnsw (embedding vector_cosine_ops)", index, StringComparison.Ordinal);
        Assert.Contains("m='16'", index, StringComparison.Ordinal);
        Assert.Contains("ef_construction='64'", index, StringComparison.Ordinal);
        Assert.Equal("3", await ScalarAsync<string>(fixture.DataSource, "SELECT value FROM mnemosyne.settings WHERE key = 'dimensions'"));
    }

    [Fact]
    public async Task MigrateAppliesTheConfiguredHnswParameters()
    {
        var dataSource = await fixture.CreateEmptyDatabaseAsync();
        var store = new ReasonletPostgreSqlStore(
            dataSource, new MnemosynePostgreSqlOptions { Dimensions = 5, HnswM = 8, HnswEfConstruction = 32 });

        var result = await store.MigrateAsync(Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal("vector(5)", await EmbeddingTypeAsync(dataSource));
        var index = await HnswIndexAsync(dataSource);
        Assert.Contains("m='8'", index, StringComparison.Ordinal);
        Assert.Contains("ef_construction='32'", index, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSchemaHasTheSpecifiedForeignKeysAndIndexes()
    {
        Assert.True((await fixture.CreateStore().MigrateAsync(Ct)).IsSuccess);

        // confdeltype: n = SET NULL, c = CASCADE.
        var foreignKeys = await RowsAsync(
            fixture.DataSource,
            "SELECT conrelid::regclass::text || '.' || a.attname || ' -> ' || confrelid::regclass::text || ' ' || confdeltype::text "
            + "FROM pg_constraint c JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = c.conkey[1] "
            + "WHERE c.contype = 'f' AND c.connamespace = 'mnemosyne'::regnamespace ORDER BY 1");
        Assert.Equal(
            [
                "mnemosyne.reasonlet_executions.reasonlet_id -> mnemosyne.reasonlets c",
                "mnemosyne.reasonlet_feedback.execution_id -> mnemosyne.reasonlet_executions c",
                "mnemosyne.reasonlets.parent_id -> mnemosyne.reasonlets n",
            ],
            foreignKeys);

        var indexes = await RowsAsync(
            fixture.DataSource,
            "SELECT tablename || ':' || substring(indexdef from ' USING .*$') FROM pg_indexes "
            + "WHERE schemaname = 'mnemosyne' AND indexname NOT LIKE '%_pkey' ORDER BY 1");
        Assert.Equal(
            [
                "reasonlet_executions: USING btree (reasonlet_id)",
                "reasonlet_executions: USING btree (scope, subject_id)",
                "reasonlets: USING btree (parent_id)",
                "reasonlets: USING btree (scope, content_hash)",
                "reasonlets: USING btree (scope, parent_id)",
                "reasonlets: USING btree (scope, status, intent)",
                "reasonlets: USING btree (scope, subject_id)",
                "reasonlets: USING hnsw (embedding vector_cosine_ops) WITH (m='16', ef_construction='64')",
            ],
            indexes);
    }

    [Fact]
    public async Task MigratingAgainSucceedsAndChangesNothing()
    {
        var store = fixture.CreateStore();
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        var before = await SnapshotAsync(fixture.DataSource);

        var again = await fixture.CreateStore().MigrateAsync(Ct);

        Assert.True(again.IsSuccess);
        Assert.Equal(before, await SnapshotAsync(fixture.DataSource));
    }

    [Fact]
    public async Task ConcurrentFirstMigrationsBothSucceedAndApplyTheDimensionOnce()
    {
        var dataSource = await fixture.CreateEmptyDatabaseAsync();
        var options = new MnemosynePostgreSqlOptions { Dimensions = 3 };

        var results = await Task.WhenAll(
            new ReasonletPostgreSqlStore(dataSource, options).MigrateAsync(Ct).AsTask(),
            new ReasonletPostgreSqlStore(dataSource, options).MigrateAsync(Ct).AsTask());

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(1, await ScalarAsync<long>(dataSource, "SELECT count(*) FROM mnemosyne.settings WHERE key = 'dimensions'"));
        Assert.Equal(1, await ScalarAsync<long>(
            dataSource, "SELECT count(*) FROM pg_indexes WHERE schemaname = 'mnemosyne' AND indexdef LIKE '%USING hnsw%'"));
        Assert.Equal("vector(3)", await EmbeddingTypeAsync(dataSource));
    }

    [Fact]
    public async Task ADifferentDimensionFailsWithBothNumbersAndLeavesTheColumnAlone()
    {
        Assert.True((await fixture.CreateStore(3).MigrateAsync(Ct)).IsSuccess);

        var result = await fixture.CreateStore(4).MigrateAsync(Ct);

        Assert.True(result.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Contains("3", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("4", result.Error.Message, StringComparison.Ordinal);
        Assert.Equal("vector(3)", await EmbeddingTypeAsync(fixture.DataSource));
    }

    [Fact]
    public async Task ADifferentDimensionFailsTheFirstCallOfAStoreThatNeverMigrated()
    {
        Assert.True((await fixture.CreateStore(3).MigrateAsync(Ct)).IsSuccess);

        var result = await fixture.CreateStore(4).GetAsync(PgVectorFixture.NewScope(), ReasonletId.New(), Ct);

        Assert.True(result.IsFailure);
        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Contains("3", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("4", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallsBeforeMigrateAsyncReturnUnavailable()
    {
        var store = new ReasonletPostgreSqlStore(
            await fixture.CreateEmptyDatabaseAsync(), new MnemosynePostgreSqlOptions { Dimensions = 3 });
        var scope = PgVectorFixture.NewScope();

        AssertNotMigrated((await store.GetAsync(scope, ReasonletId.New(), Ct)).Error);
        AssertNotMigrated((await store.RememberAsync(null!, Ct)).Error);
        AssertNotMigrated((await store.AppendExecutionAsync(scope, ReasonletId.New(), default, new ReasonletProvenance { Source = "test" }, Ct)).Error);
        AssertNotMigrated((await store.AppendFeedbackAsync(scope, ExecutionId.New(), default, Ct)).Error);
        AssertNotMigrated((await store.SearchAsync(scope, new float[] { 1, 0, 0 }, null, 10, Ct)).Error);
        AssertNotMigrated((await store.EraseAsync(new ReasonletErasure(scope, null), Ct)).Error);
    }

    [Fact]
    public async Task ASchemaWithoutARecordedDimensionIsNotMigrated()
    {
        var dataSource = await fixture.CreateEmptyDatabaseAsync();
        Assert.True((await new ReasonletPostgreSqlStore(dataSource, new MnemosynePostgreSqlOptions { Dimensions = 3 }).MigrateAsync(Ct)).IsSuccess);
        await using (var delete = dataSource.CreateCommand("DELETE FROM mnemosyne.settings WHERE key = 'dimensions'"))
        {
            await delete.ExecuteNonQueryAsync(Ct);
        }

        var result = await new ReasonletPostgreSqlStore(dataSource, new MnemosynePostgreSqlOptions { Dimensions = 3 })
            .GetAsync(PgVectorFixture.NewScope(), ReasonletId.New(), Ct);

        AssertNotMigrated(result.Error);
    }

    [Fact]
    public async Task AStoreOverAMigratedDatabaseIsReadyWithoutMigratingItself()
    {
        Assert.True((await fixture.CreateStore().MigrateAsync(Ct)).IsSuccess);
        var store = fixture.CreateStore();

        // The first call reads the recorded dimension, the second answers from the cache.
        var first = await store.GetAsync(PgVectorFixture.NewScope(), ReasonletId.New(), Ct);
        var second = await store.GetAsync(PgVectorFixture.NewScope(), ReasonletId.New(), Ct);

        Assert.Equal(MnemosyneErrorCode.NotFound, first.Error.Code);
        Assert.Equal(MnemosyneErrorCode.NotFound, second.Error.Code);
    }

    [Fact]
    public async Task MigrateAsyncMakesTheStoreReady()
    {
        var store = fixture.CreateStore();
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);

        var result = await store.GetAsync(PgVectorFixture.NewScope(), ReasonletId.New(), Ct);

        Assert.Equal(MnemosyneErrorCode.NotFound, result.Error.Code);
    }

    // Placeholders: these members are stubs until the tasks that implement them replace this test.
    [Fact]
    public async Task UnimplementedMembersThrowOnceMigrated()
    {
        var store = fixture.CreateStore();
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        var scope = PgVectorFixture.NewScope();

        await Assert.ThrowsAsync<NotImplementedException>(async () => await store.AppendExecutionAsync(scope, ReasonletId.New(), default, new ReasonletProvenance { Source = "test" }, Ct));
        await Assert.ThrowsAsync<NotImplementedException>(async () => await store.AppendFeedbackAsync(scope, ExecutionId.New(), default, Ct));
        await Assert.ThrowsAsync<NotImplementedException>(async () => await store.SearchAsync(scope, new float[] { 1, 0, 0 }, null, 10, Ct));
        await Assert.ThrowsAsync<NotImplementedException>(async () => await store.EraseAsync(new ReasonletErasure(scope, null), Ct));
    }

    private static void AssertNotMigrated(MnemosyneError error)
    {
        Assert.Equal(MnemosyneErrorCode.Unavailable, error.Code);
        Assert.Equal("schema not migrated: call MigrateAsync", error.Message);
    }

    private static Task<string> EmbeddingTypeAsync(NpgsqlDataSource dataSource) => ScalarAsync<string>(
        dataSource,
        "SELECT format_type(atttypid, atttypmod) FROM pg_attribute "
        + "WHERE attrelid = 'mnemosyne.reasonlets'::regclass AND attname = 'embedding'");

    private static Task<string> HnswIndexAsync(NpgsqlDataSource dataSource) => ScalarAsync<string>(
        dataSource,
        "SELECT indexdef FROM pg_indexes WHERE schemaname = 'mnemosyne' AND indexname = 'reasonlets_embedding_hnsw'");

    private static async Task<string> SnapshotAsync(NpgsqlDataSource dataSource) => string.Join(
        " | ",
        await EmbeddingTypeAsync(dataSource),
        await HnswIndexAsync(dataSource),
        string.Join(",", await RowsAsync(dataSource, "SELECT key || '=' || value FROM mnemosyne.settings ORDER BY key")),
        string.Join(",", await RowsAsync(dataSource, "SELECT source || ':' || version || ':' || name FROM __zaorm_migrations ORDER BY 1")),
        string.Join(",", await RowsAsync(dataSource, "SELECT indexname FROM pg_indexes WHERE schemaname = 'mnemosyne' ORDER BY 1")));

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<List<string>> RowsAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
