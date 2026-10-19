using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenFsso.Core;

// Compile-time JSON metadata, so trimmed builds serialize without reflection.
// Presence messages between client and receiver use web naming.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(PresenceRequest))]
[JsonSerializable(typeof(PresenceReply))]
[JsonSerializable(typeof(SessionReply))]
public sealed partial class WireJson : JsonSerializerContext
{
}

// The client's status pipe and the tray's --status-json output.
[JsonSerializable(typeof(ClientStatus))]
[JsonSerializable(typeof(StatusSnapshot))]
public sealed partial class StatusJson : JsonSerializerContext
{
}

public sealed record StatusSnapshot(ClientStatus? Status, ClientStatusView View);
