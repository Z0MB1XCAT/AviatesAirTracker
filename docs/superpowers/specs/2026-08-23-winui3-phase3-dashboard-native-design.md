# WinUI 3 Migration — Phase 3, first page: native Dashboard

**Date:** 2026-08-23
**Status:** Draft, awaiting review
**Branch:** `claude/blazor-winui3-migration-a10c79`
**Builds on:** Phases 0–2 (WinUI3 foundation + shell visual restoration), both complete.

## Goal

Convert `Dashboard.razor` (route `/`, the default landing page) to a native WinUI 3 `Page`,
bound to real data end to end. This is the **first** page of Phase 3 ("native pages" — every
other destination in `NavDestinations.All` still renders through the WebView2/Blazor bridge and
is untouched by this spec). Two things make Dashboard specifically worth going first: it's the
default route, and it's visually the simplest page in the app, so it's a reasonable place to pay
the one-time cost of teaching `ShellWindow` to host a native page at all, before the harder pages
(ACARS, the FMC, the map) that come later in Phase 3.

**Scope boundary:** this spec covers the Dashboard page and the shell-level plumbing every future
native page will reuse. It does not touch any other `.razor` file, does not migrate the map or
any JS-interop-heavy surface, and does not modify `LandingResultModal` or anything EFB-related.

## Decisions made

### 1. Real data, not a faithful placeholder port

Confirmed with the user: unlike a pure visual reskin, this conversion wires every section to real
data rather than reproducing today's hardcoded placeholder values. Investigation before writing
this spec found the page's actual state is more fragmented than it looks:

| Section | Today | This spec |
|---|---|---|
| Hero card | Static/decorative, no data | Ported as-is (static) |
| Stat strip (Flights / Air Hours / Avg Landing) | Real, via `PilotHubViewModel` | Real, via `DashboardViewModel` (see below) |
| Recent Flights | 3 hardcoded fake flights | Real, via `IFlightRepository.GetRecentAsync(5)` |
| "Your Assignments" table | 3 hardcoded fantasy NPCs | **Ported as-is** (revised — see below) |
| Pilot rank ring + greeting | Ring hardcoded to 32%; greeting name is real | Ring made real, local calc for now, real backend deferred — see §2 |
| Monthly "Flight Activity" chart | 7 hardcoded bar heights | **New aggregation** — see §2 |
| "Operations Team" list | Same 3 fantasy NPCs, reused | **Ported as-is** (revised — see below) |
| Quick Stats: Total Hours / Flights | Hardcoded, duplicates real stat-strip values | Real (same source as stat strip) |
| Quick Stats: "On-Time %" | Hardcoded `92%`, no backing concept | **Ported as-is** (revised — see below) |
| Quick Stats: "Landing Avg" | Hardcoded `82`, duplicates `AvgLandingScore` | Real, same value as the stat-strip card (intentional duplication — that's what today's design already does, just with a fake number) |

**Revised after the first review pass**: Assignments, Operations Team, and On-Time % are ported
faithfully as static placeholder content, exactly as they render today — not built as new empty
real-data features. Building real backing repositories for these with no way to populate them was
the "build all three as real features" choice initially made, but on reflection that trades a
mildly dishonest UI (fake people) for a visually sparse one (nothing at all) with no upside until
a real backend exists for them specifically — worse for now, not better. Revisit this once
there's an actual reason to (e.g. the Aviates Air backend gains a real staff-roster or
scheduled-time concept).

### 2. `DashboardViewModel` becomes the real source of truth

`DashboardViewModel` (DI-registered, wired into `MainViewModel.Dashboard`, but never actually
consumed by any UI today — a leftover from a dead pre-Blazor-bridge content-switching pattern)
is revived as the native Dashboard page's ViewModel, in preference to continuing to borrow
`PilotHubViewModel`. Concretely:

- `RefreshAsync()` switches `RecentFlights` from `_flightRepo.GetAllAsync().Take(5)` to
  `_flightRepo.GetRecentAsync(5)` — the repository already has this exact method
  (`IFlightRepository.GetRecentAsync(int count)`, `Core/Data/DataRepositories.cs`), no reason to
  duplicate its sort/filter logic inline.
- `PilotRank`, `RankProgress`, `NextRankText` — currently field-initializer defaults
  (`"First Officer"` / `0.15` / `"25hrs to Captain"`) that `RefreshAsync()` never touches — get
  computed for real, reusing `PilotStatsService` (see the bug fix below) instead of duplicating
  `PilotHubViewModel`'s copy of the same math a second time. **This is a local/offline
  calculation, not the real backend one** — see the callout below.
- New `MonthlyActivity` collection (7 months, current month last) backing the bar chart. This
  aggregation does not exist anywhere in the codebase today (confirmed: no `GroupBy` on date
  anywhere in `PilotStatsService`, any repository, or `PilotHubViewModel`) — add
  `PilotStatsService.GetMonthlyActivityAsync(int months = 7)` returning a small
  `MonthlyActivityPoint(string Label, int FlightCount)` list, grouping completed flights by
  `CreatedAt.Year`/`.Month`. Lives in `PilotStatsService` alongside `ComputeAsync()` since it's
  the same kind of flight-aggregation concern, not Dashboard-specific.
