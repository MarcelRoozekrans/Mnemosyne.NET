using Mnemosyne.NET.Abstractions.Validation;
using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>The stored definition of a Reasonlet.</summary>
/// <remarks>Init-only, so later phases can add members without breaking callers.</remarks>
public sealed record ReasonletDefinition
{
    /// <summary>Gets the identifier. Must not be the default id.</summary>
    public required ReasonletId Id { get; init; }

    /// <summary>Gets the scope the Reasonlet belongs to.</summary>
    public required ReasonletScope Scope { get; init; }

    /// <summary>Gets the name. Must not be blank.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the intent text. Must not be blank.</summary>
    public required string Intent { get; init; }

    /// <summary>Gets the version label. Must not be blank.</summary>
    public required string Version { get; init; }

    /// <summary>Gets the graph. Must not be null.</summary>
    public required ReasonletGraph Graph { get; init; }

    /// <summary>Gets where the Reasonlet came from. Must not be null.</summary>
    public required ReasonletProvenance Provenance { get; init; }

    /// <summary>Gets the lifecycle status. Must be a defined value.</summary>
    public required ReasonletStatus Status { get; init; }

    /// <summary>Gets when the Reasonlet was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets the optional parent Reasonlet. When present it must not be the default id.</summary>
    public ReasonletId? ParentId { get; init; }

    /// <summary>Validates the definition.</summary>
    /// <returns>Success, or a validation failure with a path relative to this record.</returns>
    public UnitResult<MnemosyneError> Validate() => Check.All(
        () => Check.NotDefault(Id, nameof(Id)),
        () => Check.Nested(Scope.Validate(), nameof(Scope)),
        () => Check.NotBlank(Name, nameof(Name)),
        () => Check.NotBlank(Intent, nameof(Intent)),
        () => Check.NotBlank(Version, nameof(Version)),
        () => Check.NotNull(Graph, nameof(Graph)),
        () => Check.Nested(Graph.Validate(), nameof(Graph)),
        () => ParentId is { } parent ? Check.NotDefault(parent, nameof(ParentId)) : UnitResult<MnemosyneError>.Success(),
        () => Check.NotNull(Provenance, nameof(Provenance)),
        () => Check.Nested(Provenance.Validate(), nameof(Provenance)),
        () => Check.Defined(Status, nameof(Status)));
}
