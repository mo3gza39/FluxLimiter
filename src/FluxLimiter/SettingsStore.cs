using System.Text.Json;

namespace FluxLimiter;

public static class SettingsStore
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluxLimiter");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string? LastLoadWarning { get; private set; }

    public static AppSettings Load()
    {
        LastLoadWarning = null;
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
            // Never leave a corrupt primary file in place where a later Save could replace it.
            try
            {
                if (File.Exists(FilePath))
                {
                    string corrupt = FilePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                    File.Move(FilePath, corrupt, overwrite: false);
                    LastLoadWarning = "Settings could not be loaded. The original file was preserved as " + Path.GetFileName(corrupt) + ".";
                }
                else LastLoadWarning = "Settings could not be loaded: " + ex.Message;
            }
            catch (Exception preserveEx) when (preserveEx is IOException or UnauthorizedAccessException)
            {
                // Refuse silent destructive recovery if the original cannot be preserved.
                LastLoadWarning = "Settings could not be loaded or preserved. Saving is disabled for this session.";
            }
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
