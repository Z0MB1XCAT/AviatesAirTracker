# WinUI 3 Migration — Phase 3, third page: native Flights — Implementation Plan

**Date:** 2026-08-23
**Spec:** `docs/superpowers/specs/2026-08-23-winui3-phase3-flights-native-design.md` — read it
first for the "why"; this plan only covers the "what" and "how."
**Branch:** `claude/blazor-winui3-migration-a10c79`

**For agentic workers: REQUIRED SUB-SKILL:** use `superpowers:subagent-driven-development`
(recommended) or `superpowers:executing-plans` to implement this plan task-by-task.

## Before you start

**Read `AviatesAirTracker/Pages/PilotHubPage.xaml`/`.xaml.cs` first** (the most recently completed
page) for the established conventions this plan reuses without re-explaining per task:
- No `<Style>` element, no new `Theme/Tokens.xaml` entry, anywhere in this project's XAML —
  silently crashes `XamlCompiler.exe`. Hardcode colors inline or set them in code-behind.
- No literal `--` inside any XAML comment (invalid XML, same silent crash) — this has recurred in
  every single task of the Pilot Hub plan despite being documented each time. Check your own new
  comments with `grep -n -- "--" AviatesAirTracker/Pages/FlightsPage.xaml` (excluding intentional
  `──` header decorations) before considering any task's build final.
- Anything bound to XAML needing per-item computed values (color, formatted text) needs a plain
  mutable class with `{ get; set; }` properties — not an init-only `record` (`CS8852` in the
  generated `XamlTypeInfo.g.cs`).
- Anything whose exact shape matters (a pill, a rounded rect) should be a `Border`, not a
  `<Button>` — this SDK has no `<Style>` to guarantee `Button` respects `CornerRadius`.
- The native-vs-WebView2 shell dispatch mechanism (`ShellWindow`'s `NativeHost` `Frame`,
  `NavDestination.NativePageType`, `App.GetService<T>()`) already exists — this page only plugs
  into it (Task 5), not rebuilds it.
- **New for this plan**: `AviatesAirTracker.LegacyWeb/Pages/Flights.razor`'s `GradeChipClass`
  already correctly uses `chip-amber` (not the buggy `chip-yellow` `PilotHubViewModel` had) — this
  page has no equivalent color bug to fix. Don't go looking for one; there isn't one.

## Task 1: `FlightsViewModel` + pure tab-filter logic, TDD

**Files:**
- New: `AviatesAirTracker/Services/FlightsFilterLogic.cs`
- New test: `tests/AviatesAirTracker.Tests/FlightsFilterLogicTests.cs`
- New: `AviatesAirTracker/ViewModels/FlightsViewModel.cs`
- Modify: `AviatesAirTracker/App.xaml.cs` (DI registration)
- Modify: `tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj`

Unlike Dashboard/PilotHub, **no `FlightsViewModel` exists yet** — `Flights.razor`'s logic all
lives in its own `@code` block. This task builds the ViewModel from scratch, reproducing that
`@code` block's exact behavior (reproduced verbatim below so you don't need to re-read the whole
`.razor` file to get every detail right) — this is a port, not a redesign.

- [ ] **Step 1: Pure tab-filter logic (TDD)**

```csharp
namespace AviatesAirTracker.Services;

public static class FlightsFilterLogic
{
    public static IEnumerable<FlightRecord> FilterByTab(IEnumerable<FlightRecord> flights, string tab) => tab switch
    {
        "active"    => flights.Where(f => f.Status == FlightStatus.InProgress),
        "completed" => flights.Where(f => f.Status == FlightStatus.Completed),
        _           => flights
    };
}
```

Write `tests/AviatesAirTracker.Tests/FlightsFilterLogicTests.cs` first: cases for `"active"`
returning only `InProgress` flights, `"completed"` returning only `Completed`, and an unknown/
`"all"` tab returning everything unfiltered. Add the `Compile Include` link to the test csproj
(`AviatesAirTracker/Services/FlightsFilterLogic.cs`), confirm tests fail then pass, same TDD
rhythm as every prior pure-logic addition in this migration.

