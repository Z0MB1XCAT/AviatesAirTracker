# Cabin Announcement System Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Automatically play cabin PA announcement MP3s at the right point in a flight, driven by SimConnect telemetry and `FlightSessionManager` state — no manual triggering.

**Architecture:** A new `CabinAnnouncementService` singleton subscribes to `FlightSessionManager.TelemetryUpdated` (20Hz), `SessionStateChanged`, `FlightCompleted`, and `LandingAnalyzer.LandingDetected`. It raises a plain C# event with a filename; `MainLayout.razor` (always mounted) forwards that to a new `window.aviatesCabin` JS queue that plays `HTMLAudioElement`s with a 25s gap between clips. Two new SimConnect variables (`PUSHBACK STATE`, `CABIN SEATBELTS ALERT SWITCH`) are appended to the end of the `AircraftState` struct.

**Tech Stack:** .NET 8 / C#, Blazor Hybrid (Razor + JS interop), SimConnect (via runtime reflection).

**Reference:** Full design rationale is in `docs/superpowers/specs/2026-07-12-cabin-announcements-design.md`. This plan implements that spec exactly — read it first if any trigger threshold here seems unmotivated.

**No automated test suite exists in this project** (confirmed in `CLAUDE.md`). Every task's verification step is `dotnet build`, which catches compile errors and (for the reflection-based SimConnect registration) is the only automatable check available. The final task is a manual, in-app smoke-test checklist.

---

### Task 1: New SimConnect variables — struct fields + registration

**Files:**
- Modify: `AviatesAirTracker/Core/SimConnect/SimConnectDefinitions.cs`
- Modify: `AviatesAirTracker/Core/SimConnect/SimConnectManager.cs`

- [ ] **Step 1: Append the two new raw fields to `AircraftState`**

In `SimConnectDefinitions.cs`, find the end of the `AircraftState` struct:

```csharp
    public double PlaneInParkingState;     // PLANE IN PARKING STATE
}
```

Replace with (new fields **appended last** — never insert mid-struct, a rejected/unsupported variable silently shifts every later field, which previously broke landing detection):

```csharp
    public double PlaneInParkingState;     // PLANE IN PARKING STATE

    // ---- CABIN ANNOUNCEMENTS (appended — must stay last in the struct) ----
    public double PushbackState;           // PUSHBACK STATE (0=straight,1=left,2=right,3=none)
    public double CabinSeatbeltsAlertSwitch; // CABIN SEATBELTS ALERT SWITCH (bool)
}
```

- [ ] **Step 2: Add derived `TelemetrySnapshot` properties**

In the same file, find:

```csharp
    public bool ParkingBrakeSet => Raw.ParkingBrake > 0.5;
    public bool EnginesRunning => Raw.EngineRunning_1 > 0.5 || Raw.EngineRunning_2 > 0.5;
```

Replace with:

```csharp
    public bool ParkingBrakeSet => Raw.ParkingBrake > 0.5;
    public bool EnginesRunning => Raw.EngineRunning_1 > 0.5 || Raw.EngineRunning_2 > 0.5;
    public bool OnAnyRunway => Raw.OnGroundAnimation > 0.5;
    public bool SeatbeltSignOn => Raw.CabinSeatbeltsAlertSwitch > 0.5;
```

- [ ] **Step 3: Register the two new variables in SimConnect, as the last entries before the struct is registered**

In `SimConnectManager.cs`, find:

```csharp
        Add(DEF_AIRCRAFT_STATE, "PLANE IN PARKING STATE", "bool");

        // Register the state struct
```

Replace with:

```csharp
        Add(DEF_AIRCRAFT_STATE, "PLANE IN PARKING STATE", "bool");
        // ---- Cabin announcements (must stay last — see AircraftState struct comment) ----
        Add(DEF_AIRCRAFT_STATE, "PUSHBACK STATE",               "number");
        Add(DEF_AIRCRAFT_STATE, "CABIN SEATBELTS ALERT SWITCH", "bool");

        // Register the state struct
```

