using System.Data.Async;
using ZeroAlloc.ORM;

namespace Mnemosyne.NET.PostgreSql.Repositories;

/// <summary>Inserts rows of <c>mnemosyne.reasonlet_executions</c>.</summary>
[Dialect(SqlDialect.PostgreSql)]
internal sealed partial class ExecutionRepository(IAsyncDbConnection connection)
{
    [Command("""
        INSERT INTO mnemosyne.reasonlet_executions
            (id, reasonlet_id, scope, executed_at, success, duration_ticks, score, source, run_id, subject_id, tags)
        VALUES
            (@id, @reasonletId, @scope, now(), @success, @durationTicks, @score, @source, @runId, @subjectId, @tags)
        """)]
    public partial Task InsertAsync(
        Guid id,
        Guid reasonletId,
        string scope,
        bool success,
        long durationTicks,
        double? score,
        string source,
        string? runId,
        string? subjectId,
        [Json] string? tags,
        IAsyncDbTransaction transaction,
        CancellationToken ct);
}
