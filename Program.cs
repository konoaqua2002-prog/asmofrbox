using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;

namespace AsmoFrBoxTelegramBot;

internal static class Program
{
    private static async Task Main()
    {
        var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine(
                "Missing TELEGRAM_BOT_TOKEN environment variable. Get a token from @BotFather on Telegram and set it, e.g.:\n" +
                "  export TELEGRAM_BOT_TOKEN=123456:ABC-your-token\n" +
                "  dotnet run");
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
}
