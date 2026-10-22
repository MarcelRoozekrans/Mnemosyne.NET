namespace Mnemosyne.NET.Abstractions;

/// <summary>Classifies a <see cref="MnemosyneError"/>.</summary>
/// <remarks>
/// The values are numbered from 1 and 0 is deliberately undefined. A <c>default</c>
/// <c>Result</c> or <c>UnitResult</c> is a failure whose error is <c>default(MnemosyneError)</c>,
/// so its code reads 0 and is recognisable as never having been set, rather than passing for a
/// <see cref="Validation"/> failure.
/// </remarks>
public enum MnemosyneErrorCode
{
    /// <summary>An input broke an invariant.</summary>
    Validation = 1,

    /// <summary>
    /// The ID does not exist in the caller's scope. An ID that exists in another scope also answers
    /// NotFound, so IDs can't be probed across scopes.
    /// </summary>
    NotFound = 2,

    /// <summary>The write lost a race the store detected, such as the parent being erased between lookup and insert.</summary>
    Conflict = 3,

    /// <summary>A backing dependency (store, embedding generator, intent extractor or selector) failed.</summary>
    Unavailable = 4,
}
