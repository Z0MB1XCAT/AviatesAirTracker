# Live Pilot Map — App Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show other pilots currently flying on the app's map, let the pilot click one to see their details/route and jump straight into a conversation with them, and give pilots an opt-out toggle for sharing their own live position.

**Architecture:** `AcarsPositionService` (existing) is extended to call the three new backend endpoints (`flight-start`/`position`/`flight-end`) from the "Backend" plan, at 30-second cadence, respecting a new `ShareLivePosition` setting. A new `OtherPilotsService` polls `GET /api/acars/live-positions` every 30s and raises an event with the current list. `MapPage.razor` renders those pilots as a second marker layer (via new `interop.js` functions) and shows a details panel with a Message button on click; messaging a map pilot is a purely local operation (add as a friend using their already-known name/ID, no backend call needed — confirmed no server-side friendship check exists on the message-send endpoint) followed by a navigation to the Messages page with that conversation pre-selected.

**Tech Stack:** C# / .NET 8, Blazor Hybrid (Razor + JS interop), CommunityToolkit.Mvvm, RestSharp/Newtonsoft.Json (existing `AviatesBackendClient` conventions), Leaflet.js (existing `interop.js`).

**Dependency:** This plan assumes the backend endpoints from `docs/superpowers/plans/2026-07-20-live-pilot-map-backend.md` exist and are deployed (or at least locally correct) — `POST /api/v1/acars/flight-start`, the extended `POST /api/v1/acars/position` (adds `heading`, uses `computePilotId`), `POST /api/v1/acars/flight-end`, `PATCH /api/v1/acars/live-sharing`, and the extended `GET /api/acars/live-positions` (new response shape: `pilotId, pilotName, lat, lon, alt, speed, heading, phase, departureIcao, arrivalIcao, callsign, waypoints, updatedAt`).

