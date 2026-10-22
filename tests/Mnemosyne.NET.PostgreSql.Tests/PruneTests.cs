using Mnemosyne.NET.Abstractions;
using Npgsql;
using Xunit;

namespace Mnemosyne.NET.PostgreSql.Tests;

public sealed class PruneTests(PgVectorFixture fixture) : IClassFixture<PgVectorFixture>
{
    private const string SubjectA = "subject-a";
    private const string SubjectB = "subject-b";

    private static readonly ReasonletOutcome Success = new(true, TimeSpan.FromMilliseconds(10), 0.5);
    private static readonly ReasonletOutcome Failure = new(false, TimeSpan.FromMilliseconds(10), null);
    private static readonly ReasonletFeedback Feedback = new(0.5, 0.5, 0.5);
    private static readonly TimeSpan FiveDays = TimeSpan.FromDays(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PruningOldRunsDeletesOnlyRunsOlderThanTheWindowInTheScope()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var r = await RememberAsync(store, scope, "r", SubjectA);
        var old = await AppendAsync(store, scope, r.Id, SubjectA);
        var recent = await AppendAsync(store, scope, r.Id, SubjectA);
        Assert.True((await store.AppendFeedbackAsync(scope, old, Feedback, Ct)).IsSuccess);
        Assert.True((await store.AppendFeedbackAsync(scope, recent, Feedback, Ct)).IsSuccess);
        await BackdateAsync(fixture.DataSource, r.Execution, old);

        // Another scope's runs are as old, but outside the policy.
        var otherScope = PgVectorFixture.NewScope();
        var o = await RememberAsync(store, otherScope, "o", SubjectA);
        await BackdateAsync(fixture.DataSource, o.Execution);

        var result = await PruneAsync(store, new ReasonletRetention { Scope = scope, ExecutionsOlderThan = FiveDays });

        Assert.Equal((0, 2), (result.Reasonlets, result.Executions));
        Assert.Equal([recent.Value], await IdsAsync(fixture.DataSource, "SELECT id FROM mnemosyne.reasonlet_executions WHERE scope = $1", scope.Value));
        Assert.Equal([recent.Value], await IdsAsync(fixture.DataSource, "SELECT execution_id FROM mnemosyne.reasonlet_feedback WHERE scope = $1", scope.Value));
        Assert.Equal((1L, 1L), await CountsAsync(fixture.DataSource, scope));
        Assert.Equal((1L, 1L), await CountsAsync(fixture.DataSource, otherScope));
    }

    [Fact]
    public async Task PruningRunsKeepsTheCounters()
    {
        // A pruned run still happened, so the totals and the timestamps stay as they were.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var k = await RememberAsync(store, scope, "k", SubjectA);
        await AppendAsync(store, scope, k.Id, SubjectA);
        await AppendAsync(store, scope, k.Id, SubjectA, Failure);
        await Entries.ExecuteAsync(
            fixture.DataSource, "UPDATE mnemosyne.reasonlet_executions SET executed_at = now() - interval '10 days' WHERE scope = $1", scope.Value);
        var before = await MetricsAsync(k.Id);

        var result = await PruneAsync(store, new ReasonletRetention { Scope = scope, ExecutionsOlderThan = FiveDays });

        Assert.Equal((0, 3), (result.Reasonlets, result.Executions));
        Assert.Equal((1L, 0L), await CountsAsync(fixture.DataSource, scope));
        Assert.Equal((3, 2), (before.Runs, before.Successes));
        Assert.Equal(before, await MetricsAsync(k.Id));
    }

    [Fact]
    public async Task PruningEverythingOldAcrossScopes()
    {
        // A prune without a scope covers the whole database, so it runs on one no other test shares.
        var dataSource = await fixture.CreateEmptyDatabaseAsync();
        var store = new ReasonletPostgreSqlStore(dataSource, new MnemosynePostgreSqlOptions { Dimensions = 3 });
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        var first = PgVectorFixture.NewScope();
        var second = PgVectorFixture.NewScope();
        var a = await RememberAsync(store, first, "a", SubjectA);
        var b = await RememberAsync(store, second, "b", SubjectB);
        var oldOnB = await AppendAsync(store, second, b.Id, SubjectB);
        var recentOnB = await AppendAsync(store, second, b.Id, SubjectB);
        await BackdateAsync(dataSource, a.Execution, b.Execution, oldOnB);

        var result = await PruneAsync(store, new ReasonletRetention { ExecutionsOlderThan = FiveDays });

        Assert.Equal((0, 3), (result.Reasonlets, result.Executions));
        Assert.Equal([recentOnB.Value], await IdsAsync(dataSource, "SELECT id FROM mnemosyne.reasonlet_executions", null));
        Assert.Equal((1L, 0L), await CountsAsync(dataSource, first));
    }

