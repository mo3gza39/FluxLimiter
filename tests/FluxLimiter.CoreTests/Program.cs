using FluxLimiter;

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}

var limiter = new SharedRateLimiter();
Assert(limiter.TryConsume("app", 1, 1024, 0), "Initial 1 KB burst");
Assert(!limiter.TryConsume("app", 1, 1, 0), "Burst exhausted");
Assert(limiter.TryConsume("app", 1, 512, .5), "Half-second refill");
Assert(!limiter.TryConsume("app", 1, 513, .5), "Budget enforced");
Assert(limiter.TryConsume("app", 1, 1024, 2), "Refill capped at one second");
Assert(!limiter.TryConsume("app", 1, 1, 2), "No accumulated unbounded burst");

var shared = new SharedRateLimiter();
Assert(shared.TryConsume("group:browser", 1, 700, 0), "Group member A");
Assert(!shared.TryConsume("group:browser", 1, 400, 0), "Group member B shares remaining budget");
Assert(shared.TryConsume("group:browser", 1, 324, 0), "Remaining group budget");
Assert(shared.TryConsume("other", 1, 1024, 0), "Independent identity");

var changed = new SharedRateLimiter();
Assert(changed.TryConsume("a", 10, 1000, 0), "Initial high cap");
Assert(!changed.TryConsume("a", 1, 1025, 0), "Lowering cap clamps burst");
Assert(changed.TryConsume("a", 1, 1024, 0), "Lowered cap accepted");

var model = new AppSettings();
var group = new ExecutableGroup { Id = "steam", Name = "Steam", DownloadLimitKBps = 500 };
model.Groups.Add(group);
model.Rules.Add(new AppRule { ExecutablePath = @"C:\Apps\steam.exe", GroupId = "steam" });
model.Rules.Add(new AppRule { ExecutablePath = @"C:\Apps\steamwebhelper.exe", GroupId = "steam" });
Assert(RuleCatalog.Resolve(model, @"C:\Apps\steam.exe").DownKBps == 500, "Grouped app A resolves cap");
Assert(RuleCatalog.Resolve(model, @"C:\Apps\steamwebhelper.exe").Scope == "Group: Steam", "Grouped app B resolves shared identity");
Assert(RuleCatalog.Resolve(model, @"C:\Apps\game.exe").Scope == "Unmanaged", "Unrelated executable not grouped");
var directions = new SharedRateLimiter();
var downKey = SharedRateLimiter.DirectionKey("group:steam", true);
var upKey = SharedRateLimiter.DirectionKey("group:steam", false);
Assert(downKey != upKey, "Upload and download use different identities");
Assert(directions.TryConsume(downKey, 1, 1024, 0), "Download bucket initial allowance");
Assert(directions.TryConsume(upKey, 1, 1024, 0), "Upload bucket unaffected by download");
Assert(!directions.TryConsume(downKey, 1, 1, 0), "Download budget exhausted independently");
Assert(directions.TryConsume(downKey, 1, 1024, 1), "Download refills after one second");
Assert(!directions.TryConsume(downKey, 1, 1, .5), "Backward time does not refill");

var pacer = new TrafficPacer();
Assert(Math.Abs(pacer.Schedule("browser", true, 1, 2048, 0) - 1) < 0.000001, "Oversized 2 KB request is paced, not permanently denied");
Assert(Math.Abs(pacer.Schedule("browser", true, 1, 1024, 0) - 2) < 0.000001, "Same-direction queued work is serialized");
Assert(Math.Abs(pacer.Schedule("browser", false, 1, 1024, 0)) < 0.000001, "Upload queue is independent");
Assert(Math.Abs(pacer.Schedule("other", true, 1, 1024, 0)) < 0.000001, "Other process queue is independent");
pacer.Clear("browser");
Assert(Math.Abs(pacer.Schedule("browser", true, 1, 1024, 0)) < 0.000001, "Clear resets directional budget");


