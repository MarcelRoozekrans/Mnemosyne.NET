namespace Mnemosyne.NET.PostgreSql.Rows;

/// <summary>The PostgreSQL server's version, read by position.</summary>
/// <param name="Number">The <c>server_version_num</c>, such as 170011 for 17.11.</param>
/// <param name="Text">The <c>server_version</c>, as the server reports it.</param>
internal sealed record ServerVersionRow(int Number, string Text);
