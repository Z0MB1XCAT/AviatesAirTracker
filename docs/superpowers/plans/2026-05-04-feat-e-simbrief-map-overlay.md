# FEAT-E: SimBrief → Map Route Overlay Pipeline — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the broken SimBrief notification chain so the planned route appears on `/map` and the TakeoffPerformanceModal triggers at T-15 min; strip the duplicate Leaflet map from ACARS and add a SimBrief import HUD panel to the Map page.

**Architecture:** A single missing `SimBriefSvc.NotifyPlanLoaded(plan)` call in `Acars.razor.ImportSimBrief()` is the root cause of two broken features — `MapViewModel` and `TakeoffPerformanceService` both subscribe to `SimBriefSvc.FlightPlanLoaded` but it is never raised. Task 1 adds that call. Task 2 strips the now-redundant Leaflet map from ACARS and replaces it with an "Open Map" link. Task 3 adds a draggable SimBrief import HUD panel to `/map` so pilots can load plans without visiting ACARS first.

**Tech Stack:** Blazor Hybrid (Razor pages), C# 12, Leaflet.js via `IJSRuntime`, CommunityToolkit.Mvvm. No automated tests — verification is visual via the running app.

---

## File Map

| File | Task | Change |
|------|------|--------|
| `AviatesAirTracker/Pages/Acars.razor` | 1, 2 | Add `NotifyPlanLoaded`; strip map HTML + JS + methods |
| `AviatesAirTracker/Pages/MapPage.razor` | 3 | Add `hud--tr` SimBrief import panel |

---

### Task 1: Add `NotifyPlanLoaded` — fix the root cause

**Files:**
- Modify: `AviatesAirTracker/Pages/Acars.razor`

- [ ] **Step 1: Find the insertion point**

Open `AviatesAirTracker/Pages/Acars.razor`. Search for `Session.AssignSimBriefPlan(plan)`. It is inside the `ImportSimBrief()` private method.

- [ ] **Step 2: Add the notification call**

Immediately after `Session.AssignSimBriefPlan(plan)`, add one line:

```csharp
_flightPlan = plan;

Session.AssignSimBriefPlan(plan);
SimBriefSvc.NotifyPlanLoaded(plan);   // ← add this line

if (_mapInitialized)
{
```

- [ ] **Step 3: Build**

```powershell
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```

Expected: `Build succeeded. 0 Error(s).`

- [ ] **Step 4: Smoke-test both downstream effects**

Run the app (`.\build.ps1 -Run`):
1. Navigate to `/acars` → click **Import Latest OFP**
2. Navigate to `/map` — the dashed planned-route line and coloured airport markers (green DEP, amber ARR) must now appear on the map
3. If the loaded OFP has `ScheduledDepartureUtc` within the next 15 minutes, the TakeoffPerformanceModal should pop up automatically

- [ ] **Step 5: Commit**

```powershell
git add AviatesAirTracker/Pages/Acars.razor
git commit -m "fix(acars): fire NotifyPlanLoaded after SimBrief import — unblocks map overlay and TakeoffPerformanceModal"
```

---

### Task 2: Strip the Leaflet map from Acars.razor, add "Open Map" card

**Files:**
- Modify: `AviatesAirTracker/Pages/Acars.razor`

All map HTML in this file uses inline styles — no CSS class cleanup is needed.

- [ ] **Step 1: Remove the map HTML block (lines ~201–237)**

Delete the entire section from `<!-- Interactive Leaflet map -->` through the closing `</div>` of its outer wrapper (the wrapper that also contains the overlay controls div and the no-flight overlay div). Replace it with this card:

```razor
<!-- Map moved to /map -->
<div class="acars-map-link-card">
    <div class="acars-map-link-info">
        <span class="acars-map-link-title">ROUTE MAP</span>
        <span class="acars-map-link-sub">Live position &amp; planned route overlay</span>
    </div>
    <a href="/map" class="acars-map-open-btn">Open Map ↗</a>
</div>
```

- [ ] **Step 2: Remove `aviatesMap.init` from `OnAfterRenderAsync`**

Find `OnAfterRenderAsync`. Delete these lines (the try/catch block that initialises the map):

```csharp
try
{
    await JS.InvokeVoidAsync("aviatesMap.init", lat, lon);
    _mapInitialized = true;

    if (_snap != null)
        await JS.InvokeVoidAsync("aviatesMap.updatePosition",
            _snap.Latitude, _snap.Longitude, _snap.Raw.HeadingTrue);
}
catch { /* WebView not ready on first paint */ }
```

- [ ] **Step 3: Remove `aviatesMap.destroy` from `DisposeAsync`**

