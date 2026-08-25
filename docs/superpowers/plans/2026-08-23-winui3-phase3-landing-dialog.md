# WinUI 3 Migration — Phase 3, fourth conversion: native landing-result dialog — Implementation Plan

**Date:** 2026-08-23
**Spec:** `docs/superpowers/specs/2026-08-23-winui3-phase3-landing-dialog-design.md` — read it
first for the "why," especially the correction from a `/landings` page to this dialog and the
`LandingAnalysisViewModel`-avoidance decision. This plan only covers the "what" and "how."
**Branch:** `claude/blazor-winui3-migration-a10c79`

**For agentic workers: REQUIRED SUB-SKILL:** use `superpowers:subagent-driven-development`
(recommended) or `superpowers:executing-plans` to implement this plan task-by-task.

## Before you start

- No `<Style>` element, no new `Theme/Tokens.xaml` entry, anywhere in this project's XAML —
  silently crashes `XamlCompiler.exe`. Hardcode colors inline or set them in code-behind.
- No literal `--` inside any XAML comment (invalid XML, same silent crash) — this has recurred in
  every single task of both prior plans (Pilot Hub, Flights) despite being documented every time.
  Check your own new comments with `grep -n -- "--" <file>` (excluding intentional `──` header
  decorations) before considering any task's build final, every single time.
- Anything bound to XAML needing per-item computed values needs a plain mutable class with
  `{ get; set; }` properties — not an init-only `record` (`CS8852`).
- Anything whose exact shape matters (a pill, a rounded rect) should be a `Border`, not a
  `<Button>` — this SDK has no `<Style>` to guarantee `Button` respects `CornerRadius`.
