# WinUI 3 Migration — Phase 3, eighth page: native Messages — implementation plan

**Date:** 2026-08-25
**Branch:** `claude/blazor-winui3-migration-a10c79`
**Design spec:** `docs/superpowers/specs/2026-08-25-winui3-phase3-messages-native-design.md`
(read this first — this plan assumes it, doesn't repeat it; the spec has two amendments from a
same-session bug fix — read those too, they change what "matching the original" means here)

## Before you start

- **Two bugs were found and fixed in the shared/legacy code during this same session, before
  this plan was written — port the fixed behavior, not the original's bugs**:
  1. `Messages.razor`'s `SendDirect()` refreshed `_inboxMessages` after sending but never
     recomputed `_conversation` (the field the bubble list actually renders from), so a just-sent
     message stayed invisible until the conversation was reopened. Fixed in commit `d054ba7`. The
     native page's send handler must refresh the inbox **and** rebuild the currently-selected
     friend's conversation — not just the former.
  2. `app.css` gained a `bubble-in` keyframe animation (`transform`/`opacity` only, spring easing)
     applied to `.msg-bubble-wrap`, also commit `d054ba7`. The native page needs an equivalent
     `Storyboard`-based appear animation on newly-added bubbles in both Inbox and Broadcast.
- Known, recurring WinUI3-SDK crash traps in this project (hit in every task of every plan so
  far): no `<Style>` element anywhere in any `.xaml` file (silently crashes `XamlCompiler.exe`,
  zero diagnostics, no compile error shown); no literal double-hyphen `--` inside any XAML
  comment (invalid XML, same silent crash — use a single hyphen or `──` instead). Run
  `grep -n -- "--" <file>.xaml | grep -v '<!--\|-->'` on every XAML file you touch before
  considering a task done — this has recurred in literally every task of every plan this session.
- `CS8852`: don't bind an init-only `record`/`record struct` to any `ItemsControl` — use a plain
  mutable class with `{ get; set; }` for any display-row wrapper type. Note: `PilotMessage` and
  `FriendEntry` (`Models/FlightModels.cs`) are already plain mutable classes, safe to bind
  directly if convenient — a wrapper `*DisplayRow` type is only needed if a task wants
  precomputed display strings (initials, formatted time, mine/theirs flags) the way every prior
  page's display-row classes do; implementer's call per task, note which was chosen.
- `Border`-not-`Button`: no `<Style>` means `Button`'s `CornerRadius` doesn't reliably render on
  this SDK. Use `Border` + `Tapped` for tabs, send buttons, the remove-friend/add-friend buttons,
  and the "Message" button in the Friends tab.
