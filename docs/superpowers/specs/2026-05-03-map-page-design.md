# Map Page Design Spec
**Date:** 2026-05-03
**Feature:** Live Map Page (`/map`)
**Approach:** Extend existing infrastructure (Approach A)

---

## Overview

Replace the current `/map` redirect stub with a full-screen interactive flight map. The page shows the live aircraft position, planned SimBrief route, altitude-coloured flown path trail, labelled waypoints with next-fix highlight, and floating HUD cards for telemetry and progress data.

---

## Architecture

### Files Modified

| File | Change |
|---|---|
| `AviatesAirTracker/Pages/MapPage.razor` | Replace 3-line redirect stub with full-screen map layout + HUD card components |
| `AviatesAirTracker/ViewModels/MapViewModel.cs` | Wire up from stub: subscribe to SimBrief and telemetry events, expose all HUD properties |
| `AviatesAirTracker/wwwroot/js/interop.js` | Swap tile layer to CartoDB Dark Matter; add `updateFlightPathAltitude(points)` function |

No new files. No new services. No DI registration changes required.

### Data Flow

```
SimBriefService.FlightPlanLoaded
  → MapViewModel.OnFlightPlanLoaded()
      → JS: aviatesMap.setPlannedRoute(waypoints)
      → JS: aviatesMap.addAirportMarker(dep.lat, dep.lon, dep.icao, "departure")
      → JS: aviatesMap.addAirportMarker(arr.lat, arr.lon, arr.icao, "arrival")

FlightSessionManager.TelemetryUpdated (batched every 5 samples — same pattern as LiveFlightViewModel)
  → MapViewModel.UpdateTelemetry(snap)
      → JS: aviatesMap.updateAircraft(lat, lon, hdg)       [existing function]
      → JS: aviatesMap.updateFlightPathAltitude(pathPoints) [new function]
      → Updates HUD ObservableProperties → StateHasChanged()

SimConnectManager.ConnectionStatusChanged
  → MapViewModel: updates IsConnected, NoFlightText
```

### "No Flight" State

- Map centres on EGLL (lat 51.477, lon −0.461) at zoom level 5
- HUD telemetry values display `----`
- Phase badge shows `PARKED` in neutral grey
- If SimBrief plan loaded but no active flight: planned route and airport markers still render
- Status message: "Waiting for SimConnect…" when disconnected; "No Active Flight" when connected but idle

---

## MapViewModel Properties

```csharp
// Position & heading
[ObservableProperty] double _lat, _lon, _hdg;

// Telemetry HUD
[ObservableProperty] string _altText    = "----";
[ObservableProperty] string _vsText     = "----";
[ObservableProperty] string _iasText    = "----";
[ObservableProperty] string _gsText     = "----";
[ObservableProperty] string _hdgText    = "----";
[ObservableProperty] string _machText   = "----";

// Progress HUD
[ObservableProperty] string _routeLabel      = "----";
[ObservableProperty] string _nextWaypoint    = "----";
[ObservableProperty] string _etaZ            = "----";
[ObservableProperty] string _distRemText     = "----";
[ObservableProperty] string _fuelRemText     = "----";

// Phase badge
[ObservableProperty] string _phaseText  = "PARKED";
[ObservableProperty] string _phaseColor = "#4A5568";

// Status
[ObservableProperty] string _noFlightText = "Waiting for SimConnect…";
[ObservableProperty] bool   _hasActiveFlight;

// Page lifecycle gate — set true in OnAfterRenderAsync, false in DisposeAsync
// Prevents JS interop calls before the Leaflet map is mounted
bool MapReady { get; set; }
```

Subscriptions wired in constructor:
- `_simBriefSvc.FlightPlanLoaded += OnFlightPlanLoaded`
- `_session.TelemetryUpdated += OnTelemetryUpdated` (batch every 5 samples)
- `_simConnect.ConnectionStatusChanged += OnConnectionStatusChanged`

Path redraw guard: `updateFlightPathAltitude` is only called when the recorded path has grown since the last render (compare `_lastPathCount` to `session.CurrentFlight?.Path.Count`). This prevents rebuilding up to 600 polyline segments 4× per second when no new GPS points have been recorded (RouteTracker writes at 2 Hz, telemetry batches at 4 Hz).

---

## HUD Card Layout

All cards: `position: fixed`, semi-transparent (`rgba(10,13,23,0.88)`), `border: 1px solid rgba(61,126,238,0.25)`, `border-radius: 6px`, `backdrop-filter: blur(6px)`, `font-family: JetBrains Mono`. All draggable via the existing `aviatesTokDrag.initDrag` pattern.

### Card 1 — Flight Identity (top-left)
```
EGLL → LEMD                    [CRUISE]
────────────────────────────────────────
ALT    FL350      VS    +200 FPM
IAS    248 KT     GS    461 KT
HDG    193°       MACH  .78
```
- Route label: `{DepartureICAO} → {ArrivalICAO}`
- Phase badge colour matches existing `SessionStateColor` values from `MainViewModel`
- Telemetry grid: 2-column, 3 rows

### Card 2 — Progress (bottom-left)
```
NEXT  ETAMO        ETA   16:42Z
────────────────────────────────
DIST REM  612 NM      FUEL  8.4 T
```
- Next waypoint sourced from SimBrief `Waypoints` list, advanced as aircraft passes each fix
- ETA calculated from remaining distance ÷ current ground speed
- Fuel in metric tonnes (kg ÷ 1000), rounded to 1 decimal

