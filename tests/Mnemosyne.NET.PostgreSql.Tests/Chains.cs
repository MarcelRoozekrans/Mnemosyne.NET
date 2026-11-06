using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET.PostgreSql.Tests;

/// <summary>Builds deep version chains with one server-side statement, for tests that a per-row round trip would slow down.</summary>
internal static class Chains
{
    /// <summary>
    /// Inserts a chain of the given depth in the scope, versions 1.0 down to 1.(depth - 1), each the child of the one before,
    /// and returns the ids of its root and its leaf. Pass a serialised graph when the rows must deserialise.
    /// </summary>
    public static async Task<(Guid Root, Guid Leaf)> InsertAsync(Npgsql.NpgsqlDataSource dataSource, ReasonletScope scope, int depth, string graphJson = "{}")
    {
        var root = Guid.CreateVersion7();
        var leaf = Guid.CreateVersion7();
        await Entries.ExecuteAsync(
            dataSource,
            $$"""
            DO $$
            DECLARE previous uuid; current uuid;
            BEGIN
                FOR i IN 0..{{depth - 1}} LOOP
                    current := CASE WHEN i = {{depth - 1}} THEN '{{leaf}}'::uuid WHEN i = 0 THEN '{{root}}'::uuid ELSE gen_random_uuid() END;
                    INSERT INTO mnemosyne.reasonlets (id, scope, name, intent, version, graph, content_hash, parent_id, status, source, created_at, embedding)
                    VALUES (current, '{{scope.Value}}', 'n', 'i', '1.' || i, '{{graphJson}}', repeat('0', 64), previous, 1, 's', now(), '[1,0,0]');
                    previous := current;
                END LOOP;
            END $$
            """);
        return (root, leaf);
    }
}
