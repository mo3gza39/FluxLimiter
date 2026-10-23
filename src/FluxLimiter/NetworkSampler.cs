using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FluxLimiter;

// Device-level throughput only. This is a UI summary, not per-app accounting.
public sealed class NetworkSampler
{
    private string? selectedAdapterId;
    private long previousReceived;
    private long previousSent;
    private long lastTick;

    public (double DownloadKBps, double UploadKBps) Sample()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = lastTick == 0 ? 0 : (now - lastTick) / (double)Stopwatch.Frequency;
        var adapter = SelectInternetAdapter();
        if (adapter is null) { Reset(now); return (0, 0); }

        try
        {
            // GetIPStatistics exposes the platform's current IP counters and avoids the legacy
            // IPv4-only selection used by the first prototype.
            var stats = adapter.GetIPStatistics();
            if (!string.Equals(selectedAdapterId, adapter.Id, StringComparison.OrdinalIgnoreCase) || elapsed <= 0)
            {
                selectedAdapterId = adapter.Id;
                previousReceived = stats.BytesReceived;
                previousSent = stats.BytesSent;
                lastTick = now;
                return (0, 0);
            }

            long received = stats.BytesReceived - previousReceived;
            long sent = stats.BytesSent - previousSent;
            previousReceived = stats.BytesReceived;
            previousSent = stats.BytesSent;
            lastTick = now;
            if (received < 0 || sent < 0) return (0, 0);
            return (received / elapsed / 1024d, sent / elapsed / 1024d);
        }
        catch (NetworkInformationException) { Reset(now); return (0, 0); }
    }

    private static NetworkInterface? SelectInternetAdapter()
    {
        // Prefer an operational interface carrying a default route. Do not rank by link speed:
        // virtual/Hyper-V adapters commonly advertise 10+ Gbit and would win incorrectly.
        // Route metric selection is left to the Windows integration layer; this fallback prefers
        // physical Wi-Fi/Ethernet over virtual/unknown interfaces and supports IPv4 or IPv6 gateways.
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(a => a.OperationalStatus == OperationalStatus.Up &&
                        a.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        a.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .Select(a => (Adapter: a, HasGateway: HasDefaultGateway(a), Rank: PhysicalRank(a)))
            .Where(x => x.HasGateway)
            .OrderBy(x => x.Rank)
            .Select(x => x.Adapter)
            .FirstOrDefault();
    }

    private static bool HasDefaultGateway(NetworkInterface adapter)
    {
        try
        {
            return adapter.GetIPProperties().GatewayAddresses.Any(g =>
                !g.Address.Equals(System.Net.IPAddress.Any) &&
                !g.Address.Equals(System.Net.IPAddress.IPv6Any));
        }
        catch (NetworkInformationException) { return false; }
    }

    private static int PhysicalRank(NetworkInterface adapter) => adapter.NetworkInterfaceType switch
    {
        NetworkInterfaceType.Wireless80211 => 0,
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT => 1,
        _ => 2
    };

    private void Reset(long now)
    {
        selectedAdapterId = null;
        previousReceived = previousSent = 0;
        lastTick = now;
    }
}