- [ ] **Step 2: `FlightsViewModel`**

```csharp
public partial class FlightsViewModel : ObservableObject
{
    private readonly IFlightRepository _flightRepo;
    private readonly IFlightDeletionRepository _deletionRepo;
    private readonly FlightSessionManager _session;

    [ObservableProperty] private double _totalNm;
    [ObservableProperty] private bool _isLoading;

    public ObservableCollection<FlightRecord> Flights { get; } = [];
    public ObservableCollection<FlightDeletionRequest> PendingRequests { get; } = [];

    public FlightsViewModel(IFlightRepository flightRepo, IFlightDeletionRepository deletionRepo, FlightSessionManager session)
    {
        _flightRepo = flightRepo;
        _deletionRepo = deletionRepo;
        _session = session;
        session.FlightCompleted += (_, _) => _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var flights = await _flightRepo.GetAllAsync();
            Flights.Clear();
            foreach (var f in flights) Flights.Add(f);

            TotalNm = flights.Where(f => f.Status == FlightStatus.Completed).Sum(f => f.ActualDistanceNm);

            var pending = await _deletionRepo.GetAllAsync();
            PendingRequests.Clear();
            foreach (var p in pending) PendingRequests.Add(p);
        }
        finally { IsLoading = false; }
    }
}
```

