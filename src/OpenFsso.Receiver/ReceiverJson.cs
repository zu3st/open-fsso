using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenFsso.Receiver;

// Compile-time JSON metadata, so trimmed builds serialize without reflection.
[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, MaxDepth = 256)]
[JsonSerializable(typeof(ReceiverOptions))]
[JsonSerializable(typeof(ReceiverPaths))]
public sealed partial class ReceiverJson : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(HealthReport))]
internal sealed partial class HealthJson : JsonSerializerContext
{
}

public sealed record ReceiverPaths(string LogDirectory, string ServiceName);
internal sealed record HealthReport(bool Ready, int ActiveLeases, bool CollectorRunning, bool LocalSocketDelivery, string CollectorAcceptance);
