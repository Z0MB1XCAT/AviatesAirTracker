# Aviates Air Flight Tracker

**Professional MSFS flight tracker for Aviates Air virtual airline pilots.**

A fully-featured Windows desktop application that connects live to Microsoft Flight Simulator via SimConnect, records your flights in real-time, and generates detailed analytics and landing scores.

---

## Features

### Live Telemetry (20Hz)
- Full aircraft state: position, altitude, speeds, heading, pitch, bank
- Engine parameters (N1, N2, fuel flow × 4 engines)
- Autopilot modes and set values
- ILS localizer/glideslope deviation
- Wind components and weather
- All navigation radios and transponder

### Advanced Landing Analysis
- Vertical speed at touchdown
- Pitch and bank angle at touchdown  
- Crosswind and headwind components
- Flare detection
- Bounce detection with counter
- Rollout distance and deceleration profile
- Runway identification (major airports built-in)
- **Landing score out of 100** with breakdown:
  - Vertical speed (30 pts)
  - Pitch attitude (20 pts)
  - Bank angle (15 pts)
  - Speed management (15 pts)
  - Crosswind handling (10 pts)
  - Approach stability (10 pts)

### Flight Phase Detection
Automatic classification: Parked → Taxi → Takeoff → Initial Climb → Climb → Cruise → Top of Descent → Descent → Approach → Final Approach → Landing → Rollout → Vacating

### SimBrief Integration
- Fetch latest OFP directly by username
- Import XML or JSON OFP files
- Compare planned vs actual: route, distance, fuel, time

### Route Tracking
- Live GPS path recording (2Hz)
- Planned route overlay from SimBrief
- Waypoint progress tracking
- Total distance accumulation

### World Map
- OpenStreetMap base layer
- Live aircraft position marker
- Actual flight path (blue solid line)
- Planned route (green dashed line)

### Charts and Telemetry
- Live mini-charts: Altitude, IAS, VS, N1 (rolling 200 samples)
- Full telemetry page: 7 charts including pitch, bank, fuel

### Flight Replay
- Play back any completed flight
- Adjustable speed (1×, 2×, 4×, 8×)
- Live altitude, speed, phase display during replay

### Pilot Statistics
- Total flights, block hours, distance, fuel used
- Average and best landing scores
- Rank progression system
- Full flight log table

### Export
- JSON flight record export
- CSV landing log
- CSV telemetry path
- ACARS PIREP text format

---

## Requirements

| Requirement | Version |
|---|---|
| Windows | 10 or 11 (64-bit) |
| .NET SDK | 8.0+ |
| MSFS | 2020 or 2024 |
| SimConnect SDK | Included with MSFS Developer Mode |

---

## Setup

### 1. Get the SimConnect DLL

> **Important:** The MSFS SDK ships *two* SimConnect files. You need the **managed .NET wrapper**, not the native C++ DLL.

| File | Type | What to do |
|---|---|---|
| `SimConnect.dll` | Native C++ — **wrong file** | Do not copy this |
| `Microsoft.FlightSimulator.SimConnect.dll` | Managed .NET wrapper — **correct file** | Copy this to `Libs\` |

Enable Developer Mode in MSFS, then install the SDK. The managed wrapper is at:

```
C:\MSFS SDK\SimConnect SDK\lib\managed\Microsoft.FlightSimulator.SimConnect.dll
```

Copy it to:

```
AviatesAirTracker\Libs\Microsoft.FlightSimulator.SimConnect.dll
```

If you cannot locate the managed wrapper, the project builds fine in **stub mode** — the full UI runs, all analytics work, and a log message explains that live telemetry is disabled.

### 2. Build

```powershell
# Debug run (development)
.\build.ps1 -Run

