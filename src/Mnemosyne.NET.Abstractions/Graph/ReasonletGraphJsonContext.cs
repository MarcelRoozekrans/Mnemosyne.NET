using System.Text.Json.Serialization;

namespace Mnemosyne.NET.Abstractions;

/// <summary>The source-generated, reflection-free JSON metadata for <see cref="ReasonletGraph"/>.</summary>
/// <remarks>
/// The attribute options let the generator emit fast paths that match the runtime options.
/// <see cref="ReasonletGraphJson"/> builds the options actually used, adding the encoder and the empty-properties rule.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true,
    AllowDuplicateProperties = false,
    MaxDepth = ReasonletGraphJson.MaxDepth)]
[JsonSerializable(typeof(ReasonletGraph))]
internal sealed partial class ReasonletGraphJsonContext : JsonSerializerContext;
