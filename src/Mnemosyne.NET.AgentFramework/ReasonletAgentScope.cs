using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET.AgentFramework;

/// <summary>The scope, and optionally the data subject, that one agent run's memory belongs to.</summary>
/// <param name="Scope">The scope recalled from and captured into.</param>
/// <param name="SubjectId">The data subject the run is about, or null when it has none.</param>
public sealed record ReasonletAgentScope(ReasonletScope Scope, string? SubjectId = null);
