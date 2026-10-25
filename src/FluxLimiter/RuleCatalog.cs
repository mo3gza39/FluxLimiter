namespace FluxLimiter;

// Configuration model only. Network enforcement is NOT implemented in this prototype.
public static class RuleCatalog
{
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path.Trim().Trim('"')); }
        catch (ArgumentException) { return ""; }
        catch (NotSupportedException) { return ""; }
        catch (PathTooLongException) { return ""; }
    }

    public static AppRule? Find(AppSettings settings, string executablePath)
    {
        string normalized = NormalizePath(executablePath);
        if (normalized.Length == 0) return null;
        return settings.Rules.FirstOrDefault(r =>
            string.Equals(NormalizePath(r.ExecutablePath), normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static (int? DownKBps, int? UpKBps, bool Blocked, string Scope) Resolve(AppSettings settings, string executablePath)
    {
        var rule = Find(settings, executablePath);
        if (rule is null) return (null, null, false, "Unmanaged");
        var group = settings.Groups.FirstOrDefault(g => !string.IsNullOrWhiteSpace(rule.GroupId) &&
            string.Equals(g.Id, rule.GroupId, StringComparison.OrdinalIgnoreCase));
        return group is null
            ? (rule.DownloadLimitKBps, rule.UploadLimitKBps, rule.Blocked, "Executable")
            : (group.DownloadLimitKBps, group.UploadLimitKBps, rule.Blocked, "Group: " + group.Name);
    }

    public static bool ValidLimit(int? value) => value is null || value > 0;

    public static string ProcessKey(int pid, string path) => pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + NormalizePath(path);
}
