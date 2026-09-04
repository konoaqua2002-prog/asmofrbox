namespace AsmoFrBoxTelegramBot;

/// <summary>
/// Anything that can answer "give me firmware entries matching this model/brand"
/// implements this. Lets the bot swap catalog sources without changing handler code.
/// </summary>
public interface IFirmwareCatalog : IDisposable
{
    Task<SearchResult> SearchAsync(
        string model,
        string brand,
        int page,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Drop any in-memory cache so the next search reloads from disk/URL.
    /// Used by admin /refreshcatalog and optional auto-refresh.
    /// </summary>
    void InvalidateCache();

    /// <summary>
    /// Force a reload and return how many entries were loaded (for admin feedback).
    /// </summary>
    Task<int> ReloadAsync(CancellationToken ct = default);
}
