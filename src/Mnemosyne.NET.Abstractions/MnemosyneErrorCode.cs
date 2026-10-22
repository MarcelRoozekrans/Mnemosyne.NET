namespace Mnemosyne.NET.Abstractions;

/// <summary>Classifies a <see cref="MnemosyneError"/>.</summary>
public enum MnemosyneErrorCode
{
    /// <summary>An input broke an invariant.</summary>
    Validation,

    /// <summary>
    /// The ID does not exist in the caller's scope. An ID that exists in another scope also answers
    /// NotFound, so IDs can't be probed across scopes.
    /// </summary>
    NotFound,

    /// <summary>The write lost a race the store detected, such as the parent being erased between lookup and insert.</summary>
    Conflict,

    /// <summary>The backing store or the embedding generator failed.</summary>
    Unavailable,
}