`Flights.razor`'s `Session.FlightCompleted` subscription has no explicit unsubscribe in a
`Dispose()` today because Blazor components dispose per-navigation; a `[RelayCommand]`-bearing
singleton ViewModel living for the app's lifetime doesn't need one either (same lifetime as
`PilotHubViewModel`'s identical subscription, which also never unsubscribes) — don't add
disposal machinery that doesn't exist elsewhere in this codebase for the same pattern.

- [ ] **Step 3: The four real command methods — reproduce this exact logic, don't improvise**

```csharp
[RelayCommand]
public async Task SubmitDeletionRequestAsync((FlightRecord Flight, string Reason) request)
{
    if (string.IsNullOrWhiteSpace(request.Reason)) return; // caller (the page) surfaces the validation message

    var deletionRequest = new FlightDeletionRequest
    {
        FlightId     = request.Flight.Id,
        FlightNumber = request.Flight.FlightNumber,
        Route        = FlightsDisplay.RouteLabel(request.Flight), // see Task 3 for FlightsDisplay
        Reason       = request.Reason.Trim(),
    };
    await _deletionRepo.SaveAsync(deletionRequest);
    var pending = await _deletionRepo.GetAllAsync();
    PendingRequests.Clear();
    foreach (var p in pending) PendingRequests.Add(p);
}

[RelayCommand]
public async Task ApproveDeletionAsync(FlightDeletionRequest req)
{
    await _deletionRepo.ApproveAsync(req.Id);
    await _flightRepo.DeleteAsync(req.FlightId);
    await RefreshAsync();
}

[RelayCommand]
public async Task RejectDeletionAsync(FlightDeletionRequest req)
{
    await _deletionRepo.RejectAsync(req.Id);
    await RefreshAsync();
}

[RelayCommand]
public async Task ForceRemoveBrokenFlightAsync(FlightRecord target)
{
    if (_session.CurrentFlight?.Id == target.Id)
        _session.Reset();

    await _flightRepo.DeleteAsync(target.Id);
    await RefreshAsync();
}
```

Note `SubmitDeletionRequestAsync` here takes a tuple parameter since `[RelayCommand]` methods
conventionally take at most one parameter — if that reads awkwardly once you're actually wiring
it to a `ContentDialog` in Task 4, a plain non-`[RelayCommand]` `async Task` method with two
parameters, called directly from code-behind rather than via `ICommand` binding, is equally
valid; this page's modals are triggered from code-behind event handlers either way, not XAML
`Command="{Binding ...}"` bindings, so the `[RelayCommand]` wrapper is a style choice here, not a
hard requirement — your call, but be consistent across all four methods.

- [ ] **Step 4: DI registration**

Add `services.AddSingleton<FlightsViewModel>();` to `App.xaml.cs`'s `ConfigureServices`, matching
every other ViewModel's registration.

- [ ] **Step 5: Build, test, commit**

```bash
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
dotnet test tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj
git add AviatesAirTracker/Services/FlightsFilterLogic.cs AviatesAirTracker/ViewModels/FlightsViewModel.cs AviatesAirTracker/App.xaml.cs tests/AviatesAirTracker.Tests/
git commit -m "feat(flights): FlightsViewModel, ported from Flights.razor's @code block"
```

---

## Task 2: Page skeleton, header, tabs, stat strip

**Files:**
- New: `AviatesAirTracker/Pages/FlightsPage.xaml`
- New: `AviatesAirTracker/Pages/FlightsPage.xaml.cs`

- [ ] **Step 1: Skeleton + DI**

```csharp
public sealed partial class FlightsPage : Page
{
    private readonly FlightsViewModel _viewModel;
    private string _activeTab = "all";

    public FlightsPage()
    {
        InitializeComponent();
        _viewModel = App.GetService<FlightsViewModel>();
        DataContext = _viewModel;
        Loaded += async (_, _) =>
        {
            await _viewModel.RefreshAsync();
            RebuildFlightRows(); // Task 3 adds this method
        };
    }
}
```
`_activeTab` lives on the page, not the ViewModel — matching `Flights.razor`'s own `_activeTab`
field placement (UI-local selection state, not domain state); the ViewModel doesn't need to know
which tab is showing, only the page's row-rebuild logic does.

- [ ] **Step 2: Header**

Exact current title/subtitle text from `Flights.razor`'s `.page-header` — read the file, don't
guess.

- [ ] **Step 3: Tab bar**

4 tabs (All / Active / Completed / Pending Review). `Border`-based active-state (a selected
background, matching the sidebar's own `.nav-item.active` treatment already established in
`ShellWindow.xaml.cs`'s `ConfigureNavAppearance` — same color convention, not the same code).
Pending Review's tab shows a live count badge (`{_viewModel.PendingRequests.Count}` — a small
`Border`+`TextBlock` pill, same technique as the ACARS/Map "Live" pills from Phase 2). Clicking a
tab sets `_activeTab` and calls `RebuildFlightRows()` (Task 3).

- [ ] **Step 4: Stat strip**

3 cards. **Check `Flights.razor`'s actual current stat-strip markup for which icons it uses** —
don't assume they match Dashboard's 3 icons; verify. Bind: Total Flights (`Flights.Count`), the
count of `Status == FlightStatus.Completed` (compute inline in code-behind or add a small
computed property), Total Distance (`{Binding TotalNm}`, formatted).

- [ ] **Step 5: Build, `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`, commit**

This won't fully build yet if `RebuildFlightRows()` is referenced but not defined — either stub
it as a no-op for this task's build check (`private void RebuildFlightRows() { }`) with a
`// Task 3 fills this in` comment, or build without wiring the `Loaded` handler's call to it yet
and add that line in Task 3 instead — your call, whichever keeps this task's build green without
inventing fake row-building logic ahead of Task 3.

```bash
git add AviatesAirTracker/Pages/FlightsPage.xaml AviatesAirTracker/Pages/FlightsPage.xaml.cs
git commit -m "feat(flights): native page skeleton, tabs, and stat strip"
```

---

## Task 3: Flight table — rows, route flags, chips

**Files:**
- Modify: `AviatesAirTracker/Pages/FlightsPage.xaml`
- Modify: `AviatesAirTracker/Pages/FlightsPage.xaml.cs`

- [ ] **Step 1: Precomputed display-row class**

```csharp
public sealed class FlightDisplayRow
{
    public string FlightNumber { get; set; } = "";
    public string RouteLabel { get; set; } = "";
    public ImageSource? DepartureFlagSource { get; set; }
    public ImageSource? ArrivalFlagSource { get; set; }
    public string AircraftType { get; set; } = "";
    public string DurationText { get; set; } = "";
    public string GradeLetter { get; set; } = "";
    public Brush GradeChipColor { get; set; } = ...;
    public string StatusLabel { get; set; } = "";
    public Brush StatusChipColor { get; set; } = ...;
    public FlightRecord Source { get; set; } = null!; // needed by Task 4's delete/force-remove handlers to know which flight a row's buttons act on
}
```
Plain mutable class, not a `record` (`CS8852` risk, same as every prior display-row class).

- [ ] **Step 2: Route flags via `SvgImageSource`**

```csharp
private static ImageSource? LoadFlagImage(string? icao)
{
    var iso = IcaoCountry.IsoFromIcao(icao);
    if (string.IsNullOrEmpty(iso)) return null;

    var path = System.IO.Path.Combine(AppContext.BaseDirectory, "wwwroot", "images", "flags", $"{iso}.svg");
    if (!System.IO.File.Exists(path)) return null;

    return new SvgImageSource(new Uri(path));
}
```
`IcaoCountry.IsoFromIcao` already exists (`Core/Reference/IcaoCountry.cs`) — verify its actual
return type/nullability before writing the null-check above (the plan assumes `string?`, confirm
against the real method signature). `SvgImageSource` is `Microsoft.UI.Xaml.Media.Imaging` — add
the `using` if `DashboardPage.xaml.cs` (which uses `BitmapImage` from the same namespace for
`hero.webp`) doesn't already cover it. In XAML, bind an `Image.Source` (not `ImageBrush` — no
crop/alignment need for small flag icons) to `{Binding DepartureFlagSource}`/`ArrivalFlagSource`;
when the source is `null` (unresolvable ICAO), the `Image` simply renders nothing — no crash, no
placeholder needed, matching how `Flights.razor`'s own markup already conditionally omits the
flag span when `dep`/`arr` is null.

