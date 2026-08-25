using AviatesAirTracker.Models;
using AviatesAirTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace AviatesAirTracker.ViewModels;

/// <summary>
/// Ported from Messages.razor's @code block (verbatim behavior, not a redesign). Port the fixed
/// behavior, not the original's bug: Messages.razor's own SendDirect() originally refreshed
/// _inboxMessages after a successful send but never recomputed _conversation (the field the
/// bubble list actually renders from), so a just-sent message stayed invisible until the
/// conversation was reopened. Fixed in the legacy page (commit d054ba7) by also rebuilding the
/// conversation after a successful send, matching what RefreshAll() already did correctly.
/// SendDirectAsync below reproduces that fixed behavior.
///
/// Event subscription (NewMessageReceived/UnreadCountChanged) intentionally lives on the page,
/// not here -- see the design spec's "page owns its own UI-local state" placement decision.
/// </summary>
public partial class MessagesViewModel : ObservableObject
{
    private readonly MessagingService _messaging;
    private readonly SettingsService _settings;

    [ObservableProperty] private bool _hasAcarsKey;
    [ObservableProperty] private int _unreadCount;
    [ObservableProperty] private string? _selectedFriendId;

    public ObservableCollection<PilotMessage> InboxMessages { get; } = [];
    public ObservableCollection<PilotMessage> BroadcastMessages { get; } = [];
    public ObservableCollection<FriendEntry> Friends { get; } = [];
    public ObservableCollection<PilotMessage> Conversation { get; } = [];

    public string MyPilotId => _messaging.MyPilotId;

    public MessagesViewModel(MessagingService messaging, SettingsService settings)
    {
        _messaging = messaging;
        _settings = settings;
    }

    // Matches OnInitializedAsync's own early return: when there's no ACARS key, nothing else is
    // populated -- the page shows only the "ACARS Key Required" empty state.
    public async Task RefreshAllAsync()
    {
        HasAcarsKey = !string.IsNullOrEmpty(_settings.Settings.AcarsKey);
        if (!HasAcarsKey) return;

        var inbox = await _messaging.GetInboxAsync();
        InboxMessages.Clear();
        foreach (var m in inbox) InboxMessages.Add(m);

        var broadcasts = (await _messaging.GetBroadcastsAsync()).OrderBy(m => m.SentAt);
        BroadcastMessages.Clear();
        foreach (var m in broadcasts) BroadcastMessages.Add(m);

        var friends = await _messaging.GetFriendsAsync();
        Friends.Clear();
        foreach (var f in friends) Friends.Add(f);

        UnreadCount = await _messaging.GetUnreadCountAsync();

        // Fixed behavior (matches RefreshAll()'s own correct handling): rebuild the conversation
        // from the freshly-refreshed inbox whenever a friend is currently selected.
        if (SelectedFriendId != null)
        {
            Conversation.Clear();
            foreach (var m in GetConversation(SelectedFriendId)) Conversation.Add(m);
        }
    }

    private List<PilotMessage> GetConversation(string friendId)
    {
        var myId = MyPilotId;
        return InboxMessages
            .Where(m => (m.SenderId == friendId && m.RecipientId == myId) ||
                        (m.SenderId == myId && m.RecipientId == friendId))
            .OrderBy(m => m.SentAt)
            .ToList();
    }

    public Task SelectFriendAsync(string pilotId)
    {
        SelectedFriendId = pilotId;
        Conversation.Clear();
        foreach (var m in GetConversation(pilotId)) Conversation.Add(m);

        // Fire-and-forget, matching the original's exact `_ = MessagingService.MarkReadAsync(...)`
        // loop -- not awaited.
        var unread = InboxMessages.Where(m => m.SenderId == pilotId && m.ReadAt == null).ToList();
        foreach (var msg in unread)
            _ = MarkReadAsync(msg.Id);

        return Task.CompletedTask;
    }

    public Task MarkReadAsync(Guid messageId) => _messaging.MarkReadAsync(messageId);

    public async Task<(bool Success, string? Error)> SendDirectAsync(string recipientId, string content)
    {
        var ok = await _messaging.SendDirectAsync(recipientId, content);
        string? error = ok ? null : (_messaging.LastSendError ?? "Failed to send message.");

        // Fixed behavior -- the single most important correctness detail in this task: refresh
        // InboxMessages AND rebuild Conversation for the currently-selected friend, not just the
        // former (the original bug left a just-sent message invisible until the conversation was
        // reopened).
        var inbox = await _messaging.GetInboxAsync();
        InboxMessages.Clear();
        foreach (var m in inbox) InboxMessages.Add(m);

        if (SelectedFriendId != null)
        {
            Conversation.Clear();
            foreach (var m in GetConversation(SelectedFriendId)) Conversation.Add(m);
        }

        return (ok, error);
    }

    public async Task<(bool Success, string? Error)> SendBroadcastAsync(string content)
    {
        var ok = await _messaging.SendBroadcastAsync(content);
        string? error = ok ? null : (_messaging.LastSendError ?? "Failed to send broadcast.");

        var broadcasts = (await _messaging.GetBroadcastsAsync()).OrderBy(m => m.SentAt);
        BroadcastMessages.Clear();
        foreach (var m in broadcasts) BroadcastMessages.Add(m);

        return (ok, error);
    }

    public async Task<(bool Success, FriendEntry? Friend)> AddFriendByCodeAsync(string code, string? name)
    {
        var entry = await _messaging.AddFriendByCodeAsync(code, name);
        if (entry == null) return (false, null);

        var friends = await _messaging.GetFriendsAsync();
        Friends.Clear();
        foreach (var f in friends) Friends.Add(f);

        return (true, entry);
    }

    public async Task RemoveFriendAsync(string pilotId)
    {
        await _messaging.RemoveFriendAsync(pilotId);

        var friends = await _messaging.GetFriendsAsync();
        Friends.Clear();
        foreach (var f in friends) Friends.Add(f);

        // Matches the original's exact guard: only clear selection/conversation if the removed
        // friend was the one currently selected.
        if (SelectedFriendId == pilotId)
        {
            SelectedFriendId = null;
            Conversation.Clear();
        }
    }
}
