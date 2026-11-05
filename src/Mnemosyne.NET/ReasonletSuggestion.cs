using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET;

/// <summary>The procedure an integration offers the model for a new question.</summary>
/// <param name="Id">The recalled Reasonlet's id, which a later capture can record as the parent of what the model did with it.</param>
/// <param name="Text">The rendered suggestion, never blank.</param>
public sealed record ReasonletSuggestion(ReasonletId Id, string Text);
