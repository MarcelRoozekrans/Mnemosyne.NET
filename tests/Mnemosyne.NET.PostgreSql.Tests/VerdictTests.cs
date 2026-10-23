using System.Data.Async;
using System.Data.Async.Adapters;
using System.Diagnostics;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql.Repositories;
using Npgsql;
using Xunit;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.PostgreSql.Tests;

/// <summary>
/// Which verdict counts for a run, how feedback moves the success counters, and how the feedback path's locks meet
/// every other writer's.
/// </summary>
public sealed class VerdictTests(PgVectorFixture fixture) : IClassFixture<PgVectorFixture>
{
    private const string Owner = "owner";
    private const string Visitor = "visitor";

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<bool, bool?, string> Precedence()
    {
        var data = new TheoryData<bool, bool?, string>();
        foreach (var agent in new[] { true, false })
        {
            foreach (var evaluated in new bool?[] { null, true, false })
            {
                foreach (var feedback in new[] { "none", "null", "true", "false" })
                {
                    data.Add(agent, evaluated, feedback);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Precedence))]
    public async Task FeedbackBeatsTheEvaluatorAndTheEvaluatorBeatsTheAgent(bool agent, bool? evaluated, string feedback)
    {
        var (store, scope, r) = await RememberedAsync();
        var evaluation = evaluated is { } judged ? new ReasonletEvaluation(judged, 0.5, "judge") : (ReasonletEvaluation?)null;
        var run = await AppendAsync(store, scope, r.Id, agent, evaluation);
        bool? worked = feedback switch { "true" => true, "false" => false, _ => null };
        if (feedback != "none")
        {
            Assert.True((await store.AppendFeedbackAsync(scope, run, new ReasonletFeedback(0.5, 0.5, 0.5, worked), Ct)).IsSuccess);
        }

        var effective = worked ?? evaluated ?? agent;
        Assert.Equal(
            $"{Pg(agent)}|{Pg(evaluated)}|{Pg(worked)}|{Pg(effective)}",
            await Entries.ScalarAsync<string>(
                fixture.DataSource,
                "SELECT concat_ws('|', agent_success::text, coalesce(evaluated_success::text, '-'), coalesce(feedback_worked::text, '-'), success::text) "
                + "FROM mnemosyne.reasonlet_executions WHERE id = $1",
                run.Value));
        Assert.Equal((2, effective ? 2 : 1), await CountersAsync(r.Id));
        await AssertMetricsMatchTheExecutionsAsync(scope);
    }

    [Fact]
    public async Task FeedbackWithoutAVerdictKeepsTheEarlierOneAndReplacesTheScores()
    {
        var (store, scope, r) = await RememberedAsync();
        var run = await AppendAsync(store, scope, r.Id, agent: true, new ReasonletEvaluation(true, 0.9, "judge"));

        Assert.True((await store.AppendFeedbackAsync(scope, run, new ReasonletFeedback(0.1, 0.1, 0.1, false), Ct)).IsSuccess);
        Assert.True((await store.AppendFeedbackAsync(scope, run, new ReasonletFeedback(0.7, 0.8, 0.9), Ct)).IsSuccess);

        Assert.Equal("false|f", await VerdictAsync(run));
        Assert.Equal("0.7|0.8|0.9", await ScoresAsync(run));
        Assert.Equal((2, 1), await CountersAsync(r.Id));
        await AssertMetricsMatchTheExecutionsAsync(scope);
    }

    [Fact]
    public async Task FeedbackWithoutAVerdictOnAnUnjudgedRunLeavesTheVerdictAlone()
    {
        var (store, scope, r) = await RememberedAsync();
        var run = await AppendAsync(store, scope, r.Id, agent: false, null);

        Assert.True((await store.AppendFeedbackAsync(scope, run, new ReasonletFeedback(0.5, 0.5, 0.5), Ct)).IsSuccess);

        Assert.Equal("-|f", await VerdictAsync(run));
        Assert.Equal((2, 1), await CountersAsync(r.Id));
    }

    [Fact]
    public async Task AVerdictCanBeChangedBackAndForthAndEachFlipMovesTheCounterByOne()
    {
        var (store, scope, r) = await RememberedAsync();
        var run = await AppendAsync(store, scope, r.Id, agent: false, null);

        foreach (var (worked, successes) in new[] { (true, 2), (true, 2), (false, 1), (true, 2), (false, 1) })
        {
            Assert.True((await store.AppendFeedbackAsync(scope, run, new ReasonletFeedback(0.5, 0.5, 0.5, worked), Ct)).IsSuccess);
            Assert.Equal((2, successes), await CountersAsync(r.Id));
            await AssertMetricsMatchTheExecutionsAsync(scope);
        }
    }

    [Fact]
    public async Task RaisingALaterRunMovesLastSuccessAtForward()
    {
        var (store, scope, r) = await RememberedAsync();
        var later = await AppendAsync(store, scope, r.Id, agent: false, null);

        await FeedbackAsync(store, scope, later, true);

        Assert.Equal((2, 2), await CountersAsync(r.Id));
        Assert.Equal(await ExecutedAtAsync(later), await LastSuccessAtAsync(r.Id));
    }

    [Fact]
    public async Task RaisingAnEarlierRunKeepsTheLaterLastSuccessAt()
    {
        var (store, scope, r) = await RememberedAsync(new ReasonletOutcome(false, TimeSpan.Zero, null));
        var later = await AppendAsync(store, scope, r.Id, agent: true, null);

        await FeedbackAsync(store, scope, r.Execution, true);

        Assert.Equal((2, 2), await CountersAsync(r.Id));
        Assert.Equal(await ExecutedAtAsync(later), await LastSuccessAtAsync(r.Id));
    }

    [Fact]
    public async Task LosingASuccessThatDoesNotHoldLastSuccessAtKeepsIt()
    {
        var (store, scope, r) = await RememberedAsync();
        var later = await AppendAsync(store, scope, r.Id, agent: true, null);

        await FeedbackAsync(store, scope, r.Execution, false);

        Assert.Equal((2, 1), await CountersAsync(r.Id));
        Assert.Equal(await ExecutedAtAsync(later), await LastSuccessAtAsync(r.Id));
    }

    [Fact]
    public async Task LosingTheSuccessThatHoldsLastSuccessAtRecomputesItFromTheRemainingSuccesses()
    {
        var (store, scope, r) = await RememberedAsync();
        var later = await AppendAsync(store, scope, r.Id, agent: true, null);
        await AppendAsync(store, scope, r.Id, agent: false, null);

        await FeedbackAsync(store, scope, later, false);

        Assert.Equal((3, 1), await CountersAsync(r.Id));
        Assert.Equal(await ExecutedAtAsync(r.Execution), await LastSuccessAtAsync(r.Id));
        await AssertMetricsMatchTheExecutionsAsync(scope);
    }

    [Fact]
    public async Task LosingTheLastRemainingSuccessClearsLastSuccessAt()
    {
        var (store, scope, r) = await RememberedAsync();
        var later = await AppendAsync(store, scope, r.Id, agent: true, null);

        await FeedbackAsync(store, scope, later, false);
        await FeedbackAsync(store, scope, r.Execution, false);

        Assert.Equal((2, 0), await CountersAsync(r.Id));
        Assert.Null(await LastSuccessAtOrNullAsync(r.Id));
        await AssertMetricsMatchTheExecutionsAsync(scope);
    }

    [Fact]
    public async Task LosingTheLatestSuccessAfterAPruneKeepsThePrunedCountAndClearsLastSuccessAt()
    {
        // The remember's success is pruned and stays counted. Overriding the only success left removes the only time
        // the store still knows, so last_success_at is null while successes is above zero.
        var (store, scope, r) = await RememberedAsync();
        var later = await AppendAsync(store, scope, r.Id, agent: true, null);
        await BackdateAsync(r.Execution);
        Assert.Equal(1, (await store.PruneAsync(new ReasonletRetention { Scope = scope, ExecutionsOlderThan = TimeSpan.FromDays(1) }, Ct)).Value.Executions);

        await FeedbackAsync(store, scope, later, false);

        Assert.Equal((2, 1), await CountersAsync(r.Id));
        Assert.Null(await LastSuccessAtOrNullAsync(r.Id));
    }

    [Fact]
    public async Task ASubjectEraseSubtractsTheEffectiveSuccessOfTheRunsItRemoves()
    {
        // The visitor's run failed by the agent's flag but a human said it worked, and the judge failed a second run
        // the agent called a success. The erase subtracts what counted: one success and two runs.
        var (store, scope, r) = await RememberedAsync();
        var raised = await AppendAsync(store, scope, r.Id, agent: false, null, Visitor);
        await AppendAsync(store, scope, r.Id, agent: true, new ReasonletEvaluation(false, 0.1, "judge"), Visitor);
        await FeedbackAsync(store, scope, raised, true);
        Assert.Equal((3, 2), await CountersAsync(r.Id));

        var erased = await store.EraseAsync(new ReasonletErasure(scope, Visitor), Ct);

        Assert.Equal(2, erased.Value.Executions);
        Assert.Equal((1, 1), await CountersAsync(r.Id));
        await AssertMetricsMatchTheExecutionsAsync(scope);
    }

    [Fact]
    public async Task APruneKeepsTheCountersOfOverriddenRuns()
    {
        var (store, scope, r) = await RememberedAsync();
        var lowered = await AppendAsync(store, scope, r.Id, agent: true, null);
        await FeedbackAsync(store, scope, lowered, false);
        var before = await MetricsRowAsync(r.Id);
        await BackdateAsync(r.Execution, lowered);

        var pruned = await store.PruneAsync(new ReasonletRetention { Scope = scope, ExecutionsOlderThan = TimeSpan.FromDays(1) }, Ct);

        Assert.Equal(2, pruned.Value.Executions);
        Assert.Equal(before, await MetricsRowAsync(r.Id));
        Assert.Equal((2, 1), await CountersAsync(r.Id));
    }

    [Fact]
    public async Task AHandWrittenUpdateThatBreaksTheVerdictRuleIsRefused()
    {
        var (_, _, r) = await RememberedAsync();

        var effective = await Assert.ThrowsAsync<PostgresException>(() => Entries.ExecuteAsync(
            fixture.DataSource, "UPDATE mnemosyne.reasonlet_executions SET success = NOT success WHERE id = $1", r.Execution.Value));
        var verdict = await Assert.ThrowsAsync<PostgresException>(() => Entries.ExecuteAsync(
            fixture.DataSource, "UPDATE mnemosyne.reasonlet_executions SET feedback_worked = NOT success WHERE id = $1", r.Execution.Value));
        var partial = await Assert.ThrowsAsync<PostgresException>(() => Entries.ExecuteAsync(
            fixture.DataSource, "UPDATE mnemosyne.reasonlet_executions SET evaluated_success = success, evaluator = 'judge' WHERE id = $1", r.Execution.Value));
        var longName = await Assert.ThrowsAsync<PostgresException>(() => Entries.ExecuteAsync(
            fixture.DataSource,
            "UPDATE mnemosyne.reasonlet_executions SET evaluated_success = success, evaluation_score = 1, evaluator = repeat('e', 101) WHERE id = $1",
            r.Execution.Value));

        Assert.Equal("reasonlet_executions_effective_success", effective.ConstraintName);
        Assert.Equal("reasonlet_executions_effective_success", verdict.ConstraintName);
        Assert.Equal("reasonlet_executions_evaluation_complete", partial.ConstraintName);
        Assert.Equal("reasonlet_executions_evaluator_check", longName.ConstraintName);
        Assert.All(new[] { effective, verdict, partial, longName }, e => Assert.Equal(PostgresErrorCodes.CheckViolation, e.SqlState));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AMixedSequenceKeepsTheCountersEqualToAModelThatIncludesPrunedHistory(int seed)
    {
        // Review focus 1. A seeded random sequence of appends with and without a judge, feedback with and without a
        // verdict, prunes of single runs and subject erases, checked after every step against a model of the
        // counters that keeps pruned runs counted.
        var random = new Random(seed);
        var (store, scope, r) = await RememberedAsync();
        var createdAt = await Entries.TimestampAsync(fixture.DataSource, "SELECT created_at FROM mnemosyne.reasonlets WHERE id = $1", r.Id.Value);
        var model = new CounterModel(createdAt);
        model.Append(new ModelRun(r.Execution, await ExecutedAtAsync(r.Execution), Agent: true, Evaluated: null, Owner));
        await AssertModelAsync(model, r.Id);

        for (var step = 0; step < 40; step++)
        {
            var roll = random.Next(100);
            if (roll < 40 || model.Runs.Count == 0)
            {
                var agent = random.Next(2) == 0;
                bool? evaluated = random.Next(3) switch { 0 => null, 1 => true, _ => false };
                var subject = random.Next(3) == 0 ? Visitor : Owner;
                var evaluation = evaluated is { } judged ? new ReasonletEvaluation(judged, 0.5, "judge") : (ReasonletEvaluation?)null;
                var run = await AppendAsync(store, scope, r.Id, agent, evaluation, subject);
                model.Append(new ModelRun(run, await ExecutedAtAsync(run), agent, evaluated, subject));
            }
            else if (roll < 75)
            {
                var run = model.Runs[random.Next(model.Runs.Count)];
                bool? worked = random.Next(3) switch { 0 => null, 1 => true, _ => false };
                Assert.True((await store.AppendFeedbackAsync(scope, run.Id, new ReasonletFeedback(0.5, 0.5, 0.5, worked), Ct)).IsSuccess);
                model.Feedback(run, worked);
            }
            else if (roll < 88)
            {
                var run = model.Runs[random.Next(model.Runs.Count)];
                await BackdateAsync(run.Id);
                var pruned = await store.PruneAsync(new ReasonletRetention { Scope = scope, ExecutionsOlderThan = TimeSpan.FromDays(1) }, Ct);
                Assert.Equal(1, pruned.Value.Executions);
                model.Prune(run);
            }
            else
            {
                var erased = await store.EraseAsync(new ReasonletErasure(scope, Visitor), Ct);
                Assert.Equal(model.EraseVisitor(), erased.Value.Executions);
            }

            await AssertModelAsync(model, r.Id);
        }
    }

    // Review focus 2: feedback racing the other writers. Each test holds one writer's locks, starts the feedback,
    // and checks where it waits, so a feedback path that took its locks in another order would wait elsewhere,
    // deadlock, or come out with a different answer.

    [Fact]
    public async Task FeedbackHoldsItsExecutionWhileItWaitsForAnAppendsMetricsRow()
    {
        var (store, scope, r) = await RememberedAsync();
        var run = await AppendAsync(store, scope, r.Id, agent: false, null);
        await using var append = await HeldAppendAsync(scope, r.Id);

        var feedback = FeedbackTask(store, scope, run, true);
        await WaitForWaiterAsync(append.Pid, "reasonlet_metrics");
        Assert.False(feedback.IsCompleted);
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, await TryLockExecutionAsync(run));

        await append.CommitAsync();

        Assert.True((await feedback.WaitAsync(WaitLimit, Ct)).IsSuccess);
        Assert.Equal((3, 3), await CountersAsync(r.Id));
        await AssertMetricsMatchTheExecutionsAsync(scope);
    }

    [Fact]
    public async Task FeedbackAndASubjectEraseOfItsRunBothCompleteWhenTheFeedbackGetsTheRunFirst()
    {
        // The feedback holds the visitor's run and waits for the metrics row an append holds. The erase then waits
        // for the run, holding no metrics row, so once the append commits the feedback finishes and the erase
        // subtracts the run with its new verdict.
        var (store, scope, r) = await RememberedAsync();
        var run = await AppendAsync(store, scope, r.Id, agent: false, null, Visitor);
        await using var append = await HeldAppendAsync(scope, r.Id);

        var feedback = FeedbackTask(store, scope, run, true);
        await WaitForWaiterAsync(append.Pid, "reasonlet_metrics");
        var erase = store.EraseAsync(new ReasonletErasure(scope, Visitor), Ct).AsTask();
        await WaitForWaiterAsync(null, "reasonlet_executions");
        await append.CommitAsync();

        Assert.True((await feedback.WaitAsync(WaitLimit, Ct)).IsSuccess);
        Assert.Equal(1, (await erase.WaitAsync(WaitLimit, Ct)).Value.Executions);
        Assert.Equal((2, 2), await CountersAsync(r.Id));
        await AssertMetricsMatchTheExecutionsAsync(scope);
    }

    [Fact]
    public async Task ASubjectEraseHoldingTheRunTakesItsMetricsRowWhileTheFeedbackWaitsAndTheFeedbackIsNotFound()
    {
        // The erase is driven step by step: it locks the visitor's run, the feedback waits for it, and the erase must
        // then get the metrics row at once, because the feedback holds only the Reasonlet's key-share lock.
        var (store, scope, r) = await RememberedAsync();
        var run = await AppendAsync(store, scope, r.Id, agent: false, null, Visitor);
        await using var erase = await HeldTransactionAsync();
        var executions = new ExecutionRepository(erase.Connection);
        var doomed = await new ReasonletRepository(erase.Connection).LockForErasureAsync(scope.Value, Visitor, erase.Transaction, Ct);
        await executions.CreateErasedExecutionsAsync(erase.Transaction, Ct);
        await executions.LockExecutionsForErasureAsync(scope.Value, doomed, Visitor, erase.Transaction, Ct);

        var feedback = FeedbackTask(store, scope, run, true);
        await WaitForWaiterAsync(erase.Pid, "reasonlet_executions");
        var kept = await executions.LockMetricsForErasureAsync(scope.Value, doomed, erase.Transaction, Ct).WaitAsync(WaitLimit, Ct);
        Assert.Equal(1, await executions.DeleteForErasureAsync(scope.Value, kept, erase.Transaction, Ct));
        await new ReasonletRepository(erase.Connection).DeleteAsync(scope.Value, doomed, erase.Transaction, Ct);
        await erase.CommitAsync();

        Assert.Equal(MnemosyneErrorCode.NotFound, (await feedback.WaitAsync(WaitLimit, Ct)).Error.Code);
        Assert.Equal((1, 1), await CountersAsync(r.Id));
        await AssertMetricsMatchTheExecutionsAsync(scope);
    }

    [Fact]
    public async Task FeedbackWaitsForAScopeEraseAtTheReasonletHoldingNothingAndIsNotFound()
    {
        var (store, scope, r) = await RememberedAsync();
        await using var erase = await HeldTransactionAsync();
        var doomed = await new ReasonletRepository(erase.Connection).LockForErasureAsync(scope.Value, null, erase.Transaction, Ct);

        var feedback = FeedbackTask(store, scope, r.Execution, false);
        await WaitForWaiterAsync(erase.Pid, "reasonlets");
        Assert.Null(await TryLockExecutionAsync(r.Execution));
        Assert.Equal(1, await new ExecutionRepository(erase.Connection).DeleteScopeExecutionsAsync(scope.Value, doomed, erase.Transaction, Ct));
        Assert.Equal(1, await new ReasonletRepository(erase.Connection).DeleteAsync(scope.Value, doomed, erase.Transaction, Ct));
        await erase.CommitAsync();

        Assert.Equal(MnemosyneErrorCode.NotFound, (await feedback.WaitAsync(WaitLimit, Ct)).Error.Code);
    }

    [Fact]
    public async Task ARunPruneWaitsForFeedbackThatHoldsTheRunAndThePrunedRunStaysCounted()
    {
        // The feedback holds the old run and waits for the metrics row an append holds. A prune of old runs must wait
        // for the feedback rather than delete the run under it, so the feedback succeeds and its verdict stays
        // counted once the run is pruned.
        var (store, scope, r) = await RememberedAsync();
        var run = await AppendAsync(store, scope, r.Id, agent: false, null);
        await BackdateAsync(run);
        await using var append = await HeldAppendAsync(scope, r.Id);

        var feedback = FeedbackTask(store, scope, run, true);
        await WaitForWaiterAsync(append.Pid, "reasonlet_metrics");
        var prune = store.PruneAsync(new ReasonletRetention { Scope = scope, ExecutionsOlderThan = TimeSpan.FromDays(1) }, Ct).AsTask();
        await WaitForWaiterAsync(null, "reasonlet_executions");
        await append.CommitAsync();

        Assert.True((await feedback.WaitAsync(WaitLimit, Ct)).IsSuccess);
        Assert.Equal(1, (await prune.WaitAsync(WaitLimit, Ct)).Value.Executions);
        Assert.Equal((3, 3), await CountersAsync(r.Id));
    }

    [Fact]
    public async Task AReasonletPruneSkipsAReasonletThatWaitingFeedbackHoldsAndDoesNotWaitForIt()
    {
        var (store, scope, r) = await RememberedAsync();
        await BackdateScopeAsync(scope);
        await using var metrics = await HeldTransactionAsync();
        await RunAsync(metrics, $"SELECT 1 FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = '{r.Id.Value}' FOR UPDATE");

        var feedback = FeedbackTask(store, scope, r.Execution, false);
        await WaitForWaiterAsync(metrics.Pid, "reasonlet_metrics");
        var pruned = await store.PruneAsync(new ReasonletRetention { Scope = scope, ReasonletsUnusedFor = TimeSpan.FromDays(1) }, Ct).AsTask().WaitAsync(WaitLimit, Ct);
        await metrics.CommitAsync();

        Assert.Equal((0, 0), (pruned.Value.Reasonlets, pruned.Value.Executions));
        Assert.True((await feedback.WaitAsync(WaitLimit, Ct)).IsSuccess);
        Assert.Equal((1, 0), await CountersAsync(r.Id));
    }

    [Fact]
    public async Task FeedbackThatArrivesWhileAReasonletPruneHoldsTheReasonletWaitsThereAndIsNotFound()
    {
        var (store, scope, r) = await RememberedAsync();
        await BackdateScopeAsync(scope);
        Task<UnitResult<MnemosyneError>>? feedback = null;
        store.BeforePruneBatchCommit = async () =>
        {
            feedback ??= FeedbackTask(store, scope, r.Execution, false);
            await WaitForWaiterAsync(null, "reasonlets");
        };

        var pruned = await store.PruneAsync(new ReasonletRetention { Scope = scope, ReasonletsUnusedFor = TimeSpan.FromDays(1) }, Ct);

        Assert.Equal((1, 1), (pruned.Value.Reasonlets, pruned.Value.Executions));
        Assert.NotNull(feedback);
        Assert.Equal(MnemosyneErrorCode.NotFound, (await feedback.WaitAsync(WaitLimit, Ct)).Error.Code);
    }

    private static string Pg(bool? value) => value switch { true => "true", false => "false", null => "-" };

    private async Task AssertModelAsync(CounterModel model, ReasonletId id)
    {
        Assert.Equal((model.RunCount, model.Successes, model.LastSuccessAt, model.LastExecutedAt), await MetricsRowAsync(id));
        foreach (var run in model.Runs)
        {
            Assert.Equal(run.Effective, await Entries.ScalarAsync<bool>(fixture.DataSource, "SELECT success FROM mnemosyne.reasonlet_executions WHERE id = $1", run.Id.Value));
        }

        Assert.Equal(
            (long)model.Runs.Count,
            await Entries.ScalarAsync<long>(fixture.DataSource, "SELECT count(*) FROM mnemosyne.reasonlet_executions WHERE reasonlet_id = $1", id.Value));
    }

    private static Task<UnitResult<MnemosyneError>> FeedbackTask(ReasonletPostgreSqlStore store, ReasonletScope scope, ExecutionId run, bool worked) =>
        store.AppendFeedbackAsync(scope, run, new ReasonletFeedback(0.5, 0.5, 0.5, worked), Ct).AsTask();

    private static async Task FeedbackAsync(ReasonletPostgreSqlStore store, ReasonletScope scope, ExecutionId run, bool worked) =>
        Assert.True((await store.AppendFeedbackAsync(scope, run, new ReasonletFeedback(0.5, 0.5, 0.5, worked), Ct)).IsSuccess);

    /// <summary>
    /// Tries to lock the run <c>FOR UPDATE NOWAIT</c> from a connection of its own, and releases it at once. Answers
    /// null when the lock was free, or the SQLSTATE that refused it.
    /// </summary>
    private async Task<string?> TryLockExecutionAsync(ExecutionId run)
    {
        try
        {
            await Entries.ExecuteAsync(fixture.DataSource, "SELECT 1 FROM mnemosyne.reasonlet_executions WHERE id = $1 FOR UPDATE NOWAIT", run.Value);
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }

    /// <summary>
    /// Waits, for at most ten seconds, until a backend waits for a row lock on <paramref name="table"/>: it holds the
    /// tuple lock PostgreSQL takes on the row while it waits for the row's holder. When <paramref name="holderPid"/>
    /// is given, the waiter must also be blocked by that backend.
    /// </summary>
    private async Task WaitForWaiterAsync(int? holderPid, string table)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < WaitLimit)
        {
            if (await Entries.ScalarAsync<bool>(
                fixture.DataSource,
                """
                SELECT EXISTS (
                    SELECT 1 FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid
                    WHERE l.locktype = 'tuple' AND l.relation = ('mnemosyne.' || $2)::regclass
                      AND a.wait_event_type = 'Lock'
                      AND ($1 < 0 OR $1 = ANY(pg_blocking_pids(a.pid))))
                """,
                holderPid ?? -1,
                table))
            {
                return;
            }

            await Task.Delay(25, Ct);
        }

        Assert.Fail($"No backend waited for a row of {table} within ten seconds.");
    }