- [ ] **Step 4: Update the registered-variable count in the log line**

Find:

```csharp
        Log.Information("[SimConnect] Data definitions registered ({Count} vars + ident)", 93);
```

Replace with:

```csharp
        Log.Information("[SimConnect] Data definitions registered ({Count} vars + ident)", 95);
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 6: Commit**

```bash
git add AviatesAirTracker/Core/SimConnect/SimConnectDefinitions.cs AviatesAirTracker/Core/SimConnect/SimConnectManager.cs
git commit -m "feat(simconnect): add pushback state and seatbelt sign telemetry vars"
```

---

### Task 2: Settings fields for cabin announcements

**Files:**
- Modify: `AviatesAirTracker/Services/SupportServices.cs`

- [ ] **Step 1: Add the two new settings to `AppSettings`**

Find:

```csharp
    // UI theme preference. "system" follows OS dark/light mode automatically.
    public string Theme { get; set; } = "system"; // "system" | "light" | "dark"
```

Replace with:

```csharp
    // UI theme preference. "system" follows OS dark/light mode automatically.
    public string Theme { get; set; } = "system"; // "system" | "light" | "dark"
    // Automatic cabin PA announcements (welcome, safety briefing, seatbelt sign, etc.)
    public bool CabinAnnouncementsEnabled { get; set; } = true;
    public int CabinAnnouncementsVolume { get; set; } = 80; // 0-100
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 3: Commit**

```bash
git add AviatesAirTracker/Services/SupportServices.cs
git commit -m "feat(settings): add cabin announcements enabled/volume settings"
```

---

### Task 3: CabinAnnouncementService

**Files:**
- Create: `AviatesAirTracker/Services/CabinAnnouncementService.cs`
- Modify: `AviatesAirTracker/App.xaml.cs`

- [ ] **Step 1: Create the service**

Write the full file:

