using System.Diagnostics;
using System.Net.NetworkInformation;

namespace FluxLimiter;

// Device-level Internet-facing throughput only. Does not attribute traffic to applications.
// Samples one active interface with an IPv4 default gateway instead of summing physical
// and virtual interfaces, which can count the same traffic more than once.
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
        if (adapter is null)
        {
            Reset(now);
            return (0, 0);
        }

        try
        {
            var stats = adapter.GetIPv4Statistics();
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

            if (received < 0 || sent < 0) return (0, 0); // interface reset/reconnect
            return (received / elapsed / 1024.0, sent / elapsed / 1024.0);
        }
        catch (NetworkInformationException)
        {
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
            .Select(a =>
            {
                try
                {
                    var p = a.GetIPProperties();
                    bool hasIpv4Gateway = p.GatewayAddresses.Any(g =>
                        g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        !g.Address.Equals(System.Net.IPAddress.Any));
                    return (Adapter: a, HasGateway: hasIpv4Gateway);
                }
                catch (NetworkInformationException)
                {
                    return (Adapter: a, HasGateway: false);
                }
            })
            .Where(x => x.HasGateway)
            .OrderByDescending(x => x.Adapter.Speed)
            .Select(x => x.Adapter)
            .FirstOrDefault();
    }

    private void Reset(long now)
    {
        selectedAdapterId = null;
        previousReceived = 0;
        previousSent = 0;
        lastTick = now;
    }
}