Assert(RuleCatalog.ProcessKey(100, @"C:\Apps\browser.exe") != RuleCatalog.ProcessKey(101, @"C:\Apps\browser.exe"), "Same executable with different PIDs has distinct UI identity");
Assert(RuleCatalog.NormalizePath(@" C:\Apps\browser.exe ") == RuleCatalog.NormalizePath(@"C:\Apps\browser.exe"), "Rule paths normalize consistently");
Assert(!RuleCatalog.ValidLimit(0) && !RuleCatalog.ValidLimit(-1) && RuleCatalog.ValidLimit(null) && RuleCatalog.ValidLimit(1), "Limit validation rejects zero and negatives");

Assert(TrafficCollector.DirectionOf("TCPIP/Send") == TrafficCollector.Direction.Send, "ETW TCP send direction");
Assert(TrafficCollector.DirectionOf("UDPIP/Recv") == TrafficCollector.Direction.Receive, "ETW UDP receive direction");
Assert(TrafficCollector.DirectionOf("TCPIP/SendIPV4") == TrafficCollector.Direction.Send, "ETW IPv4 send direction");
Assert(TrafficCollector.DirectionOf("TcpIpRecvIPV6") == TrafficCollector.Direction.Receive, "ETW IPv6 receive direction");
Assert(TrafficCollector.DirectionOf("TCPIP/Connect") == TrafficCollector.Direction.None, "Unrelated ETW event ignored");
Assert(TrafficCollector.DirectionOf(10, null) == TrafficCollector.Direction.Send, "ETW event ID 10 maps to send");
Assert(TrafficCollector.DirectionOf(11, null) == TrafficCollector.Direction.Receive, "ETW event ID 11 maps to receive");
Assert(TrafficCollector.DirectionOf(58, "Unknown") == TrafficCollector.Direction.Send, "ETW event ID 58 maps to send");
Assert(TrafficCollector.DirectionOf(59, "Unknown") == TrafficCollector.Direction.Receive, "ETW event ID 59 maps to receive");
Assert(TrafficCollector.DirectionOf("TCPIP/Datareceived") == TrafficCollector.Direction.Receive, "ETW Datareceived variant");
Assert(TrafficCollector.DirectionOf("UDPIP/DatasentoverUDPprotocol.") == TrafficCollector.Direction.Send, "ETW UDP sent variant with trailing dot");


var dailyRoot = Path.Combine(Path.GetTempPath(), "FluxLimiter-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dailyRoot);
try
{
    var dailyPath = Path.Combine(dailyRoot, "daily.json");
    var today = new DateTime(2026, 10, 24);
    Assert(DailyUsageStore.Load(dailyPath, today) == (0L, 0L), "Missing daily file reads as zero usage");
    DailyUsageStore.Add(dailyPath, today, 5 * 1024 * 1024, 1024);
    DailyUsageStore.Add(dailyPath, today, 5 * 1024 * 1024, 2048);
    var totals = DailyUsageStore.Load(dailyPath, today);
    Assert(totals == (10L * 1024 * 1024, 3L * 1024), "Daily usage accumulates deltas");
    DailyUsageStore.Add(dailyPath, new DateTime(2026, 10, 25), 1024, 0);
    DailyUsageStore.Add(dailyPath, new DateTime(2026, 11, 1), 7777, 8888);
    var october = DailyUsageStore.LoadMonth(dailyPath, new DateTime(2026, 10, 1));
    Assert(october.ReceivedBytes == 10 * 1024 * 1024 + 1024 && october.SentBytes == 3 * 1024, "LoadMonth sums only the requested calendar month");
    Assert(DailyUsageStore.LoadMonth(dailyPath, new DateTime(2025, 10, 24)) == (0L, 0L), "LoadMonth does not mix the same month of another year");
}
finally { try { Directory.Delete(dailyRoot, true); } catch { } }

Console.WriteLine("All deterministic core tests passed. No OS network enforcement tested.");
