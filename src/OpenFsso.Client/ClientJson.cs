using System.Text.Json.Serialization;

namespace OpenFsso.Client;

// Compile-time JSON metadata, so trimmed builds serialize without reflection.
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, MaxDepth = 256)]
[JsonSerializable(typeof(ClientOptions))]
[JsonSerializable(typeof(OwnerRecord))]
[JsonSerializable(typeof(ClientPaths))]
public sealed partial class ClientJson : JsonSerializerContext
{
}

public sealed record ClientPaths(string LogDirectory, string StateDirectory);
