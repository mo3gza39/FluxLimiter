namespace FluxLimiter;

public sealed class AppRule
{
    public string ExecutablePath { get; set; } = "";
    public int? DownloadLimitKbps { get; set; }
    public int? UploadLimitKbps { get; set; }
    public bool Blocked { get; set; }
}

public sealed class AppSettings
{
    public List<AppRule> Rules { get; set; } = new();
    public bool StartWithWindows { get; set; }
}