- Cross-thread UI updates from service events: `MessagingService.NewMessageReceived`/
  `UnreadCountChanged` can fire from a background poll thread. Every UI mutation in response must
  be marshaled via `DispatcherQueue.TryEnqueue(...)` — this is a new requirement for this
  migration (every prior page's initial data load happened once, on the UI thread, inside an
  `async Loaded` handler; this page also has to react to pushes arriving later, from a thread
  that isn't the UI thread).
- `ShellWindow.xaml.cs` already has a working example of subscribing to `MessagingService` events
  natively (for the nav rail's unread badge) — read it first for the exact
  subscribe/unsubscribe/dispatch pattern already proven in this codebase, don't invent a new one.
- Read `AviatesAirTracker.LegacyWeb/Pages/Messages.razor` (515 lines, now with the Task-0 fix
  already applied) and `AviatesAirTracker/Services/MessagingService.cs` directly for exact
  method signatures, copy, and conditional logic — this plan summarizes, it doesn't transcribe
  verbatim.

## Task 1 — `MessagesViewModel`

Files: `AviatesAirTracker/ViewModels/MessagesViewModel.cs` (new).

1. `ObservableObject` with `MessagingService`, `SettingsService` constructor deps (both already
   `AddSingleton` in `App.xaml.cs` — confirm the exact registration lines, and **register this
   new ViewModel too** — Task 1 of the Events plan forgot this exact step and it had to be fixed
   in Task 2, don't repeat that miss here).
2. `ObservableCollection<PilotMessage> InboxMessages`, `ObservableCollection<PilotMessage> BroadcastMessages`,
   `ObservableCollection<FriendEntry> Friends`, `ObservableCollection<PilotMessage> Conversation`,
   `[ObservableProperty] int UnreadCount`, `[ObservableProperty] bool HasAcarsKey`.
3. `RefreshAllAsync()`: `HasAcarsKey = !string.IsNullOrEmpty(_settings.Settings.AcarsKey)`; if
   false, return early (matching `OnInitializedAsync`'s own early return). Otherwise: populate
   `InboxMessages` from `GetInboxAsync()`, `BroadcastMessages` from `GetBroadcastsAsync()` ordered
   by `SentAt`, `Friends` from `GetFriendsAsync()`, `UnreadCount` from `GetUnreadCountAsync()`, and
   — **the fixed behavior, not the original bug** — if a friend is currently selected (track the
   selected id as a ViewModel field, e.g. `SelectedFriendId`), rebuild `Conversation` from the
   refreshed `InboxMessages` too.
4. `GetConversation(string friendId)`: exact port of the original's filter — messages where
   (`SenderId == friendId && RecipientId == MyPilotId`) or (`SenderId == MyPilotId && RecipientId == friendId`),
   ordered by `SentAt`.
5. `SelectFriendAsync(string pilotId)`: sets `SelectedFriendId`, rebuilds `Conversation` via
   `GetConversation`, and fire-and-forgets `MarkReadAsync` for every currently-unread message from
   that friend (matching the original's exact fire-and-forget loop, not awaited).
6. `SendDirectAsync(string recipientId, string content)`: calls `MessagingService.SendDirectAsync`,
   returns `(bool Success, string? Error)` via `MessagingService.LastSendError` on failure. On
   success, refresh `InboxMessages` **and** rebuild `Conversation` for `SelectedFriendId` (the
   fix — see "Before you start"). The page still owns the compose-box optimistic-clear/restore UX
   itself, this method doesn't touch any compose-box state.
7. `SendBroadcastAsync(string content)`: calls `MessagingService.SendBroadcastAsync`, same
   `(bool Success, string? Error)` shape, refreshes `BroadcastMessages` (ordered by `SentAt`) on
   success.
8. `AddFriendByCodeAsync(string code, string? name)`: thin wrapper around
   `MessagingService.AddFriendByCodeAsync`, refreshes `Friends` on success.
9. `RemoveFriendAsync(string pilotId)`: thin wrapper around `MessagingService.RemoveFriendAsync`,
   refreshes `Friends`, and clears `SelectedFriendId`/`Conversation` if the removed friend was the
   one currently selected (matching the original).
10. `string MyPilotId => _messaging.MyPilotId` passthrough property.
11. Build (`dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`) — 0 errors.
    **Confirm `services.AddSingleton<MessagesViewModel>();` is actually present in
    `App.xaml.cs`** before calling this task done.
12. Commit: `feat(messages): MessagesViewModel, ported from Messages.razor's @code block`.

## Task 2 — Page skeleton, ACARS gate, tab bar, Inbox tab

Files: `AviatesAirTracker/Pages/MessagesPage.xaml` (new),
`AviatesAirTracker/Pages/MessagesPage.xaml.cs` (new).

1. `MessagesPage : Page`, `MessagesViewModel` via `App.GetService<MessagesViewModel>()`,
   `SettingsService` via `App.GetService<SettingsService>()`. `Loaded` handler:
   `await _viewModel.RefreshAllAsync()` then build the UI.
2. ACARS-key gate: if `!_viewModel.HasAcarsKey`, show only the empty state (icon, "ACARS Key
   Required" title, "Configure your ACARS key in Settings to use the messaging system." sub-copy,
   a "Go to Settings" CTA `Border`-button) — nothing else renders. Since the native `SettingsPage`
   doesn't exist yet, wire the CTA as a disabled/no-op with a code comment explaining why (per
   the design spec's explicit scoping — don't build a placeholder Settings page, don't silently
   drop the button either).
3. Tab bar (Inbox / Broadcast / Friends), same `Border`-active-state technique as every prior
   page. Inbox shows an unread-count badge (only when `> 0`), Friends shows a friend-count badge
   (only when `> 0`). Switching tabs clears any page-local status/error text fields (matching
   `SwitchTab`'s reset).
4. Inbox tab layout: two-column — friend list (left) and conversation panel (right).
   - Friend list: avatar-initials circle (`GetInitials` ported verbatim: first letter of up to 2
     words, uppercased, `"?"` for blank), name, rank, an unread-dot when any unread message exists
     from that friend, empty state "Add friends to send direct messages". Tapping a friend calls
     `await _viewModel.SelectFriendAsync(id)` then rebuilds.
   - Conversation panel: empty state "Select a friend to start a conversation" when none selected.
     Otherwise: header (avatar, name, rank, a "Remove" `Border`-button calling
     `await _viewModel.RemoveFriendAsync(id)` then rebuilding), a scrolling bubble list (mine
     right-aligned/accent-colored, theirs left-aligned/surface-colored, matching `.msg-bubble`'s
     exact CSS shape — rounded with one flattened corner on the "tail" side), a per-bubble
     relative timestamp via `FormatTime` (port verbatim — see below), empty state "No messages
     yet — say hello!", an inline send-error banner, and a compose row (`TextBox`, 500-char max,
     Enter-to-send unless Shift held, a send `Border`-button disabled while empty or sending with
     a `ProgressRing` swapped in while sending).
   - Send handler: optimistic clear (grab the text, clear the box, rebuild), call
     `await _viewModel.SendDirectAsync(...)`; on failure restore the text into the box and show
     the inline error; either way, done — `SendDirectAsync` already handles its own
     inbox/conversation refresh per Task 1's fix.
   - **Bubble-in animation**: when rebuilding the bubble list, animate in only the bubble(s) newly
     added since the last rebuild (track the previous count or a set of already-seen message ids)
     via a `Storyboard` on `Opacity` (0→1, ~0.32s) plus a `CompositeTransform`/`TranslateTransform`+
     `ScaleTransform` (`translateY(10px) scale(0.96)` → identity), matching the CSS `bubble-in`
     keyframe's feel and duration. Do not animate every bubble on every rebuild — only genuinely
     new ones, or the whole list will visibly jump/flash every time a tab is revisited.
5. `FormatTime(DateTime sentAt)`: port verbatim — `DateTimeKind.Unspecified`/`Local` normalized to
   UTC, negative diff clamped to zero (clock-skew guard), "just now" (<30s), "{m}m ago" (<60min),
   "{h}h ago" (<24h), else `"MMM d, HH:mm"` in local time.
6. Build — 0 errors. Check the XAML `--`-in-comment trap.
7. Commit: `feat(messages): native page skeleton, ACARS gate, tabs, and Inbox tab`.

## Task 3 — Broadcast tab and Friends tab

Files: `AviatesAirTracker/Pages/MessagesPage.xaml`/`.xaml.cs` (modified).

1. Broadcast tab: single-column feed (header "AviatesAir Pilot Channel" + a "Public" badge), the
   same bubble-list pattern as Inbox (reuse the bubble-building helper from Task 2 rather than
   duplicating it — a shared method taking the message list + "is broadcast" flag for the
   sender-name-label difference is cleaner than copy-pasting) but every non-mine bubble shows a
   sender-name label above it (mine bubbles never do), empty state "No broadcasts yet. Be the
   first to post!", compose row with a 280-char max (not 500). Same bubble-in animation applies
   here too — this is explicitly called out in the design spec as "both tabs", don't skip it.
2. Friends tab: two side-by-side cards.
   - "Add a Pilot": a friend-code `TextBox` (uppercase/mono styling via `CharacterCasing`/
     `FontFamily`, 9-char max matching the `XXXX-XXXX` format), a name-fallback `TextBox` (60-char
     max), inline success/error status text (green/red per the original's `_addFriendError`
     flag), an "Add Pilot" `Border`-button disabled until the code is exactly 9 characters or
     while a lookup is in flight (with a `ProgressRing` + "Looking up pilot..." state while busy).
     On success: clear both fields, show the success status ("{name} added to your crew."),
     refresh the friends list. On failure ("Friend code not found. Check the code and try
     again."): show the error status, don't clear the fields.
   - "Your Crew — N pilot(s)" (singular/plural matching the original's `@(_friends.Count == 1 ? "" : "s")`):
     avatar, name, rank + "Added {MMM d}", a "Message" `Border`-button that switches to the Inbox
     tab and selects that friend (reuse Task 2's select-friend handler), and a remove-friend icon
     `Border`-button. Empty state: "No friends added yet. Enter a Friend Code to get started."
3. Build — 0 errors. Compare warnings against baseline (15 pre-existing, none referencing
   Messages) — flag any new warning.
4. Commit: `feat(messages): native Broadcast and Friends tabs`.

## Task 4 — Live-update subscriptions

Files: `AviatesAirTracker/Pages/MessagesPage.xaml.cs` (modified).

1. Read `ShellWindow.xaml.cs`'s existing `MessagingService` subscription (for the nav rail's
   unread badge) directly — this is the proven pattern for this exact service in this exact app,
   copy its subscribe/unsubscribe/dispatch shape rather than inventing a new one.
2. Subscribe to `MessagingService.NewMessageReceived` in the page's constructor or `Loaded`
   handler: on fire, marshal via `DispatcherQueue.TryEnqueue(...)` to call
   `await _viewModel.RefreshAllAsync()` then rebuild the whole page's content (matching the
   original's `OnNewMessage` → full `RefreshAll()` + `StateHasChanged()`).
3. Subscribe to `MessagingService.UnreadCountChanged`: on fire, marshal via `DispatcherQueue.TryEnqueue`
   to update just the unread badge on the Inbox tab (matching the original's narrower
   `OnUnreadCountChanged`, which only touches `_unreadCount` + re-render, not a full refresh).
4. Unsubscribe both in the page's `Unloaded` handler — WinUI's `Page` has no `IDisposable.Dispose()`
   lifecycle the way a Blazor component does; `Unloaded` is the native equivalent here. A page
   that navigates away without unsubscribing will leak a subscription and keep receiving/reacting
   to events after it's no longer visible — check this carefully, it's a real memory/behavior bug
   if missed, not just style.
5. Build — 0 errors, compare warnings against baseline.
6. Commit: `feat(messages): live NewMessageReceived/UnreadCountChanged subscriptions`.

## Task 5 — Nav wiring and final verification

Files: `AviatesAirTracker/Navigation/NavDestinations.cs` (modified).

1. `"messages"` entry gets `NativePageType: typeof(Pages.MessagesPage)` — one line, matching every
   prior page's pattern.
2. Build — 0 errors, compare warnings against baseline (15 pre-existing, none referencing
   Messages/NavDestinations) and flag anything new.
3. Verification pass (read-only code review, no computer-use/live-app testing — that's reserved
   for the orchestrating session): re-read the full diff across all 4 prior tasks against the
   design spec. Confirm: the send-refresh fix is actually present (a sent DM appears immediately
   in the native page's conversation, not just after reopening — trace the code path, don't just
   trust a prior task's own claim); the bubble-in animation only animates genuinely-new bubbles,
   not the whole list on every rebuild; the `Unloaded` handler actually unsubscribes both events;
   the ACARS-key gate hides all three tabs, not just some; Friends tab's singular/plural "pilot"
   text and the 9-character friend-code gating are exactly right. Note explicitly whether this
   environment appears to have a working backend connection and a configured ACARS key (check
   `SettingsService`/`AppSettings` without attempting network calls yourself) — defer the
   send/receive/add-friend live-functional check explicitly if not available, rather than
   skipping it silently.
4. Commit: `feat(messages): wire native MessagesPage into navigation`.

## Verification (after all five tasks land)

- Build clean, no new warnings.
- Visual: compare native Messages against the current (bug-fixed) Blazor page — ACARS gate, all
  three tabs, chat bubbles (mine/theirs, animation), broadcast feed, add/remove-friend flow.
- Functional: sending a DM appears immediately without reopening the page (the fix, verified
  natively this time); receiving a live push while the page is open updates the UI; unread badge
  updates on both the page's own tab and (already-working, don't break it) the shell's nav rail;
  add-friend success/failure paths; remove-friend. Live-backend testing deferred if this
  environment has no working connection/ACARS key configured, per the design spec.
