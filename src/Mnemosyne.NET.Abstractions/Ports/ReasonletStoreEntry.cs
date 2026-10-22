namespace Mnemosyne.NET.Abstractions;

/// <summary>The input to an atomic remember in <see cref="IReasonletStore"/>.</summary>
/// <remarks>
/// <paramref name="Intent"/> and <paramref name="Name"/> are the resolved values and are authoritative.
/// The store must not read <c>Capture.Intent</c> or <c>Capture.Name</c>.
/// </remarks>
/// <param name="Capture">The capture being stored.</param>
/// <param name="Intent">The resolved intent text.</param>
/// <param name="Name">The resolved Reasonlet name.</param>
/// <param name="Embedding">The embedding of the capture's question.</param>
public sealed record ReasonletStoreEntry(
    ReasonletCapture Capture,
    string Intent,
    string Name,
    ReadOnlyMemory<float> Embedding);
