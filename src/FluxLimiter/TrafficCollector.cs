using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace FluxLimiter;

public sealed class TrafficCollector : IDisposable
{
    private const string Provider = "Microsoft-Windows-Kernel-Network";
    private const string SessionName = "FluxLimiter.Network.Session";
    private const ulong AllKeywords = unchecked((ulong)-1);
    private static readonly string[] PidFields = { "PID", "Pid", "ProcessId", "ProcessID", "ProcessId", "IssuingThreadId" };
    private static readonly string[] SizeFields = { "size", "Size", "TransferSize", "PayloadSize" };
    private readonly ConcurrentDictionary<int, Counters> totals = new();
    private readonly ConcurrentDictionary<int, Sample> samples = new();
    private readonly ConcurrentDictionary<int, (double DownKBps, double UpKBps)> latestRates = new();
    private readonly ConcurrentDictionary<int, long> generations = new();
    private readonly CancellationTokenSource stop = new();
    private TraceEventSession? session;
    private Task? worker;
    private int started;
    private bool disposed;
    private long acceptedEvents, ignoredEvents, missingPidEvents, missingSizeEvents, totalSessionReceived, totalSessionSent;
    private string lastIgnoredName = "", lastSessionError = "";

    public bool IsRunning => session is not null;
    public (long ReceivedBytes, long SentBytes) SessionTotals => (Interlocked.Read(ref totalSessionReceived), Interlocked.Read(ref totalSessionSent));