```csharp
using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Models;
using Serilog;

namespace AviatesAirTracker.Services;

// ============================================================
// CABIN ANNOUNCEMENT SERVICE
//
// Listens to the live telemetry/session pipeline and raises
// AnnouncementRequested(fileName) for each cabin PA cue described in
// docs/superpowers/specs/2026-07-12-cabin-announcements-design.md.
// Files are resolved by the JS side under wwwroot/audio/cabin/.
// Does not touch FlightPhaseDetector's state machine — reads snap.Phase,
// which TelemetryProcessor already populates before TelemetryUpdated fires.
// ============================================================

public class CabinAnnouncementService
{
    public event EventHandler<string>? AnnouncementRequested;

    private readonly FlightSessionManager _sessionManager;
    private readonly LandingAnalyzer _landingAnalyzer;
    private readonly SettingsService _settings;

    // One-shot flags — reset on FlightCompleted
    private bool _welcomePlayed;
    private bool _safetyBriefingPlayed;
    private bool _cabinSecureTakeoffPlayed;
    private bool _seatsTakeoffPlayed;
    private bool _afterTakeoffPlayed;
    private bool _cabinSecureLandingPlayed;
    private bool _seatsLandingPlayed;
    private bool _landingAnnouncementPlayed;
    private bool _disarmedDoorsPlayed;

    // Edge-detection baselines
    private double? _lastPushbackState;
    private bool? _lastSeatbeltState;
    private DateTime _lastSeatbeltAnnouncementAt = DateTime.MinValue;
    private bool _lastEnginesRunning;

    private static readonly TimeSpan SeatbeltDebounce = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DeboardingDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LandingAnnouncementDelay = TimeSpan.FromSeconds(150);

    private CancellationTokenSource _flightCts = new();

    public CabinAnnouncementService(
        FlightSessionManager sessionManager,
        LandingAnalyzer landingAnalyzer,
        SettingsService settings)
    {
        _sessionManager = sessionManager;
        _landingAnalyzer = landingAnalyzer;
        _settings = settings;

        _sessionManager.TelemetryUpdated += OnTelemetryUpdated;
        _sessionManager.SessionStateChanged += OnSessionStateChanged;
        _sessionManager.FlightCompleted += OnFlightCompleted;
        _landingAnalyzer.LandingDetected += OnLandingDetected;
    }

    private void Request(string fileName)
    {
        if (!_settings.Settings.CabinAnnouncementsEnabled) return;
        Log.Information("[CabinAnnouncements] Requesting {File}", fileName);
        AnnouncementRequested?.Invoke(this, fileName);
    }

    // =====================================================
    // SESSION STATE — ground-side proxy cues
    // =====================================================

    private void OnSessionStateChanged(object? sender, FlightSessionState state)
    {
        switch (state)
        {
            case FlightSessionState.PreFlight:
                if (!_welcomePlayed)
                {
                    _welcomePlayed = true;
                    Request("welcome.mp3");
                }
                break;

            case FlightSessionState.Taxiing:
                if (!_cabinSecureTakeoffPlayed)
                {
                    _cabinSecureTakeoffPlayed = true;
                    Request("cabin_secure_takeoff.mp3");
                }
                break;
        }
    }

    private void OnFlightCompleted(object? sender, FlightRecord flight)
    {
        _flightCts.Cancel();
        _flightCts = new CancellationTokenSource();

        _welcomePlayed = false;
        _safetyBriefingPlayed = false;
        _cabinSecureTakeoffPlayed = false;
        _seatsTakeoffPlayed = false;
        _afterTakeoffPlayed = false;
        _cabinSecureLandingPlayed = false;
        _seatsLandingPlayed = false;
        _landingAnnouncementPlayed = false;
        _disarmedDoorsPlayed = false;

        _lastPushbackState = null;
        _lastSeatbeltState = null;
        _lastEnginesRunning = false;
    }

    // =====================================================
    // TELEMETRY — 20Hz driven cues
    // =====================================================

    private void OnTelemetryUpdated(object? sender, TelemetrySnapshot snap)
    {
        CheckPushback(snap);
        CheckSeatsForTakeoff(snap);
        CheckAfterTakeoff(snap);
        CheckSeatbeltSign(snap);
        CheckCabinSecureLanding(snap);
        CheckSeatsForLanding(snap);
        CheckEnginesOff(snap);
    }

    private void CheckPushback(TelemetrySnapshot snap)
    {
        double current = snap.Raw.PushbackState;

        // Require a real "no pushback" (3) baseline before trusting a transition into
        // 0/1/2 — guards against a rejected/unsupported sim var reading a spurious 0
        // on the very first sample (some liveries never report PUSHBACK STATE at all).
        if (_lastPushbackState is 3 && current is >= 0 and < 2.5 && !_safetyBriefingPlayed)
        {
            _safetyBriefingPlayed = true;
            Request("safety_briefing.mp3");
        }

        _lastPushbackState = current;
    }

    private void CheckSeatsForTakeoff(TelemetrySnapshot snap)
    {
        if (_seatsTakeoffPlayed) return;
        if (_sessionManager.State != FlightSessionState.Taxiing) return;
        if (!snap.OnAnyRunway) return;
        if (snap.GroundSpeedKts > 30) return;

        _seatsTakeoffPlayed = true;
        Request("seats_takeoff.mp3");
    }

    private void CheckAfterTakeoff(TelemetrySnapshot snap)
    {
        if (_afterTakeoffPlayed) return;
        if (snap.Phase is not (FlightPhase.InitialClimb or FlightPhase.Climb)) return;
        if (snap.AltitudeAGL < 2500) return;

        _afterTakeoffPlayed = true;
        Request("after_takeoff.mp3");
    }

    private void CheckSeatbeltSign(TelemetrySnapshot snap)
    {
        bool current = snap.SeatbeltSignOn;
        bool risingEdge = _lastSeatbeltState == false && current;
        _lastSeatbeltState = current;

        if (!risingEdge) return;
        if (DateTime.UtcNow - _lastSeatbeltAnnouncementAt < SeatbeltDebounce) return;

        bool descending = snap.Phase is FlightPhase.TopOfDescent or FlightPhase.Descent
                                       or FlightPhase.Approach or FlightPhase.FinalApproach;
        bool climbing = snap.Phase is FlightPhase.Climb or FlightPhase.Cruise;

        if (!descending && !climbing) return;

        _lastSeatbeltAnnouncementAt = DateTime.UtcNow;
        Request(descending ? "seatbelt_sign_on_descending.mp3" : "seatbelt_sign_on.mp3");
    }

    private void CheckCabinSecureLanding(TelemetrySnapshot snap)
    {
        if (_cabinSecureLandingPlayed) return;
        if (snap.Phase is not (FlightPhase.TopOfDescent or FlightPhase.Descent
                             or FlightPhase.Approach or FlightPhase.FinalApproach)) return;

        var plan = _sessionManager.CurrentFlight?.PlannedFlight;
        if (plan is { ArrivalLat: not 0, ArrivalLon: not 0 } && snap.GroundSpeedKts > 20)
        {
            double distanceNm = HaversineNm(snap.Latitude, snap.Longitude, plan.ArrivalLat, plan.ArrivalLon);
            double etaMinutes = distanceNm / snap.GroundSpeedKts * 60.0;

            if (etaMinutes <= 15)
            {
                _cabinSecureLandingPlayed = true;
                Request("cabin_secure_landing.mp3");
            }
            return;
        }

        // No SimBrief plan loaded — fall back to a fixed AGL threshold, roughly
        // matching a 10-15 min out call on a typical descent profile.
        if (snap.AltitudeAGL <= 10000 && snap.AltitudeAGL > 0)
        {
            _cabinSecureLandingPlayed = true;
            Request("cabin_secure_landing.mp3");
        }
    }

    private void CheckSeatsForLanding(TelemetrySnapshot snap)
    {
        if (_seatsLandingPlayed) return;
        if (snap.Phase is not (FlightPhase.Approach or FlightPhase.FinalApproach)) return;
        if (snap.AltitudeAGL > 3000) return;

        _seatsLandingPlayed = true;
        Request("seats_landing.mp3");
    }

    private void CheckEnginesOff(TelemetrySnapshot snap)
    {
        bool running = snap.EnginesRunning;
        bool fallingEdge = _lastEnginesRunning && !running;
        _lastEnginesRunning = running;

        if (!fallingEdge || _disarmedDoorsPlayed) return;
        if (_sessionManager.State is not (FlightSessionState.Landed or FlightSessionState.Complete)) return;

        _disarmedDoorsPlayed = true;
        Request("disarmed_doors.mp3");

        var token = _flightCts.Token;
        _ = Task.Delay(DeboardingDelay, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Request("deboarding.mp3");
        }, TaskScheduler.Default);
    }

    // =====================================================
    // LANDING TIMER
    // =====================================================

    private void OnLandingDetected(object? sender, LandingResult landing)
    {
        if (_landingAnnouncementPlayed) return;
        _landingAnnouncementPlayed = true;

        var token = _flightCts.Token;
        _ = Task.Delay(LandingAnnouncementDelay, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Request("landing_announcement.mp3");
        }, TaskScheduler.Default);
    }

    // =====================================================
    // GEO HELPER
    // =====================================================

    private static double HaversineNm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 3440.065; // Earth radius in nm
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }
}
```

