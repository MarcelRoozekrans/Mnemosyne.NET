namespace Mnemosyne.NET.Abstractions;

/// <summary>Identifies a Reasonlet. Backed by a time-ordered version 7 GUID.</summary>
/// <param name="Value">The underlying GUID.</param>
public readonly record struct ReasonletId(Guid Value)
{
    /// <summary>Creates a new identifier from a version 7 GUID, which sorts by creation millisecond in big-endian byte order, as PostgreSQL compares uuid.</summary>
    /// <returns>A new identifier.</returns>
    public static ReasonletId New() => new(Guid.CreateVersion7());

    /// <summary>Gets a value indicating whether this is the default, empty identifier.</summary>
    public bool IsDefault => Value == Guid.Empty;

    /// <summary>Returns the GUID in its standard form, so logs and interpolated strings show the bare identifier.</summary>
    /// <returns>The GUID, formatted as <see cref="Guid.ToString()"/> formats it.</returns>
    public override string ToString() => Value.ToString();
}
