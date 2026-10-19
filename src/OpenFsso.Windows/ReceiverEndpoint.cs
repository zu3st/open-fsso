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
    public bool CheckRevocation { get; set; } = true;
}
