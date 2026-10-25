using System.Diagnostics;
using System.Net.NetworkInformation;

namespace FluxLimiter;

// Device-level throughput only. This is a UI summary, not per-app accounting.
public sealed class NetworkSampler
{
    private string? selectedAdapterId;
    private long previousReceived;
    private long previousSent;
    private long lastTick;

    public string CurrentAdapterName { get; private set; } = "Detecting...";
    public bool IsConnected { get; private set; }

    public (double DownloadKBps, double UploadKBps) Sample()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = lastTick == 0 ? 0 : (now - lastTick) / (double)Stopwatch.Frequency;
        var adapter = SelectInternetAdapter();
        if (adapter is null)
        {
            CurrentAdapterName = "No active network";
            IsConnected = false;
            Reset(now);
            return (0, 0);
        }

        CurrentAdapterName = string.IsNullOrWhiteSpace(adapter.Name) ? adapter.Description : adapter.Name;
        IsConnected = true;

        try
        {
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
        catch (NetworkInformationException)
        {
            IsConnected = false;
            Reset(now);
            return (0, 0);
        }
    }

    private static NetworkInterface? SelectInternetAdapter()
    {
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
