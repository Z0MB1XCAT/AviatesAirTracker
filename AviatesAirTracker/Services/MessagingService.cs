using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Models;
using Serilog;
using System.Text.RegularExpressions;

namespace AviatesAirTracker.Services;

public class MessagingService : IDisposable
{
    private readonly IMessageRepository  _messageRepo;
    private readonly IFriendRepository   _friendRepo;
    private readonly AviatesBackendClient _backend;
    private readonly SettingsService     _settings;
    private readonly System.Threading.Timer _pollTimer;
    private readonly SemaphoreSlim  _pollGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _pilotIdLock = new();
    private string? _cachedPilotId;
    private bool _disposed;

    public event EventHandler<PilotMessage>? NewMessageReceived;
    public event EventHandler<int>?          UnreadCountChanged;

    public int    UnreadCount    { get; private set; }
    public string? LastSendError { get; private set; }
    public string MyPilotId => GetEffectivePilotId();

    // =====================================================
    // CONTENT FILTER
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

    private static readonly Regex[] _blockPatterns = _blockedWords
        .Select(w => new Regex(@"\b" + Regex.Escape(w) + @"\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled))
        .ToArray();

    private static bool ContainsBlockedContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;

        // Normalise common substitutions. Also strip zero-width and RTL override chars.
        var normalized = content
            .Replace("@", "a").Replace("$", "s").Replace("0", "o")
            .Replace("1", "i").Replace("3", "e").Replace("4", "a")
            .Replace("5", "s").Replace("!", "i").Replace("+", "t")
            .Replace("*", "").Replace("ph", "f")
            // Strip zero-width characters and RTL override
            .Replace("​", "").Replace("‌", "").Replace("‍", "")
            .Replace("‮", "").Replace("﻿", "");

        return _blockPatterns.Any(p => p.IsMatch(normalized));
    }

    public MessagingService(
        IMessageRepository  messageRepo,
        IFriendRepository   friendRepo,
        AviatesBackendClient backend,
        SettingsService     settings)
    {
        _messageRepo = messageRepo;
        _friendRepo  = friendRepo;
        _backend     = backend;
        _settings    = settings;

        _ = PollAsync();
        _pollTimer = new System.Threading.Timer(
            _ => { if (!_disposed) _ = PollAsync(); },
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));
    }

    // =====================================================
    // IDENTITY HELPERS
    // =====================================================

    private string GetEffectivePilotId()
    {
        lock (_pilotIdLock)
        {
            if (_cachedPilotId != null) return _cachedPilotId;
        }

        var id = _settings.Settings.PilotId;
        if (!string.IsNullOrEmpty(id))
        {
            lock (_pilotIdLock) { _cachedPilotId = id; }
            return id;
        }

        var key = _settings.Settings.AcarsKey;
        if (string.IsNullOrEmpty(key)) return "";

        // Fallback: derive a stable local ID from the ACARS key until the server ID is fetched.
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("AVIATES_PID_" + key));
        return "avt_" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    // =====================================================
    // POLLING
    // =====================================================

    private async Task EnsurePilotIdAsync(string acarsKey)
    {
        lock (_pilotIdLock)
        {
            if (!string.IsNullOrEmpty(_cachedPilotId)) return;
        }

        if (!string.IsNullOrEmpty(_settings.Settings.PilotId))
        {
            lock (_pilotIdLock) { _cachedPilotId = _settings.Settings.PilotId; }
            return;
        }

        try
        {
            var result = await _backend.ValidateAcarsKeyAsync(acarsKey);
            if (result != null && !string.IsNullOrEmpty(result.PilotId))
            {
                // Write to cached field under lock; persist settings on calling thread context
                lock (_pilotIdLock) { _cachedPilotId = result.PilotId; }
                _settings.Settings.PilotId = result.PilotId;
                _settings.Save();
                Log.Information("[Messaging] PilotId bootstrapped from auth: {Id}", result.PilotId);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Messaging] EnsurePilotId failed (non-critical, will retry next poll)");
        }
    }

