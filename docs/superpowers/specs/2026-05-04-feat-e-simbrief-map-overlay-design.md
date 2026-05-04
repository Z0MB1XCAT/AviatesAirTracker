# FEAT-E: SimBrief → Map Route Overlay Pipeline

**Date:** 2026-05-04  
**Status:** Approved  
**Scope:** Fix broken notification chain, centralise map interaction on `/map`, add SimBrief HUD panel

---

## Problem Statement

Three separate issues share a single root cause:

1. **Map page never receives SimBrief plan** — the planned route overlay never appears on `/map`
2. **TakeoffPerformanceModal never appears** — the T-15 min pre-departure briefing never triggers
3. **Duplicate map on ACARS** — `/acars` has its own embedded Leaflet map competing with `/map`

**Root cause:** `Acars.razor.ImportSimBrief()` calls `Session.AssignSimBriefPlan(plan)` but never calls `SimBriefSvc.NotifyPlanLoaded(plan)`. Because `SimBriefSvc.FlightPlanLoaded` is never raised:
- `MapViewModel.OnFlightPlanLoaded` never fires → Map page never gets the plan
- `TakeoffPerformanceService.OnFlightPlanLoaded` never fires → timer never scheduled → modal never appears

---

## Design

### Decision: Option B — Map on `/map`, ACARS loses its Leaflet map

The embedded Leaflet map is removed from `Acars.razor` and replaced with an "Open Map" navigation link. All interactive map work lives exclusively on `/map`. The SimBrief import button stays on both pages (ACARS for pre-flight briefing context, Map page for in-flight use). The broken notification chain is fixed so importing from either page propagates to all subscribers.

---

## Changes

### 1. `Acars.razor` — notification fix + map removal

**Notification fix (one line):**

In `ImportSimBrief()`, after `Session.AssignSimBriefPlan(plan)`, add:
```csharp
SimBriefSvc.NotifyPlanLoaded(plan);
```
This fires `SimBriefSvc.FlightPlanLoaded`, which unblocks both `MapViewModel` and `TakeoffPerformanceService`.

**Map removal:**

Remove the following from `Acars.razor`:
- The `<div id="leaflet-map">` container and its overlay controls HTML (~45 lines)
- All `aviatesMap.*` JS interop calls in: `ImportSimBrief()`, `ClearPlan()`, `OnTelemetryUpdated()`, `OnAfterRenderAsync()`, `DisposeAsync()`
- Fields: `_mapInitialized`, `_followAircraft`
- Methods: `CenterOnAircraft()`, `FitRoute()`
- All map-related CSS

**Add "Open Map" card** in place of the removed map:

A compact styled card containing a route summary and a primary "Open Map →" button that navigates to `/map`.

### 2. `MapPage.razor` — new SimBrief HUD panel

**New panel:** `hud--tr` (top-right), matching the existing dark-glass HUD card style. Draggable via `aviatesTokDrag.initDrag`.

**Three states:**

| State | Content |
|-------|---------|
| No plan | "No Flight Plan" label + "IMPORT OFP" button (or "Configure SimBrief" link if no username set) |
| Loading | Spinner + "FETCHING OFP…" |
| Plan loaded | Route label (`EGLL → EGKK`), waypoint count, "REFRESH OFP" ghost button |

**New injections:** `SimBriefService`, `SettingsService`, `FlightSessionManager`

**Import method flow:**
1. `SimBriefSvc.FetchLatestOFPAsync(username)`
2. `Session.AssignSimBriefPlan(plan)` — wires plan into current flight record
3. `SimBriefSvc.NotifyPlanLoaded(plan)` — fires event → `MapViewModel.OnFlightPlanLoaded` → `_planNeedsSync = true` → next render tick calls `PushPlannedRouteAsync` → route appears on map

**New CSS:** `.hud--tr { top: 12px; right: 12px; }`

---

## Event Chain (after fix)

```
SimBrief import (Acars OR MapPage)
  → SimBriefSvc.FetchLatestOFPAsync()
  → Session.AssignSimBriefPlan(plan)       [wires PIREP data]
  → SimBriefSvc.NotifyPlanLoaded(plan)     [fires FlightPlanLoaded event]
      ├─ MapViewModel.OnFlightPlanLoaded   [sets _planNeedsSync, fires MapRenderRequested]
      │     └─ MapPage.PushPlannedRouteAsync()
      │           ├─ aviatesMap.clearAirportMarkers()
      │           ├─ aviatesMap.setPlannedRoute(waypoints)
      │           ├─ aviatesMap.addAirportMarker(dep / arr / alt)
      │           └─ aviatesMap.fitRouteBounds()
      └─ TakeoffPerformanceService.OnFlightPlanLoaded
            └─ ScheduleTimer(ETD − 15 min)
                  └─ BriefingTriggered → TakeoffPerformanceModal appears
```

---

## Out of Scope

- **BUG-3 audio:** `aviatesAudio.playDepartureAlert` already has a synthesised three-tone chime fallback (C5→E5→G5 via Web Audio API). The modal will play the chime once it appears. No audio file changes needed.
- `MapViewModel.cs` — correct as-is, no changes
- `TakeoffPerformanceService.cs` — correct as-is, no changes
- `interop.js` — correct as-is, no changes
- `FlightSessionManager.cs` — correct as-is, no changes

---

## Files Changed

| File | Change |
|------|--------|
| `AviatesAirTracker/Pages/Acars.razor` | Add `NotifyPlanLoaded`, remove Leaflet map, add "Open Map" card |
| `AviatesAirTracker/Pages/MapPage.razor` | Add `hud--tr` SimBrief import panel |