**A note on testing approach:** This project has no automated test suite (per `CLAUDE.md`) — verification here is `dotnet build` (real compiler errors, not a guess) plus running the app. Task 8 is a manual run-through since it needs a live MSFS session (or the app's fallback/stub mode) and the real deployed backend, neither of which can be scripted.

---

## Task 1: Add the `LivePilotPosition` model and the `ShareLivePosition` setting

**Files:**
- Create: `AviatesAirTracker/Models/LiveMapModels.cs`
- Modify: `AviatesAirTracker/Services/SupportServices.cs` (the `AppSettings` class)

- [ ] **Step 1: Create the live-map DTOs**

Create `AviatesAirTracker/Models/LiveMapModels.cs`:

```csharp
namespace AviatesAirTracker.Models;

// ============================================================
// LIVE MAP — DTOs for GET /api/acars/live-positions
// Mirrors the LiveMapRoom Durable Object's response shape.
// ============================================================

public class LivePilotPosition
{
    public string PilotId       { get; set; } = "";
    public string PilotName     { get; set; } = "";
    public double Lat           { get; set; }
    public double Lon           { get; set; }
    public int    Alt           { get; set; }
    public int    Speed         { get; set; }
    public int    Heading       { get; set; }
    public string Phase         { get; set; } = "";
    public string? DepartureIcao { get; set; }
    public string? ArrivalIcao   { get; set; }
    public string? Callsign      { get; set; }
    public List<LiveWaypoint>? Waypoints { get; set; }
    public DateTime UpdatedAt   { get; set; }
}

public class LiveWaypoint
{
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string Id  { get; set; } = "";
}
```

- [ ] **Step 2: Add the opt-out setting**

In `AviatesAirTracker/Services/SupportServices.cs`, find the `AppSettings` class:

```csharp
    public bool AutoConnectSimConnect { get; set; } = true;
    public bool AutoStartFlight { get; set; } = true;
    public bool ShowApproachAlerts { get; set; } = true;
    public bool ShowLandingResult { get; set; } = true;
    public bool MinimizeToTray { get; set; } = false;
```

Add `ShareLivePosition` right after `MinimizeToTray`:

```csharp
    public bool AutoConnectSimConnect { get; set; } = true;
    public bool AutoStartFlight { get; set; } = true;
    public bool ShowApproachAlerts { get; set; } = true;
    public bool ShowLandingResult { get; set; } = true;
    public bool MinimizeToTray { get; set; } = false;
    public bool ShareLivePosition { get; set; } = true;
```

- [ ] **Step 3: Build**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: `Build succeeded.` (same warning count as before this change — no new warnings from this task).

- [ ] **Step 4: Commit**

```bash
git add AviatesAirTracker/Models/LiveMapModels.cs AviatesAirTracker/Services/SupportServices.cs
git commit -m "feat(app): add LivePilotPosition model and ShareLivePosition setting"
```

---

## Task 2: `AviatesBackendClient` — flight-start/end, live-sharing toggle, live-positions fetch, extended position report

**Files:**
- Modify: `AviatesAirTracker/Core/Backend/AviatesBackendClient.cs`

- [ ] **Step 1: Extend `SendPositionReportAsync` — 30s cadence, add heading**

Find:

```csharp
    private DateTime _lastPositionReport = DateTime.MinValue;

    /// <summary>
    /// Sends a live ACARS position report to the backend (every 5 minutes).
    /// </summary>
    public async Task SendPositionReportAsync(
        double lat, double lon, double altFt, int speedKts,
        string phase, string acarsKey)
    {
        if ((DateTime.UtcNow - _lastPositionReport).TotalMinutes < 5) return;

        _lastPositionReport = DateTime.UtcNow;

        try
        {
            // TODO: POST /api/v1/acars/position
            var request = new RestRequest("/api/v1/acars/position", Method.Post)
                .AddHeader("Authorization", $"Bearer {acarsKey}")
                .AddJsonBody(new
                {
                    latitude  = lat,
                    longitude = lon,
                    altitude  = (int)altFt,
                    speed     = speedKts,
                    phase     = phase,
                    timestamp = DateTime.UtcNow.ToString("o")
                });

            await _client.ExecuteAsync(request);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[BackendClient] Position report failed (non-critical)");
        }
    }
}
```

Replace with:

```csharp
    private DateTime _lastPositionReport = DateTime.MinValue;

    /// <summary>
    /// Sends a live ACARS position report to the backend (every 30 seconds, for the live map).
    /// </summary>
    public async Task SendPositionReportAsync(
        double lat, double lon, double altFt, int speedKts, int headingDeg,
        string phase, string acarsKey)
    {
        if ((DateTime.UtcNow - _lastPositionReport).TotalSeconds < 30) return;

        _lastPositionReport = DateTime.UtcNow;

        try
        {
            var request = new RestRequest("/api/v1/acars/position", Method.Post)
                .AddHeader("Authorization", $"Bearer {acarsKey}")
                .AddJsonBody(new
                {
                    latitude  = lat,
                    longitude = lon,
                    altitude  = (int)altFt,
                    speed     = speedKts,
                    heading   = headingDeg,
                    phase     = phase,
                    timestamp = DateTime.UtcNow.ToString("o")
                });

            await _client.ExecuteAsync(request);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[BackendClient] Position report failed (non-critical)");
        }
    }

    /// <summary>
    /// Registers the start of a flight (or an updated flight plan) with the live map.
    /// Safe to call multiple times for the same flight — the backend upserts.
    /// </summary>
    public async Task SendFlightStartAsync(
        string? departureIcao, string? arrivalIcao, string? callsign,
        List<Waypoint>? waypoints, string acarsKey)
    {
        try
        {
            var request = new RestRequest("/api/v1/acars/flight-start", Method.Post)
                .AddHeader("Authorization", $"Bearer {acarsKey}")
                .AddJsonBody(new
                {
                    departure_icao = departureIcao ?? "",
                    arrival_icao   = arrivalIcao   ?? "",
                    callsign       = callsign      ?? "",
                    waypoints      = waypoints?.Select(w => new { lat = w.Latitude, lon = w.Longitude, id = w.Identifier }).ToList()
                });

            await _client.ExecuteAsync(request);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[BackendClient] flight-start report failed (non-critical)");
        }
    }

    /// <summary>Removes the pilot from the live map (call on landing/session end).</summary>
    public async Task SendFlightEndAsync(string acarsKey)
    {
        try
        {
            var request = new RestRequest("/api/v1/acars/flight-end", Method.Post)
                .AddHeader("Authorization", $"Bearer {acarsKey}");

            await _client.ExecuteAsync(request);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[BackendClient] flight-end report failed (non-critical)");
        }
    }

    /// <summary>Toggles whether this pilot's position is shared on the live map. Enforced server-side.</summary>
    public async Task<bool> SetLiveSharingAsync(bool enabled, string acarsKey)
    {
        try
        {
            var request = new RestRequest("/api/v1/acars/live-sharing", Method.Patch)
                .AddHeader("Authorization", $"Bearer {acarsKey}")
                .AddJsonBody(new { enabled });

            var response = await _client.ExecuteAsync(request);
            return response.IsSuccessful;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[BackendClient] live-sharing toggle failed (non-critical)");
            return false;
        }
    }

    /// <summary>Fetches all currently-flying pilots for the live map. Public endpoint, no auth.</summary>
    public async Task<List<LivePilotPosition>> FetchLivePositionsAsync()
    {
        try
        {
            var request = new RestRequest("/api/acars/live-positions", Method.Get);
            var response = await _client.ExecuteAsync(request);

            if (!response.IsSuccessful || string.IsNullOrEmpty(response.Content))
                return new List<LivePilotPosition>();

            var wrapper = JsonConvert.DeserializeObject<LivePositionsResponse>(response.Content);
            return wrapper?.Positions ?? new List<LivePilotPosition>();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[BackendClient] FetchLivePositions failed (non-critical)");
            return new List<LivePilotPosition>();
        }
    }
}

internal class LivePositionsResponse
{
    public List<LivePilotPosition> Positions { get; set; } = new();
}
```

Note: `using AviatesAirTracker.Models;` is already present at the top of this file (confirmed — it's used for `FriendEntry` elsewhere in the same file), so `LivePilotPosition`/`LiveWaypoint`/`Waypoint` resolve without a new `using`.

- [ ] **Step 2: Build**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: `Build succeeded.` If you see `CS1501` on `SendPositionReportAsync`, a caller wasn't updated yet — that's expected until Task 3.

- [ ] **Step 3: Commit**

```bash
git add AviatesAirTracker/Core/Backend/AviatesBackendClient.cs
git commit -m "feat(app): add flight-start/end, live-sharing toggle, and live-positions fetch to AviatesBackendClient"
```

---

## Task 3: `AcarsPositionService` — 30s cadence, flight-start/end wiring, opt-out gate

**Files:**
- Modify: `AviatesAirTracker/Services/AcarsPositionService.cs`

- [ ] **Step 1: Replace the file**

Replace the full contents of `AviatesAirTracker/Services/AcarsPositionService.cs` with:

```csharp
using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Models;
using Serilog;

namespace AviatesAirTracker.Services;

// ============================================================
// ACARS POSITION SERVICE
//
// Sends live aircraft position reports to the Aviates Air backend
// while a flight is in progress, and registers/deregisters the
// pilot's flight on the live map at the right phase transitions.
//
// Outer gate: 30-second check interval (avoids spawning a Task
// on every 20Hz tick). AviatesBackendClient.SendPositionReportAsync
// has its own inner 30-second throttle for the actual HTTP call.
// ============================================================

public class AcarsPositionService : IDisposable
{
    private readonly FlightSessionManager _session;
    private readonly AviatesBackendClient _backend;
    private readonly SettingsService _settings;
    private readonly SimBriefService _simBrief;

    // DateTime is not a valid volatile field type (CS0677). Use a long ticks backing field
    // with Interlocked for safe cross-thread access between the SimConnect pump and UI thread.
    private long _lastCheckTicks = DateTime.MinValue.Ticks;
    private const int CHECK_INTERVAL_SECONDS = 30;

    /// <summary>Last time the 30-second gate fired (not necessarily when an HTTP call went out).</summary>
    public DateTime LastCheckAt => new DateTime(Interlocked.Read(ref _lastCheckTicks), DateTimeKind.Utc);

    /// <summary>Raised each time the outer 30-second gate fires.</summary>
    public event Action? GateFired;

    public AcarsPositionService(
        FlightSessionManager session,
        AviatesBackendClient backend,
        SettingsService settings,
        SimBriefService simBrief)
    {
        _session  = session;
        _backend  = backend;
        _settings = settings;
        _simBrief = simBrief;

        _session.TelemetryUpdated    += OnTelemetryUpdated;
        _session.SessionStateChanged += OnSessionStateChanged;
        _simBrief.FlightPlanLoaded   += OnFlightPlanLoaded;
        Log.Information("[AcarsPosition] Position reporting service initialized");
    }

    private void OnTelemetryUpdated(object? sender, TelemetrySnapshot snap)
    {
        // Only report when actively airborne — no need to ping during taxi or idle
        if (_session.State is not (FlightSessionState.Airborne
            or FlightSessionState.OnApproach
            or FlightSessionState.Landed))
            return;

        // Outer throttle: avoid spawning a Task every 50ms
        var now = DateTime.UtcNow;
        if ((now.Ticks - Interlocked.Read(ref _lastCheckTicks)) < TimeSpan.TicksPerSecond * CHECK_INTERVAL_SECONDS)
            return;

        Interlocked.Exchange(ref _lastCheckTicks, now.Ticks);
        GateFired?.Invoke();

        if (!_settings.Settings.ShareLivePosition) return;

        var key = _settings.Settings.AcarsKey.Trim();
        if (string.IsNullOrEmpty(key)) return;

        // Backend method self-throttles to one call per 30 seconds
        _ = _backend.SendPositionReportAsync(
            snap.Latitude,
            snap.Longitude,
            snap.AltitudePressure,    // pressure altitude = flight level reference
            (int)snap.GroundSpeedKts,
            (int)snap.Raw.HeadingTrue,
            snap.Phase.ToString(),
            key);
    }

    private void OnSessionStateChanged(object? sender, FlightSessionState newState)
    {
        if (!_settings.Settings.ShareLivePosition) return;
        var key = _settings.Settings.AcarsKey.Trim();
        if (string.IsNullOrEmpty(key)) return;

        if (newState == FlightSessionState.Taxiing)
        {
            var flight = _session.CurrentFlight;
            _ = _backend.SendFlightStartAsync(
                flight?.DepartureICAO, flight?.ArrivalICAO, flight?.Callsign,
                null, key);
        }
        else if (newState == FlightSessionState.Complete)
        {
            _ = _backend.SendFlightEndAsync(key);
        }
    }

    private void OnFlightPlanLoaded(object? sender, SimBriefFlightPlan plan)
    {
        // Only re-announce if a flight is actually in progress — SimBrief plans can be
        // imported before engines start too, and that shouldn't register a "flight" yet.
        if (_session.State is not (FlightSessionState.Taxiing or FlightSessionState.Airborne
            or FlightSessionState.OnApproach or FlightSessionState.Landed))
            return;

        if (!_settings.Settings.ShareLivePosition) return;
        var key = _settings.Settings.AcarsKey.Trim();
        if (string.IsNullOrEmpty(key)) return;

        _ = _backend.SendFlightStartAsync(
            plan.DepartureICAO, plan.ArrivalICAO, _session.CurrentFlight?.Callsign,
            plan.Waypoints, key);
    }

    public void Dispose()
    {
        _session.TelemetryUpdated    -= OnTelemetryUpdated;
        _session.SessionStateChanged -= OnSessionStateChanged;
        _simBrief.FlightPlanLoaded   -= OnFlightPlanLoaded;
        Log.Debug("[AcarsPosition] Disposed");
    }
}
```

This drops the stale `// TODO: make this configurable via AppSettings.AutoPirepIntervalMinutes` comment from the original file — that setting is unrelated to this feature (it gates something else in `Acars.razor`) and is left untouched.

- [ ] **Step 2: Build**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add AviatesAirTracker/Services/AcarsPositionService.cs
git commit -m "feat(app): wire AcarsPositionService to flight-start/end and 30s position cadence"
```

---

## Task 4: `OtherPilotsService` — polls the live map every 30s

**Files:**
- Create: `AviatesAirTracker/Services/OtherPilotsService.cs`
- Modify: `AviatesAirTracker/App.xaml.cs`

- [ ] **Step 1: Create the service**

Create `AviatesAirTracker/Services/OtherPilotsService.cs`:

```csharp
using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Models;
using Serilog;

namespace AviatesAirTracker.Services;

// ============================================================
// OTHER PILOTS SERVICE
//
// Polls the public live-positions endpoint every 30 seconds and
// raises an event with the latest snapshot. Mirrors MessagingService's
// timer-poll structure.
// ============================================================

public class OtherPilotsService : IDisposable
{
    private readonly AviatesBackendClient _backend;
    private readonly System.Threading.Timer _pollTimer;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private bool _disposed;

    public List<LivePilotPosition> LatestPositions { get; private set; } = new();

    public event EventHandler<List<LivePilotPosition>>? PositionsUpdated;

    public OtherPilotsService(AviatesBackendClient backend)
    {
        _backend = backend;

        _ = PollAsync();
        _pollTimer = new System.Threading.Timer(
            _ => { if (!_disposed) _ = PollAsync(); },
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));
    }

    private async Task PollAsync()
    {
        if (!await _pollGate.WaitAsync(0)) return;
        try
        {
            var positions = await _backend.FetchLivePositionsAsync();
            LatestPositions = positions;
            PositionsUpdated?.Invoke(this, positions);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[OtherPilots] Poll error (non-critical)");
        }
        finally
        {
            _pollGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Dispose();
        _pollGate.Dispose();
    }
}
```

- [ ] **Step 2: Register in DI**

In `AviatesAirTracker/App.xaml.cs`, find:

```csharp
        services.AddSingleton<AcarsPositionService>();
```

Add immediately after it:

```csharp
        services.AddSingleton<AcarsPositionService>();
        services.AddSingleton<OtherPilotsService>();
```

- [ ] **Step 3: Build**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add AviatesAirTracker/Services/OtherPilotsService.cs AviatesAirTracker/App.xaml.cs
git commit -m "feat(app): add OtherPilotsService polling the live map every 30s"
```

---

## Task 5: `interop.js` — render other pilots on the map

**Files:**
- Modify: `AviatesAirTracker/wwwroot/js/interop.js`

- [ ] **Step 1: Add other-pilot marker/route state and functions to `window.aviatesMap`**

Find the `_airportMarkers: []` field near the top of the `window.aviatesMap` object:

```js
window.aviatesMap = {
    ...
    _airportMarkers: [],
```

Add two new state fields right after it:

```js
window.aviatesMap = {
    ...
    _airportMarkers: [],
    _otherPilotMarkers: {},   // pilotId -> L.marker
    _otherPilotRoutes: {},    // pilotId -> L.polyline
    _otherPilotClickRef: null,
```

Find the `destroy` function (it resets `_aircraftMarker`, `_pathLine`, `_plannedLine`, `_airportMarkers` — the same block appears near line 18-21 for `init`'s reset and again near line 252-255 for `destroy`). In **both** places, add cleanup for the new fields right after `this._airportMarkers = [];`:

```js
            this._airportMarkers = [];
            Object.values(this._otherPilotMarkers).forEach(function (m) { m.remove(); });
            Object.values(this._otherPilotRoutes).forEach(function (r) { r.remove(); });
            this._otherPilotMarkers = {};
            this._otherPilotRoutes = {};
```

- [ ] **Step 2: Add the click-handler registration and the upsert/remove functions**

Add these as new methods on `window.aviatesMap` (place them near `addAirportMarker`/`clearAirportMarkers`, which are good neighbors — same file, same object):

```js
    setOtherPilotClickHandler: function (dotNetRef) {
        this._otherPilotClickRef = dotNetRef;
    },

    upsertOtherPilot: function (pilotId, lat, lon, waypoints) {
        if (!this._map) return;

        var marker = this._otherPilotMarkers[pilotId];
        if (!marker) {
            marker = L.marker([lat, lon], {
                icon: L.divIcon({
                    className: 'avt-other-pilot-icon',
                    html: '<div style="width:14px;height:14px;border-radius:50%;background:rgba(255,255,255,0.55);border:2px solid rgba(61,126,238,0.9);"></div>',
                    iconSize: [14, 14],
                    iconAnchor: [7, 7],
                })
            }).addTo(this._map);
            var self = this;
            marker.on('click', function () {
                if (self._otherPilotClickRef) {
                    self._otherPilotClickRef.invokeMethodAsync('OnOtherPilotClicked', pilotId);
                }
            });
            this._otherPilotMarkers[pilotId] = marker;
        } else {
            marker.setLatLng([lat, lon]);
        }

        var route = this._otherPilotRoutes[pilotId];
        if (Array.isArray(waypoints) && waypoints.length > 0) {
            var latlngs = waypoints
                .filter(function (w) { return (w.lat !== 0 || w.lon !== 0) && Math.abs(w.lat) <= 90 && Math.abs(w.lon) <= 180; })
                .map(function (w) { return [w.lat, w.lon]; });

            if (!route) {
                route = L.polyline(latlngs, { color: '#3D7EEE', weight: 1.5, opacity: 0.45, dashArray: '4,5' }).addTo(this._map);
                this._otherPilotRoutes[pilotId] = route;
            } else {
                route.setLatLngs(latlngs);
            }
        } else if (route) {
            route.remove();
            delete this._otherPilotRoutes[pilotId];
        }
    },

    removeOtherPilot: function (pilotId) {
        var marker = this._otherPilotMarkers[pilotId];
        if (marker) { marker.remove(); delete this._otherPilotMarkers[pilotId]; }
        var route = this._otherPilotRoutes[pilotId];
        if (route) { route.remove(); delete this._otherPilotRoutes[pilotId]; }
    },
```

- [ ] **Step 3: Sanity-check the file still parses**

Run: `node --check AviatesAirTracker/wwwroot/js/interop.js`
Expected: no output, exit code 0.

- [ ] **Step 4: Commit**

```bash
git add AviatesAirTracker/wwwroot/js/interop.js
git commit -m "feat(app): render other pilots and their routes on the Leaflet map"
```

---

## Task 6: `MessagingService` — quick-add a pilot spotted on the map

**Files:**
- Modify: `AviatesAirTracker/Services/MessagingService.cs`

- [ ] **Step 1: Add the quick-add method**

In `MessagingService.cs`, find the `FRIENDS` section:

```csharp
    // =====================================================
    // FRIENDS
    // =====================================================

    public async Task<FriendEntry?> AddFriendByCodeAsync(string friendCode, string? displayName = null)
```

Add a new method right before `AddFriendByCodeAsync`:

```csharp
    // =====================================================
    // FRIENDS
    // =====================================================

    /// <summary>
    /// Adds (or returns the existing) friend entry for a pilot spotted on the live map.
    /// No Friend Code required — the pilot's ID and name are already known from their
    /// live position broadcast, and the backend's message-send endpoint has no
    /// friendship requirement, so this is a purely local bookkeeping step.
    /// </summary>
    public async Task<FriendEntry> AddOrGetFriendFromLiveMapAsync(string pilotId, string pilotName)
    {
        var friends = await _friendRepo.GetAllAsync();
        var existing = friends.FirstOrDefault(f => f.PilotId == pilotId);
        if (existing is not null) return existing;

        var entry = new FriendEntry
        {
            PilotId   = pilotId,
            PilotName = pilotName,
            AddedAt   = DateTime.UtcNow,
        };
        await _friendRepo.AddAsync(entry);
        return entry;
    }

    public async Task<FriendEntry?> AddFriendByCodeAsync(string friendCode, string? displayName = null)
```

- [ ] **Step 2: Build**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add AviatesAirTracker/Services/MessagingService.cs
git commit -m "feat(app): add MessagingService.AddOrGetFriendFromLiveMapAsync for map-click messaging"
```

---

## Task 7: `MapPage.razor` — other-pilots layer, details panel, Message button; `Messages.razor` — open a conversation via query param

**Files:**
- Modify: `AviatesAirTracker/Pages/MapPage.razor`
- Modify: `AviatesAirTracker/Pages/Messages.razor`

- [ ] **Step 1: Inject the new services and NavigationManager**

At the top of `MapPage.razor`, find:

```razor
@page "/map"
@inject MapViewModel VM
@inject IJSRuntime JS
@inject AviatesAirTracker.Services.SimBriefService SimBriefSvc
@inject AviatesAirTracker.Services.SettingsService Settings
@inject AviatesAirTracker.Services.FlightSessionManager Session
@implements IAsyncDisposable
@using AviatesAirTracker.Models
@using System.Linq
@using Serilog
```

Replace with:

```razor
@page "/map"
@inject MapViewModel VM
@inject IJSRuntime JS
@inject NavigationManager Nav
@inject AviatesAirTracker.Services.SimBriefService SimBriefSvc
@inject AviatesAirTracker.Services.SettingsService Settings
@inject AviatesAirTracker.Services.FlightSessionManager Session
@inject AviatesAirTracker.Services.OtherPilotsService OtherPilots
@inject AviatesAirTracker.Services.MessagingService Messaging
@implements IAsyncDisposable
@using AviatesAirTracker.Models
@using System.Linq
@using Serilog
```

- [ ] **Step 2: Add the details-panel markup**

Find the closing of the controls HUD block:

```razor
    @* ── Bottom-right: Map controls ── *@
    <div class="hud hud--br hud--controls" id="hud-controls">
        <button class="map-ctrl-btn" title="Centre on aircraft" @onclick="CentreOnAircraftAsync">✈</button>
        <button class="map-ctrl-btn" title="Zoom in"           @onclick="ZoomInAsync">+</button>
        <button class="map-ctrl-btn" title="Zoom out"          @onclick="ZoomOutAsync">−</button>
    </div>
```

Add a new panel right after it:

```razor
    @* ── Bottom-right: Map controls ── *@
    <div class="hud hud--br hud--controls" id="hud-controls">
        <button class="map-ctrl-btn" title="Centre on aircraft" @onclick="CentreOnAircraftAsync">✈</button>
        <button class="map-ctrl-btn" title="Zoom in"           @onclick="ZoomInAsync">+</button>
        <button class="map-ctrl-btn" title="Zoom out"          @onclick="ZoomOutAsync">−</button>
    </div>

    @* ── Other-pilot details panel ── *@
    @if (_selectedPilot is not null)
    {
        <div class="hud hud--pilot-detail" id="hud-pilot-detail">
            <div class="hud-drag" data-drag-handle title="Drag to move"></div>
            <div class="hud-route-row">
                <span class="hud-route">@_selectedPilot.PilotName</span>
                <span class="hud-phase" style="background:#3D7EEE22;border-color:#3D7EEE66;color:#3D7EEE;">
                    @_selectedPilot.Phase.ToUpper()
                </span>
                <button class="hud-close-btn" title="Close" @onclick="() => _selectedPilot = null">×</button>
            </div>
            <div class="hud-divider"></div>
            <div class="hud-grid">
                <span class="hud-label">ALT</span>  <span class="hud-val">@_selectedPilot.Alt ft</span>
                <span class="hud-label">GS</span>   <span class="hud-val">@_selectedPilot.Speed kts</span>
                <span class="hud-label">HDG</span>  <span class="hud-val">@_selectedPilot.Heading°</span>
            </div>
            @if (!string.IsNullOrEmpty(_selectedPilot.DepartureIcao) || !string.IsNullOrEmpty(_selectedPilot.ArrivalIcao))
            {
                <div class="hud-divider"></div>
                <span class="hud-label">@(_selectedPilot.DepartureIcao ?? "----") → @(_selectedPilot.ArrivalIcao ?? "----")</span>
            }
            <div class="hud-divider"></div>
            <button class="map-ctrl-btn hud-plan-btn" @onclick="() => MessageSelectedPilotAsync()">MESSAGE</button>
        </div>
    }
```

- [ ] **Step 3: Add state, the JS-invokable click handler, subscription wiring, and message navigation**

Find the `@code` block's field declarations:

```csharp
@code {
    private bool _mapInitialized;
    private bool                _planLoading;
    private string?             _planError;
    private SimBriefFlightPlan? _currentPlan;
```

Replace with:

```csharp
@code {
    private bool _mapInitialized;
    private bool                _planLoading;
    private string?             _planError;
    private SimBriefFlightPlan? _currentPlan;
    private LivePilotPosition?  _selectedPilot;
    private DotNetObjectReference<MapPage>? _dotNetRef;
    private HashSet<string> _renderedOtherPilotIds = new();
```

Find `protected override void OnInitialized()`:

```csharp
    protected override void OnInitialized()
    {
        VM.MapRenderRequested += OnMapRenderRequested;
        SimBriefSvc.FlightPlanLoaded += OnFlightPlanLoaded;
    }
```

Replace with:

```csharp
    protected override void OnInitialized()
    {
        VM.MapRenderRequested += OnMapRenderRequested;
        SimBriefSvc.FlightPlanLoaded += OnFlightPlanLoaded;
        OtherPilots.PositionsUpdated += OnOtherPilotsUpdated;
    }
```

Find `protected override async Task OnAfterRenderAsync(bool firstRender)`:

```csharp
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;

        await JS.InvokeVoidAsync("aviatesMap.init", VM.Lat, VM.Lon);
        await JS.InvokeVoidAsync("aviatesTokDrag.initDrag", "hud-identity");
        await JS.InvokeVoidAsync("aviatesTokDrag.initDrag", "hud-progress");
        await JS.InvokeVoidAsync("aviatesTokDrag.initDrag", "hud-plan");

        _mapInitialized = true;
        VM.MapReady     = true;

        // Push any SimBrief plan that was already loaded before this page was opened
        var (plan, _) = VM.ConsumePlanSync();
        if (plan is not null)
            await PushPlannedRouteAsync(plan);

        _currentPlan = SimBriefSvc.CurrentPlan;
        if (_currentPlan is not null)
            StateHasChanged();
    }
```

Replace with:

```csharp
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;

        await JS.InvokeVoidAsync("aviatesMap.init", VM.Lat, VM.Lon);
        await JS.InvokeVoidAsync("aviatesTokDrag.initDrag", "hud-identity");
        await JS.InvokeVoidAsync("aviatesTokDrag.initDrag", "hud-progress");
        await JS.InvokeVoidAsync("aviatesTokDrag.initDrag", "hud-plan");

        _mapInitialized = true;
        VM.MapReady     = true;

        _dotNetRef = DotNetObjectReference.Create(this);
        await JS.InvokeVoidAsync("aviatesMap.setOtherPilotClickHandler", _dotNetRef);
        await RenderOtherPilotsAsync(OtherPilots.LatestPositions);

        // Push any SimBrief plan that was already loaded before this page was opened
        var (plan, _) = VM.ConsumePlanSync();
        if (plan is not null)
            await PushPlannedRouteAsync(plan);

        _currentPlan = SimBriefSvc.CurrentPlan;
        if (_currentPlan is not null)
            StateHasChanged();
    }

    // Fired from OtherPilotsService's background Timer thread — must marshal onto the
    // render sync context via InvokeAsync before touching JS interop or component state
    // (see CLAUDE.md's threading notes on cross-thread updates from background pumps).
    private void OnOtherPilotsUpdated(object? sender, List<LivePilotPosition> positions)
    {
        _ = InvokeAsync(async () =>
        {
            await RenderOtherPilotsAsync(positions);
            StateHasChanged();
        });
    }

    private async Task RenderOtherPilotsAsync(List<LivePilotPosition> positions)
    {
        if (!_mapInitialized) return;

        var currentIds = positions.Select(p => p.PilotId).ToHashSet();

        foreach (var stalePilotId in _renderedOtherPilotIds.Except(currentIds).ToList())
        {
            await JS.InvokeVoidAsync("aviatesMap.removeOtherPilot", stalePilotId);
        }

        foreach (var pos in positions)
        {
            var waypointJs = pos.Waypoints?.Select(w => new { lat = w.Lat, lon = w.Lon }).ToArray();
            await JS.InvokeVoidAsync("aviatesMap.upsertOtherPilot",
                pos.PilotId, pos.Lat, pos.Lon, (object?)waypointJs);
        }

        _renderedOtherPilotIds = currentIds;

        if (_selectedPilot is not null)
            _selectedPilot = positions.FirstOrDefault(p => p.PilotId == _selectedPilot.PilotId);
    }

    [JSInvokable]
    public void OnOtherPilotClicked(string pilotId)
    {
        _selectedPilot = OtherPilots.LatestPositions.FirstOrDefault(p => p.PilotId == pilotId);
        StateHasChanged();
    }

    private async Task MessageSelectedPilotAsync()
    {
        if (_selectedPilot is null) return;
        await Messaging.AddOrGetFriendFromLiveMapAsync(_selectedPilot.PilotId, _selectedPilot.PilotName);
        Nav.NavigateTo($"/messages?open={Uri.EscapeDataString(_selectedPilot.PilotId)}");
    }
```

- [ ] **Step 4: Dispose the new subscriptions and the `DotNetObjectReference`**

Find:

```csharp
    public async ValueTask DisposeAsync()
    {
        VM.MapRenderRequested -= OnMapRenderRequested;
        SimBriefSvc.FlightPlanLoaded -= OnFlightPlanLoaded;
        VM.MapReady     = false;
        _mapInitialized = false;
        try { await JS.InvokeVoidAsync("aviatesMap.destroy"); }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
    }
```

Replace with:

```csharp
    public async ValueTask DisposeAsync()
    {
        VM.MapRenderRequested -= OnMapRenderRequested;
        SimBriefSvc.FlightPlanLoaded -= OnFlightPlanLoaded;
        OtherPilots.PositionsUpdated -= OnOtherPilotsUpdated;
        VM.MapReady     = false;
        _mapInitialized = false;
        _dotNetRef?.Dispose();
        try { await JS.InvokeVoidAsync("aviatesMap.destroy"); }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
    }
```

- [ ] **Step 5: Add the `hud--pilot-detail` and `hud-close-btn` styles**

In the `<style>` block at the bottom of `MapPage.razor`, find:

```css
    .hud--tl { top: 12px; left: 12px; }
    .hud--bl { bottom: 12px; left: 12px; }
    .hud--br { bottom: 12px; right: 12px; min-width: unset; padding: 6px; }
    .hud--tr { top: 12px; right: 12px; min-width: 160px; }
```

Add a new position rule after it:

```css
    .hud--tl { top: 12px; left: 12px; }
    .hud--bl { bottom: 12px; left: 12px; }
    .hud--br { bottom: 12px; right: 12px; min-width: unset; padding: 6px; }
    .hud--tr { top: 12px; right: 12px; min-width: 160px; }
    .hud--pilot-detail { top: 12px; right: 190px; min-width: 170px; }
```

And add the close-button style near `.map-ctrl-btn:focus-visible`:

```css
    .hud-close-btn {
        margin-left: auto;
        background: none;
        border: none;
        color: rgba(255, 255, 255, 0.4);
        font-size: 14px;
        cursor: pointer;
        line-height: 1;
        padding: 0 0 0 8px;
    }
    .hud-close-btn:hover { color: #ffffff; }
```

- [ ] **Step 6: Let `Messages.razor` open a conversation via `?open=<pilotId>`**

In `AviatesAirTracker/Pages/Messages.razor`, find:

```razor
@page "/messages"
@inject MessagingService MessagingService
@inject SettingsService SettingsService
@implements IDisposable
```

Replace with:

```razor
@page "/messages"
@inject MessagingService MessagingService
@inject SettingsService SettingsService
@implements IDisposable
@using Microsoft.AspNetCore.Components
```

Find `protected override async Task OnInitializedAsync()`:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _hasAcarsKey = !string.IsNullOrEmpty(SettingsService.Settings.AcarsKey);
        if (!_hasAcarsKey) return;

        MessagingService.NewMessageReceived += OnNewMessage;
        MessagingService.UnreadCountChanged += OnUnreadCountChanged;

        await RefreshAll();
    }
```

Replace with:

```csharp
    [Parameter]
    [SupplyParameterFromQuery(Name = "open")]
    public string? OpenPilotId { get; set; }

    protected override async Task OnInitializedAsync()
    {
        _hasAcarsKey = !string.IsNullOrEmpty(SettingsService.Settings.AcarsKey);
        if (!_hasAcarsKey) return;

        MessagingService.NewMessageReceived += OnNewMessage;
        MessagingService.UnreadCountChanged += OnUnreadCountChanged;

        await RefreshAll();

        if (!string.IsNullOrEmpty(OpenPilotId) && _friends.Any(f => f.PilotId == OpenPilotId))
            SelectFriendAndSwitchToInbox(OpenPilotId);
    }
```

`[SupplyParameterFromQuery]` requires the page to be routed with `@page "/messages"` already handling query strings automatically in Blazor — no route template change needed.

- [ ] **Step 7: Build**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: `Build succeeded.`

- [ ] **Step 8: Commit**

```bash
git add AviatesAirTracker/Pages/MapPage.razor AviatesAirTracker/Pages/Messages.razor
git commit -m "feat(app): show other pilots on the map with a details panel and map-click messaging"
```

---

## Task 8: `Settings.razor` — live-sharing opt-out toggle

**Files:**
- Modify: `AviatesAirTracker/Pages/Settings.razor`

- [ ] **Step 1: Inject nothing new — `BackendClient` and `SettingsService` are already injected**

(No new `@inject` needed — `Settings.razor` already injects `SettingsService` and `AviatesBackendClient` at the top of the file.)

- [ ] **Step 2: Add the toggle**

Find the "App Preferences" panel's last toggle-row:

```razor
            <div class="toggle-row" style="border-bottom:none;">
                <div class="toggle-info">
                    <div class="toggle-label">Minimize to Tray</div>
                    <div class="toggle-desc">Keep running in the system tray when window is closed</div>
                </div>
                <label class="toggle-switch">
                    <input type="checkbox" @bind="SettingsService.Settings.MinimizeToTray" />
                    <span class="toggle-track"></span>
                </label>
            </div>
        </div>
```

Replace with (moving `border-bottom:none` to the new last row, and wiring the new toggle to an explicit handler instead of `@bind` so it can call the backend immediately):

```razor
            <div class="toggle-row">
                <div class="toggle-info">
                    <div class="toggle-label">Minimize to Tray</div>
                    <div class="toggle-desc">Keep running in the system tray when window is closed</div>
                </div>
                <label class="toggle-switch">
                    <input type="checkbox" @bind="SettingsService.Settings.MinimizeToTray" />
                    <span class="toggle-track"></span>
                </label>
            </div>
            <div class="toggle-row" style="border-bottom:none;">
                <div class="toggle-info">
                    <div class="toggle-label">Share My Live Position</div>
                    <div class="toggle-desc">Show your flight on the Aviates Air live map for other pilots and the website. Takes effect immediately.</div>
                </div>
                <label class="toggle-switch">
                    <input type="checkbox" checked="@SettingsService.Settings.ShareLivePosition" @onchange="OnShareLivePositionChanged" />
                    <span class="toggle-track"></span>
                </label>
            </div>
        </div>
```

- [ ] **Step 3: Add the change handler**

In the `@code` block, find `SetTheme`:

```csharp
    private async Task SetTheme(string theme)
    {
        SettingsService.Settings.Theme = theme;
        SettingsService.Save();
        await JS.InvokeVoidAsync("aviatesTheme.apply", theme);
    }
```

Add a new method right after it:

```csharp
    private async Task OnShareLivePositionChanged(ChangeEventArgs e)
    {
        var enabled = e.Value is bool b && b;
        SettingsService.Settings.ShareLivePosition = enabled;
        SettingsService.Save();

        var key = SettingsService.Settings.AcarsKey.Trim();
        if (!string.IsNullOrEmpty(key))
            await BackendClient.SetLiveSharingAsync(enabled, key);
    }
```

- [ ] **Step 4: Build**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git add AviatesAirTracker/Pages/Settings.razor
git commit -m "feat(app): add live-sharing opt-out toggle to Settings, synced to backend immediately"
```

---

## Task 9: Manual run-through (not automated — needs a live session and the real backend)

**This task is not executed as part of this plan** — it needs the backend from the other plan actually deployed, and either MSFS running or the app's SimConnect stub/fallback mode. Document for the user to run once both are ready:

1. Launch the app (`.\build.ps1 -Run`), start a flight (real or via the stub), and open `/map`.
2. Confirm your own aircraft renders as before (no regression).
3. From a second machine/account (or a scripted `curl` call to `/api/v1/acars/flight-start` + `/api/v1/acars/position` with a different ACARS key, per Task 8 of the backend plan), simulate another pilot and confirm their marker and route line appear on the map within 30 seconds.
4. Click the other pilot's marker — confirm the details panel shows name/speed/altitude/heading/dep-arr and a Message button.
5. Click Message — confirm you land on `/messages` with that pilot's conversation already open and ready to type into.
6. Go to Settings, turn off "Share My Live Position" — confirm (via the backend's `GET /api/acars/live-positions`) that you disappear from the feed immediately, not just after landing.
7. Turn it back on and confirm position reporting resumes.

---

## Explicitly Not Part of This Plan

- Deploying the backend changes from `docs/superpowers/plans/2026-07-20-live-pilot-map-backend.md` — a prerequisite, not something this plan does.
- Building or publishing a release of the app — this plan only compiles in Debug (`dotnet build`), matching how every other plan in this repo verifies app-side changes.
- Task 9's manual run-through — documented for the user, not run automatically, since it needs a live MSFS session and the real deployed backend.
