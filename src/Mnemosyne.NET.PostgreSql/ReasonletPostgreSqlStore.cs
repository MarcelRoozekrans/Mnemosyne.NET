using System.Data;
using System.Data.Async;
using System.Data.Async.Adapters;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql.Repositories;
using Mnemosyne.NET.PostgreSql.Rows;
using Npgsql;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.PostgreSql;

/// <summary>An <see cref="IReasonletStore"/> on PostgreSQL with pgvector.</summary>
/// <remarks>
/// The host calls <see cref="MigrateAsync"/> once at startup, before anything else. Every other member answers
/// <see cref="MnemosyneErrorCode.Unavailable"/> until the schema is migrated. The store opens one connection
/// per operation from the data source it is given.
/// </remarks>
public sealed class ReasonletPostgreSqlStore : IReasonletStore
{
    private const int MaxSearchLimit = 200;
    private const string MigrationResourcePrefix = "Mnemosyne.NET.PostgreSql.Migrations.";

    private readonly NpgsqlDataSource _dataSource;
    private readonly MnemosynePostgreSqlOptions _options;
    private volatile bool _migrated;

    /// <summary>Creates the store.</summary>
    /// <param name="dataSource">
    /// The data source to open connections from. Hosts that publish with native AOT should build it with
    /// <c>NpgsqlSlimDataSourceBuilder</c> and call <c>EnableArrays()</c>, because embeddings bind as arrays.
    /// </param>
    /// <param name="options">The store's settings. They are validated here.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public ReasonletPostgreSqlStore(NpgsqlDataSource dataSource, MnemosynePostgreSqlOptions options)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _dataSource = dataSource;
        _options = options;
    }

    /// <summary>Creates or updates the <c>mnemosyne</c> schema and applies the embedding dimension.</summary>
    /// <remarks>
    /// Calling it again is harmless. The first call fixes the dimension at
    /// <see cref="MnemosynePostgreSqlOptions.Dimensions"/>; a later call with a different dimension fails with
    /// <see cref="MnemosyneErrorCode.Validation"/> and changes nothing, because mixing embedding models would
    /// corrupt similarity. A database failure answers <see cref="MnemosyneErrorCode.Unavailable"/>.
    /// </remarks>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Success, or the error.</returns>
    public async ValueTask<UnitResult<MnemosyneError>> MigrateAsync(CancellationToken ct)
    {
        // Assigned rather than returned inside the try, so every line of the method can execute.
        UnitResult<MnemosyneError> result;
        try
        {
            result = await MigrateCoreAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (PostgresErrors.Map(ex) is { } error)
        {
            result = error;
        }

        return result;
    }

    private async Task<UnitResult<MnemosyneError>> MigrateCoreAsync(CancellationToken ct)
    {
        await using var connection = (await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false)).AsAsync();
        var source = new EmbeddedResourceMigrationSource(typeof(ReasonletPostgreSqlStore).Assembly, MigrationResourcePrefix);
        await new MigrationRunner(connection, source, new PostgresMigrationDialect()).RunAsync(ct).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var settings = new SettingsRepository(connection);
        await settings.LockAsync(transaction, ct).ConfigureAwait(false);
        var recorded = await settings.GetDimensionsAsync(transaction, ct).ConfigureAwait(false);
        if (recorded is { } dimensions)
        {
            if (dimensions != _options.Dimensions)
            {
                return DimensionMismatch(dimensions);
            }
        }
        else
        {
            await ApplyDimensionsAsync(connection, transaction, ct).ConfigureAwait(false);
            await settings.RecordDimensionsAsync(_options.Dimensions, transaction, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        _migrated = true;
        return UnitResult<MnemosyneError>.Success();
    }

    /// <inheritdoc />
    /// <remarks>
    /// One transaction at READ COMMITTED decides between a new Reasonlet, an execution only and a new version. Row
    /// locks on the parent and on the top of its lineage serialize concurrent children, so their versions never
    /// collide. A new version that loses a race with a concurrent erase of its lineage answers
    /// <see cref="MnemosyneErrorCode.Conflict"/>.
    /// The embedding must have <see cref="MnemosynePostgreSqlOptions.Dimensions"/> values, or the entry is a
    /// <see cref="MnemosyneErrorCode.Validation"/> failure.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The stored lineage is corrupt: the <c>parent_id</c> links above the parent form a cycle, which only
    /// hand-written SQL can create.
    /// </exception>
    public async ValueTask<Result<ReasonletReceipt, MnemosyneError>> RememberAsync(ReasonletStoreEntry entry, CancellationToken ct)
    {
        var migrated = await EnsureMigratedAsync(ct).ConfigureAwait(false);
        if (migrated.IsFailure)
        {
            return migrated.Error;
        }

        ArgumentNullException.ThrowIfNull(entry);
        var valid = entry.Validate();
        if (valid.IsFailure)
        {
            return valid.Error;
        }

        if (entry.Embedding.Length != _options.Dimensions)
        {
            return new MnemosyneError(
                MnemosyneErrorCode.Validation,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{nameof(entry.Embedding)}: must have {_options.Dimensions} values, the store's dimension, but has {entry.Embedding.Length}"));
        }

        return await GuardAsync(RememberCoreAsync(entry, ct)).ConfigureAwait(false);
    }

    private async Task<Result<ReasonletReceipt, MnemosyneError>> RememberCoreAsync(ReasonletStoreEntry entry, CancellationToken ct)
    {
        var capture = entry.Capture;

        // The hash is of the capture's graph and is never recomputed from stored text, so a graph read back unchanged matches.
        var hash = OrThrow(capture.Graph.ComputeContentHash(), "Hashing a validated graph");

        await using var connection = (await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false)).AsAsync();

        // Named rather than left to a default. The row locks serialize the writes, and each statement must then see
        // what the lock holder committed: at REPEATABLE READ a waiting child would miss its sibling's version.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        var reasonlets = new ReasonletRepository(connection);
        var executions = new ExecutionRepository(connection);

        ReasonletReceipt receipt;
        if (capture.ParentId is not { } parentId)
        {
            receipt = await InsertReasonletAsync(
                reasonlets, executions, entry, hash, LineageVersions.Initial, null, ReasonletCaptureKind.NewReasonlet, transaction, ct)
                .ConfigureAwait(false);
        }
        else
        {
            var parent = await reasonlets.LockParentAsync(capture.Scope.Value, parentId.Value, transaction, ct).ConfigureAwait(false);
            if (parent is null)
            {
                return new MnemosyneError(MnemosyneErrorCode.NotFound, "The parent Reasonlet does not exist in the scope.");
            }

            if (string.Equals(parent.ContentHash, hash, StringComparison.Ordinal))
            {
                var execution = await InsertExecutionAsync(executions, parent.Id, capture, transaction, ct).ConfigureAwait(false);
                receipt = new ReasonletReceipt(
                    new ReasonletId(parent.Id), parent.Version, execution, ReasonletCaptureKind.ExecutionOnly, (ReasonletStatus)parent.Status);
            }
            else
            {
                // The lineage is the top above the parent and the top's whole subtree, derived from parent_id within the
                // scope. Every lock is taken going up the tree: the parent FOR UPDATE, then each ancestor between the
                // parent and the top FOR KEY SHARE, bottom-up, then the top FOR UPDATE. When the parent is the top, the
                // top lock is a re-lock. A transaction only ever waits for a row above every row it holds, so
                // remembers cannot deadlock one another.
                var scope = capture.Scope.Value;

                // The walk starts at the parent this transaction holds, so the path is never empty.
                var path = await reasonlets.FindLineagePathAsync(scope, parent.Id, transaction, ct).ConfigureAwait(false);
                var top = path[^1];
                if (top.IsCycle)
                {
                    // The store never links a Reasonlet to a descendant, so a cycle is corrupt data: a bug, not a result.
                    throw new InvalidOperationException(
                        $"The parent_id links above Reasonlet {parent.Id} form a cycle through {top.Id}; the stored lineage is corrupt.");
                }

                // The path locks hold every link from the parent to the top until commit, because an erase must delete
                // an ancestor to cut a link. A row that vanished between the walk and its lock means a concurrent erase
                // already cut the path, so the walk's top and subtree are stale: a lost race with an erase.
                for (var i = 1; i < path.Count - 1; i++)
                {
                    if (await reasonlets.LockAncestorAsync(scope, path[i].Id, transaction, ct).ConfigureAwait(false) is null)
                    {
                        return LineageConflict();
                    }
                }

                if (await reasonlets.LockLineageTopAsync(scope, top.Id, transaction, ct).ConfigureAwait(false) is null)
                {
                    return LineageConflict();
                }

                var version = LineageVersions.Next(
                    await reasonlets.GetLineageVersionsAsync(scope, top.Id, transaction, ct).ConfigureAwait(false));
                receipt = await InsertReasonletAsync(
                    reasonlets, executions, entry, hash, version, parent.Id, ReasonletCaptureKind.NewVersion, transaction, ct)
                    .ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return receipt;
    }

    private static async Task<ReasonletReceipt> InsertReasonletAsync(
        ReasonletRepository reasonlets,
        ExecutionRepository executions,
        ReasonletStoreEntry entry,
        string hash,
        string version,
        Guid? parentId,
        ReasonletCaptureKind kind,
        IAsyncDbTransaction transaction,
        CancellationToken ct)
    {
        var capture = entry.Capture;
        var provenance = capture.Provenance;
        var id = ReasonletId.New();
        await reasonlets.InsertAsync(
            id.Value,
            capture.Scope.Value,
            entry.Name,
            entry.Intent,
            version,
            ReasonletGraphJson.Serialize(capture.Graph),
            hash,
            parentId,
            (short)ReasonletStatus.Active,
            provenance.Source,
            provenance.RunId,
            provenance.SubjectId,
            SerializeTags(provenance.Tags),
            ToArray(entry.Embedding),
            transaction,
            ct).ConfigureAwait(false);
        var execution = await InsertExecutionAsync(executions, id.Value, capture, transaction, ct).ConfigureAwait(false);
        return new ReasonletReceipt(id, version, execution, kind, ReasonletStatus.Active);
    }

    private static async Task<ExecutionId> InsertExecutionAsync(
        ExecutionRepository executions, Guid reasonletId, ReasonletCapture capture, IAsyncDbTransaction transaction, CancellationToken ct)
    {
        var id = ExecutionId.New();
        var outcome = capture.Outcome;
        var provenance = capture.Provenance;
        await executions.InsertAsync(
            id.Value,
            reasonletId,
            capture.Scope.Value,
            outcome.Success,
            outcome.Duration.Ticks,
            outcome.Score,
            provenance.Source,
            provenance.RunId,
            provenance.SubjectId,
            SerializeTags(provenance.Tags),
            transaction,
            ct).ConfigureAwait(false);
        return id;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ReasonletDefinition, MnemosyneError>> GetAsync(ReasonletScope scope, ReasonletId id, CancellationToken ct)
    {
        var migrated = await EnsureMigratedAsync(ct).ConfigureAwait(false);
        if (migrated.IsFailure)
        {
            return migrated.Error;
        }

        var validScope = scope.Validate();
        if (validScope.IsFailure)
        {
            return validScope.Error with { Message = $"{nameof(scope)}.{validScope.Error.Message}" };
        }

        if (id.IsDefault)
        {
            return new MnemosyneError(MnemosyneErrorCode.Validation, $"{nameof(id)}: must not be the default id");
        }

        return await GuardAsync(GetCoreAsync(scope, id, ct)).ConfigureAwait(false);
    }

    private async Task<Result<ReasonletDefinition, MnemosyneError>> GetCoreAsync(ReasonletScope scope, ReasonletId id, CancellationToken ct)
    {
        await using var connection = (await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false)).AsAsync();
        var row = await new ReasonletRepository(connection).GetAsync(scope.Value, id.Value, ct).ConfigureAwait(false);
        if (row is null)
        {
            return new MnemosyneError(MnemosyneErrorCode.NotFound, "The Reasonlet does not exist in the scope.");
        }

        return ToDefinition(row);
    }

    private static ReasonletDefinition ToDefinition(ReasonletRow row)
    {
        return new ReasonletDefinition
        {
            Id = new ReasonletId(row.Id),
            Scope = new ReasonletScope(row.Scope),
            Name = row.Name,
            Intent = row.Intent,
            Version = row.Version,

            // The store wrote this text from a validated graph, so text that no longer reads is a bug, not a result.
            Graph = OrThrow(ReasonletGraphJson.Deserialize(row.Graph), $"Reading the stored graph of Reasonlet {row.Id}"),
            Provenance = new ReasonletProvenance
            {
                Source = row.Source,
                RunId = row.RunId,
                SubjectId = row.SubjectId,
                Tags = row.Tags is null ? null : JsonSerializer.Deserialize(row.Tags, TagsJson.Default.DictionaryStringString),
            },
            Status = (ReasonletStatus)row.Status,
            CreatedAt = row.CreatedAt,
            ParentId = row.ParentId is { } parent ? new ReasonletId(parent) : null,
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// One scoped <c>INSERT … SELECT</c> from the Reasonlet's row, so a Reasonlet that is missing or in another
    /// scope answers <see cref="MnemosyneErrorCode.NotFound"/> without a prior read. A Reasonlet erased
    /// concurrently answers <see cref="MnemosyneErrorCode.NotFound"/> or <see cref="MnemosyneErrorCode.Conflict"/>.
    /// </remarks>
    public async ValueTask<Result<ExecutionId, MnemosyneError>> AppendExecutionAsync(
        ReasonletScope scope, ReasonletId id, ReasonletOutcome outcome, ReasonletProvenance provenance, CancellationToken ct)
    {
        var migrated = await EnsureMigratedAsync(ct).ConfigureAwait(false);
        if (migrated.IsFailure)
        {
            return migrated.Error;
        }

        var invalid = ValidateScope(scope) ?? ValidateId(id) ?? ValidateNested(outcome.Validate(), nameof(outcome)) ?? ValidateNested(provenance.Validate(), nameof(provenance));
        if (invalid is { } error)
        {
            return error;
        }

        return await GuardAsync(AppendExecutionCoreAsync(scope, id, outcome, provenance, ct)).ConfigureAwait(false);
    }

    private async Task<Result<ExecutionId, MnemosyneError>> AppendExecutionCoreAsync(
        ReasonletScope scope, ReasonletId id, ReasonletOutcome outcome, ReasonletProvenance provenance, CancellationToken ct)
    {
        var executionId = ExecutionId.New();
        await using var connection = (await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false)).AsAsync();
        var inserted = await new ExecutionRepository(connection).InsertForExistingAsync(
            executionId.Value,
            id.Value,
            scope.Value,
            outcome.Success,
            outcome.Duration.Ticks,
            outcome.Score,
            provenance.Source,
            provenance.RunId,
            provenance.SubjectId,
            SerializeTags(provenance.Tags),
            ct).ConfigureAwait(false);
        if (inserted == 0)
        {
            return new MnemosyneError(MnemosyneErrorCode.NotFound, "The Reasonlet does not exist in the scope.");
        }

        return executionId;
    }

    /// <inheritdoc />
    /// <remarks>
    /// One scoped upsert keyed on the execution, so feedback recorded again replaces the earlier feedback. An
    /// execution that is missing or in another scope answers <see cref="MnemosyneErrorCode.NotFound"/>.
    /// </remarks>
    public async ValueTask<UnitResult<MnemosyneError>> AppendFeedbackAsync(
        ReasonletScope scope, ExecutionId execution, ReasonletFeedback feedback, CancellationToken ct)
    {
        var migrated = await EnsureMigratedAsync(ct).ConfigureAwait(false);
        if (migrated.IsFailure)
        {
            return migrated.Error;
        }

        var invalid = ValidateScope(scope);
        if (invalid is null && execution.IsDefault)
        {
            invalid = new MnemosyneError(MnemosyneErrorCode.Validation, $"{nameof(execution)}: must not be the default id");
        }

        invalid ??= ValidateNested(feedback.Validate(), nameof(feedback));
        if (invalid is { } error)
        {
            return error;
        }

        var recorded = await GuardAsync(AppendFeedbackCoreAsync(scope, execution, feedback, ct)).ConfigureAwait(false);
        return recorded.IsFailure ? recorded.Error : UnitResult<MnemosyneError>.Success();
    }

    private async Task<Result<bool, MnemosyneError>> AppendFeedbackCoreAsync(
        ReasonletScope scope, ExecutionId execution, ReasonletFeedback feedback, CancellationToken ct)
    {
        await using var connection = (await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false)).AsAsync();
        var written = await new ExecutionRepository(connection).UpsertFeedbackAsync(
            execution.Value, scope.Value, feedback.Utility, feedback.Correctness, feedback.Reusability, ct).ConfigureAwait(false);
        if (written == 0)
        {
            return new MnemosyneError(MnemosyneErrorCode.NotFound, "The execution does not exist in the scope.");
        }

        return true;
    }

    private static MnemosyneError? ValidateScope(ReasonletScope scope) => ValidateNested(scope.Validate(), nameof(scope));

    private static MnemosyneError? ValidateId(ReasonletId id) =>
        id.IsDefault ? new MnemosyneError(MnemosyneErrorCode.Validation, $"{nameof(id)}: must not be the default id") : null;

    private static MnemosyneError? ValidateNested(UnitResult<MnemosyneError> result, string name) =>
        result.IsFailure ? result.Error with { Message = $"{name}.{result.Error.Message}" } : null;

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>> SearchAsync(
        ReasonletScope scope, ReadOnlyMemory<float> embedding, string? intent, int limit, CancellationToken ct)
    {
        var migrated = await EnsureMigratedAsync(ct).ConfigureAwait(false);
        if (migrated.IsFailure)
        {
            return migrated.Error;
        }

        var invalid = ValidateScope(scope) ?? ValidateSearch(embedding.Span, intent, limit);
        if (invalid is not null)
        {
            return invalid.Value;
        }

        return await GuardAsync(SearchCoreAsync(scope, embedding, intent, limit, ct)).ConfigureAwait(false);
    }

    private MnemosyneError? ValidateSearch(ReadOnlySpan<float> embedding, string? intent, int limit)
    {
        if (limit is < 1 or > MaxSearchLimit)
        {
            return new MnemosyneError(MnemosyneErrorCode.Validation, $"limit: must be from 1 to {MaxSearchLimit}");
        }

        if (intent is not null && string.IsNullOrWhiteSpace(intent))
        {
            return new MnemosyneError(MnemosyneErrorCode.Validation, "intent: must not be blank");
        }

        if (embedding.Length != _options.Dimensions)
        {
            return new MnemosyneError(
                MnemosyneErrorCode.Validation,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"embedding: must have {_options.Dimensions} values, the store's dimension, but has {embedding.Length}"));
        }

        foreach (var value in embedding)
        {
            if (!float.IsFinite(value))
            {
                return new MnemosyneError(MnemosyneErrorCode.Validation, "embedding: must contain only finite numbers");
            }
        }

        return null;
    }

    private async Task<Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>> SearchCoreAsync(
        ReasonletScope scope, ReadOnlyMemory<float> embedding, string? intent, int limit, CancellationToken ct)
    {
        await using var connection = (await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false)).AsAsync();
        var rows = await new ReasonletRepository(connection).SearchAsync(scope.Value, ToArray(embedding), intent, limit, ct).ConfigureAwait(false);
        var candidates = new List<ReasonletCandidate>(rows.Count);
        foreach (var row in rows)
        {
            candidates.Add(new ReasonletCandidate(ToDefinition(row.ToReasonletRow()), row.Similarity, new ReasonletMetrics(row.Runs, row.Successes, row.LastSuccessAt)));
        }

        return candidates;
    }

    /// <inheritdoc />
    public async ValueTask<Result<ReasonletErasureResult, MnemosyneError>> EraseAsync(ReasonletErasure erasure, CancellationToken ct)
    {
        var migrated = await EnsureMigratedAsync(ct).ConfigureAwait(false);
        if (migrated.IsFailure)
        {
            return migrated.Error;
        }

        throw new NotImplementedException();
    }

    /// <summary>
    /// Succeeds once the schema is migrated with this store's dimension, and caches that. Answers
    /// <see cref="MnemosyneErrorCode.Unavailable"/> before <see cref="MigrateAsync"/> has run, and
    /// <see cref="MnemosyneErrorCode.Validation"/> when the recorded dimension differs from the configured one.
    /// </summary>
    internal async ValueTask<UnitResult<MnemosyneError>> EnsureMigratedAsync(CancellationToken ct)
    {
        if (_migrated)
        {
            return UnitResult<MnemosyneError>.Success();
        }

        UnitResult<MnemosyneError> result;
        try
        {
            result = await CheckMigratedAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (PostgresErrors.Map(ex) is { } error)
        {
            result = error;
        }

        return result;
    }

    private async Task<UnitResult<MnemosyneError>> CheckMigratedAsync(CancellationToken ct)
    {
        await using var connection = (await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false)).AsAsync();
        var settings = new SettingsRepository(connection);
        var recorded = await settings.SettingsTableExistsAsync(ct).ConfigureAwait(false)
            ? await settings.GetDimensionsAsync(ct).ConfigureAwait(false)
            : null;
        if (recorded is not { } dimensions)
        {
            return new MnemosyneError(MnemosyneErrorCode.Unavailable, "schema not migrated: call MigrateAsync");
        }

        if (dimensions != _options.Dimensions)
        {
            return DimensionMismatch(dimensions);
        }

        _migrated = true;
        return UnitResult<MnemosyneError>.Success();
    }

    // DDL takes no parameters, so these statements are raw text. Only the dimension and the HNSW
    // parameters are interpolated: integers that MnemosynePostgreSqlOptions.Validate has range-checked,
    // formatted with the invariant culture so no culture can change the digits.
    private async Task ApplyDimensionsAsync(IAsyncDbConnection connection, IAsyncDbTransaction transaction, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = string.Create(
                CultureInfo.InvariantCulture,
                $"""
                ALTER TABLE mnemosyne.reasonlets ALTER COLUMN embedding TYPE vector({_options.Dimensions});
                CREATE INDEX reasonlets_embedding_hnsw ON mnemosyne.reasonlets
                    USING hnsw (embedding vector_cosine_ops)
                    WITH (m = {_options.HnswM}, ef_construction = {_options.HnswEfConstruction});
                """);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Maps the database failures that <see cref="PostgresErrors"/> knows to an error; anything else propagates.</summary>
    private static async Task<Result<T, MnemosyneError>> GuardAsync<T>(Task<Result<T, MnemosyneError>> work)
    {
        // Assigned rather than returned inside the try, so every line of the method can execute.
        Result<T, MnemosyneError> result;
        try
        {
            result = await work.ConfigureAwait(false);
        }
        catch (Exception ex) when (PostgresErrors.Map(ex) is { } error)
        {
            result = error;
        }

        return result;
    }

    /// <summary>Unwraps a result that only a bug in the store can fail, and throws when it does.</summary>
    private static T OrThrow<T>(Result<T, MnemosyneError> result, string what) =>
        result.IsSuccess ? result.Value : throw new InvalidOperationException($"{what} failed: {result.Error.Message}");

    private static string? SerializeTags(IReadOnlyDictionary<string, string>? tags) =>
        tags is null ? null : JsonSerializer.Serialize(tags, TagsJson.Default.IReadOnlyDictionaryStringString);

    /// <summary>Returns the embedding's own array when the memory spans a whole array, and a copy otherwise.</summary>
    private static float[] ToArray(ReadOnlyMemory<float> embedding) =>
        MemoryMarshal.TryGetArray(embedding, out var segment) && segment.Offset == 0 && segment.Count == segment.Array!.Length
            ? segment.Array
            : embedding.ToArray();

    private static MnemosyneError LineageConflict() =>
        new(MnemosyneErrorCode.Conflict, "The lineage changed under a concurrent erase.");

    private MnemosyneError DimensionMismatch(int recorded) => new(
        MnemosyneErrorCode.Validation,
        string.Create(
            CultureInfo.InvariantCulture,
            $"Dimensions: the database's embeddings have {recorded} dimensions, but the store is configured with {_options.Dimensions}. Mixing embedding models would corrupt similarity."));
}