- `ContentDialog` is already an established pattern (Flights' delete/force-remove dialogs) — read
  `AviatesAirTracker/Pages/FlightsPage.xaml.cs`'s `OnDeleteClicked` method for the working
  `ContentDialog` construction idiom this plan reuses.
- **This is the first shell-level (not page-level) native UI in this migration.** `ShellWindow` is
  a `Window`, not a `Page` — it does **not** expose a `XamlRoot` property the way a `Page` does.
  `ContentDialog.XamlRoot` needs to be set from some element actually in the visual tree —
  `ShellWindow.xaml`'s root element is `x:Name="RootGrid"`, so use `RootGrid.XamlRoot`, not
  `this.XamlRoot` (which won't compile) or any assumption borrowed from how `Page`-hosted dialogs
  set it.
- Do **not** use `LandingAnalysisViewModel` anywhere in this plan — it's dead/stale code per the
  spec's explicit finding. Build from `LandingReportBuilder.Build(...)` directly.

## Task 1: `LandingResultDialog` — layout, params grid, footer, photo

**Files:**
- New: `AviatesAirTracker/Dialogs/LandingResultDialog.cs`

- [ ] **Step 1: Read the source material first**

Read `AviatesAirTracker.LegacyWeb/Shared/LandingResultModal.razor` in full (already read once
during spec research, but read it again yourself — this task needs exact text/thresholds) and
`AviatesAirTracker/Core/Analytics/LandingReport.cs` (the `LandingReport`/`LandingReportFactor`
classes this dialog binds to — every property this task needs already exists there, don't guess
field names).

- [ ] **Step 2: Static builder method skeleton**

```csharp
namespace AviatesAirTracker.Dialogs;

public static class LandingResultDialog
{
    public static ContentDialog Build(XamlRoot xamlRoot, LandingReport report)
    {
        var content = new StackPanel { Spacing = 16, Width = 460 }; // or whatever width reads right, your call

        // Step 3-6 add to `content` here

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Content = content,
            CloseButtonText = "Close Report",
        };
        return dialog;
    }
}
```

- [ ] **Step 3: Header**

`report.AirportIcao`/`Runway`, `FlightNumber`/`Route`/`AircraftType` (only shown if non-empty —
match the original's conditional exactly), a formatted Zulu timestamp
(`report.TimestampUtc.ToString("HH:mm")` + "Z"). The border/accent color at the top of the header
comes from `report.GradeColor` — a thin colored top border on the dialog's content, not a full
background tint.

- [ ] **Step 4: Touchdown parameters grid**

6 cells (VS, IAS, pitch, bank, headwind, crosswind), a `UniformGrid`/`Grid` with matching column
count. Two warning thresholds from the original: `VerticalSpeedFpm > 600` and `BankDeg > 3` each
give their cell a warning-tinted background (check the modal's CSS `.param-warn` rule for the
exact color, or use a reasonable red-tinted background consistent with this project's established
warning-color convention — `#EF4444` at low opacity, matching every other "warning/danger" tint
used across Dashboard/PilotHub/Flights).

- [ ] **Step 5: Footer stats**

Rollout (only if `> 10`), threshold distance (only if `> 10`), wind (only if `WindSpeedKts > 0`,
formatted as `"{direction}°/{speed}"`), fuel (always shown) — match the original's conditional
visibility exactly, don't show a stat that would read as "0 ft rollout" when the original
wouldn't have shown it at all.

- [ ] **Step 6: Aircraft photo**

`report.AircraftImagePath` (nullable string, a `wwwroot`-relative path like
`"images/fleet-a320neo.webp"` — only 3 aircraft types ever have one, per
`LandingReportBuilder`'s own small lookup table). Load it the same file-path-from-
`AppContext.BaseDirectory` way `DashboardPage.xaml.cs`'s `hero.webp` loading already established
(`System.IO.Path.Combine(AppContext.BaseDirectory, "wwwroot", report.AircraftImagePath)`, check
`File.Exists` before constructing a `BitmapImage`). `null` or a missing file → no photo, the
dialog just shows its background gradient — no crash, no placeholder, matching the original's own
`@if (!string.IsNullOrEmpty(...))` guard.

- [ ] **Step 7: Build**

`dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`. This doesn't need to be
triggerable yet (Task 3 wires the actual trigger) — just confirm `LandingResultDialog.cs` compiles
standalone. Check for the `--`-in-comment trap.

- [ ] **Step 8: Commit**

```bash
git add AviatesAirTracker/Dialogs/LandingResultDialog.cs
git commit -m "feat(landing-dialog): native landing-result dialog -- header, params grid, footer, photo"
```

---

## Task 2: Ring, grade badge, butter meter, quality flags, score breakdown

**Files:**
- Modify: `AviatesAirTracker/Dialogs/LandingResultDialog.cs`

- [ ] **Step 1: The score ring**

`r=80`, matching `Circumference = 2π×80 ≈ 502.65` (the modal's own constant, confirm against the
real file rather than trusting this plan's arithmetic blindly). Reuse
`DashboardPage.BuildRingArc(centerX, centerY, radius, percent)` (already `internal static`,
already reused once by `PilotHubPage.xaml.cs` — this is the third consumer) with these
radius/stroke-width values (check the modal's `stroke-width` — it may differ from both Dashboard's
`8` and Pilot Hub's `14`, verify against the actual CSS/markup rather than guessing). `percent`
here is simply `report.Score` (already 0-100) — no threshold/rank-progress math needed, this ring
is a direct score-out-of-100 display, simpler than either prior ring's percent computation.
`Stroke` is `report.GradeColor` directly (already a hex color string — parse it into a
`SolidColorBrush` via `Color.FromArgb` after stripping the `#`, or find whichever hex-string-to-
`Brush` helper this codebase already has, don't write a third one if one already exists).

Center text: `report.Score` + "/100" (two `TextBlock`s or one formatted string, match the
original's visual split between the big number and the smaller "/100").

- [ ] **Step 2: Grade badge + butter meter**

Grade badge: `report.Grade`, colored/bordered with `report.GradeColor`, background a low-opacity
tint of the same color (the original's `GradeBg` helper — reproduce its exact opacity/color
mapping, or just use `report.GradeColor` at ~10% opacity directly, which is what `GradeBg`
actually computes). Butter meter: `report.ButterRating` (the headline, e.g. "PURE BUTTER"),
`report.ButterBlurb` (the joke line), and the VS readout — all colored via `report.ButterColor`.

- [ ] **Step 3: Quality flags**

Up to 4 flag chips: Stable Approach (✓/✗ based on `report.StableApproach`), Flare Detected (✓/–
based on `report.FlareDetected`), Bounce count (✓ "No Bounce" if `BounceCount == 0`, else "N
Bounce(s)" with a warning color), and conditionally either "Personal Best" (★, if
`report.IsPersonalBest`) or a rank/average line ("`{RankAmongLandings}` of `{TotalLandings}` ·
avg `{AverageScore}`", only if `TotalLandings > 1` and not a personal best) — match the original's
exact `@if`/`else if` branching, don't show both or neither.

- [ ] **Step 4: Score breakdown bars**

6 factors from `report.Factors` (already computed: `Label`, `Score`, `Max`, `Pct`, `Color`) — a
`Border`-track + `Border`-fill-width row per factor, same technique Pilot Hub's rank-progress bar
already established (`Width` computed once, no live transition). Label on the left, the bar in
the middle, `"{Score}/{Max}"` on the right in the factor's own `Color`.

- [ ] **Step 5: Wire it all into `Build()`**, add everything from Tasks 1-2 to the `content`
      `StackPanel` in the right visual order (header → hero row [ring + grade/butter/flags column]
      → params grid → breakdown bars → footer, matching the original's top-to-bottom order).

- [ ] **Step 6: Build, check for the `--` trap, commit**

```bash
git add AviatesAirTracker/Dialogs/LandingResultDialog.cs
git commit -m "feat(landing-dialog): ring, grade badge, butter meter, and score breakdown"
```

---

## Task 3: Shell wiring, auto-dismiss, final verification

**Files:**
- Modify: `AviatesAirTracker/ShellWindow.xaml.cs`

- [ ] **Step 1: New constructor dependencies**

Add `ILandingRepository landingRepository` and `AlertService alertService` to `ShellWindow`'s
constructor (`IFlightRepository` is already injected — don't add a second one). Store both as
private fields, same pattern as every other injected dependency already in this constructor.

- [ ] **Step 2: Subscribe and handle**

In the constructor, alongside the existing `_sessionManager.SessionStateChanged`/
`_messagingService.UnreadCountChanged` subscriptions:

```csharp
_alertService.LandingResultReady += OnLandingResultReady;
```

```csharp
private async void OnLandingResultReady(object? sender, LandingResult result)
{
    FlightRecord? flight = null;
    List<LandingResult>? history = null;
    try
    {
        history = await _landingRepository.GetAllAsync();
        if (Guid.TryParse(result.FlightId, out var fid))
            flight = (await _flightRepository.GetAllAsync()).FirstOrDefault(f => f.Id == fid);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "[Shell] Landing report context lookup failed");
        // Deliberately fall through -- the original modal's own comment says failures here
        // must never block the popup; still show the dialog with flight: null.
    }

    var report = LandingReportBuilder.Build(result, flight, history);
    var dialog = LandingResultDialog.Build(RootGrid.XamlRoot, report);

    // Step 3 adds the auto-dismiss timer around this ShowAsync call
    await dialog.ShowAsync();
}
```

Double-check `LandingResult`'s actual namespace/using and `LandingReportBuilder`'s exact static
method signature against the real files before writing this — the snippet above is a close
paraphrase, not guaranteed byte-exact.

- [ ] **Step 3: 60-second auto-dismiss**

The original modal auto-dismisses after 60s if the pilot hasn't already closed it. Wrap the
`ShowAsync()` call with a timer that calls `dialog.Hide()`:

```csharp
using var autoClose = new System.Threading.Timer(_ =>
{
    DispatcherQueue.TryEnqueue(() => dialog.Hide());
}, null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);

await dialog.ShowAsync();
```

`System.Threading.Timer`'s callback runs on a thread-pool thread, not the UI thread — `Hide()`
must be marshaled back via `DispatcherQueue.TryEnqueue` (`ShellWindow` is a `Window`, it has a
`DispatcherQueue` property) or it will throw/no-op silently. Verify this actually works rather
than assuming; if `dialog.Hide()` while `ShowAsync()` is still awaiting doesn't behave as
expected (check WinUI's `ContentDialog.Hide()` documentation/behavior if uncertain), that's a
real finding worth reporting, not something to silently paper over.

- [ ] **Step 4: Build and startup check**

`dotnet build AviatesAirTracker.sln -c Debug` — 0 errors, no new warnings.
`dotnet test tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj` — full suite passing
(this plan adds no new tests; everything here is direct UI/wiring code, not a TDD candidate).
Check `tasklist` for MSFS first, launch briefly, confirm clean Serilog startup (the new
constructor dependencies resolving correctly is the main regression risk here — if DI can't
resolve `ILandingRepository`/`AlertService` for `ShellWindow`, the app won't start at all, which
would show up immediately), close cleanly. **Actually triggering a real landing to see the dialog
requires MSFS running and a live flight — don't force this. If it's not available, defer it
explicitly** ("deferred — needs a live MSFS session, next time one happens"), same as this
project's established practice for every prior live-sim-gated functional check.

- [ ] **Step 5: Record results and commit**

Append a results section to this plan doc (pass/fail per step, especially whether the auto-dismiss
timer's dispatcher-marshaling behaved as expected) and commit:

```bash
git add -f docs/superpowers/plans/2026-08-23-winui3-phase3-landing-dialog.md
git add AviatesAirTracker/ShellWindow.xaml.cs
git commit -m "feat(landing-dialog): wire the native dialog to AlertService.LandingResultReady"
```

---

## Definition of done

- Build clean, full test suite passing
- `ShellWindow` starts cleanly with the two new constructor dependencies resolved
- The dialog is code-complete and visually matches the original modal, modulo the acknowledged
  animation gap
- Functional confirmation via a real landing is explicitly deferred if MSFS isn't available, not
  skipped silently
- `LandingResultModal.razor`, `MainLayout.razor`, and `LandingAnalysisViewModel` all untouched
- No `/landings` page built; Pilot Hub's two dead links remain dead, explicitly out of scope

---

## Task 3 results (2026-08-24)

Implemented against `fdf0b5fa37b054855fddfd1b85636d734bd04d89` ("fix(landing-dialog): correct
ring size and header sub-line color"), confirmed via `git rev-parse HEAD` before any edits. Only
`AviatesAirTracker/ShellWindow.xaml.cs` was modified — `LandingResultModal.razor`,
`MainLayout.razor`, and `LandingAnalysisViewModel` are untouched (`git status --short` confirms a
single changed file).

- [x] **Step 1: New constructor dependencies** — `ILandingRepository landingRepository` and
  `AlertService alertService` added to `ShellWindow`'s constructor, stored as
  `_landingRepository`/`_alertService`, same pattern as every other injected field. Both types
  were already `AddSingleton<>` in `App.xaml.cs` (lines 359/370), so no DI registration changes
  were needed.
- [x] **Step 2: Subscribe and handle** — `_alertService.LandingResultReady += OnLandingResultReady;`
  added alongside the existing subscriptions in the constructor, and unsubscribed in `OnClosed`
  (the plan's snippet didn't show this but every other event in the constructor is unsubscribed
  there, so `OnLandingResultReady` follows the same pattern for consistency). `OnLandingResultReady`
  matches the plan's snippet closely, with types verified directly against source rather than
  trusted: `LandingResult` is `AviatesAirTracker.Models.LandingResult` (`FlightId` is `string`);
  `FlightRecord.Id` is `Guid`; `ILandingRepository.GetAllAsync()` returns `Task<List<LandingResult>>`;
  `LandingReportBuilder.Build(LandingResult r, FlightRecord? flight = null,
  IReadOnlyList<LandingResult>? history = null)` — parameter order matches the plan's
  `Build(result, flight, history)` call exactly. The try/catch around both repository calls logs
  a warning and falls through with `flight: null`/`history: null` on failure, matching the
  original modal's "never block the popup" behavior.
- [x] **Step 3: 60-second auto-dismiss** — implemented per the plan's sketch, using
  `System.Threading.Timer` (fully qualified, matching the existing convention in
  `MessagingService.cs`/`OtherPilotsService.cs`) with the callback marshaling `dialog.Hide()`
  through `DispatcherQueue.TryEnqueue`. **Verified rather than assumed correct**: WinUI's
  `ContentDialog.Hide()` is documented to be safe to call while `ShowAsync()` is still pending —
  it forces that await to complete with `ContentDialogResult.None`, the same path a user clicking
  the close button takes. The timer callback runs on a thread-pool thread, so `Hide()` (a UI-tree
  operation) cannot be called directly from it — `DispatcherQueue.TryEnqueue` is required and is
  available because `ShellWindow` is a `Window`. The `using var autoClose` disposes the timer the
  moment `ShowAsync()` returns for any reason (user closed it manually, or the timer already fired),
  so there's no risk of a stray `Hide()` firing against an already-dismissed dialog racing with a
  new one. No doubts remain about this implementation's correctness — it compiled cleanly and the
  pattern is the standard documented one for WinUI/UWP auto-dismissing `ContentDialog`s.
- [x] **Step 4: Build and startup check**
  - `dotnet build AviatesAirTracker.sln -c Debug`: **Build succeeded, 0 errors.** 15 warnings, all
    pre-existing (unused fields in `LandingAnalyzer`/`FlightSessionManager`/`FlightPhaseDetector`,
    `MVVMTK0034` in `MainViewModel`) — none introduced by this change.
  - `dotnet test tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj`: **39/39 passed**,
    0 failed, 0 skipped. No new tests added, per the plan.
  - Startup check: confirmed no MSFS process via `tasklist` first. Launched the built
    `AviatesAirTracker.exe` directly (not via `build.ps1 -Run`), waited 8s, and inspected the
    Serilog output at `%AppData%\AviatesAirTracker\logs\aviates_20260824_001.log`. Found exactly
    one `[Shell]`-tagged line (`[Shell] Flights nav badge: AttentionDotInfoBadgeStyle`), which only
    logs from inside `ShellWindow`'s constructor after all fields — including the two new ones —
    are assigned, so its presence is direct evidence the DI container resolved
    `ILandingRepository`/`AlertService` and constructed `ShellWindow` successfully. No unhandled
    exceptions and no other `[Shell]` errors. The only exceptions in the log window are repeated
    `SharpDX.DirectInput` `DIERR_DEVICENOTREG` errors from `PttInputService`'s joystick poll loop —
    pre-existing, unrelated to this change (this sandbox has no joystick hardware), and not
    `[Shell]`-tagged. Closed the app via `CloseMainWindow()`; the log shows a full graceful
    shutdown sequence ("All services disposed in 20 ms — exiting") and the process was confirmed
    gone from `tasklist` afterward. No `request_access`/screenshot/computer-use verification was
    attempted, per the instructions.
  - **Explicitly deferred, not skipped**: actually triggering `LandingResultReady` via a real
    landing (needs MSFS running plus a live flight to touchdown) was not attempted. The app also
    ships a `--debug-landing` dev flag (`App.xaml.cs`, DEBUG-only) that fires a synthetic
    `LandingResult` 12s after startup specifically for testing this dialog without MSFS — noted
    here for whoever picks up the deferred check next, but not used in this task since verifying
    the dialog's actual on-screen appearance still requires screenshot/computer-use, which this
    task's instructions explicitly excluded. **Deferred — needs a live MSFS session (or a
    `--debug-landing` run with screenshot verification enabled), next time either is available.**
- [x] **Step 5: Record results and commit** — this section, plus the commit below.

**Definition-of-done check**: build clean and full suite passing (yes); `ShellWindow` starts
cleanly with both new constructor dependencies resolved (yes, evidenced by the startup log); the
dialog is code-complete (yes, Tasks 1-2 plus this task's wiring); real-landing functional
confirmation explicitly deferred, not skipped (yes, see above); `LandingResultModal.razor`,
`MainLayout.razor`, `LandingAnalysisViewModel` untouched (yes, confirmed via `git status`); no
`/landings` page built (yes, out of scope, not touched).

## Confirmed by the user (2026-08-23), via `--debug-landing`

The orchestrating session found and used the `--debug-landing` dev flag noted above to close the
deferred functional-verification gap without needing a live MSFS session. The user ran the app
with the flag, saw the synthetic landing trigger the native dialog, and confirmed it renders
correctly — closing the one item this task's own verification had to leave open.

**Phase 3's fourth conversion (the landing-result dialog) is done.**
