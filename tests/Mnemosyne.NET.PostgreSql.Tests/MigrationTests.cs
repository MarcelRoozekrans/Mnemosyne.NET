using Mnemosyne.NET.Abstractions;
using System.Data.Async.Adapters;
using Npgsql;
using Xunit;
using ZeroAlloc.ORM.Migrations;

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
            "dimensions=3,hnsw_ef_construction=64,hnsw_m=16,min_binary_schema=3,schema_version=4",
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
                "mnemosyne.reasonlet_executions CHECK ((((evaluated_success IS NULL) = (evaluator IS NULL)) AND ((evaluated_success IS NULL) = (evaluation_score IS NULL))))",
                "mnemosyne.reasonlet_executions CHECK ((length(evaluator) <= 100))",
                "mnemosyne.reasonlet_executions CHECK ((success = COALESCE(feedback_worked, evaluated_success, agent_success)))",
                "mnemosyne.reasonlet_executions FOREIGN KEY (scope, reasonlet_id) REFERENCES mnemosyne.reasonlets(scope, id) ON DELETE CASCADE",
                "mnemosyne.reasonlet_executions UNIQUE (scope, id)",
                "mnemosyne.reasonlet_feedback FOREIGN KEY (scope, execution_id) REFERENCES mnemosyne.reasonlet_executions(scope, id) ON DELETE CASCADE",
                "mnemosyne.reasonlet_metrics CHECK (((judged_runs >= 0) AND (judged_runs <= runs)))",
                "mnemosyne.reasonlet_metrics CHECK (((judged_successes >= 0) AND (judged_successes <= judged_runs)))",
                "mnemosyne.reasonlet_metrics CHECK ((judged_successes <= successes))",
                "mnemosyne.reasonlet_metrics CHECK (((successes - judged_successes) <= (runs - judged_runs)))",
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
                "reasonlet_executions: USING btree (scope, executed_at) INCLUDE (id)",
                "reasonlet_executions: USING btree (scope, id)",
                "reasonlet_executions: USING btree (scope, reasonlet_id)",
                "reasonlet_executions: USING btree (scope, subject_id)",
                "reasonlets: USING btree (scope, id)",
                "reasonlets: USING btree (scope, id) WHERE (status = 2)",
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
            "INSERT INTO mnemosyne.reasonlet_executions (id, reasonlet_id, scope, executed_at, agent_success, success, duration_ticks, source) "
            + "VALUES ($1, $2, $3, now(), true, true, 0, 's')",
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
        AssertNotMigrated((await store.AppendExecutionAsync(scope, ReasonletId.New(), default, new ReasonletProvenance { Source = "test" }, null, Ct)).Error);
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
            async () => await store.AppendExecutionAsync(PgVectorFixture.NewScope(), ReasonletId.New(), default, null!, null, Ct));
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

    [Fact]
    public async Task MigrationAddsThePendingIndex()
    {
        Assert.True((await fixture.CreateStore().MigrateAsync(Ct)).IsSuccess);

        var definition = await ScalarAsync<string>(
            fixture.DataSource, "SELECT indexdef FROM pg_indexes WHERE schemaname = 'mnemosyne' AND indexname = 'reasonlets_pending'");

        Assert.Contains("(scope, id)", definition, StringComparison.Ordinal);
        Assert.Contains("WHERE (status = 2)", definition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADatabaseAtMigration0003MigratesToFourKeepingTheMinimumBinary()
    {
        var dataSource = await fixture.CreateEmptyDatabaseAsync();
        await using (var connection = (await dataSource.OpenConnectionAsync(Ct)).AsAsync())
        {
            var source = new EmbeddedResourceMigrationSource(typeof(ReasonletPostgreSqlStore).Assembly, "Mnemosyne.NET.PostgreSql.Migrations.");
            await new MigrationRunner(connection, new MigrationsUpTo(source, 3), new PostgresMigrationDialect()).RunAsync(Ct);
        }

        await Entries.ExecuteAsync(
            dataSource,
            """
            ALTER TABLE mnemosyne.reasonlets ALTER COLUMN embedding TYPE vector(3);
            CREATE INDEX reasonlets_embedding_hnsw ON mnemosyne.reasonlets USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64);
            INSERT INTO mnemosyne.settings (key, value) VALUES ('dimensions', '3'), ('hnsw_m', '16'), ('hnsw_ef_construction', '64');
            """);
        Assert.Equal(0, await ScalarAsync<long>(
            dataSource, "SELECT count(*) FROM pg_indexes WHERE schemaname = 'mnemosyne' AND indexname = 'reasonlets_pending'"));
        var store = new ReasonletPostgreSqlStore(dataSource, new MnemosynePostgreSqlOptions { Dimensions = 3 });
        AssertNotMigrated((await store.GetAsync(new ReasonletScope("old"), ReasonletId.New(), Ct)).Error);

        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);

        Assert.Equal("1,2,3,4", string.Join(",", await RowsAsync(dataSource, "SELECT version::text FROM __zaorm_migrations ORDER BY version")));
        Assert.Equal("4", (await RowsAsync(dataSource, "SELECT value FROM mnemosyne.settings WHERE key = 'schema_version'"))[0]);
        Assert.Equal("3", (await RowsAsync(dataSource, "SELECT value FROM mnemosyne.settings WHERE key = 'min_binary_schema'"))[0]);
        Assert.Equal(1, await ScalarAsync<long>(
            dataSource, "SELECT count(*) FROM pg_indexes WHERE schemaname = 'mnemosyne' AND indexname = 'reasonlets_pending'"));
    }

    [Fact]
    public async Task TheMigrationRunnerAppliesEachFileInOneTransactionWithItsHistoryRow()
    {
        // 0002 has no BEGIN of its own: it relies on the runner to apply it whole or not at all. A file that fails
        // after a DDL statement must leave neither the table nor a history row behind, and a statement that cannot
        // run inside a transaction block must be refused, which shows the transaction is the runner's own and not
        // only the implicit one of a multi-statement batch.
        var dataSource = await fixture.CreateEmptyDatabaseAsync();
        await using var connection = (await dataSource.OpenConnectionAsync(Ct)).AsAsync();

        var failed = await Assert.ThrowsAsync<PostgresException>(() => new MigrationRunner(
            connection, new ListMigrationSource("probe", "CREATE TABLE public.probe (x int); INSERT INTO public.probe VALUES (1); SELECT 1 / 0;"),
            new PostgresMigrationDialect()).RunAsync(Ct));
        var vacuum = await Assert.ThrowsAsync<PostgresException>(() => new MigrationRunner(
            connection, new ListMigrationSource("vacuum", "VACUUM"), new PostgresMigrationDialect()).RunAsync(Ct));

        Assert.Equal(PostgresErrorCodes.DivisionByZero, failed.SqlState);
        Assert.Equal(PostgresErrorCodes.ActiveSqlTransaction, vacuum.SqlState);
        Assert.True(await ScalarAsync<bool>(dataSource, "SELECT to_regclass('public.probe') IS NULL"));
        Assert.Equal(0L, await ScalarAsync<long>(dataSource, "SELECT count(*) FROM __zaorm_migrations"));
    }

    [Fact]
    public async Task APhase17DatabaseIsNotMigratedUntilMigrateAsyncUpgradesItKeepingItsRunsAndCounters()
    {
        // A database at the 1.7 schema: 0001 alone, the dimension and HNSW index the 1.7 MigrateAsync applied, and runs
        // written in the 1.7 shape, with feedback that has no verdict. It also carries a schema_version row below 2,
        // which 0002 must overwrite rather than fail on.
        var dataSource = await fixture.CreateEmptyDatabaseAsync();
        await using (var connection = (await dataSource.OpenConnectionAsync(Ct)).AsAsync())
        {
            var source = new EmbeddedResourceMigrationSource(typeof(ReasonletPostgreSqlStore).Assembly, "Mnemosyne.NET.PostgreSql.Migrations.");
            await new MigrationRunner(connection, new MigrationsUpTo(source, 1), new PostgresMigrationDialect()).RunAsync(Ct);
        }

        var reasonlet = Guid.CreateVersion7();
        var runs = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
        await Entries.ExecuteAsync(
            dataSource,
            """
            ALTER TABLE mnemosyne.reasonlets ALTER COLUMN embedding TYPE vector(3);
            CREATE INDEX reasonlets_embedding_hnsw ON mnemosyne.reasonlets USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64);
            INSERT INTO mnemosyne.settings (key, value) VALUES ('dimensions', '3'), ('hnsw_m', '16'), ('hnsw_ef_construction', '64'), ('schema_version', '1');
            """);
        await Entries.ExecuteAsync(
            dataSource,
            "INSERT INTO mnemosyne.reasonlets (id, scope, name, intent, version, graph, content_hash, status, source, created_at, embedding) "
            + "VALUES ($1, 'old', 'n', 'i', '1.0', '{}', repeat('0', 64), 1, 's', now() - interval '3 days', '[1,0,0]')",
            reasonlet);
        await Entries.ExecuteAsync(
            dataSource,
            """
            INSERT INTO mnemosyne.reasonlet_executions (id, reasonlet_id, scope, executed_at, success, duration_ticks, source)
            VALUES ($2[1], $1, 'old', now() - interval '3 days', true, 0, 's'),
                   ($2[2], $1, 'old', now() - interval '2 days', false, 0, 's'),
                   ($2[3], $1, 'old', now() - interval '1 day', true, 0, 's')
            """,
            reasonlet,
            runs);
        await Entries.ExecuteAsync(
            dataSource,
            "INSERT INTO mnemosyne.reasonlet_feedback (execution_id, scope, utility, correctness, reusability, recorded_at) VALUES ($1, 'old', 0.1, 0.2, 0.3, now())",
            runs[1]);

        // Counts above what the runs left show pruned history, which the upgrade must keep.
        await Entries.ExecuteAsync(
            dataSource,
            "INSERT INTO mnemosyne.reasonlet_metrics (reasonlet_id, scope, runs, successes, last_success_at, last_executed_at) "
            + "VALUES ($1, 'old', 7, 5, now() - interval '1 day', now() - interval '1 day')",
            reasonlet);
        var metricsBefore = await RowsAsync(dataSource, "SELECT concat_ws('|', runs, successes, last_success_at, last_executed_at) FROM mnemosyne.reasonlet_metrics");
        var store = new ReasonletPostgreSqlStore(dataSource, new MnemosynePostgreSqlOptions { Dimensions = 3 });

        AssertNotMigrated((await store.GetAsync(new ReasonletScope("old"), new ReasonletId(reasonlet), Ct)).Error);
        AssertNotMigrated((await store.AppendFeedbackAsync(new ReasonletScope("old"), new ExecutionId(runs[1]), new ReasonletFeedback(0.5, 0.5, 0.5, true), Ct)).Error);

        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);

        Assert.Equal(
            [
                $"{runs[0]}|t|t|-|-|-|-",
                $"{runs[1]}|f|f|-|-|-|-",
                $"{runs[2]}|t|t|-|-|-|-",
            ],
            await RowsAsync(
                dataSource,
                "SELECT concat_ws('|', id, agent_success, success, coalesce(evaluated_success::text, '-'), coalesce(evaluation_score::text, '-'), "
                + "coalesce(evaluator, '-'), coalesce(feedback_worked::text, '-')) FROM mnemosyne.reasonlet_executions ORDER BY executed_at"));
        Assert.Equal(metricsBefore, await RowsAsync(dataSource, "SELECT concat_ws('|', runs, successes, last_success_at, last_executed_at) FROM mnemosyne.reasonlet_metrics"));
        Assert.Equal("0.1|0.2|0.3", (await RowsAsync(dataSource, "SELECT concat_ws('|', utility, correctness, reusability) FROM mnemosyne.reasonlet_feedback"))[0]);
        Assert.Equal("1,2,3,4", string.Join(",", await RowsAsync(dataSource, "SELECT version::text FROM __zaorm_migrations ORDER BY version")));
        Assert.Equal("4", (await RowsAsync(dataSource, "SELECT value FROM mnemosyne.settings WHERE key = 'schema_version'"))[0]);
        Assert.Equal("0|0", (await RowsAsync(dataSource, "SELECT concat_ws('|', judged_runs, judged_successes) FROM mnemosyne.reasonlet_metrics"))[0]);

        // The upgraded store works: a verdict on the old failed run raises the counter and makes the run judged.
        Assert.True((await store.AppendFeedbackAsync(new ReasonletScope("old"), new ExecutionId(runs[1]), new ReasonletFeedback(0.5, 0.5, 0.5, true), Ct)).IsSuccess);
        Assert.Equal(
            "7|6|1|1",
            (await RowsAsync(dataSource, "SELECT concat_ws('|', runs, successes, judged_runs, judged_successes) FROM mnemosyne.reasonlet_metrics"))[0]);
    }

    [Fact]
    public async Task APhase2DatabaseUpgradeBackfillsTheJudgedCountersFromTheStoredRuns()
    {
        // A database at the 2.3 schema, 0001 and 0002, with runs of every kind: judged by an evaluator, judged by a
        // human, and agent-only. The counters also hold pruned runs, whose verdicts are gone, so the backfill counts
        // them as agent-only. Every run of the last Reasonlet was pruned.
        var dataSource = await fixture.CreateEmptyDatabaseAsync();
        await using (var connection = (await dataSource.OpenConnectionAsync(Ct)).AsAsync())
        {
            var source = new EmbeddedResourceMigrationSource(typeof(ReasonletPostgreSqlStore).Assembly, "Mnemosyne.NET.PostgreSql.Migrations.");
            await new MigrationRunner(connection, new MigrationsUpTo(source, 2), new PostgresMigrationDialect()).RunAsync(Ct);
        }

        var mixed = Guid.CreateVersion7();
        var unjudged = Guid.CreateVersion7();
        var pruned = Guid.CreateVersion7();
        var agentOnlyFailure = Guid.CreateVersion7();
        await Entries.ExecuteAsync(
            dataSource,
            """
            ALTER TABLE mnemosyne.reasonlets ALTER COLUMN embedding TYPE vector(3);
            CREATE INDEX reasonlets_embedding_hnsw ON mnemosyne.reasonlets USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64);
            INSERT INTO mnemosyne.settings (key, value) VALUES ('dimensions', '3'), ('hnsw_m', '16'), ('hnsw_ef_construction', '64');
            """);
        await Entries.ExecuteAsync(
            dataSource,
            "INSERT INTO mnemosyne.reasonlets (id, scope, name, intent, version, graph, content_hash, status, source, created_at, embedding) "
            + "SELECT id, 'old', 'n', 'i', '1.0', '{}', repeat('0', 64), 1, 's', now() - interval '3 days', '[1,0,0]' FROM unnest($1::uuid[]) id",
            new[] { mixed, unjudged, pruned });
        await Entries.ExecuteAsync(
            dataSource,
            """
            INSERT INTO mnemosyne.reasonlet_executions
                (id, reasonlet_id, scope, executed_at, agent_success, evaluated_success, evaluation_score, evaluator, feedback_worked, success,
                 duration_ticks, source)
            VALUES (gen_random_uuid(), $1, 'old', now() - interval '2 days', false, true, 0.9, 'judge', NULL, true, 0, 's'),
                   (gen_random_uuid(), $1, 'old', now() - interval '2 days', true, false, 0.1, 'judge', NULL, false, 0, 's'),
                   (gen_random_uuid(), $1, 'old', now() - interval '2 days', false, NULL, NULL, NULL, true, true, 0, 's'),
                   (gen_random_uuid(), $1, 'old', now() - interval '2 days', true, true, 0.9, 'judge', false, false, 0, 's'),
                   (gen_random_uuid(), $1, 'old', now() - interval '2 days', true, NULL, NULL, NULL, NULL, true, 0, 's'),
                   ($3, $1, 'old', now() - interval '2 days', false, NULL, NULL, NULL, NULL, false, 0, 's'),
                   (gen_random_uuid(), $2, 'old', now() - interval '2 days', true, NULL, NULL, NULL, NULL, true, 0, 's')
            """,
            mixed,
            unjudged,
            agentOnlyFailure);
        await Entries.ExecuteAsync(
            dataSource,
            """
            INSERT INTO mnemosyne.reasonlet_metrics (reasonlet_id, scope, runs, successes, last_success_at, last_executed_at)
            VALUES ($1, 'old', 10, 6, now() - interval '2 days', now() - interval '2 days'),
                   ($2, 'old', 2, 2, now() - interval '2 days', now() - interval '2 days'),
                   ($3, 'old', 3, 2, now() - interval '3 days', now() - interval '3 days')
            """,
            mixed,
            unjudged,
            pruned);
        var store = new ReasonletPostgreSqlStore(dataSource, new MnemosynePostgreSqlOptions { Dimensions = 3 });
        AssertNotMigrated((await store.GetAsync(new ReasonletScope("old"), new ReasonletId(mixed), Ct)).Error);

        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);

        Assert.Equal("10|6|4|2", await CountersAsync(dataSource, mixed));
        Assert.Equal("2|2|0|0", await CountersAsync(dataSource, unjudged));
        Assert.Equal("3|2|0|0", await CountersAsync(dataSource, pruned));
        Assert.Empty(await RowsAsync(
            dataSource,
            """
            SELECT m.reasonlet_id::text FROM mnemosyne.reasonlet_metrics m
            CROSS JOIN LATERAL (SELECT (count(*) FILTER (WHERE evaluated_success IS NOT NULL OR feedback_worked IS NOT NULL))::int AS judged_runs,
                                       (count(*) FILTER (WHERE (evaluated_success IS NOT NULL OR feedback_worked IS NOT NULL) AND success))::int
                                           AS judged_successes
                                FROM mnemosyne.reasonlet_executions e WHERE e.reasonlet_id = m.reasonlet_id) a
            WHERE (m.judged_runs, m.judged_successes) <> (a.judged_runs, a.judged_successes)
            """));
        Assert.Equal(
            "dimensions=3,hnsw_ef_construction=64,hnsw_m=16,min_binary_schema=3,schema_version=4",
            string.Join(",", await RowsAsync(dataSource, "SELECT key || '=' || value FROM mnemosyne.settings ORDER BY key")));
        Assert.Equal("1,2,3,4", string.Join(",", await RowsAsync(dataSource, "SELECT version::text FROM __zaorm_migrations ORDER BY version")));

        // The upgraded store keeps the counters: a verdict that agrees with the agent's failure makes its run judged.
        Assert.True((await store.AppendFeedbackAsync(
            new ReasonletScope("old"), new ExecutionId(agentOnlyFailure), new ReasonletFeedback(0.5, 0.5, 0.5, false), Ct)).IsSuccess);
        Assert.Equal("10|6|5|2", await CountersAsync(dataSource, mixed));
    }

    [Fact]
    public async Task ADatabaseThatNeedsANewerBinaryRefusesEveryCallUntilTheSettingIsGone()
    {
        var dataSource = await fixture.CreateEmptyDatabaseAsync();
        Assert.True((await new ReasonletPostgreSqlStore(dataSource, new MnemosynePostgreSqlOptions { Dimensions = 3 }).MigrateAsync(Ct)).IsSuccess);
        await Entries.ExecuteAsync(dataSource, "UPDATE mnemosyne.settings SET value = '5' WHERE key = 'min_binary_schema'");
        var store = new ReasonletPostgreSqlStore(dataSource, new MnemosynePostgreSqlOptions { Dimensions = 3 });
        var scope = PgVectorFixture.NewScope();

        AssertTooOld((await store.MigrateAsync(Ct)).Error);
        AssertTooOld((await store.GetAsync(scope, ReasonletId.New(), Ct)).Error);
        AssertTooOld((await store.RememberAsync(Entries.Entry(scope), Ct)).Error);
        AssertTooOld((await store.AppendExecutionAsync(scope, ReasonletId.New(), default, new ReasonletProvenance { Source = "test" }, null, Ct)).Error);
        AssertTooOld((await store.AppendFeedbackAsync(scope, ExecutionId.New(), default, Ct)).Error);
        AssertTooOld((await store.SearchAsync(scope, new float[] { 1, 0, 0 }, null, 10, Ct)).Error);
        AssertTooOld((await store.EraseAsync(new ReasonletErasure(scope, null), Ct)).Error);
        AssertTooOld((await store.PruneAsync(new ReasonletRetention { Scope = scope, ExecutionsOlderThan = TimeSpan.FromDays(1) }, Ct)).Error);
        AssertTooOld((await store.ApproveAsync(scope, ReasonletId.New(), Ct)).Error);
        AssertTooOld((await store.RejectAsync(scope, ReasonletId.New(), Ct)).Error);
        AssertTooOld((await store.ListPendingAsync(scope, 10, null, Ct)).Error);

        // A missing setting counts as 0, so it asks nothing of the binary.
        await Entries.ExecuteAsync(dataSource, "DELETE FROM mnemosyne.settings WHERE key = 'min_binary_schema'");

        Assert.True((await store.RememberAsync(Entries.Entry(scope), Ct)).IsSuccess);
        Assert.True((await new ReasonletPostgreSqlStore(dataSource, new MnemosynePostgreSqlOptions { Dimensions = 3 }).MigrateAsync(Ct)).IsSuccess);
    }

    [Fact]
    public async Task ASchemaVersionBelowThreeIsNotMigrated()
    {
        var dataSource = await fixture.CreateEmptyDatabaseAsync();
        Assert.True((await new ReasonletPostgreSqlStore(dataSource, new MnemosynePostgreSqlOptions { Dimensions = 3 }).MigrateAsync(Ct)).IsSuccess);
        await Entries.ExecuteAsync(dataSource, "UPDATE mnemosyne.settings SET value = '2' WHERE key = 'schema_version'");

        var result = await new ReasonletPostgreSqlStore(dataSource, new MnemosynePostgreSqlOptions { Dimensions = 3 })
            .GetAsync(PgVectorFixture.NewScope(), ReasonletId.New(), Ct);

        AssertNotMigrated(result.Error);
    }

    [Fact]
    public async Task AnOldInstanceThatOmitsAgentSuccessFailsLoudlyAfterTheUpgrade()
    {
        // The 1.7 insert names success but not agent_success, so the upgraded schema refuses it rather than storing a
        // run whose inputs are missing.
        var store = fixture.CreateStore();
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        var scope = PgVectorFixture.NewScope();
        var receipt = (await store.RememberAsync(Entries.Entry(scope), Ct)).Value;

        var thrown = await Assert.ThrowsAsync<PostgresException>(() => Entries.ExecuteAsync(
            fixture.DataSource,
            "INSERT INTO mnemosyne.reasonlet_executions (id, reasonlet_id, scope, executed_at, success, duration_ticks, source) "
            + "VALUES ($1, $2, $3, now(), true, 0, 's')",
            Guid.CreateVersion7(),
            receipt.Id.Value,
            scope.Value));

        Assert.Equal(PostgresErrorCodes.NotNullViolation, thrown.SqlState);
        Assert.Equal("agent_success", thrown.ColumnName);
    }

    private sealed class ListMigrationSource(string name, string sql) : IMigrationSource
    {
        public string Name => name;

        public IReadOnlyList<Migration> GetMigrations() => [new Migration(1, name, sql)];
    }

    /// <summary>
    /// The store's own migrations up to <paramref name="last"/>, under the store's source name, so a later MigrateAsync
    /// continues the history.
    /// </summary>
    private sealed class MigrationsUpTo(IMigrationSource inner, int last) : IMigrationSource
    {
        public string Name => inner.Name;

        public IReadOnlyList<Migration> GetMigrations() => [.. inner.GetMigrations().Where(m => m.Version <= last)];
    }

    private static void AssertTooOld(MnemosyneError error)
    {
        Assert.Equal(MnemosyneErrorCode.Unavailable, error.Code);
        Assert.Equal(
            "binary too old for the database: the database needs a binary of schema 5 or later, and this one is schema 4; upgrade Mnemosyne.NET.PostgreSql",
            error.Message);
    }

    /// <summary>Reads a Reasonlet's runs, successes, judged runs and judged successes, joined by bars.</summary>
    private static async Task<string> CountersAsync(NpgsqlDataSource dataSource, Guid reasonlet) => await Entries.ScalarAsync<string>(
        dataSource,
        "SELECT concat_ws('|', runs, successes, judged_runs, judged_successes) FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1",
        reasonlet);

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
