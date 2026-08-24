# WinUI 3 Migration — Phase 3, fourth conversion: native landing-result dialog

**Date:** 2026-08-23
**Status:** Draft, awaiting review
**Branch:** `claude/blazor-winui3-migration-a10c79`
**Builds on:** Dashboard, Pilot Hub, Flights, all complete — reuses the ring-drawing technique
(`Path`/`ArcSegment`), `ContentDialog` (established by Flights' delete/force-remove dialogs), and
file-path-based image loading for `wwwroot` assets.

## Correction to the original plan for this session

The original intent was a fourth **page** conversion, targeting `/landings` — the route Pilot
Hub's "View all" and "View logbook" links point at. Research found **that route doesn't exist**:
there is no `LandingAnalysis.razor`, no `/landings` page anywhere in the current app. Those two
links are dead (would 404), not merely unported. Building a `/landings` logbook page now would be
net-new feature design with no existing UI to reference — out of scope for "convert what exists,"
the same reasoning that kept Bookings and the backend rank integration out of earlier specs. That
work is left as an explicitly separate future feature, not folded in here.

What *does* exist and *is* a real, working, unconverted piece of native-equivalent UI:
**`AviatesAirTracker.LegacyWeb/Shared/LandingResultModal.razor`** — a global overlay that pops up
automatically after every landing (rendered once in `MainLayout.razor`, not page-scoped), showing
a score ring, grade badge, a "butter meter" headline, quality flags, a touchdown-parameters grid,
a score-breakdown bar list, and footer stats. This spec converts that instead.

## A real architectural finding: don't use `LandingAnalysisViewModel`

`AviatesAirTracker/ViewModels/LandingAnalysisViewModel.cs` is DI-registered and does run on every
real landing (`MainViewModel.LandingAnalysis.AddLanding(result)`, called from
`AlertService.LandingResultReady`) — but nothing renders from it. `MainViewModel.NavigateToLanding()`
sets `CurrentPage`/`IsLandingActive`, and nothing in `ShellWindow` or anywhere else binds to
`MainViewModel.CurrentPage` (confirmed via grep) — a leftover from a pre-`ShellWindow` navigation
design, the same "wired to a ghost UI" pattern `DashboardViewModel` was in before Dashboard's own
conversion. Beyond being unused, it independently **recomputes a simpler, different set of fields
than the actual modal uses** — `VsText`/`PitchText`/etc. formatted directly from `LandingResult`,
not from `LandingReport`.

The actual modal builds from **`Core/Analytics/LandingReport.cs`** via `LandingReportBuilder.Build(...)`
— and this project's own `CLAUDE.md` states the architectural rule explicitly: *"Landing reports
are shared: `LandingReport.cs` is the single projection rendered by BOTH the desktop
`LandingResultModal.razor` and the EFB Landing app. Add pilot-facing landing data there, never to
one UI only."* `LandingReport`/`LandingReportBuilder` is self-contained (every color is a hardcoded
hex string computed in C#, no CSS-class-name lookups the way Pilot Hub's grade-C/F bugs came
from — confirmed no missing-color bug exists here) and already correct.

