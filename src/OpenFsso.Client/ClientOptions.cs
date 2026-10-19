using System.Text.Json.Serialization;
using OpenFsso.Core;
using OpenFsso.Windows;

namespace OpenFsso.Client;

public sealed record ClientOptions
{
    public required ReceiverEndpoint[] Receivers
    {
        get; init;
    }
    public required string StateDirectory
    {
        get; init;
    }
    public required string LogDirectory
    {
        get; init;
    }
    public bool AllowRdp { get; init; } = false;
    public const int RenewalSeconds = 60;
    [JsonPropertyName("onFabric")]
    public FabricRule? OnFabric
    {
        get; init;
    }

    // The embedded JSON Schema checks fields; these rules span items or the OS.
    public void Validate()
    {
        if (Receivers.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Receivers.Length || Receivers.Select(x => x.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Receivers.Length || !Path.IsPathFullyQualified(StateDirectory) || !Path.IsPathFullyQualified(LogDirectory))
        {
            throw new InvalidDataException("Receiver names and hosts must be unique, and data paths absolute.");
        }
    }
}
