using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET;

/// <summary>The scope, and optionally the subject, one chat call recalls and captures under.</summary>
/// <param name="Scope">The scope the call's Reasonlets live in.</param>
/// <param name="SubjectId">The subject the call is about, such as a user id, or null for none.</param>
public sealed record ReasonletCallScope(ReasonletScope Scope, string? SubjectId);
