using Mnemosyne.NET.Abstractions;
using Npgsql;
using Xunit;

namespace Mnemosyne.NET.PostgreSql.Tests;

/// <summary>Approving and rejecting Reasonlets that wait for review, against a real database.</summary>
public sealed class ReviewTests(PgVectorFixture fixture) : IClassFixture<PgVectorFixture>
{
    private const string NotFound = "The Reasonlet does not exist in the scope.";
    private const string NotPending = "Only a Reasonlet pending review can be rejected.";
    private const string HasVersions = "The Reasonlet has versions below it; approve or reject them first.";

    private static readonly ReasonletFeedback Feedback = new(0.5, 0.5, 0.5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ApproveMakesAPendingReasonletRecallable()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var pending = await RememberAsync(store, scope, "a", review: true);

        var result = await store.ApproveAsync(scope, pending.Id, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(ReasonletStatus.Active, (await store.GetAsync(scope, pending.Id, Ct)).Value.Status);
        var found = (await store.SearchAsync(scope, Entries.Entry(scope).Embedding.Span.ToArray(), null, 10, Ct)).Value;
        Assert.Equal([pending.Id], found.Select(c => c.Reasonlet.Id));
    }

    [Fact]
    public async Task ApprovingAnActiveReasonletSucceeds()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var active = await RememberAsync(store, scope, "a");
        var pending = await RememberAsync(store, scope, "b", review: true);
        Assert.True((await store.ApproveAsync(scope, pending.Id, Ct)).IsSuccess);

        Assert.True((await store.ApproveAsync(scope, active.Id, Ct)).IsSuccess);
        Assert.True((await store.ApproveAsync(scope, pending.Id, Ct)).IsSuccess);

        Assert.Equal(ReasonletStatus.Active, (await store.GetAsync(scope, active.Id, Ct)).Value.Status);
        Assert.Equal(ReasonletStatus.Active, (await store.GetAsync(scope, pending.Id, Ct)).Value.Status);
    }

    [Fact]
    public async Task RejectDeletesThePendingReasonletAndItsRuns()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var kept = await RememberAsync(store, scope, "kept");
        var keptPending = await RememberAsync(store, scope, "kept-pending", review: true);
        var pending = await RememberAsync(store, scope, "doomed", review: true);
        var run = await store.AppendExecutionAsync(
            scope, pending.Id, new ReasonletOutcome(true, TimeSpan.FromMilliseconds(5), null), Entries.Provenance(), null, Ct);
        Assert.True(run.IsSuccess);
        Assert.True((await store.AppendFeedbackAsync(scope, run.Value.Execution, Feedback, Ct)).IsSuccess);
        Assert.True((await store.AppendFeedbackAsync(scope, kept.Execution, Feedback, Ct)).IsSuccess);
        Assert.Equal((2L, 1L, 1L), await RowsOfAsync(pending.Id));

