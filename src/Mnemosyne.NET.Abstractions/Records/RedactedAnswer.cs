using System.Text;

namespace Mnemosyne.NET.Abstractions;

/// <summary>Prints a run's answer for a record's <c>ToString</c> without its text, which may never reach a log.</summary>
internal static class RedactedAnswer
{
    /// <summary>What <c>ToString</c> shows in place of an answer that is present.</summary>
    internal const string Redacted = "[redacted]";

    /// <summary>Appends <c>, Answer = [redacted]</c>, or <c>, Answer = null</c> when there is no answer.</summary>
    internal static void Print(StringBuilder builder, string? answer) =>
        builder.Append(", Answer = ").Append(answer is null ? "null" : Redacted);
}