- [ ] **Step 2: Register the service in DI**

In `App.xaml.cs`, find:

```csharp
        services.AddSingleton<FlightSessionManager>();
```

Replace with:

```csharp
        services.AddSingleton<FlightSessionManager>();
        services.AddSingleton<CabinAnnouncementService>();
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add AviatesAirTracker/Services/CabinAnnouncementService.cs AviatesAirTracker/App.xaml.cs
git commit -m "feat(cabin): add CabinAnnouncementService with all 10 trigger cues"
```

---

### Task 4: JS audio queue

**Files:**
- Modify: `AviatesAirTracker/wwwroot/js/interop.js`

- [ ] **Step 1: Add the `aviatesCabin` module**

Find the end of the existing `aviatesAudio` module:

```js
    // Three-tone ascending chime (C5 → E5 → G5) as fallback
    _playChime: function (ctx) {
        var freqs = [523.25, 659.25, 783.99];
        freqs.forEach(function (freq, i) {
            var osc  = ctx.createOscillator();
            var gain = ctx.createGain();
            osc.type = 'sine';
            osc.frequency.value = freq;
            gain.gain.setValueAtTime(0, ctx.currentTime + i * 0.35);
            gain.gain.linearRampToValueAtTime(0.4, ctx.currentTime + i * 0.35 + 0.05);
            gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + i * 0.35 + 0.55);
            osc.connect(gain);
            gain.connect(ctx.destination);
            osc.start(ctx.currentTime + i * 0.35);
            osc.stop(ctx.currentTime + i * 0.35 + 0.6);
        });
    },
};
```

