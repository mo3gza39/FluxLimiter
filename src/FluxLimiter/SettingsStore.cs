using System.Text.Json;

namespace FluxLimiter;

public static class SettingsStore
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluxLimiter");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
            settings.Rules ??= new();
            settings.Groups ??= new();
            // Ignore invalid/duplicate paths and invalid limits, rather than applying ambiguous rules.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            settings.Rules = settings.Rules.Where(r =>
                !string.IsNullOrWhiteSpace(RuleCatalog.NormalizePath(r.ExecutablePath)) &&
                RuleCatalog.ValidLimit(r.DownloadLimitKBps) && RuleCatalog.ValidLimit(r.UploadLimitKBps) &&
                seen.Add(RuleCatalog.NormalizePath(r.ExecutablePath))).ToList();
            settings.Groups = settings.Groups.Where(g =>
                !string.IsNullOrWhiteSpace(g.Id) &&
                RuleCatalog.ValidLimit(g.DownloadLimitKBps) && RuleCatalog.ValidLimit(g.UploadLimitKBps))
                .GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Preserve the original file for manual recovery.
            return new();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Folder);
        string temp = FilePath + ".tmp";
        string backup = FilePath + ".bak";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        try
        {
            if (File.Exists(FilePath))
                File.Replace(temp, FilePath, backup);
            else
                File.Move(temp, FilePath);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