- [ ] **Step 3: Grade and status chip colors**

Both are already bug-free in `Flights.razor` (confirmed during spec research) — port the exact
mappings, no fix needed:
```
GradeChipClass: >=90 chip-green, >=75 chip-blue, >=60 chip-amber, else chip-red
StatusChipClass: InProgress -> active(blue-ish), Completed -> done(green-ish), else -> pending(muted)
```
Map to the same hex values Pilot Hub's Task 4 already established for its own grade chips
(`#3EC7AC` green / `#4D9BEC` blue / `#F59E0B` amber / `#EF4444` red) for the grade chip, and pick
reasonable status-chip colors matching `app.css`'s `.status-chip.active`/`.done`/`.pending` rules
(check the actual CSS values rather than guessing).

- [ ] **Step 4: `RebuildFlightRows()`**

```csharp
private void RebuildFlightRows()
{
    FlightRows.Clear();
    var filtered = FlightsFilterLogic.FilterByTab(_viewModel.Flights, _activeTab);
    foreach (var f in filtered)
    {
        FlightRows.Add(new FlightDisplayRow { /* ... */ });
    }
    EmptyStateText.Visibility = FlightRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    FlightsList.Visibility = FlightRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
}
```
Called from `Loaded` (replacing Task 2's stub, if you stubbed it) and from each tab-click handler.
Empty-state copy: exact current text from `Flights.razor`.

- [ ] **Step 5: Build, `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`, commit**

```bash
git add AviatesAirTracker/Pages/FlightsPage.xaml AviatesAirTracker/Pages/FlightsPage.xaml.cs
git commit -m "feat(flights): flight table with route flags and status/grade chips"
```

---

## Task 4: Modals — delete, approve/reject, force-remove

**Files:**
- Modify: `AviatesAirTracker/Pages/FlightsPage.xaml`
- Modify: `AviatesAirTracker/Pages/FlightsPage.xaml.cs`

This is the new pattern for this migration — nothing prior has needed a modal. WinUI's native
`ContentDialog` is the idiomatic mechanism; use it rather than a custom overlay `Border` (a
`ContentDialog` shown via `ShowAsync()` handles focus-trapping, dismiss-on-escape, and the
darkened backdrop for free — a hand-rolled overlay would need to reimplement all of that).

- [ ] **Step 1: Delete-with-reason dialog**

```csharp
private async void OnDeleteClicked(FlightRecord target)
{
    var reasonBox = new TextBox { PlaceholderText = "Reason for deletion", AcceptsReturn = true, Height = 80 };
    var errorText = new TextBlock { Foreground = new SolidColorBrush(Colors.Red), Visibility = Visibility.Collapsed, Text = "A reason is required." };
    var panel = new StackPanel { Spacing = 8, Children = { reasonBox, errorText } };

    var dialog = new ContentDialog
    {
        XamlRoot = this.XamlRoot,
        Title = "Request Flight Deletion",
        Content = panel,
        PrimaryButtonText = "Submit",
        CloseButtonText = "Cancel",
        DefaultButton = ContentDialogButton.Primary,
    };

    dialog.PrimaryButtonClick += async (_, args) =>
    {
        if (string.IsNullOrWhiteSpace(reasonBox.Text))
        {
            errorText.Visibility = Visibility.Visible;
            args.Cancel = true; // keep the dialog open on validation failure
            return;
        }
        await _viewModel.SubmitDeletionRequestAsync((target, reasonBox.Text));
    };

    await dialog.ShowAsync();
}
```
`this.XamlRoot` must resolve correctly from within a `Page` hosted in `ShellWindow`'s `NativeHost`
`Frame` — this hasn't been exercised before in this migration (Dashboard/PilotHub have no
dialogs), so verify it actually works when you build/test this, don't assume. If `XamlRoot` is
null/unavailable for some reason specific to how `NativeHost.Navigate()` hosts pages, that's a
real finding to report, not something to silently work around with a different (worse) mechanism.

