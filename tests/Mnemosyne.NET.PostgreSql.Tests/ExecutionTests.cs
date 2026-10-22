using Mnemosyne.NET.Abstractions;
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
        var first = await Entries.ScalarAsync<DateTime>(
            fixture.DataSource, "SELECT recorded_at::timestamp FROM mnemosyne.reasonlet_feedback WHERE execution_id = $1", receipt.Execution.Value);

        Assert.True((await store.AppendFeedbackAsync(scope, receipt.Execution, new ReasonletFeedback(0.9, 0.8, 0.7), Ct)).IsSuccess);

        Assert.Equal("0.9|0.8|0.7", await FeedbackAsync(receipt.Execution));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlet_feedback WHERE execution_id = $1", receipt.Execution.Value));
        Assert.True(
            await Entries.ScalarAsync<DateTime>(
                fixture.DataSource, "SELECT recorded_at::timestamp FROM mnemosyne.reasonlet_feedback WHERE execution_id = $1", receipt.Execution.Value) >= first);
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
