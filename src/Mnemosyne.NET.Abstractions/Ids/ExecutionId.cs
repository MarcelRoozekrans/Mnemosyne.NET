namespace Mnemosyne.NET.Abstractions;

/// <summary>Identifies a Execution. Backed by a time-ordered version 7 GUID.</summary>
/// <param name="Value">The underlying GUID.</param>
public readonly record struct ExecutionId(Guid Value)
{
    /// <summary>Creates a new identifier from a version 7 GUID, which sorts by creation time.</summary>
    /// <returns>A new identifier.</returns>
    public static ExecutionId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this is the default, empty identifier.</summary>
    public bool IsDefault => Value == Guid.Empty;
}
