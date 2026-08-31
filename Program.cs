using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;

namespace AsmoFrBoxTelegramBot;

internal static class Program
{
    private static async Task Main()
    {
        // Load optional config.json (next to the executable) into environment
        // variables so the rest of the code can keep using Environment.Get...
        // Environment variables still win if already set (Docker/systemd/etc.).
        LoadConfigJson();

        var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine(
                "Missing TELEGRAM_BOT_TOKEN. Set it in config.json next to the app, or as an environment variable, e.g.:\n" +
                "  config.json  →  { \"TELEGRAM_BOT_TOKEN\": \"123456:ABC-your-token\" }\n" +
                "  or:  export TELEGRAM_BOT_TOKEN=123456:ABC-your-token\n" +
                "  then:  dotnet run");
            Environment.Exit(1);
            return;
        }

        // Optional: point at an external catalog file or an https:// URL you host.
        // Falls back to firmware_catalog.json next to the app, then to the
        // copy embedded in the assembly at build time (see LocalCatalogService.cs).
        var catalogSource = Environment.GetEnvironmentVariable("CATALOG_SOURCE");

        using var catalog = new LocalCatalogService(catalogSource);
        using var frBox = new FrBoxService();

        var bot = new TelegramBotClient(token);
        var me = await bot.GetMe();
        Console.WriteLine($"Logged in as @{me.Username} (id {me.Id}). Listening for updates...");

        var botService = new BotService(bot, catalog, frBox);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery },
        };

        bot.StartReceiving(
            botService.HandleUpdateAsync,
            botService.HandleErrorAsync,
            receiverOptions,
            cts.Token);

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (TaskCanceledException)
        {
            // normal shutdown
        }

        Console.WriteLine("Shutting down.");
    }

    /// <summary>
    /// Reads config.json from the app directory (if present) and copies any
    /// known keys into environment variables — but only when the variable is
    /// not already set, so Docker / systemd / shell exports still take priority.
    /// </summary>
    private static void LoadConfigJson()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        if (!File.Exists(configPath))
            return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = doc.RootElement;

            SetEnvIfMissing(root, "TELEGRAM_BOT_TOKEN");
            SetEnvIfMissing(root, "ADMIN_TELEGRAM_CHAT_IDS");
            SetEnvIfMissing(root, "ADMIN_TELEGRAM_CHAT_ID");       // legacy single-admin
            SetEnvIfMissing(root, "ADMIN_TELEGRAM_USERNAMES");
            SetEnvIfMissing(root, "ADMIN_TELEGRAM_USERNAME");      // legacy single-admin
            SetEnvIfMissing(root, "CATALOG_SOURCE");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: could not parse config.json: {ex.Message}");
        }
    }

    private static void SetEnvIfMissing(JsonElement root, string key)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
            return;

        if (root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
        {
            var value = el.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