### Card 3 — Map Controls (bottom-right)
Three stacked icon buttons:
- `✈` — centre map on aircraft (`aviatesMap.panTo(lat, lon)`)
- `+` — zoom in
- `−` — zoom out

---

## Altitude Trail — JS Implementation

### New function: `window.aviatesMap.updateFlightPathAltitude(points)`

```js
// points: [{lat, lon, alt}]  — alt in feet
updateFlightPathAltitude(points) {
  // Remove previous altitude path layer if exists
  if (this._altPathLayer) this._map.removeLayer(this._altPathLayer);

  if (!points || points.length < 2) return;

  var renderer = L.canvas();
  var group = L.layerGroup().addTo(this._map);
  this._altPathLayer = group;

  for (var i = 0; i < points.length - 1; i++) {
    var p1 = points[i], p2 = points[i + 1];
    var midAlt = (p1.alt + p2.alt) / 2;
    L.polyline(
      [[p1.lat, p1.lon], [p2.lat, p2.lon]],
      { color: altToColor(midAlt), weight: 3, opacity: 0.85, renderer: renderer }
    ).addTo(group);
  }
}

function altToColor(altFt) {
  if (altFt <= 0)   return 'hsl(0,0%,90%)';
  if (altFt <= 100) return 'hsl(0,0%,90%)';   // white band near ground
  var t   = Math.min((altFt - 100) / (45000 - 100), 1.0);
  var hue = Math.round(270 * (1 - t));         // 270° violet → 0° red
  return 'hsl(' + hue + ',55%,42%)';           // dimmed: sat 55%, lit 42%
}
```

The existing `setFlightPath()` function is **not removed** — it remains for other callers — but `MapPage.razor` exclusively uses `updateFlightPathAltitude()`.

### Tile Layer Change (in `aviatesMap.init`)

```js
// Replace existing OpenStreetMap tile URL with:
L.tileLayer('https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png', {
  attribution: '© <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors © <a href="https://carto.com/">CARTO</a>',
  subdomains: 'abcd',
  maxZoom: 19
}).addTo(this._map);
```

No API key required. CartoDB Dark Matter is free and open.

---

## Colour Reference — Altitude Scale

| Altitude | Colour | HSL |
|---|---|---|
| 0 – 100 ft | White | `hsl(0, 0%, 90%)` |
| 100 ft | Violet | `hsl(270, 55%, 42%)` |
| ~11,250 ft | Blue | `hsl(240, 55%, 42%)` |
| ~22,500 ft | Green | `hsl(150, 55%, 42%)` — midpoint hue |
| ~33,750 ft | Yellow | `hsl(60, 55%, 42%)` |
| ~40,000 ft | Orange | `hsl(30, 55%, 42%)` |
| ≥ 45,000 ft | Red (clamped) | `hsl(0, 55%, 42%)` |

Formula: `hue = 270 × (1 − clamp((alt − 100) / 44900, 0, 1))`

---

## Waypoint Rendering

- Waypoints sourced from `SimBriefFlightPlan.Waypoints` (already parsed by `SimBriefService`)
- Rendered as small cyan dots on the planned route line via `L.circleMarker`
- Labels displayed at zoom ≥ 7 only (hidden at lower zoom to avoid clutter)
- Next waypoint: highlighted in amber (`#EAB308`) with `▶` prefix in the HUD card
- Waypoint advance logic: when aircraft lat/lon is within 5 NM of the current next waypoint, advance to the following one

---

## MapPage.razor Structure

```razor
@page "/map"
@inject MapViewModel VM
@inject IJSRuntime JS
@implements IAsyncDisposable

<!-- Full-screen Leaflet container -->
<div id="map-container" style="position:absolute;inset:0;z-index:0;"></div>

<!-- HUD Card: Flight Identity + Telemetry -->
<div class="map-hud map-hud--top-left" id="hud-identity">
  ...route label, phase badge, telemetry grid...
</div>

<!-- HUD Card: Progress -->
<div class="map-hud map-hud--bottom-left" id="hud-progress">
  ...next waypoint, ETA, dist rem, fuel...
</div>

<!-- HUD Card: Controls -->
<div class="map-hud map-hud--bottom-right" id="hud-controls">
  ...centre, zoom+, zoom- buttons...
</div>

<!-- No-flight overlay (shown when !VM.HasActiveFlight && !SimBrief loaded) -->
@if (!VM.HasActiveFlight) {
  <div class="map-no-flight">@VM.NoFlightText</div>
}

@code {
  protected override async Task OnAfterRenderAsync(bool firstRender) {
    if (firstRender) {
      await JS.InvokeVoidAsync("aviatesMap.init", VM.Lat, VM.Lon);
      await JS.InvokeVoidAsync("aviatesTokDrag.initDrag", "hud-identity");
      await JS.InvokeVoidAsync("aviatesTokDrag.initDrag", "hud-progress");
      VM.MapReady = true;
    }
  }

  public async ValueTask DisposeAsync() {
    VM.MapReady = false;
    await JS.InvokeVoidAsync("aviatesMap.destroy");
  }
}
```

---

## Out of Scope

- Weather overlay (future backlog item)
- Other traffic / TCAS display
- Custom VOR/airway overlays (Aviation Chart style — not chosen)
- Terrain shading
- Mobile / touch-specific controls (app is Windows desktop only)
