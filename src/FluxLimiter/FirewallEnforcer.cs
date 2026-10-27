using System.Collections;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FluxLimiter;

public static class FirewallEnforcer
{
    private const string Prefix = "FluxLimiter Block ";
    private const int DirectionIn = 1, DirectionOut = 2, ActionBlock = 0, AllProfiles = int.MaxValue;
    private static readonly object Sync = new();

    public static void Synchronize(AppSettings settings)
    {
        lock (Sync)
        {
            if (!OperatingSystem.IsWindows()) return;
            Type policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2") ?? throw new PlatformNotSupportedException();
            Type ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule") ?? throw new PlatformNotSupportedException();
            dynamic policy = Activator.CreateInstance(policyType) ?? throw new InvalidOperationException();
            dynamic? rulesCollection = null;
            try
            {
                rulesCollection = policy.Rules;
                var expected = settings.Rules.Where(r => r is not null && r.Blocked)
                    .Select(r => RuleCatalog.NormalizePath(r.ExecutablePath)).Where(p => p.Length > 0 && File.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase)
                    .SelectMany(p => new[] { Expected(p, DirectionOut, "OUT"), Expected(p, DirectionIn, "IN") }).ToDictionary(x => x.Name, StringComparer.Ordinal);
                var existing = new HashSet<string>(StringComparer.Ordinal); var obsolete = new List<string>();
                foreach (object item in (IEnumerable)rulesCollection)
                {
                    dynamic rule = item;
                    try
                    {
                        string name = rule.Name as string ?? ""; if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                        if (expected.TryGetValue(name, out var wanted) && IsCorrect(rule, wanted)) existing.Add(name); else obsolete.Add(name);
                    }
                    finally { if (Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item); }
                }
                foreach (string name in obsolete) rulesCollection.Remove(name);
                foreach (var entry in expected.Values) if (!existing.Contains(entry.Name)) AddRule(rulesCollection, ruleType, entry);
            }
            finally { if (rulesCollection is not null && Marshal.IsComObject(rulesCollection)) Marshal.FinalReleaseComObject(rulesCollection); if (Marshal.IsComObject(policy)) Marshal.FinalReleaseComObject(policy); }
        }
    }
    private static bool IsCorrect(dynamic rule, ExpectedRule wanted) =>
        string.Equals((string?)rule.ApplicationName, wanted.Path, StringComparison.OrdinalIgnoreCase) &&
        (int)rule.Direction == wanted.Direction && (int)rule.Action == ActionBlock && (bool)rule.Enabled;
    private static ExpectedRule Expected(string path, int direction, string suffix) => new(Prefix + StableId(path) + " " + suffix, path, direction, suffix);
    private static void AddRule(dynamic rules, Type type, ExpectedRule e)
    {
        dynamic rule = Activator.CreateInstance(type) ?? throw new InvalidOperationException();
        try { rule.Name=e.Name; rule.Description="Managed by FluxLimiter."; rule.ApplicationName=e.Path; rule.Direction=e.Direction; rule.Action=ActionBlock; rule.Enabled=true; rule.Profiles=AllProfiles; rule.InterfaceTypes="All"; rules.Add(rule); }
        finally { if (Marshal.IsComObject(rule)) Marshal.FinalReleaseComObject(rule); }
    }
    private static string StableId(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RuleCatalog.NormalizePath(path).ToUpperInvariant())).AsSpan(0,8));
    private readonly record struct ExpectedRule(string Name,string Path,int Direction,string Suffix);
}