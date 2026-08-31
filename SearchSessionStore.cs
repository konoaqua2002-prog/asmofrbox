using System.Collections.Concurrent;

namespace AsmoFrBoxTelegramBot;

/// <summary>
/// Per-chat state kept purely in memory. A process restart just clears
/// everyone's current search -- acceptable for a search-and-link bot,
/// avoids needing a database.
/// </summary>
public sealed class SearchSessionStore
{
    private static readonly TimeSpan OtpLifetime = TimeSpan.FromMinutes(5);

    public sealed class Session
    {
        public string Query = "";
        public int Page = 1;
        public SearchResult? LastResult;
        public UnlockedShare? Share;
        public OtpState? Otp;
    }

    public sealed class OtpState
    {
        public string Code { get; set; } = "";
        public DateTimeOffset IssuedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset ExpiresAtUtc { get; set; } = DateTimeOffset.UtcNow.Add(OtpLifetime);
        public bool IsVerified { get; set; }
    }

    public sealed class UnlockedShare
    {
        public required string ShareId;
        public required string ShareToken;
        public required List<ShareFile> Files;
        /// <summary>Index (within LastResult.Items) of the firmware entry this share was unlocked for.</summary>
        public required int SourceEntryIndex;
    }

    private readonly ConcurrentDictionary<long, Session> _sessions = new();

    public Session Get(long chatId) => _sessions.GetOrAdd(chatId, static _ => new Session());

    public OtpState? IssueOtp(long chatId)
    {
        var session = Get(chatId);
        var code = Random.Shared.Next(100000, 999999).ToString("D6");
        session.Otp = new OtpState
        {
            Code = code,
            IssuedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.Add(OtpLifetime),
            IsVerified = false,
        };
        return session.Otp;
    }

    public bool IsOtpVerified(long chatId)
    {
        var session = Get(chatId);
        var otp = session.Otp;
        if (otp is null) return false;
        if (DateTimeOffset.UtcNow > otp.ExpiresAtUtc)
        {
            session.Otp = null;
            return false;
        }

        return otp.IsVerified;
    }

    public bool TryVerifyOtp(long chatId, string code)
    {
        var session = Get(chatId);
        var otp = session.Otp;
        if (otp is null) return false;
        if (DateTimeOffset.UtcNow > otp.ExpiresAtUtc)
        {
            session.Otp = null;
            return false;
        }

        if (string.Equals(otp.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            otp.IsVerified = true;
            return true;
        }

        return false;
    }

    public void ResetOtp(long chatId) => Get(chatId).Otp = null;
}
