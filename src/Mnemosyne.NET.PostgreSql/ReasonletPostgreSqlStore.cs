using System.Data.Async;
using System.Data.Async.Adapters;
using System.Globalization;
using Mnemosyne.NET.Abstractions;
using Mnemosyne.NET.PostgreSql.Repositories;
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
    public async ValueTask<Result<ReasonletReceipt, MnemosyneError>> RememberAsync(ReasonletStoreEntry entry, CancellationToken ct)
    {
        var migrated = await EnsureMigratedAsync(ct).ConfigureAwait(false);
        if (migrated.IsFailure)
        {
            return migrated.Error;
        }

        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public async ValueTask<Result<ReasonletDefinition, MnemosyneError>> GetAsync(ReasonletScope scope, ReasonletId id, CancellationToken ct)
    {
        var migrated = await EnsureMigratedAsync(ct).ConfigureAwait(false);
        if (migrated.IsFailure)
        {
            return migrated.Error;
        }

        return new MnemosyneError(MnemosyneErrorCode.NotFound, "The Reasonlet does not exist in the scope.");
    }

    /// <inheritdoc />
    public async ValueTask<Result<ExecutionId, MnemosyneError>> AppendExecutionAsync(
        ReasonletScope scope, ReasonletId id, ReasonletOutcome outcome, ReasonletProvenance provenance, CancellationToken ct)
    {
        var migrated = await EnsureMigratedAsync(ct).ConfigureAwait(false);
        if (migrated.IsFailure)
        {
            return migrated.Error;
        }

        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public async ValueTask<UnitResult<MnemosyneError>> AppendFeedbackAsync(
        ReasonletScope scope, ExecutionId execution, ReasonletFeedback feedback, CancellationToken ct)
    {
        var migrated = await EnsureMigratedAsync(ct).ConfigureAwait(false);
        if (migrated.IsFailure)
        {
            return migrated.Error;
        }

        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<ReasonletCandidate>, MnemosyneError>> SearchAsync(
        ReasonletScope scope, ReadOnlyMemory<float> embedding, string? intent, int limit, CancellationToken ct)
    {
        var migrated = await EnsureMigratedAsync(ct).ConfigureAwait(false);
        if (migrated.IsFailure)
        {
            return migrated.Error;
        }

        throw new NotImplementedException();
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

    private MnemosyneError DimensionMismatch(int recorded) => new(
        MnemosyneErrorCode.Validation,
        string.Create(
            CultureInfo.InvariantCulture,
            $"Dimensions: the database's embeddings have {recorded} dimensions, but the store is configured with {_options.Dimensions}. Mixing embedding models would corrupt similarity."));
}
