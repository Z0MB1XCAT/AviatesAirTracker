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
| "Your Assignments" table | 3 hardcoded fantasy NPCs | **New feature** — see §3 |
| Pilot rank ring + greeting | Ring hardcoded to 32%; greeting name is real | Ring made real (see §2); greeting already real |
| Monthly "Flight Activity" chart | 7 hardcoded bar heights | **New aggregation** — see §2 |
| "Operations Team" list | Same 3 fantasy NPCs, reused | **New feature** — see §3 |
| Quick Stats: Total Hours / Flights | Hardcoded, duplicates real stat-strip values | Real (same source as stat strip) |
| Quick Stats: "On-Time %" | Hardcoded `92%`, no backing concept | **New feature** — see §3 |
| Quick Stats: "Landing Avg" | Hardcoded `82`, duplicates `AvgLandingScore` | Real, same value as the stat-strip card (intentional duplication — that's what today's design already does, just with a fake number) |

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
  `PilotHubViewModel`'s copy of the same math a second time.
- New `MonthlyActivity` collection (7 months, current month last) backing the bar chart. This
  aggregation does not exist anywhere in the codebase today (confirmed: no `GroupBy` on date
  anywhere in `PilotStatsService`, any repository, or `PilotHubViewModel`) — add
  `PilotStatsService.GetMonthlyActivityAsync(int months = 7)` returning a small
  `MonthlyActivityPoint(string Label, int FlightCount)` list, grouping completed flights by
  `CreatedAt.Year`/`.Month`. Lives in `PilotStatsService` alongside `ComputeAsync()` since it's
  the same kind of flight-aggregation concern, not Dashboard-specific.
- New `OnTimePercentage` (nullable — see §3) and `Assignments`/`OperationsTeam` collections.

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

### 3. Three real features, not fantasy data and not silently deferred

The user's explicit choice: build all three as real features rather than leaving them static or
repurposing Operations Team onto the existing friends system. Since none of the three has a live
Aviates Air backend endpoint to source data from yet, "real feature" here means: the same pattern
`Core/Data/DataRepositories.cs` already uses everywhere else in this app — a real domain model, a
real repository interface, and an in-memory implementation that's honestly empty until a backend
exists to populate it (exactly how `IApiService`/`NullApiService` already works for the backend
integration generally, per `CLAUDE.md`). This is not the same thing as today's fantasy NPCs: today
the UI lies about there being 3 real dispatchers; after this change the UI is honest that there
are currently zero, with a proper empty state, ready to display real ones the moment a backend
provides them.

**Flagging plainly, since it changes what the page looks like**: with no backend, Assignments and
Operations Team will render as genuine empty states (not populated with placeholder people) once
this ships — the dashboard will look sparser than today's screenshot in those two panels. This is
the honest consequence of "build it as a real feature" when there is nothing behind it yet, not a
missed requirement. If that's not the intended tradeoff, this is the moment to say so before
implementation starts.

- **`IAssignmentRepository`** (new) — `Assignment { Guid Id, string DispatcherName, string Role,
  string Description, DateTime AssignedAt, bool IsComplete }`. `InMemoryAssignmentRepository`
  starts empty. Dashboard shows up to 5 open assignments, or an empty-state message ("No open
  assignments") if none. DI-registered singleton, same lifetime pattern as every other
  `InMemory*Repository`.
- **`IOperationsTeamRepository`** (new) — `OperationsTeamMember { Guid Id, string Name, string
  Role, bool IsOnline }`. `InMemoryOperationsTeamRepository` starts empty. Dashboard shows up to
  3 members with a "See All Team" placeholder link (disabled — no destination page exists yet),
  or an empty state.
- **On-Time %** — add `FlightRecord.ScheduledDepartureUtc` (nullable `DateTime?`, new field,
  additive/backward-compatible with existing saved JSON flight records). Populated at flight-save
  time from `SimBriefService.CurrentPlan?.ScheduledDepartureUtc` when a SimBrief plan was loaded
  for that flight (mirrors how other SimBrief-derived fields already get stamped onto
  `FlightRecord` at completion — check `FlightSessionManager`'s existing save path for the exact
  spot). `PilotStatsService.ComputeAsync()` adds `OnTimePercentage` (nullable `double?`): among
  completed flights that have a recorded `ScheduledDepartureUtc`, the percentage where
  `BlockOutTime` is within 15 minutes of it (a standard VA on-time-performance tolerance). If zero
  flights have a recorded scheduled time yet (true for every flight logged before this ships,
  and for any flight without a SimBrief plan), `OnTimePercentage` is `null` and the Dashboard
  shows "—" rather than a fabricated number or a misleading 0%.

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
state), Quick Stats card (4 tiles, `OnTimePercentage` rendering "—" when null).