Replace with (adds the new module right after, leaves `aviatesAudio` untouched):

```js
    // Three-tone ascending chime (C5 → E5 → G5) as fallback
    _playChime: function (ctx) {
        var freqs = [523.25, 659.25, 783.99];
        freqs.forEach(function (freq, i) {
            var osc  = ctx.createOscillator();
            var gain = ctx.createGain();
            osc.type = 'sine';
            osc.frequency.value = freq;
            gain.gain.setValueAtTime(0, ctx.currentTime + i * 0.35);
            gain.gain.linearRampToValueAtTime(0.4, ctx.currentTime + i * 0.35 + 0.05);
            gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + i * 0.35 + 0.55);
            osc.connect(gain);
            gain.connect(ctx.destination);
            osc.start(ctx.currentTime + i * 0.35);
            osc.stop(ctx.currentTime + i * 0.35 + 0.6);
        });
    },
};

// ── Cabin announcement queue ──────────────────────────
// Plays cabin PA MP3s (wwwroot/audio/cabin/) one at a time, in the order
// requested, with a fixed gap between clips so back-to-back cues don't
// feel rushed. A missing/undecodable file is skipped, not retried.
window.aviatesCabin = {
    _queue: [],
    _playing: false,
    _gapMs: 25000,

    enqueue: function (url, volumePct) {
        this._queue.push({ url: url, volume: (volumePct == null ? 80 : volumePct) / 100 });
        if (!this._playing) this._playNext();
    },

    _playNext: function () {
        var self = this;
        var item = this._queue.shift();
        if (!item) { this._playing = false; return; }

        this._playing = true;
        var audio = new Audio(item.url);
        audio.volume = Math.min(1, Math.max(0, item.volume));

        var advance = function () {
            setTimeout(function () { self._playNext(); }, self._gapMs);
        };

        audio.addEventListener('ended', advance);
        audio.addEventListener('error', function () {
            console.warn('[aviatesCabin] Failed to load ' + item.url);
            self._playNext();
        });

        audio.play().catch(function () {
            console.warn('[aviatesCabin] Playback blocked for ' + item.url);
            self._playNext();
        });
    }
};
```

- [ ] **Step 2: Manually verify the JS loads without syntax errors**

Run: `.\build.ps1 -Run` (or open the app if already running), then open the app's dev tools console (right-click the Blazor view if available, or check `logs/` for a WebView2 script error). Confirm no `Uncaught SyntaxError` referencing `interop.js`.
Expected: app launches normally, no console errors mentioning `aviatesCabin`.

- [ ] **Step 3: Commit**

```bash
git add AviatesAirTracker/wwwroot/js/interop.js
git commit -m "feat(cabin): add aviatesCabin JS playback queue"
```

---

### Task 5: Wire MainLayout to the service

**Files:**
- Modify: `AviatesAirTracker/Shared/MainLayout.razor`

- [ ] **Step 1: Inject the service**

Find:

```razor
@inject AviatesAirTracker.Core.SimConnect.SimConnectManager SimConnectMgr
@inject IJSRuntime JS
```

Replace with:

```razor
@inject AviatesAirTracker.Core.SimConnect.SimConnectManager SimConnectMgr
@inject CabinAnnouncementService CabinAnnouncements
@inject IJSRuntime JS
```