- `Assignments`/`OperationsTeam`/`OnTimeText` stay as the same static placeholder content
  Dashboard.razor already has today — plain fields on the ViewModel, not backed by any
  repository, per the revised decision above.

### Deferred: a real backend rank/level feature already exists, unused

`BACKEND-API/worker.js` already has a complete, deployed `computeRank(totalHours)` function
(same seven rank tiers as `PilotStatsService`) that also returns `rank_pct` — the exact
rank-progress percentage this spec is about to calculate locally — served from
`/api/portal/profile` and folded into PIREP/Discord-rank-sync flows. Client-side,
`AviatesBackendClient.FetchStatsAsync(pilotId, acarsKey)` already exists and already deserializes
the response into `RemotePilotStats.Rank`/`RemotePilotStats.RankProgress` — but **it is called
from nowhere in the app** (confirmed via grep: defined once, zero call sites). This is the same
"built but never wired up" situation `DashboardViewModel` itself was in before this spec.

**Explicitly deferred, per the user's direction**: this spec does not wire `FetchStatsAsync` into
either ViewModel. `DashboardViewModel`/`PilotHubViewModel` keep computing rank/progress locally via
`PilotStatsService` (bug fix below still applies — it's a real, independent improvement regardless
of which source eventually wins). The correct long-term direction is for
`AviatesBackendClient.FetchStatsAsync` to become the actual source of truth for rank/progress
everywhere it's shown (Dashboard, PilotHub, anywhere else), since the backend is the one place
Discord-rank-sync and any other server-side consumer already agree on — but that swap, plus
whatever offline/fallback behavior is needed when the backend is unreachable, is real follow-up
work, noted here rather than done now.

