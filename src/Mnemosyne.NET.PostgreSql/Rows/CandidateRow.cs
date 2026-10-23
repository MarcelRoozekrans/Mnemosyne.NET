using ZeroAlloc.ORM;

namespace Mnemosyne.NET.PostgreSql.Rows;

/// <summary>A search hit: a stored Reasonlet without its embedding, plus its similarity and execution metrics, read by position.</summary>
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
/// <param name="Similarity">One minus the cosine distance, clamped to at least 0.</param>
/// <param name="Runs">How many executions the Reasonlet has.</param>
/// <param name="Successes">How many of them succeeded.</param>
/// <param name="LastSuccessAt">When the latest successful execution ran, or null when none succeeded.</param>
/// <param name="LastExecutedAt">When the latest execution ran, or when the Reasonlet was stored if it has no metrics row.</param>
/// <param name="ParentRuns">The parent's executions, or null when there is no parent metrics row.</param>
/// <param name="ParentSuccesses">The parent's successful executions, or null when there is no parent metrics row.</param>
/// <param name="ParentLastSuccessAt">When the parent's latest successful execution ran, or null.</param>
/// <param name="ParentLastExecutedAt">When the parent's latest execution ran, or null when there is no parent metrics row.</param>
internal sealed record CandidateRow(
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
    DateTimeOffset CreatedAt,
    double Similarity,
    int Runs,
    int Successes,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset LastExecutedAt,
    int? ParentRuns,
    int? ParentSuccesses,
    DateTimeOffset? ParentLastSuccessAt,
    DateTimeOffset? ParentLastExecutedAt)
{
    /// <summary>The Reasonlet columns, for the mapping that <c>GetAsync</c> shares.</summary>
    public ReasonletRow ToReasonletRow() =>
        new(Id, Scope, Name, Intent, Version, Graph, ParentId, Status, Source, RunId, SubjectId, Tags, CreatedAt);
}
