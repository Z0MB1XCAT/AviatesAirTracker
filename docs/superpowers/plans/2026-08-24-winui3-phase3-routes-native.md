# WinUI 3 Migration — Phase 3, fifth page: native Routes — Implementation Plan

**Date:** 2026-08-24
**Spec:** `docs/superpowers/specs/2026-08-24-winui3-phase3-routes-native-design.md` — read it
first for the "why." This plan only covers the "what" and "how."
**Branch:** `claude/blazor-winui3-migration-a10c79`

**For agentic workers: REQUIRED SUB-SKILL:** use `superpowers:subagent-driven-development`
(recommended) or `superpowers:executing-plans` to implement this plan task-by-task.

## Before you start

- No `<Style>` element, no new `Theme/Tokens.xaml` entry, anywhere in this project's XAML —
  silently crashes `XamlCompiler.exe`. Hardcode colors inline or set them in code-behind.
- No literal `--` inside any XAML comment (invalid XML, same silent crash) — this has recurred in
  every single task of every prior plan despite being documented every time. Check your own new
  comments with `grep -n -- "--" <file>` (excluding intentional `──` header decorations) before
  considering any task's build final, every single time.
- Anything bound to XAML needing per-item computed values needs a plain mutable class with
  `{ get; set; }` properties — not an init-only `record` (`CS8852`).
- Anything whose exact shape matters should be a `Border`, not a `<Button>` — this SDK has no
  `<Style>` to guarantee `Button` respects `CornerRadius`. Native controls with acceptable
  default chrome (`ProgressRing`, `CalendarDatePicker`, `TimePicker`, `ComboBox`) are fine to use
  as-is — this rule is about shape-sensitive custom pills/rects, not every control.
- `ContentDialog` is established (Flights' delete/force-remove dialogs, the landing-result
  dialog) — read `AviatesAirTracker/Dialogs/LandingResultDialog.cs` for the dedicated-builder-
  class pattern this plan's booking dialog follows, since it's too large to inline in
  `RoutesPage.xaml.cs` the way Flights' two smaller dialogs were.
- **The 5-active-booking cap is NOT a client-side check to build** —
  `BookingService.CreateBookingAsync` already enforces it internally (fetches existing bookings,
  compares against `MaxBookings`, returns an error tuple if exceeded). `Routes.razor` itself never
  duplicates this check; don't invent one. The only client-side validation `Routes.razor` actually
  does is: date/time both present, valid date/time parse, departure in the future, departure not
  more than 90 days out. Reproduce exactly these four checks, no more, no fewer.

## Task 1: `RoutesViewModel`

**Files:**
- New: `AviatesAirTracker/ViewModels/RoutesViewModel.cs`
- Modify: `AviatesAirTracker/App.xaml.cs` (DI registration)

- [ ] **Step 1: Read the source material**

Read `AviatesAirTracker.LegacyWeb/Pages/Routes.razor`'s full `@code` block (starts around line
517) yourself before writing anything — this plan quotes the exact booking-submit logic below,
but confirm it against the live file rather than trusting a paraphrase for anything not quoted
verbatim.

- [ ] **Step 2: The ViewModel**

```csharp
public partial class RoutesViewModel : ObservableObject
{
    private readonly RoutesService _routesService;
    private readonly BookingService _bookingService;
    private readonly FleetService _fleetService;
    private readonly SettingsService _settings;

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private bool _isLoading = true;

    public ObservableCollection<AviatesRoute> Routes { get; } = [];

    public string CurrentAirportIata => _settings.Settings.CurrentAirportIata ?? "";

    public RoutesViewModel(RoutesService routesService, BookingService bookingService, FleetService fleetService, SettingsService settings)
    {
        _routesService = routesService;
        _bookingService = bookingService;
        _fleetService = fleetService;
        _settings = settings;
    }

    public async Task SyncPositionAsync() => await _bookingService.SyncPositionAsync();

    public async Task LoadRoutesAsync(string tab, string? searchQuery)
    {
        IsLoading = true;
        try
        {
            var fleet  = tab == "all" ? null : tab;
            var q      = string.IsNullOrWhiteSpace(searchQuery) ? null : searchQuery.Trim();
            var origin = string.IsNullOrEmpty(CurrentAirportIata) ? null : CurrentAirportIata;

            var (routes, total) = await _routesService.GetRoutesAsync(fleet, q, origin);
            Routes.Clear();
            foreach (var r in routes) Routes.Add(r);
            TotalCount = total;
        }
        finally { IsLoading = false; }
    }

    public async Task ClearPositionAsync()
    {
        await _bookingService.SetPositionAsync("");
        await LoadRoutesAsync("all", null); // matches Routes.razor's ClearPosition(), which reloads with whatever tab/search state the page currently has -- the page (not the ViewModel) owns that state, so it passes it in; "all"/null here is illustrative only, the real call site in Task 2 passes the page's actual current tab/search
    }
}
```

