namespace Mnemosyne.NET.Abstractions;

/// <summary>The lifecycle status of a Reasonlet.</summary>
public enum ReasonletStatus
{
    /// <summary>The Reasonlet is active and eligible for matching.</summary>
    Active,

    /// <summary>The Reasonlet awaits review before it becomes active.</summary>
    PendingReview,
}
