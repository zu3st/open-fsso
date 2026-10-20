using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using OpenFsso.Core;
using Windows.Win32;
using Windows.Win32.Networking.WinSock;
using Windows.Win32.NetworkManagement.IpHelper;

namespace OpenFsso.Windows;

public sealed class WindowsFabricNetwork : IFabricNetwork
{
    public IReadOnlyList<FabricNetwork> Capture(IReadOnlySet<string> resolveMacFor)
    {
        var result = new List<FabricNetwork>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            try
            {
                var properties = adapter.GetIPProperties();
                var addresses = properties.UnicastAddresses.Where(value => !IPAddress.IsLoopback(value.Address)).ToArray();
                var ipv4 = adapter.Supports(NetworkInterfaceComponent.IPv4) ? properties.GetIPv4Properties() : null;
                var servers = new List<DhcpServerObservation>();
                if (ipv4?.IsDhcpEnabled == true)
                {
                    foreach (var server in properties.DhcpServerAddresses.Where(value => value.AddressFamily == AddressFamily.InterNetwork))
                    {
                        string? mac = null;
                        if (resolveMacFor.Contains(server.ToString()))
                        {
                            // Only resolve a server on this adapter's local link.
                            // A relay/gateway MAC is not the DHCP server's identity.
                            var source = addresses.FirstOrDefault(value => value.Address.AddressFamily == AddressFamily.InterNetwork && OnLink(value, server));
                            if (source != null)
                            {
                                mac = ReadMac(ipv4.Index, source.Address, server);
                            }
                        }

                        servers.Add(new(server.ToString(), mac));
                    }
                }

                result.Add(new(ipv4?.Index ?? properties.GetIPv6Properties().Index, addresses.Select(value => value.Address.ToString()).ToArray(), servers.ToArray()));
            }
            catch (NetworkInformationException)
            {
                // An adapter can disappear during a hot-plug enumeration.
            }
        }

        return result;
    }

    // Connecting a UDP socket only consults the routing table; it sends nothing.
    public string? RouteSource(string address, int port)
    {
        try
        {
            using var probe = new Socket(IPAddress.Parse(address).AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(IPAddress.Parse(address), port);
            return ((IPEndPoint)probe.LocalEndPoint!).Address.ToString();
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static bool OnLink(UnicastIPAddressInformation source, IPAddress server) => new IPNetwork(source.Address, source.PrefixLength).Contains(server);

    private static unsafe string? ReadMac(int index, IPAddress source, IPAddress server)
    {
        SOCKADDR_INET Address(IPAddress value)
        {
            var address = new SOCKADDR_INET();
            address.Ipv4.sin_family = ADDRESS_FAMILY.AF_INET;
            address.Ipv4.sin_addr.S_un.S_addr = BitConverter.ToUInt32(value.GetAddressBytes());
            return address;
        }

        var row = new MIB_IPNET_ROW2 { InterfaceIndex = (uint)index, Address = Address(server) };
        if (PInvoke.GetIpNetEntry2(&row) != 0 || row.State is not (NL_NEIGHBOR_STATE.NlnsReachable or NL_NEIGHBOR_STATE.NlnsPermanent))
        {
            var local = Address(source);
            if (PInvoke.ResolveIpNetEntry2(&row, &local) != 0)
            {
                return null;
            }
        }

        if (row.PhysicalAddressLength != 6)
        {
            return null;
        }

        byte[] mac = new byte[6];
        for (int part = 0; part < mac.Length; part++)
        {
            mac[part] = row.PhysicalAddress[part];
        }

        return new PhysicalAddress(mac).ToString();
    }
}
