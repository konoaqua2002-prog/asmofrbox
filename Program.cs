using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;

namespace AsmoFrBoxTelegramBot;

internal static class Program
{
    private static async Task Main()
    {
        // Priority for secrets (env vars always win if already set):
        //   1. Railway / Docker environment variables
        //   2. secrets.json  (token + admin IDs — easy to replace on deploy)
        //   3. config.json   (optional non-secret settings like CATALOG_SOURCE)
        LoadJsonIntoEnv("secrets.json");
        LoadJsonIntoEnv("config.json");

        var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine(
                "Missing TELEGRAM_BOT_TOKEN.\n" +
                "Put it in secrets.json next to the app (recommended on Railway):\n" +
                "  { \"TELEGRAM_BOT_TOKEN\": \"123456:ABC...\", \"ADMIN_TELEGRAM_CHAT_IDS\": \"123,456\" }\n" +
                "or set TELEGRAM_BOT_TOKEN as an environment variable.");
            Environment.Exit(1);
            return;
        }

        // Optional: point at an external catalog file or an https:// URL you host
        // (e.g. the JSON exported by the Python firmware_server_gui "Export All").
        // Falls back to firmware_catalog.json next to the app, then embedded resource.
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

        // Optional auto-refresh of catalog when CATALOG_SOURCE is an HTTP(S) URL.
        // CATALOG_REFRESH_MINUTES=60 (default). Set to 0 to disable.
        var refreshMinutes = 60;
        if (int.TryParse(Environment.GetEnvironmentVariable("CATALOG_REFRESH_MINUTES"), out var rm))
            refreshMinutes = rm;

        if (refreshMinutes > 0 &&
            !string.IsNullOrWhiteSpace(catalogSource) &&
            (catalogSource.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             catalogSource.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            _ = Task.Run(async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(refreshMinutes), cts.Token).ConfigureAwait(false);
                        var n = await catalog.ReloadAsync(cts.Token).ConfigureAwait(false);
                        Console.WriteLine($"[auto-refresh] Catalog reloaded: {n} entries.");
                    }
                    catch (TaskCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[auto-refresh] Failed: {ex.Message}");
                    }
                }
            }, cts.Token);
            Console.WriteLine($"Catalog auto-refresh every {refreshMinutes} minute(s) from {catalogSource}");
        }

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
    /// Reads a JSON file from the app directory (if present) and copies known
    /// keys into environment variables — only when the variable is not already
    /// set, so Railway / Docker / shell exports still take priority.
    /// </summary>
    private static void LoadJsonIntoEnv(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path))
            return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            SetEnvIfMissing(root, "TELEGRAM_BOT_TOKEN");
            SetEnvIfMissing(root, "ADMIN_TELEGRAM_CHAT_IDS");
            SetEnvIfMissing(root, "ADMIN_TELEGRAM_CHAT_ID");       // legacy single-admin
            SetEnvIfMissing(root, "ADMIN_TELEGRAM_USERNAMES");
            SetEnvIfMissing(root, "ADMIN_TELEGRAM_USERNAME");      // legacy single-admin
            SetEnvIfMissing(root, "CATALOG_SOURCE");
            SetEnvIfMissing(root, "CATALOG_REFRESH_MINUTES");
            Console.WriteLine($"Loaded settings from {fileName}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: could not parse {fileName}: {ex.Message}");
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
        else if (root.TryGetProperty(key, out el) && el.ValueKind == JsonValueKind.Number)
        {
            Environment.SetEnvironmentVariable(key, el.GetRawText());
        }
    }
}
