using System.Collections;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FluxLimiter;

/// <summary>
/// Applies FluxLimiter block rules through the Windows Firewall policy store.
/// Windows Firewall is backed by Windows Filtering Platform. Rate limiting is intentionally
/// not implemented here; it requires the dedicated WFP callout driver.
/// </summary>
public static class FirewallEnforcer
{
    private const string Prefix = "FluxLimiter Block ";
    private const int DirectionIn = 1;
    private const int DirectionOut = 2;
    private const int ActionBlock = 0;
    private const int AllProfiles = int.MaxValue;

    public static void Synchronize(AppSettings settings)
    {
        if (!OperatingSystem.IsWindows()) return;

        Type policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2")
            ?? throw new PlatformNotSupportedException("Windows Firewall policy API is unavailable.");
        Type ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule")
            ?? throw new PlatformNotSupportedException("Windows Firewall rule API is unavailable.");

        dynamic policy = Activator.CreateInstance(policyType)
            ?? throw new InvalidOperationException("Unable to open Windows Firewall policy.");

        // Remove only rules owned by FluxLimiter. Rebuilding the small managed set makes
        // synchronization deterministic after crashes, path changes, and rule deletion.
        var owned = new List<string>();
        foreach (object item in (IEnumerable)policy.Rules)
        {
            dynamic existing = item;
            string name = existing.Name as string ?? "";
            if (name.StartsWith(Prefix, StringComparison.Ordinal))
                owned.Add(name);
            Marshal.FinalReleaseComObject(item);
        }

        foreach (string name in owned)
        {
            try { policy.Rules.Remove(name); }
            catch (COMException) { /* Rule may have been removed concurrently. */ }
        }

        foreach (AppRule appRule in settings.Rules.Where(r => r.Blocked))
        {
            string path = RuleCatalog.NormalizePath(appRule.ExecutablePath);
            if (path.Length == 0 || !File.Exists(path)) continue;
            AddRule(policy, ruleType, path, DirectionOut, "OUT");
            AddRule(policy, ruleType, path, DirectionIn, "IN");
        }

        Marshal.FinalReleaseComObject(policy);
    }

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
        finally
        {
            Marshal.FinalReleaseComObject(rule);
        }
    }

    private static string StableId(string path)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }
}