**Bug found and fixed as part of this work, not a new task**: `PilotHubViewModel.ComputeRankProgress`
switches on rank-name strings (`"Student Pilot"`, `"First Officer"`, `"Senior First Officer"`,
`"Captain"`) that don't match what `PilotStatsService.ComputeAsync()` actually produces
(`"Cadet"`, `"Junior First Officer"`, `"First Officer"`, `"Senior First Officer"`,
`"Junior Captain"`, `"Captain"`, `"Senior Captain"`). Four of the seven real rank values —
`Cadet`, `Junior First Officer`, `Junior Captain`, `Senior Captain` — fall through to the
switch's default arm and silently show "Top Rank" at 100% progress, including for a brand-new
pilot with zero hours. This spec fixes it by moving rank-progress computation into
`PilotStatsService` itself (next to the rank computation it must stay in sync with — same file,
so a future rank-threshold change can't silently desync the two again the way it just did), with
corrected case labels, and has both `PilotHubViewModel` and `DashboardViewModel` call the one
shared method. This is a pre-existing bug unrelated to Phase 3, but fixing it in place while
extracting the logic anyway is cheaper than fixing it separately later, and leaving a known-wrong
calculation in the code this spec is actively touching would be an odd thing to do deliberately.

## Shell plumbing: native page hosting

`ShellWindow.xaml` today is one `Grid` cell containing only `UiHost` (the `WebView2`). Nothing
hosts a native `Page`. This spec adds that capability, since every future Phase 3 page needs it
too — a one-time cost paid here, not Dashboard-specific:

- Add a `Frame x:Name="NativeHost"` to the same `Grid.Row` as `UiHost`, both `Grid.Row="1"`
  (stacked, `Visibility` toggled between them — never both visible at once).
- `NavDestination` (`Navigation/NavDestinations.cs`) gets a new property, e.g.
  `Type? NativePageType` (null = still WebView2-hosted, matching every destination except
  Dashboard for now).
- `ShellWindow.Navigate(NavDestination destination)` branches: if `NativePageType != null`,
  `NativeHost.Navigate(destination.NativePageType)`, `NativeHost.Visibility = Visible`,
  `UiHost.Visibility = Collapsed`; otherwise the existing `UiHost.CoreWebView2.Navigate(...)`
  path, with `NativeHost.Visibility = Collapsed`, `UiHost.Visibility = Visible`.
- `OnFirstActivated`'s initial navigation (`Navigate(_pendingDestination)`) goes through the same
  branch, so landing on Dashboard at startup (today's default) shows the native page immediately
  without a WebView2 flash first.
- The login gate (`IsLoggedIn()` check, `Nav.IsPaneVisible`) is untouched — login always goes
  through the WebView2 path regardless, since `/login` has no `NativePageType`.

## What gets built

### `AviatesAirTracker/Pages/DashboardPage.xaml` (new)

Native WinUI reproduction of the current layout: hero card (static content + a redrawn, static
version of the SVG art — no CSS `@keyframes` equivalent attempted in this first pass, see "Out of
scope"), 3 stat cards, Recent Flights (`ItemsControl` bound to `RecentFlights`, empty state if
none), Your Assignments (`ItemsControl` bound to `Assignments`, empty state), right panel:
pilot-stats card (custom rank-progress ring — see below — greeting text, monthly-activity bar row
bound to `MonthlyActivity`), Operations Team card (`ItemsControl` bound to `OperationsTeam`, empty
state), Quick Stats card (4 tiles — Total Hours/Flights real, On-Time % the same static text
Dashboard.razor has today).

**Rank-progress ring**: no native WinUI control matches `.pilot-ring`'s thin accent-colored arc
with a centered percentage label. Built as a small reusable `Path` with an `ArcSegment` computed
from `RankProgress` (0–1), following the exact same geometry the CSS version uses
(`circumference = 2π × 45`, per the existing `.pilot-ring` CSS) so the two stay visually
consistent if the Blazor version is ever compared side by side.

### `AviatesAirTracker/ViewModels/DashboardViewModel.cs` (modified)

Per §2 above: real `RecentFlights` via `GetRecentAsync`, real rank progress via the fixed shared
`PilotStatsService` method (local calculation — the real backend one is deferred, see §2's
callout), new `MonthlyActivity` property populated in `RefreshAsync()`. `Assignments`,
`OperationsTeam`, `OnTimeText` stay as static content, not new observable data.

### `AviatesAirTracker/Services/SupportServices.cs` (modified, `PilotStatsService`)

`ComputeRankProgress` moved here from `PilotHubViewModel`, with corrected rank-name case labels;
`GetMonthlyActivityAsync` added.

### `AviatesAirTracker/ViewModels/PilotHubViewModel.cs` (modified)

Switches to calling the now-shared, now-correct `PilotStatsService.ComputeRankProgress` instead
of its own private copy. This is the bug fix landing for the page that's had it wrong all along,
not just for the new Dashboard consumer.

### `ShellWindow.xaml` / `ShellWindow.xaml.cs` / `Navigation/NavDestinations.cs` (modified)

Native-page hosting plumbing per the "Shell plumbing" section above. Only Dashboard's
`NavDestination` gets a `NativePageType` set in this spec; every other destination stays
WebView2-routed.

## What this spec explicitly does not do

- Does not touch `Dashboard.razor` itself or remove it — kept as-is, unreferenced by the native
  shell once `NavDestinations` points Dashboard at the native page, but left in place rather than
  deleted (this project's established pattern throughout the WinUI3 migration: old code stays
  until the whole migration is done, not deleted phase-by-phase).
- Does not migrate any other page. `Flights`, `Routes`, `Fleet`, `PilotHub`, `ACARS`, `Map`, etc.
  all keep routing through the WebView2 exactly as today.
- Does not reproduce the hero card's CSS animations (radar pulse, route-draw, breathing dot) —
  static art only in this first pass. A `Storyboard`/Composition version is legitimate future
  polish, not blocking.
- Does not build an actual "See All Team" destination page, or make the Follow button on
  Operations Team members functional — both stay inert placeholders, same as today, since neither
  has anywhere real to navigate to yet.
- Does not wire `AviatesBackendClient.FetchStatsAsync` into any ViewModel, despite the backend
  already having a working `rank`/`rank_pct` feature behind it — explicitly deferred per §2's
  callout, noted as real follow-up work rather than done here.
- Does not build any new repository/model for Assignments, Operations Team, or On-Time tracking
  — all three stay exactly as static as they are in `Dashboard.razor` today.
- Does not change `PilotHub.razor` itself (still Blazor-rendered) — only its ViewModel's
  rank-progress bug gets fixed, since that ViewModel is shared with the new native consumer.

## Verification

- `dotnet test` — new tests for `PilotStatsService.ComputeRankProgress` (covering all 7 real rank
  values, including the 4 that were previously broken) and `GetMonthlyActivityAsync`, following
  this project's established TDD pattern (pure logic tested before UI wiring).
- Build clean, no new warnings.
- Visual: native Dashboard compared against a screenshot of the current Blazor Dashboard for
  layout/color fidelity across every section, including Assignments/Operations Team/On-Time %
  since they're meant to look identical to today, not just "close."
- Functional: confirm clicking "Dashboard" in the nav rail shows the native page (no WebView2
  flash), and confirm every other nav item still shows the Blazor-rendered page exactly as before
  — this is the regression risk of the shell plumbing change, worth checking explicitly since it
  touches `Navigate()`, which every single destination goes through.

## Follow-up work noted, not scoped into this spec

- Wire `AviatesBackendClient.FetchStatsAsync` as the real source of truth for pilot rank/progress
  (Dashboard, PilotHub, anywhere else it's shown), replacing the local `PilotStatsService`
  calculation this spec adds as an interim. Needs an offline/unreachable-backend fallback design.
- A real Assignments/Operations Team backend concept, if the Aviates Air backend ever grows one —
  today `BACKEND-API/worker.js` has nothing resembling either.
- On-Time % tracking, if it's ever wanted for real — would need a scheduled-vs-actual time
  comparison, which doesn't exist client- or server-side today.
