namespace Mnemosyne.NET.PostgreSql.Rows;

/// <summary>What a reject decides on, read by position after it has locked the Reasonlet.</summary>
/// <param name="Status">The Reasonlet's <c>ReasonletStatus</c> number.</param>
/// <param name="HasChildren">Whether another Reasonlet in the scope has it as its parent.</param>
internal sealed record RejectTargetRow(short Status, bool HasChildren);
