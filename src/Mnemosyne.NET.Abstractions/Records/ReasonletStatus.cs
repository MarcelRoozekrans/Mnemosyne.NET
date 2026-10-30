namespace Mnemosyne.NET.Abstractions;

/// <summary>The lifecycle status of a Reasonlet.</summary>
/// <remarks>The values are numbered from 1 because stores persist them; 0 is undefined and fails validation.</remarks>
public enum ReasonletStatus
{
    /// <summary>The Reasonlet is active and eligible for matching.</summary>
    Active = 1,

    /// <summary>The Reasonlet awaits review before it becomes active.</summary>
    /// <remarks>This is what the review gate stores while <c>MnemosyneOptions.RequireReview</c> is on. Approving the Reasonlet makes it <see cref="Active"/>.</remarks>
    PendingReview = 2,
}
