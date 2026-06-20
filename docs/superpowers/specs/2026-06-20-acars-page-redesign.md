# ACARS Page Redesign — Design Spec
**Date:** 2026-06-20  
**Scope:** `AviatesAirTracker/Pages/Acars.razor`, `AviatesAirTracker/Shared/AcarsFmc.razor`, `AviatesAirTracker/Services/SimBriefService.cs`, `AviatesAirTracker/Models/FlightModels.cs`, `AviatesAirTracker/wwwroot/js/interop.js`, `AviatesAirTracker/Pages/Settings.razor`

---

## Goals

Overhaul the ACARS page from its current "all over the place" state into a coherent, modern airline dispatch/tracking interface. Fix broken behaviours (phase display, SimBrief import), remove a misplaced UI element (FMC button), embed a live map, and add a suite of airline-grade features.

---

## Layout — Option C (Two-Column)

### Top area (full-width, always visible)

1. **Connection banner** — status dot · live Zulu clock (ticking, seconds) · session state label · LIVE chip + End Flight button when airborne
2. **Active booking banner** — callsign, route, aircraft, SimBrief hint, dismiss button (unchanged from current)
3. **Aircraft ident strip** — model, callsign, flight number, GPS active leg, coordinates (unchanged)
4. **2×3 Instrument grid** — six cards: ALT / IAS / VS (row 1) · HDG / GS / MACH (row 2). GS and MACH are new additions. All formatted the same as current cards.
5. **Flight progress strip** — visible only when OFP loaded (unchanged, sits below instruments)

### Two-column body

```
┌── LEFT COLUMN (1fr) ──────────┬── RIGHT SIDEBAR (300px, sticky top:16px) ──┐
│  Compact map (220px tall)      │  Flight Plan card                           │
│  + "↗ Full Map" corner button  │  Phase tracker (7 steps)                   │
│                                │  ACARS key card                             │
└────────────────────────────────┴────────────────────────────────────────────┘
```

Right sidebar uses `align-self: start; position: sticky; top: 16px`.

### Full-width panels (below the two-column body, only when OFP loaded)

In order, top to bottom:
1. Route strip (waypoints, SID/STAR)
2. METAR cards — DEP / ARR / ALT (existing, with live refresh)
3. **NOTAM section** (collapsible, collapsed by default)
4. **OFP Text Viewer** (collapsible, collapsed by default)
5. **Cruise Compliance Check** (visible only during Cruise phase)
6. Position Report panel (with remarks textarea)
7. **Position Report History** (below the send button)
8. **ACARS Flight Event Log**

---

## Core Fixes

### 1. Flight Phase Accuracy

**Problem:** `PhaseStatus()` in `Acars.razor` maps `FlightSessionState.Airborne` → display index 2 ("Climb") for everything except `FlightPhase.Cruise` or `FlightPhase.TopOfDescent`. Descent shows as Climb.

**Fix:** Replace the 6-step display (Parked/Taxi/Climb/Cruise/Approach/Landing) with a **7-step tracker**: Parked → Taxi → Climb → Cruise → Descent → Approach → Landing.

New `PhaseStatus()` index mapping:
- `Idle / PreFlight` → 0 (Parked)
- `Taxiing` → 1 (Taxi)
- `Airborne` + phase is `Climb / InitialClimb / Takeoff` → 2 (Climb)
- `Airborne` + phase is `Cruise / TopOfDescent` → 3 (Cruise)
- `Airborne` + phase is `Descent` → 4 (Descent)  ← **new**
- `OnApproach` or phase is `Approach / FinalApproach` → 5 (Approach)
- `Landed / Complete` → 6 (Landing)

### 2. SimBrief Import by Username or Numeric Pilot ID

**Problem:** `FetchLatestOFPAsync` always uses `?username=`. SimBrief also supports `?userid=` for numeric pilot IDs.

**Fix in `SimBriefService.FetchLatestOFPAsync`:**
```csharp
bool isNumericId = username.All(char.IsDigit);
var request = new RestRequest(SIMBRIEF_API_BASE)
    .AddParameter(isNumericId ? "userid" : "username", username)
    .AddParameter("json", "1");
```

