using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Models;
using Serilog;
using System.Text.RegularExpressions;

namespace AviatesAirTracker.Services;

// ============================================================
// MESSAGING SERVICE
//
// Manages pilot-to-pilot direct messages and broadcast channel.
// - Polls backend every 30 seconds for new messages.
// - Every message is authenticated via ACARS key (Bearer token),
//   which the backend records for moderation — never exposed to
//   other clients.
// - Friends are identified by their friend code (XXXX-XXXX),
//   derived from their ACARS key server-side.
// ============================================================

public class MessagingService : IDisposable
{
    private readonly IMessageRepository _messageRepo;
    private readonly IFriendRepository  _friendRepo;
    private readonly AviatesBackendClient _backend;
    private readonly SettingsService    _settings;
    private readonly System.Threading.Timer _pollTimer;
    private bool _disposed;

    public event EventHandler<PilotMessage>? NewMessageReceived;
    public event EventHandler<int>? UnreadCountChanged;

    public int    UnreadCount        { get; private set; }
    /// <summary>Human-readable reason for the last failed send (content blocked, network error, etc.).</summary>
    public string? LastSendError     { get; private set; }

    // =====================================================
    // CONTENT FILTER
    // Client-side pre-filter before a message reaches the backend.
    // The backend performs its own moderation; this is an early
    // gate that gives immediate feedback to the sender.
    // =====================================================

