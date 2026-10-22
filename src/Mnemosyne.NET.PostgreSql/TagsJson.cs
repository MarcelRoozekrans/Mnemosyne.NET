using System.Text.Json.Serialization;

namespace Mnemosyne.NET.PostgreSql;

/// <summary>Source-generated JSON for provenance tags, so reading and writing the <c>tags</c> jsonb columns needs no reflection.</summary>
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
internal sealed partial class TagsJson : JsonSerializerContext;