**Fix in Settings UI:** Change label from "Pilot ID" to "SimBrief Username or Pilot ID" with helper text: "Enter your SimBrief username (e.g. johndoe) or numeric Pilot ID."

### 3. FMC Button Removal + Keyboard Shortcut

**Remove:** The `fmc-launch-btn` button block (Acars.razor lines 19–22) and its associated CSS class.

**Add keyboard shortcut:**
- In `OnAfterRenderAsync(firstRender)`, call `JS.InvokeVoidAsync("acarsInterop.initFmcShortcut", DotNetObjectReference.Create(this))`
- Add `[JSInvokable] public void OpenFmc() => _showFmc = true; InvokeAsync(StateHasChanged);`
- In `interop.js`, extend the existing `window.acarsInterop` object (already defined at line 269) with `initFmcShortcut(dotNetRef)` and `disposeFmcShortcut()`. The shortcut handler calls `dotNetRef.invokeMethodAsync('OpenFmc')`. Remove listener on page dispose via `disposeFmcShortcut()`.

**Settings hint:** Add a small muted line at the bottom of the ACARS Key card in Settings: *"Tip: Open ACARS FMC with Ctrl+Shift+F"*

### 4. Embedded Compact Map

**Problem:** The current "map area" is just a link card. User wants a live map visible on the ACARS page.

**Approach:** Add a new `aviatesMapMini` JS object in `interop.js` — a completely separate Leaflet instance attached to `<div id="acars-mini-map">`. This avoids any conflict with the existing `aviatesMap` singleton used by `/map`.

**Mini map behaviour:**
- Initialised with `aviatesMapMini.init(lat, lon)` in `OnAfterRenderAsync(firstRender)`
- Updated with `aviatesMapMini.update(lat, lon, hdg)` on every 5th telemetry sample (same cadence as the rest of the ACARS UI)
- Shows: aircraft position marker with heading arrow, dark tile layer (same as `/map`)
- Does NOT show route overlay (that remains `/map` only)
- "↗ Full Map" button overlaid bottom-right corner of the map div, links to `/map`
- `aviatesMapMini.destroy()` called in `DisposeAsync()`

**Map div dimensions:** `height: 220px; border-radius: var(--r-xl); overflow: hidden`

### 5. Submit Position Report — Wire Up

The "Submit Position Report" button is permanently disabled. `AcarsPositionService` already auto-sends position reports every ~5 minutes while airborne (via a 30s polling gate + 5-min inner throttle in `AviatesBackendClient`). The manual button should call `AviatesBackendClient.SendPositionReportAsync(lat, lon, altPressure, gs, phase, acarsKey)` directly — bypassing the 5-min throttle — so pilots can force an immediate report. Remove `disabled` attribute and "Backend pending" label.

---

## New Features

### Feature 1: Live Zulu Clock

- Component field: `string _utcNow = ""`
- `System.Timers.Timer _clockTimer` — 1-second interval, starts in `OnInitializedAsync`
- Each tick: `_utcNow = DateTime.UtcNow.ToString("HH:mm:ss") + "Z"`, `InvokeAsync(StateHasChanged)`
- Displayed inline in the connection banner next to the status dot
- Timer disposed in `DisposeAsync()`

### Feature 2: ACARS Flight Event Log

- Field: `List<(DateTime At, string Message)> _eventLog = new()`
- Subscribe to `FlightPhaseDetector.PhaseChanged` event (inject `FlightPhaseDetector` into ACARS page)
- Each phase change appends: `(DateTime.UtcNow, $"{evt.Current} · {evt.AltitudeAGL:F0} ft AGL")`
- Also append session state changes via `Session.SessionStateChanged` for "Engines started", "Takeoff", "Landed" milestones
- Displayed below the PIREP panel as a reverse-chronological list (newest first), max 50 entries
- Cleared on `DisposeAsync` (session-scoped)
- Format: `HH:mmZ   Phase label   · detail`

### Feature 3: Full OFP Text Viewer

**Model change — `SimBriefFlightPlan`:**
```csharp
public string OFPText { get; set; } = "";
```

**Parser change — `ParseSimBriefJson`:**
```csharp
plan.OFPText = root["text"]?.ToString() ?? "";
```

