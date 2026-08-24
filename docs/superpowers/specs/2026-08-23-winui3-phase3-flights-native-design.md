# WinUI 3 Migration — Phase 3, third page: native Flights

**Date:** 2026-08-23
**Status:** Draft, awaiting review
**Branch:** `claude/blazor-winui3-migration-a10c79`
**Builds on:** Dashboard and Pilot Hub, both complete — reuses their established conventions
throughout (icon-as-`Path`/`Polyline` reproduction, `Border`-not-`Button`, a `Loaded`-handler
`RefreshAsync` pattern, plain mutable precomputed-display-row classes for anything bound to an
`ItemsControl`).

## Goal

Convert `Flights.razor` (route `/flights`) to a native WinUI 3 `Page`. Chosen as the third page
because it's the natural next step in the pilot-career narrative (Dashboard → Pilot Hub →
Flights) and because Dashboard's "View Flights" CTA has been sitting inert since it shipped —
converting this page finally gives it somewhere real to navigate to.

## What's different about this page, versus Dashboard/PilotHub

Both prior pages were read-mostly: a handful of inert placeholder buttons, but the vast majority
of real interaction was "look at data." Flights is not that. Real, working, currently-wired
features found during research:

- **Tabs** (All / Active / Completed / Pending Review, with a live badge count on Pending)
- **Delete-with-reason flow**: a row's trash icon opens a modal requiring a typed reason, submits
  via `IFlightDeletionRepository.SaveAsync`
- **Approve/Reject** a pending deletion request (Pending tab) — approve cascades to
  `IFlightRepository.DeleteAsync`
- **Force-remove a broken in-progress flight**, including resetting `FlightSessionManager` if it's
  the currently-active flight

None of this is deferred the way Pilot Hub's Bookings tab was — Bookings was a genuinely separate
feature bolted onto a stats page; these four things ARE the Flights page, not an adjacent
concern. This spec builds all of them.

**Decorative and staying that way** (confirmed inert in the current Blazor page, no behavior to
port): the "Filter" and "Export" buttons in the table header, and the per-row "View details"
chevron. Same treatment as every inert placeholder found on Dashboard/PilotHub — kept visually
present, no click handler.

**No missing-CSS-class bug found this time**, worth stating plainly rather than implying one
exists — every class/custom-property this page's markup references was checked against
`app.css` and all exist. Unlike Dashboard's rank-progress bug or Pilot Hub's grade-C/grade-F
color bugs, this page has nothing broken to fix in passing.

## Decision: a new `FlightsViewModel`

Unlike Dashboard/PilotHub, **no `FlightsViewModel` exists** — `Flights.razor` is the older
pattern, with all logic (repository calls, tab filtering, delete/approve/reject operations)
living directly in the page's `@code` block. This spec builds a proper `FlightsViewModel`
(`ObservableObject`, `[ObservableProperty]`/`[RelayCommand]`, `CommunityToolkit.Mvvm`) following
the exact shape `PilotHubViewModel` already establishes, rather than porting the `@code` block's
logic directly into the native page's code-behind. Reasons:
- Consistency: every other converted page now has a ViewModel; a fourth different pattern
  (page-code-behind-owns-everything) would be a real inconsistency for no benefit.
- The delete/approve/reject operations mutate real state and need to be callable/testable
  independent of any specific UI — natural `[RelayCommand]` methods.
- `IFlightRepository`/`IFlightDeletionRepository`/`FlightSessionManager` are already DI-registered
  singletons; injecting them into a new ViewModel instead of a `Page` costs nothing extra.

`Flights.razor` itself is untouched (same "leave the old page in place, unreferenced" pattern as
every prior conversion) — this new ViewModel has no effect on it, since it still does its own raw
repository calls independently.

## What gets built

### `AviatesAirTracker/ViewModels/FlightsViewModel.cs` (new)

- `ObservableCollection<FlightRecord> Flights` (all flights, loaded via `GetAllAsync()`) and a
  computed/filtered view per active tab (`All`/`Active`/`Completed`/`PendingReview`) — port the
  exact filter predicates `Flights.razor`'s `FilteredFlights()` already uses, don't reinvent them.
- `ObservableCollection<FlightDeletionRequest>` for the Pending Review tab.
- Stat-strip values (Total Flights, Completed, Total Distance) — computed client-side from the
  loaded list, same as the current page (no new aggregate query needed).
- `[RelayCommand]` methods: `SubmitDeletionRequestAsync(FlightRecord, string reason)`,
  `ApproveDeletionAsync(FlightDeletionRequest)` (cascades to `FlightRepo.DeleteAsync`),
  `RejectDeletionAsync(FlightDeletionRequest)`, `ForceRemoveBrokenFlightAsync(FlightRecord)`
  (resets `FlightSessionManager` first if it's the current flight, matching `Flights.razor`'s
  exact current logic — read it directly rather than reconstructing from this summary).