Find `DisposeAsync`. Delete:

```csharp
if (_mapInitialized)
{
    try { await JS.InvokeVoidAsync("aviatesMap.destroy"); } catch { }
}
```

- [ ] **Step 4: Remove map calls from `OnTelemetryUpdated`**

Find the telemetry handler (the lambda subscribed to `Session.TelemetryUpdated`). Delete the `_mapInitialized` guard block:

```csharp
if (_mapInitialized && _snap != null)
{
    try
    {
        await JS.InvokeVoidAsync("aviatesMap.updatePosition",
            _snap.Latitude, _snap.Longitude, _snap.Raw.HeadingTrue);
        if (_followAircraft)
            await JS.InvokeVoidAsync("aviatesMap.panTo",
                _snap.Latitude, _snap.Longitude);
    }
    catch { /* Ignore if map torn down during navigation */ }
}
```

- [ ] **Step 5: Remove map calls from `ImportSimBrief()`**

Inside `ImportSimBrief()`, after the `SimBriefSvc.NotifyPlanLoaded(plan)` line added in Task 1, delete the entire `if (_mapInitialized)` block (it clears markers, sets the planned route, adds airport markers, and calls `fitRouteBounds`):

```csharp
if (_mapInitialized)
{
    await JS.InvokeVoidAsync("aviatesMap.clearAirportMarkers");
    // ... all the setPlannedRoute / addAirportMarker / fitRouteBounds calls ...
    await JS.InvokeVoidAsync("aviatesMap.fitRouteBounds");
}
```

- [ ] **Step 6: Remove map calls from `ClearPlan()`**

Inside `ClearPlan()`, delete the `if (_mapInitialized)` block:

```csharp
if (_mapInitialized)
{
    await JS.InvokeVoidAsync("aviatesMap.clearAirportMarkers");
    await JS.InvokeVoidAsync("aviatesMap.setPlannedRoute", Array.Empty<object>());
}
```

- [ ] **Step 7: Delete the four map-control methods**

Delete these methods entirely:

```csharp
private void ToggleFollow() => _followAircraft = !_followAircraft;

private async Task CenterOnAircraft() { ... }

private async Task FitRoute() { ... }
```

(The `// ── Map controls ─────` comment block can be deleted with them.)

- [ ] **Step 8: Remove the two map state fields**

Delete:

```csharp
private bool _mapInitialized;
private bool _followAircraft = true;
```

- [ ] **Step 9: Add the "Open Map" card CSS**

In the `<style>` block at the bottom of `Acars.razor`, add:

```css
/* ── Open Map card ───────────────────────────────── */
.acars-map-link-card {
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: var(--r-xl);
    padding: 20px 24px;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 16px;
}
.acars-map-link-info {
    display: flex;
    flex-direction: column;
    gap: 4px;
}
.acars-map-link-title {
    font-family: var(--font-mono);
    font-size: 11px;
    font-weight: 700;
    letter-spacing: 0.12em;
    color: var(--text-primary);
}
.acars-map-link-sub {
    font-size: 12px;
    color: var(--text-muted);
}
.acars-map-open-btn {
    display: inline-flex;
    align-items: center;
    padding: 8px 18px;
    background: var(--accent);
    color: #fff;
    border-radius: var(--r-md);
    font-size: 12px;
    font-weight: 600;
    text-decoration: none;
    letter-spacing: 0.02em;
    transition: opacity 0.15s;
    white-space: nowrap;
}
.acars-map-open-btn:hover  { opacity: 0.85; }
.acars-map-open-btn:active { opacity: 0.7;  }
```

- [ ] **Step 10: Build**

```powershell
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```

Expected: `Build succeeded. 0 Error(s).`

- [ ] **Step 11: Verify**

Run the app:
- `/acars` shows the "ROUTE MAP / Open Map ↗" card where the Leaflet map used to be
- "Open Map ↗" navigates to `/map`
- SimBrief import, METAR display, and the waypoint strip below the layout all still work
- No browser console errors about `aviatesMap` not defined

- [ ] **Step 12: Commit**

```powershell
git add AviatesAirTracker/Pages/Acars.razor
git commit -m "feat(acars): remove embedded Leaflet map, add Open Map card — centralise map on /map"
```

---

### Task 3: Add SimBrief import HUD panel to MapPage.razor

**Files:**
- Modify: `AviatesAirTracker/Pages/MapPage.razor`

Adds a top-right `hud--tr` draggable panel with three states: no plan, loading, and plan loaded. Mirrors the dark-glass HUD card style already used by the `hud--tl` and `hud--bl` panels.

- [ ] **Step 1: Add new injections at the top of the file**

