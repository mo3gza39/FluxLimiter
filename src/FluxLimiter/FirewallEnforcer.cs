using System.Collections;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FluxLimiter;

/// <summary>Synchronizes FluxLimiter-owned block rules without disturbing unrelated firewall rules.</summary>
public static class FirewallEnforcer
{
    private const string Prefix = "FluxLimiter Block ";
    private const int DirectionIn = 1, DirectionOut = 2, ActionBlock = 0, AllProfiles = int.MaxValue;

    public static void Synchronize(AppSettings settings)
    {
        if (!OperatingSystem.IsWindows()) return;
        Type policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
            ?? throw new PlatformNotSupportedException("Windows Firewall policy API is unavailable.");
        Type ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule")
            ?? throw new PlatformNotSupportedException("Windows Firewall rule API is unavailable.");
        dynamic policy = Activator.CreateInstance(policyType)
            ?? throw new InvalidOperationException("Unable to open Windows Firewall policy.");

        try
        {
            var expected = settings.Rules.Where(r => r.Blocked)
                .Select(r => RuleCatalog.NormalizePath(r.ExecutablePath))
                .Where(p => p.Length > 0 && File.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(p => new[] { Expected(p, DirectionOut, "OUT"), Expected(p, DirectionIn, "IN") })
                .ToDictionary(x => x.Name, StringComparer.Ordinal);

            var existing = new HashSet<string>(StringComparer.Ordinal);
            var obsolete = new List<string>();
            foreach (object item in (IEnumerable)policy.Rules)
            {
                dynamic rule = item;
                try
                {
                    string name = rule.Name as string ?? "";
                    if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                    existing.Add(name);
                    if (!expected.ContainsKey(name)) obsolete.Add(name);
                }
                finally { Marshal.FinalReleaseComObject(item); }
            }

            foreach (string name in obsolete)
            {
                try { policy.Rules.Remove(name); }
                catch (COMException) { }
            }

            foreach (var entry in expected.Values)
                if (!existing.Contains(entry.Name))
                    AddRule(policy, ruleType, entry.Path, entry.Direction, entry.Suffix);
        }
        finally { Marshal.FinalReleaseComObject(policy); }
    }

    private static ExpectedRule Expected(string path, int direction, string suffix) =>
        new(Prefix + StableId(path) + " " + suffix, path, direction, suffix);

    private static void AddRule(dynamic policy, Type ruleType, string path, int direction, string suffix)
    {
        dynamic rule = Activator.CreateInstance(ruleType)
            ?? throw new InvalidOperationException("Unable to create Windows Firewall rule.");
        try
        {
            rule.Name = Prefix + StableId(path) + " " + suffix;
            rule.Description = "Managed by FluxLimiter. Remove the block in FluxLimiter instead of editing this rule.";
            rule.ApplicationName = path;
            rule.Direction = direction;
            rule.Action = ActionBlock;
            rule.Enabled = true;
            rule.Profiles = AllProfiles;
            rule.InterfaceTypes = "All";
            policy.Rules.Add(rule);
        }
        finally { Marshal.FinalReleaseComObject(rule); }
    }

    private static string StableId(string path)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(RuleCatalog.NormalizePath(path).ToUpperInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private readonly record struct ExpectedRule(string Name, string Path, int Direction, string Suffix);
}
