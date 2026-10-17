using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Beam.App;

public sealed record SetupAdapter(string Id, string Name, string Address, NetworkInterfaceType Kind, bool IsUp, bool HasGateway)
{
    public string Key => $"{Id}/{Address}";
    public string Label => $"{Name} / {Address}";
}

public static class NetworkSetup
{
    public static bool IsLanAddress(string address) => IPAddress.TryParse(address, out var ip)
        && ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)
        && ip.GetAddressBytes() is var bytes && bytes[0] is > 0 and < 224
        && !(bytes[0] == 169 && bytes[1] == 254);

    public static IReadOnlyList<SetupAdapter> Filter(IEnumerable<SetupAdapter> adapters) => adapters
        .Where(a => a.IsUp && IsLanAddress(a.Address))
        .OrderByDescending(a => a.HasGateway && a.Kind is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)
        .ThenByDescending(a => a.Kind == NetworkInterfaceType.Wireless80211)
        .ThenByDescending(a => a.HasGateway).ThenBy(a => a.Key, StringComparer.Ordinal)
        .DistinctBy(a => a.Key).ToArray();

    public static SetupAdapter? Select(IEnumerable<SetupAdapter> adapters, string? selectedKey)
    {
        var usable = Filter(adapters);
        return usable.FirstOrDefault(a => a.Key == selectedKey) ?? usable.FirstOrDefault();
    }

    public static IReadOnlyList<SetupAdapter> Discover()
    {
        var result = new List<SetupAdapter>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                try
                {
                    var properties = adapter.GetIPProperties();
                    var gateway = properties.GatewayAddresses.Any(a => IsLanAddress(a.Address.ToString()));
                    result.AddRange(properties.UnicastAddresses.Select(a => new SetupAdapter(adapter.Id, adapter.Name,
                        a.Address.ToString(), adapter.NetworkInterfaceType, true, gateway)));
                }
                catch (NetworkInformationException) { }
            }
        }
        catch (NetworkInformationException) { }
        return Filter(result);
    }
}
