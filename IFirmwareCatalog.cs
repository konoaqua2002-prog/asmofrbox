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
}
