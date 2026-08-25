# WinUI 3 Migration — Phase 3, eighth page: native Messages

**Date:** 2026-08-25
**Status:** Draft, awaiting review
**Branch:** `claude/blazor-winui3-migration-a10c79`
**Builds on:** Dashboard, Pilot Hub, Flights, the landing-result dialog, Routes, Fleet, and Events
— reuses established conventions throughout (icon-as-`Path` reproduction, `Border`-not-`Button`,
the tab-bar `Border`-active-state technique, the `EventsService`-style thin-wrapper ViewModel
shape).

## Goal

Convert `Messages.razor` (route `/messages`) to a native WinUI 3 `Page`. The largest remaining
real feature: direct-message chat (friend list + conversation + compose), a public broadcast
channel, and friend management (add by code, remove) — three tabs, all backed by the same
already-DI-registered `MessagingService` that `ShellWindow` already uses natively for the nav
rail's unread badge (Phase 2 work).

## What's different about this page

- **`MessagingService` is already live-wired natively.** `ShellWindow.xaml.cs` already injects it
  and subscribes to `UnreadCountChanged` for the sidebar badge. This page reuses the exact same
  singleton and the exact same two events (`NewMessageReceived`, `UnreadCountChanged`) — no new
  service wiring, just a second, page-scoped subscription (unsubscribed on page unload, matching
  `Messages.razor`'s own `IDisposable.Dispose()`).
- **Real-time-ish push, not just a `Loaded`-handler fetch.** Unlike every prior converted page,
  this one must react to events arriving *while the page is open* (`OnNewMessage` triggers a full
  `RefreshAll()` + re-render; `OnUnreadCountChanged` updates the inbox tab's badge count). The
  native page needs the same two subscriptions, both marshaled onto the UI thread — `DispatcherQueue.TryEnqueue`,
  the same cross-thread-update pattern this project already uses for the 20Hz telemetry pump (see
  `docs/superpowers/notes/20hz-batching-reference.md`), not `InvokeAsync` (that's Blazor-specific).
- **ACARS-key gate at the very top**, not per-action — the whole page (all three tabs) is replaced
  by a single "ACARS Key Required" empty state when `SettingsService.Settings.AcarsKey` is empty,
  with a CTA to Settings. Different from Events, where the gate is per-action (register/create)
  with the rest of the page still browsable.
- **A deep-link parameter** (`[SupplyParameterFromQuery(Name = "open")] OpenPilotId`) — used
  elsewhere in the app to jump straight into a conversation (e.g. from a friend's profile or the
  live map). The native page doesn't have Blazor query params; this needs a different mechanism —
  see "What this spec explicitly does not do" below.

## Decision: a new `MessagesViewModel`

Like Flights/Routes/Events before it, `Messages.razor` has no ViewModel — logic lives directly in
its `@code` block against `MessagingService`/`SettingsService`. This spec builds
`MessagesViewModel` for consistency, following the same thin-wrapper shape `EventsViewModel`
established (plain async methods, not `[RelayCommand]`-wrapped).

## What gets built

### `AviatesAirTracker/ViewModels/MessagesViewModel.cs` (new)

- `ObservableCollection<PilotMessage> InboxMessages`, `ObservableCollection<PilotMessage> BroadcastMessages`,
  `ObservableCollection<FriendEntry> Friends`, `ObservableCollection<PilotMessage> Conversation`,
  `int UnreadCount`, `bool HasAcarsKey`.
- `RefreshAllAsync()` — wraps the original's `RefreshAll()` exactly: inbox, broadcasts (ordered by
  `SentAt`), friends, unread count, and (if a friend is currently selected) rebuilds `Conversation`
  via the same filter logic as `GetConversation(friendId)`.
- `string MyPilotId => _messaging.MyPilotId` passthrough (needed by the page to tell "mine" vs.
  "theirs" bubbles).
- `SendDirectAsync(string recipientId, string content)` / `SendBroadcastAsync(string content)` —
  thin wrappers returning `(bool Success, string? Error)` via `MessagingService.LastSendError`,
  matching the original's exact optimistic-clear-then-restore-on-failure UX (the page owns the
  compose-box clear/restore, not the ViewModel).
- `AddFriendByCodeAsync(string code, string? name)` / `RemoveFriendAsync(string pilotId)` — thin
  wrappers.
- `MarkReadAsync(Guid messageId)` — thin wrapper, called for every currently-unread message from
  a friend the moment their conversation is opened (matches `SelectFriend`'s fire-and-forget
  `_ = MessagingService.MarkReadAsync(...)` loop).
- Event subscription lives on the **page**, not the ViewModel (an `EventHandler` field owned by a
  `Page` that outlives typical DI-singleton-ViewModel lifetime assumptions is simpler to
  unsubscribe correctly on `Unloaded` — matches every other page's "page owns its own UI-local
  state" placement decision already established for `_activeTab` etc.).

### `AviatesAirTracker/Pages/MessagesPage.xaml`/`.xaml.cs` (new)

- ACARS-key gate: if `!HasAcarsKey`, show only the "ACARS Key Required" empty state (icon,
  title, sub-copy, a CTA `Border`-button navigating to the native `SettingsPage` — **not yet
  built**; see "What this spec explicitly does not do"), nothing else.
- Tab bar: Inbox (with an unread-count badge, shown only when `> 0`) / Broadcast / Friends (with
  a friend-count badge, shown only when `> 0`) — same `Border`-active-state technique as every
  prior page. Switching tabs clears `_addFriendStatus`/`_dmSendError`/`_broadcastSendError`
  (page-local fields), matching `SwitchTab`'s exact reset.
- **Inbox tab**: two-column layout — friend list (left, avatar-initials circle, name, rank, an
  unread-dot per friend with any unread messages, empty state "Add friends to send direct
  messages") and a conversation panel (right): empty state when no friend selected ("Select a
  friend to start a conversation"), else a header (avatar, name, rank, a "Remove" `Border`-button
  calling `RemoveFriendAsync` then clearing selection if it was the removed friend), a scrolling
  bubble list (mine/theirs alignment and color, per-bubble relative timestamp via the exact
  `FormatTime` logic below, empty state "No messages yet — say hello!"), an inline send-error
  banner, and a compose row (`TextBox` + send `Border`-button, Enter-to-send unless Shift is held,
  disabled while empty or sending, a `ProgressRing` swapped in for the send icon while sending).
  Selecting a friend marks their unread messages read (fire-and-forget, matching the original)
  and switches to their conversation.
- **Broadcast tab**: a single public feed (header "AviatesAir Pilot Channel" + a "Public" badge),
  the same bubble-list/compose pattern as Inbox but single-column and everyone's messages show a
  sender-name label above non-mine bubbles (mine bubbles don't), empty state "No broadcasts yet.
  Be the first to post!", 280-char compose limit (vs. Inbox's 500).
- **Friends tab**: two side-by-side cards — "Add a Pilot" (friend-code `TextBox`, uppercase/mono
  styling, 9-char max matching the `XXXX-XXXX` format, a name-fallback `TextBox`, inline
  success/error status text, an "Add Pilot" `Border`-button disabled until the code is 9
  characters or while a lookup is in flight, with a loading-spinner + "Looking up pilot..." state)
  and "Your Crew — N pilot(s)" (the friends list: avatar, name, rank + "Added {date}", a "Message"
  `Border`-button that switches to Inbox and selects that friend, and a remove-friend icon
  `Border`-button) — empty state "No friends added yet. Enter a Friend Code to get started."
- `FormatTime(DateTime sentAt)`: exact relative-time logic from the original — treat
  `DateTimeKind.Unspecified` as UTC, clock-skew guard (negative diff clamped to zero), "just now"
  (<30s), "{m}m ago" (<60min), "{h}h ago" (<24h), else `"MMM d, HH:mm"` in local time. Port
  verbatim, don't approximate.
- `GetInitials(string name)`: first letter of up to the first 2 words, uppercased, `"?"` for
  blank/whitespace — port verbatim.
- Live-update subscriptions: `MessagingService.NewMessageReceived` → refresh everything and
  rebuild the UI (marshaled via `DispatcherQueue.TryEnqueue`); `MessagingService.UnreadCountChanged`
  → update just the unread badge (same marshaling). Both unsubscribed in the page's `Unloaded`
  handler (WinUI's `Page` doesn't have Blazor's `IDisposable.Dispose()` lifecycle, `Unloaded` is
  the native equivalent for this).

### `AviatesAirTracker/Navigation/NavDestinations.cs` (modified)

`"messages"` entry gets `NativePageType: typeof(Pages.MessagesPage)` — one line.

## What this spec explicitly does not do

- Does not touch `Messages.razor` — left in place, unreferenced.
- Does not modify `MessagingService`, `PilotMessage`, or `FriendEntry` — page-only conversion.
- **Does not build the native `SettingsPage`** — it doesn't exist yet (no prior spec has covered
  Settings, the largest remaining page at 1058 lines in the legacy Blazor app). The "ACARS Key
  Required" CTA button is built pointing at `typeof(Pages.SettingsPage)`, but until that page
  exists this spec leaves the button either disabled with a tooltip, or wired to a placeholder
  that does nothing yet — implementer's call, noted clearly in a code comment either way. Settings
  is a separate, future conversion, not silently smuggled into this one.
- **Does not implement the `?open={pilotId}` deep-link parameter.** The legacy Blazor version
  reads it via `[SupplyParameterFromQuery]`; native `Frame.Navigate` has no equivalent built-in
  query-string mechanism. If this capability is still needed (e.g. a "Message" button somewhere
  else in the app that should jump straight into a conversation), it would need a small
  `Frame.Navigate(typeof(MessagesPage), pilotIdString)` parameter-passing mechanism as a
  follow-up — out of scope here since nothing in the currently-converted native pages actually
  triggers this deep link yet (grep confirmed: no native page currently constructs a `/messages?open=`
  URL, so there's no live caller to preserve).
- Does not touch any other nav destination.

## Verification

- Build clean, no new warnings.
- Visual: native Messages compared against the current Blazor page — ACARS-key gate, all three
  tabs, chat bubbles (mine/theirs), broadcast feed, add/remove-friend flow.
- Functional: sending a DM/broadcast, receiving one live while the page is open (via the existing
  30s poll this service already runs — same live-data dependency every network-gated check in
  this migration has needed), unread-count badge updates, add-friend-by-code success/failure
  paths, remove-friend. Needs a working backend connection and ACARS key to fully exercise —
  defer explicitly if unavailable, same as every prior live-network-gated check.
