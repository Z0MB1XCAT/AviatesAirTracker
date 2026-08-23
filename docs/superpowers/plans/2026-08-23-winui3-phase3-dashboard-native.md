# WinUI 3 Migration — Phase 3, first page: native Dashboard — Implementation Plan

**Date:** 2026-08-23
**Spec:** `docs/superpowers/specs/2026-08-23-winui3-phase3-dashboard-native-design.md` — read it
first for the "why" behind every decision below; this plan only covers the "what" and "how."
**Branch:** `claude/blazor-winui3-migration-a10c79`

**For agentic workers: REQUIRED SUB-SKILL:** use `superpowers:subagent-driven-development`
(recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use
checkbox format (`- [ ]`).

## Before you start: two real, pre-existing bugs this plan fixes in passing

1. `PilotHubViewModel.ComputeRankProgress` switches on rank-name strings (`"Student Pilot"`,
   `"First Officer"`, `"Senior First Officer"`, `"Captain"`) that don't match what
   `PilotStatsService.ComputeAsync()` actually produces (`"Cadet"`, `"Junior First Officer"`,
   `"First Officer"`, `"Senior First Officer"`, `"Junior Captain"`, `"Captain"`,
   `"Senior Captain"`). Four of the seven real ranks fall through to the switch's default arm and
   silently show "Top Rank" at 100% progress — including a brand-new Cadet with zero hours. Task 1
   fixes this while extracting the logic to somewhere both `PilotHubViewModel` and the new
   `DashboardViewModel` can share it.
2. `DashboardViewModel.RefreshAsync()` has an existing, deliberate fix (see its `MAJOR-07`
   comment): it fetches `GetAllAsync()` once for stats (`TotalFlights`/`TotalHours`/etc.) and only
   slices `.Take(5)` off that same list for the `RecentFlights` display, specifically because an
   earlier version used `GetRecentAsync(5)` for everything and corrupted the *total* counts.
   **Do not revert this.** Task 3 below adds a *separate* `GetRecentAsync(5)` call for the
   `RecentFlights` list only (it filters to `Completed` and sorts by date, which raw
   `allFlights.Take(5)` doesn't) — the stats calculation keeps using `allFlights` from
   `GetAllAsync()`, untouched.

## Task 1: Fix + extract rank-progress logic, TDD

**Files:**
- New: `AviatesAirTracker/Services/PilotStatsLogic.cs`
- New test: `tests/AviatesAirTracker.Tests/PilotStatsLogicTests.cs`
- Modify: `AviatesAirTracker/ViewModels/PilotHubViewModel.cs`
- Modify: `tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj`

- [ ] **Step 1: Create the pure-logic file**

`PilotStatsService` (`Services/SupportServices.cs`) is a big shared file with dependencies
(`Serilog`, `Core.Data`, `Core.SimConnect`) that don't link cleanly into the plain-`net8.0` test
project — the same friction Phase 2's Task 6 hit with `ShellBadgeLogic`, solved the same way
there: pure logic goes in its own small file with zero WinUI/service dependencies, so it can be
`Compile Include`-linked into the test project directly.

```csharp
namespace AviatesAirTracker.Services;

public static class PilotStatsLogic
{
    /// <summary>
    /// Rank-name strings here MUST exactly match PilotStatsService.ComputeAsync()'s rank
    /// thresholds (Cadet/Junior First Officer/First Officer/Senior First Officer/Junior
    /// Captain/Captain/Senior Captain) — a prior version of this method (previously private
    /// in PilotHubViewModel) used different strings and silently misfired for 4 of 7 ranks.
    /// If PilotStatsService's rank thresholds ever change, this switch must change with them.
    /// </summary>
    public static (string NextRank, double ProgressPct) ComputeRankProgress(double hours, string rank) => rank switch
    {
        "Cadet"                => ("Junior First Officer",  Math.Min(hours / 25.0, 1.0) * 100),
        "Junior First Officer" => ("First Officer",         Math.Min((hours - 25) / 50.0, 1.0) * 100),
        "First Officer"        => ("Senior First Officer",  Math.Min((hours - 75) / 125.0, 1.0) * 100),
        "Senior First Officer" => ("Junior Captain",        Math.Min((hours - 200) / 250.0, 1.0) * 100),
        "Junior Captain"       => ("Captain",               Math.Min((hours - 450) / 350.0, 1.0) * 100),
        "Captain"              => ("Senior Captain",        Math.Min((hours - 800) / 700.0, 1.0) * 100),
        _                      => ("Top Rank",               100.0)
    };
}
```

Double-check the threshold boundaries above against `PilotStatsService.ComputeAsync()`'s actual
switch (`< 25`/`< 75`/`< 200`/`< 450`/`< 800`/`< 1500`) before committing to these numbers — they
should produce a continuous 0→100 ramp across each rank's own hour range (e.g. Cadet is 0–25h so
its progress denominator is 25; Junior First Officer is 25–75h so its range is 50, etc.). Verify
the arithmetic yourself rather than trust this snippet blindly; get it right and write a test that
would catch it if wrong.

- [ ] **Step 2: Write the failing tests first**

```csharp
using AviatesAirTracker.Services;
using Xunit;

namespace AviatesAirTracker.Tests;

public class PilotStatsLogicTests
{
    [Theory]
    [InlineData(0,    "Cadet",                "Junior First Officer")]
    [InlineData(25,   "Junior First Officer", "First Officer")]
    [InlineData(75,   "First Officer",        "Senior First Officer")]
    [InlineData(200,  "Senior First Officer", "Junior Captain")]
    [InlineData(450,  "Junior Captain",       "Captain")]
    [InlineData(800,  "Captain",              "Senior Captain")]
    [InlineData(1500, "Senior Captain",       "Top Rank")]
    public void ComputeRankProgress_EveryRealRank_ReturnsCorrectNextRank(double hours, string rank, string expectedNext)
    {
        var (nextRank, _) = PilotStatsLogic.ComputeRankProgress(hours, rank);
        Assert.Equal(expectedNext, nextRank);
    }

    [Fact]
    public void ComputeRankProgress_BrandNewCadet_IsNotTopRank()
    {
        // The bug this fixes: a 0-hour Cadet previously fell through to the default arm
        // and showed "Top Rank" at 100%, because "Cadet" didn't match any case label.
        var (nextRank, pct) = PilotStatsLogic.ComputeRankProgress(0, "Cadet");
        Assert.NotEqual("Top Rank", nextRank);
        Assert.Equal(0, pct, precision: 1);
    }

    [Fact]
    public void ComputeRankProgress_MidwayThroughARank_IsRoughlyHalfway()
    {
        // Cadet is 0-25h; 12.5h should be ~50%.
        var (_, pct) = PilotStatsLogic.ComputeRankProgress(12.5, "Cadet");
        Assert.Equal(50, pct, precision: 0);
    }

    [Fact]
    public void ComputeRankProgress_UnknownRankString_FallsBackToTopRank()
    {
        var (nextRank, pct) = PilotStatsLogic.ComputeRankProgress(999, "Something Made Up");
        Assert.Equal("Top Rank", nextRank);
        Assert.Equal(100, pct);
    }
}
```

Add `<Compile Include="../../AviatesAirTracker/Services/PilotStatsLogic.cs" Link="Services/PilotStatsLogic.cs" />`
to the test csproj.

- [ ] **Step 3: Confirm the tests fail** (compile error, since `PilotStatsLogic` doesn't exist
      yet) — `dotnet test tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj`

- [ ] **Step 4: Confirm the tests pass** once Step 1's file is in place.

- [ ] **Step 5: Update `PilotHubViewModel` to use the shared, corrected method**

Delete the private `ComputeRankProgress` method entirely from `PilotHubViewModel.cs`. Change the
call site:
```csharp
(NextRank, RankProgressPct) = PilotStatsLogic.ComputeRankProgress(stats.TotalHoursBlock, stats.Rank);
```
Note it already used `stats.TotalHoursBlock` (not `TotalHoursAir`, which is what
`PilotStatsService.ComputeAsync()`'s own `Rank` field is computed from) — **this Block-vs-Air
mismatch between the rank threshold and the progress-within-rank calculation predates this task
and is not in scope to fix here.** Preserve it exactly as-is; don't "helpfully" change which hours
metric feeds progress while you're in this code, since that's an unrelated behavior change with
its own consequences worth its own review, not a drive-by.

- [ ] **Step 6: Build and run `dotnet test`** — full suite should be green (existing 22 tests +
      the new `PilotStatsLogicTests`).

- [ ] **Step 7: Commit**

```bash
git add AviatesAirTracker/Services/PilotStatsLogic.cs AviatesAirTracker/ViewModels/PilotHubViewModel.cs tests/AviatesAirTracker.Tests/
git commit -m "fix(pilot-stats): correct rank-progress logic and extract it for reuse

PilotHubViewModel.ComputeRankProgress switched on rank-name strings
that didn't match PilotStatsService.ComputeAsync()'s actual output --
4 of 7 real ranks silently showed 'Top Rank' at 100%, including a
brand-new Cadet. Fixed and moved to PilotStatsLogic so Phase 3's
Dashboard page can share the corrected calculation instead of
duplicating (and potentially re-breaking) it."
```

---

## Task 2: Monthly flight-activity aggregation, TDD

**Files:**
- Modify: `AviatesAirTracker/Services/PilotStatsLogic.cs`
- Modify: `tests/AviatesAirTracker.Tests/PilotStatsLogicTests.cs`
- Modify: `AviatesAirTracker/Services/SupportServices.cs` (`PilotStatsService`)

- [ ] **Step 1: Add the pure aggregation to `PilotStatsLogic.cs`**

```csharp
public readonly record struct MonthlyActivityPoint(string Label, int FlightCount);

/// <summary>
/// Groups completed flights by calendar month, oldest to newest, always returning exactly
/// `months` points (zero-filled for months with no flights) ending at `asOfUtc`'s month. The
/// UI needs a fixed-width bar row, not a sparse list that skips empty months.
/// </summary>
public static IReadOnlyList<MonthlyActivityPoint> ComputeMonthlyActivity(
    IEnumerable<FlightRecord> completedFlights, DateTime asOfUtc, int months = 7)
{
    var counts = completedFlights
        .GroupBy(f => new DateTime(f.CreatedAt.Year, f.CreatedAt.Month, 1))
        .ToDictionary(g => g.Key, g => g.Count());

    var points = new List<MonthlyActivityPoint>();
    for (int i = months - 1; i >= 0; i--)
    {
        var monthStart = new DateTime(asOfUtc.Year, asOfUtc.Month, 1).AddMonths(-i);
        points.Add(new MonthlyActivityPoint(
            monthStart.ToString("MMM"),
            counts.TryGetValue(monthStart, out var c) ? c : 0));
    }
    return points;
}
```

`asOfUtc` is a parameter (not `DateTime.UtcNow` read internally) specifically so the tests below
are deterministic — don't have this method read the clock itself.

- [ ] **Step 2: Write the failing tests**

```csharp
[Fact]
public void ComputeMonthlyActivity_EmptyFlights_ReturnsSevenZeroPoints()
{
    var points = PilotStatsLogic.ComputeMonthlyActivity([], new DateTime(2026, 8, 23));
    Assert.Equal(7, points.Count);
    Assert.All(points, p => Assert.Equal(0, p.FlightCount));
    Assert.Equal("Aug", points[^1].Label);
    Assert.Equal("Feb", points[0].Label);
}

[Fact]
public void ComputeMonthlyActivity_FlightsInDifferentMonths_CountedSeparately()
{
    var flights = new[]
    {
        new FlightRecord { CreatedAt = new DateTime(2026, 8, 1) },
        new FlightRecord { CreatedAt = new DateTime(2026, 8, 15) },
        new FlightRecord { CreatedAt = new DateTime(2026, 7, 1) },
    };
    var points = PilotStatsLogic.ComputeMonthlyActivity(flights, new DateTime(2026, 8, 23));
    Assert.Equal(2, points[^1].FlightCount);  // Aug
    Assert.Equal(1, points[^2].FlightCount);  // Jul
}

[Fact]
public void ComputeMonthlyActivity_FlightOlderThanWindow_Excluded()
{
    var flights = new[] { new FlightRecord { CreatedAt = new DateTime(2025, 1, 1) } };
    var points = PilotStatsLogic.ComputeMonthlyActivity(flights, new DateTime(2026, 8, 23));
    Assert.All(points, p => Assert.Equal(0, p.FlightCount));
}
```

- [ ] **Step 3/4: Confirm fail then pass**, same as Task 1.

- [ ] **Step 5: Wire the async wrapper into `PilotStatsService`**

```csharp
public async Task<IReadOnlyList<MonthlyActivityPoint>> GetMonthlyActivityAsync(int months = 7)
{
    var flights = await _flightRepo.GetAllAsync();
    var completed = flights.Where(f => f.Status == FlightStatus.Completed);
    return PilotStatsLogic.ComputeMonthlyActivity(completed, DateTime.UtcNow, months);
}
```

- [ ] **Step 6: Build, `dotnet test`, commit**

```bash
git add AviatesAirTracker/Services/ tests/AviatesAirTracker.Tests/
git commit -m "feat(pilot-stats): add monthly flight-activity aggregation"
```

---

## Task 3: `DashboardViewModel` real-data wiring

**Files:**
- Modify: `AviatesAirTracker/ViewModels/DashboardViewModel.cs`

- [ ] **Step 1: Add `PilotStatsService` as a constructor dependency** (already DI-registered as a
      singleton in `App.xaml.cs` — no registration change needed).

- [ ] **Step 2: Add the new observable properties**

```csharp
public ObservableCollection<MonthlyActivityPoint> MonthlyActivity { get; } = [];
```

Change `RankProgress`'s meaning from a 0–1 fraction (its current dead default, `0.15`) to a 0–100
percentage, matching `PilotStatsLogic.ComputeRankProgress`'s natural output and
`PilotHubViewModel.RankProgressPct`'s existing convention — one convention across both ViewModels
rather than two. This is a safe change: nothing currently consumes `RankProgress` for real (it's
dead code today), so there's no existing UI behavior to preserve. The native ring control built in
Task 6 divides by 100 itself when computing its arc sweep.

- [ ] **Step 3: Update `RefreshAsync()`**

```csharp
public async Task RefreshAsync()
{
    IsLoading = true;
    try
    {
        var allFlights = await _flightRepo.GetAllAsync();   // unchanged -- stats need ALL flights, see MAJOR-07 note at top of this plan
        var landings   = await _landingRepo.GetAllAsync();
        var recent     = await _flightRepo.GetRecentAsync(5); // separate call: filtered+sorted, unlike allFlights.Take(5)

        RecentFlights.Clear();
        foreach (var f in recent) RecentFlights.Add(f);

        RecentLandings.Clear();
        foreach (var l in landings.Take(5)) RecentLandings.Add(l);

        TotalFlightsText    = allFlights.Count.ToString();
        TotalHoursText      = $"{allFlights.Sum(f => f.BlockTime.TotalHours):F1}";
        AvgLandingScoreText = landings.Any() ? $"{landings.Average(l => l.LandingScore):F0}" : "—";
        TotalDistanceText   = $"{allFlights.Sum(f => f.ActualDistanceNm):F0}";

        var stats = await _statsService.ComputeAsync();
        PilotRank = stats.Rank;
        (NextRankText, RankProgress) = PilotStatsLogic.ComputeRankProgress(stats.TotalHoursBlock, stats.Rank);
        // NextRankText's display format ("25hrs to Captain") differs from the raw next-rank
        // name PilotStatsLogic returns ("Captain") -- decide the exact wording in DashboardPage
        // XAML/binding (Task 5) rather than baking a specific string format into the ViewModel;
        // expose the raw NextRank name here and format it for display at the binding site, OR
        // format it here if that's simpler -- your call, but be consistent with how
        // PilotHubViewModel exposes NextRank (a raw name, not a pre-formatted sentence) so the
        // two ViewModels don't diverge in what they consider "the next rank" data shape.

        MonthlyActivity.Clear();
        foreach (var point in await _statsService.GetMonthlyActivityAsync()) MonthlyActivity.Add(point);

        WelcomeText = $"Welcome back, {(_settings.Settings.PilotName.Length > 0 ? _settings.Settings.PilotName : "Pilot")}";
    }
    finally
    {
        IsLoading = false;
    }
}
```

Resolve the `NextRankText` naming/format question above before writing this for real — read how
`PilotHubViewModel.NextRank` is actually consumed today (it's Blazor-bound, so check
`PilotHub.razor`'s markup for the exact surrounding text) to decide whether `DashboardViewModel`
should match that shape or keep its own pre-formatted sentence. Either is fine; just make a
deliberate choice and don't leave two subtly different "next rank" data shapes by accident.

- [ ] **Step 4: Build** (this will not fully compile end-to-end until Task 5's `DashboardPage`
      exists to consume it, but `DashboardViewModel.cs` itself should compile standalone — verify
      with `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`).

- [ ] **Step 5: Commit**

```bash
git add AviatesAirTracker/ViewModels/DashboardViewModel.cs
git commit -m "feat(dashboard): wire DashboardViewModel to real recent-flights, rank and activity data"
```

---

## Task 4: Shell native-page hosting plumbing

**Files:**
- Modify: `AviatesAirTracker/Navigation/NavDestinations.cs`
- Modify: `AviatesAirTracker/App.xaml.cs`
- Modify: `AviatesAirTracker/ShellWindow.xaml`
- Modify: `AviatesAirTracker/ShellWindow.xaml.cs`

This is the one-time cost every later Phase 3 page reuses — get it right here, not
Dashboard-specific despite Dashboard being the only consumer today.

- [ ] **Step 1: `NavDestination` gets an optional native-page type**

```csharp
public sealed record NavDestination(string Tag, string Label, string Route, string? Group, Type? NativePageType = null);
```

Optional with a default so the other 12 entries in `NavDestinations.All` don't need touching.
Dashboard's entry becomes:
```csharp
new("dashboard", "Dashboard", "/", "Overview", NativePageType: typeof(Pages.DashboardPage)),
```
(This line will not compile until Task 5 creates `Pages/DashboardPage.xaml` — that's fine, Task 5
follows immediately after in execution order; just don't consider Task 4 "done and verified" until
Task 5 lands and the whole thing builds together. Note this dependency in your report.)

- [ ] **Step 2: Expose service resolution for parameterless-constructed pages**

`Frame.Navigate(Type)` constructs the target page via its parameterless constructor — it has no
way to run this app's constructor-injection DI the way `ShellWindow` itself gets built. Add a
minimal static accessor to `App.xaml.cs`, right after `_serviceProvider = services.BuildServiceProvider();`:

```csharp
public static T GetService<T>() where T : class =>
    ((App)Application.Current)._serviceProvider.GetRequiredService<T>();
```

This is the one and only place native pages reach into DI from outside constructor injection —
don't scatter `Application.Current` casts elsewhere; route everything through this.

- [ ] **Step 3: `ShellWindow.xaml` — add the native `Frame` alongside `UiHost`**

`NavigationView`'s content area currently has exactly one child, `UiHost`. Wrap both in a `Grid`:

```xml
<controls:NavigationView ...>
    <Grid>
        <controls:WebView2 x:Name="UiHost" />
        <Frame x:Name="NativeHost" Visibility="Collapsed" />
    </Grid>
</controls:NavigationView>
```

- [ ] **Step 4: `ShellWindow.xaml.cs` — branch `Navigate()`**

```csharp
private void Navigate(NavDestination destination)
{
    if (destination.NativePageType != null)
    {
        NativeHost.Navigate(destination.NativePageType);
        NativeHost.Visibility = Visibility.Visible;
        UiHost.Visibility = Visibility.Collapsed;
        return;
    }

    NativeHost.Visibility = Visibility.Collapsed;
    UiHost.Visibility = Visibility.Visible;

    if (UiHost.CoreWebView2 == null) return;
    var baseUrl = App.LegacyUiUrl;
    if (baseUrl == null)
    {
        Log.Error("[Shell] Legacy UI host is not running; cannot navigate");
        return;
    }
    UiHost.CoreWebView2.Navigate($"{baseUrl}{destination.Route}?shell=native");
}
```

`OnFirstActivated`'s call to `Navigate(_pendingDestination)` needs no change — it already goes
through this same method, so landing on Dashboard at startup shows the native page immediately.

Also check `OnBackRequested`:
```csharp
private void OnBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
{
    if (NativeHost.Visibility == Visibility.Visible && NativeHost.CanGoBack) { NativeHost.GoBack(); return; }
    if (UiHost.CoreWebView2?.CanGoBack == true) UiHost.CoreWebView2.GoBack();
}
```
Dashboard is the root/home destination (`Nav.SelectedItem` defaults to it), so `NativeHost.CanGoBack`
will normally be false there — this branch mostly future-proofs later native pages that might
navigate within themselves, not something Dashboard itself exercises today. Include it anyway
since leaving `OnBackRequested` WebView2-only while `NativeHost` exists is a latent bug for the
very next page this plan's plumbing gets reused for.

- [ ] **Step 5: Commit** (after Task 5 makes the whole thing compile — see Step 1's note)

```bash
git add AviatesAirTracker/Navigation/NavDestinations.cs AviatesAirTracker/App.xaml.cs AviatesAirTracker/ShellWindow.xaml AviatesAirTracker/ShellWindow.xaml.cs
git commit -m "feat(shell): add native-page hosting alongside the WebView2 bridge"
```

---

## Task 5: `DashboardPage` — layout and static/simple-binding content

**Files:**
- New: `AviatesAirTracker/Pages/DashboardPage.xaml`
- New: `AviatesAirTracker/Pages/DashboardPage.xaml.cs`

Reminder from Phase 2: **any `<Style>` element, or any new entry added to `Theme/Tokens.xaml`,
silently crashes `XamlCompiler.exe`** on this SDK (Windows App SDK 1.8.250906003) — confirmed
repeatedly across Phase 2's Tasks 5–6. Style everything on this page via direct property values
in XAML or code-behind, the same way `ShellWindow`'s nav-item styling was done. If you need a new
color not already in `Theme/Tokens.xaml`, hardcode it inline (`Color.FromArgb(...)` in
code-behind, or a literal `#RRGGBB` in XAML) rather than adding a token — adding one will crash
the build the same way it did in Phase 2 Task 6.

- [ ] **Step 1: Page skeleton + DI resolution**

```csharp
public sealed partial class DashboardPage : Page
{
    private readonly DashboardViewModel _viewModel;

    public DashboardPage()
    {
        InitializeComponent();
        _viewModel = App.GetService<DashboardViewModel>();
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.RefreshAsync();
    }
}
```

- [ ] **Step 2: Hero card** — static content, reproducing `app.css`'s `.hero-card` (gradient
      `linear-gradient(130deg, #1B3F8B 0%, #0F172A 48%, #1A1040 100%)`, `.hero-title` in the
      `DisplayFontBold` resource already bundled from Phase 2, the "View Flights" CTA button —
      inert for now, no destination to navigate to). Skip the animated SVG art entirely per the
      spec (static art or omit — your call on which reads better, but no `@keyframes` equivalent
      in this task).

- [ ] **Step 3: Stat strip** — 3 cards bound to `TotalFlightsText`/`TotalHoursText`/`AvgLandingScoreText`.

- [ ] **Step 4: Recent Flights** — `ItemsControl` (or `ListView` if item-click behavior seems
      worth adding, though nothing in the current Blazor version has any) bound to
      `RecentFlights`, showing route (`DepartureICAO` → `ArrivalICAO`), `AircraftType`,
      `PilotName`, formatted `CreatedAt`. Empty-state text ("No recent flights") when the
      collection is empty — a genuinely reachable state for a new pilot, unlike Assignments/Ops
      Team below which always have their static content.

- [ ] **Step 5: "Your Assignments" table** — static content, the exact same 3 rows
      `Dashboard.razor` has today (same names/roles/descriptions) — a literal transcription, not
      a rewrite. Read `Dashboard.razor`'s actual markup for the exact current text before typing
      this out from memory.

- [ ] **Step 6: Operations Team panel** — same treatment, static, same 3 people as today,
      transcribed from the actual current markup.

- [ ] **Step 7: Quick Stats panel** — 4 tiles: Total Hours and Flights bound to
      `TotalHoursText`/`TotalFlightsText` (real, same source as the stat strip — this is
      intentional duplication, matching today's design), On-Time % as static text (`"92%"`,
      unchanged), Landing Avg bound to `AvgLandingScoreText` (real now, same value the stat-strip
      card shows — also intentional duplication, per the spec).

- [ ] **Step 8: Build** — `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`. This
      is the point where Task 4's `NavDestinations.cs` line referencing `Pages.DashboardPage`
      finally compiles — if it doesn't, fix whichever of the two tasks has the mismatch before
      moving on.

- [ ] **Step 9: Commit**

```bash
git add AviatesAirTracker/Pages/
git commit -m "feat(dashboard): native DashboardPage layout with real and static content"
```

---

## Task 6: Custom rank-progress ring + monthly-activity bar chart

**Files:**
- Modify: `AviatesAirTracker/Pages/DashboardPage.xaml`
- Modify: `AviatesAirTracker/Pages/DashboardPage.xaml.cs`

The two pieces flagged in the spec as having no direct WinUI equivalent.

- [ ] **Step 1: Rank-progress ring**

`app.css`'s `.pilot-ring` is a 100×100px SVG circle, `stroke-width: 8`, `r=45` (circumference
`2π×45 ≈ 282.7`), animated via `stroke-dashoffset`. Reproduce with a `Path`
containing two overlapping arcs (a full-circle track in a muted color, and a partial arc in
`AccentBrush` whose sweep angle is `RankProgress / 100 * 360` degrees) — computed in code-behind
since `ArcSegment`'s `Point`/`IsLargeArc` need trig, not a XAML-only binding. A small helper:

```csharp
private static PathFigure BuildRingArc(double centerX, double centerY, double radius, double percent)
{
    var angle = percent / 100.0 * 360.0;
    var startPoint = new Windows.Foundation.Point(centerX, centerY - radius);
    var radians = (angle - 90) * Math.PI / 180.0;
    var endPoint = new Windows.Foundation.Point(
        centerX + radius * Math.Cos(radians),
        centerY + radius * Math.Sin(radians));

    return new PathFigure
    {
        StartPoint = startPoint,
        Segments = { new ArcSegment
        {
            Point = endPoint,
            Size = new Windows.Foundation.Size(radius, radius),
            IsLargeArc = angle > 180,
            SweepDirection = SweepDirection.Clockwise,
        }},
    };
}
```

Recompute and reassign the `Path.Data` whenever `RankProgress` changes (subscribe to
`DashboardViewModel.PropertyChanged`, or simplest: recompute once in `Loaded` after
`RefreshAsync()` completes, since this page doesn't need live updates mid-session the way the
20Hz telemetry pages do). A centered `TextBlock` shows `{RankProgress:F0}%`.

- [ ] **Step 2: Monthly activity bar chart**

`ItemsControl` bound to `MonthlyActivity`, `ItemsPanel` set to a horizontal `StackPanel`. Each
item's bar height scales relative to the max `FlightCount` in the current set (not a fixed pixel
value like today's hardcoded CSS) — compute `MaxActivity` as a small derived value (either a
`[ObservableProperty]` on `DashboardViewModel` recomputed in `RefreshAsync()`, or a converter) so
a pilot with very few flights doesn't get a chart that's visually all-empty-except-one-maxed-bar
in a confusing way. Use a simple linear scale, tallest bar at a fixed max height (e.g. 48px per
`app.css`'s apparent bar heights), others proportional; zero-flight months render as a minimal
visible sliver, not literally 0px, so the axis stays legible.

- [ ] **Step 3: Build and run**

`dotnet build AviatesAirTracker.sln -c Debug`. Check `tasklist` for MSFS before launching, then
launch and confirm no crash. **Be conservative about touching the user's desktop** — build and a
brief launch-then-close to check the Serilog log for a clean startup and no unhandled exceptions
is sufficient; don't attempt computer-use/screenshot verification in this task, that's Task 8.

- [ ] **Step 4: Commit**

```bash
git add AviatesAirTracker/Pages/
git commit -m "feat(dashboard): custom rank-progress ring and monthly-activity chart"
```

---

## Task 7: End-to-end shell verification

**Files:** none (verification only, but may produce small fixes if something's broken)

- [ ] **Step 1: Confirm the automated suite** — `dotnet test`, expect all pre-existing tests plus
      Tasks 1–2's new `PilotStatsLogicTests`, all passing.

- [ ] **Step 2: Confirm a clean build** — `dotnet build AviatesAirTracker.sln -c Debug`, 0 errors,
      no new warnings beyond the established 10-warning baseline.

- [ ] **Step 3: Functional check — the actual regression risk of this task**

Every nav-rail destination routes through the same `Navigate()` method Task 4 modified. Check
`tasklist` for MSFS first, then launch (briefly, closing afterward — same desktop-conservatism
note as Task 6) and confirm via the Serilog log and/or a quick look if the user is available:
clicking Dashboard shows the native page with no WebView2 flash, and clicking every *other* nav
item still shows the Blazor-rendered page exactly as it did before this plan started. This is
the one thing that could silently break every other page in the app if `Navigate()`'s branching
logic has a bug — worth being thorough here specifically, not a place to rubber-stamp.

- [ ] **Step 4: Visual comparison, if the user is available to look**

Native Dashboard vs. the current Blazor Dashboard (reachable by navigating the WebView2 to `/`
without `?shell=native`, or a screenshot of it from before this plan started) — layout, colors,
fonts, and specifically that Assignments/Operations Team/On-Time % look identical to today (they
were meant to, being a faithful static port) while Recent Flights/rank ring/activity chart show
real data. If the user isn't available, defer this explicitly rather than skip it silently, same
as this project's established practice throughout Phases 0–2.

- [ ] **Step 5: Record results and commit**

Append a results section to this plan doc (pass/fail per step, any deviations found) and to the
design spec if anything discovered here changes a decision recorded there. Commit both together:

```bash
git add -f docs/superpowers/plans/2026-08-23-winui3-phase3-dashboard-native.md docs/superpowers/specs/2026-08-23-winui3-phase3-dashboard-native-design.md
git commit -m "docs(plan): record Dashboard native-page verification results"
```

---

## Definition of done

- `dotnet test` passes, including the new `PilotStatsLogicTests`
- The rank-progress bug is fixed everywhere it's used (`PilotHubViewModel` and the new
  `DashboardViewModel`), from one shared, tested source
- Clicking Dashboard in the nav rail shows a native WinUI page with real recent-flights/rank/
  monthly-activity data; every other nav item is provably unaffected
- Assignments, Operations Team, and On-Time % look identical to today's Blazor Dashboard —
  faithful static ports, not new empty features
- No other `.razor` file touched; `Dashboard.razor` itself left in place, just unreferenced by
  the native shell
- The deferred backend-rank-integration and other follow-up items from the design spec are
  **not** attempted in this plan — check the plan didn't scope-creep into them
