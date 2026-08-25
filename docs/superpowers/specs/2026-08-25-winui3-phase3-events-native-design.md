# WinUI 3 Migration — Phase 3, seventh page: native Events

**Date:** 2026-08-25
**Status:** Draft, awaiting review
**Branch:** `claude/blazor-winui3-migration-a10c79`
**Builds on:** Dashboard, Pilot Hub, Flights, the landing-result dialog, Routes, and Fleet —
reuses established conventions throughout (icon-as-`Path` reproduction, `Border`-not-`Button`,
`ContentDialog` for modals, the `Loaded`-handler refresh pattern, the tab-bar `Border`-active-state
technique, the Routes-established responsive `ItemsWrapGrid` fill fix).

## Goal

Convert `Events.razor` (route `/events`) to a native WinUI 3 `Page`. A real, backend-driven
feature — three tabs (Upcoming / Past / My Events), a featured-event hero card, a register/
unregister flow, and a create-event form — not decorative content. Confirmed genuinely functional
by reading `EventsService.cs` and the full `@code` block directly, unlike `News.razor` (checked
during this same research pass: fully static placeholder content, no `@code` block, non-functional
tabs and "Read" links — explicitly **not** a candidate for conversion right now, nothing real to
port).

## Decision: a new `EventsViewModel`

Like Flights and Routes before their own conversions, `Events.razor` has no ViewModel — logic
lives directly in its `@code` block against `EventsService`/`SettingsService`. This spec builds
`EventsViewModel` for consistency with every other converted page (`FlightsViewModel`'s shape:
plain async methods, not `[RelayCommand]`-wrapped, since actions are triggered from code-behind
event handlers).

## What gets built

### `AviatesAirTracker/ViewModels/EventsViewModel.cs` (new)

- `ObservableCollection<AviatesEvent> Events`, `AviatesEvent? FeaturedEvent`, `bool IsLoading`.
- `LoadEventsAsync(string tab)` — wraps `EventsService.GetEventsAsync("past"|"upcoming")` /
  `GetMyEventsAsync(acarsKey)` (empty list if `tab == "my"` and no ACARS key is set, matching the
  original's exact branch), sets `FeaturedEvent` to `Events.FirstOrDefault(e => e.IsFeatured)`.
- `RegisterAsync(int eventId, bool currentlyRegistered)` — wraps `RegisterForEventAsync`/
  `UnregisterFromEventAsync`, returns `(bool Success, string Message)` for the page to show as a
  status banner; does **not** reload internally (the page calls `LoadEventsAsync` again itself
  after, matching `HandleRegister`'s own `await LoadEvents()` at the end).
- `CreateEventAsync(CreateEventRequest req)` — wraps `EventsService.CreateEventAsync`, validates
  nothing itself (validation — title required, date required, ACARS key present — stays in the
  page/dialog, matching every other page's split between ViewModel-as-thin-wrapper and
  page-owns-validation established by `RoutesViewModel`/`BookingDialog`).
- ACARS-key gating (`SettingsService.Settings.AcarsKey`) is read directly by the page for the
  three places it matters (My Events empty state copy, register button, create button) — same
  as the original reads `SettingsService` directly rather than the ViewModel hiding it.

### `AviatesAirTracker/Pages/EventsPage.xaml`/`.xaml.cs` (new)

- Page header, status banner (dismissible, success/error variants — `_statusMessage`/`_isError`
  in the original), tab bar (Upcoming / Past / My Events — same `Border`-active-state technique
  as Flights/Routes/Fleet) with a "Create Event" button alongside it.
- Loading state ("Loading events…").
- Featured-event hero card (Upcoming tab only, when a featured event exists): eyebrow label with
  month/year, title, description, a Register/Unregister CTA. The decorative concentric-circle SVG
  art (`Events.razor` lines 61-66) is nice-to-have, not required — a simplified or omitted version
  is acceptable, note which was done.
- Empty states: three distinct messages depending on tab (My Events: ACARS-key-missing vs.
  no-registrations copy; Past: "No past events found."; Upcoming: "No upcoming events at the
  moment. Check back soon!") — read `Events.razor` lines 79-94 directly for exact text.
- Event list: date block (day/month), title + Featured/Registered chips, description, creator
  byline, meta row (time UTC, registration count `"{count}{/max}" registered`, route,
  rank/aircraft restriction chips), and a trailing action — Register / Unregister / "Full" chip
  (when `MaxParticipants > 0 && RegistrationCount >= MaxParticipants && !IsRegistered`) — hidden
  entirely on the Past tab. A per-event busy flag (disables the button, shows "..." while a
  register/unregister call is in flight) matches the original's `_busyEvents` HashSet.
- Create Event dialog (`ContentDialog`, following the `BookingDialog`/Flights-delete-dialog
  pattern — dark-themed per the fix already established on `BookingDialog.cs`
  `RequestedTheme = ElementTheme.Dark` + native-button recoloring, so this doesn't ship with the
  same "looks like stock Windows" bug that had to be fixed on Routes after the fact): title,
  description, date (`CalendarDatePicker`), time UTC (free-text, matching the original's plain
  text input — not a `TimePicker`, since the original never validates or parses this field
  beyond storing the raw string), route, aircraft restriction, rank restriction, max participants
  (numeric, 0 = unlimited). An inline warning when no ACARS key is set (matching the original's
  amber banner). Validation: title required, date required, matching `CreateEvent()`'s exact two
  checks — no more, no less.

### `AviatesAirTracker/Navigation/NavDestinations.cs` (modified)

`"events"` entry gets `NativePageType: typeof(Pages.EventsPage)` — one line.

## What this spec explicitly does not do

- Does not touch `Events.razor` — left in place, unreferenced.
- Does not convert `News.razor` or `Support.razor` in this pass — News is fully static/decorative
  (confirmed, nothing functional to port); Support is small and mostly static too (one real FAQ
  accordion, three inert placeholder buttons) and can be picked up separately later if wanted.
- Does not add event editing/deletion — there isn't any in the original, create + register/
  unregister is the whole write surface.
- Does not touch any other nav destination.

## Verification

- Build clean, no new warnings.
- Visual: native Events compared against the current Blazor page — all three tabs, featured hero,
  event cards (all chip/state combinations), create-event dialog.
- Functional: tab switching reloads correctly, register/unregister updates state and shows the
  status banner, create-event validation matches exactly, ACARS-key gating in all three spots.
  Needs a working backend connection and a set ACARS key to fully exercise the register/create
  flows — if unavailable in this environment, defer that specific check explicitly rather than
  skip silently, same as every prior live-network-gated functional check in this migration.
