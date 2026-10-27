using System.Globalization;
using System.Text.Json;

namespace FluxLimiter;

public static class DailyUsageStore
{
    public sealed class DayUsage { public string Date { get; set; } = ""; public long ReceivedBytes { get; set; } public long SentBytes { get; set; } }
    public sealed class DailyUsageFile { public int SchemaVersion { get; set; } = 1; public List<DayUsage> Days { get; set; } = new(); }
    private const int RetainedDays = 400;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string DateKey(DateTime localDate) => localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static (long ReceivedBytes, long SentBytes) Load(string filePath, DateTime localDate)
    {
        var file = TryRead(filePath); var day = file?.Days.FirstOrDefault(d => d.Date == DateKey(localDate));
        return day is null ? (0, 0) : (Math.Max(0, day.ReceivedBytes), Math.Max(0, day.SentBytes));
    }
    public static (long ReceivedBytes, long SentBytes) LoadMonth(string filePath, DateTime anyDayInMonth)
    {
        var file = TryRead(filePath); if (file is null) return (0, 0);
        string prefix = anyDayInMonth.ToString("yyyy-MM-", CultureInfo.InvariantCulture);
        return file.Days.Where(d => d.Date?.StartsWith(prefix, StringComparison.Ordinal) == true)
            .Aggregate((ReceivedBytes: 0L, SentBytes: 0L), (a,d) => (a.ReceivedBytes + Math.Max(0,d.ReceivedBytes), a.SentBytes + Math.Max(0,d.SentBytes)));
    }
    public static bool Add(string filePath, DateTime localDate, long receivedDelta, long sentDelta)
    {
        receivedDelta = Math.Max(0, receivedDelta); sentDelta = Math.Max(0, sentDelta);
        if (receivedDelta == 0 && sentDelta == 0) return true;
        try
        {
            var file = TryRead(filePath) ?? new DailyUsageFile();
            string key = DateKey(localDate); var day = file.Days.FirstOrDefault(d => d.Date == key);
            if (day is null) { day = new DayUsage { Date = key }; file.Days.Add(day); }
            checked { day.ReceivedBytes += receivedDelta; day.SentBytes += sentDelta; }
            file.Days = file.Days.OrderByDescending(d => d.Date, StringComparer.Ordinal).Take(RetainedDays).ToList();
            WriteAtomic(filePath, file); return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or OverflowException) { return false; }
    }
    private static DailyUsageFile? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return new DailyUsageFile();
            var file = JsonSerializer.Deserialize<DailyUsageFile>(File.ReadAllText(path));
            if (file?.Days is null || file.Days.Any(d => d is null || string.IsNullOrWhiteSpace(d.Date))) return null;
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    private static void WriteAtomic(string path, DailyUsageFile file)
    {
        string? folder = Path.GetDirectoryName(path); if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        string temp = path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions));
        try { if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}