After the existing `@inject IJSRuntime JS` line, add:

```razor
@inject AviatesAirTracker.Services.SimBriefService SimBriefSvc
@inject AviatesAirTracker.Services.SettingsService Settings
@inject AviatesAirTracker.Services.FlightSessionManager Session
```

- [ ] **Step 2: Add the HUD panel HTML**

Inside `<div class="map-page">`, after the `hud--br` controls card closing `</div>`, add:

```razor
@* ── Top-right: SimBrief plan ── *@
<div class="hud hud--tr" id="hud-plan">
    <div class="hud-drag" data-drag-handle title="Drag to move"></div>

    @if (_planLoading)
    {
        <div class="hud-plan-loading">
            <div class="hud-plan-spinner"></div>
            <span class="hud-label">FETCHING OFP…</span>
        </div>
    }
    else if (_currentPlan is not null)
    {
        <div class="hud-route-row">
            <span class="hud-route">@_currentPlan.DepartureICAO → @_currentPlan.ArrivalICAO</span>
        </div>
        <div class="hud-divider"></div>
        <span class="hud-label">@_currentPlan.AircraftType &nbsp;·&nbsp; @_currentPlan.Waypoints.Count WPT</span>
        <div class="hud-divider"></div>
        <button class="map-ctrl-btn hud-plan-btn" @onclick="ImportSimBriefAsync">REFRESH OFP</button>
    }
    else
    {
        @if (_planError is not null)
        {
            <div class="hud-plan-error">@_planError</div>
            <div class="hud-divider"></div>
        }

        @if (string.IsNullOrWhiteSpace(Settings.Settings.SimBriefUsername))
        {
            <span class="hud-label">NO SIMBRIEF ID</span>
            <div class="hud-divider"></div>
            <a href="/settings" class="map-ctrl-btn hud-plan-btn" style="text-decoration:none;">CONFIGURE</a>
        }
        else
        {
            <span class="hud-label">NO FLIGHT PLAN</span>
            <div class="hud-divider"></div>
            <button class="map-ctrl-btn hud-plan-btn" @onclick="ImportSimBriefAsync">IMPORT OFP</button>
        }
    }
</div>
```

- [ ] **Step 3: Add C# state fields**

In the `@code` block, alongside the existing `private bool _mapInitialized;` field, add:

```csharp
private bool                _planLoading;
private string?             _planError;
private SimBriefFlightPlan? _currentPlan;
```

- [ ] **Step 4: Subscribe to `FlightPlanLoaded` in `OnInitialized`**

The existing `OnInitialized` only subscribes to `VM.MapRenderRequested`. Add a second subscription:

```csharp
protected override void OnInitialized()
{
    VM.MapRenderRequested += OnMapRenderRequested;
    SimBriefSvc.FlightPlanLoaded += OnFlightPlanLoaded;
}
```

- [ ] **Step 5: Add the `FlightPlanLoaded` handler**

```csharp
private void OnFlightPlanLoaded(object? sender, SimBriefFlightPlan plan)
{
    _ = InvokeAsync(() =>
    {
        _currentPlan = plan;
        StateHasChanged();
    });
}
```

- [ ] **Step 6: Unsubscribe in `DisposeAsync`**

In the existing `DisposeAsync`, add the unsubscribe as the first line:

```csharp
public async ValueTask DisposeAsync()
{
    VM.MapRenderRequested -= OnMapRenderRequested;
    SimBriefSvc.FlightPlanLoaded -= OnFlightPlanLoaded;   // ← add this
    VM.MapReady     = false;
    _mapInitialized = false;
    try { await JS.InvokeVoidAsync("aviatesMap.destroy"); }
    catch (JSDisconnectedException) { }
    catch (ObjectDisposedException) { }
}
```

- [ ] **Step 7: Seed the panel from an already-loaded plan**

In `OnAfterRenderAsync`, after the existing `PushPlannedRouteAsync` call, add one line:

```csharp
// Push any SimBrief plan that was already loaded before this page was opened
var (plan, _) = VM.ConsumePlanSync();
if (plan is not null)
    await PushPlannedRouteAsync(plan);

_currentPlan = SimBriefSvc.CurrentPlan;   // ← add this
```

- [ ] **Step 8: Initialise drag for the new panel**

In `OnAfterRenderAsync`, alongside the existing `aviatesTokDrag.initDrag` calls, add:

```csharp
await JS.InvokeVoidAsync("aviatesTokDrag.initDrag", "hud-identity");
await JS.InvokeVoidAsync("aviatesTokDrag.initDrag", "hud-progress");
await JS.InvokeVoidAsync("aviatesTokDrag.initDrag", "hud-plan");   // ← add this
```

