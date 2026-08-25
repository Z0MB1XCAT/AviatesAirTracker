# WinUI 3 Migration — Phase 3, fifth page: native Routes

**Date:** 2026-08-24
**Status:** Draft, awaiting review
**Branch:** `claude/blazor-winui3-migration-a10c79`
**Builds on:** Dashboard, Pilot Hub, Flights, and the landing-result dialog — reuses established
conventions throughout (icon-as-`Path` reproduction, `Border`-not-`Button`, the `Loaded`-handler
`RefreshAsync` pattern, `SvgImageSource` flag icons, `ContentDialog` for modals per the landing
dialog and Flights' delete/force-remove precedent).

## Goal

Convert `Routes.razor` (route `/routes`) to a native WinUI 3 `Page`. A flat, non-paginated route
catalog (mainline/cargo/regional schedule, live from the backend) with a booking flow — no
per-route detail view, the interaction model is card → booking modal directly.

## What's different about this page

Unlike every prior page, **nothing found here is decorative**. Tabs, search, the position
banner's "Reposition" action, and the full booking flow (date/time selection, registration
picker, callsign generation, validation, submission) are all real and already wired to live
services. There is also **no CSS-reference bug** to fix this time (every class Routes.razor uses
was checked against `app.css` and all exist) — worth stating plainly rather than implying one
exists. The complexity here isn't "port working logic vs. inert placeholders," it's "port a
genuinely complete feature faithfully."

**Not map-dependent** — confirmed no Leaflet/JS interop anywhere on this page, safe for this
migration phase (Map conversion is explicitly a separate, later phase).

## Decision: a new `RoutesViewModel`

Like Flights before its own conversion, `Routes.razor` has no ViewModel — logic lives in its
`@code` block, driving `RoutesService`/`BookingService`/`FleetService`/`SettingsService` directly.
This spec builds `RoutesViewModel` for consistency with every other converted page, following
`FlightsViewModel`'s shape (plain async methods rather than `[RelayCommand]`-wrapped, since this
page's actions are triggered from code-behind event handlers, not XAML `Command` bindings).

**No client-side filter logic to extract as pure/testable** — unlike Flights' tab filter (which
filtered an already-in-memory list, a natural TDD candidate), Routes' fleet/search filtering
happens **server-side** via `RoutesService.GetRoutesAsync(fleet, q, ...)`'s own parameters. There
is nothing here shaped like a pure function worth a dedicated TDD task; every prior page's TDD
step existed because there was genuine client-side pure logic to extract, not as a ritual to
repeat regardless.

## What gets built

### `AviatesAirTracker/ViewModels/RoutesViewModel.cs` (new)

- `ObservableCollection<AviatesRoute> Routes`, `int TotalCount`, `bool IsLoading`.
- Position-instrument state: `string? CurrentAirportIata` (from `SettingsService.Settings`),
  refreshed the same way the original does — `BookingService.SyncPositionAsync()` on load.
- `LoadRoutesAsync(string fleet, string? query)` — thin wrapper around
  `RoutesService.GetRoutesAsync`, matching the original's exact parameter usage (`limit` stays at
  its existing default, no pagination UI to build).
- `ClearPositionAsync()` — wraps `BookingService.SetPositionAsync("")`.
- Booking methods, reproducing `Routes.razor`'s exact validation and flow (read the `.razor`
  file's actual booking-modal `@code` directly during implementation, this spec doesn't
  transcribe it verbatim the way the Flights/landing-dialog plans did given its length):
  `LoadRegistrationsAsync(string aircraftType)` (wraps `FleetService.GetRegistrationsAsync`),
  `GenerateCallsign(...)` (wraps `BookingService.GenerateCallsign`), `CreateBookingAsync(...)`
  (validates future date within 90 days and the 5-active-booking cap client-side before calling
  `BookingService.CreateBookingAsync`, matching the original's exact guard conditions — don't
  loosen or tighten them).

