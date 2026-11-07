namespace Mnemosyne.NET;

/// <summary>Marks that <c>AddMnemosyneVariantSelection</c> already ran, and holds the key of the selector it wraps.</summary>
internal sealed class VariantSelectionMarker
{
    /// <summary>The key the wrapped selector is registered under.</summary>
    internal static readonly object OriginalKey = new();
}