**This spec's native dialog consumes `LandingReportBuilder.Build(...)` directly, the same way the
EFB companion already does — not `LandingAnalysisViewModel`.** `LandingAnalysisViewModel` is left
in place, untouched, unreferenced by anything new (same "leave old code in place" precedent as
every prior page's now-orphaned `.razor` file) — it keeps running its harmless, now-doubly-dead
side effect on every landing; not worth touching in this task.

## Where this lives: the shell, not a page

The original modal renders globally in `MainLayout.razor`, independent of which page is showing —
it must appear over Dashboard, Flights, or a still-WebView2-hosted page equally. The native
equivalent belongs in **`ShellWindow`**, the one place already wired for shell-wide concerns
(`ShellWindow.xaml.cs` already subscribes to `FlightSessionManager.SessionStateChanged` and
`MessagingService.UnreadCountChanged` for the nav-rail badges, and already injects
`IFlightRepository`). This is a new kind of task for this migration: nothing built so far has
lived at the shell level rather than inside a `Page`.

## What gets built

### `AviatesAirTracker/Dialogs/LandingResultDialog.cs` (new)

A dedicated builder class (not a `Page`) — `ShellWindow` shouldn't grow the full ~250-line dialog
layout inline the way Flights' smaller confirm/reason dialogs were built inline in its own
code-behind; this one is big enough to warrant its own file, the same way `DashboardPage`'s
`BuildRingArc` earned its own reusable method once a second consumer needed it.

- `public static ContentDialog Build(XamlRoot xamlRoot, LandingReport report)` — constructs and
  returns the `ContentDialog`, caller calls `ShowAsync()`.
- **Score ring**: `r=80`, matching `Circumference = 2π×80 ≈ 502.65` (the modal's own constant) —
  reuse `DashboardPage.BuildRingArc`'s technique (already `internal static`, reused once already
  by Pilot Hub) with these radius/stroke-width values, not Dashboard's or Pilot Hub's own numbers.
  `Stroke` set directly from `report.GradeColor` (already a hex string, no grade-name switch
  needed here — unlike Pilot Hub, this report type hands you the color directly).
- **Grade badge, butter meter, quality flags**: static-ish content, direct property bindings —
  `report.Grade`/`GradeColor`, `report.ButterRating`/`ButterBlurb`/`ButterColor`,
  `report.StableApproach`/`FlareDetected`/`BounceCount`/`IsPersonalBest` flag chips (`Border`-based
  pills, matching the established chip pattern from every prior page).
- **Touchdown parameters grid** (6 cells: VS, IAS, pitch, bank, headwind, crosswind) — plain
  bound text, with the two "warn" thresholds (`VerticalSpeedFpm > 600`, `BankDeg > 3`) driving a
  different (warning-colored) cell background, matching the modal's `param-warn` class.
- **Score breakdown bars** (6 factors from `report.Factors`) — a `Border`-track +
  `Border`-fill-width pattern, same technique Pilot Hub's rank-progress bar already established
  (`Width` set once in code-behind, not a live transition).
- **Footer stats** (rollout/threshold/wind/fuel, each conditionally shown per the modal's own
  `> 10`/`> 0` guards) + the Close button (a `Border`, not a `<Button>`, though `ContentDialog`'s
  own `CloseButtonText` may be simpler and more idiomatic here than a custom footer button — your
  call during implementation, whichever reads cleaner; `ContentDialog` already provides a
  dismiss mechanism for free).
- **Aircraft photo**: `report.AircraftImagePath` (nullable — only 3 aircraft types have one, per
  `LandingReportBuilder`'s own small lookup table) loaded the same file-path-from-
  `AppContext.BaseDirectory` way Dashboard's `hero.webp` and Flights' flag icons already are;
  `null` → no photo, gradient-only background, matching the modal's own conditional `@if`.

**Explicitly not reproduced**: the elaborate entrance choreography (ring animating from empty to
final score over ~260ms, staggered bar-width transitions, backdrop fade-in/out, 260ms exit
animation before the element unmounts). Same precedent as Dashboard's hero SVG and every prior
page's CSS `@keyframes`/`transition` — show the final state directly, no WinUI
`Storyboard`/Composition equivalent attempted in this pass. This is a real, acknowledged fidelity
gap, not silently dropped.

**The 60-second auto-dismiss timer** — this is simple, real, functional behavior (not decorative
animation), worth keeping: a `DispatcherTimer` (or `System.Threading.Timer` marshaled back to the
UI thread) that calls `dialog.Hide()` after 60s if the pilot hasn't already dismissed it, disposed
if the dialog closes earlier for any reason.

### `AviatesAirTracker/ShellWindow.xaml.cs` (modified)

- New constructor dependencies: `ILandingRepository`, `AlertService` (`IFlightRepository` is
  already injected, for the Flights nav badge).
- Subscribe to `AlertService.LandingResultReady` (same event `MainViewModel`/the old modal both
  already use — no new plumbing needed at the source).
- On the event: fetch flight history the same way `LandingResultModal.razor`'s `BuildReportAsync`
  does (`await LandingRepo.GetAllAsync()`, look up the owning flight from `FlightRepo.GetAllAsync()`
  by matching `result.FlightId`), call `LandingReportBuilder.Build(result, flight, history)`, then
  `LandingResultDialog.Build(this.XamlRoot, report).ShowAsync()`. Wrap in the same best-effort
  try/catch the original modal uses ("failures here must never block the popup") — if the flight
  lookup fails, still show the dialog with `flight: null`, matching the original's own resilience.

## What this spec explicitly does not do

- Does not build a `/landings` logbook page — explicitly deferred, see the correction above.
- Does not touch `LandingResultModal.razor`, `MainLayout.razor`, or `LandingAnalysisViewModel` —
  all left in place, unreferenced by this new code, same "leave old code alone" precedent as
  every prior conversion.
- Does not reproduce entrance/exit animations — static final-state rendering only.
- Does not fix Pilot Hub's two dead `/landings` links — they stay dead until (if ever) a real
  logbook page gets built as its own separately-scoped task.

## Verification

- Build clean, no new warnings.
- Functional: confirm `ShellWindow`'s constructor changes don't break app startup (new DI
  dependencies resolve correctly). Actually triggering a real landing to see the dialog requires
  MSFS running and a live flight — not something to force; if that's not available, this is
  explicitly deferred to a human/future live-session check, same as every prior page's live-MSFS-
  gated functional checks.
- Visual: compare against the current Blazor modal for layout/color fidelity on everything except
  the acknowledged-gap animations.
