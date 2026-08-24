# WinUI 3 Migration — Phase 3, second page: native Pilot Hub — Implementation Plan

**Date:** 2026-08-23
**Spec:** `docs/superpowers/specs/2026-08-23-winui3-phase3-pilothub-native-design.md` — read it
first for the "why"; this plan only covers the "what" and "how."
**Branch:** `claude/blazor-winui3-migration-a10c79`

**For agentic workers: REQUIRED SUB-SKILL:** use `superpowers:subagent-driven-development`
(recommended) or `superpowers:executing-plans` to implement this plan task-by-task.

## Before you start

**Read `AviatesAirTracker/Pages/DashboardPage.xaml`/`.xaml.cs` first.** This plan reuses its
conventions throughout rather than re-explaining them per task:
- Every visual defect Dashboard shipped with and then had fixed (empty stat-icon swatches, an
  unstyled `<Button>` rendering wrong, a literal `--` in an XAML comment crashing
  `XamlCompiler.exe`) is a mistake this plan expects you not to repeat. Read
  `docs/superpowers/plans/2026-08-23-winui3-phase3-dashboard-native.md`'s final "Visual polish"
  section for the specifics.
- No `<Style>` element, no new `Theme/Tokens.xaml` entry, anywhere in this project's XAML —
  silently crashes `XamlCompiler.exe`. Hardcode colors inline or set them in code-behind.
- No literal `--` inside any XAML comment (invalid XML, same silent crash). Use a single hyphen,
  a semicolon, or a real em dash character.
- Anything bound to XAML that needs per-item computed values (color, formatted text) needs a
  plain mutable class with `{ get; set; }` properties, built in code-behind — not an init-only
  `record`, which fails a different way (`CS8852` in the generated `XamlTypeInfo.g.cs`). Follow
  `DashboardPage.xaml.cs`'s `FlightDisplayRow`/`MonthlyActivityBarRow` pattern.
- Anything whose exact shape matters (a pill, a rounded rect) should be a `Border`, not a
  `<Button>` — this SDK has no `<Style>` available to guarantee `Button`'s default template
  respects `CornerRadius`.
- The native-vs-WebView2 shell dispatch mechanism (`ShellWindow`'s `NativeHost` `Frame`,
  `NavDestination.NativePageType`, `App.GetService<T>()`) already exists from Dashboard's Task 4 —
  this page only needs to plug into it (Task 5 below), not rebuild any of it.

## Task 1: Page skeleton, DI, stat strip

**Files:**
- New: `AviatesAirTracker/Pages/PilotHubPage.xaml`
- New: `AviatesAirTracker/Pages/PilotHubPage.xaml.cs`

- [ ] **Step 1: Page skeleton + DI resolution**

```csharp
public sealed partial class PilotHubPage : Page
{
    private readonly PilotHubViewModel _viewModel;

    public PilotHubPage()
    {
        InitializeComponent();
        _viewModel = App.GetService<PilotHubViewModel>();
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.RefreshAsync();
    }
}
```
Later tasks add more to the `Loaded` handler (building the rank bar, ring, and table rows after
`RefreshAsync()` completes) — don't overwrite this lambda in a later task, extend it.

- [ ] **Step 2: Page header**

`.page-header` in `PilotHub.razor` — a title + subtitle, plain `TextBlock`s, no data binding.
Read the actual current markup for the exact title/subtitle text rather than guessing.

- [ ] **Step 3: Stat strip — 4 cards**

Same `.big-stat-icon` pattern as Dashboard's stat strip, four cards this time
(`TotalFlights`/`BlockHours`/`AvgLandingScore`/`TotalDistance`), reusing 3 of Dashboard's exact
icon paths plus one new one:

| Card | Color | Icon path (viewBox 0 0 24 24) |
|---|---|---|
| Total Flights | blue `#4D9BEC` | `M22 16.5L13 3l-2 4-5-1L2 9l5 2-1 3 4 1 1 5 5-3z` (reuse from Dashboard) |
| Block Hours | cyan `#06B6D4` | circle `cx=12 cy=12 r=10` + polyline `12,6 12,12 16,14` (reuse from Dashboard) |
| Avg Landing | green `#3EC7AC` | polyline `22,12 18,12 15,21 9,3 6,12 2,12` (reuse from Dashboard) |
| Total nm | violet `#B8934A` | `M12 2l3.09 6.26L22 9.27l-5 4.87 1.18 6.88L12 17.77l-6.18 3.25L7 14.14 2 9.27l6.91-1.01L12 2z` (new — a compass/star) |