# Release single EXE
.\build.ps1 -Release
```

The published EXE will be in `dist\AviatesAirTracker.exe` (~150MB self-contained).

### 3. First Launch

1. Start MSFS first
2. Launch AviatesAirTracker.exe
3. The status bar will show **MSFS Connected** when SimConnect establishes
4. Enter your Pilot Name and SimBrief username in **Settings**
5. Load your SimBrief OFP before flight (top toolbar or Settings)

---

## Architecture

```
AviatesAirTracker/
├── Core/
│   ├── SimConnect/
│   │   ├── SimConnectDefinitions.cs   ← 70-variable AircraftState struct
│   │   ├── SimConnectManager.cs       ← Connection + 20Hz polling
│   │   └── TelemetryProcessor.cs      ← EMA filters + history buffer
│   ├── Analytics/
│   │   ├── FlightPhaseDetector.cs     ← Phase state machine
│   │   └── LandingAnalyzer.cs         ← Scoring + bounce detection
│   └── Data/
│       └── DataRepositories.cs        ← Interfaces + InMemory impls
├── Models/
│   └── FlightModels.cs               ← FlightRecord, LandingResult, etc.
├── Services/
│   ├── FlightSessionManager.cs        ← Top-level orchestrator
│   ├── RouteTracker.cs
│   ├── SimBriefService.cs
│   ├── ExportService.cs
│   ├── RunwayDetector.cs
│   └── SupportServices.cs             ← Fuel, Alerts, Settings, Stats
├── ViewModels/
│   ├── MainViewModel.cs               ← Navigation + top-level state
│   └── ChildViewModels.cs             ← Per-page VMs
├── Views/
│   ├── DashboardView.xaml
│   ├── LiveFlightView.xaml
│   ├── MapView.xaml
│   ├── LandingAnalysisView.xaml
│   ├── StatisticsView.xaml
│   ├── ReplayView.xaml
│   ├── TelemetryView.xaml
│   ├── SettingsView.xaml
│   └── ViewCodeBehinds.cs
├── Resources/Styles/
│   ├── AviatesTheme.xaml              ← Colors (#0A0D17 bg, #3D7EEE accent)
│   ├── Controls.xaml                  ← Buttons, inputs, grids
│   ├── Typography.xaml                ← Text styles + badges
│   └── Animations.xaml                ← Fade, slide, pulse
├── Converters/
│   ├── ValueConverters.cs
│   └── ExtraConverters.cs
├── MainWindow.xaml                    ← Navigation shell
└── App.xaml.cs                        ← DI container + startup
```

### Threading Model

| Thread | Responsibility |
|---|---|
| UI Thread | WPF rendering, ViewModel property updates |
| SimConnect Pump | Win32 WndProc message dispatch (HWND hook) |
| 20Hz Poll Timer | `DispatcherTimer` fires `RequestDataOnSimObjectType` |
| Reconnect Timer | 5-second auto-reconnect when MSFS not running |
| Replay Timer | `System.Timers.Timer` for flight playback |
| Map Refresh | Background task, 500ms, `Dispatcher.BeginInvoke` |

### Data Layer (Backend-Ready)

All data flows through interfaces:

```csharp
IFlightRepository   // Save/load FlightRecord
IPilotRepository    // Pilot profile + statistics  
ILandingRepository  // LandingResult history
```

**Current:** `InMemoryFlightRepository` etc. (in-process, no persistence)

**Future:** Replace with `ApiFlightRepository` that POSTs to  
`https://api.aviatesair.com` using the pilot's ACARS key.  
All ViewModels depend only on the interfaces — zero UI changes required.

---

## Backend Integration Plan

When the Aviates Air backend SQL database is ready:

1. Create `ApiFlightRepository : IFlightRepository` that calls the REST API
2. Create `ApiLandingRepository : ILandingRepository`
3. Update DI in `App.xaml.cs` — swap `InMemory*` for `Api*`
4. Pass ACARS key from `SettingsService` as Authorization header

The system already stores the ACARS key field in settings.

---

## Extending the Runway Database

Edit `RunwayDetector.cs` → `LoadBuiltInRunways()`.

Each entry:
```csharp
AddRunways("ICAO", new[]
{
    ("RUNWAY_ID", heading_degrees, thresh_lat, thresh_lon, end_lat, end_lon, length_ft),
});
```

For production accuracy, replace with a Navigraph NavData or MSFS airport data query.

---

## Landing Score Reference

| Score | Grade | Typical VS |
|---|---|---|
| 90–100 | EXCELLENT | −100 to −250 fpm |
| 75–89  | GOOD      | −250 to −400 fpm |
| 60–74  | FAIR      | −400 to −600 fpm |
| 40–59  | HARD      | −600 to −800 fpm |
| 0–39   | VERY HARD | below −800 fpm   |

---

## License

© 2026 Aviates Air. All rights reserved.  
For use by registered Aviates Air virtual airline pilots only.
