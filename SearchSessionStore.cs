using System.Collections.Concurrent;

namespace AsmoFrBoxTelegramBot;

/// <summary>
/// Per-chat state kept purely in memory. A process restart just clears
/// everyone's current search -- acceptable for a search-and-link bot,
/// avoids needing a database.
///
/// OTP handling is designed for concurrent users:
/// - Each chat has its own independent OTP state (no shared global lock).
/// - A short cooldown prevents a single user from flooding admins with requests.
/// - Each OTP gets a unique RequestId so admins can tell concurrent requests apart.
/// - Issuing a new OTP while one is still live is blocked (or returns the existing one).
/// </summary>
public sealed class SearchSessionStore
{
    private static readonly TimeSpan OtpLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OtpRequestCooldown = TimeSpan.FromSeconds(45);

    public sealed class Session
    {
        public string Query = "";
        public int Page = 1;
        public SearchResult? LastResult;
        public UnlockedShare? Share;
        public OtpState? Otp;
        /// <summary>UTC time of the last OTP issue attempt (used for per-user cooldown).</summary>
        public DateTimeOffset LastOtpRequestAtUtc = DateTimeOffset.MinValue;
    }

    public sealed class OtpState
    {
        /// <summary>Short unique id so admins can distinguish concurrent OTP requests.</summary>
        public string RequestId { get; set; } = "";
        public string Code { get; set; } = "";
        public DateTimeOffset IssuedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset ExpiresAtUtc { get; set; } = DateTimeOffset.UtcNow.Add(OtpLifetime);
        public bool IsVerified { get; set; }
        /// <summary>True after an admin has pressed "Send OTP to client".</summary>
        public bool DeliveredToClient { get; set; }
        /// <summary>Display name of the admin who approved / sent the OTP.</summary>
        public string ApprovedByDisplayName { get; set; } = "";
        /// <summary>Telegram user id of the admin who approved / sent the OTP.</summary>
        public long? ApprovedByUserId { get; set; }
        public DateTimeOffset? ApprovedAtUtc { get; set; }
    }

    public sealed class UnlockedShare
    {
        public required string ShareId;
        public required string ShareToken;
        public required List<ShareFile> Files;
        /// <summary>Index (within LastResult.Items) of the firmware entry this share was unlocked for.</summary>
        public required int SourceEntryIndex;
    }

    /// <summary>Result of trying to issue an OTP for a chat.</summary>
    public enum IssueOtpStatus
    {
        Issued,
        /// <summary>A still-valid OTP already exists for this chat; returned it instead of making a new one.</summary>
        AlreadyPending,
        /// <summary>User is still inside the per-user cooldown window.</summary>
        Cooldown,
        /// <summary>Unexpected failure.</summary>
        Failed,
    }

    public sealed class IssueOtpResult
    {
        public IssueOtpStatus Status { get; init; }
        public OtpState? Otp { get; init; }
        /// <summary>Seconds remaining on the cooldown (only set when Status == Cooldown).</summary>
        public int CooldownSecondsRemaining { get; init; }
    }

    private readonly ConcurrentDictionary<long, Session> _sessions = new();

    public Session Get(long chatId) => _sessions.GetOrAdd(chatId, static _ => new Session());

    /// <summary>
    /// Issues a new OTP for the given chat, or returns the existing live one.
    /// Thread-safe per chat; concurrent users do not interfere with each other.
    /// </summary>
    public IssueOtpResult IssueOtp(long chatId)
    {
        var session = Get(chatId);
        var now = DateTimeOffset.UtcNow;

        // Reuse a still-valid, unverified OTP instead of generating a new one.
        // This prevents one user from spamming admins with many concurrent codes.
        if (session.Otp is { } existing
            && !existing.IsVerified
            && now <= existing.ExpiresAtUtc)
        {
            return new IssueOtpResult
            {
                Status = IssueOtpStatus.AlreadyPending,
                Otp = existing,
            };
        }

        // Per-user cooldown so a single client cannot flood the admin inbox.
        var sinceLast = now - session.LastOtpRequestAtUtc;
        if (sinceLast < OtpRequestCooldown)
        {
            var remaining = (int)Math.Ceiling((OtpRequestCooldown - sinceLast).TotalSeconds);
            return new IssueOtpResult
            {
                Status = IssueOtpStatus.Cooldown,
                CooldownSecondsRemaining = Math.Max(1, remaining),
            };
        }

        var code = Random.Shared.Next(100000, 999999).ToString("D6");
        // Short request id (4 hex chars) — enough to tell concurrent admin notifications apart.
        var requestId = Random.Shared.Next(0x1000, 0xFFFF).ToString("X4");

        session.Otp = new OtpState
        {
            RequestId = requestId,
            Code = code,
            IssuedAtUtc = now,
            ExpiresAtUtc = now.Add(OtpLifetime),
            IsVerified = false,
            DeliveredToClient = false,
        };
        session.LastOtpRequestAtUtc = now;

        return new IssueOtpResult
        {
            Status = IssueOtpStatus.Issued,
            Otp = session.Otp,
        };
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

    /// <summary>
    /// Marks the current OTP as delivered to the client (admin pressed the button)
    /// and records which admin approved it. Returns false if there is no live OTP
    /// for that chat, or if it was already delivered.
    /// </summary>
    public bool MarkOtpDelivered(long chatId, string approvedByDisplayName, long approvedByUserId)
    {
        var session = Get(chatId);
        var otp = session.Otp;
        if (otp is null || DateTimeOffset.UtcNow > otp.ExpiresAtUtc)
            return false;
        if (otp.DeliveredToClient)
            return false;
        otp.DeliveredToClient = true;
        otp.ApprovedByDisplayName = approvedByDisplayName ?? "";
        otp.ApprovedByUserId = approvedByUserId;
        otp.ApprovedAtUtc = DateTimeOffset.UtcNow;
        return true;
    }

    public void ResetOtp(long chatId) => Get(chatId).Otp = null;

    /// <summary>
    /// Snapshot of all chats that currently have a live (non-expired, unverified) OTP.
    /// Useful for diagnostics; not required for normal operation.
    /// </summary>
    public IReadOnlyList<(long ChatId, OtpState Otp)> GetPendingOtps()
    {
        var now = DateTimeOffset.UtcNow;
        var list = new List<(long, OtpState)>();
        foreach (var kv in _sessions)
        {
            var otp = kv.Value.Otp;
            if (otp is not null && !otp.IsVerified && now <= otp.ExpiresAtUtc)
                list.Add((kv.Key, otp));
        }
        return list;
    }
}
