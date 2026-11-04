using Microsoft.Extensions.AI;
using Mnemosyne.NET.Abstractions;

namespace Mnemosyne.NET;

/// <summary>Sets and reads the per-call Reasonlet scope on <see cref="ChatOptions"/>.</summary>
public static class ReasonletChatOptionsExtensions
{
    /// <summary>The key under which the per-call scope is stored in <see cref="ChatOptions.AdditionalProperties"/>.</summary>
    public const string ScopeKey = "mnemosyne.scope";

    /// <summary>Stores the scope the call recalls and captures under. It changes <paramref name="options"/> and returns it.</summary>
    /// <param name="options">The options to change, or null to create new ones.</param>
    /// <param name="scope">The scope. Must be valid.</param>
    /// <param name="subjectId">The subject the call is about, or null for none.</param>
    /// <returns>The options that now hold the scope.</returns>
    /// <exception cref="ArgumentException"><paramref name="scope"/> is not valid.</exception>
    public static ChatOptions WithReasonletScope(this ChatOptions? options, ReasonletScope scope, string? subjectId = null)
    {
        var validation = scope.Validate();
        if (validation.IsFailure)
        {
            throw new ArgumentException(validation.Error.Message, nameof(scope));
        }

        options ??= new ChatOptions();
        options.AdditionalProperties ??= [];
        options.AdditionalProperties[ScopeKey] = new ReasonletCallScope(scope, subjectId);
        return options;
    }

    /// <summary>Reads the scope stored by <see cref="WithReasonletScope"/>.</summary>
    /// <param name="options">The options to read, or null.</param>
    /// <returns>The scope, or null when there are no options, no scope or a value of another type under the key.</returns>
    public static ReasonletCallScope? GetReasonletScope(this ChatOptions? options) =>
        options?.AdditionalProperties is { } properties
        && properties.TryGetValue(ScopeKey, out var value)
        && value is ReasonletCallScope scope
            ? scope
            : null;
}
