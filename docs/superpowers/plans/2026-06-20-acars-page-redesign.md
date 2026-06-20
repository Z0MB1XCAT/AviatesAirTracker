# ACARS Page Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Overhaul the ACARS page to Option C two-column layout with 7 new features, fix the phase display bug, embed a live mini map, replace the FMC button with a keyboard shortcut, and add SimBrief import by numeric pilot ID.

**Architecture:** All changes are self-contained within the existing Blazor Hybrid project — no new services or DI registrations required beyond a new `AutoPirepIntervalMinutes` field on `AppSettings`. The embedded mini map uses a second Leaflet instance (`aviatesMapMini`) separate from the `/map` singleton to avoid conflicts. The FMC is triggered via a JS `keydown` listener rather than a page button.

**Tech Stack:** Blazor Hybrid (.NET 8), Razor pages, Leaflet.js (CDN), RestSharp, Newtonsoft.Json, `System.Timers.Timer`

**Spec:** `docs/superpowers/specs/2026-06-20-acars-page-redesign.md`

---

## Files Modified

| File | What changes |
|------|-------------|
| `AviatesAirTracker/Models/FlightModels.cs` | Add `OFPText` and `NOTAMs` to `SimBriefFlightPlan` |
| `AviatesAirTracker/Services/SimBriefService.cs` | userid/username detection; parse OFP text + NOTAMs |
| `AviatesAirTracker/Services/SupportServices.cs` | Add `AutoPirepIntervalMinutes` to `AppSettings` |
| `AviatesAirTracker/Services/AcarsPositionService.cs` | Expose `LastCheckAt`; add `PositionReported` event |
| `AviatesAirTracker/wwwroot/js/interop.js` | Add `aviatesMapMini` object; extend `acarsInterop` |
| `AviatesAirTracker/Pages/Acars.razor` | Full rewrite — layout, all features, all fixes |
| `AviatesAirTracker/Pages/Settings.razor` | Interval selector, FMC hint, SimBrief label |

---

## Task 1: SimBriefFlightPlan — Add OFPText and NOTAMs fields

**Files:**
- Modify: `AviatesAirTracker/Models/FlightModels.cs`

- [ ] **Step 1: Add two properties to SimBriefFlightPlan**

Find the `SimBriefFlightPlan` class in `FlightModels.cs`. Add these two properties in the existing block of `public string` properties (after `ArrivalMetar` works well):

```csharp
public string OFPText { get; set; } = "";
public List<string> NOTAMs { get; set; } = new();
```

- [ ] **Step 2: Verify build**

```powershell
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add AviatesAirTracker/Models/FlightModels.cs
git commit -m "feat(models): add OFPText and NOTAMs fields to SimBriefFlightPlan"
```

---

## Task 2: SimBriefService — userid/username detection + OFP text + NOTAM parsing

**Files:**
- Modify: `AviatesAirTracker/Services/SimBriefService.cs`

- [ ] **Step 1: Update `FetchLatestOFPAsync` for userid/username detection**

In `SimBriefService.cs`, replace the `FetchLatestOFPAsync` method's request building block. Find:

```csharp
var request = new RestRequest(SIMBRIEF_API_BASE)
    .AddParameter("username", username)
    .AddParameter("json", "1");
```

Replace with:

```csharp
bool isNumericId = username.Trim().All(char.IsDigit);
var request = new RestRequest(SIMBRIEF_API_BASE)
    .AddParameter(isNumericId ? "userid" : "username", username.Trim())
    .AddParameter("json", "1");
```

- [ ] **Step 2: Capture OFP text in `ParseSimBriefJson`**

At the end of `ParseSimBriefJson`, before `return plan;`, add:

```csharp
// OFP full text (may be empty if SimBrief omits it)
plan.OFPText = root["text"]?.ToString() ?? "";
```

- [ ] **Step 3: Parse NOTAMs in `ParseSimBriefJson`**

Immediately after the OFP text line, add:

```csharp
// NOTAMs
var notamToken = root["notams"]?["notam"];
if (notamToken is JArray notamArr)
    plan.NOTAMs = notamArr
        .Select(n => n["body"]?.ToString() ?? n.ToString())
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .ToList();
```

- [ ] **Step 4: Verify build**

```powershell
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 5: Commit**

```bash
git add AviatesAirTracker/Services/SimBriefService.cs
git commit -m "feat(simbrief): support numeric pilot ID; parse OFP text and NOTAMs"
```

---

## Task 3: AppSettings + AcarsPositionService — configurable interval and event

**Files:**
- Modify: `AviatesAirTracker/Services/SupportServices.cs`
- Modify: `AviatesAirTracker/Services/AcarsPositionService.cs`

- [ ] **Step 1: Add `AutoPirepIntervalMinutes` to `AppSettings`**

In `SupportServices.cs`, find the `AppSettings` class (line ~302). Add after `FriendCode`:

```csharp
// Interval (minutes) between automatic ACARS position reports. 0 = off.
public int AutoPirepIntervalMinutes { get; set; } = 5;
```

- [ ] **Step 2: Expose `LastCheckAt` and `PositionReported` event on `AcarsPositionService`**

In `AcarsPositionService.cs`, add two public members after the private fields:

```csharp
/// <summary>Last time the 30-second gate fired (not necessarily when an HTTP call went out).</summary>
public DateTime LastCheckAt => _lastCheck;