- [ ] **Step 2: Approve/Reject buttons (Pending Review tab)**

Two `Border`-as-button elements per pending-request row, calling
`_viewModel.ApproveDeletionAsync(request)`/`RejectDeletionAsync(request)` directly (no
confirmation dialog needed for these two — `Flights.razor` doesn't have one either, don't add
one that wasn't there).

- [ ] **Step 3: Force-remove confirmation dialog**

Same `ContentDialog` pattern as Step 1, simpler (no text input, just a confirm/cancel), calling
`_viewModel.ForceRemoveBrokenFlightAsync(target)` on confirm. Exact current confirmation copy
from `Flights.razor`'s force-remove modal — read it, don't invent wording.

- [ ] **Step 4: Wire the row action icons**

The trash icon (delete) and force-remove trigger (wherever `Flights.razor` actually surfaces it —
check the file; the research summary didn't pin down exactly which UI element triggers force-
remove vs. the reason-required delete flow, confirm this distinction before wiring two different
buttons to the wrong flows) get real `Tapped`/`PointerPressed` handlers now, calling the dialogs
above. The already-confirmed-inert elements (Filter, Export, View-details chevron) stay exactly
as inert as Task 2/3 left them — don't accidentally wire one of these while you're in the
neighborhood.

- [ ] **Step 5: Build, `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`, commit**

```bash
git add AviatesAirTracker/Pages/FlightsPage.xaml AviatesAirTracker/Pages/FlightsPage.xaml.cs
git commit -m "feat(flights): delete/approve/reject/force-remove dialogs"
```

---

## Task 5: Shell wiring, Dashboard CTA, final verification

**Files:**
- Modify: `AviatesAirTracker/Navigation/NavDestinations.cs`
- Modify: `AviatesAirTracker/Pages/DashboardPage.xaml.cs`

- [ ] **Step 1: Nav destination**

```csharp
new("flights", "Flights", "/flights", "Overview", NativePageType: typeof(Pages.FlightsPage)),
```

- [ ] **Step 2: Wire Dashboard's "View Flights" CTA**

`DashboardPage.xaml`'s hero `Border` (currently inert, with a code comment stating so — find and
update that comment too, it'll be wrong once this lands) gets a `Tapped` handler. **This is the
first time one native page navigates to another** — nothing prior has needed this. The natural
mechanism is `this.Frame.Navigate(typeof(Pages.FlightsPage))`, but verify `Frame` is actually
populated on a `Page` hosted via `ShellWindow`'s `NativeHost.Navigate(pageType)` call before
assuming this works — if `this.Frame` is null or navigates the wrong `Frame`, that's a real
finding, report it plainly rather than papering over it with, say, a static reference back to
`ShellWindow`'s `NativeHost` (which would work but is a worse pattern than the standard `Frame`
navigation idiom — only fall back to it if `this.Frame` genuinely doesn't work as expected, and
say so).

