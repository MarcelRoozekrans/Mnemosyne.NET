using Mnemosyne.NET.Abstractions;
using Npgsql;
using Xunit;

namespace Mnemosyne.NET.PostgreSql.Tests;

public sealed class ExecutionTests(PgVectorFixture fixture) : IClassFixture<PgVectorFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ReasonletOutcome Outcome = new(false, TimeSpan.FromTicks(98765), 0.25);

    [Fact]
    public async Task AnAppendedExecutionIsReturnedAndStoredWithItsOutcomeAndProvenance()
    {
        var (store, scope, receipt) = await RememberedAsync();
        var provenance = Entries.Provenance();

        var result = await store.AppendExecutionAsync(scope, receipt.Id, Outcome, provenance, Ct);

        Assert.True(result.IsSuccess);
        var id = result.Value;
        Assert.False(id.IsDefault);
        Assert.NotEqual(receipt.Execution, id);
        Assert.Equal(2L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlet_executions WHERE reasonlet_id = $1", receipt.Id.Value));
        Assert.Equal(
            "f|98765|0.25|test-source|run-1|subject-1|" + scope.Value,
            await Entries.ScalarAsync<string>(
                fixture.DataSource,
                "SELECT concat_ws('|', success, duration_ticks, score, source, run_id, subject_id, scope) FROM mnemosyne.reasonlet_executions WHERE id = $1",
                id.Value));
        Assert.Equal(
            "first|last|",
            await Entries.ScalarAsync<string>(
                fixture.DataSource,
                "SELECT concat_ws('|', tags->>'a', tags->>'z', tags->>'é') FROM mnemosyne.reasonlet_executions WHERE id = $1",
                id.Value));
    }

    [Fact]
    public async Task AnExecutionWithoutOptionalProvenanceStoresNulls()
    {
        var (store, scope, receipt) = await RememberedAsync();

        var id = (await store.AppendExecutionAsync(
            scope, receipt.Id, new ReasonletOutcome(true, TimeSpan.Zero, null), new ReasonletProvenance { Source = "bare" }, Ct)).Value;

        Assert.Equal(
            1L,
            await CountAsync(
                "SELECT count(*) FROM mnemosyne.reasonlet_executions WHERE id = $1 AND score IS NULL AND run_id IS NULL AND subject_id IS NULL AND tags IS NULL",
                id.Value));
    }

    [Fact]
    public async Task AReasonletThatIsMissingOrInAnotherScopeIsNotFound()
    {
        var (store, scope, receipt) = await RememberedAsync();
        var provenance = Entries.Provenance();

        var missing = await store.AppendExecutionAsync(scope, ReasonletId.New(), Outcome, provenance, Ct);
        var otherScope = await store.AppendExecutionAsync(PgVectorFixture.NewScope(), receipt.Id, Outcome, provenance, Ct);

        Assert.Equal(MnemosyneErrorCode.NotFound, missing.Error.Code);
        Assert.Equal(MnemosyneErrorCode.NotFound, otherScope.Error.Code);
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlet_executions WHERE reasonlet_id = $1", receipt.Id.Value));
    }

    [Fact]
    public async Task InvalidExecutionInputsAreValidationErrors()
    {
        var (store, scope, receipt) = await RememberedAsync();
        var provenance = Entries.Provenance();

        AssertValidation("scope.Value: must not be blank", (await store.AppendExecutionAsync(new ReasonletScope(" "), receipt.Id, Outcome, provenance, Ct)).Error);
        AssertValidation("id: must not be the default id", (await store.AppendExecutionAsync(scope, default, Outcome, provenance, Ct)).Error);
        AssertValidation("outcome.Duration: must not be negative", (await store.AppendExecutionAsync(scope, receipt.Id, new ReasonletOutcome(true, TimeSpan.FromTicks(-1), null), provenance, Ct)).Error);
        AssertValidation("provenance.Source: must not be blank", (await store.AppendExecutionAsync(scope, receipt.Id, Outcome, new ReasonletProvenance { Source = " " }, Ct)).Error);
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlet_executions WHERE reasonlet_id = $1", receipt.Id.Value));
    }

    [Fact]
    public async Task FeedbackIsRecordedAndRecordingItAgainReplacesIt()
    {
        var (store, scope, receipt) = await RememberedAsync();

        Assert.True((await store.AppendFeedbackAsync(scope, receipt.Execution, new ReasonletFeedback(0.1, 0.2, 0.3), Ct)).IsSuccess);
        Assert.Equal("0.1|0.2|0.3", await FeedbackAsync(receipt.Execution));
        var first = await Entries.TimestampAsync(
            fixture.DataSource, "SELECT recorded_at FROM mnemosyne.reasonlet_feedback WHERE execution_id = $1", receipt.Execution.Value);

        Assert.True((await store.AppendFeedbackAsync(scope, receipt.Execution, new ReasonletFeedback(0.9, 0.8, 0.7), Ct)).IsSuccess);

        Assert.Equal("0.9|0.8|0.7", await FeedbackAsync(receipt.Execution));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlet_feedback WHERE execution_id = $1", receipt.Execution.Value));
        Assert.True(
            await Entries.TimestampAsync(
                fixture.DataSource, "SELECT recorded_at FROM mnemosyne.reasonlet_feedback WHERE execution_id = $1", receipt.Execution.Value) >= first);
    }

    [Fact]
    public async Task FeedbackOnAnExecutionThatIsMissingOrInAnotherScopeIsNotFound()
    {
        var (store, scope, receipt) = await RememberedAsync();
        var feedback = new ReasonletFeedback(0.5, 0.5, 0.5);

        var missing = await store.AppendFeedbackAsync(scope, ExecutionId.New(), feedback, Ct);
        var otherScope = await store.AppendFeedbackAsync(PgVectorFixture.NewScope(), receipt.Execution, feedback, Ct);

        Assert.Equal(MnemosyneErrorCode.NotFound, missing.Error.Code);
        Assert.Equal(MnemosyneErrorCode.NotFound, otherScope.Error.Code);
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlet_feedback WHERE execution_id = $1", receipt.Execution.Value));
    }

    [Fact]
    public async Task InvalidFeedbackInputsAreValidationErrors()
    {
        var (store, scope, receipt) = await RememberedAsync();
        var feedback = new ReasonletFeedback(0.5, 0.5, 0.5);

        AssertValidation("scope.Value: must not be blank", (await store.AppendFeedbackAsync(new ReasonletScope(""), receipt.Execution, feedback, Ct)).Error);
        AssertValidation("execution: must not be the default id", (await store.AppendFeedbackAsync(scope, default, feedback, Ct)).Error);
        AssertValidation("feedback.Correctness: must be a finite number between 0 and 1", (await store.AppendFeedbackAsync(scope, receipt.Execution, new ReasonletFeedback(0.5, 1.5, 0.5), Ct)).Error);
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlet_feedback WHERE execution_id = $1", receipt.Execution.Value));
    }

    [Fact]
    public async Task AStoreThatIsNotMigratedIsUnavailable()
    {
        var store = new ReasonletPostgreSqlStore(await fixture.CreateEmptyDatabaseAsync(), new MnemosynePostgreSqlOptions { Dimensions = 3 });
        var scope = PgVectorFixture.NewScope();

        var execution = await store.AppendExecutionAsync(scope, ReasonletId.New(), Outcome, Entries.Provenance(), Ct);
        var feedback = await store.AppendFeedbackAsync(scope, ExecutionId.New(), default, Ct);

        Assert.Equal(MnemosyneErrorCode.Unavailable, execution.Error.Code);
        Assert.Equal(MnemosyneErrorCode.Unavailable, feedback.Error.Code);
    }

    [Fact]
    public async Task ADatabaseFailureIsMappedAndAnUnknownOneIsNot()
    {
        var (store, scope, receipt) = await RememberedAsync();
        await Entries.ExecuteAsync(fixture.DataSource, "ALTER TABLE mnemosyne.reasonlet_feedback ADD CONSTRAINT utility_small CHECK (utility < 0.5)");
        try
        {
            // A check violation is neither a conflict nor an outage, so it is a bug that propagates.
            await Assert.ThrowsAnyAsync<Exception>(
                async () => await store.AppendFeedbackAsync(scope, receipt.Execution, new ReasonletFeedback(0.9, 0.9, 0.9), Ct));
        }
        finally
        {
            await Entries.ExecuteAsync(fixture.DataSource, "ALTER TABLE mnemosyne.reasonlet_feedback DROP CONSTRAINT utility_small");
        }
    }

    [Fact]
    public async Task CancellationThrows()
    {
        var (store, scope, receipt) = await RememberedAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.AppendExecutionAsync(scope, receipt.Id, Outcome, Entries.Provenance(), cancelled.Token));
    }

    [Fact]
    public async Task FeedbackFromAnotherScopeCannotOverwriteExistingFeedback()
    {
        var (store, scope, receipt) = await RememberedAsync();
        Assert.True((await store.AppendFeedbackAsync(scope, receipt.Execution, new ReasonletFeedback(0.1, 0.2, 0.3), Ct)).IsSuccess);

        var result = await store.AppendFeedbackAsync(PgVectorFixture.NewScope(), receipt.Execution, new ReasonletFeedback(0.9, 0.8, 0.7), Ct);

        Assert.Equal(MnemosyneErrorCode.NotFound, result.Error.Code);
        Assert.Equal("0.1|0.2|0.3", await FeedbackAsync(receipt.Execution));
    }

    [Fact]
    public async Task FeedbackCancellationThrows()
    {
        var (store, scope, receipt) = await RememberedAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.AppendFeedbackAsync(scope, receipt.Execution, new ReasonletFeedback(0.5, 0.5, 0.5), cancelled.Token));
    }

    [Fact]
    public async Task LastExecutedAtFollowsEveryExecution()
    {
        // The remember's execution sets it, and a later append, a failure here, moves it forward: it tracks any run,
        // not only successes.
        var (store, scope, receipt) = await RememberedAsync();
        Assert.Equal(await ExecutedAtAsync(receipt.Execution), await LastExecutedAtAsync(receipt.Id));

        var later = (await store.AppendExecutionAsync(scope, receipt.Id, Outcome, Entries.Provenance(), Ct)).Value;

        var executedAt = await ExecutedAtAsync(later);
        Assert.True(executedAt > await ExecutedAtAsync(receipt.Execution));
        Assert.Equal(executedAt, await LastExecutedAtAsync(receipt.Id));
    }

    [Fact]
    public async Task TheMetricsCountEveryExecutionExactlyThroughRememberAppendAndASubjectErase()
    {
        // R is C's. C remembered it (a success), A re-ran it as an execution only (a failure), B appended a success
        // and A appended the last success. R's child N has its own row. Erasing A keeps R and subtracts A's two runs,
        // which leaves the counts of C's and B's.
        var store = fixture.CreateStore();
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        var scope = PgVectorFixture.NewScope();
        var r = (await store.RememberAsync(Entries.Entry(scope, provenance: Subject("c")), Ct)).Value;
        Assert.Equal((1, 1), await MetricsAsync(r.Id));
        Assert.Equal(
            ReasonletCaptureKind.ExecutionOnly,
            (await store.RememberAsync(Entries.Entry(scope, parentId: r.Id, outcome: new ReasonletOutcome(false, TimeSpan.Zero, null), provenance: Subject("a")), Ct)).Value.Kind);
        Assert.Equal((2, 1), await MetricsAsync(r.Id));
        var bRun = (await store.AppendExecutionAsync(scope, r.Id, new ReasonletOutcome(true, TimeSpan.Zero, null), Subject("b"), Ct)).Value;
        Assert.True((await store.AppendExecutionAsync(scope, r.Id, new ReasonletOutcome(true, TimeSpan.Zero, null), Subject("a"), Ct)).IsSuccess);
        Assert.Equal((4, 3), await MetricsAsync(r.Id));
        var n = (await store.RememberAsync(Entries.Entry(scope, Entries.Graph("n"), r.Id, provenance: Subject("b")), Ct)).Value;
        Assert.Equal((1, 1), await MetricsAsync(n.Id));
        await AssertMetricsMatchTheExecutionsAsync(scope);

        var erased = await store.EraseAsync(new ReasonletErasure(scope, "a"), Ct);

        Assert.Equal((0, 2), (erased.Value.Reasonlets, erased.Value.Executions));
        Assert.Equal((2, 2), await MetricsAsync(r.Id));
        Assert.Equal(
            await Entries.TimestampAsync(fixture.DataSource, "SELECT executed_at FROM mnemosyne.reasonlet_executions WHERE id = $1", bRun.Value),
            await Entries.TimestampAsync(fixture.DataSource, "SELECT last_success_at FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1", r.Id.Value));
        await AssertMetricsMatchTheExecutionsAsync(scope);
    }

    [Fact]
    public async Task ConcurrentAppendsToOneReasonletLoseNoCount()
    {
        var (store, scope, receipt) = await RememberedAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(
            () => store.AppendExecutionAsync(scope, receipt.Id, new ReasonletOutcome(i % 2 == 0, TimeSpan.Zero, null), Entries.Provenance(), Ct).AsTask(),
            Ct)));

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal((51, 26), await MetricsAsync(receipt.Id));
        await AssertMetricsMatchTheExecutionsAsync(scope);
    }

    [Fact]
    public async Task AnAppendThatWaitsForAnEraseOfItsReasonletIsNotFound()
    {
        // The append locks the Reasonlet FOR KEY SHARE before it writes anything, so it waits for the erase's lock and
        // then finds the Reasonlet gone.
        var (store, scope, receipt) = await RememberedAsync();
        await using var erase = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var transaction = await erase.BeginTransactionAsync(Ct);
        await RunAsync(erase, $"DELETE FROM mnemosyne.reasonlets WHERE id = '{receipt.Id.Value}'");

        var append = store.AppendExecutionAsync(scope, receipt.Id, Outcome, Entries.Provenance(), Ct).AsTask();
        await WaitForLockWaitAsync(erase);
        await transaction.CommitAsync(Ct);
        var result = await append;

        Assert.Equal(MnemosyneErrorCode.NotFound, result.Error.Code);
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlet_executions WHERE scope = $1", scope.Value));
    }

    [Fact]
    public async Task FeedbackThatWaitsForAnEraseOfItsExecutionIsAConflict()
    {
        // The upsert reads the execution, then its foreign-key check waits for the erase's lock on it and finds it
        // gone: 23503, a lost race with the erase.
        var (store, scope, receipt) = await RememberedAsync();
        await using var erase = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var transaction = await erase.BeginTransactionAsync(Ct);
        await RunAsync(erase, $"DELETE FROM mnemosyne.reasonlet_executions WHERE id = '{receipt.Execution.Value}'");

        var feedback = store.AppendFeedbackAsync(scope, receipt.Execution, new ReasonletFeedback(0.5, 0.5, 0.5), Ct).AsTask();
        await WaitForLockWaitAsync(erase);
        await transaction.CommitAsync(Ct);
        var result = await feedback;

        Assert.Equal(MnemosyneErrorCode.Conflict, result.Error.Code);
        Assert.Contains("23503", result.Error.Detail, StringComparison.Ordinal);
    }

    private static ReasonletProvenance Subject(string subject) => new() { Source = "test", SubjectId = subject };

    private async Task<(int Runs, int Successes)> MetricsAsync(ReasonletId id) => (
        await Entries.ScalarAsync<int>(fixture.DataSource, "SELECT runs FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1", id.Value),
        await Entries.ScalarAsync<int>(fixture.DataSource, "SELECT successes FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1", id.Value));

    private Task<DateTimeOffset> ExecutedAtAsync(ExecutionId id) => Entries.TimestampAsync(
        fixture.DataSource, "SELECT executed_at FROM mnemosyne.reasonlet_executions WHERE id = $1", id.Value);

    private Task<DateTimeOffset> LastExecutedAtAsync(ReasonletId id) => Entries.TimestampAsync(
        fixture.DataSource, "SELECT last_executed_at FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1", id.Value);

    /// <summary>Asserts that every Reasonlet of the scope has a metrics row equal to an aggregate of its executions.</summary>
    private async Task AssertMetricsMatchTheExecutionsAsync(ReasonletScope scope) => Assert.Equal(
        0L,
        await Entries.ScalarAsync<long>(
            fixture.DataSource,
            """
            SELECT count(*) FROM mnemosyne.reasonlets r
            LEFT JOIN mnemosyne.reasonlet_metrics m ON m.reasonlet_id = r.id
            LEFT JOIN LATERAL (SELECT count(*)::int AS runs, (count(*) FILTER (WHERE success))::int AS successes,
                                      max(executed_at) FILTER (WHERE success) AS last_success_at,
                                      max(executed_at) AS last_executed_at
                               FROM mnemosyne.reasonlet_executions e WHERE e.reasonlet_id = r.id) a ON true
            WHERE r.scope = $1
              AND (m.reasonlet_id IS NULL OR m.scope <> r.scope OR (m.runs, m.successes) <> (a.runs, a.successes)
                   OR m.last_success_at IS DISTINCT FROM a.last_success_at
                   OR m.last_executed_at IS DISTINCT FROM a.last_executed_at)
            """,
            scope.Value));

    private static async Task RunAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Waits until a backend other than <paramref name="other"/>'s is blocked on a lock.</summary>
    private async Task WaitForLockWaitAsync(NpgsqlConnection other)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var waiting = await Entries.ScalarAsync<long>(
                fixture.DataSource,
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND pid <> $1",
                other.ProcessID);
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(25, Ct);
        }

        Assert.Fail("The store never waited for a lock.");
    }

    private async Task<(ReasonletPostgreSqlStore Store, ReasonletScope Scope, ReasonletReceipt Receipt)> RememberedAsync()
    {
        var store = fixture.CreateStore();
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        var scope = PgVectorFixture.NewScope();
        var receipt = (await store.RememberAsync(Entries.Entry(scope), Ct)).Value;
        return (store, scope, receipt);
    }

    private static void AssertValidation(string message, MnemosyneError error)
    {
        Assert.Equal(MnemosyneErrorCode.Validation, error.Code);
        Assert.Equal(message, error.Message);
    }

    private Task<long> CountAsync(string sql, object parameter) => Entries.ScalarAsync<long>(fixture.DataSource, sql, parameter);

    private Task<string> FeedbackAsync(ExecutionId execution) => Entries.ScalarAsync<string>(
        fixture.DataSource,
        "SELECT concat_ws('|', utility, correctness, reusability) FROM mnemosyne.reasonlet_feedback WHERE execution_id = $1",
        execution.Value);
}