- Subscribes to `FlightSessionManager.FlightCompleted` for live refresh, same pattern
  `PilotHubViewModel` already uses, disposed cleanly (check how `Flights.razor` currently
  disposes this subscription and replicate the same lifetime discipline).
- Registered as `AddSingleton<FlightsViewModel>()` in `App.xaml.cs`, matching every other
  ViewModel's DI lifetime.

### `AviatesAirTracker/Pages/FlightsPage.xaml`/`.xaml.cs` (new)

- Page header, tab bar (4 tabs, active-state styling matching the sidebar's own established
  active-item treatment — a `Border`-based selected background, not a `<Style>`-driven
  `TabView`), stat strip (3 cards, reuse Dashboard's icon-reproduction technique for whichever
  icons `Flights.razor` actually uses — check the file, don't assume they match Dashboard's).
- Flight table: `ItemsControl` with a precomputed display-row class (route flags, aircraft,
  duration, landing-grade chip, status chip, action icons) — same
  `FlightDisplayRow`/`LandingDisplayRow`-style pattern as the prior two pages.
- **Route flag icons**: `IcaoCountry.IsoFromIcao(icao)` (already exists,
  `Core/Reference/IcaoCountry.cs`) resolves an ICAO code to its country's ISO code;
  `wwwroot/images/flags/{iso}.svg` already ships these as SVGs. WinUI has a dedicated
  `Microsoft.UI.Xaml.Media.Imaging.SvgImageSource` class for exactly this — load it the same way
  Dashboard's `hero.webp` was loaded (a `System.IO.Path.Combine(AppContext.BaseDirectory, ...)`
  file path, since `wwwroot` ships as loose content, not a packaged asset), assigned to a plain
  `Image.Source`, not an `ImageBrush` (no cropping/alignment need here, unlike the hero photo).
  Handle a missing/unresolvable ISO gracefully (some ICAO codes won't resolve — check what
  `IsoFromIcao` returns for those, likely `null`, and skip rendering that flag rather than
  crashing on a bad file path).
- Delete-with-reason modal, approve/reject actions, force-remove confirmation: use WinUI's native
  `ContentDialog` — the idiomatic modal mechanism, not a custom overlay `Border`. This is a new UI
  pattern for this migration (nothing prior has needed a modal), worth its own implementation
  task and its own careful verification, since `ContentDialog` has its own default chrome this
  project hasn't exercised yet.
- Empty state when the filtered list is empty (icon + message, exact current copy).
- `Loaded` handler resolving `FlightsViewModel` via `App.GetService<FlightsViewModel>()`, calling
  a refresh method, same shape as the prior two pages.

### `AviatesAirTracker/Navigation/NavDestinations.cs` (modified)

`"flights"` entry gets `NativePageType: typeof(Pages.FlightsPage)` — one line, same pattern as
`dashboard`/`pilot-hub`.

### `AviatesAirTracker/Pages/DashboardPage.xaml.cs` (modified)

Dashboard's "View Flights" `Border` (currently inert, explicit code comment stating so) gets a
real `Tapped` handler navigating to the Flights page. This is the one piece of this spec that
touches an already-shipped page — small, additive, and directly motivated by this page existing
now. Check how navigation between native pages should actually work (does `DashboardPage` have
access to the `Frame`/`ShellWindow` it's hosted in? `Frame.Navigate` from within a hosted `Page`
uses `this.Frame`, which should already be set by the hosting `NativeHost` `Frame` — verify this
works as expected rather than assuming, since no native page has navigated to another native page
before now).

## What this spec explicitly does not do

- Does not touch `Flights.razor` — left in place, unreferenced.
- Does not wire the Filter/Export buttons or the "View details" chevron — all three stay inert,
  matching current behavior exactly.
- Does not build a flight-detail view — "View details" has nowhere to go yet, same as it does
  today.
- Does not touch any other nav destination.

## Verification

- Build clean, no new warnings.
- Visual: native Flights compared against the current Blazor page — tabs, stat strip, table
  (including flag icons — confirm they actually resolve/render, not just compile), chip colors.
- Functional: tab switching actually filters; submitting a deletion request with an empty reason
  is rejected (matching the current page's validation); approving a pending deletion actually
  removes the flight; force-removing a broken flight actually clears `FlightSessionManager`'s
  current-flight state if applicable. Dashboard's "View Flights" button now actually navigates.
- Regression: every other nav item, native and WebView2-hosted alike, still works.