        var result = await store.RejectAsync(scope, pending.Id, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(MnemosyneErrorCode.NotFound, (await store.GetAsync(scope, pending.Id, Ct)).Error.Code);
        Assert.Equal((0L, 0L, 0L), await RowsOfAsync(pending.Id));
        Assert.Equal((1L, 1L, 1L), await RowsOfAsync(kept.Id));
        Assert.Equal((1L, 0L, 1L), await RowsOfAsync(keptPending.Id));
        Assert.Equal(2L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value));
    }

    [Fact]
    public async Task RejectingAnActiveReasonletIsAConflict()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var active = await RememberAsync(store, scope, "a");

        var result = await store.RejectAsync(scope, active.Id, Ct);

        Assert.Equal(MnemosyneErrorCode.Conflict, result.Error.Code);
        Assert.Equal(NotPending, result.Error.Message);
        Assert.Equal(ReasonletStatus.Active, (await store.GetAsync(scope, active.Id, Ct)).Value.Status);
        Assert.Equal((1L, 0L, 1L), await RowsOfAsync(active.Id));
    }

    [Fact]
    public async Task RejectingAReasonletWithAVersionIsAConflict()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var parent = await RememberAsync(store, scope, "a", review: true);
        var child = await RememberAsync(store, scope, "b", parent.Id, review: true);

        var result = await store.RejectAsync(scope, parent.Id, Ct);

        Assert.Equal(MnemosyneErrorCode.Conflict, result.Error.Code);
        Assert.Equal(HasVersions, result.Error.Message);
        Assert.True((await store.GetAsync(scope, parent.Id, Ct)).IsSuccess);
        Assert.Equal(parent.Id, (await store.GetAsync(scope, child.Id, Ct)).Value.ParentId);
    }

    [Fact]
    public async Task ApproveAndRejectInAnotherScopeAreNotFound()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var otherScope = PgVectorFixture.NewScope();
        var pending = await RememberAsync(store, scope, "a", review: true);
        var elsewhere = await RememberAsync(store, otherScope, "a", review: true);

        var approved = await store.ApproveAsync(otherScope, pending.Id, Ct);
        var rejected = await store.RejectAsync(otherScope, pending.Id, Ct);
        var missing = await store.RejectAsync(scope, ReasonletId.New(), Ct);

        Assert.Equal((MnemosyneErrorCode.NotFound, NotFound), (approved.Error.Code, approved.Error.Message));
        Assert.Equal((MnemosyneErrorCode.NotFound, NotFound), (rejected.Error.Code, rejected.Error.Message));
        Assert.Equal((MnemosyneErrorCode.NotFound, NotFound), (missing.Error.Code, missing.Error.Message));
        Assert.Equal(ReasonletStatus.PendingReview, (await store.GetAsync(scope, pending.Id, Ct)).Value.Status);
        Assert.Equal(ReasonletStatus.PendingReview, (await store.GetAsync(otherScope, elsewhere.Id, Ct)).Value.Status);
        Assert.Equal((1L, 0L, 1L), await RowsOfAsync(pending.Id));
    }

    [Fact]
    public async Task ApproveAndRejectValidateTheirArguments()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var id = ReasonletId.New();

        var results = new[]
        {
            await store.ApproveAsync(default, id, Ct),
            await store.ApproveAsync(scope, default, Ct),
            await store.RejectAsync(default, id, Ct),
            await store.RejectAsync(scope, default, Ct),
        };

        Assert.All(results, r => Assert.Equal(MnemosyneErrorCode.Validation, r.Error.Code));
        Assert.Equal(
            ["scope.Value: must not be blank", "id: must not be the default id", "scope.Value: must not be blank", "id: must not be the default id"],
            results.Select(r => r.Error.Message));
    }

    [Fact]
    public async Task AStoreThatIsNotMigratedIsUnavailable()
    {
        var store = new ReasonletPostgreSqlStore(await fixture.CreateEmptyDatabaseAsync(), new MnemosynePostgreSqlOptions { Dimensions = 3 });
        var scope = PgVectorFixture.NewScope();

        Assert.Equal(MnemosyneErrorCode.Unavailable, (await store.ApproveAsync(scope, ReasonletId.New(), Ct)).Error.Code);
        Assert.Equal(MnemosyneErrorCode.Unavailable, (await store.RejectAsync(scope, ReasonletId.New(), Ct)).Error.Code);
    }

    [Fact]
    public async Task RejectRacingAVersionNeverOrphansIt()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var parent = await RememberAsync(store, scope, "a", review: true);
        var childId = Guid.CreateVersion7();

        // A remember adding a version holds the parent FOR NO KEY UPDATE until it commits. The reject starts while that
        // lock is held, so it waits, and the child commits while it waits: the reject must see the child afterwards.
        await using (var remember = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await using var transaction = await remember.BeginTransactionAsync(Ct);
            await RunAsync(remember, "SELECT id FROM mnemosyne.reasonlets WHERE scope = $1 AND id = $2 FOR NO KEY UPDATE", scope.Value, parent.Id.Value);

            var reject = store.RejectAsync(scope, parent.Id, Ct).AsTask();
            await WaitUntilBlockedByAsync(remember);
            Assert.False(reject.IsCompleted);
            await RunAsync(
                remember,
                """
                INSERT INTO mnemosyne.reasonlets
                    (id, scope, name, intent, version, graph, content_hash, parent_id, status, source, created_at, embedding)
                SELECT $1, scope, name, intent, '1.1', graph, content_hash, id, 2, source, now(), embedding
                FROM mnemosyne.reasonlets WHERE scope = $2 AND id = $3
                """,
                childId,
                scope.Value,
                parent.Id.Value);
            await transaction.CommitAsync(Ct);

            var result = await reject;
            Assert.Equal(MnemosyneErrorCode.Conflict, result.Error.Code);
            Assert.Equal(HasVersions, result.Error.Message);
        }

        Assert.Equal(parent.Id.Value, await Entries.ScalarAsync<Guid>(
            fixture.DataSource, "SELECT parent_id FROM mnemosyne.reasonlets WHERE id = $1", childId));

        // The other order: a reject holds the parent FOR UPDATE and deletes it, and a remember of a version below it
        // waits for that lock, then finds no parent, as after an erase.
        var other = await RememberAsync(store, scope, "other", review: true);
        await using (var reject = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await using var transaction = await reject.BeginTransactionAsync(Ct);
            await RunAsync(reject, "SELECT id FROM mnemosyne.reasonlets WHERE scope = $1 AND id = $2 FOR UPDATE", scope.Value, other.Id.Value);

            var remember = store.RememberAsync(Entries.Entry(scope, Entries.Graph("other-child"), other.Id) with { ReviewRequired = true }, Ct).AsTask();
            await WaitUntilBlockedByAsync(reject);
            Assert.False(remember.IsCompleted);
            await RunAsync(reject, "DELETE FROM mnemosyne.reasonlets WHERE scope = $1 AND id = $2", scope.Value, other.Id.Value);
            await transaction.CommitAsync(Ct);

            var result = await remember;
            Assert.Equal(MnemosyneErrorCode.NotFound, result.Error.Code);
        }

        // Only the first parent and its child remain: the remember stored no version below the deleted Reasonlet.
        Assert.Equal(2L, await CountAsync("SELECT count(*) FROM mnemosyne.reasonlets WHERE scope = $1", scope.Value));
    }

    [Fact]
    public async Task RejectRacingApproveNeverDeletesAnApprovedReasonlet()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();

        // First a forced interleaving: an approve has updated the row and not committed, and the reject waits for it.
        var held = await RememberAsync(store, scope, "held", review: true);
        await using (var approver = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await using var transaction = await approver.BeginTransactionAsync(Ct);
            await RunAsync(approver, "UPDATE mnemosyne.reasonlets SET status = 1 WHERE scope = $1 AND id = $2", scope.Value, held.Id.Value);

            var reject = store.RejectAsync(scope, held.Id, Ct).AsTask();
            await WaitUntilBlockedByAsync(approver);
            Assert.False(reject.IsCompleted);
            await transaction.CommitAsync(Ct);

            var result = await reject;
            Assert.Equal((MnemosyneErrorCode.Conflict, NotPending), (result.Error.Code, result.Error.Message));
        }

        Assert.Equal(ReasonletStatus.Active, (await store.GetAsync(scope, held.Id, Ct)).Value.Status);

        // Then the other order: a reject holds the row FOR UPDATE and deletes it, and the approve waits for it.
        var doomed = await RememberAsync(store, scope, "doomed", review: true);
        await using (var rejecter = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await using var transaction = await rejecter.BeginTransactionAsync(Ct);
            await RunAsync(rejecter, "SELECT id FROM mnemosyne.reasonlets WHERE scope = $1 AND id = $2 FOR UPDATE", scope.Value, doomed.Id.Value);
            await RunAsync(rejecter, "DELETE FROM mnemosyne.reasonlets WHERE scope = $1 AND id = $2", scope.Value, doomed.Id.Value);

            var approve = store.ApproveAsync(scope, doomed.Id, Ct).AsTask();
            await WaitUntilBlockedByAsync(rejecter);
            Assert.False(approve.IsCompleted);
            await transaction.CommitAsync(Ct);

            var result = await approve;
            Assert.Equal((MnemosyneErrorCode.NotFound, NotFound), (result.Error.Code, result.Error.Message));
        }

        Assert.Equal(MnemosyneErrorCode.NotFound, (await store.GetAsync(scope, doomed.Id, Ct)).Error.Code);

        // Then free-running races as a smoke test, each ending in one of the two serialized outcomes.
        for (var i = 0; i < 20; i++)
        {
            var pending = await RememberAsync(store, scope, $"race-{i}", review: true);

            var approve = Task.Run(async () => await store.ApproveAsync(scope, pending.Id, Ct), Ct);
            var reject = Task.Run(async () => await store.RejectAsync(scope, pending.Id, Ct), Ct);
            var (approved, rejected) = (await approve, await reject);

            var after = await store.GetAsync(scope, pending.Id, Ct);
            if (approved.IsSuccess)
            {
                Assert.Equal((MnemosyneErrorCode.Conflict, NotPending), (rejected.Error.Code, rejected.Error.Message));
                Assert.Equal(ReasonletStatus.Active, after.Value.Status);
            }
            else
            {
                Assert.True(rejected.IsSuccess, rejected.IsFailure ? rejected.Error.ToString() : null);
                Assert.Equal((MnemosyneErrorCode.NotFound, NotFound), (approved.Error.Code, approved.Error.Message));
                Assert.Equal(MnemosyneErrorCode.NotFound, after.Error.Code);
            }
        }
    }

    [Fact]
    public async Task CancellationThrows()
    {
        var store = await MigratedStoreAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var scope = PgVectorFixture.NewScope();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.ApproveAsync(scope, ReasonletId.New(), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.RejectAsync(scope, ReasonletId.New(), cancelled.Token));
    }

    [Fact]
    public async Task ListReturnsOnlyPendingInIdOrder()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        await RememberAsync(store, scope, "active");
        var first = await RememberAsync(store, scope, "p1", review: true);
        var second = await RememberAsync(store, scope, "p2", review: true);
        await RememberAsync(store, PgVectorFixture.NewScope(), "other", review: true);

        var listed = (await store.ListPendingAsync(scope, 10, null, Ct)).Value;

        Assert.Equal(new[] { first.Id, second.Id }.OrderBy(i => i.Value), listed.Select(r => r.Definition.Id));
        Assert.All(listed, r => Assert.Equal(ReasonletStatus.PendingReview, r.Definition.Status));
        Assert.Equal(scope, listed[0].Definition.Scope);
    }

    [Fact]
    public async Task ListPagesWithAfter()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var ids = new List<ReasonletId>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add((await RememberAsync(store, scope, $"p{i}", review: true)).Id);
        }

        var sorted = ids.OrderBy(i => i.Value).ToList();
        var page1 = (await store.ListPendingAsync(scope, 2, null, Ct)).Value;
        var page2 = (await store.ListPendingAsync(scope, 2, page1[^1].Definition.Id, Ct)).Value;
        var page3 = (await store.ListPendingAsync(scope, 2, page2[^1].Definition.Id, Ct)).Value;
        var page4 = (await store.ListPendingAsync(scope, 2, page3[^1].Definition.Id, Ct)).Value;

        Assert.Equal(sorted.Take(2), page1.Select(r => r.Definition.Id));
        Assert.Equal(sorted.Skip(2).Take(2), page2.Select(r => r.Definition.Id));
        Assert.Equal(sorted.Skip(4), page3.Select(r => r.Definition.Id));
        Assert.Empty(page4);
    }

    [Fact]
    public async Task ListValidatesLimitAndAfter()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();

        foreach (var limit in new[] { 0, 101 })
        {
            var result = await store.ListPendingAsync(scope, limit, null, Ct);
            Assert.Equal((MnemosyneErrorCode.Validation, "limit: must be from 1 to 100"), (result.Error.Code, result.Error.Message));
        }

        var after = await store.ListPendingAsync(scope, 10, default(ReasonletId), Ct);
        Assert.Equal(MnemosyneErrorCode.Validation, after.Error.Code);
        Assert.StartsWith("after:", after.Error.Message, StringComparison.Ordinal);
        Assert.Equal(MnemosyneErrorCode.Validation, (await store.ListPendingAsync(default, 10, null, Ct)).Error.Code);
        Assert.True((await store.ListPendingAsync(scope, 100, null, Ct)).IsSuccess);
    }

    [Fact]
    public async Task ListedMetricsCountRunsOnThePendingReasonlet()
    {
        var store = await MigratedStoreAsync();
        var scope = PgVectorFixture.NewScope();
        var busy = await RememberAsync(store, scope, "busy", review: true);
        var fresh = await RememberAsync(store, scope, "fresh", review: true);
        var bare = await RememberAsync(store, scope, "bare", review: true);
        var outcome = new ReasonletOutcome(true, TimeSpan.FromMilliseconds(5), null);
        Assert.True((await store.AppendExecutionAsync(scope, busy.Id, outcome, Entries.Provenance(), new ReasonletEvaluation(true, 0.9, "judge"), Ct)).IsSuccess);
        Assert.True((await store.AppendExecutionAsync(scope, busy.Id, outcome, Entries.Provenance(), null, Ct)).IsSuccess);
        await Entries.ExecuteAsync(fixture.DataSource, "DELETE FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1", bare.Id.Value);

        var listed = (await store.ListPendingAsync(scope, 10, null, Ct)).Value.ToDictionary(r => r.Definition.Id);

        var busyMetrics = listed[busy.Id].Metrics;
        Assert.Equal((3, 3, 1, 1), (busyMetrics.Runs, busyMetrics.Successes, busyMetrics.JudgedRuns, busyMetrics.JudgedSuccesses));
        Assert.NotNull(busyMetrics.LastSuccessAt);
        Assert.Equal(1, listed[fresh.Id].Metrics.Runs);
        var none = listed[bare.Id];
        Assert.Equal((0, 0, 0, 0), (none.Metrics.Runs, none.Metrics.Successes, none.Metrics.JudgedRuns, none.Metrics.JudgedSuccesses));
        Assert.Null(none.Metrics.LastSuccessAt);
        Assert.Equal(none.Definition.CreatedAt, none.Metrics.LastExecutedAt);
    }

    private async Task<ReasonletPostgreSqlStore> MigratedStoreAsync()
    {
        var store = fixture.CreateStore();
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        return store;
    }

    private static async Task<ReasonletReceipt> RememberAsync(
        ReasonletPostgreSqlStore store, ReasonletScope scope, string variant, ReasonletId? parent = null, bool review = false)
    {
        var result = await store.RememberAsync(Entries.Entry(scope, Entries.Graph(variant), parent) with { ReviewRequired = review }, Ct);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }

    /// <summary>The Reasonlet's executions, feedback and metrics rows, counted by its id alone.</summary>
    private async Task<(long Executions, long Feedback, long Metrics)> RowsOfAsync(ReasonletId id) => (
        await CountAsync("SELECT count(*) FROM mnemosyne.reasonlet_executions WHERE reasonlet_id = $1", id.Value),
        await CountAsync(
            "SELECT count(*) FROM mnemosyne.reasonlet_feedback f JOIN mnemosyne.reasonlet_executions e ON e.id = f.execution_id WHERE e.reasonlet_id = $1",
            id.Value),
        await CountAsync("SELECT count(*) FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1", id.Value));

    private Task<long> CountAsync(string sql, params object[] parameters) => Entries.ScalarAsync<long>(fixture.DataSource, sql, parameters);

    private static async Task RunAsync(NpgsqlConnection connection, string sql, params object[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Waits until some backend waits for a lock that <paramref name="holder"/>'s backend holds.</summary>
    private async Task WaitUntilBlockedByAsync(NpgsqlConnection holder)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var waiting = await CountAsync(
                "SELECT count(*) FROM pg_stat_activity WHERE $1 = ANY(pg_blocking_pids(pid))", holder.ProcessID);
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(25, Ct);
        }

        Assert.Fail("Nothing ever waited for the held lock.");
    }
}
