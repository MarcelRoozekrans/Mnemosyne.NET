using ZeroAlloc.ORM;

namespace Mnemosyne.NET.PostgreSql.Rows;

/// <summary>A stored Reasonlet without its embedding, read by position.</summary>
/// <param name="Id">The id.</param>
/// <param name="Scope">The scope.</param>
/// <param name="Name">The name.</param>
/// <param name="Intent">The intent text.</param>
/// <param name="Version">The version label.</param>
/// <param name="Graph">The graph exactly as <c>ReasonletGraphJson.Serialize</c> wrote it.</param>
/// <param name="ParentId">The parent, or null.</param>
/// <param name="Status">The <c>ReasonletStatus</c> number.</param>
/// <param name="Source">The provenance source.</param>
/// <param name="RunId">The provenance run id, or null.</param>
/// <param name="SubjectId">The provenance subject id, or null.</param>
/// <param name="Tags">The provenance tags as a JSON object, or null.</param>
/// <param name="CreatedAt">When the Reasonlet was stored.</param>
internal sealed record ReasonletRow(
    Guid Id,
    string Scope,
    string Name,
    string Intent,
    string Version,
    string Graph,
    Guid? ParentId,
    short Status,
    string Source,
    string? RunId,
    string? SubjectId,
    [Json] string? Tags,
    DateTimeOffset CreatedAt);
