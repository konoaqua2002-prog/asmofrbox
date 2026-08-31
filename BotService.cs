using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace AsmoFrBoxTelegramBot;

/// <summary>
/// All Telegram-facing logic. Flow: request OTP -> verify OTP -> search
/// models -> select a version -> review its full name/details -> confirm ->
/// bot sends the FRBox share link + extraction code -> OTP is immediately
/// invalidated, so a different model/version requires a fresh OTP.
/// FrBoxService is kept around (unused in this flow) for a possible future
/// "unlock & list files" feature -- see README.
/// </summary>
public sealed class BotService
{
    private const int PageSize = 6;

    private readonly ITelegramBotClient _bot;
    private readonly IFirmwareCatalog _catalog;
    private readonly FrBoxService _frBox;
    private readonly SearchSessionStore _sessions = new();

    public BotService(ITelegramBotClient bot, IFirmwareCatalog catalog, FrBoxService frBox)
    {
        _bot = bot;
        _catalog = catalog;
        _frBox = frBox;
    }

    public async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            if (update.Message is { } message && message.Text is { } text)
            {
                await HandleMessageAsync(message.Chat.Id, text, ct).ConfigureAwait(false);
            }
            else if (update.CallbackQuery is { } cq)
            {
                await HandleCallbackAsync(cq, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            var chatId = update.Message?.Chat.Id ?? update.CallbackQuery?.Message?.Chat.Id;
            if (chatId is { } id)
            {
                await TrySend(id, $"⚠️ Something went wrong: {ex.Message}", ct).ConfigureAwait(false);
            }
        }
    }