- [ ] **Step 2: Subscribe in `OnInitialized`**

Find:

```csharp
        SimConnectMgr.ConnectionStatusChanged += OnSimConnectionChanged;
        _connStatus = SimConnectMgr.ConnectionStatus;
        RefreshHeader();
```

Replace with:

```csharp
        SimConnectMgr.ConnectionStatusChanged += OnSimConnectionChanged;
        CabinAnnouncements.AnnouncementRequested += OnCabinAnnouncementRequested;
        _connStatus = SimConnectMgr.ConnectionStatus;
        RefreshHeader();
```

- [ ] **Step 3: Add the handler**

Find:

```csharp
    private void OnSimConnectionChanged(object? sender, SimConnectionStatus status)
    {
        _ = InvokeAsync(() => { _connStatus = status; StateHasChanged(); });
    }
```

Replace with:

```csharp
    private void OnSimConnectionChanged(object? sender, SimConnectionStatus status)
    {
        _ = InvokeAsync(() => { _connStatus = status; StateHasChanged(); });
    }

    private void OnCabinAnnouncementRequested(object? sender, string fileName)
    {
        _ = InvokeAsync(async () =>
        {
            try
            {
                var url = $"audio/cabin/{fileName}";
                var volume = SettingsService.Settings.CabinAnnouncementsVolume;
                await JS.InvokeVoidAsync("aviatesCabin.enqueue", url, volume);
            }
            catch
            {
                // JS interop can fail during shutdown/navigation — non-fatal.
            }
        });
    }
```

- [ ] **Step 4: Unsubscribe in `DisposeAsync`**

Find:

```csharp
        SimConnectMgr.ConnectionStatusChanged -= OnSimConnectionChanged;
        MessagingService.UnreadCountChanged   -= OnUnreadCountChanged;
```

Replace with:

```csharp
        SimConnectMgr.ConnectionStatusChanged -= OnSimConnectionChanged;
        CabinAnnouncements.AnnouncementRequested -= OnCabinAnnouncementRequested;
        MessagingService.UnreadCountChanged   -= OnUnreadCountChanged;
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 6: Commit**

```bash
git add AviatesAirTracker/Shared/MainLayout.razor
git commit -m "feat(cabin): wire MainLayout to play requested cabin announcements"
```

---

### Task 6: Settings page UI

**Files:**
- Modify: `AviatesAirTracker/Pages/Settings.razor`

- [ ] **Step 1: Invoke the frontend-design skill**

This project's `CLAUDE.md` requires invoking the `frontend-design` skill before writing any Razor/CSS/HTML. Do that now, then apply its guidance to the block below (brand colors `#0A0D17`/`#3D7EEE`, existing `--accent` CSS variable, spring-style transitions) while keeping the diff scoped to this one panel — do not restyle the rest of the page.

- [ ] **Step 2: Add the Cabin Announcements panel**

Find the end of the existing Preferences panel:

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

Replace with (closes the Preferences panel, then opens a new one):

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

        <!-- Cabin Announcements -->
        <div class="panel-card anim-up-d2">
            <div class="form-section-title">Cabin Announcements</div>
            <div class="toggle-row">
                <div class="toggle-info">
                    <div class="toggle-label">Automatic Cabin Announcements</div>
                    <div class="toggle-desc">Play welcome, safety briefing, seatbelt sign, and other PA cues automatically during a flight</div>
                </div>
                <label class="toggle-switch">
                    <input type="checkbox" @bind="SettingsService.Settings.CabinAnnouncementsEnabled" />
                    <span class="toggle-track"></span>
                </label>
            </div>
            <div class="toggle-row" style="border-bottom:none;">
                <div class="toggle-info">
                    <div class="toggle-label">Volume</div>
                    <div class="toggle-desc">@SettingsService.Settings.CabinAnnouncementsVolume%</div>
                </div>
                <input type="range" min="0" max="100"
                       style="width:140px;accent-color:var(--accent);"
                       @bind="SettingsService.Settings.CabinAnnouncementsVolume"
                       @bind:event="oninput" />
            </div>
        </div>
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add AviatesAirTracker/Pages/Settings.razor
git commit -m "feat(cabin): add cabin announcements settings UI"
```

---

### Task 7: Audio file drop-in folder

**Files:**
- Create: `AviatesAirTracker/wwwroot/audio/cabin/README.md`

- [ ] **Step 1: Create the manifest**

```markdown
# Cabin Announcement Audio Files

