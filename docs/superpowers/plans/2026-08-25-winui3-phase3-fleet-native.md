# WinUI 3 Migration — Phase 3, sixth page: native Fleet — implementation plan

**Date:** 2026-08-25
**Branch:** `claude/blazor-winui3-migration-a10c79`
**Design spec:** `docs/superpowers/specs/2026-08-25-winui3-phase3-fleet-native-design.md` (read
this first — this plan assumes it, doesn't repeat it)

## Before you start

- `AviatesAirTracker.ViewModels.FleetViewModel` already exists and is already a DI singleton
  (`App.xaml.cs` line ~418, `services.AddSingleton<FleetViewModel>()`) — **no new ViewModel task
  in this plan.** Get it via `App.GetService<FleetViewModel>()`, same as every prior page.
- Known, recurring WinUI3-SDK crash traps in this project (hit in every task of every plan so
  far): no `<Style>` element anywhere in any `.xaml` file (silently crashes `XamlCompiler.exe`,
  zero diagnostics); no literal double-hyphen `--` inside any XAML comment (invalid XML, same
  silent crash — use a single hyphen or `──` instead). Run
  `grep -n -- "--" <file>.xaml | grep -v '<!--\|-->'` on every XAML file you touch before
  considering a task done.
- `CS8852`: don't bind an init-only `record`/`record struct` to any `ItemsControl` — use a plain
  mutable class with `{ get; set; }` for any display-row wrapper type, matching every prior
  `*DisplayRow`/`*Row` class in this migration.
- `Border`-not-`Button`: this SDK has no `<Style>` to retemplate `Button`, so its default chrome
  doesn't reliably respect `CornerRadius` (established after Dashboard's "sharp oval" bug). Use
  `Border` + `Tapped` for anything pill/precisely-shaped — the refresh icon-button, filter tabs,
  card taps, the panel's close button.
- Route grid gap bug, already fixed once on Routes, don't repeat it here: a fixed-width card in
  an `ItemsWrapGrid` leaves a dead gap on the right when the available width isn't an exact
  multiple of the card width + gutter. `RoutesPage.xaml.cs` (already merged) has the fix:
  `RouteCardsList_SizeChanged`/`UpdateRouteGridColumns`, which recomputes `ItemsWrapGrid.ItemWidth`
  from the panel's own `ActualWidth` on every resize, with cards `HorizontalAlignment="Stretch"`
  instead of a fixed `Width`. Port the same technique to Fleet's type-card grid (and the
  registration-card grid, if it also wraps into multiple columns) from the start.
- `AviatesAirTracker/Dialogs/BookingDialog.cs` (Routes' booking dialog) has an established fix
  for `ContentDialog`'s native buttons defaulting to stock Windows light chrome instead of the
  app's dark palette (`RequestedTheme = ElementTheme.Dark` + a `VisualTreeHelper` walk to recolor
  `PrimaryButton`/`CloseButton` directly) — **not needed here**, Fleet has no `ContentDialog`,
  noted only so nobody reintroduces the same bug if a dialog gets added later.
- Read `Fleet.razor` and its scoped `<style>` block directly for exact copy, class names, and
  conditional field logic before writing XAML — this plan summarizes, it doesn't transcribe
  verbatim.

## Task 1 — Page skeleton, header, loading/error states, stats row, filter tabs, type card grid

Files: `AviatesAirTracker/Pages/FleetPage.xaml` (new), `AviatesAirTracker/Pages/FleetPage.xaml.cs`
(new).

1. `FleetPage : Page`, `FleetViewModel` via `App.GetService<FleetViewModel>()`, `DataContext`.
   `Loaded` handler calls `await _viewModel.LoadAsync()` then builds the UI from the result
   (same `Loaded`-handler-does-the-initial-fetch pattern every prior page uses).
2. Header: "Fleet" / "AviatesAir operates a modern, fuel-efficient fleet across all
   subsidiaries." plus a refresh icon-`Border` (the refresh SVG path is in `Fleet.razor` lines
   20-24 — copy verbatim, same icon-as-`Path`-data technique used throughout this migration).
   Tapped calls `await _viewModel.RefreshAsync()` then rebuilds the UI. A spin animation on
   `IsLoading` is optional polish, not required — a plain static icon is fine; note in your
   report which you did.
3. Loading state (`VM.IsLoading && VM.Data.Types.Count == 0`): spinner + "Loading fleet data…".
   Error state (`VM.Error != null`): an error banner showing `VM.Error`'s text. Both are
   mutually-exclusive early states before the main content, matching `Fleet.razor`'s own
   `@if`/`else if`/`else` structure exactly — don't build a state that can show alongside the
   main content.
4. Stats row: 4 cards (Total Aircraft / In Service / In Maintenance / On Order, reading
   `VM.Data.Stats`), reusing Dashboard's own stat-card visual pattern (icon chip + number +
   label) — check `DashboardPage.xaml` for the exact structure/spacing to match rather than
   inventing a new layout. Icon SVGs are in `Fleet.razor` lines 52-93 (plane/check/alert/shield
   paths) — copy verbatim.