    public Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, HandleErrorSource source, CancellationToken ct)
    {
        Console.Error.WriteLine($"[{source}] {exception}");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- text

    private async Task HandleMessageAsync(long chatId, string text, CancellationToken ct)
    {
        text = text.Trim();

        if (text.StartsWith('/'))
        {
            var spaceIdx = text.IndexOf(' ');
            var cmd = (spaceIdx < 0 ? text : text[..spaceIdx]).ToLowerInvariant();
            var arg = spaceIdx < 0 ? "" : text[(spaceIdx + 1)..].Trim();
            // Strip a trailing @BotUsername (group chats).
            var atIdx = cmd.IndexOf('@');
            if (atIdx >= 0) cmd = cmd[..atIdx];

            switch (cmd)
            {
                case "/start":
                case "/help":
                    await TrySend(chatId,
                        "👋 <b>Firmware Finder</b>\n\n" +
                        "This bot requires OTP verification before any firmware or download link can be shown.\n\n" +
                        "1️⃣ Tap <b>🔐 Request OTP</b> below (or send <code>/requestotp</code>) — request a 6-digit code\n" +
                        "2️⃣ <code>/verify 123456</code> — verify it\n" +
                        "3️⃣ Send a model name (e.g. <code>CN6</code> or <code>H616AF</code>) or use <code>/search &lt;model&gt;</code> — I'll search the firmware catalog\n" +
                        "4️⃣ Tap a result to select a version\n" +
                        "5️⃣ Review the full version name/details and confirm it's correct\n" +
                        "6️⃣ I'll send you the download link\n\n" +
                        "🔐 <i>Each OTP is good for one download. If you want a different model or version afterwards, you'll need to request and verify a new OTP.</i>",
                        ct, ParseMode.Html, BuildRequestOtpKeyboard()).ConfigureAwait(false);
                    return;

                case "/requestotp":
                case "/otp":
                    await RequestOtpAsync(chatId, ct).ConfigureAwait(false);
                    return;

                case "/verify":
                    if (string.IsNullOrWhiteSpace(arg))
                    {
                        await TrySend(chatId, "Usage: <code>/verify 123456</code>", ct, ParseMode.Html).ConfigureAwait(false);
                        return;
                    }

                    if (_sessions.TryVerifyOtp(chatId, arg))
                    {
                        await TrySend(chatId, "✅ OTP verified. You can now search for firmware and access links.", ct, ParseMode.Html).ConfigureAwait(false);
                    }
                    else
                    {
                        await TrySend(chatId, "❌ Invalid or expired OTP. Send <code>/requestotp</code> to generate a fresh code.", ct, ParseMode.Html).ConfigureAwait(false);
                    }
                    return;

                case "/search":
                    if (string.IsNullOrWhiteSpace(arg))
                    {
                        await TrySend(chatId, "Usage: <code>/search CN6</code>", ct, ParseMode.Html).ConfigureAwait(false);
                        return;
                    }
                    await RunSearchAsync(chatId, arg, page: 1, ct).ConfigureAwait(false);
                    return;

                default:
                    await TrySend(chatId, "Unknown command. Try /help.", ct).ConfigureAwait(false);
                    return;
            }
        }

        if (!await EnsureOtpVerifiedAsync(chatId, ct).ConfigureAwait(false))
        {
            return;
        }

        // Plain text = search query.
        await RunSearchAsync(chatId, text, page: 1, ct).ConfigureAwait(false);
    }

    private async Task RequestOtpAsync(long chatId, CancellationToken ct)
    {
        var otp = _sessions.IssueOtp(chatId);
        if (otp is null)
        {
            await TrySend(chatId, "⚠️ Could not generate OTP right now. Please try again in a moment.", ct, ParseMode.Html).ConfigureAwait(false);
            return;
        }

        var requesterDisplayName = await GetRequesterDisplayNameAsync(chatId, ct).ConfigureAwait(false);
        await NotifyAdminOfOtpAsync(chatId, requesterDisplayName, otp.Code, ct).ConfigureAwait(false);

        await TrySend(chatId,
            "🔐 Your verification request has been sent to the administrator. Please wait for confirmation before continuing.",
            ct, ParseMode.Html).ConfigureAwait(false);
    }

    private async Task NotifyAdminOfOtpAsync(long chatId, string requesterDisplayName, string otpCode, CancellationToken ct)
    {
        var adminChatIds = GetAdminChatIds();
        var adminUsernames = GetAdminUsernames();

        var adminKeyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("📨 Send OTP to client", $"sendotp:{chatId}") },
        });

        var adminText =
            $"[🔐] OTP request\n\nOTP: <code>{Escape(otpCode)}</code>\nName: {Escape(requesterDisplayName)}\nUser: {Escape(requesterDisplayName)}\nUser ID: <code>{chatId}</code>";

        var delivered = 0;

        // Prefer numeric chat IDs -- always reliable, works even if that
        // admin has never DM'd the bot before.
        foreach (var adminChatId in adminChatIds)
        {
            try
            {
                await _bot.SendMessage(adminChatId,
                    adminText,
                    parseMode: ParseMode.Html,
                    replyMarkup: adminKeyboard,
                    cancellationToken: ct).ConfigureAwait(false);
                delivered++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not send OTP to admin chat {adminChatId}: {ex.Message}");
            }
        }

        // Also notify any admins configured by @username only (requires the
        // bot to already have a private chat with that user).
        foreach (var adminUsername in adminUsernames)
        {
            try
            {
                await _bot.SendMessage(adminUsername,
                    adminText,
                    parseMode: ParseMode.Html,
                    replyMarkup: adminKeyboard,
                    cancellationToken: ct).ConfigureAwait(false);
                delivered++;
                Console.WriteLine($"OTP sent to admin username {adminUsername}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not send OTP to admin username {adminUsername}. The bot must already have a chat with that admin user, or add their numeric chat ID to ADMIN_TELEGRAM_CHAT_IDS instead. Details: {ex.Message}");
            }
        }

        if (delivered == 0)
        {
            Console.WriteLine("No valid admin target configured for OTP delivery, or every delivery attempt failed. Set ADMIN_TELEGRAM_CHAT_IDS to one or more numeric Telegram user/chat IDs (comma-separated) and/or ADMIN_TELEGRAM_USERNAMES to one or more @usernames.");
        }
    }

    /// <summary>
    /// Reads the configured admin chat IDs. Accepts ADMIN_TELEGRAM_CHAT_IDS
    /// as a comma-separated list of numeric IDs (multiple admins); falls
    /// back to the older single-admin ADMIN_TELEGRAM_CHAT_ID for backward
    /// compatibility.
    /// </summary>
    private static IReadOnlyList<long> GetAdminChatIds()
    {
        var raw = Environment.GetEnvironmentVariable("ADMIN_TELEGRAM_CHAT_IDS")
                  ?? Environment.GetEnvironmentVariable("ADMIN_TELEGRAM_CHAT_ID");
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<long>();

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, out var id) ? (long?)id : null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Reads the configured admin usernames. Accepts ADMIN_TELEGRAM_USERNAMES
    /// as a comma-separated list (multiple admins); falls back to the older
    /// single-admin ADMIN_TELEGRAM_USERNAME, then to "asmo_qt" if neither is set.
    /// </summary>
    private static IReadOnlyList<string> GetAdminUsernames()
    {
        var raw = Environment.GetEnvironmentVariable("ADMIN_TELEGRAM_USERNAMES")
                  ?? Environment.GetEnvironmentVariable("ADMIN_TELEGRAM_USERNAME")
                  ?? "asmo_qt";

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.TrimStart('@'))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// True for any Telegram user configured as an admin (via
    /// ADMIN_TELEGRAM_CHAT_IDS and/or ADMIN_TELEGRAM_USERNAMES, each
    /// comma-separated for multiple admins) -- checked against whoever
    /// actually tapped the button, so a client can't forge this callback to
    /// have the OTP "sent" to themselves or someone else.
    /// </summary>
    private static bool IsCallerAdmin(CallbackQuery cq)
    {
        if (GetAdminChatIds().Contains(cq.From.Id)) return true;

        if (!string.IsNullOrWhiteSpace(cq.From.Username) &&
            GetAdminUsernames().Any(u => string.Equals(u, cq.From.Username, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Handles the admin tapping "📨 Send OTP to client" on the admin
    /// notification -- looks up that client's current (still-live) OTP and
    /// messages it to them directly, so the admin never has to copy/paste it
    /// by hand.
    /// </summary>
    private async Task HandleSendOtpToClientAsync(CallbackQuery cq, string data, CancellationToken ct)
    {
        if (!IsCallerAdmin(cq))
        {
            await _bot.AnswerCallbackQuery(cq.Id, "Only the administrator can do this.", showAlert: true, cancellationToken: ct)
                .ConfigureAwait(false);
            return;
        }

        if (!long.TryParse(data.AsSpan("sendotp:".Length), out var clientChatId))
        {
            await _bot.AnswerCallbackQuery(cq.Id, "Malformed request.", showAlert: true, cancellationToken: ct)
                .ConfigureAwait(false);
            return;
        }

        var clientSession = _sessions.Get(clientChatId);
        var otp = clientSession.Otp;
        if (otp is null || DateTimeOffset.UtcNow > otp.ExpiresAtUtc)
        {
            await _bot.AnswerCallbackQuery(cq.Id, "That OTP has expired -- ask the client to request a new one.", showAlert: true, cancellationToken: ct)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            await _bot.SendMessage(clientChatId,
                $"🔐 Your verification code: <code>{Escape(otp.Code)}</code>\n\nSend <code>/verify {Escape(otp.Code)}</code> to continue.",
                parseMode: ParseMode.Html, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _bot.AnswerCallbackQuery(cq.Id, $"Could not message client: {ex.Message}", showAlert: true, cancellationToken: ct)
                .ConfigureAwait(false);
            return;
        }

        await _bot.AnswerCallbackQuery(cq.Id, "OTP sent to client.", cancellationToken: ct).ConfigureAwait(false);

        // Best effort: pull the button off the admin message once actioned.
        if (cq.Message is { } adminMessage)
        {
            try
            {
                await _bot.EditMessageReplyMarkup(adminMessage.Chat.Id, adminMessage.Id,
                    replyMarkup: null, cancellationToken: ct).ConfigureAwait(false);
            }
            catch
            {
                // best effort
            }
        }
    }

    private async Task<string> GetRequesterDisplayNameAsync(long chatId, CancellationToken ct)
    {
        try
        {
            var chat = await _bot.GetChat(new ChatId(chatId), ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(chat.Username)) return chat.Username;
            var name = string.Join(" ", new[] { chat.FirstName, chat.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return string.IsNullOrWhiteSpace(name) ? $"User-{chatId}" : name;
        }
        catch
        {
            return $"User-{chatId}";
        }
    }

    private async Task<bool> EnsureOtpVerifiedAsync(long chatId, CancellationToken ct)
    {
        if (_sessions.IsOtpVerified(chatId))
        {
            return true;
        }

        await TrySend(chatId,
            "🔐 OTP verification required before any link can be opened. Tap the button below (or send <code>/requestotp</code>) to receive a code, then verify it with <code>/verify 123456</code>.",
            ct, ParseMode.Html, BuildRequestOtpKeyboard()).ConfigureAwait(false);
        return false;
    }

    private async Task RunSearchAsync(long chatId, string query, int page, CancellationToken ct)
    {
        if (!await EnsureOtpVerifiedAsync(chatId, ct).ConfigureAwait(false))
        {
            return;
        }

        var session = _sessions.Get(chatId);
        var result = await _catalog.SearchAsync(query, brand: "", page, PageSize, ct).ConfigureAwait(false);

        session.Query = query;
        session.Page = page;
        session.LastResult = result;
        session.Share = null;

        if (result.Total == 0)
        {
            await TrySend(chatId, $"No firmware found for “{Escape(query)}”. Try a shorter/different model string.", ct)
                .ConfigureAwait(false);
            return;
        }

        var text = $"🔍 Found <b>{result.Total}</b> result(s) for “{Escape(query)}” — page {result.Page}/{result.TotalPages}:";
        await _bot.SendMessage(chatId, text, parseMode: ParseMode.Html, replyMarkup: BuildResultsKeyboard(result), cancellationToken: ct)
            .ConfigureAwait(false);
    }

    // ------------------------------------------------------------ callback

    private async Task HandleCallbackAsync(CallbackQuery cq, CancellationToken ct)
    {
        var chatId = cq.Message?.Chat.Id;
        var messageId = cq.Message?.Id;
        var data = cq.Data ?? "";

        if (chatId is null || messageId is null)
        {
            await _bot.AnswerCallbackQuery(cq.Id, cancellationToken: ct).ConfigureAwait(false);
            return;
        }

        // Admin-only action, fired from the admin's own chat -- must be
        // handled before the client OTP-verification gate below, since the
        // admin's chat is never itself "OTP verified".
        if (data.StartsWith("sendotp:", StringComparison.Ordinal))
        {
            await HandleSendOtpToClientAsync(cq, data, ct).ConfigureAwait(false);
            return;
        }

        // Client tapped the "🔐 Request OTP" button -- same as /requestotp,
        // but must be handled before the OTP-verified gate below since the
        // whole point is to request a code while still unverified.
        if (data == "reqotp")
        {
            await RequestOtpAsync(chatId.Value, ct).ConfigureAwait(false);
            await _bot.AnswerCallbackQuery(cq.Id, "🔐 OTP requested — hang tight for the code.", cancellationToken: ct)
                .ConfigureAwait(false);
            return;
        }

        if (!_sessions.IsOtpVerified(chatId.Value))
        {
            await _bot.AnswerCallbackQuery(cq.Id,
                "OTP verification required. Tap 🔐 Request OTP or send /requestotp to receive a code.",
                showAlert: true,
                cancellationToken: ct).ConfigureAwait(false);
            return;
        }

        var session = _sessions.Get(chatId.Value);

        try
        {
            if (data.StartsWith("pg:", StringComparison.Ordinal) && int.TryParse(data.AsSpan(3), out var page))
            {
                var result = await _catalog.SearchAsync(session.Query, brand: "", page, PageSize, ct).ConfigureAwait(false);
                session.Page = page;
                session.LastResult = result;
                await _bot.EditMessageText(chatId.Value, messageId.Value,
                    $"🔍 Found <b>{result.Total}</b> result(s) for “{Escape(session.Query)}” — page {result.Page}/{result.TotalPages}:",
                    parseMode: ParseMode.Html, replyMarkup: BuildResultsKeyboard(result), cancellationToken: ct)
                    .ConfigureAwait(false);
            }
            else if (data.StartsWith("v:", StringComparison.Ordinal) && int.TryParse(data.AsSpan(2), out var vi))
            {
                var entry = GetEntry(session, vi);
                if (entry is null) { await AlertExpired(cq, ct).ConfigureAwait(false); return; }

                await _bot.EditMessageText(chatId.Value, messageId.Value,
                    BuildEntryDetailText(entry) + "\n\n❓ <b>Is this the correct model/version?</b>",
                    parseMode: ParseMode.Html,
                    replyMarkup: BuildEntryKeyboard(vi, session.Page), cancellationToken: ct)
                    .ConfigureAwait(false);
            }
            else if (data.StartsWith("u:", StringComparison.Ordinal) && int.TryParse(data.AsSpan(2), out var ui))
            {
                // Client tapped "Confirm — this is correct" on the version-detail screen.
                var entry = GetEntry(session, ui);
                if (entry is null) { await AlertExpired(cq, ct).ConfigureAwait(false); return; }

                await _bot.EditMessageText(chatId.Value, messageId.Value,
                    BuildEntryDetailText(entry) + "\n\n✅ Confirmed. Download link sent below.",
                    parseMode: ParseMode.Html, cancellationToken: ct).ConfigureAwait(false);

                await SendDownloadLinkAsync(chatId.Value, entry, ct).ConfigureAwait(false);
            }
            else if (data.StartsWith("b:", StringComparison.Ordinal) && int.TryParse(data.AsSpan(2), out var bp))
            {
                var result = session.LastResult ?? await _catalog.SearchAsync(session.Query, "", bp, PageSize, ct).ConfigureAwait(false);
                await _bot.EditMessageText(chatId.Value, messageId.Value,
                    $"🔍 Found <b>{result.Total}</b> result(s) for “{Escape(session.Query)}” — page {result.Page}/{result.TotalPages}:",
                    parseMode: ParseMode.Html, replyMarkup: BuildResultsKeyboard(result), cancellationToken: ct)
                    .ConfigureAwait(false);
            }

            await _bot.AnswerCallbackQuery(cq.Id, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _bot.AnswerCallbackQuery(cq.Id, $"Error: {ex.Message}", showAlert: true, cancellationToken: ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends the FRBox share link + extraction code for the confirmed
    /// firmware entry, then revokes the client's OTP verification. Policy:
    /// one OTP = one download. If the client wants a different model or
    /// version afterwards, EnsureOtpVerifiedAsync / the callback OTP gate
    /// will force a fresh /requestotp + /verify before anything else
    /// happens.
    /// </summary>
    private async Task SendDownloadLinkAsync(long chatId, FirmwareEntry entry, CancellationToken ct)
    {
        var link = entry.DownloadLink;
        var copyFormat = string.IsNullOrEmpty(entry.ExtractionCode)
            ? link
            : $"{link}?pwd={entry.ExtractionCode}";

        await _bot.SendMessage(chatId,
            $"📥 <b>{Escape(entry.Brand)} {Escape(entry.Project)}</b>\n" +
            $"Version: <code>{Escape(entry.Version)}</code>\n\n" +
            $"🔗 <b>Download link</b>\n{Escape(link)}\n" +
            (string.IsNullOrEmpty(entry.ExtractionCode) ? "" : $"🔑 Extraction code: <code>{Escape(entry.ExtractionCode)}</code>\n") +
            $"\n<i>Copy-paste format:</i>\n<code>{Escape(copyFormat)}</code>",
            parseMode: ParseMode.Html, cancellationToken: ct).ConfigureAwait(false);

        _sessions.ResetOtp(chatId);

        await TrySend(chatId,
            "🔐 This OTP has now been used. To download a different model or version, send <code>/requestotp</code> again.",
            ct, ParseMode.Html).ConfigureAwait(false);
    }

    private static FirmwareEntry? GetEntry(SearchSessionStore.Session session, int index)
    {
        var items = session.LastResult?.Items;
        if (items is null || index < 0 || index >= items.Count) return null;
        return items[index];
    }

    private async Task AlertExpired(CallbackQuery cq, CancellationToken ct) =>
        await _bot.AnswerCallbackQuery(cq.Id, "This result has expired — please search again.", showAlert: true, cancellationToken: ct)
            .ConfigureAwait(false);

    // -------------------------------------------------------------- render

    private static InlineKeyboardMarkup BuildResultsKeyboard(SearchResult result)
    {
        var rows = new List<InlineKeyboardButton[]>();
        for (int i = 0; i < result.Items.Count; i++)
        {
            rows.Add(new[] { InlineKeyboardButton.WithCallbackData(result.Items[i].ButtonLabel, $"v:{i}") });
        }

        var nav = new List<InlineKeyboardButton>();
        if (result.Page > 1)
            nav.Add(InlineKeyboardButton.WithCallbackData("⬅️ Prev", $"pg:{result.Page - 1}"));
        if (result.Page < result.TotalPages)
            nav.Add(InlineKeyboardButton.WithCallbackData("Next ➡️", $"pg:{result.Page + 1}"));
        if (nav.Count > 0) rows.Add(nav.ToArray());

        return new InlineKeyboardMarkup(rows);
    }

    private static InlineKeyboardMarkup BuildRequestOtpKeyboard() =>
        new(new[] { new[] { InlineKeyboardButton.WithCallbackData("🔐 Request OTP", "reqotp") } });

    private static InlineKeyboardMarkup BuildEntryKeyboard(int entryIndex, int page) =>
        new(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("✅ Confirm — this is correct", $"u:{entryIndex}") },
            new[] { InlineKeyboardButton.WithCallbackData("⬅️ Not this one, back to results", $"b:{page}") },
        });

    private static string BuildEntryDetailText(FirmwareEntry e)
    {
        return
            $"📦 <b>{Escape(e.Brand)} {Escape(e.Project)}</b>\n" +
            $"Version: <code>{Escape(e.Version)}</code>\n" +
            (string.IsNullOrWhiteSpace(e.Platform) ? "" : $"Platform: {Escape(e.Platform)}\n") +
            $"Market: {Escape(e.Market)}\n" +
            $"Created: {Escape(e.CreatedAt)}";
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private async Task TrySend(long chatId, string text, CancellationToken ct, ParseMode parseMode = ParseMode.None, InlineKeyboardMarkup? replyMarkup = null)
    {
        try
        {
            await _bot.SendMessage(chatId, text, parseMode: parseMode, replyMarkup: replyMarkup, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (ApiRequestException)
        {
            // Best effort -- e.g. bot was blocked by the user.
        }
    }
}
