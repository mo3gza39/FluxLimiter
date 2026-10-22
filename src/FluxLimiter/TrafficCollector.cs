using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace FluxLimiter;

// Measurement only. ETW never enforces bandwidth limits.
public sealed class TrafficCollector : IDisposable
{
    private const string Provider = "Microsoft-Windows-Kernel-Network";
    private readonly ConcurrentDictionary<int, Counters> totals = new();
    private readonly ConcurrentDictionary<int, Sample> samples = new();
    private TraceEventSession? session;
    private Task? worker;
    private int started;
    private bool disposed;

    public Task StartAsync()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return Task.CompletedTask;
        worker = Task.Run(Run);
        return Task.CompletedTask;
    }

    private void Run()
    {
        try
        {
            using var local = new TraceEventSession("FluxLimiter.Network." + Environment.ProcessId);
            session = local;
            local.StopOnDispose = true;
            local.EnableProvider(Provider, TraceEventLevel.Informational, ulong.MaxValue);
            local.Source.Dynamic.All += OnEvent;
            local.Source.Process();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // UI shows an em dash when ETW is unavailable.
        }
        finally { session = null; }
    }

    private void OnEvent(TraceEvent data)
    {
        if (data.ProcessID <= 0) return;
        string name = data.EventName ?? string.Empty;
        bool receive = name.Contains("Recv", StringComparison.OrdinalIgnoreCase) || name.Contains("Receive", StringComparison.OrdinalIgnoreCase);
        bool send = name.Contains("Send", StringComparison.OrdinalIgnoreCase);
        if (!receive && !send) return;
        long bytes = ReadSize(data);
        if (bytes <= 0) return;
        totals.AddOrUpdate(data.ProcessID,
            _ => receive ? new Counters(bytes, 0) : new Counters(0, bytes),
            (_, old) => receive ? old with { Received = old.Received + bytes } : old with { Sent = old.Sent + bytes });
    }

    private static long ReadSize(TraceEvent data)
    {
        foreach (string candidate in new[] { "size", "Size", "TransferSize", "PayloadSize" })
        {
            try
            {
                object? value = data.PayloadByName(candidate);
                if (value is null) continue;
                return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException or OverflowException) { }
        }
        return 0;
    }

    public (bool HasData, double DownloadKBps, double UploadKBps) GetRate(IEnumerable<int> pids)
    {
        double down = 0, up = 0;
        bool any = false;
        long now = Stopwatch.GetTimestamp();
        foreach (int pid in pids.Distinct())
        {
            if (!totals.TryGetValue(pid, out var current)) continue;
            any = true;
            var previous = samples.AddOrUpdate(pid, _ => new Sample(current.Received, current.Sent, now), (_, old) => old);
            double elapsed = (now - previous.Tick) / (double)Stopwatch.Frequency;
            if (elapsed > 0)
            {
                down += Math.Max(0, current.Received - previous.Received) / elapsed / 1024d;
                up += Math.Max(0, current.Sent - previous.Sent) / elapsed / 1024d;
                samples[pid] = new Sample(current.Received, current.Sent, now);
            }
        }
        return (any, down, up);
    }

    public (bool HasData, long TotalBytes) GetTotal(IEnumerable<int> pids)
    {
        long total = 0;
        bool any = false;
        foreach (int pid in pids.Distinct())
            if (totals.TryGetValue(pid, out var value)) { any = true; total += value.Received + value.Sent; }
        return (any, total);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { session?.Dispose(); } catch { }
        if (worker is not null) { try { worker.Wait(TimeSpan.FromSeconds(1)); } catch { } }
    }

    private readonly record struct Counters(long Received, long Sent);
    private readonly record struct Sample(long Received, long Sent, long Tick);
}
