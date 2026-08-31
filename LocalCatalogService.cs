using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AsmoFrBoxTelegramBot;

/// <summary>
/// Default catalog source. Loads firmware entries from, in priority order:
///   1. An external file next to the bot's working directory / CATALOG_SOURCE
///      env var (if present — lets you swap the catalog without rebuilding).
///   2. An embedded resource baked into the assembly at build time — see
///      AsmoFrBoxTelegramBot.csproj: firmware_catalog.json at the project
///      root gets embedded automatically if it exists when you build.
///   3. Nothing — empty catalog, bot still starts (searches just return 0 results).
/// An https URL can also be passed in explicitly to fetch from a host you control.
///
/// The bot never calls any third-party firmware-search API. All filtering and
/// paging happens in memory once the JSON is loaded.
///
/// JSON shape (an array of entries):
/// [
///   {
///     "id": 1,
///     "brand_name": "TECNO",
///     "project_name": "CN6",
///     "version": "CN6-H616AF-U-TR-250101V123",
///     "platform": "MT6789",
///     "market_type": "Global",
///     "created_at": "2025-01-01",
///     "download_link": "https://frbox.transsion.com/disk/s/xxxxxxxx",
///     "extraction_code": "ab12"
///   }
/// ]
/// </summary>
public sealed class LocalCatalogService : IFirmwareCatalog
{
    private const string EmbeddedResourceName = "AsmoFrBoxTelegramBot.firmware_catalog.json";

    private readonly string? _explicitSource;
    private readonly bool _isUrl;
    private readonly HttpClient? _http;

    private List<FirmwareEntry>? _cache;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    /// <param name="source">
    /// Optional override. Either a local file path or an "https://" URL you
    /// control. Leave null to use the default priority: external file, then
    /// the catalog embedded inside the assembly at build time.
    /// </param>
    public LocalCatalogService(string? source = null)
    {
        _explicitSource = source;
        _isUrl = _explicitSource is not null
                 && (_explicitSource.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                     || _explicitSource.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        if (_isUrl)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        }
    }

    public async Task<SearchResult> SearchAsync(
        string model,
        string brand,
        int page,
        int limit,
        CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct).ConfigureAwait(false);

        page = Math.Max(1, page);
        limit = Math.Clamp(limit, 1, 200);

        IEnumerable<FirmwareEntry> filtered = all;

        if (!string.IsNullOrWhiteSpace(brand))
        {
            filtered = filtered.Where(e =>
                string.Equals(e.Brand, brand, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            var needle = model.Trim();
            filtered = filtered.Where(e =>
                Contains(e.Project, needle) ||
                Contains(e.Version, needle) ||
                Contains(e.Brand, needle));
        }

        var list = filtered
            .OrderByDescending(e => e.CreatedAt, StringComparer.Ordinal)
            .ToList();

        var total = list.Count;
        var pageItems = list.Skip((page - 1) * limit).Take(limit).ToList();

        return new SearchResult
        {
            Ok = true,
            Total = total,
            Page = page,
            Limit = limit,
            Items = pageItems,
        };
    }

    private static bool Contains(string haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) &&
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private async Task<List<FirmwareEntry>> GetAllAsync(CancellationToken ct)
    {
        if (_cache is not null) return _cache;

        await _loadLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache is not null) return _cache;

            string? json;

            if (_isUrl)
            {
                json = await _http!.GetStringAsync(_explicitSource, ct).ConfigureAwait(false);
            }
            else
            {
                var filePath = _explicitSource
                    ?? Path.Combine(AppContext.BaseDirectory, "firmware_catalog.json");

                if (File.Exists(filePath))
                {
                    json = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
                }
                else
                {
                    json = ReadEmbeddedCatalog();
                }
            }

            if (json is null)
            {
                _cache = new List<FirmwareEntry>();
                return _cache;
            }

            var dtos = JsonSerializer.Deserialize<List<CatalogEntryDto>>(json, JsonOpts)
                       ?? new List<CatalogEntryDto>();

            _cache = dtos.Select(d => new FirmwareEntry
            {
                Id = d.Id,
                Brand = d.BrandName ?? "",
                Project = d.ProjectName ?? "",
                Version = d.Version ?? "",
                Platform = d.Platform ?? "",
                Market = d.MarketType ?? "",
                CreatedAt = d.CreatedAt ?? "",
                DownloadLink = d.DownloadLink ?? "",
                ExtractionCode = d.ExtractionCode ?? "",
            }).ToList();

            return _cache;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private static string? ReadEmbeddedCatalog()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(EmbeddedResourceName);
        if (stream is null) return null;

        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Forces the next search to re-read the source (file or URL).</summary>
    public void InvalidateCache() => _cache = null;

    public void Dispose()
    {
        _http?.Dispose();
        _loadLock.Dispose();
    }

    private sealed class CatalogEntryDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("brand_name")] public string? BrandName { get; set; }
        [JsonPropertyName("project_name")] public string? ProjectName { get; set; }
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("platform")] public string? Platform { get; set; }
        [JsonPropertyName("market_type")] public string? MarketType { get; set; }
        [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
        [JsonPropertyName("download_link")] public string? DownloadLink { get; set; }
        [JsonPropertyName("extraction_code")] public string? ExtractionCode { get; set; }
    }
}
