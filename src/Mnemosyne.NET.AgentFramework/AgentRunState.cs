using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET.AgentFramework;

/// <summary>What the start of one agent run leaves for its end: kept against the run's session, never in a field runs share.</summary>
/// <param name="Scope">The run's scope and subject, as the scope resolver returned them.</param>
/// <param name="Parent">The id of the Reasonlet suggested to the run, or null when nothing was suggested.</param>
/// <param name="Lookup">The run's own <c>find_procedure</c> tool, or null when none was added.</param>
/// <param name="StartTimestamp">The <see cref="TimeProvider.GetTimestamp"/> at which the run started.</param>
internal sealed record AgentRunState(ReasonletAgentScope Scope, ReasonletId? Parent, FindProcedureFunction? Lookup, long StartTimestamp);
