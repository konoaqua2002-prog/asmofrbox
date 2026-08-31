namespace AsmoFrBoxTelegramBot;

public sealed class FirmwareEntry
{
    public int Id { get; set; }
    public string Brand { get; set; } = "";
    public string Project { get; set; } = "";
    public string Version { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Market { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string DownloadLink { get; set; } = "";
    public string ExtractionCode { get; set; } = "";

    public string Display =>
        string.IsNullOrWhiteSpace(Platform)
            ? $"{Brand} | {Project} | {Version} | {Market}"
            : $"{Brand} | {Project} | {Version} | {Platform} | {Market}";

    /// <summary>Short label for an inline keyboard button (Telegram truncates long ones anyway).</summary>
    public string ButtonLabel
    {
        get
        {
            var label = $"{Brand} {Project} — {Version}";
            return label.Length > 60 ? label[..57] + "..." : label;
        }
    }
}

public sealed class ShareFile
{
    public string FileId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long Size { get; set; }

    public string SizeText => FormatSize(Size);

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double n = bytes;
        int i = 0;
        while (n >= 1024 && i < units.Length - 1)
        {
            n /= 1024;
            i++;
        }
        return i == 0 ? $"{bytes} B" : $"{n:0.0} {units[i]}";
    }
}

public sealed class SearchResult
{
    public bool Ok { get; set; }
    public int Total { get; set; }
    public int Page { get; set; }
    public int Limit { get; set; }
    public List<FirmwareEntry> Items { get; set; } = new();

    public int TotalPages => Limit <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(Total / (double)Limit));
}

/// <summary>
/// Time-limited direct download URL for one firmware file.
/// Safe to hand to a Telegram user: no catalog host, no share password.
/// </summary>
public sealed class SecureDownloadLink
{
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public long Size { get; set; }
    public string Url { get; set; } = "";
    public DateTimeOffset? ExpiresAt { get; set; }
    public int ExpireSeconds { get; set; }

    public string SizeText => ShareFile.FormatSize(Size);

    public string ExpiresText =>
        ExpiresAt is null ? "unknown" : ExpiresAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
