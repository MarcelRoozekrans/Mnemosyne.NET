namespace Mnemosyne.NET.Abstractions;

/// <summary>Describes why a Mnemosyne operation failed.</summary>
/// <param name="Code">The error classification.</param>
/// <param name="Message">A human-readable message. Validation errors read "path: reason".</param>
/// <param name="Detail">Optional extra detail, such as a backing-store message.</param>
public readonly record struct MnemosyneError(MnemosyneErrorCode Code, string Message, string? Detail = null);
