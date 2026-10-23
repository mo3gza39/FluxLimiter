using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace FluxLimiter;

// Measurement only. ETW never enforces bandwidth limits.
// Runtime validation on Windows is required before these counters are treated as authoritative.
public sealed class TrafficCollector : IDisposable
{
    private const string Provider = "Microsoft-Windows-Kernel-Network";
    private static readonly string[] PidFields = { "PID", "Pid", "ProcessId", "ProcessID" };
    private static readonly string[] SizeFields = { "size", "Size", "TransferSize", "PayloadSize" };
    private readonly ConcurrentDictionary<int, Counters> totals = new();
    private readonly ConcurrentDictionary<int, Sample> samples = new();
    private readonly CancellationTokenSource stop = new();
    private TraceEventSession? session;
    private Task? worker;
    private int started;
    private bool disposed;

    public bool IsRunning => session is not null;
    public string Status => IsRunning ? "ETW running (validation pending)" : "ETW unavailable/retrying";

    public Task StartAsync()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return Task.CompletedTask;
        worker = Task.Run(Run);
        return Task.CompletedTask;
    }

    private void Run()
    {
        while (!stop.IsCancellationRequested)
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
                // Retry instead of permanently latching a failed first start.
            }
            finally { session = null; }

            if (!stop.IsCancellationRequested)
                stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
        }
    }

    private void OnEvent(TraceEvent data)
    {
        Direction direction = DirectionOf(data.EventName);
        if (direction == Direction.None) return;

        int pid = ReadPid(data);
        if (pid <= 0) return;
        long bytes = ReadLong(data, SizeFields);
        if (bytes <= 0) return;

        totals.AddOrUpdate(pid,
            _ => direction == Direction.Receive ? new Counters(bytes, 0) : new Counters(0, bytes),
            (_, old) => direction == Direction.Receive
                ? old with { Received = old.Received + bytes }
                : old with { Sent = old.Sent + bytes });
    }

    internal static Direction DirectionOf(string? eventName)
    {
        string name = eventName ?? "";
        int slash = name.LastIndexOf('/');
        string operation = slash >= 0 ? name[(slash + 1)..] : name;
        if (operation.Equals("Recv", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("Receive", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("RecvIPV4", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("RecvIPV6", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("ReceiveIPV4", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("ReceiveIPV6", StringComparison.OrdinalIgnoreCase) ||
            operation.EndsWith("RecvIPV4", StringComparison.OrdinalIgnoreCase) ||
            operation.EndsWith("RecvIPV6", StringComparison.OrdinalIgnoreCase))
            return Direction.Receive;
        if (operation.Equals("Send", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("SendIPV4", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("SendIPV6", StringComparison.OrdinalIgnoreCase) ||
            operation.EndsWith("SendIPV4", StringComparison.OrdinalIgnoreCase) ||
            operation.EndsWith("SendIPV6", StringComparison.OrdinalIgnoreCase))
            return Direction.Send;
        return Direction.None;
    }

    private static int ReadPid(TraceEvent data)
    {
        long payloadPid = ReadLong(data, PidFields);
        if (payloadPid is > 0 and <= int.MaxValue) return (int)payloadPid;
        // Do not attribute an event to the ETW header PID when payload attribution is absent.
        // Kernel providers can report System/zero in the header, which would corrupt per-app totals.
        return 0;
    }

    private static long ReadLong(TraceEvent data, IEnumerable<string> candidates)
    {
        foreach (string candidate in candidates)
        {
            try
            {
                object? value = data.PayloadByName(candidate);
                if (value is not null) return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException or OverflowException) { }
        }
        return 0;
    }

    public void Prune(IEnumerable<int> livePids)
    {
        var live = livePids.ToHashSet();
        foreach (int pid in totals.Keys) if (!live.Contains(pid)) totals.TryRemove(pid, out _);
        foreach (int pid in samples.Keys) if (!live.Contains(pid)) samples.TryRemove(pid, out _);
    }

    public (bool HasData, double DownloadKBps, double UploadKBps) GetRate(IEnumerable<int> pids)
    {
        double down = 0, up = 0; bool any = false; long now = Stopwatch.GetTimestamp();
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
        long total = 0; bool any = false;
        foreach (int pid in pids.Distinct())
            if (totals.TryGetValue(pid, out var value)) { any = true; total += value.Received + value.Sent; }
        return (any, total);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stop.Cancel();
        try { session?.Dispose(); } catch { }
        if (worker is not null) { try { worker.Wait(TimeSpan.FromSeconds(1)); } catch { } }
        stop.Dispose();
    }

    internal enum Direction { None, Receive, Send }
    private readonly record struct Counters(long Received, long Sent);
    private readonly record struct Sample(long Received, long Sent, long Tick);
}