Note the comment on `ClearPositionAsync` — the page (Task 2) owns `_activeTab`/`_searchQuery` the
same way `FlightsPage`/`RoutesPage`'s predecessors keep UI-local selection state on the page, not
the ViewModel; when Task 2 calls this method, it should re-call `LoadRoutesAsync` itself with its
own current tab/search afterward rather than relying on this method's illustrative default.
Simplest: don't put the reload inside `ClearPositionAsync` at all — just `SetPositionAsync` here,
and let the page's own click handler call `LoadRoutesAsync` right after, mirroring
`Routes.razor`'s `ClearPosition()` exactly. Use your judgment on whichever reads cleaner, but
match the original's actual sequencing (`SetPositionAsync` then reload).

- [ ] **Step 3: Registration
      loading + callsign generation — exposed for the booking dialog (Task 3) to call**

Confirmed directly against source: `GetRegistrationsAsync` lives on **`FleetService`**, not
`BookingService` — add `FleetService` as a fourth constructor dependency (already DI-registered
elsewhere in this app, `AddSingleton` per the design spec's research).

```csharp
public async Task<List<AircraftRegistration>> LoadRegistrationsAsync(string typeCode)
{
    if (string.IsNullOrEmpty(typeCode)) return [];
    return await _fleetService.GetRegistrationsAsync(typeCode);
}

public static string GenerateCallsign(string? aircraftType) => BookingService.GenerateCallsign(aircraftType);

public async Task<(FlightBooking? Booking, string? Error)> CreateBookingAsync(
    AviatesRoute route, string aircraftType, string? registration, DateTime scheduledDepUtc, string callsign)
    => await _bookingService.CreateBookingAsync(route, aircraftType, registration, scheduledDepUtc, callsign);

public async Task SetPositionAsync(string iata) => await _bookingService.SetPositionAsync(iata);
```

(`SetPositionAsync` added here too, per Step 5's note below, so `BookingDialog` doesn't need its
own separate reference to `BookingService` — it only ever talks to `RoutesViewModel`.)

- [ ] **Step 4: DI registration**

`services.AddSingleton<RoutesViewModel>();` in `App.xaml.cs`, matching every other ViewModel.

- [ ] **Step 5: Build, commit**

```bash
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
git add AviatesAirTracker/ViewModels/RoutesViewModel.cs AviatesAirTracker/App.xaml.cs
git commit -m "feat(routes): RoutesViewModel, ported from Routes.razor's @code block"
```

---

## Task 2: Page skeleton, position banner, tabs, search, route card grid

**Files:**
- New: `AviatesAirTracker/Pages/RoutesPage.xaml`
- New: `AviatesAirTracker/Pages/RoutesPage.xaml.cs`

- [ ] **Step 1: Skeleton + DI**

```csharp
public sealed partial class RoutesPage : Page
{
    private readonly RoutesViewModel _viewModel;
    private string _activeTab = "all";
    private string _searchQuery = "";
    private DispatcherTimer? _searchDebounce;

    public RoutesPage()
    {
        InitializeComponent();
        _viewModel = App.GetService<RoutesViewModel>();
        DataContext = _viewModel;
        Loaded += async (_, _) =>
        {
            await _viewModel.SyncPositionAsync();
            await _viewModel.LoadRoutesAsync(_activeTab, _searchQuery);
            RebuildRouteCards();
            UpdatePositionBanner();
        };
    }
}
```

- [ ] **Step 2: Position instrument banner**

Two states per `CurrentAirportIata`: locked (IATA, route count, a pulsing-green status dot —
reuse the pulsing-dot idiom if one already exists in this codebase, e.g. Phase 2's ACARS/Map
"Live" pill dot, otherwise a plain static dot is an acceptable simplification worth noting in
your report) plus a "Reposition" `Border`-button calling `ClearPositionAsync` then reloading;
free (a dashed-border banner, "no base set" copy — exact text from `Routes.razor`).

- [ ] **Step 3: Tab bar**

All Routes / Mainline / Cargo / Regional — same `Border`-active-state technique `FlightsPage`
already established. Clicking a tab clears search (matching `Routes.razor`'s `SwitchTab`'s
`_searchQuery = ""` reset) and reloads.

- [ ] **Step 4: Search**

A toggle reveals a `TextBox`. Debounce: a `DispatcherTimer` (interval 400ms) reset (`Stop()` +
`Start()`) on every `TextChanged`, firing `LoadRoutesAsync` once it elapses without another
keystroke — the simplest native equivalent of the original's `Task.Delay`+`CancellationTokenSource`
cancel-and-restart pattern. Toggling search closed while a query is active clears it and reloads
(matching `ToggleSearch()`'s exact behavior).

- [ ] **Step 5: Loading/empty states + result count**

A `ProgressRing` (`IsActive` bound/toggled to `IsLoading`) while loading. Two distinct empty
states — exact copy from `Routes.razor` for "no routes from locked position" vs. generic "no
routes found"/"no routes matching {query}" (these are different messages for different
conditions, don't collapse them into one). Result count strip: "Showing {Routes.Count} of
{TotalCount} routes\" — exact original phrasing.

- [ ] **Step 6: Route card grid**

Precomputed display-row class (plain mutable, not a `record`):
```csharp
public sealed class RouteDisplayRow
{
    public string RouteLabel { get; set; } = "";
    public ImageSource? OriginFlagSource { get; set; }
    public ImageSource? DestFlagSource { get; set; }
    public string OriginName { get; set; } = "";
    public string DestName { get; set; } = "";
    public string AircraftType { get; set; } = "";
    public string Frequency { get; set; } = "";
    public string FlightNumber { get; set; } = "";
    public string DistanceText { get; set; } = "";
    public string BlockTimeText { get; set; } = "";
    public AviatesRoute Source { get; set; } = null!; // Task 3's booking dialog needs the full route object
}
```
Flags via `IcaoCountry.IsoFromIcao` + `SvgImageSource`, the exact same technique `FlightsPage`
already established (literally the same `wwwroot/images/flags/*.svg` files) — reuse that code's
shape, don't re-derive it. Distance: `route.DistanceNm` (already computed on `AviatesRoute`).
Block time: `route.BlockTimeDisplay` (also already computed). A wrap-layout `ItemsControl`
(3-column card grid). Each card's "Book" `Border`-button opens the booking dialog (Task 3) with
the row's `Source` route.

- [ ] **Step 7: Build, check for `--` trap, commit**

```bash
git add AviatesAirTracker/Pages/RoutesPage.xaml AviatesAirTracker/Pages/RoutesPage.xaml.cs
git commit -m "feat(routes): native page skeleton, position banner, tabs, search, and route grid"
```

---

## Task 3: Booking dialog

**Files:**
- New: `AviatesAirTracker/Dialogs/BookingDialog.cs`

This is the largest single piece of work in this plan — a full form with async-loaded data,
regenerable state, and validation matching the original exactly.

- [ ] **Step 1: Skeleton, following `LandingResultDialog`'s builder-class pattern**

```csharp
namespace AviatesAirTracker.Dialogs;

public static class BookingDialog
{
    public static async Task ShowAsync(XamlRoot xamlRoot, RoutesViewModel viewModel, AviatesRoute route)
    {
        // build content, wire handlers, ShowAsync -- Steps 2-6 below
    }
}
```
Called from `RoutesPage`'s "Book" button handler as
`await BookingDialog.ShowAsync(RoutesPage_XamlRoot, _viewModel, row.Source);` — a `Page` DOES
expose `XamlRoot` directly (unlike `ShellWindow`, which needed `RootGrid.XamlRoot`), so
`this.XamlRoot` from within `RoutesPage` is correct here; don't copy the `Window`-vs-`Page`
`XamlRoot` workaround from the landing-dialog plan verbatim, that was specific to `ShellWindow`
being a `Window`.

- [ ] **Step 2: Header + date/time fields**

Route summary (origin → dest, aircraft type — read from `route`). `CalendarDatePicker` for the
date (default `DateTime.Today.AddDays(1)`, matching `Routes.razor`'s exact default), `TimePicker`
for the time (default 10:00, matching the original's exact default `"10:00"`).

- [ ] **Step 3: Registration selector**

On dialog open, call `viewModel.LoadRegistrationsAsync(route.AircraftType.Trim())` (matches
`OpenBookingModal`'s exact `.Trim()` call — don't drop it, aircraft type strings in this codebase
have had whitespace issues before). Populate a `ComboBox`. Default selection: prefer the first
`Status == "active"` registration, falling back to the first one overall if none are active,
falling back to empty if the list is empty — matches `LoadRegistrationsAsync`'s exact fallback
chain. Show a loading indicator while the async call is in flight (`_loadingRegistrations`
equivalent).

- [ ] **Step 4: Callsign preview + regenerate**

Initial value: `RoutesViewModel.GenerateCallsign(route.AircraftType)` (no `.Trim()` here — check
the original, `OpenBookingModal` calls `GenerateCallsign(route.AircraftType)` untrimmed, a real
inconsistency versus the registration load's trimmed call, worth preserving exactly rather than
"fixing" since it's what the original actually does). A "Regenerate" `Border`-button re-calls the
same method and updates the displayed value.

- [ ] **Step 5: Validation + submit — reproduce these four checks exactly, no more, no fewer**

```csharp
// 1. Both date and time present (CalendarDatePicker/TimePicker controls make this less likely
//    than the original's raw HTML inputs, but check for a null SelectedDate anyway)
// 2. departure in the future: depUtc <= DateTime.UtcNow -> "Departure must be in the future."
// 3. not more than 90 days out: depUtc > DateTime.UtcNow.AddDays(90) -> "Bookings cannot be
//    made more than 90 days in advance."
// (there is no 4th "invalid format" check needed the way Routes.razor has one -- that check
// exists only because the original parses raw HTML date/time strings; CalendarDatePicker/
// TimePicker give you a real DateTime/TimeSpan directly, so this failure mode doesn't exist
// in the native version. Don't build a check for something that can't happen here.)
```
On submit: `await viewModel.CreateBookingAsync(route, route.AircraftType.Trim(), registration, depUtc, callsign)`
(match `SubmitBooking()`'s exact `_selectedAircraft` value, which is `route.AircraftType.Trim()`
set once in `OpenBookingModal` — not `route.AircraftType` untrimmed). On success: advance position
via `await viewModel.SetPositionAsync(route.DestIata)` (the small wrapper Step 3 added, so
`BookingDialog` never needs its own `BookingService` reference), show a success toast (5-second
auto-dismiss, matching the original — a `TeachingTip` or a manually-timed overlay, whichever is
simpler to get right), and trigger a route-list reload on the underlying page — simplest approach:
have `ShowAsync`'s caller (`RoutesPage`'s "Book" button handler) `await BookingDialog.ShowAsync(...)`
and reload afterward regardless of outcome (a reload after a failed/cancelled booking is a
harmless no-op, simpler than threading a success flag back out). On failure: show the error
message from `CreateBookingAsync`'s returned tuple inline in the dialog (don't close it), matching
the original's `_modalError` display exactly. Use the double-click guard pattern Flights' delete
dialog already established (`args.GetDeferral()` + `Cancel = true` re-entrancy check) since this
submit is also async.

- [ ] **Step 6: Build, check for `--` trap, commit**

```bash
git add AviatesAirTracker/Dialogs/BookingDialog.cs
git commit -m "feat(routes): native booking dialog"
```

---

## Task 4: Shell wiring, final verification

**Files:**
- Modify: `AviatesAirTracker/Navigation/NavDestinations.cs`

- [ ] **Step 1**: `"routes"` entry gets `NativePageType: typeof(Pages.RoutesPage)`.

- [ ] **Step 2**: `dotnet build AviatesAirTracker.sln -c Debug` — 0 errors, no new warnings.
      `dotnet test tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj` — full suite
      passing (no new tests this plan — no pure/testable logic was found worth a TDD task, per
      the design spec's own note).

- [ ] **Step 3**: Check `tasklist` for MSFS, launch briefly, confirm clean Serilog startup, close
      cleanly. **Do NOT attempt request_access/screenshot/computer-use verification** unless you
      are the orchestrating session itself continuing this plan directly (a background subagent
      dispatched to implement this task should not attempt it, per this migration's established
      practice) — actually testing the booking flow needs a valid ACARS key and hits the live
      backend; defer that explicitly if unavailable, don't skip it silently.

- [ ] **Step 4**: Record results in this plan doc, commit:
```bash
git add -f docs/superpowers/plans/2026-08-24-winui3-phase3-routes-native.md
git add AviatesAirTracker/Navigation/NavDestinations.cs
git commit -m "feat(routes): wire native page into the nav rail"
```

---

## Definition of done

- Build clean, full test suite passing
- Clicking "Routes" in the nav rail shows the native page; every other nav item unaffected
- Position banner both states render correctly; Reposition works
- Tabs and debounced search actually filter (server round-trip)
- Booking dialog: registration list loads, callsign regenerates, the three real validation
  checks match the original exactly, a successful booking advances position and shows a toast, a
  failed booking shows the server's error message inline
- No `.razor` file touched; `Routes.razor` left in place, unreferenced