- [ ] **Step 3: Full build and test suite**

`dotnet build AviatesAirTracker.sln -c Debug` — 0 errors, no new warnings beyond baseline.
`dotnet test tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj` — full suite passing,
including Task 1's new `FlightsFilterLogicTests`.

- [ ] **Step 4: Startup check**

Check `tasklist` for MSFS first. Launch briefly, confirm clean Serilog startup, no `[Shell]`
errors, close cleanly. Same conservative no-computer-use practice as every prior task in this
migration — defer actual click-through/dialog verification to a human, state that plainly.

- [ ] **Step 5: Record results and commit**

Append a results section to this plan doc covering pass/fail per step, and specifically note
whether `this.Frame.Navigate(...)` worked as expected for Dashboard→Flights navigation (this is
new territory, worth a clear record either way). Commit:

```bash
git add -f docs/superpowers/plans/2026-08-23-winui3-phase3-flights-native.md
git add AviatesAirTracker/Navigation/NavDestinations.cs AviatesAirTracker/Pages/DashboardPage.xaml.cs
git commit -m "feat(flights): wire native page into the nav rail and Dashboard's CTA"
```

---

## Definition of done

- Build clean, full test suite passing (including new `FlightsFilterLogicTests`)
- Clicking "Flights" in the nav rail shows the native page; every other nav item unaffected
- Tabs filter correctly; delete-with-reason validates and submits; approve/reject work; force-
  remove clears `FlightSessionManager`'s current flight when applicable
- Dashboard's "View Flights" button navigates to the native Flights page
- Filter/Export/View-details stay inert, matching current behavior exactly
- No `.razor` file touched; `Flights.razor` left in place, unreferenced

---

## Task 5 results (2026-08-24)

**Starting-commit check:** the worktree's `HEAD` was initially `d908203` (unrelated PIREP-sync
work several commits past the expected base), not `17b99995...`. `git merge-base --is-ancestor`
confirmed `17b99995...` was still a strict descendant of `d908203` (i.e. a clean fast-forward, not
a divergent branch) — `git merge --ff-only 17b99995c5be83cf2662c35ae3ead7e4a383db5c` succeeded
after stashing an unrelated local-only `.claude/settings.local.json` permission-allowlist tweak
(unstashed cleanly afterward, no conflict). `git rev-parse HEAD` confirmed the exact target hash
before any Task 5 edits began. `FlightsPage.xaml`/`.xaml.cs` were confirmed present with the
flight table, route flags, grade/status chips, the Pending Review request list, and all three
`ContentDialog`s (delete-with-reason, force-remove, plus approve/reject actions) — Tasks 1–4's
work, intact.

**Step 1 — Nav destination:** Pass. Added
`NativePageType: typeof(Pages.FlightsPage)` to the `"flights"` entry in
`AviatesAirTracker/Navigation/NavDestinations.cs`, matching the `dashboard`/`pilot-hub` pattern
exactly.

