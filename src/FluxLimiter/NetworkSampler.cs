using System.Diagnostics;
using System.Net.NetworkInformation;

namespace FluxLimiter;

// Device-level adapter throughput only. Does not attribute traffic to applications.
public sealed class NetworkSampler
{
    private readonly Dictionary<string, (long Received, long Sent)> previous = new();
    private long lastTick;
    public (double DownloadKBps, double UploadKBps) Sample()
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = lastTick == 0 ? 0 : (now - lastTick) / (double)Stopwatch.Frequency;
        double down = 0, up = 0;
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
            try
            {
                var stats = adapter.GetIPv4Statistics();
                string id = adapter.Id;
                live.Add(id);
                if (elapsed > 0 && previous.TryGetValue(id, out var old))
                {
                    long received = stats.BytesReceived - old.Received;
                    long sent = stats.BytesSent - old.Sent;
                    if (received >= 0) down += received;
                    if (sent >= 0) up += sent;
                }
                previous[id] = (stats.BytesReceived, stats.BytesSent);
            }
            catch (NetworkInformationException) { }
        }
        foreach (string id in previous.Keys.Where(k => !live.Contains(k)).ToArray()) previous.Remove(id);
        lastTick = now;
        return elapsed > 0 ? (down / elapsed / 1024.0, up / elapsed / 1024.0) : (0, 0);
    }
}
