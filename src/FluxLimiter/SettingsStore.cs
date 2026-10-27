using System.Text.Json;

namespace FluxLimiter;

public static class SettingsStore
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluxLimiter");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string? LastLoadWarning { get; private set; }
    public static bool LastLoadSucceeded { get; private set; }

    public static AppSettings Load()
    {
        LastLoadWarning = null; LastLoadSucceeded = true;
        if (!File.Exists(FilePath)) return new();
        try { return Normalize(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new()); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            string backup = FilePath + ".bak";
            try
            {
                if (File.Exists(backup))
                {
                    var recovered = Normalize(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(backup)) ?? throw new JsonException("Backup is empty."));
                    LastLoadWarning = "Primary settings were unreadable; recovered the last valid backup.";
                    return recovered;
                }
            }
            catch (Exception backupEx) when (backupEx is JsonException or IOException or UnauthorizedAccessException) { }
            LastLoadSucceeded = false;
            LastLoadWarning = "Settings could not be loaded safely; firewall synchronization and saving are disabled.";
            return new();
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.Rules ??= new(); settings.Groups ??= new();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        settings.Rules = settings.Rules.Where(r => r is not null &&
            !string.IsNullOrWhiteSpace(RuleCatalog.NormalizePath(r.ExecutablePath)) &&
            RuleCatalog.ValidLimit(r.DownloadLimitKBps) && RuleCatalog.ValidLimit(r.UploadLimitKBps) &&
            seen.Add(RuleCatalog.NormalizePath(r.ExecutablePath))).ToList();
        settings.Groups = settings.Groups.Where(g => g is not null && !string.IsNullOrWhiteSpace(g.Id) &&
            RuleCatalog.ValidLimit(g.DownloadLimitKBps) && RuleCatalog.ValidLimit(g.UploadLimitKBps))
            .GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Folder);
        string temp = FilePath + ".tmp", backup = FilePath + ".bak";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        try { if (File.Exists(FilePath)) File.Replace(temp, FilePath, backup); else File.Move(temp, FilePath); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}