### `AviatesAirTracker/Pages/RoutesPage.xaml`/`.xaml.cs` (new)

- Page header, position-instrument banner (two states: locked — IATA, route count, pulsing green
  status dot, Reposition button; free — dashed "no base set" banner), tab bar (All/Mainline/
  Cargo/Regional, same `Border`-active-state technique Flights already established), a search
  toggle revealing a `TextBox` with the original's 400ms debounce (a `DispatcherTimer` reset on
  each keystroke, simplest native equivalent of the original's `Task.Delay` + `CancellationTokenSource`
  pattern — verify during implementation which reads cleaner in WinUI).
- Loading spinner (a WinUI `ProgressRing` is fine here — this is a real, if minimal, spinner
  need, not a shape-sensitive pill/button where `<Style>` limitations would matter) and the two
  distinct empty states (no routes from locked position vs. no routes found/matching).
- Result count strip ("Showing N of Total").
- Route card grid: `ItemsControl` with a 3-column wrap layout (`ItemsWrapGrid` or a `UniformGrid`,
  implementer's call), each card showing the dual-flag route chip (reuse the exact
  `IcaoCountry.IsoFromIcao` + `SvgImageSource` technique Flights already established for its own
  route flags — literally the same two files, `wwwroot/images/flags/*.svg`), IATA pair, names,
  aircraft/frequency/flight-number chips, distance/block-time, and a "Book" `Border`-button.

### Booking dialog (new, likely its own file: `AviatesAirTracker/Dialogs/BookingDialog.cs`)

Following the `LandingResultDialog` precedent of a dedicated builder class rather than inlining a
~150+-line dialog into the page's code-behind (Flights' two smaller dialogs were small enough to
inline; this one — date/time pickers, an async-loaded registration selector, a regenerable
callsign preview, inline validation, a success toast — is not). Native controls to reach for:
`CalendarDatePicker`/`DatePicker` for the date, `TimePicker` for the time, `ComboBox` for the
registration list (populated after the aircraft type is known — check whether the original loads
registrations eagerly or on-demand and match that), a `Border`+`TextBlock` callsign preview with
a "Regenerate" `Border`-button, inline `TextBlock` error messaging matching the original's
validation copy, and a `ContentDialog` `PrimaryButtonClick` handler (with a deferral, same pattern
Flights' delete dialog already established) wired to `CreateBookingAsync`. The success toast is a
separate, small transient UI element (the original auto-dismisses after 5s) — a lightweight
`TeachingTip` or a manually-timed `Border` overlay, implementer's call on whichever is simpler to
get right without a `<Style>`.

### `AviatesAirTracker/Navigation/NavDestinations.cs` (modified)

`"routes"` entry gets `NativePageType: typeof(Pages.RoutesPage)` — one line, same pattern as
every prior page.

## What this spec explicitly does not do

- Does not touch `Routes.razor` — left in place, unreferenced.
- Does not build a route-detail page — there isn't one in the original either, card → modal is
  the whole interaction model.
- Does not touch `PilotHub.razor`'s "Browse Routes" link — that's the *original* Blazor page's
  own inbound link; once this spec ships, `PilotHubPage`'s equivalent (if it has one — check
  during implementation) would be worth wiring to navigate here, following the same
  Dashboard-CTA-to-Flights precedent, but only if such a link actually exists on the native
  Pilot Hub page already.
- Does not touch any other nav destination.

## Verification

- Build clean, no new warnings.
- Visual: native Routes compared against the current Blazor page — position banner both states,
  tabs, search, card grid (flags rendering), booking modal's full field set.
- Functional: booking validation (past date rejected, >90-day-out date rejected, 5-active-booking
  cap enforced) matches the original's exact guards; a successful booking actually advances the
  locked position and shows the success toast; "Reposition" actually clears it. These need a
  valid ACARS key to test against the live backend — if one isn't configured/available in this
  environment, defer explicitly rather than skip silently, same as every prior live-network-gated
  functional check in this migration.