    [Fact]
    public async Task PruningBatchesUntilDone()
    {
        // Ten runs in batches of three: three full batches and a short one, which ends the loop.
        var store = await MigratedStoreAsync();
        store.PruneBatchSizeOverride = 3;
        var batches = 0;
        store.BeforePruneBatchCommit = () =>
        {
            batches++;
            return Task.CompletedTask;
        };
        var scope = PgVectorFixture.NewScope();
        await OldRunsAsync(store, scope, 10);

        var result = await PruneAsync(store, new ReasonletRetention { Scope = scope, ExecutionsOlderThan = FiveDays });

        Assert.Equal((0, 10), (result.Reasonlets, result.Executions));
        Assert.Equal(4, batches);
        Assert.Equal((1L, 0L), await CountsAsync(fixture.DataSource, scope));
    }

    [Fact]
    public async Task PruningAgainFindsNothing()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        await OldRunsAsync(store, scope, 2);
        var retention = new ReasonletRetention { Scope = scope, ExecutionsOlderThan = FiveDays };
        Assert.Equal(2, (await PruneAsync(store, retention)).Executions);

        var again = await PruneAsync(store, retention);

        Assert.Equal((0, 0), (again.Reasonlets, again.Executions));
    }

    [Fact]
    public async Task PruningThenErasingKeepsTheCountersExact()
    {
        // K is B's. The prune removes B's remember run and A's old run but keeps their counts. The erase of A then
        // removes A's recent run, the only run of A left, and subtracts exactly that one: the counters end at every
        // run that ever happened, less the erased one.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var k = await RememberAsync(store, scope, "k", SubjectB);
        var oldA = await AppendAsync(store, scope, k.Id, SubjectA);
        await AppendAsync(store, scope, k.Id, SubjectA, Failure);
        var recentB = await AppendAsync(store, scope, k.Id, SubjectB);
        await BackdateAsync(fixture.DataSource, k.Execution, oldA);
        Assert.Equal(2, (await PruneAsync(store, new ReasonletRetention { Scope = scope, ExecutionsOlderThan = FiveDays })).Executions);
        Assert.Equal((4, 3), await RunsAsync(k.Id));

        var erased = await store.EraseAsync(new ReasonletErasure(scope, SubjectA), Ct);

        Assert.Equal((0, 1), (erased.Value.Reasonlets, erased.Value.Executions));
        Assert.Equal((3, 3), await RunsAsync(k.Id));
        Assert.Equal([recentB.Value], await IdsAsync(fixture.DataSource, "SELECT id FROM mnemosyne.reasonlet_executions WHERE scope = $1", scope.Value));
    }

    [Fact]
    public async Task AWindowLongerThanTheCalendarPrunesNothing()
    {
        // Now minus the window falls before the first representable instant, so the cutoff is that instant.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        await OldRunsAsync(store, scope, 2);

        var result = await PruneAsync(store, new ReasonletRetention { Scope = scope, ExecutionsOlderThan = TimeSpan.MaxValue });

        Assert.Equal((0, 0), (result.Reasonlets, result.Executions));
        Assert.Equal((1L, 2L), await CountsAsync(fixture.DataSource, scope));
    }

    [Fact]
    public async Task AReasonletWindowAloneDeletesNoRecentlyUsedRuns()
    {
        // The runs are old, but the Reasonlet ran just now by its metrics, so it is in use and nothing goes.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        await OldRunsAsync(store, scope, 2);

        var result = await PruneAsync(store, new ReasonletRetention { Scope = scope, ReasonletsUnusedFor = FiveDays });

        Assert.Equal((0, 0), (result.Reasonlets, result.Executions));
        Assert.Equal((1L, 2L), await CountsAsync(fixture.DataSource, scope));
    }

    [Theory]
    [MemberData(nameof(InvalidRetentions))]
    public async Task AnInvalidRetentionIsAValidationErrorAndTouchesNothing(string? scope, long? executionDays, long? reasonletDays, string path)
    {
        var store = await MigratedStoreAsync();
        var runs = PgVectorFixture.NewScope();
        await OldRunsAsync(store, runs, 2);
        var retention = new ReasonletRetention
        {
            Scope = scope switch
            {
                null => null,
                "default" => default(ReasonletScope),
                "runs" => runs,
                _ => new ReasonletScope(scope),
            },
            ExecutionsOlderThan = executionDays is { } e ? TimeSpan.FromDays(e) : null,
            ReasonletsUnusedFor = reasonletDays is { } r ? TimeSpan.FromDays(r) : null,
        };

        var result = await store.PruneAsync(retention, Ct);

        Assert.Equal(MnemosyneErrorCode.Validation, result.Error.Code);
        Assert.StartsWith(path + ": ", result.Error.Message);
        Assert.Equal((1L, 2L), await CountsAsync(fixture.DataSource, runs));
    }

    public static TheoryData<string?, long?, long?, string> InvalidRetentions() => new()
    {
        { "runs", null, null, "ExecutionsOlderThan" },
        { "runs", 0, null, "ExecutionsOlderThan" },
        { "runs", -5, null, "ExecutionsOlderThan" },
        { "runs", 5, 0, "ReasonletsUnusedFor" },
        { "default", 5, null, "Scope.Value" },
        { " ", 5, null, "Scope.Value" },
    };

    [Fact]
    public async Task AStoreThatIsNotMigratedIsUnavailable()
    {
        var store = new ReasonletPostgreSqlStore(await fixture.CreateEmptyDatabaseAsync(), new MnemosynePostgreSqlOptions { Dimensions = 3 });

        var result = await store.PruneAsync(new ReasonletRetention { ExecutionsOlderThan = FiveDays }, Ct);

        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
    }

    [Fact]
    public async Task CancellationThrows()
    {
        var store = await MigratedStoreAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.PruneAsync(new ReasonletRetention { Scope = PgVectorFixture.NewScope(), ExecutionsOlderThan = FiveDays }, cancelled.Token));
    }

    [Fact]
    public async Task ADeadlockedBatchIsRetried()
    {
        // The first try rolls back, so the retry finds every run still there and deletes them all.
        var store = await MigratedStoreAsync();
        var calls = 0;
        store.BeforePruneBatchCommit = () => ++calls == 1 ? throw Pg("40P01") : Task.CompletedTask;
        var scope = PgVectorFixture.NewScope();
        await OldRunsAsync(store, scope, 3);

        var result = await PruneAsync(store, new ReasonletRetention { Scope = scope, ExecutionsOlderThan = FiveDays });

        Assert.Equal((0, 3), (result.Reasonlets, result.Executions));
        Assert.Equal(2, calls);
        Assert.Equal((1L, 0L), await CountsAsync(fixture.DataSource, scope));
    }

    [Fact]
    public async Task APersistentConflictGivesUpAfterThreeRetries()
    {
        var store = await MigratedStoreAsync();
        var calls = 0;
        store.BeforePruneBatchCommit = () =>
        {
            calls++;
            throw Pg("40001");
        };
        var scope = PgVectorFixture.NewScope();
        await OldRunsAsync(store, scope, 2);

        var result = await store.PruneAsync(new ReasonletRetention { Scope = scope, ExecutionsOlderThan = FiveDays }, Ct);

        Assert.Equal(MnemosyneErrorCode.Conflict, result.Error.Code);
        Assert.Equal(4, calls);
        Assert.Equal((1L, 2L), await CountsAsync(fixture.DataSource, scope));
    }

    [Fact]
    public async Task AFailedPruneKeepsEarlierBatches()
    {
        // The first batch of three commits; every try of the second fails. Those three stay deleted, and the
        // progress the telemetry reads on the failure path counts them.
        var store = await MigratedStoreAsync();
        store.PruneBatchSizeOverride = 3;
        var calls = 0;
        store.BeforePruneBatchCommit = () => ++calls > 1 ? throw Pg("40001") : Task.CompletedTask;
        var scope = PgVectorFixture.NewScope();
        await OldRunsAsync(store, scope, 7);
        var progress = new PruneProgress();

        var result = await store.PruneAsync(new ReasonletRetention { Scope = scope, ExecutionsOlderThan = FiveDays }, progress, Ct);

        Assert.Equal(MnemosyneErrorCode.Conflict, result.Error.Code);
        Assert.Equal(5, calls);
        Assert.Equal((1L, 4L), await CountsAsync(fixture.DataSource, scope));
        Assert.Equal((0, 3), (progress.Reasonlets, progress.Executions));
    }

    [Fact]
    public async Task ANonRetryableFailureEndsThePruneAtOnce()
    {
        var store = await MigratedStoreAsync();
        var calls = 0;
        store.BeforePruneBatchCommit = () =>
        {
            calls++;
            throw Pg("08006");
        };
        var scope = PgVectorFixture.NewScope();
        await OldRunsAsync(store, scope, 2);

        var result = await store.PruneAsync(new ReasonletRetention { Scope = scope, ExecutionsOlderThan = FiveDays }, Ct);

        Assert.Equal(MnemosyneErrorCode.Unavailable, result.Error.Code);
        Assert.Equal(1, calls);
        Assert.Equal((1L, 2L), await CountsAsync(fixture.DataSource, scope));
    }

    private static PostgresException Pg(string sqlState) => new("injected", "ERROR", "ERROR", sqlState);

    private static async Task<ReasonletPruneResult> PruneAsync(ReasonletPostgreSqlStore store, ReasonletRetention retention)
    {
        var result = await store.PruneAsync(retention, Ct);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }

    private async Task<ReasonletPostgreSqlStore> MigratedStoreAsync()
    {
        var store = fixture.CreateStore();
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        return store;
    }

    /// <summary>Remembers one Reasonlet in the scope and runs it until it has <paramref name="count"/> runs, all ten days old.</summary>
    private async Task OldRunsAsync(ReasonletPostgreSqlStore store, ReasonletScope scope, int count)
    {
        var r = await RememberAsync(store, scope, "r", SubjectA);
        for (var i = 1; i < count; i++)
        {
            await AppendAsync(store, scope, r.Id, SubjectA);
        }

        await Entries.ExecuteAsync(
            fixture.DataSource, "UPDATE mnemosyne.reasonlet_executions SET executed_at = now() - interval '10 days' WHERE scope = $1", scope.Value);
    }

    private static async Task<ReasonletReceipt> RememberAsync(ReasonletPostgreSqlStore store, ReasonletScope scope, string variant, string subject)
    {
        var provenance = new ReasonletProvenance { Source = "test", SubjectId = subject };
        var result = await store.RememberAsync(Entries.Entry(scope, Entries.Graph(variant), provenance: provenance), Ct);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static async Task<ExecutionId> AppendAsync(
        ReasonletPostgreSqlStore store, ReasonletScope scope, ReasonletId id, string subject, ReasonletOutcome? outcome = null)
    {
        var result = await store.AppendExecutionAsync(scope, id, outcome ?? Success, new ReasonletProvenance { Source = "test", SubjectId = subject }, Ct);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static Task BackdateAsync(NpgsqlDataSource dataSource, params ExecutionId[] executions) => Entries.ExecuteAsync(
        dataSource,
        "UPDATE mnemosyne.reasonlet_executions SET executed_at = now() - interval '10 days' WHERE id = ANY($1)",
        executions.Select(e => e.Value).ToArray());

    /// <summary>Counts a scope's Reasonlets and executions.</summary>
    private static async Task<(long Reasonlets, long Executions)> CountsAsync(NpgsqlDataSource dataSource, ReasonletScope scope) => (
        await Entries.ScalarAsync<long>(dataSource, "SELECT count(*) FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value),
        await Entries.ScalarAsync<long>(dataSource, "SELECT count(*) FROM mnemosyne.reasonlet_executions WHERE scope = $1", scope.Value));

    private async Task<(int Runs, int Successes)> RunsAsync(ReasonletId id) => (
        await Entries.ScalarAsync<int>(fixture.DataSource, "SELECT runs FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1", id.Value),
        await Entries.ScalarAsync<int>(fixture.DataSource, "SELECT successes FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1", id.Value));

    /// <summary>Reads a Reasonlet's counters and timestamps.</summary>
    private async Task<(int Runs, int Successes, DateTime LastExecutedAt, DateTime LastSuccessAt)> MetricsAsync(ReasonletId id)
    {
        var (runs, successes) = await RunsAsync(id);
        return (
            runs,
            successes,
            await Entries.ScalarAsync<DateTime>(fixture.DataSource, "SELECT last_executed_at FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1", id.Value),
            await Entries.ScalarAsync<DateTime>(fixture.DataSource, "SELECT last_success_at FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1", id.Value));
    }

    private static async Task<List<Guid>> IdsAsync(NpgsqlDataSource dataSource, string sql, object? parameter)
    {
        await using var command = dataSource.CreateCommand(sql);
        if (parameter is not null)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        await using var reader = await command.ExecuteReaderAsync(Ct);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(Ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}
