# WinUI 3 Migration — Phase 4, first piece: native map engine + Map page

**Date:** 2026-08-25
**Status:** Draft, awaiting review
**Branch:** `claude/blazor-winui3-migration-a10c79`
**Builds on:** every prior converted page — reuses established conventions. **New territory**:
this is the first page whose data layer (`MapViewModel`) is *already* fully native, and the first
whose rendering layer has no native equivalent anywhere in the codebase to build on.

## Why this is a different kind of conversion

Every page converted so far was "port working Blazor markup + logic to XAML + code-behind."
`MapPage.razor` is different: its `@code` block and its `MapViewModel` are **already native C#**
(confirmed by direct research) — `MapViewModel` is DI-registered in `App.xaml.cs` today and
already does all the telemetry decimation, route-tracking, and other-pilot diffing. The only thing
standing between this page and a native port is that its *rendering target* is Leaflet.js,
reached today via `IJSRuntime` calls into `wwwroot/js/interop.js`'s `window.aviatesMap` object (a
17-function API) over a Blazor-Server SignalR connection.

**Confirmed during research**: there is no native mapping code anywhere in this project to build
on. `Mapsui.Tiling`/`Mapsui.Nts` are referenced in the `.csproj` but have **zero actual code
usages** — fully vestigial, a leftover from a pre-Blazor WPF map that no longer exists. This phase
adds the missing piece: `Mapsui.UI.WinUI` (the matching 4.1.7 WinUI control package, to line up
with the already-pinned `Mapsui.Tiling`/`Mapsui.Nts` versions) plus a new native rendering layer
that reimplements the parts of `window.aviatesMap`'s API this page actually uses.

**Decision (confirmed with the user)**: build a real native Mapsui map, not a scoped WebView2
hosting Leaflet. This means reimplementing rendering natively rather than reusing `interop.js`.

## What already exists and needs zero porting

- `MapViewModel` (`AviatesAirTracker.ViewModels`) — already computes every HUD text field
  (`AltText`, `VsText`, `IasText`, `GsText`, `HdgText`, `MachText`, phase badge + color,
  `NoFlightText`, `HasActiveFlight`), decimates the 20 Hz telemetry stream 5:1, raises
  `MapRenderRequested`, exposes `GetPathIfChanged()` (returns `null` unless the recorded path
  actually grew) and `ConsumePlanSync()` (drains a `volatile` dirty flag). None of this changes.
- `RouteTracker` (recorded flown path, next waypoint, remaining distance), `SimBriefService`
  (`FlightPlanLoaded` event, `FetchLatestOFPAsync`), `OtherPilotsService` (30 s-polled
  `PositionsUpdated` event with `List<LivePilotPosition>`), `MessagingService`
  (`AddOrGetFriendFromLiveMapAsync`), `FlightSessionManager` — all already native, already
  DI-registered, all reused exactly as-is.
- The page's own control flow (subscribe to `MapRenderRequested`/`FlightPlanLoaded`/
  `PositionsUpdated`, diff other-pilot ids against a `HashSet`, marshal cross-thread updates) is
  already a clean, well-structured pattern — the native page's code-behind is a close structural
  mirror of `MapPage.razor`'s `@code` block, not a redesign.

## The new piece: a native map rendering layer

### `AviatesAirTracker/Services/NativeMapRenderer.cs` (new, or similar name — implementer's call)

A thin class wrapping a `Mapsui.Map` + exposing the subset of `window.aviatesMap`'s API this page
actually calls (research confirmed 3 of the 17 exported JS functions — `resetPath`,
`setFlightPath`, `invalidateSize`— have **zero callers anywhere in the repo**; don't port dead
API surface):

- `Init(double lat, double lon)` — creates the `Mapsui.Map`, adds a tile layer sourced from the
  same CARTO dark basemap the JS version uses (`https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png`,
  subdomains a/b/c/d, confirmed exact URL from `interop.js`), sets initial center/zoom (zoom 7,
  min 3 / max 18, matching the original), adds an own-aircraft marker layer, a flown-path layer,
  and a planned-route layer.