5. Filter tabs: All / Mainline / Regional / Cargo, same `Border`-active-state tab technique
   Flights/Routes already established (a page-local `_activeFilter` field mirroring the tab
   `Border`s' visual state). Tapping a tab calls `_viewModel.SetFilter(name)` (this already
   clears an out-of-filter selection internally — don't add a redundant guard) then rebuilds the
   card grid from `_viewModel.FilteredTypes`.
6. Type card grid: `ItemsControl` + `ItemsWrapGrid`, using the `SizeChanged`/`ItemWidth`-recompute
   technique from `RoutesPage.xaml.cs` (port it, don't reinvent it) so cards fill the available
   width instead of leaving a gap. A `TypeDisplayRow` plain mutable class (not a record) carrying
   everything the card needs pre-computed: status chip class/label, badge text, photo `ImageSource?`
   (via a `FleetPhoto(string typeCode)` lookup — the 3-entry dictionary + fallback exactly matching
   `Fleet.razor`'s own `_fleetPhotos`, real files at `wwwroot/images/fleet-a319.webp`,
   `fleet-a320neo.webp`, `fleet-a380.webp` — same file-path-based loading pattern Dashboard's
   `hero.webp` established), emoji fallback text, name, active/total-or-on-order line, the 4-stat
   mini grid values/labels (capacity-or-payload via `type.CapacityDisplay`/`CapacityLabel`, range,
   status-dependent count, subsidiary), selected/ordered boolean flags for visual state, and the
   source `AircraftType` for the click handler. Selected state: accent-ring border (matching
   `.fleet-card--selected`'s `box-shadow`/`border-color` — read `app.css` for the exact rgba
   values). Ordered state: dashed border + dimmed photo/text (matching `.fleet-card--ordered`/
   `.fleet-card-photo--dim`). Card tap calls a handler that will be wired to selection in Task 2
   — for this task, stub it as a no-op or leave a `// Task 2 wires this.` comment so the build
   stays green, matching the exact pattern Routes' Task 2 used for its own "Book" button stub.
7. Build (`dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`) — 0 errors. Check
   the XAML `--`-in-comment trap before finalizing.
8. Commit: `feat(fleet): native page skeleton, stats row, filter tabs, and type card grid`.

## Task 2 — Registration detail panel, selection wiring, and nav wiring

Files: `AviatesAirTracker/Pages/FleetPage.xaml`/`.xaml.cs` (modified), 
`AviatesAirTracker/Navigation/NavDestinations.cs` (modified).

1. Wire the type card's tap handler (stubbed in Task 1) to
   `await _viewModel.SelectTypeAsync(type)` (this one ViewModel method already handles the
   toggle-off-if-same-type-clicked-again logic internally — don't reimplement it, and don't
   reproduce `Fleet.razor`'s own `HandleTypeClick`, which duplicates the same logic inline by
   calling `FleetService` directly instead of the ViewModel method; the design spec explicitly
   calls this out as an inconsistency to flatten, not reproduce) then rebuild the panel/grid
   (the selected card's visual ring state needs to update too, so a full grid rebuild — or at
   minimum a re-evaluation of each row's `IsSelected` flag — is needed here, not just showing
   the panel).
2. Registration detail panel: visible when `_viewModel.SelectedType != null`. Header (type emoji,
   `"{Name} — Registrations"`, then a sub-line with aircraft count/category/subsidiary and
   optional cruise-speed/service-ceiling — read `Fleet.razor` lines 209-236 for the exact
   conditional formatting, e.g. `FL@(t.MaxAltitudeFt / 100)`), and a close `Border`-button calling
   `_viewModel.ClearSelection()` then rebuilding.
3. Loading state (`VM.IsLoadingRegs`): small spinner + "Loading registrations…". Empty state
   (`VM.SelectedRegistrations.Count == 0`): "No registration records found." Populated state: a
   registration-card grid (reuse the same responsive `ItemsWrapGrid` technique from Task 1/Routes
   if this also wraps into multiple columns — check `Fleet.razor`'s `.fleet-reg-grid` CSS for
   its actual column behavior before deciding). Each registration card: registration code badge,
   status chip (active/maintenance/on-order/retired — 4 states, not 3, matching lines 256-267
   exactly), then conditionally-shown stats (MSN only if present, delivery-or-expected date
   always with a status-dependent label, hub only if present, flights+hours only if
   `TotalFlights > 0` — read lines 278-308 directly, several of these are real conditionals, not
   always-shown fields), and an optional notes line. A `RegistrationDisplayRow` plain mutable
   class (not a record) for the pre-formatted fields, same pattern as every prior `*Row` class.
4. Nav wiring: `AviatesAirTracker/Navigation/NavDestinations.cs`'s `"fleet"` entry gets
   `NativePageType: typeof(Pages.FleetPage)` — one line, matching every prior page's pattern.
5. Build — 0 errors, compare warning count against the current baseline (15 pre-existing,
   unrelated to Fleet/Routes/Booking/NavDestinations) and flag anything new.
6. Verification pass (read-only code review, no computer-use/live-app needed): re-read the full
   diff for both tasks against the design spec — confirm the loading/error/main-content states
   are mutually exclusive, confirm the registration panel's 4 status states and conditional
   field logic match `Fleet.razor` exactly, confirm the type-card grid actually fills its row
   width (not a repeat of Routes' original gap bug). Note whether this environment has a working
   backend connection to actually see real fleet data (check `SettingsService`/`AppSettings` for
   a configured backend URL/key without attempting network calls yourself) — defer that specific
   functional check explicitly if not available, rather than skipping it silently.
7. Commit: `feat(fleet): registration detail panel, selection wiring, and nav wiring`.

## Verification (after both tasks land)

- Build clean, no new warnings.
- Visual: compare native Fleet against the current Blazor page — stats row, filter tabs, card
  grid (photos vs. emoji placeholders, selected/ordered visual states), registration panel
  (loading/empty/populated, all 4 status chip states).
- Functional: filter switching, card selection toggle, refresh (re-fetches and re-selects the
  previously-selected type if one was open, matching `RefreshAsync`'s own behavior). Live-backend
  data display is deferred if this environment has no working connection configured, per the
  design spec.
