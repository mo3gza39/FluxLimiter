using System.Globalization;
using System.Text.Json;

namespace FluxLimiter;

public static class DailyUsageStore
{
    public sealed class DayUsage
    {
        public string Date { get; set; } = "";
        public long ReceivedBytes { get; set; }
        public long SentBytes { get; set; }
    }

    public sealed class DailyUsageFile
    {
        public int SchemaVersion { get; set; } = 1;
        public List<DayUsage> Days { get; set; } = new();
    }

    private const int RetainedDays = 400;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DateKey(DateTime localDate) => localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static (long ReceivedBytes, long SentBytes) Load(string filePath, DateTime localDate)
    {
        try
        {
            if (!File.Exists(filePath)) return (0, 0);
            var file = JsonSerializer.Deserialize<DailyUsageFile>(File.ReadAllText(filePath));
            var day = file?.Days?.FirstOrDefault(d => d.Date == DateKey(localDate));
            return day is null ? (0, 0) : (day.ReceivedBytes, day.SentBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return (0, 0); }
    }

    public static (long ReceivedBytes, long SentBytes) LoadMonth(string filePath, DateTime anyDayInMonth)
    {
        try
        {
            if (!File.Exists(filePath)) return (0, 0);
            var file = JsonSerializer.Deserialize<DailyUsageFile>(File.ReadAllText(filePath));
            string prefix = anyDayInMonth.ToString("yyyy-MM-", CultureInfo.InvariantCulture);
            long rx = 0, tx = 0;
            foreach (var d in file?.Days ?? new List<DayUsage>())
            {
                if (d.Date is null || !d.Date.StartsWith(prefix, StringComparison.Ordinal)) continue;
                rx += Math.Max(0, d.ReceivedBytes);
                tx += Math.Max(0, d.SentBytes);
            }
            return (rx, tx);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return (0, 0); }
    }

    public static void Add(string filePath, DateTime localDate, long receivedDelta, long sentDelta)
    {
        if (receivedDelta < 0) receivedDelta = 0;
        if (sentDelta < 0) sentDelta = 0;
        if (receivedDelta == 0 && sentDelta == 0) return;
        try
        {
            var file = ReadFile(filePath);
            string key = DateKey(localDate);
            var day = file.Days.FirstOrDefault(d => d.Date == key);
            if (day is null) { day = new DayUsage { Date = key }; file.Days.Add(day); }
            day.ReceivedBytes += receivedDelta;
            day.SentBytes += sentDelta;
            file.Days = file.Days.OrderByDescending(d => d.Date, StringComparer.Ordinal).Take(RetainedDays).ToList();
            WriteAtomic(filePath, file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static DailyUsageFile ReadFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath)) return JsonSerializer.Deserialize<DailyUsageFile>(File.ReadAllText(filePath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new DailyUsageFile();
    }

    private static void WriteAtomic(string filePath, DailyUsageFile file)
    {
        string? folder = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        string temp = filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions));
        try
        {
            if (File.Exists(filePath)) File.Replace(temp, filePath, filePath + ".bak");
            else File.Move(temp, filePath);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
