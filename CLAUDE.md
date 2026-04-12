# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**Aviates Air Tracker** is a Windows desktop application for Microsoft Flight Simulator (MSFS). It connects to MSFS via SimConnect at 20Hz, records flights in real-time, analyzes landings, and provides charts, maps, and career statistics for virtual airline pilots. The UI is **Blazor Hybrid** (Razor pages hosted in a WPF `BlazorWebView`), keeping WPF only as a thin shell for the Win32 HWND that SimConnect requires.

## Build Commands

```powershell
# Run in debug mode (development)
.\build.ps1 -Run

# Build release single-file EXE to dist/
.\build.ps1 -Release

# Clean build artifacts
.\build.ps1 -Clean
```

Direct dotnet commands:
```bash
dotnet restore AviatesAirTracker/AviatesAirTracker.csproj
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
dotnet publish AviatesAirTracker/AviatesAirTracker.csproj -c Release -r win-x64 \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist/
```

There are no automated tests. CI is handled by `.github/workflows/build.yml` (build → installer → release on version tags).

## Architecture

### Telemetry Pipeline (20Hz)

```
MSFS (SimConnect)
  → SimConnectManager (Win32 HWND hook + DispatcherTimer)
  → AircraftState struct (70 variables, SimConnectDefinitions.cs)
  → TelemetryProcessor (EMA filters + 600-sample history buffer)
  → FlightSessionManager (orchestrator)
      ├─ FlightPhaseDetector (state machine: Parked → Taxi → Takeoff → Cruise → Approach → Landing)
      ├─ LandingAnalyzer (6-point score: VS, pitch, bank, speed, crosswind, stability)
      ├─ RouteTracker (2Hz GPS path recording)
      ├─ FuelAnalyzer
      └─ DataRepository (in-memory, swappable via interfaces)
  → ViewModels → Blazor Razor Pages
```

### UI Navigation

`MainWindow.xaml` hosts a `BlazorWebView`. `BlazorApp.razor` is the Blazor root; `Shared/MainLayout.razor` is the sidebar navigation shell. Pages are in `Pages/` and route via standard `@page` directives:

| Page | Route |
|------|-------|
| Dashboard.razor | `/` |
| Flights.razor | `/flights` |
| Routes.razor | `/routes` |
| Fleet.razor | `/fleet` |
| PilotHub.razor | `/pilot-hub` |
| Acars.razor | `/acars` |
| Messages.razor | `/messages` |
| News.razor | `/news` |
| Events.razor | `/events` |
| Support.razor | `/support` |
| LiveFlight.razor | `/live-flight` |
| MapPage.razor | `/map` |
| LandingAnalysis.razor | `/landings` |
| Statistics.razor | `/statistics` |
| Replay.razor | `/replay` |
| TelemetryPage.razor | `/telemetry` |
| Settings.razor | `/settings` |

### MVVM + Blazor

