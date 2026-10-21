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
Console.WriteLine("All deterministic core tests passed. No OS network enforcement tested.");