    private async Task PollAsync()
    {
        // Guard: skip if a poll is already in flight
        if (!await _pollGate.WaitAsync(0)) return;
        try
        {
            await PollCoreAsync();
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task PollCoreAsync()
    {
        var acarsKey = _settings.Settings.AcarsKey;
        if (string.IsNullOrEmpty(acarsKey)) return;

        await EnsurePilotIdAsync(acarsKey);

        var pilotId = GetEffectivePilotId();
        if (string.IsNullOrEmpty(pilotId)) return;

        try
        {
            await RetrySendUnsyncedMessagesAsync(pilotId, acarsKey);

            // PERF-001: fetch existing IDs once into a HashSet instead of calling GetInboxAsync per message
            var inboxMessages  = await _backend.FetchInboxAsync(pilotId, acarsKey);
            var existingDmIds  = new HashSet<Guid>((await _messageRepo.GetInboxAsync(pilotId)).Select(m => m.Id));
            foreach (var msg in inboxMessages)
            {
                if (!existingDmIds.Contains(msg.Id))
                {
                    msg.SyncedToBackend = true;
                    await _messageRepo.SaveAsync(msg);
                    NewMessageReceived?.Invoke(this, msg);
                    Log.Debug("[Messaging] New DM from {Sender}", msg.SenderName);
                }
            }

            var broadcasts        = await _backend.FetchBroadcastsAsync(acarsKey);
            var existingBcastIds  = new HashSet<Guid>((await _messageRepo.GetBroadcastsAsync()).Select(m => m.Id));
            foreach (var msg in broadcasts)
            {
                if (!existingBcastIds.Contains(msg.Id))
                {
                    msg.SyncedToBackend = true;
                    await _messageRepo.SaveAsync(msg);
                }
            }

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

    private async Task RetrySendUnsyncedMessagesAsync(string pilotId, string acarsKey)
    {
        try
        {
            var allMessages = await _messageRepo.GetInboxAsync(pilotId);
            var unsynced = allMessages
                .Where(m => m.SenderId == pilotId && !m.SyncedToBackend)
                .ToList();

            foreach (var msg in unsynced)
            {
                var sent = await _backend.SendMessageAsync(msg, acarsKey);
                if (sent)
                {
                    msg.SyncedToBackend = true;
                    await _messageRepo.SaveAsync(msg);
                    Log.Debug("[Messaging] Synced unsent message {Id} to backend", msg.Id);
                }
                else
                {
                    msg.LastSyncAttempt = DateTime.UtcNow;
                    Log.Debug("[Messaging] Retry send failed for message {Id}, will retry next poll", msg.Id);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Messaging] Retry sync error (non-critical)");
        }
    }

    // =====================================================
    // SEND
    // =====================================================

    public async Task<bool> SendDirectAsync(string recipientId, string content)
    {
        LastSendError = null;

        // BUG-007: validate recipientId before proceeding
        if (string.IsNullOrWhiteSpace(recipientId))
        {
            LastSendError = "Recipient not specified.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(content)) return false;

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

        await _messageRepo.SaveAsync(msg);

        // PERF-003: use Task.Run instead of ContinueWith with async lambda
        _ = Task.Run(async () =>
        {
            try
            {
                var sent = await _backend.SendMessageAsync(msg, acarsKey);
                if (sent)
                {
                    msg.SyncedToBackend = true;
                    await _messageRepo.SaveAsync(msg);
                }
                else
                    Log.Debug("[Messaging] SendDirect to {Recipient} will retry on next poll", recipientId);
            }
            catch (Exception ex) { Log.Warning(ex, "[Messaging] Background SendDirect failed"); }
        });

        return true;
    }

    public async Task<bool> SendBroadcastAsync(string content)
    {
        LastSendError = null;

        if (string.IsNullOrWhiteSpace(content)) return false;

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

        await _messageRepo.SaveAsync(msg);

        _ = Task.Run(async () =>
        {
            try
            {
                var sent = await _backend.SendMessageAsync(msg, acarsKey);
                if (sent)
                {
                    msg.SyncedToBackend = true;
                    await _messageRepo.SaveAsync(msg);
                }
                else
                    Log.Debug("[Messaging] SendBroadcast will retry on next poll");
            }
            catch (Exception ex) { Log.Warning(ex, "[Messaging] Background SendBroadcast failed"); }
        });

        return true;
    }

    // =====================================================
    // FRIENDS
    // =====================================================

    public async Task<FriendEntry?> AddFriendByCodeAsync(string friendCode, string? displayName = null)
    {
        var acarsKey = _settings.Settings.AcarsKey;
        if (string.IsNullOrEmpty(acarsKey)) return null;

        friendCode = friendCode.Trim().ToUpperInvariant();

        var myCode = _settings.Settings.FriendCode;
        if (!string.IsNullOrEmpty(myCode) &&
            friendCode.Equals(myCode, StringComparison.OrdinalIgnoreCase))
            return null;

        var entry = await _backend.ResolveFriendCodeAsync(friendCode, acarsKey);

        // BUG-005: do NOT create a local fake entry when backend is offline.
        // A fake "fc:" PilotId will never match the real pilot's ID from the backend,
        // causing messages to be undeliverable even after connectivity is restored.
        if (entry == null) return null;

        var alreadyAdded = await _friendRepo.ExistsAsync(entry.PilotId);
        if (!alreadyAdded)
            await _friendRepo.AddAsync(entry);

        return entry;
    }

    public async Task RemoveFriendAsync(string pilotId)
    {
        await _friendRepo.RemoveAsync(pilotId);

        // BUG-006: only call backend if we have a valid key
        var acarsKey = _settings.Settings.AcarsKey;
        if (!string.IsNullOrEmpty(acarsKey))
            await _backend.RemoveFriendAsync(pilotId, acarsKey);
    }

    public Task<List<FriendEntry>> GetFriendsAsync() => _friendRepo.GetAllAsync();

    // =====================================================
    // INBOX / BROADCASTS (local cache)
    // =====================================================

    public Task<List<PilotMessage>> GetInboxAsync()
        => _messageRepo.GetInboxAsync(GetEffectivePilotId());

    public Task<List<PilotMessage>> GetBroadcastsAsync()
        => _messageRepo.GetBroadcastsAsync();

    public async Task MarkReadAsync(Guid messageId)
    {
        await _messageRepo.MarkReadAsync(messageId);
        // SEC-015: refresh unread count immediately after marking read
        var pilotId = GetEffectivePilotId();
        if (!string.IsNullOrEmpty(pilotId))
        {
            var newCount = await _messageRepo.GetUnreadCountAsync(pilotId);
            if (newCount != UnreadCount)
            {
                UnreadCount = newCount;
                UnreadCountChanged?.Invoke(this, UnreadCount);
            }
        }
    }

    public Task<int> GetUnreadCountAsync()
        => _messageRepo.GetUnreadCountAsync(GetEffectivePilotId());

    // BUG-004: cancel in-flight polls on dispose
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _pollTimer.Dispose();
        _pollGate.Dispose();
        _cts.Dispose();
    }
}