ViewModels use CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`). In Razor pages, inject the ViewModel and call `await InvokeAsync(() => { Property = value; StateHasChanged(); })` for cross-thread updates from the SimConnect pump. **Do not call `StateHasChanged()` on every 20Hz tick** — batch every 3–5 samples (see `LiveFlightViewModel` which updates every 5 samples).

### Dependency Injection

All services are singletons registered in `App.xaml.cs`:
- **Data:** `IFlightRepository`, `IPilotRepository`, `ILandingRepository`, `IMessageRepository`, `IFriendRepository` → InMemory implementations
- **SimConnect:** `SimConnectManager`, `TelemetryProcessor`, `FlightPhaseDetector`
- **Analytics:** `LandingAnalyzer`, `ApproachMonitor`, `FuelAnalyzer`, `RunwayDetector`
- **Business:** `FlightSessionManager`, `RouteTracker`, `SimBriefService`, `ExportService`, `DiscordPresenceService`
- **Social:** `MessagingService`, `EventsService`, `RoutesService`, `SupportServices`
- **Backend:** `AviatesBackendClient`, `IApiService` → `NullApiService` (swap when backend is ready)
- **UI:** All 9 ViewModels + `MainWindow`

### SimConnect SDK Loading

`SimConnectManager.cs` loads `Microsoft.FlightSimulator.SimConnect.dll` **at runtime via reflection** — not a build-time reference. Both DLLs (`Libs/Microsoft.FlightSimulator.SimConnect.dll` and `Libs/SimConnect.dll`) must be present at runtime. `SimConnectStub.cs` provides fallback mode when MSFS is not running. Auto-reconnect fires every 5 seconds.

### Threading Model

| Thread | Role |
|--------|------|
| UI Thread | WPF/Blazor rendering, ViewModel property updates |
| SimConnect Pump | Win32 WndProc message dispatch (HWND hook) |
| 20Hz DispatcherTimer | `RequestDataOnSimObjectType` calls |
| Reconnect Timer | 5-second retry when MSFS not detected |
| Replay Timer | `System.Timers.Timer` for flight playback |
| Messaging Poll | 30-second interval for new messages from backend |
| Discord Presence | 10-second update interval |

### Map (Leaflet.js)

The map page uses Leaflet.js via JS interop. The full JS API lives in `wwwroot/js/interop.js` as `window.aviatesMap`. Call it from Razor via `IJSRuntime`:
```csharp
await JS.InvokeVoidAsync("aviatesMap.init", lat, lon);
await JS.InvokeVoidAsync("aviatesMap.updateAircraft", lat, lon, heading);
```
Always call `aviatesMap.init` on `OnAfterRenderAsync(firstRender)` and `aviatesMap.destroy` on `IAsyncDisposable.DisposeAsync`.

### Backend Integration

`Core/Backend/AviatesBackendClient.cs` is wired into the DI container. `Services/IApiService.cs` + `NullApiService.cs` provide a stub. `MessagingService` actively polls the backend every 30 seconds using the ACARS key as a Bearer token (stored in `SettingsService`). Swap `NullApiService` for a real implementation when endpoints are ready — no page changes required.

## Key File Locations

| File | Purpose |
|------|---------|
| `AviatesAirTracker/App.xaml.cs` | DI container setup, crash handlers, Serilog config |
| `AviatesAirTracker/BlazorApp.razor` | Blazor root / router |
| `AviatesAirTracker/Shared/MainLayout.razor` | Sidebar nav shell, unread message badge |
| `AviatesAirTracker/wwwroot/css/app.css` | All styles — brand colors `#0A0D17` bg, `#3D7EEE` accent |
| `AviatesAirTracker/wwwroot/js/interop.js` | Leaflet.js map JS interop (`window.aviatesMap`) |
| `Core/SimConnect/SimConnectManager.cs` | Runtime SDK loading, 20Hz polling, HWND hook |
| `Core/SimConnect/SimConnectDefinitions.cs` | 70-variable `AircraftState` struct |
| `Core/SimConnect/TelemetryProcessor.cs` | EMA filtering, history buffer |
| `Core/Analytics/FlightPhaseDetector.cs` | Flight phase state machine |
| `Core/Analytics/LandingAnalyzer.cs` | Landing scoring algorithm |
| `Core/Data/DataRepositories.cs` | Repository interfaces + InMemory implementations |
| `Core/Backend/AviatesBackendClient.cs` | HTTP client for Aviates Air backend |
| `Services/FlightSessionManager.cs` | Top-level orchestrator |
| `Services/MessagingService.cs` | Pilot messaging, friend codes, 30s backend poll |
| `Services/DiscordPresenceService.cs` | Discord Rich Presence, 10s update |
| `Services/RunwayDetector.cs` | Built-in runway database, touchdown identification |
| `Models/FlightModels.cs` | `FlightRecord`, `LandingResult`, and other domain models |
| `Controls/AviationControls.cs` | WPF vector instrument controls (CompassRose, etc.) |

## Technology Stack

- **.NET 8, `Microsoft.NET.Sdk.Razor`** (x64 Windows only), C# 12, nullable reference types enabled
- **WPF** retained only as HWND shell hosting `BlazorWebView`
- **CommunityToolkit.Mvvm** — `[ObservableProperty]`, `[RelayCommand]`
- **OxyPlot.Blazor** — charts in Razor pages; `OxyPlot.Wpf` kept for legacy XAML views
- **Leaflet.js** (CDN via `index.html`) — interactive map via JS interop
- **Mapsui.Wpf** — kept for legacy WPF MapView
- **RestSharp + Newtonsoft.Json** — SimBrief OFP import, backend HTTP
- **DiscordRichPresence** — Discord game presence
- **Serilog** — rolling daily logs in `logs/` (7-day retention)

## Frontend Design Rules

**Always invoke the `frontend-design` skill before writing any Razor/CSS/HTML.** These rules apply to all UI work:

- **Brand colors:** `#0A0D17` background, `#3D7EEE` accent. Never use default Tailwind palette.
- **Typography:** Pair a display/serif with a clean sans. Tight tracking (`-0.03em`) on large headings, generous line-height (`1.7`) on body.
- **Shadows:** Layered, color-tinted shadows with low opacity — never flat `shadow-md`.
- **Gradients:** Layer multiple radial gradients. Add grain/texture via SVG noise filter for depth.
- **Depth system:** Base → elevated → floating surface layers.
- **Animations:** Only animate `transform` and `opacity`. Never `transition-all`. Use spring-style easing.
- **Interactive states:** Every clickable element needs hover, `focus-visible`, and active states.
- **Reference images:** If `images/` contains assets (logos, color guides), use them — no placeholders where real assets exist.
