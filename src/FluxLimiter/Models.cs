namespace FluxLimiter;

public sealed class AppRule
{
    public string ExecutablePath { get; set; } = "";
    // KB/s = kilobytes per second (1024 bytes), NOT kilobits.
    public int? DownloadLimitKBps { get; set; }
    public int? UploadLimitKBps { get; set; }
    public bool Blocked { get; set; }
    public string? GroupId { get; set; }

    // Legacy JSON keys from initial scaffold: Kbps was ambiguous.
    [System.Text.Json.Serialization.JsonPropertyName("DownloadLimitKbps")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyDownloadLimit { get => null; set { if (value.HasValue && !DownloadLimitKBps.HasValue) DownloadLimitKBps = value; } }
    [System.Text.Json.Serialization.JsonPropertyName("UploadLimitKbps")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? LegacyUploadLimit { get => null; set { if (value.HasValue && !UploadLimitKBps.HasValue) UploadLimitKBps = value; } }
}

public sealed class ExecutableGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public int? DownloadLimitKBps { get; set; }
    public int? UploadLimitKBps { get; set; }
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 2;
    public List<AppRule> Rules { get; set; } = new();
    public List<ExecutableGroup> Groups { get; set; } = new();
    public bool StartWithWindows { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool TrayNoticeShown { get; set; }
}