**Rank-progress ring**: no native WinUI control matches `.pilot-ring`'s thin accent-colored arc
with a centered percentage label. Built as a small reusable `Path` with an `ArcSegment` computed
from `RankProgress` (0–1), following the exact same geometry the CSS version uses
(`circumference = 2π × 45`, per the existing `.pilot-ring` CSS) so the two stay visually
consistent if the Blazor version is ever compared side by side.

### `AviatesAirTracker/ViewModels/DashboardViewModel.cs` (modified)

Per §2/§3 above: real `RecentFlights` via `GetRecentAsync`, real rank progress via the fixed
shared `PilotStatsService` method, new `MonthlyActivity`, `Assignments`, `OperationsTeam`,
`OnTimePercentage` properties, all populated in `RefreshAsync()`.

### `AviatesAirTracker/Services/SupportServices.cs` (modified, `PilotStatsService`)

`ComputeRankProgress` moved here from `PilotHubViewModel`, with corrected rank-name case labels;
`GetMonthlyActivityAsync`; `OnTimePercentage` added to `PilotStatistics`.

### `AviatesAirTracker/ViewModels/PilotHubViewModel.cs` (modified)

Switches to calling the now-shared, now-correct `PilotStatsService.ComputeRankProgress` instead
of its own private copy. This is the bug fix landing for the page that's had it wrong all along,
not just for the new Dashboard consumer.

### `AviatesAirTracker/Core/Data/DataRepositories.cs` (modified)

New `IAssignmentRepository`/`InMemoryAssignmentRepository`,
`IOperationsTeamRepository`/`InMemoryOperationsTeamRepository`, following the exact shape of the
existing repositories in this file. Registered as singletons in `App.xaml.cs`.

### `AviatesAirTracker/Models/FlightModels.cs` (modified)

`FlightRecord.ScheduledDepartureUtc` (nullable, additive). New `Assignment`,
`OperationsTeamMember` model classes.

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
- Does not implement a real Aviates Air backend endpoint for assignments or operations-team data
  — the repositories are real and ready, but genuinely empty, exactly as `NullApiService` is
  today for the backend generally.
- Does not change `PilotHub.razor` itself (still Blazor-rendered) — only its ViewModel's
  rank-progress bug gets fixed, since that ViewModel is shared with the new native consumer.

## Verification

- `dotnet test` — new tests for `PilotStatsService.ComputeRankProgress` (covering all 7 real rank
  values, including the 4 that were previously broken) and `GetMonthlyActivityAsync`, following
  this project's established TDD pattern (pure logic tested before UI wiring).
- Build clean, no new warnings.
- Visual: native Dashboard compared against a screenshot of the current Blazor Dashboard for
  layout/color fidelity on the sections that keep the same data (hero, stat strip). Assignments
  and Operations Team panels checked for a correct, non-broken empty state rather than compared
  against the old fantasy content, since that content is intentionally gone.
- Functional: confirm clicking "Dashboard" in the nav rail shows the native page (no WebView2
  flash), and confirm every other nav item still shows the Blazor-rendered page exactly as before
  — this is the regression risk of the shell plumbing change, worth checking explicitly since it
  touches `Navigate()`, which every single destination goes through.