**Step 2 — Dashboard CTA:** Pass, with one correction made along the way. Added
`Tapped="OnViewFlightsTapped"` to the hero `Border` in `DashboardPage.xaml` and an
`OnViewFlightsTapped` handler in `DashboardPage.xaml.cs` calling `this.Frame.Navigate(typeof(FlightsPage))`.
The stale "Inert, matching the current Blazor page: no destination to navigate to yet." comment
was rewritten to describe the new behavior. The initial edit introduced a literal `--` inside the
updated XAML comment ("... in the code-behind) -- the first native-to-native navigation...") —
caught by re-running the project's own documented `grep -n -- "--"` check before considering the
task done, and fixed by rewording to a semicolon. This is exactly the recurring gotcha the plan's
"Before you start" section warned about, now recurring once more, this time in a page that had
been previously clean.

`this.Frame` was **not** taken on faith. `ShellWindow.xaml` declares `NativeHost` as a literal
`<Frame x:Name="NativeHost" .../>`, and `ShellWindow.Navigate()` calls
`NativeHost.Navigate(destination.NativePageType)` to route to any native page. This is the
standard WinUI/UWP `Frame.Navigate(Type)` API — its documented behavior is that the Frame sets the
navigated-to `Page` instance's own `Frame` property to itself as part of navigation, which is
framework plumbing, not something `ShellWindow` implements. `this.Frame.Navigate(...)` inside
`DashboardPage` was written on that basis, with a defensive null-check that logs
`Log.Warning("[Dashboard] View Flights tapped but this.Frame is null; cannot navigate to FlightsPage")`
and returns rather than throwing, in case that framework guarantee doesn't hold in this specific
host configuration for some undiscovered reason. **This has not been exercised at runtime** — the
Step 4 startup check only confirms the app launches and the Dashboard page loads without an
unhandled exception; it does not click the CTA. Whether `this.Frame` is actually non-null and
whether the navigation actually lands on `FlightsPage` in practice is unverified and left to human
click-through, per the task's explicit instruction not to attempt computer-use verification here.

