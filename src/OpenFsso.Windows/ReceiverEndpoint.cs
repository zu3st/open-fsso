using System.Diagnostics.CodeAnalysis;

namespace OpenFsso.Windows;

public sealed record ReceiverEndpoint
{
    public required string Name
    {
        get; init;
    }
    public required string Host
    {
        get; init;
    }
    public required string Address
    {
        get; init;
    }
    public required int Port
    {
        get; init;
    }
    public bool CheckRevocation { get; init; } = true;

    public ReceiverEndpoint()
    {
    }

    [SetsRequiredMembers]
    public ReceiverEndpoint(string Name, string Host, string Address, int Port, bool CheckRevocation = true)
    {
        this.Name = Name;
        this.Host = Host;
        this.Address = Address;
        this.Port = Port;
        this.CheckRevocation = CheckRevocation;
    }
}
