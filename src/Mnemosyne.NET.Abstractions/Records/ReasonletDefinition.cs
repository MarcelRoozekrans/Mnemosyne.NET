using ZeroAlloc.Results;

namespace Mnemosyne.NET.Abstractions;

/// <summary>The stored definition of a Reasonlet.</summary>
/// <param name="Id">The identifier. Must not be the default id.</param>
/// <param name="Scope">The scope the Reasonlet belongs to.</param>
/// <param name="Name">The name. Must not be blank.</param>
/// <param name="Intent">The intent text. Must not be blank.</param>
/// <param name="Version">The version label. Must not be blank.</param>
/// <param name="Graph">The graph. Must not be null.</param>
/// <param name="ParentId">The optional parent Reasonlet. When present it must not be the default id.</param>
/// <param name="Provenance">Where the Reasonlet came from. Must not be null.</param>
/// <param name="Status">The lifecycle status. Must be a defined value.</param>
/// <param name="CreatedAt">When the Reasonlet was created.</param>
public sealed record ReasonletDefinition(
    ReasonletId Id,
    ReasonletScope Scope,
    string Name,
    string Intent,
    string Version,
    ReasonletGraph Graph,
    ReasonletId? ParentId,
    ReasonletProvenance Provenance,
    ReasonletStatus Status,
    DateTimeOffset CreatedAt)
{
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
