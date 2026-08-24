# WinUI 3 Migration — Phase 3, second page: native Pilot Hub

**Date:** 2026-08-23
**Status:** Draft, awaiting review
**Branch:** `claude/blazor-winui3-migration-a10c79`
**Builds on:** Phase 3's Dashboard page (complete) — reuses its established patterns directly:
the `Loaded`-handler `RefreshAsync()` idiom, `Border`-not-`Button` for anything whose shape
matters, `Path`/`Polyline` reproductions of `Dashboard.razor`'s exact SVG icon paths, and a
custom-drawn `Path`/`ArcSegment` ring for the one shape WinUI has no control for.

## Goal

Convert `PilotHub.razor` (route `/pilot-hub`) to a native WinUI 3 `Page`. Chosen as the second
page specifically because `PilotHubViewModel` is already correct and live — unlike Dashboard's
ViewModel, which had to be revived from dead code, this one just needs a native view bound to it.

## Scope: Career tab only, Bookings deferred

`PilotHub.razor` turns out to have two tabs, not one — a **Career** tab (stats, rank, last
landing, landing history — what this spec was originally scoped around) and a **My Bookings**
tab, discovered during research, that is fully functional: async booking load, a working "Start
Flight" button with real side effects (`BookingService.SetActiveBooking`,
`SimBriefService.OpenDispatch`, navigation to `/acars`), a working "Cancel" button with a confirm
modal and error/spinner states.

**This spec covers Career only.** Bookings is a materially different kind of work — the first
genuinely *stateful* interactive flow this migration has needed (every native page and every
inert placeholder so far has been read-only or a no-op button) — and folding it into "convert the
stats page" would be exactly the kind of scope creep this project has consistently avoided (see
Dashboard's spec explicitly declining to build real backend-dependent features in the same task
as the visual port). Bookings gets its own follow-up task once there's a reason to prioritize it.

**Consequence, stated plainly**: the native Pilot Hub page ships with no tab bar at all — a
single Career view, since building a tab bar with one working tab and one that goes nowhere would
be worse than not building it yet. The tab bar returns when Bookings is converted.

## Two pre-existing bugs found, fixed in passing (not new tasks)

Both are real defects in the *current* Blazor page — this spec fixes them rather than faithfully
porting broken behavior, following the same precedent as Dashboard's rank-progress bug fix.

1. **A grade-F landing's score ring renders invisible.** `app.css` defines `.score-ring-fill`
   colors for `.grade-a`/`.grade-b`/`.grade-c`/`.grade-d` but has no `.grade-f` rule at all, so an
   F-grade ring's `stroke` is unset (SVG default: no stroke) — only the gray background track
   shows, with no indication a landing happened. Fixed: the native ring maps grade F to `--red`
   (`#EF4444`), the same color already correctly used for the lowest tier elsewhere (e.g. the
   recent-landings table's `ScoreColor`/`GradeChipClass` helpers already fall back to red for the
   bottom band).
2. **A grade-C landing's score text and chip use an undefined color.** `PilotHubViewModel`'s
   `ScoreColor()` and `GradeChipClass()` helpers emit `var(--yellow)`/`chip-yellow` for the 60–74
   score band, but `app.css` defines neither `--yellow` nor `.chip-yellow` (only `--amber`/
   `.chip-amber` exist) — the browser silently ignores the invalid value and falls back to
   inherited text color, so a grade-C score/chip renders low-contrast and inconsistent with the
   ring, which *does* correctly use `--amber` for the same grade. Fixed: the native page maps
   grade C to `--amber` (`#F59E0B`) everywhere (ring, score text, chip) — internally consistent,
   matching what the ring already does correctly.

Both fixes are scoped to the native page only. `PilotHubViewModel.ScoreColor`/`GradeChipClass`
(the C# helpers, still used by the still-Blazor-rendered `PilotHub.razor` until this page fully
replaces it) are **not** touched — the native page computes its own grade→color mapping in
code-behind rather than reusing those two specific helper methods, so the still-live Blazor page's
existing (buggy) behavior is undisturbed until it's retired. `GradeLabel`/`GradeLetter`/`VsColor`
(no bugs found in these) are fine to reuse or reproduce identically either way.

## What gets built

### `AviatesAirTracker/Pages/PilotHubPage.xaml` (new)

- **Stat strip**: 4 cards (Total Flights/Block Hours/Avg Landing/Total nm), reusing 3 of
  Dashboard's exact icon paths (plane, clock, activity-zigzag) plus one new one — a compass/star
  path (`M12 2l3.09 6.26L22 9.27l-5 4.87 1.18 6.88L12 17.77l-6.18 3.25L7 14.14 2 9.27l6.91-1.01L12 2z`)
  for Total Distance, colored violet (`#b8934a`) to match `.big-stat-icon.violet`.
