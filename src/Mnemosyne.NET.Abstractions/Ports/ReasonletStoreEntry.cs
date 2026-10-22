namespace Mnemosyne.NET.Abstractions;

/// <summary>The input to an atomic remember in <see cref="IReasonletStore"/>.</summary>
/// <param name="Capture">The capture being stored.</param>
/// <param name="Intent">The resolved intent text.</param>
/// <param name="Name">The resolved Reasonlet name.</param>
/// <param name="Embedding">The embedding of the intent.</param>
public sealed record ReasonletStoreEntry(
    ReasonletCapture Capture,
    string Intent,
    string Name,
    ReadOnlyMemory<float> Embedding);