Drop your recorded MP3s here using these exact filenames — `CabinAnnouncementService`
requests them by name and the JS player resolves them relative to this folder.
A missing file is skipped silently (logged as a warning in the browser console),
so the app runs fine before any of these exist.

| File | Plays when |
|---|---|
| `welcome.mp3` | Engines start |
| `safety_briefing.mp3` | Pushback begins |
| `cabin_secure_takeoff.mp3` | Taxi begins |
| `seats_takeoff.mp3` | Aircraft lines up on the runway |
| `after_takeoff.mp3` | Climbing through ~2,500 ft AGL |
| `seatbelt_sign_on.mp3` | Seatbelt sign turned on during climb/cruise |
| `seatbelt_sign_on_descending.mp3` | Seatbelt sign turned on during descent/approach |
| `cabin_secure_landing.mp3` | ~10-15 min from touchdown (ETA-based, or 10,000 ft AGL if no SimBrief plan loaded) |
| `seats_landing.mp3` | Descending through 3,000 ft AGL on approach |
| `landing_announcement.mp3` | 2.5 min after touchdown |
| `disarmed_doors.mp3` | Engines shut down after landing |
| `deboarding.mp3` | 20s after `disarmed_doors.mp3` |

See `docs/superpowers/specs/2026-07-12-cabin-announcements-design.md` for the
full trigger design and rationale.
```

- [ ] **Step 2: Commit**

```bash
git add AviatesAirTracker/wwwroot/audio/cabin/README.md
git commit -m "docs(cabin): add audio drop-in folder with filename manifest"
```

---

### Task 8: Full build and manual smoke test

**Files:** none (verification only)

- [ ] **Step 1: Full solution build**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: `Build succeeded. 0 Error(s)` (warnings pre-existing/unrelated are fine — do not introduce new ones from this feature's files)

- [ ] **Step 2: Launch and confirm the settings toggle**

Run: `.\build.ps1 -Run`
Navigate to Settings → confirm the "Cabin Announcements" panel appears, the toggle and slider respond, and clicking Save persists them (reopen Settings after restart to confirm).

- [ ] **Step 3: Smoke-test with MSFS running**

Without any MP3s in place yet, start a flight and watch `logs/` (or the console) for `[CabinAnnouncements] Requesting ...` lines appearing at the expected points:
- On engine start → `welcome.mp3`
- On pushback (if the aircraft reports `PUSHBACK STATE`) → `safety_briefing.mp3`
- On taxi start → `cabin_secure_takeoff.mp3`
- Lined up on the runway → `seats_takeoff.mp3`
- Climbing through ~2,500 ft AGL → `after_takeoff.mp3`
- Toggling the seatbelt sign in cruise → `seatbelt_sign_on.mp3`; during descent → `seatbelt_sign_on_descending.mp3`
- ~10-15 min from destination → `cabin_secure_landing.mp3`
- Descending through 3,000 ft AGL on approach → `seats_landing.mp3`
- ~2.5 min after touchdown → `landing_announcement.mp3`
- Shutting down engines at the gate → `disarmed_doors.mp3`, then `deboarding.mp3` 20s later

Expected: each line appears once per flight, in a plausible order, and disabling the Settings toggle mid-flight stops new lines from appearing.

- [ ] **Step 4: Drop in one real MP3 and confirm playback**

Place any short MP3 as `AviatesAirTracker/wwwroot/audio/cabin/welcome.mp3`, restart the app, start engines, and confirm audio actually plays through system output at the configured volume.
Expected: audio plays once, at the Settings-configured volume, with no console errors.