- **Rank card**: dark card, rank badge/title, and — per the research — this rank indicator is a
  **width-animated bar**, not an SVG ring (`.rank-progress-track`/`.rank-progress-fill`), simpler
  to port than Dashboard's ring: a `Border` with a fixed-width track and a second `Border` inside
  it whose `Width` is `RankProgressPct/100 * trackWidth`, computed once in code-behind after
  `RefreshAsync()` (no live/animated transition needed, same reasoning Dashboard used for its
  ring). Skip the `::after` radial-glow pseudo-element — decorative, same call Dashboard made for
  its own card glows.
- **Last Landing Analysis card**: the score ring (`Path`/`ArcSegment`, same construction technique
  as Dashboard's rank ring, but `radius=65`/`stroke-width=14` per the real CSS values, not
  Dashboard's `radius=45`/`stroke-width=8` — don't copy Dashboard's exact numbers, use these) with
  `Stroke` computed per-landing from grade (green/accent-blue/amber/red for A/B/C/D, red for F per
  the bug fix above — not the CSS's missing-color/no-stroke behavior). Below it, the 2×2 readout
  grid (V/S colored via a `VsColor`-equivalent, Pitch/Crosswind/Runway static). "View all" link to
  `/landings` — inert for now (Landing Analysis isn't a native page yet), same treatment as
  Dashboard's inert hero CTA and "See All Team" button.
- **Landing History table**: `ItemsControl` (or a `Grid`-based table layout, implementer's call —
  match whichever reads cleaner given WinUI has no direct `<table>` equivalent) bound to
  `RecentLandings`, columns per the research: Date, Airport (ICAO+runway), V/S, Pitch, Crosswind,
  Score, Grade chip. Per-row V/S and Score colors, grade chip color, all computed in code-behind
  per row (same `FlightDisplayRow`-style precomputed-display-row pattern Dashboard established for
  its Recent Flights list, to avoid needing a converter/`Style` for per-item coloring). "View
  logbook" link — inert, same reasoning as "View all."
- Empty-state handling: `RecentLandings` being empty is a genuinely reachable state (a brand-new
  pilot with zero landings) — same empty-state treatment Dashboard gave its Recent Flights list.

### `AviatesAirTracker/Navigation/NavDestinations.cs` (modified)

Pilot Hub's entry (`"pilot-hub"`) gets `NativePageType: typeof(Pages.PilotHubPage)`, following the
exact pattern Dashboard's entry already established — no other shell-plumbing changes needed,
Phase 3's Task 4 (from the Dashboard plan) already built the native-vs-WebView2 dispatch mechanism
every subsequent page just plugs into.

### `AviatesAirTracker/Pages/PilotHubPage.xaml.cs` (new)

`PilotHubViewModel` injected via `App.GetService<PilotHubViewModel>()` (already DI-registered,
`AddSingleton`), same constructor-and-`Loaded`-handler shape as `DashboardPage.xaml.cs`:
```csharp
_viewModel = App.GetService<PilotHubViewModel>();
DataContext = _viewModel;
Loaded += async (_, _) => { await _viewModel.RefreshAsync(); /* build rank bar, ring, table rows */ };
```

## What this spec explicitly does not do

- Does not build the My Bookings tab, or any tab bar UI at all — deferred, per the scope decision
  above, to its own future task once prioritized.
- Does not touch `PilotHub.razor` or delete it — left in place, unreferenced once
  `NavDestinations` points `pilot-hub` at the native page, same as `Dashboard.razor`.
- Does not fix `PilotHubViewModel.ScoreColor`/`GradeChipClass`'s underlying C# bugs — the native
  page computes its own corrected grade→color mapping instead, since those two methods still back
  the live Blazor page until it's fully retired.
- Does not build a native Landing Analysis (`/landings`) page — "View all"/"View logbook" stay
  inert links, same as every other cross-page link this migration has left inert until the target
  page itself is converted.
- Does not touch any other nav destination — every other page keeps routing through the WebView2
  exactly as before.

## Verification

- Build clean, no new warnings.
- Visual: native Pilot Hub compared against the current Blazor Career tab for layout/color
  fidelity — stat strip, rank bar, landing ring (check specifically that a grade-F landing, if any
  exists in local test data, now shows a visible red ring instead of invisible), landing history
  table (check grade-C rows render amber, not broken/uncolored).
- Functional: clicking "Pilot Hub" in the nav rail shows the native page with no WebView2 flash;
  every other nav item unaffected (same regression check Dashboard's Task 7 ran, since this page
  reuses the identical `Navigate()` dispatch path).