**Step 3 — Full build and test suite:** Pass. `dotnet build AviatesAirTracker.sln -c Debug` — 0
errors. 9 unique warning messages (each emitted twice, once per restore/build pass), all
pre-existing and unrelated to this task's files (`LandingAnalyzer`/`FlightSessionManager`/
`FlightPhaseDetector` unused-field warnings, `MainViewModel` `MVVMTK0034` generated-property
warnings) — no new warnings introduced by the `NavDestinations.cs`/`DashboardPage.xaml`/
`DashboardPage.xaml.cs` changes. `dotnet test tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj`
— 39/39 passing, matching the expected count (including Task 1's `FlightsFilterLogicTests`); Task
5 adds no new tests, as expected.

**Step 4 — Startup check:** Pass, within its deliberately limited scope. `tasklist` confirmed MSFS
was not running beforehand. The built exe was launched, left running ~10 seconds, and closed
cleanly (process exit confirmed via `tasklist`/`Get-Process`). The Serilog log
(`%AppData%\AviatesAirTracker\logs\aviates_20260824_001.log`) shows a clean startup: `[Startup] UI
host ready at http://127.0.0.1:...`, `[Shell] Flights nav badge: AttentionDotInfoBadgeStyle`
(the badge-refresh logic introduced back in Phase 3 Task 2, now exercising the `flights` entry's
resolution normally), and no `[Shell]`-tagged errors, no `FTL`-level log lines, and no "Unhandled"/
"Exception was thrown" entries anywhere in this run's window. The only exceptions logged are
pre-existing, unrelated `SharpDX.SharpDXException: DIERR_DEVICENOTREG` debug-level noise from
`PttInputService`'s joystick poll loop — expected on this machine, which has no physical joystick
attached, and present in prior runs' logs too, unconnected to this task's changes.

**What Step 4 does NOT verify** (consistent with every prior task in this migration, but a longer
list than usual given how much of this page is new interaction surface):
- Whether clicking "Flights" in the nav rail actually renders the native page correctly (table
  layout, route flags, chip colors, tab switching).
- Whether the Pending Review tab's request list — built from scratch in Task 4, no prior
  equivalent — actually renders and behaves correctly.
- Whether any of the three `ContentDialog`s actually display when triggered (`this.XamlRoot`
  resolving correctly is the specific open question flagged since Task 4; this task is the first
  point where it's reachable at all, but reachability was not exercised).
- Whether `this.Frame.Navigate(...)` for the Dashboard→Flights CTA actually works end-to-end, as
  covered above.
- General visual fidelity of the whole page against `Flights.razor`/the design spec.

All of the above are left to a human's click-through pass, per this project's established,
deliberate practice (documented identically in the Dashboard and Pilot Hub tasks' own results
sections) of not using computer-use/screenshot verification from an agent for this kind of check.

**Notable fixes/decisions from earlier tasks, for context:**
- **Pending Review badge bug (`ca41b80`):** `PendingRequests` holds every deletion request
  regardless of status (the Pending Review tab itself needs to show Approved/Rejected rows, not
  just open ones), but the nav-rail badge count was originally reading `PendingRequests.Count`
  directly instead of filtering to `DeletionRequestStatus.Pending` — `Flights.razor`'s own badge
  filters to just the open ones. Caught and fixed before it could show a misleading count once
  real approval/rejection history accumulates.
- **`FlightLogRow` naming (Task 3):** `DashboardPage.xaml.cs` already declares its own
  `FlightDisplayRow` class (for the Recent Flights list) in the same `AviatesAirTracker.Pages`
  namespace. Naming the Flights page's own display-row class `FlightDisplayRow` too would have
  been a `CS0101` duplicate-type compile error the moment both pages exist in the same assembly,
  so Task 3 named it `FlightLogRow` instead — same pattern (plain mutable class, not a record, for
  the usual `CS8852` reason), different name.
- **Double-click guard (Task 4):** Both the delete-with-reason and force-remove `ContentDialog`s
  reproduce a re-entrancy guard ported from `Flights.razor` itself: a boolean (`isSubmitting`/
  `isRemoving`) set before the async repository call and checked at the top of the
  `PrimaryButtonClick` handler, cancelling (`args.Cancel = true`) a second click that arrives while
  the first is still in flight — guarding against a fast double-click firing
  `SubmitDeletionRequestAsync`/`ForceRemoveBrokenFlightAsync` twice concurrently before the dialog
  visually closes. `FlightsViewModel` itself has no equivalent guard, matching the original
  `.razor` behavior exactly (the guard lives at the UI layer, not the ViewModel layer).
- **Pending Review tab's request list (Task 4):** Did not exist until Task 4 built it from
  scratch — Task 3 only covered the flight table itself (all tabs before Task 4 showed the same
  flight table, just filtered). Task 4 added the distinct deletion-request row layout with its own
  Approve/Reject actions.

**Overall verdict:** All five steps of Task 5 pass. Build is clean (0 errors, no new warnings),
the full 39-test suite passes, and a real startup/shutdown cycle logged no `[Shell]` errors or
unhandled exceptions. The nav-rail wiring and Dashboard CTA are code-complete and compile
correctly, resting on a documented, standard WinUI `Frame` navigation guarantee rather than an
untested assumption — but per this migration's consistent practice, actual visual and
click-through behavior (dialog display, `XamlRoot` resolution, native-to-native navigation firing
correctly, tab/table/Pending-Review rendering) remains unverified by this task and is left to a
human pass, same as Dashboard's and Pilot Hub's own final tasks.

## Confirmed by the user (2026-08-23)

The user ran the app and confirmed every previously-unverified item works: Dashboard's "View
Flights" button navigates to the native page correctly (the first native-to-native navigation in
this migration, working as expected on the first try), the delete dialog displays and behaves
(`XamlRoot` resolves fine from a `NativeHost`-hosted page), and the table/tabs render correctly.
One polish note from the screenshot: the Flight Logbook and Pending Review table rows had their
text sitting right against the divider lines below them (header/row vertical padding was
12px/13px) — bumped to 17px/19px on both tables for real breathing room, commit `e4c7cef`.

**Phase 3's third native page is done.**
