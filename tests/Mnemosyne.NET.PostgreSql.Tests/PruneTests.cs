using System.Data.Async;
using System.Data.Async.Adapters;
using System.Diagnostics;
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
        // Ten runs in batches of three: three full batches, a short one, and an empty one, which ends the loop.
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
        Assert.Equal(5, batches);
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
        // The first try rolls back, so the retry finds every run still there and deletes them all; an empty batch
        // then ends the prune.
        var store = await MigratedStoreAsync();
        var calls = 0;
        store.BeforePruneBatchCommit = () => ++calls == 1 ? throw Pg("40P01") : Task.CompletedTask;
        var scope = PgVectorFixture.NewScope();
        await OldRunsAsync(store, scope, 3);

        var result = await PruneAsync(store, new ReasonletRetention { Scope = scope, ExecutionsOlderThan = FiveDays });

        Assert.Equal((0, 3), (result.Reasonlets, result.Executions));
        Assert.Equal(3, calls);
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

    [Fact]
    public async Task CancellationInsideABatchPropagatesAndIsNotRetried()
    {
        // The batch is cancelled after its delete and before its commit. The cancellation is neither retried nor
        // mapped to an error, and the batch rolls back.
        var store = await MigratedStoreAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var calls = 0;
        store.BeforePruneBatchCommit = async () =>
        {
            calls++;
            await cancellation.CancelAsync();
            cancellation.Token.ThrowIfCancellationRequested();
        };
        var scope = PgVectorFixture.NewScope();
        await OldRunsAsync(store, scope, 2);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.PruneAsync(new ReasonletRetention { Scope = scope, ExecutionsOlderThan = FiveDays }, cancellation.Token));

        Assert.Equal(1, calls);
        Assert.Equal((1L, 2L), await CountsAsync(fixture.DataSource, scope));
    }

    [Fact]
    public async Task AnIdleLineageGoesWhole()
    {
        // Every version and every run is older than the window, so the lineage goes with its runs, feedback and metrics.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var root = await RememberAsync(store, scope, "root", SubjectA);
        var child = await RememberAsync(store, scope, "child", SubjectA, root.Id);
        await RememberAsync(store, scope, "grandchild", SubjectA, child.Id);
        var run = await AppendAsync(store, scope, root.Id, SubjectA);
        Assert.True((await store.AppendFeedbackAsync(scope, run, Feedback, Ct)).IsSuccess);
        await BackdateScopeAsync(fixture.DataSource, scope);

        var result = await PruneAsync(store, new ReasonletRetention { Scope = scope, ReasonletsUnusedFor = FiveDays });

        Assert.Equal((3, 4), (result.Reasonlets, result.Executions));
        Assert.Equal((0L, 0L), await CountsAsync(fixture.DataSource, scope));
        Assert.Equal(0L, await Entries.ScalarAsync<long>(fixture.DataSource, "SELECT count(*) FROM mnemosyne.reasonlet_metrics WHERE scope = $1", scope.Value));
        Assert.Equal(0L, await Entries.ScalarAsync<long>(fixture.DataSource, "SELECT count(*) FROM mnemosyne.reasonlet_feedback WHERE scope = $1", scope.Value));
    }

    [Fact]
    public async Task ASupersededVersionStaysWhileALaterVersionIsUsed()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var root = await RememberAsync(store, scope, "root", SubjectA);
        var child = await RememberAsync(store, scope, "child", SubjectA, root.Id);
        await BackdateScopeAsync(fixture.DataSource, scope);
        await Entries.ExecuteAsync(
            fixture.DataSource,
            "UPDATE mnemosyne.reasonlet_metrics SET last_executed_at = now() - interval '1 day' WHERE reasonlet_id = $1",
            child.Id.Value);

        var result = await PruneAsync(store, new ReasonletRetention { Scope = scope, ReasonletsUnusedFor = FiveDays });

        Assert.Equal((0, 0), (result.Reasonlets, result.Executions));
        Assert.Equal((2L, 2L), await CountsAsync(fixture.DataSource, scope));
    }

    [Fact]
    public async Task ANewVersionInsideTheWindowKeepsItsParent()
    {
        // The child never ran inside the window, but it was remembered inside it.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var parent = await RememberAsync(store, scope, "parent", SubjectA);
        var child = await RememberAsync(store, scope, "child", SubjectA, parent.Id);
        await BackdateScopeAsync(fixture.DataSource, scope);
        await Entries.ExecuteAsync(
            fixture.DataSource, "UPDATE mnemosyne.reasonlets SET created_at = now() - interval '1 day' WHERE id = $1", child.Id.Value);

        var result = await PruneAsync(store, new ReasonletRetention { Scope = scope, ReasonletsUnusedFor = FiveDays });

        Assert.Equal((0, 0), (result.Reasonlets, result.Executions));
        Assert.Equal((2L, 2L), await CountsAsync(fixture.DataSource, scope));
    }

    [Fact]
    public async Task AnIdleBranchGoesAndTheSharedAncestorStays()
    {
        // R has an idle branch A -> A2 and a used branch B. A prune of every scope also takes another scope's idle
        // Reasonlet, so it runs on a database no other test shares.
        var dataSource = await fixture.CreateEmptyDatabaseAsync();
        var store = new ReasonletPostgreSqlStore(dataSource, new MnemosynePostgreSqlOptions { Dimensions = 3 });
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        var scope = PgVectorFixture.NewScope();
        var r = await RememberAsync(store, scope, "r", SubjectA);
        var a = await RememberAsync(store, scope, "a", SubjectA, r.Id);
        await RememberAsync(store, scope, "a2", SubjectA, a.Id);
        var b = await RememberAsync(store, scope, "b", SubjectA, r.Id);
        var otherScope = PgVectorFixture.NewScope();
        await RememberAsync(store, otherScope, "o", SubjectB);
        await BackdateScopeAsync(dataSource, scope);
        await BackdateScopeAsync(dataSource, otherScope);
        await Entries.ExecuteAsync(
            dataSource, "UPDATE mnemosyne.reasonlet_metrics SET last_executed_at = now() - interval '1 day' WHERE reasonlet_id = $1", b.Id.Value);

        var result = await PruneAsync(store, new ReasonletRetention { ReasonletsUnusedFor = FiveDays });

        Assert.Equal((3, 3), (result.Reasonlets, result.Executions));
        Assert.Equal(
            new[] { r.Id.Value, b.Id.Value }.Order(),
            (await IdsAsync(dataSource, "SELECT id FROM mnemosyne.reasonlets", null)).Order());
        Assert.Equal(r.Id.Value, await Entries.ScalarAsync<Guid>(dataSource, "SELECT parent_id FROM mnemosyne.reasonlets WHERE id = $1", b.Id.Value));
        Assert.Equal((0L, 0L), await CountsAsync(dataSource, otherScope));
    }

    [Fact]
    public async Task PendingReviewIsPrunedLikeActive()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var pending = await RememberAsync(store, scope, "pending", SubjectA);
        await Entries.ExecuteAsync(
            fixture.DataSource, $"UPDATE mnemosyne.reasonlets SET status = {(short)ReasonletStatus.PendingReview} WHERE id = $1", pending.Id.Value);
        await BackdateScopeAsync(fixture.DataSource, scope);

        var result = await PruneAsync(store, new ReasonletRetention { Scope = scope, ReasonletsUnusedFor = FiveDays });

        Assert.Equal((1, 1), (result.Reasonlets, result.Executions));
        Assert.Equal((0L, 0L), await CountsAsync(fixture.DataSource, scope));
    }

    [Fact]
    public async Task BothWindowsCountEachRunOnce()
    {
        // The run window takes every old run first, the idle Reasonlet's included, so the Reasonlet pass finds none of
        // them left to count again. The used Reasonlet keeps its recent run.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var idle = await RememberAsync(store, scope, "idle", SubjectA);
        await AppendAsync(store, scope, idle.Id, SubjectA);
        var used = await RememberAsync(store, scope, "used", SubjectA);
        await BackdateScopeAsync(fixture.DataSource, scope);
        var recent = await AppendAsync(store, scope, used.Id, SubjectA);

        var result = await PruneAsync(
            store, new ReasonletRetention { Scope = scope, ExecutionsOlderThan = FiveDays, ReasonletsUnusedFor = FiveDays });

        Assert.Equal((1, 3), (result.Reasonlets, result.Executions));
        Assert.Equal([used.Id.Value], await IdsAsync(fixture.DataSource, "SELECT id FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value));
        Assert.Equal([recent.Value], await IdsAsync(fixture.DataSource, "SELECT id FROM mnemosyne.reasonlet_executions WHERE scope = $1", scope.Value));
    }

    [Fact]
    public async Task ALongIdleChainPrunesAcrossBatches()
    {
        // 2,500 versions in one chain, each with one old run. Batches of 1,000 go deepest first, so after every
        // committed batch each remaining row that had a parent still has it: no batch deletes a parent and keeps its
        // child. The foreign key would set a cut link to null rather than leave it dangling, so a null is the sign.
        var store = await MigratedStoreAsync();
        store.PruneBatchSizeOverride = 1000;
        var scope = PgVectorFixture.NewScope();
        await ChainAsync(fixture.DataSource, scope, 2500);
        var children = (await IdsAsync(
            fixture.DataSource, "SELECT id FROM mnemosyne.reasonlets WHERE scope = $1 AND parent_id IS NOT NULL", scope.Value)).ToArray();
        Assert.Equal(2499, children.Length);
        var checks = 0;
        store.AfterPruneBatch = async () =>
        {
            checks++;
            Assert.Equal(0L, await Entries.ScalarAsync<long>(
                fixture.DataSource, "SELECT count(*) FROM mnemosyne.reasonlets WHERE id = ANY($1) AND parent_id IS NULL", children));
        };

        var result = await PruneAsync(store, new ReasonletRetention { Scope = scope, ReasonletsUnusedFor = FiveDays });

        Assert.Equal((2500, 2500), (result.Reasonlets, result.Executions));
        Assert.Equal((0L, 0L), await CountsAsync(fixture.DataSource, scope));

        // Per chunk of 1,000: a run batch that deletes the chunk's runs, an empty one, and the Reasonlet batch.
        Assert.Equal(9, checks);
    }

    [Fact(Timeout = 30_000)]
    public async Task AReasonletUsedAfterTheSelectSurvives()
    {
        // Root -> candidate -> leaf, all idle when the prune lists them. Another transaction holds the candidate as an
        // append does, so the prune, having listed it, waits for its lock. That transaction then appends a run with the
        // store's own insert and commits. The re-check sees the run: the candidate and its root survive and are not
        // counted. The leaf is still idle and goes; the root, the leaf's batch-mate, is not deleted with it.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var (root, candidate, leaf) = await IdleLineageAsync(store, scope);
        await using var other = (await fixture.DataSource.OpenConnectionAsync(Ct)).AsAsync();
        await using var transaction = await other.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, Ct);
        var pid = await RunAsync(other, transaction, $"SELECT 1 FROM mnemosyne.reasonlets WHERE id = '{candidate.Id.Value}' FOR KEY SHARE");

        var prune = store.PruneAsync(new ReasonletRetention { Scope = scope, ReasonletsUnusedFor = FiveDays }, Ct).AsTask();
        await WaitForPruneLockWaitAsync(pid);
        var appended = await new Repositories.ExecutionRepository(other).InsertAsync(
            Guid.CreateVersion7(), candidate.Id.Value, scope.Value, true, 10, null, "test", null, SubjectB, null, transaction, Ct);
        Assert.Equal(1, appended);
        await transaction.CommitAsync(Ct);
        var result = await prune;

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : null);
        Assert.Equal((1, 1), (result.Value.Reasonlets, result.Value.Executions));
        Assert.Equal(
            new[] { root.Id.Value, candidate.Id.Value }.Order(),
            (await IdsAsync(fixture.DataSource, "SELECT id FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value)).Order());
        Assert.Equal(0L, await Entries.ScalarAsync<long>(fixture.DataSource, "SELECT count(*) FROM mnemosyne.reasonlets WHERE id = $1", leaf.Id.Value));
        Assert.Equal(root.Id.Value, await Entries.ScalarAsync<Guid>(
            fixture.DataSource, "SELECT parent_id FROM mnemosyne.reasonlets WHERE id = $1", candidate.Id.Value));
        Assert.Equal(3L, await Entries.ScalarAsync<long>(
            fixture.DataSource, "SELECT count(*) FROM mnemosyne.reasonlet_executions WHERE scope = $1", scope.Value));
    }

    [Fact(Timeout = 30_000)]
    public async Task AReasonletGivenANewVersionAfterTheSelectSurvives()
    {
        // As above, but the other transaction remembers a new child version of the candidate, as the store's remember
        // does: it locks the parent, inserts the child, and records the child's first run. The new child is used, so
        // the candidate and its root are too.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var (root, candidate, leaf) = await IdleLineageAsync(store, scope);
        await using var other = (await fixture.DataSource.OpenConnectionAsync(Ct)).AsAsync();
        await using var transaction = await other.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, Ct);
        var pid = await RunAsync(other, transaction, $"SELECT 1 FROM mnemosyne.reasonlets WHERE id = '{candidate.Id.Value}' FOR NO KEY UPDATE");

        var prune = store.PruneAsync(new ReasonletRetention { Scope = scope, ReasonletsUnusedFor = FiveDays }, Ct).AsTask();
        await WaitForPruneLockWaitAsync(pid);
        var newChild = Guid.CreateVersion7();
        await RunAsync(
            other,
            transaction,
            "INSERT INTO mnemosyne.reasonlets (id, scope, name, intent, version, graph, content_hash, parent_id, status, source, created_at, embedding) "
            + $"VALUES ('{newChild}', '{scope.Value}', 'n', 'i', '1.3', '{{}}', repeat('0', 64), '{candidate.Id.Value}', 1, 's', now(), '[1,0,0]')");
        Assert.Equal(1, await new Repositories.ExecutionRepository(other).InsertAsync(
            Guid.CreateVersion7(), newChild, scope.Value, true, 10, null, "test", null, SubjectB, null, transaction, Ct));
        await transaction.CommitAsync(Ct);
        var result = await prune;

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : null);
        Assert.Equal((1, 1), (result.Value.Reasonlets, result.Value.Executions));
        Assert.Equal(
            new[] { root.Id.Value, candidate.Id.Value, newChild }.Order(),
            (await IdsAsync(fixture.DataSource, "SELECT id FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value)).Order());
        Assert.Equal(0L, await Entries.ScalarAsync<long>(fixture.DataSource, "SELECT count(*) FROM mnemosyne.reasonlets WHERE id = $1", leaf.Id.Value));
        Assert.Equal(candidate.Id.Value, await Entries.ScalarAsync<Guid>(
            fixture.DataSource, "SELECT parent_id FROM mnemosyne.reasonlets WHERE id = $1", newChild));
    }

    [Fact(Timeout = 30_000)]
    public async Task APruneAndASubjectEraseOfAnIdleReasonletItKeepsBothComplete()
    {
        // R is B's and idle. Its runs are B's remember run and, later, A's run, so erasing A keeps R and removes A's
        // run. A subject erase locks that run and then R's metrics row, without locking R. Another transaction holds
        // B's run, the lower id, so the prune stops on it holding R. The erase then runs while the prune waits: it
        // holds nothing the prune holds, so it completes. Had the prune locked R's metrics row before R's runs, the
        // erase would wait for it while holding A's run, which the prune needs next: a deadlock once B's run is free.
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var r = await RememberAsync(store, scope, "r", SubjectB);
        await AppendAsync(store, scope, r.Id, SubjectA);
        await BackdateScopeAsync(fixture.DataSource, scope);
        await using var other = (await fixture.DataSource.OpenConnectionAsync(Ct)).AsAsync();
        await using var transaction = await other.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, Ct);
        var pid = await RunAsync(
            other, transaction, $"SELECT 1 FROM mnemosyne.reasonlet_executions WHERE id = '{r.Execution.Value}' FOR KEY SHARE");

        var prune = store.PruneAsync(new ReasonletRetention { Scope = scope, ReasonletsUnusedFor = FiveDays }, Ct).AsTask();
        await WaitForPruneLockWaitAsync(pid);
        var erase = store.EraseAsync(new ReasonletErasure(scope, SubjectA), Ct).AsTask();
        var erasedFirst = await Task.WhenAny(erase, Task.Delay(TimeSpan.FromSeconds(5), Ct)) == erase;
        await transaction.CommitAsync(Ct);
        var erased = await erase;
        var pruned = await prune;

        Assert.True(erasedFirst, "The erase waited for the prune.");
        Assert.True(erased.IsSuccess, erased.IsFailure ? erased.Error.ToString() : null);
        Assert.True(pruned.IsSuccess, pruned.IsFailure ? pruned.Error.ToString() : null);
        Assert.Equal((0, 1), (erased.Value.Reasonlets, erased.Value.Executions));
        Assert.Equal((1, 1), (pruned.Value.Reasonlets, pruned.Value.Executions));
        Assert.Equal((0L, 0L), await CountsAsync(fixture.DataSource, scope));
    }

    [Fact]
    public async Task AFailedIdlePruneKeepsEarlierBatches()
    {
        // A chain of four in batches of two. The first chunk commits: a run batch deletes its two runs, an empty run
        // batch ends that loop, and the Reasonlet batch deletes the two deepest versions. Every try of the next batch
        // then fails. What was committed stays deleted, and the progress the telemetry reads counts it.
        var store = await MigratedStoreAsync();
        store.PruneBatchSizeOverride = 2;
        var calls = 0;
        store.BeforePruneBatchCommit = () => ++calls > 3 ? throw Pg("40001") : Task.CompletedTask;
        var scope = PgVectorFixture.NewScope();
        var chain = await ChainAsync(fixture.DataSource, scope, 4);
        var progress = new PruneProgress();

        var result = await store.PruneAsync(new ReasonletRetention { Scope = scope, ReasonletsUnusedFor = FiveDays }, progress, Ct);

        Assert.Equal(MnemosyneErrorCode.Conflict, result.Error.Code);
        Assert.Equal(7, calls);
        Assert.Equal((2, 2), (progress.Reasonlets, progress.Executions));
        Assert.Equal(
            chain[..2].Order(),
            (await IdsAsync(fixture.DataSource, "SELECT id FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value)).Order());
        Assert.Equal((2L, 2L), await CountsAsync(fixture.DataSource, scope));
    }

    [Fact]
    public async Task AReasonletWindowLongerThanTheCalendarPrunesNothing()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        await ChainAsync(fixture.DataSource, scope, 2);

        var result = await PruneAsync(store, new ReasonletRetention { Scope = scope, ReasonletsUnusedFor = TimeSpan.MaxValue });

        Assert.Equal((0, 0), (result.Reasonlets, result.Executions));
        Assert.Equal((2L, 2L), await CountsAsync(fixture.DataSource, scope));
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

    private static async Task<ReasonletReceipt> RememberAsync(
        ReasonletPostgreSqlStore store, ReasonletScope scope, string variant, string subject, ReasonletId? parent = null)
    {
        var provenance = new ReasonletProvenance { Source = "test", SubjectId = subject };
        var result = await store.RememberAsync(Entries.Entry(scope, Entries.Graph(variant), parent, provenance: provenance), Ct);
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

    /// <summary>Makes every Reasonlet of the scope ten days old, as remembered and as last run, and every run of it.</summary>
    private static async Task BackdateScopeAsync(NpgsqlDataSource dataSource, ReasonletScope scope)
    {
        await Entries.ExecuteAsync(
            dataSource, "UPDATE mnemosyne.reasonlets SET created_at = now() - interval '10 days' WHERE scope = $1", scope.Value);
        await Entries.ExecuteAsync(
            dataSource,
            "UPDATE mnemosyne.reasonlet_metrics SET last_executed_at = now() - interval '10 days', "
            + "last_success_at = now() - interval '10 days' WHERE scope = $1",
            scope.Value);
        await Entries.ExecuteAsync(
            dataSource, "UPDATE mnemosyne.reasonlet_executions SET executed_at = now() - interval '10 days' WHERE scope = $1", scope.Value);
    }

    /// <summary>
    /// Writes a chain of <paramref name="length"/> versions in the scope with SQL, each ten days old with one run as old
    /// and the metrics row that run gives it, and answers their ids from the top down.
    /// </summary>
    private static async Task<Guid[]> ChainAsync(NpgsqlDataSource dataSource, ReasonletScope scope, int length)
    {
        var ids = Enumerable.Range(0, length).Select(_ => Guid.CreateVersion7()).ToArray();
        await Entries.ExecuteAsync(
            dataSource,
            """
            INSERT INTO mnemosyne.reasonlets
                (id, scope, name, intent, version, graph, content_hash, parent_id, status, source, created_at, embedding)
            SELECT $2[i], $1, 'n', 'i', '1.' || (i - 1), '{}', repeat('0', 64), $2[i - 1], 1, 's', now() - interval '10 days', '[1,0,0]'
            FROM generate_series(1, $3) i
            """,
            scope.Value,
            ids,
            length);
        await Entries.ExecuteAsync(
            dataSource,
            """
            INSERT INTO mnemosyne.reasonlet_executions (id, reasonlet_id, scope, executed_at, success, duration_ticks, source)
            SELECT gen_random_uuid(), id, scope, now() - interval '10 days', true, 0, 's' FROM mnemosyne.reasonlets WHERE scope = $1
            """,
            scope.Value);
        await Entries.ExecuteAsync(
            dataSource,
            """
            INSERT INTO mnemosyne.reasonlet_metrics (reasonlet_id, scope, runs, successes, last_success_at, last_executed_at)
            SELECT id, scope, 1, 1, now() - interval '10 days', now() - interval '10 days' FROM mnemosyne.reasonlets WHERE scope = $1
            """,
            scope.Value);
        return ids;
    }

    /// <summary>Remembers root, candidate and leaf, one under the other, and makes them all idle.</summary>
    private async Task<(ReasonletReceipt Root, ReasonletReceipt Candidate, ReasonletReceipt Leaf)> IdleLineageAsync(
        ReasonletPostgreSqlStore store, ReasonletScope scope)
    {
        var root = await RememberAsync(store, scope, "root", SubjectA);
        var candidate = await RememberAsync(store, scope, "candidate", SubjectA, root.Id);
        var leaf = await RememberAsync(store, scope, "leaf", SubjectA, candidate.Id);
        await BackdateScopeAsync(fixture.DataSource, scope);
        return (root, candidate, leaf);
    }

    /// <summary>Runs a statement in the transaction and answers the connection's backend process id.</summary>
    private static async Task<int> RunAsync(IAsyncDbConnection connection, IAsyncDbTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
        command.CommandText = "SELECT pg_backend_pid()";
        return (int)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>
    /// Waits, for at most ten seconds, until <paramref name="count"/> client backends other than
    /// <paramref name="otherPid"/> wait for a lock: the prune, which has listed its candidates and is locking them, or
    /// the store operations the test started before it.
    /// </summary>
    private async Task WaitForPruneLockWaitAsync(int otherPid, int count = 1)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            var waiting = await Entries.ScalarAsync<long>(
                fixture.DataSource,
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND backend_type = 'client backend' AND pid <> $1",
                otherPid);
            if (waiting >= count)
            {
                return;
            }

            await Task.Delay(25, Ct);
        }

        Assert.Fail($"Fewer than {count} backends waited for a lock within ten seconds.");
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