/// <summary>Raised each time the outer 30-second gate fires (true = HTTP call was attempted).</summary>
public event Action<bool>? GateFired;
```

In `OnTelemetryUpdated`, immediately after `_lastCheck = DateTime.UtcNow;`, add:

```csharp
GateFired?.Invoke(true);
```

- [ ] **Step 3: Verify build**

```powershell
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add AviatesAirTracker/Services/SupportServices.cs AviatesAirTracker/Services/AcarsPositionService.cs
git commit -m "feat(settings): add AutoPirepIntervalMinutes; expose AcarsPositionService gate event"
```

---

## Task 4: JavaScript — aviatesMapMini + FMC keyboard shortcut

**Files:**
- Modify: `AviatesAirTracker/wwwroot/js/interop.js`

- [ ] **Step 1: Add `aviatesMapMini` object**

Open `interop.js`. Before the final closing line (or after the last `window.xxx = ...` block), add:

```javascript
// ── ACARS page mini map (separate Leaflet instance, no conflict with aviatesMap) ──
window.aviatesMapMini = (function () {
    let _map = null;
    let _marker = null;
    const DARK_TILE = 'https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png';
    const TILE_ATTR = '&copy; <a href="https://www.openstreetmap.org/copyright">OSM</a> &copy; <a href="https://carto.com/">CARTO</a>';

    function _arrowIcon(hdg) {
        const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="28" height="28"
                         style="transform:rotate(${hdg}deg);transform-origin:center;">
            <polygon points="12,2 8,20 12,16 16,20" fill="#3D7EEE" stroke="#fff" stroke-width="1.2"/>
        </svg>`;
        return L.divIcon({ html: svg, className: '', iconAnchor: [14, 14] });
    }

    return {
        init(lat, lon) {
            if (_map) this.destroy();
            const el = document.getElementById('acars-mini-map');
            if (!el) return;
            _map = L.map(el, { zoomControl: false, attributionControl: false })
                    .setView([lat || 51.5, lon || -0.1], 8);
            L.tileLayer(DARK_TILE, { attribution: TILE_ATTR, maxZoom: 18 }).addTo(_map);
            _marker = L.marker([lat || 51.5, lon || -0.1], { icon: _arrowIcon(0) }).addTo(_map);
        },
        update(lat, lon, hdg) {
            if (!_map || !_marker) return;
            const ll = [lat, lon];
            _marker.setLatLng(ll);
            _marker.setIcon(_arrowIcon(hdg));
            _map.panTo(ll, { animate: true, duration: 1 });
        },
        destroy() {
            if (_map) { _map.remove(); _map = null; _marker = null; }
        }
    };
})();
```

- [ ] **Step 2: Extend `acarsInterop` with FMC keyboard shortcut methods**

Find the existing `window.acarsInterop = {` block in `interop.js` (line ~269). Add two new methods inside the object, before its closing `};`:

```javascript
initFmcShortcut(dotNetRef) {
    this._fmcRef = dotNetRef;
    this._fmcHandler = (e) => {
        if (e.ctrlKey && e.shiftKey && (e.key === 'F' || e.key === 'f')) {
            e.preventDefault();
            dotNetRef.invokeMethodAsync('OpenFmc');
        }
    };
    document.addEventListener('keydown', this._fmcHandler);
},
disposeFmcShortcut() {
    if (this._fmcHandler) {
        document.removeEventListener('keydown', this._fmcHandler);
        this._fmcHandler = null;
    }
    if (this._fmcRef) {
        this._fmcRef.dispose();
        this._fmcRef = null;
    }
},
```

- [ ] **Step 3: Commit** (no build needed — JS is not compiled)

```bash
git add AviatesAirTracker/wwwroot/js/interop.js
git commit -m "feat(js): add aviatesMapMini Leaflet instance and FMC keyboard shortcut handler"
```

---

## Task 5: ACARS page — Option C layout, phase fix, mini map wiring, FMC shortcut

**Files:**
- Modify: `AviatesAirTracker/Pages/Acars.razor`

This task replaces the entire page structure and `@code` block. The existing panels (booking banner, aircraft strip, progress strip, METAR, route, PIREP, end-flight modal) carry over unchanged — only their *position* in the layout changes.

- [ ] **Step 1: Replace the page header — remove the FMC button**

Replace the entire `<div class="page-header ...">` block (lines 14–23 in the current file) with:

```razor
<div class="page-header anim-up">
    <div>
        <h1 class="page-title">ACARS / Tracking</h1>
        <p class="page-sub">Real-time flight tracking, telemetry, and ACARS communications. Connect MSFS to go live.</p>
    </div>
</div>
```

The `<AcarsFmc>` component line stays immediately after the header (no change).

- [ ] **Step 2: Add Zulu clock to the connection banner**

In the connection banner `<div>` (the one with `style="background:var(--surface);border:1.5px solid ..."`), add the clock display. Find the `<div style="flex:1;min-width:0;">` block and add the clock sibling before the Connect/LIVE buttons:

```razor
<div style="font-family:var(--font-mono);font-size:13px;font-weight:700;color:var(--text-muted);letter-spacing:0.04em;flex-shrink:0;">
    @_utcNow
</div>
```

- [ ] **Step 3: Replace the 1fr/300px map+panel grid with Option C layout**

Find and replace the entire `<!-- Map + right panel -->` section (currently lines 199–356):

```razor
<!-- Two-column body: left = mini map, right = sticky sidebar -->
<div style="display:grid;grid-template-columns:1fr 300px;gap:var(--sp-5);" class="anim-up-d3">

    <!-- Left: compact Leaflet mini map -->
    <div style="position:relative;height:220px;border-radius:var(--r-xl);overflow:hidden;border:1px solid var(--border);">
        <div id="acars-mini-map" style="width:100%;height:100%;background:var(--surface-2);"></div>
        @if (!_miniMapInitialized)
        {
            <div style="position:absolute;inset:0;display:flex;align-items:center;justify-content:center;color:var(--text-muted);font-size:12px;">
                @(_simStatus == SimConnectionStatus.Connected ? "Initialising map…" : "Connect MSFS to show map")
            </div>
        }
        <a href="/map"
           style="position:absolute;bottom:10px;right:10px;background:rgba(10,13,23,0.85);border:1px solid rgba(61,126,238,0.4);border-radius:var(--r-md);padding:5px 12px;font-size:11px;font-weight:600;color:var(--accent);text-decoration:none;backdrop-filter:blur(4px);">
            ↗ Full Map
        </a>
    </div>

    <!-- Right: sticky sidebar -->
    <div style="display:flex;flex-direction:column;gap:var(--sp-4);align-self:start;position:sticky;top:16px;">

        <!-- SimBrief / Flight Plan card — UNCHANGED internals from current file -->
        <div class="panel-card">
            @* ... copy the existing Flight Plan card content here unchanged ... *@
        </div>

        <!-- Phase tracker — 7 steps -->
        <div class="panel-card">
            <div class="panel-header" style="margin-bottom:var(--sp-4);">
                <span class="panel-title">Flight Phase</span>
            </div>
            <div style="display:flex;flex-direction:column;gap:var(--sp-2);">
                @foreach (var phase in new[] { "Parked", "Taxi", "Climb", "Cruise", "Descent", "Approach", "Landing" })
                {
                    var (chipCls, textCls) = PhaseStatus(phase);
                    <div style="display:flex;align-items:center;gap:12px;padding:6px 12px;border-radius:var(--r-md);background:@(chipCls == "active" ? "rgba(61,126,238,0.08)" : "var(--surface-2)");">
                        <span class="status-chip @chipCls" style="padding:0;background:none;"><span class="chip-dot"></span></span>
                        <span style="font-size:12px;font-weight:@(chipCls == "active" ? "700" : "500");color:@textCls;">@phase</span>
                    </div>
                }
            </div>
        </div>

        <!-- ACARS key card — UNCHANGED from current file -->
        <div class="panel-card">
            @* ... copy existing ACARS key card content unchanged ... *@
        </div>

    </div>
</div>
```

> **Note:** Copy the existing Flight Plan card and ACARS Key card HTML verbatim from the current file into the marked placeholders above.

- [ ] **Step 4: Replace the 2×3 instrument grid (add GS and MACH, replace N1 and FUEL)**

Find the `<!-- Instrument row -->` section. Replace with:

```razor
<!-- Instrument row: 2 rows of 3 -->
<div class="instrument-row anim-up-d2">
    <div class="instrument-card">
        <div class="instrument-label">Altitude MSL</div>
        <div class="instrument-value" style="color:@(_snap != null ? "var(--text-primary)" : "var(--text-muted)");">@FmtAlt()</div>
        <div class="instrument-unit">ft</div>
    </div>
    <div class="instrument-card">
        <div class="instrument-label">Airspeed IAS</div>
        <div class="instrument-value" style="color:@(_snap != null ? "var(--text-primary)" : "var(--text-muted)");">@FmtIas()</div>
        <div class="instrument-unit">kts</div>
    </div>
    <div class="instrument-card">
        <div class="instrument-label">Vertical Speed</div>
        <div class="instrument-value" style="color:@VsColor();">@FmtVs()</div>
        <div class="instrument-unit">fpm</div>
    </div>
    <div class="instrument-card">
        <div class="instrument-label">Heading</div>
        <div class="instrument-value" style="color:@(_snap != null ? "var(--text-primary)" : "var(--text-muted)");">@FmtHdg()</div>
        <div class="instrument-unit">°</div>
    </div>
    <div class="instrument-card">
        <div class="instrument-label">Ground Speed</div>
        <div class="instrument-value" style="color:@(_snap != null ? "var(--text-primary)" : "var(--text-muted)");">@FmtGs()</div>
        <div class="instrument-unit">kts</div>
    </div>
    <div class="instrument-card">
        <div class="instrument-label">Mach</div>
        <div class="instrument-value" style="color:@(_snap != null ? "var(--text-primary)" : "var(--text-muted)");font-size:28px;">@FmtMach()</div>
        <div class="instrument-unit"></div>
    </div>
</div>
```

- [ ] **Step 5: Update the `@code` block — new fields**

Add these fields at the top of the `@code` section, alongside existing fields:

```csharp
// Mini map
private bool _miniMapInitialized;
private DotNetObjectReference<Acars>? _dotNetRef;

// Zulu clock
private string _utcNow = DateTime.UtcNow.ToString("HH:mm:ss") + "Z";
private System.Timers.Timer? _clockTimer;
```

- [ ] **Step 6: Update `@code` — new injections**

Add these `@inject` directives at the top of the file (after existing injects):

```razor
@inject AviatesAirTracker.Core.Analytics.FlightPhaseDetector PhaseDetector
@inject AviatesAirTracker.Services.AcarsPositionService AcarsPositionSvc
@inject AviatesAirTracker.Core.Backend.AviatesBackendClient BackendClient
```

- [ ] **Step 7: Update `OnInitialized` — start clock, subscribe to phase events**

Extend the existing `OnInitialized` method:

```csharp
protected override void OnInitialized()
{
    _simStatus    = SimConnect.ConnectionStatus;
    _snap         = Session.LatestTelemetry;
    _sessionState = Session.State;

    SimConnect.ConnectionStatusChanged += OnConnectionStatusChanged;
    SimConnect.AircraftIdentReceived   += OnAircraftIdentReceived;
    Session.TelemetryUpdated           += OnTelemetryUpdated;
    Session.SessionStateChanged        += OnSessionStateChanged;
    PhaseDetector.PhaseChanged         += OnPhaseChanged;   // NEW

    // Zulu clock
    _clockTimer = new System.Timers.Timer(1000);
    _clockTimer.Elapsed += (_, _) =>
    {
        _utcNow = DateTime.UtcNow.ToString("HH:mm:ss") + "Z";
        InvokeAsync(StateHasChanged);
    };
    _clockTimer.AutoReset = true;
    _clockTimer.Start();
}
```

- [ ] **Step 8: Update `OnAfterRenderAsync` — init mini map + FMC shortcut**

Replace or extend `OnAfterRenderAsync`. If it doesn't exist yet, add it. If `OnInitializedAsync` already has some logic, keep it and add this:

```csharp
protected override async Task OnAfterRenderAsync(bool firstRender)
{
    if (!firstRender) return;

    _dotNetRef = DotNetObjectReference.Create(this);
    await JS.InvokeVoidAsync("acarsInterop.initFmcShortcut", _dotNetRef);

    var lat = _snap?.Latitude ?? 51.5;
    var lon = _snap?.Longitude ?? -0.1;
    try
    {
        await JS.InvokeVoidAsync("aviatesMapMini.init", lat, lon);
        _miniMapInitialized = true;
        StateHasChanged();
    }
    catch { /* Leaflet not ready yet — will retry on next telemetry update */ }
}
```

- [ ] **Step 9: Update `OnTelemetryUpdated` — push mini map updates**

Replace the existing `OnTelemetryUpdated` method:

```csharp
private void OnTelemetryUpdated(object? _, TelemetrySnapshot snap)
{
    _snap = snap;
    if (++_sampleCounter < UI_UPDATE_SAMPLES) return;
    _sampleCounter = 0;
    _ = InvokeAsync(async () =>
    {
        if (_disposed) return;
        StateHasChanged();
        if (_miniMapInitialized)
        {
            try { await JS.InvokeVoidAsync("aviatesMapMini.update", snap.Latitude, snap.Longitude, snap.Raw.HeadingMagnetic); }
            catch { /* page may be navigating away */ }
        }
    });
}
```

- [ ] **Step 10: Update `DisposeAsync` — dispose clock, shortcuts, mini map**

Replace the existing `DisposeAsync`:

```csharp
public async ValueTask DisposeAsync()
{
    _disposed = true;
    _clockTimer?.Dispose();
    _metarTimer?.Dispose();

    SimConnect.ConnectionStatusChanged -= OnConnectionStatusChanged;
    SimConnect.AircraftIdentReceived   -= OnAircraftIdentReceived;
    Session.TelemetryUpdated           -= OnTelemetryUpdated;
    Session.SessionStateChanged        -= OnSessionStateChanged;
    PhaseDetector.PhaseChanged         -= OnPhaseChanged;

    try { await JS.InvokeVoidAsync("acarsInterop.disposeFmcShortcut"); } catch { }
    if (_miniMapInitialized)
        try { await JS.InvokeVoidAsync("aviatesMapMini.destroy"); } catch { }
    _dotNetRef?.Dispose();
}
```

- [ ] **Step 11: Add `[JSInvokable] OpenFmc` method**

Add to the `@code` block:

```csharp
[Microsoft.JSInterop.JSInvokable]
public void OpenFmc()
{
    _showFmc = true;
    InvokeAsync(StateHasChanged);
}
```

- [ ] **Step 12: Fix `PhaseStatus` — 7-step tracker with Descent**

Replace the existing `PhaseStatus` method:

```csharp
private (string chipClass, string textColor) PhaseStatus(string phaseName)
{
    var phaseOrder = new[] { "Parked", "Taxi", "Climb", "Cruise", "Descent", "Approach", "Landing" };

    int currentIdx = _sessionState switch
    {
        FlightSessionState.Idle or FlightSessionState.PreFlight => 0,
        FlightSessionState.Taxiing => 1,
        FlightSessionState.Airborne when _snap?.Phase is FlightPhase.Cruise => 3,
        FlightSessionState.Airborne when _snap?.Phase is FlightPhase.TopOfDescent or FlightPhase.Descent => 4,
        FlightSessionState.Airborne => 2,   // Takeoff / InitialClimb / Climb
        FlightSessionState.OnApproach => 5,
        FlightSessionState.Landed or FlightSessionState.Complete => 6,
        _ => -1
    };

    var idx = System.Array.IndexOf(phaseOrder, phaseName);
    if (currentIdx == -1 || idx > currentIdx) return ("offline", "var(--text-muted)");
    if (idx == currentIdx) return ("active", "var(--text-primary)");
    return ("done", "var(--text-muted)");
}
```

- [ ] **Step 13: Add `FmtMach` helper**

Add alongside the other `Fmt*` helpers:

```csharp
private string FmtMach() => _snap != null ? $".{(_snap.Raw.Mach * 100):F0}" : "—";
```

- [ ] **Step 14: Verify build**

```powershell
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 15: Commit**

```bash
git add AviatesAirTracker/Pages/Acars.razor
git commit -m "feat(acars): Option C layout, 7-step phase tracker, mini map, FMC keyboard shortcut (Ctrl+Shift+F)"
```

---

## Task 6: ACARS page — NOTAM section + OFP text viewer

**Files:**
- Modify: `AviatesAirTracker/Pages/Acars.razor`

These two panels go inside the existing `@if (_flightPlan != null)` block, after the METAR cards and before the PIREP panel.

- [ ] **Step 1: Add NOTAM section panel**

After the closing `</div>` of the METAR cards grid, add:

```razor
<!-- NOTAM section -->
@if (_flightPlan.NOTAMs.Count > 0)
{
    <details class="panel-card" style="cursor:default;">
        <summary style="list-style:none;display:flex;align-items:center;justify-content:space-between;cursor:pointer;padding:0;">
            <span class="panel-title">NOTAMS (@_flightPlan.NOTAMs.Count)</span>
            <svg viewBox="0 0 24 24" fill="none" stroke="var(--text-muted)" stroke-width="2" width="14" height="14" class="notam-caret">
                <polyline points="6 9 12 15 18 9"/>
            </svg>
        </summary>
        <div style="margin-top:var(--sp-3);display:flex;flex-direction:column;gap:6px;">
            @foreach (var notam in _flightPlan.NOTAMs)
            {
                <div style="display:flex;gap:10px;align-items:flex-start;">
                    <span style="width:6px;height:6px;border-radius:50%;background:var(--amber,#f59e0b);flex-shrink:0;margin-top:5px;"></span>
                    <pre style="font-family:var(--font-mono);font-size:10px;color:var(--text-secondary);margin:0;white-space:pre-wrap;word-break:break-all;line-height:1.6;">@notam</pre>
                </div>
            }
        </div>
    </details>
}
```

- [ ] **Step 2: Add OFP text viewer panel**

After the NOTAM section, add:

```razor
<!-- OFP text viewer -->
@if (!string.IsNullOrWhiteSpace(_flightPlan.OFPText))
{
    <details class="panel-card" style="cursor:default;">
        <summary style="list-style:none;display:flex;align-items:center;justify-content:space-between;cursor:pointer;padding:0;">
            <span class="panel-title">OFP TEXT</span>
            <svg viewBox="0 0 24 24" fill="none" stroke="var(--text-muted)" stroke-width="2" width="14" height="14">
                <polyline points="6 9 12 15 18 9"/>
            </svg>
        </summary>
        <div style="margin-top:var(--sp-3);background:var(--surface-2);border-radius:var(--r-md);padding:10px 14px;max-height:400px;overflow-y:auto;">
            <pre style="font-family:var(--font-mono);font-size:10px;color:var(--text-secondary);margin:0;white-space:pre-wrap;line-height:1.7;">@_flightPlan.OFPText</pre>
        </div>
    </details>
}
```

- [ ] **Step 3: Verify build**

```powershell
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add AviatesAirTracker/Pages/Acars.razor
git commit -m "feat(acars): add NOTAM section and OFP text viewer panels"
```

---

## Task 7: ACARS page — Flight event log

**Files:**
- Modify: `AviatesAirTracker/Pages/Acars.razor`

- [ ] **Step 1: Add event log field to `@code`**

```csharp
private readonly List<(DateTime At, string Message)> _eventLog = new();
```

- [ ] **Step 2: Add `OnPhaseChanged` handler to `@code`**

```csharp
private void OnPhaseChanged(object? _, FlightPhaseChangedEvent evt)
{
    var label = evt.Current switch
    {
        FlightPhase.Takeoff       => $"Takeoff · {evt.AltitudeAGL:F0} ft AGL",
        FlightPhase.InitialClimb  => $"Initial Climb · {evt.AltitudeMSL:F0} ft",
        FlightPhase.Climb         => $"Climbing · {evt.AltitudeMSL:F0} ft",
        FlightPhase.Cruise        => $"Cruise · FL{(int)(evt.AltitudeMSL / 100):D3}",
        FlightPhase.TopOfDescent  => $"Top of Descent · FL{(int)(evt.AltitudeMSL / 100):D3}",
        FlightPhase.Descent       => $"Descending · {evt.AltitudeMSL:F0} ft",
        FlightPhase.Approach      => $"Approach · {evt.AltitudeAGL:F0} ft AGL",
        FlightPhase.FinalApproach => "Final Approach",
        FlightPhase.Landing       => "Touchdown",
        FlightPhase.Rollout       => "Rollout",
        FlightPhase.Vacating      => "Vacating Runway",
        FlightPhase.Parked        => "Parked",
        FlightPhase.Taxi          => "Taxiing",
        _                         => evt.Current.ToString()
    };

    if (_eventLog.Count >= 50) _eventLog.RemoveAt(_eventLog.Count - 1);
    _eventLog.Insert(0, (DateTime.UtcNow, label));
    InvokeAsync(StateHasChanged);
}
```

- [ ] **Step 3: Add event log panel to HTML**

At the very bottom of the `@if (_flightPlan != null)` block (after the PIREP panel), add:

```razor
<!-- ACARS Flight Event Log -->
@if (_eventLog.Count > 0)
{
    <div class="panel-card">
        <div class="panel-header" style="margin-bottom:var(--sp-3);">
            <span class="panel-title">ACARS Event Log</span>
            <span style="font-size:11px;color:var(--text-muted);">This session</span>
        </div>
        <div style="display:flex;flex-direction:column;gap:4px;max-height:240px;overflow-y:auto;">
            @foreach (var (at, msg) in _eventLog)
            {
                <div style="display:flex;align-items:baseline;gap:12px;padding:5px 0;border-bottom:1px solid var(--border-light);">
                    <span style="font-family:var(--font-mono);font-size:10px;color:var(--text-muted);flex-shrink:0;min-width:46px;">@at.ToString("HH:mm")Z</span>
                    <span style="font-size:12px;color:var(--text-secondary);">@msg</span>
                </div>
            }
        </div>
    </div>
}
```

- [ ] **Step 4: Verify build**

```powershell
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 5: Commit**

```bash
git add AviatesAirTracker/Pages/Acars.razor
git commit -m "feat(acars): add ACARS flight event log panel"
```

---

## Task 8: ACARS page — Position report (submit wiring, history, auto-status) + cruise compliance check

**Files:**
- Modify: `AviatesAirTracker/Pages/Acars.razor`

- [ ] **Step 1: Add position report fields to `@code`**

```csharp
private record PirepRecord(DateTime SentAt, string Position, int AltFt, string Phase, bool Success);
private readonly List<PirepRecord> _pirepHistory = new();
private bool _pirepSending;
private string? _pirepSendStatus;   // "sent" | "failed" | null
```

- [ ] **Step 2: Add `SubmitPositionReport` method**

```csharp
private async Task SubmitPositionReport()
{
    if (_snap == null) return;
    var key = Settings.Settings.AcarsKey.Trim();
    if (string.IsNullOrEmpty(key)) return;

    _pirepSending    = true;
    _pirepSendStatus = null;
    StateHasChanged();

    bool success = false;
    try
    {
        await BackendClient.SendPositionReportAsync(
            _snap.Latitude, _snap.Longitude,
            _snap.AltitudePressure, (int)_snap.GroundSpeedKts,
            _snap.Phase.ToString(), key);
        success          = true;
        _pirepSendStatus = "sent";
    }
    catch
    {
        _pirepSendStatus = "failed";
    }
    finally
    {
        _pirepSending = false;
        if (_pirepHistory.Count >= 20) _pirepHistory.RemoveAt(_pirepHistory.Count - 1);
        _pirepHistory.Insert(0, new PirepRecord(
            DateTime.UtcNow, FormatCoords(), (int)_snap.AltitudeMSL,
            _snap.Phase.ToString(), success));
        StateHasChanged();
    }
}
```

- [ ] **Step 3: Add cruise compliance helpers**

```csharp
private string CruiseMachStatus()
{
    if (_snap == null || _flightPlan == null || _flightPlan.CruiseMach <= 0) return "ok";
    var diff = Math.Abs(_snap.Raw.Mach - _flightPlan.CruiseMach);
    return diff > 0.05 ? "red" : diff > 0.02 ? "amber" : "ok";
}

private string CruiseAltStatus()
{
    if (_snap == null || _flightPlan == null || _flightPlan.CruiseAltitudeFt <= 0) return "ok";
    var diff = Math.Abs(_snap.AltitudeMSL - _flightPlan.CruiseAltitudeFt);
    return diff > 2000 ? "red" : diff > 1000 ? "amber" : "ok";
}

private string StatusColor(string status) => status switch
{
    "red"   => "var(--red,#ef4444)",
    "amber" => "var(--amber,#f59e0b)",
    _       => "var(--green,#22c55e)"
};

private string StatusIcon(string status) => status == "ok" ? "✓" : "⚠";
```

- [ ] **Step 4: Replace the "Submit Position Report" button in the PIREP panel HTML**

Find the existing disabled submit button:

```razor
<button disabled style="background:var(--surface-2);border:1px solid var(--border);...cursor:not-allowed;"
        title="Available when Aviates backend is connected">
    Submit Position Report
</button>
<span style="font-size:11px;color:var(--text-muted);margin-left:auto;">Backend pending</span>
```

Replace with:

```razor
<button @onclick="SubmitPositionReport" disabled="@(_pirepSending || string.IsNullOrEmpty(Settings.Settings.AcarsKey))"
        class="btn btn-primary btn-sm"
        title="@(string.IsNullOrEmpty(Settings.Settings.AcarsKey) ? "Configure your ACARS key in Settings first" : "Send position report now")">
    @(_pirepSending ? "Sending…" : _pirepSendStatus == "sent" ? "✓ Sent" : _pirepSendStatus == "failed" ? "✗ Failed — Retry" : "Submit Position Report")
</button>
```

- [ ] **Step 5: Add auto-reporting status line**

Directly after the submit button row, add:

```razor
<div style="font-size:11px;color:var(--text-muted);margin-top:6px;display:flex;align-items:center;gap:6px;">
    <span style="width:6px;height:6px;border-radius:50%;background:@(_sessionState is FlightSessionState.Airborne or FlightSessionState.OnApproach ? "var(--green,#22c55e)" : "var(--border)");display:inline-block;flex-shrink:0;"></span>
    @if (_sessionState is FlightSessionState.Airborne or FlightSessionState.OnApproach)
    {
        var nextIn = (int)(5 - (DateTime.UtcNow - AcarsPositionSvc.LastCheckAt).TotalMinutes);
        <span>Auto-reporting active · checks every 30s · next HTTP report in ~@(Math.Max(0, nextIn))m</span>
    }
    else
    {
        <span>Auto-reporting starts on takeoff</span>
    }
</div>
```

- [ ] **Step 6: Add position report history panel**

After the PIREP panel closing div, add:

```razor
<!-- Position Report History -->
@if (_pirepHistory.Count > 0)
{
    <div class="panel-card">
        <div class="panel-header" style="margin-bottom:var(--sp-3);">
            <span class="panel-title">Position Report History</span>
            <span style="font-size:11px;color:var(--text-muted);">This session</span>
        </div>
        <div style="overflow-x:auto;">
            <table style="width:100%;border-collapse:collapse;font-size:11px;font-family:var(--font-mono);">
                <thead>
                    <tr style="color:var(--text-muted);font-size:10px;text-transform:uppercase;letter-spacing:0.08em;">
                        <th style="text-align:left;padding:4px 8px 6px 0;">Time</th>
                        <th style="text-align:left;padding:4px 8px 6px 0;">Position</th>
                        <th style="text-align:right;padding:4px 0 6px 8px;">Alt</th>
                        <th style="text-align:left;padding:4px 8px 6px 8px;">Phase</th>
                        <th style="text-align:center;padding:4px 0 6px 0;">Status</th>
                    </tr>
                </thead>
                <tbody>
                    @foreach (var r in _pirepHistory)
                    {
                        <tr style="border-top:1px solid var(--border-light);">
                            <td style="padding:5px 8px 5px 0;color:var(--text-muted);">@r.SentAt.ToString("HH:mm")Z</td>
                            <td style="padding:5px 8px 5px 0;color:var(--text-secondary);">@r.Position</td>
                            <td style="padding:5px 0 5px 8px;text-align:right;color:var(--text-primary);">@r.AltFt.ToString("N0")</td>
                            <td style="padding:5px 8px 5px 8px;color:var(--text-muted);">@r.Phase</td>
                            <td style="padding:5px 0 5px 0;text-align:center;color:@(r.Success ? "var(--green,#22c55e)" : "var(--red,#ef4444)");">
                                @(r.Success ? "✓" : "✗")
                            </td>
                        </tr>
                    }
                </tbody>
            </table>
        </div>
    </div>
}
```

- [ ] **Step 7: Add cruise compliance check panel**

After the position report history and before the event log, add:

```razor
<!-- Cruise compliance check — only during cruise phase -->
@if (_flightPlan != null && _snap?.Phase is FlightPhase.Cruise)
{
    var machSt = CruiseMachStatus();
    var altSt  = CruiseAltStatus();
    <div class="panel-card">
        <div class="panel-header" style="margin-bottom:var(--sp-3);">
            <span class="panel-title">Cruise Compliance</span>
            <span style="font-size:11px;color:var(--text-muted);">vs SimBrief OFP</span>
        </div>
        <div style="display:grid;grid-template-columns:1fr 1fr;gap:var(--sp-3);">
            <div style="background:var(--surface-2);border:1px solid @StatusColor(machSt)44;border-radius:var(--r-md);padding:10px 14px;">
                <div style="font-size:10px;font-weight:700;letter-spacing:0.1em;color:var(--text-muted);text-transform:uppercase;margin-bottom:4px;">Mach</div>
                <div style="display:flex;align-items:baseline;gap:8px;">
                    <span style="font-family:var(--font-mono);font-size:18px;font-weight:800;color:@StatusColor(machSt);">@StatusIcon(machSt)</span>
                    <div>
                        <div style="font-family:var(--font-mono);font-size:13px;font-weight:700;color:var(--text-primary);">M@(_snap!.Raw.Mach.ToString("F2"))</div>
                        <div style="font-size:10px;color:var(--text-muted);">Plan: M@(_flightPlan.CruiseMach.ToString("F2"))</div>
                    </div>
                </div>
            </div>
            <div style="background:var(--surface-2);border:1px solid @StatusColor(altSt)44;border-radius:var(--r-md);padding:10px 14px;">
                <div style="font-size:10px;font-weight:700;letter-spacing:0.1em;color:var(--text-muted);text-transform:uppercase;margin-bottom:4px;">Altitude</div>
                <div style="display:flex;align-items:baseline;gap:8px;">
                    <span style="font-family:var(--font-mono);font-size:18px;font-weight:800;color:@StatusColor(altSt);">@StatusIcon(altSt)</span>
                    <div>
                        <div style="font-family:var(--font-mono);font-size:13px;font-weight:700;color:var(--text-primary);">FL@(((int)_snap!.AltitudeMSL / 100):D3)</div>
                        <div style="font-size:10px;color:var(--text-muted);">Plan: FL@(_flightPlan.CruiseAltitudeFt / 100:D3)</div>
                    </div>
                </div>
            </div>
        </div>
    </div>
}
```

- [ ] **Step 8: Verify build**

```powershell
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 9: Commit**

```bash
git add AviatesAirTracker/Pages/Acars.razor
git commit -m "feat(acars): wire position report submit, add history, auto-reporting status, and cruise compliance check"
```

---

## Task 9: Settings page — interval selector, FMC hint, SimBrief label

**Files:**
- Modify: `AviatesAirTracker/Pages/Settings.razor`

- [ ] **Step 1: Update SimBrief label**

In `Settings.razor`, find the SimBrief username label/input. Change the label from `"SimBrief Pilot ID"` or `"SimBrief Username"` (whatever the current text is) to:

```razor
<label class="form-label">SimBrief Username or Pilot ID</label>
<input class="form-input" type="text"
       @bind="SettingsService.Settings.SimBriefUsername"
       placeholder="johndoe or 12345" />
<div style="font-size:11px;color:var(--text-muted);margin-top:4px;">Enter your SimBrief username (e.g. johndoe) or numeric Pilot ID (e.g. 123456).</div>
```

- [ ] **Step 2: Add FMC shortcut hint to the ACARS section**

Find the ACARS Key section in `Settings.razor`. After the existing `<a href="/settings" ...>Configure ACARS</a>` link or at the bottom of the ACARS card, add:

```razor
<div style="margin-top:12px;padding-top:10px;border-top:1px solid var(--border-light);font-size:11px;color:var(--text-muted);">
    Tip: Open the ACARS FMC with
    <kbd style="background:var(--surface-2);border:1px solid var(--border);border-radius:3px;padding:1px 5px;font-family:var(--font-mono);font-size:10px;">Ctrl+Shift+F</kbd>
</div>
```

- [ ] **Step 3: Add auto-PIREP interval selector**

Find an appropriate ACARS settings card in `Settings.razor`. Add the interval selector alongside other ACARS settings:

```razor
<div class="form-group">
    <label class="form-label">Auto Position Report Interval</label>
    <select class="form-input" @bind="SettingsService.Settings.AutoPirepIntervalMinutes">
        <option value="0">Off</option>
        <option value="5">Every 5 minutes</option>
        <option value="10">Every 10 minutes</option>
        <option value="15">Every 15 minutes</option>
        <option value="30">Every 30 minutes</option>
    </select>
    <div style="font-size:11px;color:var(--text-muted);margin-top:4px;">How often position reports are automatically sent while airborne.</div>
</div>
```

> **Note:** `AppSettings.AutoPirepIntervalMinutes` was added in Task 3. The interval controls display/UX only for now — wiring to the `AcarsPositionService` check interval is a future enhancement since the service uses a compile-time constant. The setting stores user intent and can be wired in a follow-up.

- [ ] **Step 4: Verify build**

```powershell
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```

Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 5: Commit**

```bash
git add AviatesAirTracker/Pages/Settings.razor
git commit -m "feat(settings): SimBrief label, FMC shortcut hint, auto-PIREP interval selector"
```

---

## Manual Smoke Test Checklist

After all tasks are complete, run the app with `.\build.ps1 -Run` and verify:

- [ ] ACARS page loads without errors — two-column layout visible
- [ ] Six instrument cards show ALT/IAS/VS/HDG/GS/MACH (no N1 or FUEL card)
- [ ] UTC clock ticks every second in the connection banner
- [ ] Phase tracker shows 7 steps; with MSFS connected and climbing, "Climb" is active; while descending at cruise altitude, "Descent" is active (not "Climb")
- [ ] `Ctrl+Shift+F` opens the FMC window; no button visible on page
- [ ] "Open Map ↗" button in mini map corner navigates to `/map`
- [ ] SimBrief import with a numeric pilot ID fetches the OFP (uses `?userid=` parameter)
- [ ] SimBrief import with a username string still works (uses `?username=` parameter)
- [ ] If OFP has NOTAMs, the NOTAM panel appears collapsed; expanding shows each NOTAM
- [ ] If OFP has text, the OFP text viewer panel appears collapsed; expanding shows the raw briefing
- [ ] "Submit Position Report" button is enabled when ACARS key is configured and sends a report
- [ ] After submitting, a row appears in the Position Report History table
- [ ] Settings page shows "SimBrief Username or Pilot ID" label with helper text
- [ ] Settings page shows FMC keyboard shortcut hint in ACARS section
- [ ] Settings page shows auto-PIREP interval dropdown
