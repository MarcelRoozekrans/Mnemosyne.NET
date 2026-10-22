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
            + "AND table_name IN ('reasonlets', 'reasonlet_executions', 'reasonlet_feedback', 'reasonlet_metrics', 'settings')");
        Assert.Equal(5, tables);
        Assert.Equal("vector(3)", await EmbeddingTypeAsync(fixture.DataSource));
        var index = await HnswIndexAsync(fixture.DataSource);
        Assert.Contains("USING hnsw (embedding vector_cosine_ops)", index, StringComparison.Ordinal);
        Assert.Contains("m='16'", index, StringComparison.Ordinal);
        Assert.Contains("ef_construction='64'", index, StringComparison.Ordinal);
        Assert.Equal(
            "dimensions=3,hnsw_ef_construction=64,hnsw_m=16",
            string.Join(",", await RowsAsync(fixture.DataSource, "SELECT key || '=' || value FROM mnemosyne.settings ORDER BY key")));
    }

    [Fact]
    public async Task AServerOlderThanPostgreSql15IsRejectedWithItsVersionAndTheMinimum()
    {
        // server_version_num is fixed by the server, so the test shadows current_setting: a schema listed before
        // pg_catalog on the search_path is searched first. MigrateAsync must stop before the migrations, which an
        // older server could not parse.
        var dataSource = await fixture.CreateEmptyDatabaseAsync("-c search_path=old_server,pg_catalog,public");
        await Entries.ExecuteAsync(
            dataSource,
            """
            CREATE SCHEMA old_server;
            CREATE FUNCTION old_server.current_setting(name text) RETURNS text LANGUAGE sql
                AS $$ SELECT CASE name WHEN 'server_version_num' THEN '140011' WHEN 'server_version' THEN '14.11' END $$;
            """);

        var result = await new ReasonletPostgreSqlStore(dataSource, new MnemosynePostgreSqlOptions { Dimensions = 3 }).MigrateAsync(Ct);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal(
            "PostgreSQL 14.11 is running, but the store needs PostgreSQL 15 or later for its scoped foreign keys.",
            result.Error.Message);
        Assert.True(await ScalarAsync<bool>(dataSource, "SELECT to_regclass('mnemosyne.reasonlets') IS NULL"));
    }

    [Theory]
    [InlineData(8, 64)]
    [InlineData(16, 100)]
    public async Task DifferentHnswOptionsLaterFailWithBothValuesAndLeaveTheIndexAlone(int m, int efConstruction)
    {
        var dataSource = await fixture.CreateEmptyDatabaseAsync();
        Assert.True((await new ReasonletPostgreSqlStore(dataSource, new MnemosynePostgreSqlOptions { Dimensions = 3 }).MigrateAsync(Ct)).IsSuccess);
        var before = await HnswIndexAsync(dataSource);

        var result = await new ReasonletPostgreSqlStore(
            dataSource, new MnemosynePostgreSqlOptions { Dimensions = 3, HnswM = m, HnswEfConstruction = efConstruction }).MigrateAsync(Ct);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal(
            $"HnswM and HnswEfConstruction: the database's HNSW index was built with m = 16 and ef_construction = 64, but the store is configured with m = {m} and ef_construction = {efConstruction}. The options apply only when the first MigrateAsync builds the index.",
            result.Error.Message);
        Assert.Equal(before, await HnswIndexAsync(dataSource));
    }

    [Fact]
    public async Task AnOldPgVectorIsRejectedWithItsVersionAndTheMinimum()
    {
        var dataSource = await fixture.CreateEmptyDatabaseAsync();
        var options = new MnemosynePostgreSqlOptions { Dimensions = 3 };
        Assert.True((await new ReasonletPostgreSqlStore(dataSource, options).MigrateAsync(Ct)).IsSuccess);
        await Entries.ExecuteAsync(dataSource, "UPDATE pg_extension SET extversion = '0.7.4' WHERE extname = 'vector'");

        var result = await new ReasonletPostgreSqlStore(dataSource, options).MigrateAsync(Ct);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Equal("pgvector 0.7.4 is installed, but the store needs pgvector 0.8.0 or later for iterative index scans.", result.Error.Message);
    }

    [Fact]
    public async Task AMissingPgVectorIsRejected()
    {
        var dataSource = await fixture.CreateEmptyDatabaseAsync();
        var options = new MnemosynePostgreSqlOptions { Dimensions = 3 };
        Assert.True((await new ReasonletPostgreSqlStore(dataSource, options).MigrateAsync(Ct)).IsSuccess);
        await Entries.ExecuteAsync(dataSource, "UPDATE pg_extension SET extname = 'vector_gone' WHERE extname = 'vector'");

        var result = await new ReasonletPostgreSqlStore(dataSource, options).MigrateAsync(Ct);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.Contains("(not installed)", result.Error.Message, StringComparison.Ordinal);
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
    public async Task TheSchemaHasTheSpecifiedConstraintsAndIndexes()
    {
        Assert.True((await fixture.CreateStore().MigrateAsync(Ct)).IsSuccess);

        var constraints = await RowsAsync(
            fixture.DataSource,
            "SELECT conrelid::regclass::text || ' ' || pg_get_constraintdef(oid) FROM pg_constraint "
            + "WHERE contype IN ('f', 'u', 'c') AND connamespace = 'mnemosyne'::regnamespace ORDER BY 1");
        Assert.Equal(
            [
                "mnemosyne.reasonlet_executions FOREIGN KEY (scope, reasonlet_id) REFERENCES mnemosyne.reasonlets(scope, id) ON DELETE CASCADE",
                "mnemosyne.reasonlet_executions UNIQUE (scope, id)",
                "mnemosyne.reasonlet_feedback FOREIGN KEY (scope, execution_id) REFERENCES mnemosyne.reasonlet_executions(scope, id) ON DELETE CASCADE",
                "mnemosyne.reasonlet_metrics FOREIGN KEY (scope, reasonlet_id) REFERENCES mnemosyne.reasonlets(scope, id) ON DELETE CASCADE",
                "mnemosyne.reasonlets CHECK ((length(content_hash) = 64))",
                "mnemosyne.reasonlets FOREIGN KEY (scope, parent_id) REFERENCES mnemosyne.reasonlets(scope, id) ON DELETE SET NULL (parent_id)",
                "mnemosyne.reasonlets UNIQUE (scope, id)",
            ],
            constraints);

        var indexes = await RowsAsync(
            fixture.DataSource,
            "SELECT tablename || ':' || substring(indexdef from ' USING .*$') FROM pg_indexes "
            + "WHERE schemaname = 'mnemosyne' AND indexname NOT LIKE '%_pkey' ORDER BY 1");
        Assert.Equal(
            [
                "reasonlet_executions: USING btree (executed_at)",
                "reasonlet_executions: USING btree (scope, id)",
                "reasonlet_executions: USING btree (scope, reasonlet_id)",
                "reasonlet_executions: USING btree (scope, subject_id)",
                "reasonlet_metrics: USING btree (last_executed_at)",
                "reasonlets: USING btree (created_at)",
                "reasonlets: USING btree (scope, id)",
                "reasonlets: USING btree (scope, parent_id)",
                "reasonlets: USING btree (scope, status, intent)",
                "reasonlets: USING btree (scope, subject_id)",
                "reasonlets: USING hnsw (embedding vector_cosine_ops) WITH (m='16', ef_construction='64')",
            ],
            indexes);
        Assert.Equal("text", await ScalarAsync<string>(
            fixture.DataSource,
            "SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'mnemosyne.reasonlets'::regclass AND attname = 'content_hash'"));
    }

    [Fact]
    public async Task TheForeignKeysRefuseARowThatPointsIntoAnotherScope()
    {
        var store = fixture.CreateStore();
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        var scope = PgVectorFixture.NewScope();
        var receipt = (await store.RememberAsync(Entries.Entry(scope), Ct)).Value;
        var other = PgVectorFixture.NewScope().Value;

        var child = await Assert.ThrowsAsync<PostgresException>(() => Entries.ExecuteAsync(
            fixture.DataSource,
            "INSERT INTO mnemosyne.reasonlets (id, scope, name, intent, version, graph, content_hash, parent_id, status, source, created_at, embedding) "
            + "VALUES ($1, $2, 'n', 'i', '1.1', '{}', repeat('0', 64), $3, 1, 's', now(), '[1,0,0]')",
            Guid.CreateVersion7(),
            other,
            receipt.Id.Value));
        var execution = await Assert.ThrowsAsync<PostgresException>(() => Entries.ExecuteAsync(
            fixture.DataSource,
            "INSERT INTO mnemosyne.reasonlet_executions (id, reasonlet_id, scope, executed_at, success, duration_ticks, source) "
            + "VALUES ($1, $2, $3, now(), true, 0, 's')",
            Guid.CreateVersion7(),
            receipt.Id.Value,
            other));
        var feedback = await Assert.ThrowsAsync<PostgresException>(() => Entries.ExecuteAsync(
            fixture.DataSource,
            "INSERT INTO mnemosyne.reasonlet_feedback (execution_id, scope, utility, correctness, reusability, recorded_at) "
            + "VALUES ($1, $2, 0, 0, 0, now())",
            receipt.Execution.Value,
            other));
        var metrics = await Assert.ThrowsAsync<PostgresException>(() => Entries.ExecuteAsync(
            fixture.DataSource,
            "UPDATE mnemosyne.reasonlet_metrics SET scope = $1 WHERE reasonlet_id = $2",
            other,
            receipt.Id.Value));

        Assert.All(new[] { child, execution, feedback, metrics }, e => Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, e.SqlState));
    }

    [Fact]
    public async Task AContentHashOfAnotherLengthIsRefused()
    {
        Assert.True((await fixture.CreateStore().MigrateAsync(Ct)).IsSuccess);

        var thrown = await Assert.ThrowsAsync<PostgresException>(() => Entries.ExecuteAsync(
            fixture.DataSource,
            "INSERT INTO mnemosyne.reasonlets (id, scope, name, intent, version, graph, content_hash, status, source, created_at, embedding) "
            + "VALUES ($1, 's', 'n', 'i', '1.0', '{}', repeat('0', 63), 1, 's', now(), '[1,0,0]')",
            Guid.CreateVersion7()));

        Assert.Equal(PostgresErrorCodes.CheckViolation, thrown.SqlState);
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
        AssertNotMigrated((await store.RememberAsync(Entries.Entry(scope), Ct)).Error);
        AssertNotMigrated((await store.AppendExecutionAsync(scope, ReasonletId.New(), default, new ReasonletProvenance { Source = "test" }, Ct)).Error);
        AssertNotMigrated((await store.AppendFeedbackAsync(scope, ExecutionId.New(), default, Ct)).Error);
        AssertNotMigrated((await store.SearchAsync(scope, new float[] { 1, 0, 0 }, null, 10, Ct)).Error);
        AssertNotMigrated((await store.EraseAsync(new ReasonletErasure(scope, null), Ct)).Error);
    }

    [Fact]
    public async Task NullArgumentsThrowBeforeTheMigrationCheck()
    {
        var store = new ReasonletPostgreSqlStore(
            await fixture.CreateEmptyDatabaseAsync(), new MnemosynePostgreSqlOptions { Dimensions = 3 });

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.RememberAsync(null!, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.EraseAsync(null!, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await store.AppendExecutionAsync(PgVectorFixture.NewScope(), ReasonletId.New(), default, null!, Ct));
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