- `UpdatePosition(double lat, double lon, double heading)` — moves the own-aircraft marker,
  rotates it (Mapsui's `SymbolStyle.SymbolRotation`), appends to the flown-path polyline (cap at
  the same 2000-point ring buffer the JS version uses, to bound memory on very long flights).
- `UpdateFlightPathAltitude(IReadOnlyList<(double Lat, double Lon, double AltFt)> points)` — the
  altitude-colored flown path. The original renders this as ~2000 individual 1-segment polylines
  each with its own HSL-ramped color (`hsl(270·(1−t), 55%, 42%)`, 100 ft → 45 000 ft) on a shared
  canvas renderer, rebuilt from scratch every time the path length changes. Mapsui doesn't have a
  built-in per-vertex-colored line style, so the native port needs the same "one feature per
  segment" approach — port the exact same HSL→RGB color ramp math, and keep the same
  rebuild-only-on-change guard (`MapViewModel.GetPathIfChanged()` already provides this) so this
  doesn't re-run on every 4 Hz tick, only when the path actually grew.
- `SetPlannedRoute(IReadOnlyList<(double Lat, double Lon, string? Name)> points)` — the dashed
  planned-route line + waypoint dot markers. The original thins labels to ~80 max and shows
  hover tooltips on each dot; WinUI/Mapsui has no free hover-tooltip-on-map-feature equivalent —
  **use `ToolTipService` triggered by `MapControl.Info` pointer-hit-testing**, or fall back to a
  simpler "tap a waypoint dot to see its name in a small popup" interaction if hover hit-testing
  proves too heavy at 4 Hz — implementer's call during the plan, note which was chosen and why.
- `AddAirportMarker(double lat, double lon, string icao, string type)` /
  `ClearAirportMarkers()` — DEP (green) / ARR (amber) / ALT (purple, "ALT " prefix) labeled pins,
  same 3-marker cap as the original.
- `FitRouteBounds()` — fit the view to the planned route + airport markers, same padding/max-zoom
  behavior as the original (`padding [48,48]`, `maxZoom 10`).
- `UpsertOtherPilot(string pilotId, double lat, double lon, IReadOnlyList<(double,double)>? waypoints)` /
  `RemoveOtherPilot(string pilotId)` — a marker per other pilot + an optional dashed route line,
  matching the original. Marker taps need to raise a C# event (`OtherPilotClicked`) that the page
  subscribes to — this replaces the original's `DotNetObjectReference`/`[JSInvokable]` round-trip
  with a direct native event, actually simpler than what it replaces.
- `CenterOnAircraft()` / `ZoomIn()` / `ZoomOut()` / `PanTo(lat, lon)` — thin wrappers over
  Mapsui's `Navigator` (`MapControl.Map.Navigator.CenterOn`/`ZoomIn`/`ZoomOut`).
- `Dispose()` — tear down layers/features.

This class is the direct native analogue of `window.aviatesMap`, built once here and **reused
by the ACARS phase's own mini-map** rather than duplicated — a shared foundation, not a
per-page implementation, which is the main reason this is being scoped as its own phase before
ACARS rather than folded into it.

### `AviatesAirTracker/Pages/MapPage.xaml`/`.xaml.cs` (new)

- A `Mapsui.UI.WinUI.MapControl` filling the page, wrapping the `NativeMapRenderer` above.
- 5 floating HUD panels, ported from `MapPage.razor`'s markup + its substantial inline `<style>`
  block (208 lines — this is the actual visual spec, read it directly rather than guessing
  spacing/colors): identity strip (route + phase badge + ALT/VS/IAS/GS/HDG/MACH grid, draggable),
  progress strip (NEXT/ETA/DIST REM/FUEL, draggable), map controls (center/zoom+/zoom−, **not**
  draggable, matching the original), pilot-detail panel (conditional on a selected other pilot —
  name, phase badge, close, ALT/GS/HDG grid, DEP→ARR, a Message button; **note the original has a
  bug here**: its drag handle exists in markup but `initDrag` is never called on it, so it doesn't
  actually drag despite looking like it should — decide during implementation whether to fix this
  or faithfully reproduce the non-functional handle, and say which), and the plan HUD (loading/
  loaded/error/no-plan states, draggable). Dragging: `ManipulationDelta` + a `TranslateTransform`
  on each draggable panel, replacing the original's `aviatesTokDrag.initDrag` mousedown/move/up
  JS.
