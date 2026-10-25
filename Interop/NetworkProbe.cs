using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DevShell.Interop;

internal enum NetworkKind
{
    None,
    Ethernet,
    Wifi,
    Other,
}

internal sealed record NetworkStatus(
    NetworkKind Kind,
    string Title,
    string Detail,
    long LinkSpeedBitsPerSecond,
    IReadOnlyList<string> Addresses,
    bool HasGateway,
    IReadOnlyList<AdapterSummary> Adapters);

internal sealed record AdapterSummary(
    string Name,
    string Description,
    string Kind,
    bool IsUp,
    long LinkSpeedBitsPerSecond,
    string Addresses)
{
    public string SpeedText => NetworkProbe.FormatSpeed(LinkSpeedBitsPerSecond);

    public string StateText => IsUp ? "Connected" : "Disconnected";

    public string Detail => string.Join(
        " · ",
        new[]
        {
            Kind,
            StateText,
            SpeedText,
            string.IsNullOrWhiteSpace(Addresses) ? "no IPv4" : Addresses,
        }.Where(part => !string.IsNullOrWhiteSpace(part)));
}

/// <summary>Reads the machine's connectivity using the managed network APIs.</summary>
internal static class NetworkProbe
{
    public static NetworkStatus Read()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(n => !IsFilterDriver(n))
                .ToArray();

            var summaries = new List<AdapterSummary>();
            foreach (var nic in interfaces)
            {
                summaries.Add(Summarize(nic));
            }

            var active = interfaces
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => !IsVirtualNoise(n))
                .OrderByDescending(HasGateway)
                .ThenByDescending(n => n.Speed)
                .FirstOrDefault();

            if (active is null)
            {
                return new NetworkStatus(
                    NetworkKind.None,
                    "Not connected",
                    "No active adapter",
                    0,
                    Array.Empty<string>(),
                    false,
                    summaries);
            }

            var kind = KindOf(active);
            var addresses = Addresses(active);

            return new NetworkStatus(
                kind,
                kind switch
                {
                    NetworkKind.Wifi => "Wi-Fi",
                    NetworkKind.Ethernet => "Ethernet",
                    _ => "Network",
                },
                active.Description,
                active.Speed,
                addresses,
                HasGateway(active),
                summaries);
        }
        catch (Exception ex)
        {
            App.Log("NetworkProbe.Read", ex);
            return new NetworkStatus(NetworkKind.None, "Network error", ex.Message, 0, Array.Empty<string>(), false, Array.Empty<AdapterSummary>());
        }
    }

    private static AdapterSummary Summarize(NetworkInterface nic)
    {
        return new AdapterSummary(
            nic.Name,
            nic.Description,
            KindToString(KindOf(nic)),
            nic.OperationalStatus == OperationalStatus.Up,
            nic.Speed,
            string.Join(", ", Addresses(nic)));
    }

    private static NetworkKind KindOf(NetworkInterface nic)
    {
        return nic.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => NetworkKind.Wifi,
            NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.Ethernet3Megabit
                or NetworkInterfaceType.FastEthernetFx
                or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.GigabitEthernet => NetworkKind.Ethernet,
            _ => NetworkKind.Other,
        };
    }

    /// <summary>
    /// NDIS filter drivers — the WFP lightweight filters and the QoS packet scheduler — show up in
    /// the managed adapter list as if they were network cards, each named after the card it is
    /// stacked on ("Ethernet0-WFP Native MAC Layer Lightweight Filter-0000"). They carry no
    /// address and are not something anyone can plug a cable into, so they never belong in a list
    /// of adapters and they must never be picked as the active link.
    /// </summary>
    private static bool IsFilterDriver(NetworkInterface nic)
    {
        var text = (nic.Name + " " + nic.Description).ToLowerInvariant();
        return text.Contains("lightweight filter") || text.Contains("packet scheduler")
            || text.Contains("native mac layer") || text.Contains("802.3 mac layer")
            || text.Contains("wfplwfilter") || text.Contains("ndisuio")
            || text.Contains("wan miniport") || text.Contains("kernel debug network adapter");
    }

    /// <summary>Hyper-V / WSL / virtual machine and other adapters that are up but rarely the real link.</summary>
    private static bool IsVirtualNoise(NetworkInterface nic)
    {
        var text = (nic.Name + " " + nic.Description).ToLowerInvariant();
        return text.Contains("vethernet") || text.Contains("hyper-v") || text.Contains("wsl")
            || text.Contains("virtualbox") || text.Contains("vmware") || text.Contains("loopback")
            || text.Contains("bluetooth") || text.Contains("wi-fi direct");
    }

    private static bool HasGateway(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GatewayAddresses
                .Any(g => g.Address is not null && !g.Address.Equals(IPAddress.Any));
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string> Addresses(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static string KindToString(NetworkKind kind) => kind switch
    {
        NetworkKind.Wifi => "Wi-Fi",
        NetworkKind.Ethernet => "Ethernet",
        NetworkKind.Other => "Other",
        _ => "None",
    };

    public static string FormatSpeed(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
        {
            return "—";
        }

        double mbps = bitsPerSecond / 1_000_000d;
        return mbps >= 1000 ? $"{mbps / 1000:0.##} Gbps" : $"{mbps:0.##} Mbps";
    }
}