    /// <summary>Opens a transaction that stays open until it is committed or disposed.</summary>
    private async Task<HeldTransaction> HeldTransactionAsync()
    {
        var connection = (await fixture.DataSource.OpenConnectionAsync(Ct)).AsAsync();
        var transaction = await connection.BeginTransactionAsync(Ct);
        var held = new HeldTransaction(connection, transaction);
        held.Pid = await PidAsync(held);
        return held;
    }

    /// <summary>Opens a transaction that appends a success to the Reasonlet with the store's own statement, and holds it.</summary>
    private async Task<HeldTransaction> HeldAppendAsync(ReasonletScope scope, ReasonletId id)
    {
        var held = await HeldTransactionAsync();
        Assert.Equal(1, await new ExecutionRepository(held.Connection).InsertAsync(
            Guid.CreateVersion7(), id.Value, scope.Value, true, null, null, null, 0, null, "test", null, Owner, null, held.Transaction, Ct));
        return held;
    }

    private static async Task<int> PidAsync(HeldTransaction held)
    {
        await using var command = held.Connection.CreateCommand();
        command.Transaction = held.Transaction;
        command.CommandText = "SELECT pg_backend_pid()";
        return (int)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task RunAsync(HeldTransaction held, string sql)
    {
        await using var command = held.Connection.CreateCommand();
        command.Transaction = held.Transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<(ReasonletPostgreSqlStore Store, ReasonletScope Scope, ReasonletReceipt Receipt)> RememberedAsync(ReasonletOutcome? outcome = null)
    {
        var store = fixture.CreateStore();
        Assert.True((await store.MigrateAsync(Ct)).IsSuccess);
        var scope = PgVectorFixture.NewScope();
        var provenance = new ReasonletProvenance { Source = "test", SubjectId = Owner };
        var receipt = (await store.RememberAsync(
            Entries.Entry(scope, provenance: provenance, outcome: outcome ?? new ReasonletOutcome(true, TimeSpan.Zero, null)), Ct)).Value;
        return (store, scope, receipt);
    }

    private static async Task<ExecutionId> AppendAsync(
        ReasonletPostgreSqlStore store, ReasonletScope scope, ReasonletId id, bool agent, ReasonletEvaluation? evaluation, string subject = Owner)
    {
        var result = await store.AppendExecutionAsync(
            scope, id, new ReasonletOutcome(agent, TimeSpan.Zero, null), new ReasonletProvenance { Source = "test", SubjectId = subject }, evaluation, Ct);
        Assert.True(result.IsSuccess);
        return result.Value.Execution;
    }

    private Task BackdateAsync(params ExecutionId[] runs) => Entries.ExecuteAsync(
        fixture.DataSource,
        "UPDATE mnemosyne.reasonlet_executions SET executed_at = now() - interval '10 days' WHERE id = ANY($1)",
        runs.Select(e => e.Value).ToArray());

    private async Task BackdateScopeAsync(ReasonletScope scope)
    {
        await Entries.ExecuteAsync(
            fixture.DataSource, "UPDATE mnemosyne.reasonlets SET created_at = now() - interval '10 days' WHERE scope = $1", scope.Value);
        await Entries.ExecuteAsync(
            fixture.DataSource,
            "UPDATE mnemosyne.reasonlet_metrics SET last_executed_at = now() - interval '10 days', last_success_at = now() - interval '10 days' WHERE scope = $1",
            scope.Value);
        await Entries.ExecuteAsync(
            fixture.DataSource, "UPDATE mnemosyne.reasonlet_executions SET executed_at = now() - interval '10 days' WHERE scope = $1", scope.Value);
    }

    private async Task<(int Runs, int Successes)> CountersAsync(ReasonletId id) => (
        await Entries.ScalarAsync<int>(fixture.DataSource, "SELECT runs FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1", id.Value),
        await Entries.ScalarAsync<int>(fixture.DataSource, "SELECT successes FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1", id.Value));

    private async Task<(int Runs, int Successes, DateTimeOffset? LastSuccessAt, DateTimeOffset LastExecutedAt)> MetricsRowAsync(ReasonletId id)
    {
        var (runs, successes) = await CountersAsync(id);
        return (
            runs,
            successes,
            await LastSuccessAtOrNullAsync(id),
            await Entries.TimestampAsync(fixture.DataSource, "SELECT last_executed_at FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1", id.Value));
    }

    private Task<DateTimeOffset> ExecutedAtAsync(ExecutionId id) => Entries.TimestampAsync(
        fixture.DataSource, "SELECT executed_at FROM mnemosyne.reasonlet_executions WHERE id = $1", id.Value);

    private Task<DateTimeOffset> LastSuccessAtAsync(ReasonletId id) => Entries.TimestampAsync(
        fixture.DataSource, "SELECT last_success_at FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1", id.Value);

    private async Task<DateTimeOffset?> LastSuccessAtOrNullAsync(ReasonletId id) =>
        await Entries.ScalarAsync<bool>(fixture.DataSource, "SELECT last_success_at IS NULL FROM mnemosyne.reasonlet_metrics WHERE reasonlet_id = $1", id.Value)
            ? null
            : await LastSuccessAtAsync(id);

    private Task<string> VerdictAsync(ExecutionId run) => Entries.ScalarAsync<string>(
        fixture.DataSource,
        "SELECT concat_ws('|', coalesce(feedback_worked::text, '-'), success) FROM mnemosyne.reasonlet_executions WHERE id = $1",
        run.Value);

    private Task<string> ScoresAsync(ExecutionId run) => Entries.ScalarAsync<string>(
        fixture.DataSource,
        "SELECT concat_ws('|', utility, correctness, reusability) FROM mnemosyne.reasonlet_feedback WHERE execution_id = $1",
        run.Value);

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
              AND (m.reasonlet_id IS NULL OR (m.runs, m.successes) <> (a.runs, a.successes)
                   OR m.last_success_at IS DISTINCT FROM a.last_success_at
                   OR m.last_executed_at IS DISTINCT FROM a.last_executed_at)
            """,
            scope.Value));

    /// <summary>A connection and its open transaction, held across awaits, which commits on request and rolls back on disposal.</summary>
    private sealed class HeldTransaction(IAsyncDbConnection connection, IAsyncDbTransaction transaction) : IAsyncDisposable
    {
        public IAsyncDbConnection Connection { get; } = connection;

        public IAsyncDbTransaction Transaction { get; } = transaction;

        public int Pid { get; set; }

        public async Task CommitAsync() => await Transaction.CommitAsync(Ct);

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed record ModelRun(ExecutionId Id, DateTimeOffset ExecutedAt, bool Agent, bool? Evaluated, string Subject)
    {
        public bool? Worked { get; set; }

        public bool Effective => Worked ?? Evaluated ?? Agent;
    }

    /// <summary>
    /// The counters the store must keep for one Reasonlet, computed from the spec's rules rather than from the
    /// executions left, so pruned runs stay counted.
    /// </summary>
    private sealed class CounterModel(DateTimeOffset createdAt)
    {
        public List<ModelRun> Runs { get; } = [];

        public int RunCount { get; private set; }

        public int Successes { get; private set; }

        public DateTimeOffset? LastSuccessAt { get; private set; }

        private readonly DateTimeOffset _createdAt = createdAt;

        public DateTimeOffset LastExecutedAt { get; private set; } = createdAt;

        public void Append(ModelRun run)
        {
            Runs.Add(run);
            RunCount++;
            LastExecutedAt = Max(LastExecutedAt, run.ExecutedAt)!.Value;
            if (run.Effective)
            {
                Successes++;
                LastSuccessAt = Max(LastSuccessAt, run.ExecutedAt);
            }
        }

        public void Feedback(ModelRun run, bool? worked)
        {
            var before = run.Effective;
            run.Worked = worked ?? run.Worked;
            if (run.Effective == before)
            {
                return;
            }

            if (run.Effective)
            {
                Successes++;
                LastSuccessAt = Max(LastSuccessAt, run.ExecutedAt);
            }
            else
            {
                Successes--;
                if (run.ExecutedAt >= LastSuccessAt)
                {
                    LastSuccessAt = LatestSuccess();
                }
            }
        }

        /// <summary>A pruned run leaves the records and stays in the counters. It was backdated before the prune.</summary>
        public void Prune(ModelRun run) => Runs.Remove(run);

        public int EraseVisitor()
        {
            var removed = Runs.Where(r => r.Subject == Visitor).ToList();
            Runs.RemoveAll(r => r.Subject == Visitor);
            if (removed.Count == 0)
            {
                return 0;
            }

            RunCount -= removed.Count;
            Successes -= removed.Count(r => r.Effective);
            if (removed.Max(r => r.ExecutedAt) >= LastExecutedAt)
            {
                LastExecutedAt = Runs.Count == 0 ? _createdAt : Runs.Max(r => r.ExecutedAt);
            }

            if (removed.Where(r => r.Effective).Select(r => (DateTimeOffset?)r.ExecutedAt).Max() is { } latest && latest >= LastSuccessAt)
            {
                LastSuccessAt = LatestSuccess();
            }

            return removed.Count;
        }

        private DateTimeOffset? LatestSuccess() => Runs.Where(r => r.Effective).Select(r => (DateTimeOffset?)r.ExecutedAt).Max();

        private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset b) => a is { } value && value > b ? value : b;
    }
}