Reproduce these exactly the way `DashboardPage.xaml` already does (`Viewbox` containing
`Path`/`Polyline`/`Grid`-of-shapes with the literal SVG coordinate data — WinUI's Path.Data
mini-language accepts the same `M`/`L`/`z` commands as SVG). Bind values to
`{Binding TotalFlights}`/`{Binding BlockHours}`/`{Binding AvgLandingScore}`/`{Binding TotalDistance}`
(all already-formatted strings on `PilotHubViewModel`, confirm exact property names against
`ViewModels/PilotHubViewModel.cs` before binding — don't assume from this table).

- [ ] **Step 4: Build**

`dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`. This won't be reachable from
the nav rail yet (Task 5 wires that) — just confirm the page itself compiles standalone.

- [ ] **Step 5: Commit**

```bash
git add AviatesAirTracker/Pages/PilotHubPage.xaml AviatesAirTracker/Pages/PilotHubPage.xaml.cs
git commit -m "feat(pilot-hub): native page skeleton and stat strip"
```

---

## Task 2: Rank card

**Files:**
- Modify: `AviatesAirTracker/Pages/PilotHubPage.xaml`
- Modify: `AviatesAirTracker/Pages/PilotHubPage.xaml.cs`

- [ ] **Step 1: Static structure**

`.rank-card`: a dark card (skip the `::after` radial-glow pseudo-element — decorative, same call
Dashboard made for its own hero-card glows), containing:
- "🪖 Rank Status" label (static text, literal emoji is fine — check `PilotHub.razor`'s exact
  current text)
- `{Binding PilotRank}` as the title
- A subtitle combining `RankProgressPct` and `NextRank` — read `PilotHub.razor`'s exact current
  phrasing (`"{pct}% progress toward {next}"`) and reproduce it, e.g. via a multi-`TextBlock`
  `StackPanel Orientation="Horizontal"` the same way Dashboard's rank card built its "Keep flying
  to reach {NextRankText}" line, or a single formatted string set in code-behind — your call,
  just match the current wording.

- [ ] **Step 2: The progress bar — NOT a ring**

Unlike Dashboard's rank indicator, `PilotHub.razor`'s is a plain width-animated bar
(`.rank-progress-track`/`.rank-progress-fill`), not an SVG ring — simpler to build. A `Border`
(track, fixed width, muted background) containing a second `Border` (fill, accent-colored)
whose `Width` is set in code-behind once after `RefreshAsync()`:

```csharp
RankProgressFill.Width = trackWidth * (_viewModel.RankProgressPct / 100.0);
```

where `trackWidth` matches whatever fixed `Width` you give the track `Border` in XAML. No
animation/transition needed (matches Dashboard's precedent of a one-shot post-refresh
computation, not a live CSS transition).

- [ ] **Step 3: Progress labels + mini-stat row**

`.rank-progress-labels` (current rank / next rank, small text below the bar) and the 3-column
mini-stat row (`TotalFlights`/`BlockHours`/`AvgLandingScore` again — yes, duplicated from the
stat strip above; that's what the current page already does, not a mistake to "fix").

- [ ] **Step 4: Build, `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`, commit**

```bash
git add AviatesAirTracker/Pages/PilotHubPage.xaml AviatesAirTracker/Pages/PilotHubPage.xaml.cs
git commit -m "feat(pilot-hub): rank progress card"
```

---

## Task 3: Last Landing Analysis card — the score ring

**Files:**
- Modify: `AviatesAirTracker/Pages/PilotHubPage.xaml`
- Modify: `AviatesAirTracker/Pages/PilotHubPage.xaml.cs`

This is the riskiest task in this plan — the one genuinely new shape-problem versus Dashboard
(a dynamically-colored ring, not a single static accent color) plus a real bug fix.

- [ ] **Step 1: Panel header + empty state**

`.panel-header` ("Last Landing Analysis" title + inert "View all" link/`Border`, matching
Dashboard's inert-link treatment — no `/landings` native page exists yet). Empty state
(`!VM.HasLandings`): centered muted text, read the exact current copy from `PilotHub.razor`.

- [ ] **Step 2: The score ring — grade-dependent color, and a real bug fix**

`app.css`'s ring: `r=65`, `stroke-width=14`, `stroke-dasharray=408` (circumference `2π×65≈408.4`)
— **different numbers from Dashboard's rank ring** (`r=45`, `stroke-width=8`). Don't copy
Dashboard's `BuildRingArc` call site numbers; use these. The construction technique itself (a
`Path`/`ArcSegment` sweeping clockwise from 12 o'clock) is identical to what
`DashboardPage.xaml.cs`'s `BuildRingArc` already does — reuse that exact method if it's
accessible/appropriate (same assembly, `Pages` namespace — check whether it makes sense to extract
it to a small shared helper now that two pages need it, or duplicate the ~25-line method; your
call, but if you duplicate, add a comment noting the duplication so a future third consumer knows
to extract it then rather than a third copy).

**The color, and the bug fix from the spec**: `Path.Stroke` must be computed per-landing from
`LastLandingGradeClass`/grade, NOT a hardcoded single brush like Dashboard's ring. Map:
- Grade A (`grade-a`) → `#3EC7AC` (green)
- Grade B (`grade-b`) → `#4D9BEC` (accent blue)
- Grade C (`grade-c`) → `#F59E0B` (amber) — this is the SAME color the CSS already correctly uses;
  no bug here, just confirming the mapping
- Grade D (`grade-d`) → `#EF4444` (red)
- Grade F (`grade-f`) → **`#EF4444` (red)** — **this is the bug fix**. The current CSS has no
  `.grade-f` rule at all, so a real F-grade landing renders with an invisible ring stroke in the
  live Blazor app today. The native page must not reproduce that — map F to red explicitly.

Check `PilotHubViewModel.GradeClass(int score)` for the exact score→grade-string mapping (already
read in prior research: `>=90 A, >=75 B, >=60 C, >=40 D, else F`) and write your own small
grade→color mapping in `PilotHubPage.xaml.cs` (don't call `PilotHubViewModel.GradeClass` and then
string-match its output — call it, get the grade string, switch on it directly, same shape as
Dashboard's `PilotStatsLogic.ComputeRankProgress` switch pattern). Center text: score number +
grade label (`{Binding LastLanding.LandingScore}` / computed grade label text).

- [ ] **Step 3: The 2×2 readout grid**

V/S (color via a `VsColor`-equivalent switch — reproduce `PilotHubViewModel.VsColor`'s exact
thresholds in code-behind, don't call the C# method directly if it's `private`/inaccessible from
`Pages`; check its accessibility first), Pitch (static accent color), Crosswind
(`Math.Abs(...)`, static text-primary color), Runway (`AirportICAO` + `RunwayIdentifier`, static
text-primary color). Match `PilotHub.razor`'s exact current formatting (`"F0"`/`"F1"` decimal
places, `"fpm"`/`"°"`/`"kts"` suffixes).

- [ ] **Step 4: Build, `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`, commit**

```bash
git add AviatesAirTracker/Pages/PilotHubPage.xaml AviatesAirTracker/Pages/PilotHubPage.xaml.cs
git commit -m "fix(pilot-hub): native landing-score ring with grade colors, fixing the invisible grade-F ring"
```

---

## Task 4: Landing History table

**Files:**
- Modify: `AviatesAirTracker/Pages/PilotHubPage.xaml`
- Modify: `AviatesAirTracker/Pages/PilotHubPage.xaml.cs`

- [ ] **Step 1: Section header + empty state**

"Landing History" title + inert "View logbook" link, same treatment as Task 3's "View all". Empty
state (`!VM.HasLandings`): centered muted text, exact current copy.

- [ ] **Step 2: Table as an `ItemsControl`**

WinUI has no `<table>` equivalent — a `Grid`-per-row `ItemsControl` (columns: Date, Airport, V/S,
Pitch, Crosswind, Score, Grade), same general shape as Dashboard's Recent Flights list but with
more columns and per-column coloring. Build a precomputed display-row class, same pattern as
`DashboardPage.xaml.cs`'s `FlightDisplayRow`:

```csharp
public sealed class LandingDisplayRow
{
    public string AirportLine { get; set; } = "";
    public string DateText { get; set; } = "";
    public string RunwayText { get; set; } = "";
    public string VsText { get; set; } = "";
    public Brush VsColor { get; set; } = ...;
    public string PitchText { get; set; } = "";
    public string CrosswindText { get; set; } = "";
    public string ScoreText { get; set; } = "";
    public Brush ScoreColor { get; set; } = ...;
    public string GradeLetter { get; set; } = "";
    public Brush GradeChipColor { get; set; } = ...;
}
```

Plain mutable class, not a `record` — same `CS8852` reasoning as `FlightDisplayRow`.

- [ ] **Step 3: The second bug fix — grade-C color**

Per the spec: `PilotHubViewModel.ScoreColor()`/`GradeChipClass()` emit an undefined
`var(--yellow)`/`chip-yellow` for the 60–74 (grade C) band in the current Blazor page — silently
broken in the browser. The native page must not reproduce this. Write your own
score→color mapping in code-behind (same reasoning as Task 3 — don't call the buggy C# helpers):
- `>= 90`: green `#3EC7AC`
- `>= 75`: accent blue `#4D9BEC`
- `>= 60`: **amber `#F59E0B`** (the fix — was undefined yellow)
- `>= 40`: red `#EF4444` (matches `GradeChipClass`'s existing `chip-orange`/`chip-red` split for
  D/F — check the exact existing threshold split for D vs F chip colors in `PilotHubViewModel`
  and preserve it if it differs from this simplified 4-tier list; this table is illustrative, not
  gospel — verify against the actual method)
- `< 40`: red `#EF4444`

Apply this same corrected mapping to BOTH the score text color and the grade chip color, so they
stay internally consistent (today's bug has the ring using amber for C while the table uses broken
yellow — the fix makes all three agree).

- [ ] **Step 4: Wire it together**

```csharp
private void RebuildLandingRows()
{
    LandingRows.Clear();
    foreach (var l in _viewModel.RecentLandings)
    {
        LandingRows.Add(new LandingDisplayRow { /* ... */ });
    }
}
```
Called from the `Loaded` handler after `RefreshAsync()`, same as Dashboard's
`RebuildRecentFlightRows()`/`RebuildMonthlyActivityRows()`.

- [ ] **Step 5: Build, `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`, commit**

```bash
git add AviatesAirTracker/Pages/PilotHubPage.xaml AviatesAirTracker/Pages/PilotHubPage.xaml.cs
git commit -m "fix(pilot-hub): native landing history table, fixing the undefined grade-C color"
```

---

## Task 5: Shell wiring + end-to-end verification

**Files:**
- Modify: `AviatesAirTracker/Navigation/NavDestinations.cs`

- [ ] **Step 1: Point the nav destination at the native page**

```csharp
new("pilot-hub", "Pilot Hub", "/pilot-hub", "Pilot", NativePageType: typeof(Pages.PilotHubPage)),
```
Exactly the pattern Dashboard's entry already established — no other shell changes needed, since
`ShellWindow.Navigate()`'s native-vs-WebView2 branch already exists.

- [ ] **Step 2: Full solution build and test suite**

`dotnet build AviatesAirTracker.sln -c Debug` — 0 errors, no new warnings beyond the established
baseline. `dotnet test tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj` — full suite
passing (this plan adds no new tests of its own; there's no new pure/testable logic the way
Dashboard's rank-progress and monthly-activity aggregation were — everything here is direct
per-item color/text formatting inside a `Page`, not a candidate for the TDD treatment).

- [ ] **Step 3: Startup check**

Check `tasklist` for MSFS first (should not be running). Launch the built exe briefly, confirm a
clean Serilog startup with no `[Shell]` errors or unhandled exceptions, close it. **Be
conservative about touching the user's desktop** — no computer-use/screenshot verification in this
task, same practice as every prior page/phase in this migration. If you cannot determine from logs
alone whether Pilot Hub actually renders correctly when clicked, say so plainly and defer to a
human visual check, rather than assume success.

- [ ] **Step 4: Record results and commit**

Append a results section to this plan doc: pass/fail per step above, and explicitly call out both
bug fixes (grade-F ring color, grade-C table/ring color) as resolved, with the exact hex values
used, so the historical record is unambiguous about what changed and why. Commit:

```bash
git add -f docs/superpowers/plans/2026-08-23-winui3-phase3-pilothub-native.md AviatesAirTracker/Navigation/NavDestinations.cs
git commit -m "feat(pilot-hub): wire native page into the nav rail"
```

(Split into two commits if that reads cleaner — one for the `NavDestinations.cs` change, one for
the docs update — implementer's call, this project has done it both ways.)

---

## Definition of done

- Build clean, full test suite passing
- Clicking "Pilot Hub" in the nav rail shows the native page with no WebView2 flash; every other
  nav item (including the now-native Dashboard) unaffected
- The two pre-existing color bugs (invisible grade-F ring, undefined grade-C color) are fixed in
  the native page, not reproduced
- No `.razor` file touched; `PilotHub.razor` left in place, unreferenced
- My Bookings tab is explicitly out of scope — not built, not stubbed, no tab bar at all in this
  pass (a single-tab Career-only page)