**UI:** Collapsible `<details>` panel below the NOTAM section. When expanded, shows a scrollable `<pre>` block (`max-height: 400px; overflow-y: auto`) in `var(--font-mono)` at 11px. Label shows "OFP TEXT" with a caret toggle.

### Feature 4: Cruise Speed / Altitude Check

- Visible only when `_flightPlan != null` and `_snap?.Phase is FlightPhase.Cruise`
- Displayed as a compact two-row status card inside (or directly below) the Flight Plan card in the right sidebar
- **Mach check:** `|currentMach - _flightPlan.CruiseMach| > 0.02` → amber; `> 0.05` → red
- **Altitude check:** `|currentAlt - _flightPlan.CruiseAltitudeFt| > 1000` → amber; `> 2000` → red
- Current Mach from `_snap.Raw.Mach` (`AIRSPEED MACH` SimConnect variable, confirmed in `SimConnectDefinitions.cs`)
- Green tick with "On Plan" when both within tolerance

### Feature 5: Automatic Position Reporting — Expose Existing Service

`AcarsPositionService` already auto-sends position reports while airborne (30-second polling gate, 5-minute HTTP throttle inside `AviatesBackendClient.SendPositionReportAsync`). No new timer is needed.

**What to add:**
- A status line in the PIREP panel: "Auto-reporting active · next report in ~Xm" — computed from `AcarsPositionService`'s `_lastCheck` timestamp
- **New settings field:** `int AutoPirepIntervalMin { get; set; } = 5` (maps to `AcarsPositionService.CHECK_INTERVAL_SECONDS` — make this constant configurable via settings). Options: 5 / 10 / 15 / 30 min.
- Inject `AcarsPositionService` into the ACARS page to read its last-check time for the UI countdown
- Each auto-report that fires appends to `_pirepHistory` (see Feature 6) by wiring a callback or event on `AcarsPositionService`

### Feature 6: Position Report History

- Field: `List<PirepRecord> _pirepHistory = new()` where `PirepRecord` is a local `record` with `SentAt`, `PositionString`, `AltFt`, `Phase`, `bool Success`
- Populated by both manual and auto submissions
- Displayed as a compact table below the "Copy PIREP Text" button: columns `TIME · POSITION · ALT · PHASE · STATUS`
- Status: green "✓ Sent" or red "✗ Failed"
- Max 20 entries per session (oldest removed when full)

### Feature 7: NOTAM Section

**Model change — `SimBriefFlightPlan`:**
```csharp
public List<string> NOTAMs { get; set; } = new();
```

**Parser change — `ParseSimBriefJson`:**
```csharp
var notamsToken = root["notams"]?["notam"];
if (notamsToken is JArray notamArr)
    plan.NOTAMs = notamArr.Select(n => n["body"]?.ToString() ?? n.ToString()).ToList();
```

**UI:** Collapsible panel (`<details>`) below the METAR cards. Collapsed by default. When expanded, each NOTAM is shown in its own monospace row with a small `●` amber indicator. Panel header shows NOTAM count: "NOTAMS (4)". Hidden entirely if `NOTAMs.Count == 0`.

---

## Files Changed

| File | Changes |
|------|---------|
| `Pages/Acars.razor` | Full layout rewrite (Option C), all 7 new features, core fixes 1–4, mini map wiring |
| `Shared/AcarsFmc.razor` | Remove `fmc-launch-btn` CSS; no logic changes |
| `Services/SimBriefService.cs` | Numeric userid detection in `FetchLatestOFPAsync`; capture `OFPText` and `NOTAMs` in parser |
| `Models/FlightModels.cs` | Add `OFPText`, `NOTAMs` to `SimBriefFlightPlan` |
| `wwwroot/js/interop.js` | Add `aviatesMapMini` object; add `acarsInterop.initFmcShortcut` |
| `Pages/Settings.razor` | Label change, auto-PIREP interval selector, FMC shortcut hint |

---

## Out of Scope

- Changes to `/map` page (MapPage.razor) — only the mini map is added to ACARS
- `AcarsFmc.razor` internal pages — FMC content is unchanged; only the launch mechanism changes
- Backend changes — position reporting uses existing `AviatesBackendClient` endpoints
- Website repo — no changes
