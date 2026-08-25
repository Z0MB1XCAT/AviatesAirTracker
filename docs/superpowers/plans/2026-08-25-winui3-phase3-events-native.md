# WinUI 3 Migration — Phase 3, seventh page: native Events — implementation plan

**Date:** 2026-08-25
**Branch:** `claude/blazor-winui3-migration-a10c79`
**Design spec:** `docs/superpowers/specs/2026-08-25-winui3-phase3-events-native-design.md` (read
this first — this plan assumes it, doesn't repeat it)

## Before you start

- Known, recurring WinUI3-SDK crash traps in this project (hit in every task of every plan so
  far): no `<Style>` element anywhere in any `.xaml` file (silently crashes `XamlCompiler.exe`,
  zero diagnostics, no compile error shown); no literal double-hyphen `--` inside any XAML
  comment (invalid XML, same silent crash — use a single hyphen or the `──` box-drawing character
  instead). Run `grep -n -- "--" <file>.xaml | grep -v '<!--\|-->'` on every XAML file you touch
  before considering a task done — this trap has recurred in literally every task of every plan
  this session, including ones written specifically to avoid it.
- `CS8852`: don't bind an init-only `record`/`record struct` to any `ItemsControl` — use a plain
  mutable class with `{ get; set; }` for any display-row wrapper type.
- `Border`-not-`Button`: no `<Style>` means `Button`'s `CornerRadius` doesn't reliably render on
  this SDK. Use `Border` + `Tapped` for tabs, the Create Event button, register/unregister
  buttons, and the dialog's Regenerate-style secondary actions.
- Responsive card/row width: if the event list or anything else wraps into a multi-column
  `ItemsWrapGrid`, port the `SizeChanged`/`ItemWidth`-recompute technique already established on
  `RoutesPage.xaml.cs`/`FleetPage.xaml.cs` (`*_SizeChanged`/`Update*GridColumns`) rather than a
  fixed-width card — this bug has already shipped and had to be fixed after the fact once
  (Routes) and been proactively avoided once (Fleet); don't repeat it a third time. Note: the
  event list itself is a single vertical column in the original (`display:flex;flex-direction:
  column`), not a wrapping grid — this note only applies if the implementer's own layout choice
  ends up wrapping (e.g. the featured-event art), not the main list.
- `ContentDialog` dark theming: `AviatesAirTracker/Dialogs/BookingDialog.cs` has the established
  fix for `ContentDialog`'s native Primary/Close buttons defaulting to stock Windows light chrome
  instead of the app's dark palette — `RequestedTheme = ElementTheme.Dark` on the dialog plus a
  `VisualTreeHelper` walk (`FindDescendant<Button>(dialog, "PrimaryButton"/"CloseButton")`) to
  recolor them directly. Apply this from the start on the Create Event dialog — don't ship the
  same "looks like stock Windows" bug that had to be fixed on Routes after the fact.
- Read `Events.razor` and its scoped `<style>` block directly for exact copy, class names, and
  conditional logic before writing XAML/C# — this plan summarizes, it doesn't transcribe
  verbatim. `AviatesAirTracker/Services/EventsService.cs` has the `AviatesEvent`/
  `CreateEventRequest` model shapes and the 5 service methods (`GetEventsAsync`,
  `GetMyEventsAsync`, `RegisterForEventAsync`, `UnregisterFromEventAsync`, `CreateEventAsync`) —
  read it directly rather than guessing signatures.

## Task 1 — `EventsViewModel`

Files: `AviatesAirTracker/ViewModels/EventsViewModel.cs` (new).

1. `ObservableObject` with `EventsService`, `SettingsService` constructor deps (both already
   `AddSingleton` in `App.xaml.cs` — confirm the exact registration lines before assuming).
2. `ObservableCollection<AviatesEvent> Events`, `AviatesEvent? FeaturedEvent`, `bool IsLoading`
   as `[ObservableProperty]`s.
3. `LoadEventsAsync(string tab)`: `"past"` → `GetEventsAsync("past")`; `"my"` with a non-empty
   `SettingsService.Settings.AcarsKey` → `GetMyEventsAsync(key)`; `"my"` with no key → empty list
   (no service call); anything else → `GetEventsAsync("upcoming")`. Sets `FeaturedEvent` to
   `Events.FirstOrDefault(e => e.IsFeatured)` after populating `Events`. Matches `LoadEvents()`'s
   exact switch in the original — read it directly, don't guess the branch conditions.
4. `RegisterAsync(int eventId, bool currentlyRegistered)`: calls
   `RegisterForEventAsync`/`UnregisterFromEventAsync` based on `currentlyRegistered`, returns
   `(bool Success, string Message)` for the page to display. Does **not** call `LoadEventsAsync`
   itself — matches the original's `HandleRegister`, which calls `LoadEvents()` as a separate
   step after setting the status message, not bundled into one call.
5. `CreateEventAsync(CreateEventRequest req)`: thin wrapper around
   `EventsService.CreateEventAsync(req, acarsKey)` — reads the ACARS key from
   `SettingsService.Settings.AcarsKey` itself so callers don't have to pass it. Returns
   `(bool Success, string Error)`. No validation inside this method — matches the original,
   where `CreateEvent()`'s three guard checks (ACARS key present, title non-empty, date
   non-empty) happen in the page/dialog before this is ever called, not inside the service
   wrapper. Don't add validation here that belongs in Task 3's dialog.
