using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET;

/// <summary>What a caller adds to a chat transcript to make it a <see cref="ReasonletCapture"/>.</summary>
/// <remarks>
/// Init-only, so later phases can add members without breaking callers. The members are copied into the capture
/// unchanged; <see cref="IReasonletMemory.RememberAsync"/> validates them.
/// </remarks>
public sealed record TranscriptCaptureDetails
{
    /// <summary>Gets the scope to capture into.</summary>
    public required ReasonletScope Scope { get; init; }

    /// <summary>Gets where the capture came from.</summary>
    public required ReasonletProvenance Provenance { get; init; }

    /// <summary>Gets the outcome of the run, as the agent sees it.</summary>
    public required ReasonletOutcome Outcome { get; init; }

    /// <summary>
    /// Gets the Reasonlet the run followed, usually the one recall offered. With it, an equal shape is recorded as an
    /// execution of that Reasonlet and a different one as a new version; without it, the capture is a new Reasonlet.
    /// </summary>
    public ReasonletId? ParentId { get; init; }

    /// <summary>Gets the optional intent text.</summary>
    public string? Intent { get; init; }

    /// <summary>Gets the optional name.</summary>
    public string? Name { get; init; }
}