    public string Status
    {
        get
        {
            long accepted = Interlocked.Read(ref acceptedEvents), ignored = Interlocked.Read(ref ignoredEvents), missingPid = Interlocked.Read(ref missingPidEvents), missingSize = Interlocked.Read(ref missingSizeEvents);
            string state = session is not null ? "ETW running" : "ETW unavailable/retrying";
            string text = state + " · accepted " + accepted.ToString(CultureInfo.InvariantCulture) + " · ignored " + ignored.ToString(CultureInfo.InvariantCulture)
                + " · no-pid " + missingPid.ToString(CultureInfo.InvariantCulture) + " · no-size " + missingSize.ToString(CultureInfo.InvariantCulture);
            if (lastIgnoredName.Length > 0) text += " · last ignored " + lastIgnoredName;
            if (session is null && lastSessionError.Length > 0) text += " · " + lastSessionError;
            return text;
        }
    }

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
                using var local = new TraceEventSession(SessionName);
                session = local; local.StopOnDispose = true;
                local.EnableProvider(Provider, TraceEventLevel.Informational, AllKeywords);
                local.Source.Dynamic.All += OnEvent;
                local.Source.Process();
                lastSessionError = "";
            }
            catch (Exception ex) when (!stop.IsCancellationRequested) { lastSessionError = Trim(ex.GetType().Name + ": " + ex.Message, 160); }
            finally { session = null; }
            if (!stop.IsCancellationRequested) stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
        }
    }

    private void OnEvent(TraceEvent data)
    {
        int eventId = (int)data.ID;
        Direction direction = DirectionOf(eventId, data.EventName);
        if (direction == Direction.None) { Interlocked.Increment(ref ignoredEvents); lastIgnoredName = Trim(string.IsNullOrEmpty(data.EventName) ? "EventID(" + eventId.ToString(CultureInfo.InvariantCulture) + ")" : data.EventName, 80); return; }
        int pid = ReadPid(data);
        if (pid <= 0) { Interlocked.Increment(ref missingPidEvents); return; }
        long bytes = ReadLong(data, SizeFields);
        if (bytes <= 0) { Interlocked.Increment(ref missingSizeEvents); return; }
        Interlocked.Increment(ref acceptedEvents);
        if (direction == Direction.Receive) Interlocked.Add(ref totalSessionReceived, bytes); else Interlocked.Add(ref totalSessionSent, bytes);
        totals.AddOrUpdate(pid, _ => direction == Direction.Receive ? new Counters(bytes, 0) : new Counters(0, bytes),
            (_, old) => direction == Direction.Receive ? old with { Received = old.Received + bytes } : old with { Sent = old.Sent + bytes });
    }

    public void TickRateWindow()
    {
        long now = Stopwatch.GetTimestamp();
        foreach (var pair in totals)
        {
            int pid = pair.Key; var current = pair.Value;
            if (!samples.TryGetValue(pid, out var previous)) { samples[pid] = new Sample(current.Received, current.Sent, now); latestRates[pid] = (0, 0); continue; }
            double elapsed = (now - previous.Tick) / (double)Stopwatch.Frequency;
            if (elapsed >= 0.25)
            {
                double down = Math.Max(0, current.Received - previous.Received) / elapsed / 1024d;
                double up = Math.Max(0, current.Sent - previous.Sent) / elapsed / 1024d;
                samples[pid] = new Sample(current.Received, current.Sent, now); latestRates[pid] = (down, up);
            }
        }
    }

    internal static Direction DirectionOf(string? eventName) => DirectionOf(0, eventName);
    internal static Direction DirectionOf(int eventId, string? eventName)
    {
        switch (eventId) { case 10: case 26: case 42: case 58: return Direction.Send; case 11: case 27: case 43: case 59: return Direction.Receive; }
        string operation = OperationOf(eventName);
        if (IsReceive(operation)) return Direction.Receive;
        if (IsSend(operation)) return Direction.Send;
        return Direction.None;
    }

    private static string OperationOf(string? eventName)
    {
        string name = eventName ?? ""; int slash = name.LastIndexOf('/'); string operation = slash >= 0 ? name[(slash + 1)..] : name;
        return operation.Trim().TrimEnd('.');
    }

    private static bool IsReceive(string operation) =>
        operation.Equals("Recv", StringComparison.OrdinalIgnoreCase) || operation.Equals("Receive", StringComparison.OrdinalIgnoreCase) ||
        operation.Equals("Datareceived", StringComparison.OrdinalIgnoreCase) || operation.Equals("DatareceivedoverUDPprotocol", StringComparison.OrdinalIgnoreCase) ||
        operation.Equals("RecvIPV4", StringComparison.OrdinalIgnoreCase) || operation.Equals("RecvIPV6", StringComparison.OrdinalIgnoreCase) ||
        operation.Equals("ReceiveIPV4", StringComparison.OrdinalIgnoreCase) || operation.Equals("ReceiveIPV6", StringComparison.OrdinalIgnoreCase) ||
        operation.EndsWith("RecvIPV4", StringComparison.OrdinalIgnoreCase) || operation.EndsWith("RecvIPV6", StringComparison.OrdinalIgnoreCase);

    private static bool IsSend(string operation) =>
        operation.Equals("Send", StringComparison.OrdinalIgnoreCase) || operation.Equals("Datasent", StringComparison.OrdinalIgnoreCase) ||
        operation.Equals("DatasentoverUDPprotocol", StringComparison.OrdinalIgnoreCase) || operation.Equals("SendIPV4", StringComparison.OrdinalIgnoreCase) ||
        operation.Equals("SendIPV6", StringComparison.OrdinalIgnoreCase) || operation.EndsWith("SendIPV4", StringComparison.OrdinalIgnoreCase) ||
        operation.EndsWith("SendIPV6", StringComparison.OrdinalIgnoreCase);

    private static int ReadPid(TraceEvent data)
    {
        long payloadPid = ReadLong(data, PidFields);
        if (payloadPid is > 0 and <= int.MaxValue) return (int)payloadPid;
        int eventPid = data.ProcessID;
        return eventPid > 0 ? eventPid : 0;
    }

    private static long ReadLong(TraceEvent data, IEnumerable<string> candidates)
    {
        foreach (string candidate in candidates)
        {
            try { object? value = data.PayloadByName(candidate); if (value is not null) return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException or OverflowException) { }
        }
        return 0;
    }

    public void Prune(IEnumerable<(int Pid, long StartTicks)> liveProcesses)
    {
        var live = new Dictionary<int, long>();
        foreach (var (pid, start) in liveProcesses) if (pid > 0) live[pid] = start;
        foreach (int pid in totals.Keys)
        {
            if (!live.TryGetValue(pid, out long start))
            {
                totals.TryRemove(pid, out _); samples.TryRemove(pid, out _); latestRates.TryRemove(pid, out _); generations.TryRemove(pid, out _); continue;
            }
            if (start != 0 && generations.TryGetValue(pid, out long known) && known != 0 && known != start)
            { totals.TryRemove(pid, out _); samples.TryRemove(pid, out _); latestRates.TryRemove(pid, out _); }
            if (start != 0) generations[pid] = start;
        }
        foreach (int pid in samples.Keys) if (!live.ContainsKey(pid)) { samples.TryRemove(pid, out _); latestRates.TryRemove(pid, out _); }
    }

    public (bool HasData, double DownloadKBps, double UploadKBps) GetRate(IEnumerable<int> pids)
    {
        double down = 0, up = 0; bool any = false; long now = Stopwatch.GetTimestamp();
        foreach (int pid in pids.Distinct())
        {
            if (!totals.TryGetValue(pid, out var current)) continue;
            any = true;
            if (latestRates.TryGetValue(pid, out var cached)) { down += cached.DownKBps; up += cached.UpKBps; continue; }
            var previous = samples.AddOrUpdate(pid, _ => new Sample(current.Received, current.Sent, now), (_, old) => old);
            double elapsed = (now - previous.Tick) / (double)Stopwatch.Frequency;
            if (elapsed > 0) { down += Math.Max(0, current.Received - previous.Received) / elapsed / 1024d; up += Math.Max(0, current.Sent - previous.Sent) / elapsed / 1024d; samples[pid] = new Sample(current.Received, current.Sent, now); }
        }
        return (any, down, up);
    }

    public (bool HasData, long TotalBytes) GetTotal(IEnumerable<int> pids)
    {
        long total = 0; bool any = false;
        foreach (int pid in pids.Distinct()) if (totals.TryGetValue(pid, out var value)) { any = true; total += value.Received + value.Sent; }
        return (any, total);
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true; stop.Cancel();
        try { session?.Dispose(); } catch { }
        if (worker is not null) { try { worker.Wait(TimeSpan.FromSeconds(1)); } catch { } }
        stop.Dispose();
    }

    private static string Trim(string value, int max)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= max ? value : value[..max];
    }

    internal enum Direction { None, Receive, Send }
    private readonly record struct Counters(long Received, long Sent);
    private readonly record struct Sample(long Received, long Sent, long Tick);
}