- [ ] **Step 9: Add the import method**

```csharp
private async Task ImportSimBriefAsync()
{
    var username = Settings.Settings.SimBriefUsername;
    if (string.IsNullOrWhiteSpace(username)) return;

    _planLoading = true;
    _planError   = null;
    StateHasChanged();

    try
    {
        var plan = await SimBriefSvc.FetchLatestOFPAsync(username);
        if (plan is not null)
        {
            _currentPlan = plan;
            Session.AssignSimBriefPlan(plan);
            SimBriefSvc.NotifyPlanLoaded(plan);
        }
        else
        {
            _planError = "NO OFP FOUND";
        }
    }
    catch
    {
        _planError = "FETCH FAILED";
    }
    finally
    {
        _planLoading = false;
        StateHasChanged();
    }
}
```

- [ ] **Step 10: Add new CSS rules**

In the `<style>` block of `MapPage.razor`, add after the existing `.hud--br` rule:

```css
/* ── Top-right HUD position ─────────────────────── */
.hud--tr { top: 12px; right: 12px; min-width: 160px; }

/* ── Plan HUD inner elements ────────────────────── */
.hud-plan-loading {
    display: flex;
    align-items: center;
    gap: 8px;
}
.hud-plan-spinner {
    width: 12px;
    height: 12px;
    border: 1.5px solid rgba(61, 126, 238, 0.3);
    border-top-color: #3D7EEE;
    border-radius: 50%;
    animation: hud-spin 0.8s linear infinite;
    flex-shrink: 0;
}
@@keyframes hud-spin { to { transform: rotate(360deg); } }
.hud-plan-error {
    font-size: 8px;
    color: #F97316;
    letter-spacing: 0.06em;
}
.hud-plan-btn {
    width: 100%;
    height: 22px;
    font-size: 9px;
    letter-spacing: 0.06em;
    display: flex;
    align-items: center;
    justify-content: center;
}
```

- [ ] **Step 11: Build**

```powershell
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```

Expected: `Build succeeded. 0 Error(s).`

- [ ] **Step 12: Verify**

Run the app and navigate to `/map`:

1. **No plan state** — panel shows "NO FLIGHT PLAN" and "IMPORT OFP" button in top-right
2. **Import from map** — click "IMPORT OFP"; spinner appears; panel switches to route label (`EGLL → EGKK`), aircraft type, waypoint count; dashed planned route and airport markers appear on the map canvas
3. **Drag** — grab the drag handle strip at the top of the panel and move it; it repositions freely
4. **Import from ACARS then navigate to map** — clear the plan, go to `/acars`, import OFP there, navigate back to `/map`; panel should already show the route (seeded via `SimBriefSvc.CurrentPlan`)
5. **Error state** — temporarily set a bad SimBrief username in Settings, click "IMPORT OFP"; panel should show "NO OFP FOUND" in orange

- [ ] **Step 13: Commit**

```powershell
git add AviatesAirTracker/Pages/MapPage.razor
git commit -m "feat(map): add SimBrief import HUD panel to /map page"
```

---

## Self-Review

**Spec coverage:**
- Fix broken notification chain → Task 1 ✅
- `NotifyPlanLoaded` unblocks `TakeoffPerformanceModal` → Task 1 (same fix) ✅
- Remove Leaflet map from ACARS → Task 2 ✅
- "Open Map" card replacing map → Task 2, Step 1 ✅
- SimBrief import stays on ACARS → Task 1/2 (import method untouched, only map removed) ✅
- New `hud--tr` panel on `/map` → Task 3 ✅
- Three panel states (no plan / loading / plan loaded) → Task 3, Step 2 ✅
- Draggable panel → Task 3, Step 8 ✅
- Seed from already-loaded plan on page open → Task 3, Step 7 ✅
- Subscribe/unsubscribe `FlightPlanLoaded` → Task 3, Steps 4 and 6 ✅
- Import method fires full chain → Task 3, Step 9 ✅

**No placeholders, no TODOs, no vague steps.**

**Type consistency:**
- `SimBriefFlightPlan` — consistent throughout
- `_currentPlan`, `_planLoading`, `_planError` — defined in Step 3, used in Steps 2, 5, 9
- `ImportSimBriefAsync` — matches `@onclick` references in Step 2
- `OnFlightPlanLoaded` — defined Step 5, subscribed Step 4, unsubscribed Step 6
- `SimBriefSvc.NotifyPlanLoaded` — consistent with Task 1 and Task 3 Step 9
- `hud-plan-btn` — defined in CSS Step 10, used in HTML Step 2