6. Build (`dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`) — 0 errors.
7. Commit: `feat(events): EventsViewModel, ported from Events.razor's @code block`.

## Task 2 — Page skeleton: header, tabs, featured hero, event list, empty states

Files: `AviatesAirTracker/Pages/EventsPage.xaml` (new),
`AviatesAirTracker/Pages/EventsPage.xaml.cs` (new).

1. `EventsPage : Page`, `EventsViewModel` via `App.GetService<EventsViewModel>()`,
   `SettingsService` via `App.GetService<SettingsService>()` (needed directly for the ACARS-key
   checks in three places, matching the original reading `SettingsService` itself rather than
   the ViewModel hiding it — see the design spec). `Loaded` handler: default tab `"upcoming"`,
   call `await _viewModel.LoadEventsAsync(_activeTab)`, then build the UI.
2. Header: "Events" title, "Group fly-ins, competitions, and community gatherings across the
   AviatesAir network." subtitle.
3. Status banner: dismissible, success/error visual variants, shown when a status message is
   set (page-local `_statusMessage`/`_isError` fields, matching the original — this state lives
   on the page, not the ViewModel, same placement decision Routes made for `_activeTab`/
   `_searchQuery`).
4. Tab bar (Upcoming / Past / My Events), same `Border`-active-state technique as Flights/Routes/
   Fleet, plus a "Create Event" `Border`-button alongside it (opens the dialog — stub as a no-op
   with a `// Task 3 wires this.` comment for this task, matching the exact stub pattern Routes'
   Task 2 used for its own "Book" button and Fleet's Task 1 used for its card tap). Switching
   tabs clears the status message and reloads (`await _viewModel.LoadEventsAsync(tab)`), matching
   `SwitchTab`'s exact behavior — including the early-return no-op if the same tab is tapped
   again.
5. Loading state: "Loading events…".
6. Featured-event hero card (Upcoming tab only, `_viewModel.FeaturedEvent != null`): eyebrow
   ("Featured Event · {Month Year}"), title, description, a Register/Unregister CTA wired the
   same way the event-list button is (Task 2 can wire this directly since it's simple enough —
   no dialog dependency). The decorative concentric-circle SVG is optional polish; a simplified
   or omitted version is fine, note which you did.
7. Empty states: 3 distinct messages exactly matching `Events.razor` lines 79-94 (My Events:
   ACARS-key-missing vs. no-registrations text; Past: "No past events found."; Upcoming: "No
   upcoming events at the moment. Check back soon!").
8. Event list (single vertical column, not a wrapping grid — matches the original's
   `flex-direction:column` layout): date block, title + Featured/Registered chips, description,
   "By {CreatedByName}" byline (only if non-empty), meta row (time UTC — only if non-empty,
   registration count with `/{max}` suffix only if `MaxParticipants > 0`, route — only if
   non-empty, rank/aircraft restriction chips — only if non-empty), and — hidden entirely on the
   Past tab — a trailing Register/Unregister/Full state: Registered → red "Unregister" button;
   not registered and full (`MaxParticipants > 0 && RegistrationCount >= MaxParticipants`) →
   "Full" chip (no button); otherwise → primary "Register" button. A per-event busy `HashSet<int>`
   (page-local field) disables the tapped button and shows "..." while its own register/
   unregister call is in flight, matching `_busyEvents` exactly — other events' buttons stay
   interactive during another event's in-flight call.
9. Register/Unregister handler (used by both the hero CTA and each list item): add the event id
   to the busy set, call `await _viewModel.RegisterAsync(id, currentlyRegistered)`, set the
   status message/error flag from the result, remove from the busy set, then
   `await _viewModel.LoadEventsAsync(_activeTab)` and rebuild — matches `HandleRegister`'s exact
   sequencing (register call → status message → reload, not register-and-reload-in-one-step).
   If no ACARS key is set, show the "Set your ACARS key in Settings before registering for
   events." message and return without calling the service, matching the original's guard.
10. Build — 0 errors. Check the XAML `--`-in-comment trap.
11. Commit: `feat(events): native page skeleton, tabs, featured hero, and event list`.

## Task 3 — Create Event dialog

Files: `AviatesAirTracker/Dialogs/CreateEventDialog.cs` (new),
`AviatesAirTracker/Pages/EventsPage.xaml.cs` (modified).

1. Follow `BookingDialog.cs`'s established dedicated-builder-class pattern: a static
   `ShowAsync(XamlRoot, EventsViewModel, SettingsService)` entry point, `ContentDialog` built
   entirely in C# (no XAML for this one, matching `BookingDialog`/`LandingResultDialog`).
2. Dark theme from the start: `RequestedTheme = ElementTheme.Dark`, `Background`/`Foreground`/
   `BorderBrush` set directly, plus the `Opened` + `VisualTreeHelper` `PrimaryButton`/
   `CloseButton` recoloring `BookingDialog.cs` already established — copy the technique (and its
   `FindDescendant<T>` helper, or factor it out if you'd rather share it, implementer's call).
3. Fields, in the original's order: Title (`TextBox`, maxlength 100), Description (multi-line
   `TextBox`, maxlength 500), Date (`CalendarDatePicker`) + Time UTC (`TextBox`, free text,
   placeholder "18:00", maxlength 10 — **not** a `TimePicker`, the original never parses this
   field beyond storing the raw string, so a native time-value control would be over-engineering
   a field that's just free text), Route (`TextBox`, placeholder "e.g. EGLL → KJFK"), Aircraft
   Restriction + Rank Restriction (two `TextBox`es side by side), Max Participants (`NumberBox`
   or a numeric `TextBox`, 0 = unlimited, matching the original's `min="0" max="9999"`). An
   inline amber warning banner shown when `SettingsService.Settings.AcarsKey` is empty, matching
   the original's exact copy: "No ACARS key found. Go to Settings and enter your ACARS key
   before creating events."
4. `PrimaryButtonClick` handler with the double-click-guard deferral pattern (`isCreating` bool +
   `args.GetDeferral()`, matching `BookingDialog`/Flights' delete dialog): validate ACARS key
   present, title non-blank, date non-blank — matching `CreateEvent()`'s exact 3 guard checks, no
   more, no less (do not add a max-participants range check or a time-format check, neither
   exists in the original). On failure, show an inline error and `args.Cancel = true` (keep the
   dialog open). On success: call `_viewModel.CreateEventAsync(req)`; if it succeeds, the page
   needs to know so it can switch to the Upcoming tab and reload (matching `CreateEvent()`'s own
   `_activeTab = "upcoming"; await LoadEvents();` on success) — either return the result from
   `ShowAsync` for the page to act on, or take a callback, implementer's call, but the page-side
   tab-switch-and-reload must actually happen, don't drop it.
5. Wire `EventsPage.xaml.cs`'s "Create Event" button (stubbed in Task 2) to call
   `CreateEventDialog.ShowAsync(...)`, then react to a successful creation per the point above.
6. Build — 0 errors. Compare warnings against baseline (15 pre-existing, none referencing
   Events/CreateEventDialog).
7. Commit: `feat(events): native create-event dialog`.

## Task 4 — Nav wiring and final verification

Files: `AviatesAirTracker/Navigation/NavDestinations.cs` (modified).

1. `"events"` entry gets `NativePageType: typeof(Pages.EventsPage)` — one line, matching every
   prior page's pattern. Read the file first to confirm the exact property/syntax used by
   sibling entries.
2. Build — 0 errors, compare warnings against baseline (15 pre-existing, none referencing
   Events/NavDestinations) and flag anything new.
3. Verification pass (read-only code review, no computer-use/live-app testing — that's reserved
   for the orchestrating session): re-read the full diff across all 3 prior tasks
   (`git diff <Task-1-base-commit>..HEAD`) against the design spec. Confirm: the 3 empty-state
   messages are exactly right; the busy-set behavior only disables the specific event's own
   button, not all of them; the Create Event dialog's validation is exactly the original's 3
   checks (no more, no less); the dialog is dark-themed from the start (not shipped with the
   stock-Windows-chrome bug Routes had to fix after the fact). Note explicitly whether this
   environment appears to have a working backend connection and a configured ACARS key (check
   `SettingsService`/`AppSettings` without attempting network calls yourself) — defer the
   register/create live-functional check explicitly if not available, rather than skipping it
   silently.
4. Commit: `feat(events): wire native EventsPage into navigation`.

## Verification (after all four tasks land)

- Build clean, no new warnings.
- Visual: compare native Events against the current Blazor page — all three tabs, featured hero,
  event cards (all chip/state combinations: featured, registered, full, past), create-event
  dialog (dark-themed, ACARS-key warning banner).
- Functional: tab switching reloads and clears the status message; register/unregister updates
  state, shows the status banner, and only busies its own event's button; create-event validation
  matches exactly; ACARS-key gating in all three spots (My Events empty state, register guard,
  create guard). Live-backend register/create testing is deferred if this environment has no
  working connection/ACARS key configured, per the design spec.