- "No active flight" overlay (centered text, `IsHitTestVisible="False"`, matching the original's
  `pointer-events:none`) shown via `VM.HasActiveFlight`.
- Cross-page navigation: the original's `MessageSelectedPilotAsync` calls
  `Nav.NavigateTo($"/messages?open={pilotId}")` — the native page target, `MessagesPage`, doesn't
  have a query-parameter mechanism (this was explicitly scoped out of the Messages conversion,
  since nothing called it at the time). This phase needs to either add a minimal
  `Frame.Navigate(typeof(MessagesPage), pilotId)` parameter-passing path (`MessagesPage` reading
  an `OnNavigatedTo` parameter to auto-select that friend), or drop the auto-select and just
  navigate to Messages plain — implementer's call, note which, but don't silently drop the whole
  feature without saying so.
- Same threading pattern established everywhere else in this migration: `MapViewModel`'s events
  (`MapRenderRequested`, plus `OtherPilotsService.PositionsUpdated`, `SimBriefService.FlightPlanLoaded`)
  may not fire on the UI thread — marshal via `DispatcherQueue.TryEnqueue`, subscribe in `Loaded`,
  unsubscribe in `Unloaded`.

### `AviatesAirTracker/Navigation/NavDestinations.cs` (modified)

`"map"` entry gets `NativePageType: typeof(Pages.MapPage)` — one line.

## What this phase explicitly does not do

- Does not touch `MapPage.razor` or `interop.js` — left in place, unreferenced by the native page
  once wired (the legacy Blazor Map route becomes unreachable from the shell, same as every prior
  converted page).
- Does not convert `Acars.razor` or its own mini-map — that's the next phase, which will **reuse**
  `NativeMapRenderer` rather than rebuild it. Confirmed during research: ACARS's mini-map is a much
  smaller surface (one marker, pan, no other layers) than the full map, so once this foundation
  exists, wiring ACARS to it should be comparatively small.
- Does not touch `DispatchRadioService`/the PTT voice pipeline — confirmed during research to live
  in `Shared/MainLayout.razor` (the global Blazor layout), not on the Map or ACARS pages
  specifically. Converting these two pages does not remove the app's WebView2 dependency; that
  only happens when `MainLayout.razor` itself is eventually retired, which is out of scope here.
- Does not add weather/radar/airspace/hazard-zone map layers — none exist in the original either.
- Does not fix the two other-pilots/SimBrief-plan inconsistencies research surfaced as
  pre-existing (Map's `ImportSimBriefAsync` doesn't persist the plan to disk the way ACARS's
  does; `HoppiesAcarsOrchestrator.MessageLog`'s 200-entry shared history isn't read by ACARS on
  init) — those are ACARS-phase concerns, noted here only so they aren't lost.

## Verification

- Build clean, no new warnings.
- Visual: native map compared against the current Leaflet-based page — basemap tiles, own-aircraft
  marker + rotation, flown path (altitude coloring), planned route + waypoint markers, DEP/ARR/ALT
  airport pins, other-pilot markers + their route lines, all 5 HUD panels (4 draggable, 1 fixed).
- Functional: pan/zoom/center controls, other-pilot marker tap → detail panel → Message button →
  navigates to native Messages (with or without auto-select, per the decision above), SimBrief
  import (loading/error/success states), the "no active flight" overlay. Needs live telemetry
  (MSFS connected) and, for other-pilots, a live backend connection with other pilots actually
  broadcasting position — defer those specific checks explicitly if unavailable, same as every
  prior live-dependency check in this migration.
