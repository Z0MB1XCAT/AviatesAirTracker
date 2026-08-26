# WinUI 3 Migration — Phase 4, first piece: native map engine + Map page — implementation plan

**Date:** 2026-08-25
**Branch:** `claude/blazor-winui3-migration-a10c79`
**Design spec:** `docs/superpowers/specs/2026-08-25-winui3-phase4-map-native-design.md` (read
this first — this plan assumes it, doesn't repeat it)

## Before you start — this plan carries more risk than any prior page, read this carefully

- **UPDATE, resolved by Task 1's spike (commit `7db1929`): the NuGet package id is `Mapsui.WinUI`,
  NOT `Mapsui.UI.WinUI`.** The latter is only the assembly/namespace
  (`Mapsui.UI.WinUI.MapControl` is the correct fully-qualified type name to use in code/XAML) —
  `Mapsui.UI.WinUI` does not exist as a package id on nuget.org and adding it fails with `NU1101`.
  `<PackageReference Include="Mapsui.WinUI" Version="4.1.7" />` is already added to
  `AviatesAirTracker.csproj` and confirmed building clean. Every task from here on should use the
  correct package id if it needs to reference it; the type/namespace name in code is unaffected.
- This was the first task in the whole migration that added a brand-new NuGet package whose
  compatibility with this exact SDK combination (`net8.0-windows10.0.19041.0`, Windows App SDK
  1.8.250906003/1.8.250907003) had never been verified — now confirmed working. `Mapsui.Tiling`/
  `Mapsui.Nts` at version 4.1.7 were already referenced; `Mapsui.WinUI` 4.1.7 is now added
  alongside them. The original "go/no-go spike" framing below is preserved for historical context
  but the spike has already passed — no need to repeat it. **Task 1's first real step was a
  go/no-go spike**: add the package, build a trivial page with just a bare `MapControl` on it, and
  confirm it actually renders. If this fails in a way that looks like a genuine SDK
  incompatibility (not just a usage mistake), STOP, do not attempt increasingly creative
  workarounds, and report back in detail — the orchestrating session needs to know before any
  further tasks are dispatched, since every later task in this plan assumes this foundation works.
- Known, recurring WinUI3-SDK crash traps in this project (hit in every task of every plan so
  far): no `<Style>` element anywhere in any `.xaml` file (silently crashes `XamlCompiler.exe`,
  zero diagnostics); no literal double-hyphen `--` inside any XAML comment (invalid XML, same
  silent crash — use a single hyphen or `──` instead); **and, confirmed for the first time during
  the immediately-prior Settings page**: a malformed hex color literal (anything other than 3, 6,
  or 8 hex digits after `#`) builds with 0 errors but throws an unhandled `XamlParseException` and
  crashes the app the first time the page is actually opened. Run all three checks on every XAML
  file you touch before considering a task done:
  ```
  grep -n -- "--" <file>.xaml | grep -v '<!--\|-->'
  grep -oE '#[0-9A-Fa-f]+' <file>.xaml | sort -u | awk '{ n=length($0)-1; if (n!=3 && n!=6 && n!=8) print }'
  ```
  Both must return nothing.
- `CS8852`: don't bind an init-only `record`/`record struct` to any `ItemsControl` — use a plain
  mutable class with `{ get; set; }` for any display-row wrapper type.
- `Border`-not-`Button`: use `Border` + `Tapped`/`ManipulationDelta` for anything clickable or
  draggable — this SDK has no `<Style>` to retemplate `Button`.
- `MapViewModel`, `RouteTracker`, `SimBriefService`, `OtherPilotsService`, `MessagingService`,
  `FlightSessionManager` are **already fully native** and already DI-registered in `App.xaml.cs`
  — read `MapViewModel.cs` directly before Task 1, it's the single most important file to
  understand in this whole plan. Nothing about its own logic needs to change; only its consumer
  (the page) is new.
- Cross-thread UI updates: `MapViewModel.MapRenderRequested`, `OtherPilotsService.PositionsUpdated`,
  and `SimBriefService.FlightPlanLoaded` may all fire off the UI thread (the 20 Hz telemetry pump,
  a 30 s poll timer, and an async fetch respectively) — marshal every UI mutation via
  `DispatcherQueue.TryEnqueue`, subscribe in `Loaded`, unsubscribe in `Unloaded`, matching the
  pattern already established on `MessagesPage.xaml.cs` and `SettingsPage.xaml.cs`.
- Read `AviatesAirTracker.LegacyWeb/Pages/MapPage.razor` directly in full (570 lines, ~208 of
  which are the inline `<style>` block that's the actual visual spec for HUD panel styling) and
  `AviatesAirTracker/wwwroot/js/interop.js`'s `window.aviatesMap` section (lines ~1-330ish) for
  exact behavior — this plan summarizes, it doesn't transcribe verbatim.

## Task 1 — Go/no-go spike, then the map engine's foundation (tile layer, own-aircraft marker, position updates)

Files: `AviatesAirTracker/AviatesAirTracker.csproj` (modified), `AviatesAirTracker/Services/NativeMapRenderer.cs` (new), a throwaway test page if needed for the spike (delete it before committing — this task's real deliverable is the renderer class, not a page).

1. **DONE — spike passed.** `<PackageReference Include="Mapsui.WinUI" Version="4.1.7" />` (matching the already-pinned `Mapsui.Tiling`/`Mapsui.Nts` version exactly) is already added and building clean, commit `7db1929`. This step is preserved for historical record only.
2. Once the package builds, do a minimal on-screen verification: a `Mapsui.UI.WinUI.MapControl` with a `Mapsui.Map` containing just a tile layer, shown somewhere reachable (a temporary throwaway test harness is fine — don't wire it into real navigation yet, that's Task 4). Confirm it actually paints tiles. This can be done via computer-use/live-app if the environment allows, or by careful code review + a clean build if not — note in your report which you did and why.
3. Build `AviatesAirTracker/Services/NativeMapRenderer.cs`:
   - Constructor takes a `Mapsui.Map` (or wraps/owns one — implementer's call on exact shape, note which).
   - `Init(double lat, double lon)`: adds a tile layer sourced from
     `https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png` (subdomains a/b/c/d — confirmed
     exact URL by reading `interop.js` directly, don't guess), sets initial center/zoom (zoom 7,
     min 3 / max 18, matching the original's `L.map` options), adds an own-aircraft marker layer
     (empty at first), a flown-path layer, and a planned-route layer (both empty).
   - `UpdatePosition(double lat, double lon, double heading)`: moves/creates the own-aircraft
     marker (a `PointFeature` with a `SymbolStyle` — port the original's plane-SVG look as closely
     as Mapsui's symbol styles allow; a simple rotated triangle/arrow is an acceptable
     simplification if an exact SVG-in-Mapsui port proves awkward, note if you simplified),
     rotates it via `SymbolRotation`, and appends the point to the flown-path polyline feature —
     cap the flown path at 2000 points (matching the original's ring-buffer cap), dropping the
     oldest point once the cap is reached.
   - `Dispose()`: clean up layers/features.
4. Build (`dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`) — 0 errors. Compare
   warning count/content to baseline (15 pre-existing, none referencing NativeMapRenderer) — flag
   any new warning.
5. Delete any throwaway test harness from step 2 before committing — this task's real deliverable
   is `NativeMapRenderer.cs` plus the package reference, not a demo page.
6. Commit: `feat(map): add Mapsui.UI.WinUI, native map renderer foundation (tiles, own-aircraft marker, position updates)`.

## Task 2 — Flown-path altitude coloring, planned route, waypoint markers, airport markers

Files: `AviatesAirTracker/Services/NativeMapRenderer.cs` (modified).

1. `UpdateFlightPathAltitude(IReadOnlyList<(double Lat, double Lon, double AltFt)> points)`: the
   original renders this as ~2000 individual 1-segment polylines, each colored via
   `hsl(270·(1−t), 55%, 42%)` where `t` is a 0-1 normalized altitude between 100 ft and 45 000 ft
   (read `interop.js`'s exact ramp math and port it precisely — including the HSL→RGB conversion,
   since `SolidColorBrush`/Mapsui styles need RGB, not HSL directly). Since Mapsui has no built-in
   per-vertex-colored line style, build one `LineString` feature per segment, each with its own
   `VectorStyle.Line` color, matching the original's approach. This method should only be called
   when the path has actually changed (the page will gate this via `MapViewModel.GetPathIfChanged()`
   returning non-null — this class itself doesn't need to know about that gating, just do the
   rebuild efficiently when called).
2. `SetPlannedRoute(IReadOnlyList<(double Lat, double Lon, string? Name)> points)`: a dashed
   line feature for the route, plus a `PointFeature` + small circle-marker style for each named
   (non-null-`Name`) waypoint, thinned to ~80 labeled points max if there are more (matching the
   original's thinning behavior — read `interop.js` for its exact thinning logic/threshold and
   port it, don't invent a different thinning strategy). For hover/tap-to-show waypoint names: per
   the design spec, either wire `MapControl.Info`-based hit-testing to a `ToolTipService` popup on
   hover, or fall back to a simpler tap-to-show interaction if hover hit-testing at 4 Hz render
   cadence proves too expensive or awkward — try the hover approach first, only fall back if it's
   genuinely problematic, and clearly document in your report + code comments which you ended up
   with and why.
3. `AddAirportMarker(double lat, double lon, string icao, string type)` (type is `"dep"`/`"arr"`/`"alt"`)
   and `ClearAirportMarkers()`: labeled pin markers, colors dep=green (`#10B981`), arr=amber
   (`#F59E0B`), alt=purple (`#A855F7`) — matching the original exactly — with `"ALT "` prefixed
   onto the alt marker's label only (matching the original's exact label format).
4. `FitRouteBounds()`: fit the map's viewport to the planned-route points + airport markers
   combined, with padding and a max-zoom cap (original: `padding [48,48]`, `maxZoom 10` — Mapsui's
   `Navigator.ZoomToBox`/equivalent should support a similar padding/max-zoom concept, use
   whatever its actual API calls it).
5. Build — 0 errors, compare warnings to baseline.
6. Commit: `feat(map): altitude-colored flight path, planned route, waypoint and airport markers`.

## Task 3 — Other-pilot markers, zoom/pan/center controls

Files: `AviatesAirTracker/Services/NativeMapRenderer.cs` (modified).

1. `UpsertOtherPilot(string pilotId, double lat, double lon, IReadOnlyList<(double Lat, double Lon)>? waypoints)`
   / `RemoveOtherPilot(string pilotId)`: a marker per other pilot (create-or-move on upsert), plus
   an optional dashed route polyline per pilot when `waypoints` is non-null/non-empty (matching
   the original's white/blue-dot marker + per-pilot dashed `#3D7EEE` route line).
2. Marker click/tap handling: raise a C# `event EventHandler<string>? OtherPilotClicked` (passing
   the `pilotId`) when a pilot marker is tapped — wire this via Mapsui's `MapControl.Info`
   hit-testing event (or whatever the equivalent tap/click mechanism is on the version pinned).
   This directly replaces the original's `DotNetObjectReference`/`[JSInvokable] OnOtherPilotClicked`
   round-trip with a plain native event — simpler than what it replaces, not a regression.
3. `CenterOnAircraft()` / `ZoomIn()` / `ZoomOut()` / `PanTo(double lat, double lon)`: thin wrappers
   over Mapsui's `Navigator` (`CenterOn`/`ZoomIn`/`ZoomOut`) — `PanTo` should animate similarly to
   the original's 0.5 s animated pan if Mapsui's navigator supports an animation duration
   parameter, else a simple non-animated jump is an acceptable simplification, note if so.
4. Build — 0 errors, compare warnings to baseline.
5. Commit: `feat(map): other-pilot markers with click handling, pan/zoom/center controls`.

## Task 4 — `MapPage` skeleton: the `MapControl` + non-conditional HUD panels

Files: `AviatesAirTracker/Pages/MapPage.xaml` (new), `AviatesAirTracker/Pages/MapPage.xaml.cs` (new).

1. `MapPage : Page` hosting a `Mapsui.UI.WinUI.MapControl` filling the page, wrapping a
   `NativeMapRenderer` instance (from Tasks 1-3). Inject `MapViewModel` via
   `App.GetService<MapViewModel>()` (already DI-registered), plus `SimBriefService`/`SettingsService`/
   `FlightSessionManager`/`OtherPilotsService`/`MessagingService` as needed.
2. `Loaded` handler: `NativeMapRenderer.Init(VM.Lat, VM.Lon)`, subscribe to
   `MapViewModel.MapRenderRequested` (marshaled via `DispatcherQueue.TryEnqueue` — this fires from
   the telemetry pump thread, confirmed during research), `OtherPilotsService.PositionsUpdated`
   (same marshaling — fires from a background `Timer`), `SimBriefService.FlightPlanLoaded` (same
   marshaling). Wire `NativeMapRenderer.OtherPilotClicked` too, even though the pilot-detail panel
   it drives is Task 5's job — just get the subscription wired now so Task 5 doesn't have to touch
   this task's `Loaded` handler.
3. `Unloaded` handler: unsubscribe everything subscribed in `Loaded`.
4. `OnMapRenderRequested` equivalent: push `VM`'s current position/heading to
   `NativeMapRenderer.UpdatePosition`, call `NativeMapRenderer.UpdateFlightPathAltitude` only when
   `VM.GetPathIfChanged()` returns non-null, call `NativeMapRenderer.SetPlannedRoute` +
   `AddAirportMarker`×≤3 + `FitRouteBounds` only when `VM.ConsumePlanSync()` indicates the plan is
   dirty — matching the original's exact "only rebuild what actually changed" gating, don't
   rebuild everything on every tick.
5. **Identity strip** panel (top-left, draggable): route label + phase badge (color from
   `VM.PhaseColor`), then a grid of ALT/VS/IAS/GS/HDG/MACH bound to `VM`'s existing text
   properties.
6. **Progress strip** panel (bottom-left, draggable): NEXT waypoint / ETA / DIST REM / FUEL, bound
   to `VM`.
7. **Map controls** panel (bottom-right, **not** draggable, matching the original exactly): 3
   `Border`-buttons (center-on-aircraft / zoom-in / zoom-out) calling `NativeMapRenderer`'s
   corresponding methods.
8. **"No active flight" overlay**: centered text, `IsHitTestVisible="False"`, shown via
   `VM.HasActiveFlight` — bound live, not just checked once at load.
9. Dragging: implement a small reusable drag helper (`ManipulationDelta` + `TranslateTransform`,
   applied to the identity/progress panels in this task, and reusable by Task 5's plan/pilot-detail
   panels) rather than writing the drag logic 4 separate times.
10. Build — 0 errors. Check both XAML traps (double-hyphen comment, malformed hex color).
11. Commit: `feat(map): native page skeleton, map control, identity/progress/controls HUD panels`.

## Task 5 — Plan HUD, pilot-detail panel, SimBrief import, Messages navigation

Files: `AviatesAirTracker/Pages/MapPage.xaml`/`.xaml.cs` (modified), possibly
`AviatesAirTracker/Pages/MessagesPage.xaml.cs` (modified, for the parameter-passing navigation —
see point 4 below).

1. **Plan HUD** panel (top-right, draggable, reuse Task 4's drag helper): 4 states matching the
   original exactly — loading (spinner), loaded (DEP→ARR, aircraft type, waypoint count, a
   "Refresh OFP" `Border`-button), error-and-no-username (a "Configure" link/button navigating to
   the native `SettingsPage`), no-plan (an "Import OFP" `Border`-button). `ImportOfpAsync()`:
   `SimBriefService.FetchLatestOFPAsync(username)` → `FlightSessionManager.AssignSimBriefPlan(plan)`
   → `SimBriefService.NotifyPlanLoaded(plan)`. **Note the original does NOT persist this plan to
   disk** (`Settings.SaveSimBriefPlanAsync` is never called from `MapPage.razor`'s import, unlike
   ACARS's own import) — the design spec flagged this as a pre-existing inconsistency to leave
   alone for now (an ACARS-phase concern), so match the original's behavior here, don't fix it
   unilaterally.
2. **Pilot-detail panel** (top-right, offset from the plan HUD, conditional on a selected other
   pilot): name, phase badge, close `Border`, ALT/GS/HDG grid, DEP→ARR line, a "Message"
   `Border`-button. Populated when `NativeMapRenderer.OtherPilotClicked` fires (subscribed in Task
   4) — resolve the clicked pilot from `OtherPilotsService.LatestPositions` and show this panel.
   **The original has a real bug here** (confirmed during research): its drag handle exists in
   markup but `initDrag` is never actually called on it, so despite looking draggable it doesn't
   drag. Decide whether to wire real dragging (a small, justified improvement — this panel is
   structurally identical to the others that DO drag) or faithfully leave it non-draggable to
   match the original exactly — either is defensible, but state your choice explicitly in the
   commit message/report, don't silently pick one without saying so.
3. **Message button handler**: `MessagingService.AddOrGetFriendFromLiveMapAsync(pilotId, pilotName)`
   then navigate to the native `MessagesPage`.
4. **Messages navigation parameter**: `MessagesPage` currently has no mechanism to receive a
   "which friend to auto-select" parameter (this was explicitly out of scope when Messages was
   converted, since nothing called it yet). Add a minimal one: `this.Frame.Navigate(typeof(Pages.MessagesPage), pilotId)`
   from here, and in `MessagesPage.xaml.cs`, override `OnNavigatedTo(NavigationEventArgs e)` to
   check `e.Parameter as string` and, if present and matching a friend in `_viewModel.Friends`,
   switch to the Inbox tab and call the existing `SelectFriendAsync` flow (reuse whatever method
   `MessagesPage` already uses for this, don't duplicate it). This is a small, additive change to
   an already-shipped page — keep it minimal, don't refactor anything else in `MessagesPage` while
   you're in there.
5. Build — 0 errors, compare warnings to baseline (15 pre-existing, none referencing Map/Messages)
   — flag any new warning.
6. Commit: `feat(map): plan HUD, pilot-detail panel, SimBrief import, and Messages navigation`.

## Task 6 — Nav wiring and final verification

Files: `AviatesAirTracker/Navigation/NavDestinations.cs` (modified).

1. `"map"` entry gets `NativePageType: typeof(Pages.MapPage)` — one line, matching every prior
   page's pattern.
2. Build — 0 errors, compare warnings against baseline and flag anything new.
3. Verification pass (read-only code review, no computer-use/live-app testing needed unless the
   environment genuinely allows it and there's no sign of user activity — this plan's Task 1 spike
   already established whether live rendering verification is feasible in this environment; if it
   was, consider doing one more live check here since this is the riskiest plan in the migration
   so far): re-read the full diff across all 5 prior tasks (`git diff <Task-1-base-commit>..HEAD`)
   against the design spec. Confirm: the 3 dead JS functions (`resetPath`/`setFlightPath`/
   `invalidateSize`) were correctly NOT ported (no unused code cluttering `NativeMapRenderer`);
   the flown-path/planned-route rebuilds are gated on actual changes, not re-running every tick;
   `OtherPilotClicked` is a plain native event, not a JS-interop-shaped workaround; both XAML traps
   (double-hyphen, malformed hex) are clean on every touched file; the Map-controls panel is
   genuinely non-draggable (matching the original) while the other 3-4 panels are; the
   Messages-navigation parameter addition is minimal and doesn't disturb Messages' existing
   behavior when navigated to normally (without a parameter). Note explicitly whether this
   environment has live telemetry (MSFS connected) or a live backend connection with other pilots
   broadcasting — defer those specific functional checks explicitly if unavailable.
4. Commit: `feat(map): wire native MapPage into navigation`.

## Verification (after all six tasks land)

- Build clean, no new warnings.
- Visual: native map compared against the current Leaflet-based page — basemap, own-aircraft
  marker + rotation, altitude-colored flown path, planned route + waypoint markers, DEP/ARR/ALT
  pins, other-pilot markers + route lines, all 5 HUD panels in their correct drag/non-drag states.
- Functional: pan/zoom/center, other-pilot tap → detail panel → Message → navigates to Messages
  with the friend auto-selected, SimBrief import (all 4 states), "no active flight" overlay.
  Live-telemetry and live-backend-dependent checks deferred explicitly if this environment lacks
  MSFS/network/other broadcasting pilots, per the design spec.
