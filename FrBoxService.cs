using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AsmoFrBoxTelegramBot;

/// <summary>
/// Downloads / lists files from Transsion FRBox (Aliyun PDS) share links.
/// Unchanged from the original WinForms app — only the namespace moved.
/// </summary>
public sealed class FrBoxService : IDisposable
{
    private const string PdsApi = "https://fra315.api.aliyunpds.com";
    private readonly HttpClient _http;

    public FrBoxService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://frbox.transsion.com/");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://frbox.transsion.com");
    }

    public static string ParseShareId(string link)
    {
        var m = Regex.Match(link ?? "", @"/disk/s/([A-Za-z0-9_-]+)");
        if (!m.Success)
            throw new InvalidOperationException("Invalid firmware share link.");
        return m.Groups[1].Value;
    }

    public async Task<string> GetShareTokenAsync(string shareId, string password, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { share_id = shareId, share_pwd = password ?? "" });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync($"{PdsApi}/v2/share_link/get_share_token", content, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("share_token", out var tok))
            throw new InvalidOperationException("Could not unlock firmware share (bad extraction code?).");
        return tok.GetString() ?? throw new InvalidOperationException("Empty share token.");
    }

    public async Task<List<ShareFile>> ListAllFilesAsync(
        string shareId,
        string shareToken,
        CancellationToken ct = default)
    {
        var result = new List<ShareFile>();
        await WalkAsync(shareId, shareToken, "root", "", result, ct).ConfigureAwait(false);
        return result;
    }

    private async Task WalkAsync(
        string shareId,
        string shareToken,
        string parent,
        string prefix,
        List<ShareFile> result,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            share_id = shareId,
            parent_file_id = parent,
            limit = 200,
            order_by = "name",
            order_direction = "ASC",
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{PdsApi}/v2/file/list");
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("x-share-token", shareToken);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in items.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var type = item.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            var fileId = item.TryGetProperty("file_id", out var f) ? f.GetString() ?? "" : "";
            var path = string.IsNullOrEmpty(prefix) ? name : $"{prefix}/{name}";

            if (type == "folder")
            {
                await WalkAsync(shareId, shareToken, fileId, path, result, ct).ConfigureAwait(false);
            }
            else
            {
                long size = 0;
                if (item.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv))
                    size = sv;
                result.Add(new ShareFile
                {
                    FileId = fileId,
                    Name = name,
                    Path = path,
                    Size = size,
                });
            }
        }
    }

    /// <summary>
    /// Generate a time-limited direct download URL for one file inside a share.
    /// expireSec: typically 600-3600 (PDS enforces its own max).
    /// </summary>
    public async Task<SecureDownloadLink> CreateSecureDownloadLinkAsync(
        string shareId,
        string shareToken,
        ShareFile file,
        int expireSec = 3600,
        CancellationToken ct = default)
    {
        expireSec = Math.Clamp(expireSec, 60, 4 * 3600);

        var payload = JsonSerializer.Serialize(new
        {
            share_id = shareId,
            file_id = file.FileId,
            expire_sec = expireSec,
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{PdsApi}/v2/file/get_download_url");
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("x-share-token", shareToken);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        string? url = null;
        if (doc.RootElement.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
            url = u.GetString();
        else if (doc.RootElement.TryGetProperty("download_url", out var du) && du.ValueKind == JsonValueKind.String)
            url = du.GetString();

        if (string.IsNullOrEmpty(url))
            throw new InvalidOperationException("No download URL returned for file.");

        DateTimeOffset? expires = null;
        if (doc.RootElement.TryGetProperty("expiration", out var exp) && exp.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(exp.GetString(), out var parsed))
        {
            expires = parsed;
        }
        else
        {
            expires = DateTimeOffset.UtcNow.AddSeconds(expireSec);
        }

        long size = file.Size;
        if (doc.RootElement.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var sv))
            size = sv;

        return new SecureDownloadLink
        {
            FileName = file.Name,
            FilePath = file.Path,
            Size = size,
            Url = url,
            ExpiresAt = expires,
            ExpireSeconds = expireSec,
        };
    }

    public async Task<string> GetDownloadUrlAsync(
        string shareId,
        string shareToken,
        string fileId,
        CancellationToken ct = default)
    {
        var dummy = new ShareFile { FileId = fileId, Name = "file", Path = "file" };
        var link = await CreateSecureDownloadLinkAsync(shareId, shareToken, dummy, 3600, ct)
            .ConfigureAwait(false);
        return link.Url;
    }

    public async Task<List<SecureDownloadLink>> CreateSecureDownloadLinksAsync(
        string shareId,
        string shareToken,
        IEnumerable<ShareFile> files,
        int expireSec = 3600,
        CancellationToken ct = default)
    {
        var list = new List<SecureDownloadLink>();
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            list.Add(await CreateSecureDownloadLinkAsync(shareId, shareToken, f, expireSec, ct)
                .ConfigureAwait(false));
        }
        return list;
    }

    public async Task DownloadFileAsync(
        string downloadUrl,
        string shareToken,
        string destinationPath,
        IProgress<(long done, long total)>? progress,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        req.Headers.TryAddWithoutValidation("x-share-token", shareToken);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? -1L;
        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var output = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 128,
            useAsync: true);

        var buffer = new byte[1024 * 256];
        long done = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            progress?.Report((done, total));
        }
    }

    /// <summary>
    /// Lists the entries inside a remote ZIP (e.g. the firmware package's
    /// download link) by reading only its central directory over HTTP Range
    /// requests -- the ZIP itself is never downloaded.
    /// </summary>
    public Task<List<RemoteZipReader.RemoteZipEntry>> ListRemoteZipEntriesAsync(
        string downloadUrl,
        string shareToken,
        CancellationToken ct = default)
        => RemoteZipReader.ListEntriesAsync(
            _http,
            downloadUrl,
            req => req.Headers.TryAddWithoutValidation("x-share-token", shareToken),
            ct);

    /// <summary>
    /// Downloads and decompresses a single entry from a remote ZIP straight
    /// to disk, fetching only that entry's bytes.
    /// </summary>
    public Task ExtractRemoteZipEntryAsync(
        string downloadUrl,
        string shareToken,
        RemoteZipReader.RemoteZipEntry entry,
        string destinationPath,
        IProgress<(long done, long total)>? progress,
        CancellationToken ct = default)
        => RemoteZipReader.ExtractEntryAsync(
            _http,
            downloadUrl,
            entry,
            destinationPath,
            req => req.Headers.TryAddWithoutValidation("x-share-token", shareToken),
            progress,
            ct);

    public void Dispose() => _http.Dispose();
}
