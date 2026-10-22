namespace Mnemosyne.NET.PostgreSql;

/// <summary>Counts what the committed batches of one prune deleted, so far.</summary>
internal sealed class PruneProgress
{
    public int Reasonlets { get; set; }

    public int Executions { get; set; }
}