    private static readonly HashSet<string> _blockedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "fuck", "fucker", "fucking", "fucked", "fucks",
        "shit", "shitting", "bullshit",
        "cunt", "cunts",
        "nigger", "niggers", "nigga", "niggas",
        "faggot", "faggots", "fag",
        "retard", "retarded",
        "asshole", "assholes",
        "bastard", "bastards",
        "cock", "cocks",
        "pussy", "pussies",
        "whore", "whores",
        "slut", "sluts",
        "kike", "spic", "chink", "wetback",
        "twat",
    };

    // Pre-compiled patterns for each blocked word (word-boundary match, case-insensitive).
    private static readonly Regex[] _blockPatterns = _blockedWords
        .Select(w => new Regex(@"\b" + Regex.Escape(w) + @"\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled))
        .ToArray();

    private static bool ContainsBlockedContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;

        // Normalise common leet/symbol substitutions used to bypass filters
        var normalized = content
            .Replace("@", "a").Replace("$", "s").Replace("0", "o")
            .Replace("1", "i").Replace("3", "e").Replace("4", "a")
            .Replace("5", "s").Replace("!", "i").Replace("+", "t")
            .Replace("*", "");

        return _blockPatterns.Any(p => p.IsMatch(normalized));
    }

    public MessagingService(
        IMessageRepository messageRepo,
        IFriendRepository  friendRepo,
        AviatesBackendClient backend,
        SettingsService    settings)
    {
        _messageRepo = messageRepo;
        _friendRepo  = friendRepo;
        _backend     = backend;
        _settings    = settings;

        // Poll every 30 seconds; first tick fires after 5 seconds.
        _pollTimer = new System.Threading.Timer(
            async _ => await PollAsync(),
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30));
    }

    // =====================================================
    // IDENTITY HELPERS
    // =====================================================

    /// <summary>
    /// Returns the configured PilotId, or a stable ID derived from the ACARS key when the
    /// setting is blank (e.g. the backend did not return one during auth).
    /// Must be consistent so locally-stored messages match backend-fetched ones.
    /// </summary>
    private string GetEffectivePilotId()
    {
        var id = _settings.Settings.PilotId;
        if (!string.IsNullOrEmpty(id)) return id;

        var key = _settings.Settings.AcarsKey;
        if (string.IsNullOrEmpty(key)) return "";

        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("AVIATES_PID_" + key));
        return "avt_" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    // =====================================================
    // POLLING
    // =====================================================

    private async Task PollAsync()
    {
        var acarsKey = _settings.Settings.AcarsKey;
        var pilotId  = GetEffectivePilotId();

        // Only the ACARS key is strictly required for auth; PilotId guards local storage.
        if (string.IsNullOrEmpty(acarsKey) || string.IsNullOrEmpty(pilotId)) return;

        try
        {
            // Fetch new direct messages
            var inboxMessages = await _backend.FetchInboxAsync(pilotId, acarsKey);
            foreach (var msg in inboxMessages)
            {
                var existing = (await _messageRepo.GetInboxAsync(pilotId))
                    .Any(m => m.Id == msg.Id);
                if (!existing)
                {
                    await _messageRepo.SaveAsync(msg);
                    NewMessageReceived?.Invoke(this, msg);
                    Log.Debug("[Messaging] New DM from {Sender}", msg.SenderName);
                }
            }

            // Fetch broadcasts
            var broadcasts = await _backend.FetchBroadcastsAsync(acarsKey);
            foreach (var msg in broadcasts)
            {
                var existing = (await _messageRepo.GetBroadcastsAsync())
                    .Any(m => m.Id == msg.Id);
                if (!existing)
                    await _messageRepo.SaveAsync(msg);
            }

            // Update unread count
            var newCount = await _messageRepo.GetUnreadCountAsync(pilotId);
            if (newCount != UnreadCount)
            {
                UnreadCount = newCount;
                UnreadCountChanged?.Invoke(this, UnreadCount);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Messaging] Poll error (non-critical)");
        }
    }

    // =====================================================
    // SEND
    // =====================================================

    /// <summary>
    /// Sends a direct message to a specific pilot.
    /// Returns false if the content is blocked, the ACARS key is missing, or the backend
    /// rejects the message — check <see cref="LastSendError"/> for the reason.
    /// </summary>
    public async Task<bool> SendDirectAsync(string recipientId, string content)
    {
        LastSendError = null;

        if (string.IsNullOrWhiteSpace(content)) return false;

        // Client-side content moderation gate
        if (ContainsBlockedContent(content))
        {
            LastSendError = "Your message contains inappropriate language and was not sent.";
            Log.Warning("[Messaging] DM blocked by content filter (recipient={Recipient})", recipientId);
            return false;
        }

        var acarsKey  = _settings.Settings.AcarsKey;
        var pilotId   = GetEffectivePilotId();
        var pilotName = _settings.Settings.PilotName;

        if (string.IsNullOrEmpty(acarsKey))
        {
            LastSendError = "No ACARS key configured. Go to Settings to add your key.";
            Log.Warning("[Messaging] Cannot send — no ACARS key configured");
            return false;
        }

        var msg = new PilotMessage
        {
            SenderId    = pilotId,
            SenderName  = pilotName,
            RecipientId = recipientId,
            Content     = content.Trim(),
            SentAt      = DateTime.UtcNow,
            Type        = MessageType.Direct
        };

        // Send to backend first. Only persist locally after the backend confirms receipt
        // so the UI correctly reflects delivery state rather than always showing optimistic success.
        var sent = await _backend.SendMessageAsync(msg, acarsKey);
        if (!sent)
        {
            LastSendError = "Message could not be delivered. Check your connection and try again.";
            Log.Warning("[Messaging] SendDirect to {Recipient} rejected by backend", recipientId);
            return false;
        }

        await _messageRepo.SaveAsync(msg);
        return true;
    }

    /// <summary>
    /// Sends a message to the broadcast channel (visible to all pilots).
    /// Returns false if the content is blocked, the ACARS key is missing, or the backend
    /// rejects the message — check <see cref="LastSendError"/> for the reason.
    /// </summary>
    public async Task<bool> SendBroadcastAsync(string content)
    {
        LastSendError = null;

        if (string.IsNullOrWhiteSpace(content)) return false;

        // Client-side content moderation gate
        if (ContainsBlockedContent(content))
        {
            LastSendError = "Your message contains inappropriate language and was not sent.";
            Log.Warning("[Messaging] Broadcast blocked by content filter");
            return false;
        }

        var acarsKey  = _settings.Settings.AcarsKey;
        var pilotId   = GetEffectivePilotId();
        var pilotName = _settings.Settings.PilotName;

        if (string.IsNullOrEmpty(acarsKey))
        {
            LastSendError = "No ACARS key configured.";
            return false;
        }

        var msg = new PilotMessage
        {
            SenderId    = pilotId,
            SenderName  = pilotName,
            RecipientId = "broadcast",
            Content     = content.Trim(),
            SentAt      = DateTime.UtcNow,
            Type        = MessageType.Broadcast
        };

        // Only save locally after backend confirms receipt.
        var sent = await _backend.SendMessageAsync(msg, acarsKey);
        if (!sent)
        {
            LastSendError = "Broadcast could not be delivered. Check your connection and try again.";
            Log.Warning("[Messaging] SendBroadcast rejected by backend");
            return false;
        }

        await _messageRepo.SaveAsync(msg);
        return true;
    }

    // =====================================================
    // FRIENDS
    // =====================================================

    /// <summary>
    /// Resolves a friend code against the backend and adds the pilot to the friends list.
    /// Returns the resolved FriendEntry on success, null if the code is invalid.
    /// </summary>
    public async Task<FriendEntry?> AddFriendByCodeAsync(string friendCode, string? displayName = null)
    {
        var acarsKey = _settings.Settings.AcarsKey;
        if (string.IsNullOrEmpty(acarsKey)) return null;

        friendCode = friendCode.Trim().ToUpperInvariant();

        // Prevent adding yourself
        var myCode = _settings.Settings.FriendCode;
        if (!string.IsNullOrEmpty(myCode) &&
            friendCode.Equals(myCode, StringComparison.OrdinalIgnoreCase))
            return null;

        var entry = await _backend.ResolveFriendCodeAsync(friendCode, acarsKey);

        // If the backend is offline/unavailable, fall back to a local entry.
        // Use the caller-supplied display name if provided, otherwise use the code.
        if (entry == null)
        {
            var name = !string.IsNullOrWhiteSpace(displayName)
                ? displayName.Trim()
                : "Pilot " + friendCode;
            entry = new FriendEntry
            {
                PilotId    = "fc:" + friendCode,
                PilotName  = name,
                FriendCode = friendCode,
                Rank       = "Pilot",
                AddedAt    = DateTime.UtcNow
            };
        }

        var alreadyAdded = await _friendRepo.ExistsAsync(entry.PilotId);
        if (!alreadyAdded)
            await _friendRepo.AddAsync(entry);

        return entry;
    }

    public async Task RemoveFriendAsync(string pilotId)
    {
        await _friendRepo.RemoveAsync(pilotId);
        await _backend.RemoveFriendAsync(pilotId, _settings.Settings.AcarsKey);
    }

    public Task<List<FriendEntry>> GetFriendsAsync() => _friendRepo.GetAllAsync();

    // =====================================================
    // INBOX / BROADCASTS (local cache)
    // =====================================================

    public Task<List<PilotMessage>> GetInboxAsync()
        => _messageRepo.GetInboxAsync(GetEffectivePilotId());

    public Task<List<PilotMessage>> GetBroadcastsAsync()
        => _messageRepo.GetBroadcastsAsync();

    public Task MarkReadAsync(Guid messageId)
        => _messageRepo.MarkReadAsync(messageId);

    public Task<int> GetUnreadCountAsync()
        => _messageRepo.GetUnreadCountAsync(GetEffectivePilotId());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Dispose();
    }
}
