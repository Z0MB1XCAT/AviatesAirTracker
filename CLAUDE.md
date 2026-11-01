# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**Aviates Air Tracker** is a Windows desktop application for Microsoft Flight Simulator (MSFS). It connects to MSFS via SimConnect at 20Hz, records flights in real-time, analyzes landings, and provides charts, maps, and career statistics for virtual airline pilots. The UI is **native WinUI 3** (`AviatesAirTracker.csproj` targets `net8.0-windows10.0.19041.0` with `UseWinUI=true`, plain `Microsoft.NET.Sdk`) — there is no WPF, no Blazor, and no Razor anywhere in the app; XAML pages navigate inside a native `Frame` hosted by `ShellWindow`.

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
dotnet publish AviatesAirTracker/AviatesAirTracker.csproj -c Release -r win-x64 --self-contained true -o dist/
```

Note: `PublishSingleFile` is **not** used — the Windows App SDK doesn't support it, so `dist/` is a multi-file folder tree (runtime, WindowsAppSDK, SimConnect DLLs, etc.), not one self-extracting EXE. `AviatesAirTracker.sln` has two projects — the app and `tests/AviatesAirTracker.Tests` — `build.ps1` targets the app `.csproj` directly (tests run separately via `dotnet test`).

Automated tests exist: `tests/AviatesAirTracker.Tests` is a real xUnit project (`dotnet test tests/AviatesAirTracker.Tests/AviatesAirTracker.Tests.csproj`) with dozens of `[Fact]`/`[Theory]` tests covering things like the native voice pipeline's Opus codec round-trip and radio-degradation DSP, the `VoiceChannelClient` `/voice` wire protocol, flights/pilot-stats filtering logic, `ShellWindow`'s unread-badge logic, and the `IUiDispatcher`/`WindowSubclass` platform plumbing. CI is handled by `.github/workflows/build.yml` (build → installer → release on version tags).

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
  → ViewModels → native WinUI 3 pages, via `ShellWindow`'s `Frame` navigation
```

### UI Navigation

`ShellWindow.xaml` hosts a `NavigationView` (its rail items built from `NavDestinations.All`) and a `Frame` (`NativeHost`) that navigates directly between native `Page` types — there is no web host, routing table, or URL scheme involved. `AviatesAirTracker/Navigation/NavDestinations.cs` is the single source of truth for the rail: each `NavDestination` carries a `NativePageType` that `ShellWindow.Navigate` passes straight to `NativeHost.Navigate(...)`. The current destinations:

| Tag | Label | Group | Native Page |
|-----|-------|-------|-------------|
| dashboard | Dashboard | Overview | `Pages.DashboardPage` |
| flights | Flights | Overview | `Pages.FlightsPage` |
| routes | Routes | Overview | `Pages.RoutesPage` |
| fleet | Fleet | Overview | `Pages.FleetPage` |
| pilot-hub | Pilot Hub | Pilot | `Pages.PilotHubPage` |
| acars | ACARS | Pilot | `Pages.AcarsPage` |
| map | Map | Pilot | `Pages.MapPage` |
| messages | Messages | Pilot | `Pages.MessagesPage` |
| news | News | Community | `Pages.NewsPage` |
| events | Events | Community | `Pages.EventsPage` |
| support | Support | Community | `Pages.SupportPage` |
| settings | Settings | *(footer, no group)* | `Pages.SettingsPage` |

`LoginPage` is also native (navigated to directly by `ShellWindow` outside the rail, before the user is authenticated) but isn't a `NavDestination` entry since it isn't a rail item.

### MVVM + native threading

ViewModels use CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`) and take `Platform.IUiDispatcher` via constructor DI instead of touching WinUI's `DispatcherQueue` directly — this keeps the ViewModel layer free of any UI-framework type and testable (see `Platform/ImmediateUiDispatcher.cs`, used in unit tests). From a background thread (e.g. the SimConnect pump), marshal onto the UI thread with `_ui.Invoke(() => { Property = value; })` (synchronous, waits) or `_ui.BeginInvoke(...)` (fire-and-forget) — `DispatcherQueueUiDispatcher` is the production implementation, resolved once at startup from `DispatcherQueue.GetForCurrentThread()`. **Do not marshal on every 20Hz tick** — decimate first (e.g. `AcarsViewModel.UpdateTelemetry` only applies every 5th sample, 20Hz → 4Hz) and only then dispatch (see docs/superpowers/notes/20hz-batching-reference.md for the preserved original every-5-samples pattern from the Blazor era).

### Dependency Injection

All services are singletons registered in `App.xaml.cs`'s `ConfigureServices`:
- **Data:** `IFlightRepository`, `ILandingRepository`, `IFlightDeletionRepository`, `IMessageRepository`, `IFriendRepository` → JSON-persisted implementations
- **Infrastructure:** `IUiDispatcher` → `DispatcherQueueUiDispatcher`, `FilePickerService`, `SettingsService`, `AlertService`
- **SimConnect:** `SimConnectManager`, `FlightPhaseDetector`, `TelemetryProcessor`
- **Analytics:** `LandingAnalyzer`, `ApproachMonitor`, `FuelAnalyzer`, `RunwayDetector`
- **Business:** `AviatesBackendClient`, `AcarsPositionService`, `HoppiesAcarsClient`, `HazardZoneService`, `WeatherAdvisoryService`, `HoppiesAcarsOrchestrator`, `OtherPilotsService`, `PttInputService`, `DispatchRadioService`, `MessagingService`, `RouteTracker`, `FlightTrailService`, `FlightSessionManager`, `DiscordPresenceService`, `SimBriefService`, `TakeoffPerformanceService`, `ExportService`, `PilotStatsService`, `EventsService`, `RoutesService`, `BookingService`, `FleetService`, `UpdateService`, `FlightRecoveryService`
- **Native voice pipeline:** `VoiceAudioEngine`, `VoiceChannelClient`, `VoiceOrchestrator` (wires `DispatchRadioService`'s PTT/radio events into NAudio WASAPI capture/render + the Opus/WebM wire protocol)
- **EFB companion:** `EfbStateService`, `EfbChecklistService`, `EfbManualsService`, `EfbAtisService`, `EfbDatalinkService`, `EfbServer`
- **UI:** 13 ViewModels (`DashboardViewModel`, `MapViewModel`, `LandingAnalysisViewModel`, `PilotHubViewModel`, `SettingsViewModel`, `ReplayViewModel`, `MainViewModel`, `FleetViewModel`, `FlightsViewModel`, `RoutesViewModel`, `EventsViewModel`, `MessagesViewModel`, `AcarsViewModel`) + `ShellWindow` (not `MainWindow` — that name is gone)

### SimConnect SDK Loading

`SimConnectManager.cs` loads `Microsoft.FlightSimulator.SimConnect.dll` **at runtime via reflection** — not a build-time reference. Both DLLs (`Libs/Microsoft.FlightSimulator.SimConnect.dll` and `Libs/SimConnect.dll`) must be present at runtime. `SimConnectStub.cs` provides fallback mode when MSFS is not running. Auto-reconnect fires every 5 seconds.

### Threading Model

| Thread | Role |
|--------|------|
| UI Thread | Native WinUI 3 rendering (`DispatcherQueue`), ViewModel property updates |
| SimConnect Pump | Win32 WndProc message dispatch (HWND hook) |
| 20Hz DispatcherTimer | `RequestDataOnSimObjectType` calls |
| Reconnect Timer | 5-second retry when MSFS not detected |
| Replay Timer | `System.Timers.Timer` for flight playback |
| Messaging Poll | 30-second interval for new messages from backend |
| Discord Presence | 10-second update interval |

### Map (native, Mapsui)

`Services/NativeMapRenderer.cs` is a native Mapsui-backed rendering layer — the direct analogue of the old `window.aviatesMap` JS interop object, now owning one `Mapsui.Map` (tile layer via `Mapsui.Tiling`, vector/route layers via `Mapsui.Nts`) rendered by a `Mapsui.WinUI` `MapControl` on `MapPage`. It exposes the same conceptual API surface as the old interop object as plain C# methods (init/center/zoom, update-aircraft, draw route/waypoints, hit-testing) instead of JS calls — no `IJSRuntime`, no `wwwroot/js`, no JS at all. `MapViewModel` calls into it directly and marshals results back via `IUiDispatcher` like any other ViewModel.

### Backend Integration

`Core/Backend/AviatesBackendClient.cs` is wired into the DI container. `Services/IApiService.cs` + `NullApiService.cs` provide a stub. `MessagingService` actively polls the backend every 30 seconds using the ACARS key as a Bearer token (stored in `SettingsService`). Swap `NullApiService` for a real implementation when endpoints are ready — no page changes required.

### EFB Companion (virtual tablet)

A **new, self-contained module** that hosts a local web server on the LAN so a tablet/phone/second monitor can open the AviatesAir EFB. It only *consumes* existing services (telemetry pipeline, SimBrief, booking) — it does **not** modify them, and works with the live sim or the stub.

- `Services/Efb/EfbServer.cs` — embedded ASP.NET Core **Kestrel** host (via `<FrameworkReference Include="Microsoft.AspNetCore.App" />`), bound to `0.0.0.0` on a random free port. Token + 6-digit-code pairing. WebSocket at `/efb/ws`, REST under `/efb/api`, serves the frontend from **embedded resources** (`EfbWeb/**`, prefix `AviatesAirTracker.EfbWeb.`).
- `Services/Efb/EfbStateService.cs` — subscribes to `FlightSessionManager.TelemetryUpdated`/`SessionStateChanged`; folds telemetry + `SimBriefService.CurrentPlan` + `BookingService.ActiveBooking` into an `EfbSnapshot`; broadcasts throttled to ~4 Hz.
- `Core/Efb/` — DTOs + `GeoMath`, aircraft performance profiles + calculator (training-grade, **not** certified), per-aircraft normal checklist sets (`EfbChecklistLibrary` — one set per fleet family, resolved from the flight's aircraft type; simulation use only), the manuals library core (`EfbManuals.cs`) and company flap policy (`EfbCompanyPolicy.cs`).
- **Manuals** (`AviatesAirTracker/Manuals/*.md`, embedded): the AviatesAir document library — Company NOTAMs, OM-A, OM-B, QRH, Pilot Guides — as markdown with front matter; the single source for the EFB Manuals app and the PDF edition (`docs/manuals/`). Company restrictions published as NOTAMs (737 FLAPS 5 min takeoff, 737 FLAPS 25 / A320 CONF 3 min landing) are enforced in `EfbCompanyPolicy` — change both together.
- Frontend: `EfbWeb/` is a vanilla-JS **iOS-style springboard** (wallpaper + live flight widget + discrete full-screen apps). Flat files only (embedded-resource name → request-path is a plain prefix strip). Keep it offline-safe — the Map app is the only online one (Leaflet, Esri tiles and RainViewer radar load lazily when it opens; network traffic, aviation weather and the VATSpy/SimAware sector data come through the host).
- App interiors use the design system in `EfbWeb/ui.css` (airline crew-app look, light/dark, iPadOS parts, instrument screens kept dark). Destination photos come from Wikimedia Commons when online, with a credit line. See docs/EFB.md › App design system.
- Reuses existing systems rather than duplicating them: **Messages** = `MessagingService` (friend codes, DMs, broadcasts, shared history); **Map traffic** = `OtherPilotsService`; **OFP** = `SimBriefFlightPlan.PdfUrl`.
- Flight plan: an ACARS-imported plan flows into the EFB automatically. A plan imported *inside* the EFB is stored in `EfbStateService` only and must **never** call `SimBriefService.NotifyPlanLoaded` (that would leak into ACARS/Hoppies/takeoff-briefing).
- Custom wallpaper is uploaded to the host and stored at `%AppData%\AviatesAirTracker\efb_wallpaper.img` (shared across paired devices). Default wallpaper order: uploaded → bundled `EfbWeb/wallpaper.jpg` → gradient.
- **Landing reports are shared**: `Core/Analytics/LandingReport.cs` is the single projection rendered by BOTH the desktop `LandingResultModal.razor` and the EFB Landing app. Add pilot-facing landing data there, never to one UI only. Landings persist before the popup fires, so the EFB can always re-show them.
- ATIS (`Services/Efb/EfbAtisService.cs`) prefers the VATSIM datafeed (cached 90 s) and falls back to a METAR-derived ATIS.
- Settings: `AppSettings.Efb*` (`EfbEnabled`, `EfbAutoStart`, `EfbPort`, `EfbPairingSecret`, `EfbTheme`, `EfbWeightUnit`). The weight unit is the one KG/LB setting every EFB app reads (client helpers `WU()`/`fmtW()`/`bigFromKg()` in `app.js`); OFP documents and printed checklists keep their own units.
- ACARS datalink (`Services/Efb/EfbDatalinkService.cs`, wire formats in `Core/Efb/EfbAcars.cs`) runs under the **pilot's own** Hoppie logon and only **peeks** the pilot's inbox, never the shared company inbox (that stays server-side, see the AI dispatcher). Started in `App.xaml.cs` when enabled; disposed on exit.
- Full docs: [docs/EFB.md](docs/EFB.md).

## Key File Locations

| File | Purpose |
|------|---------|
| `AviatesAirTracker/App.xaml.cs` | DI container setup, crash handlers, Serilog config |
| `AviatesAirTracker/ShellWindow.xaml.cs` | Root native window — `NavigationView` rail + `Frame` navigation, unread message badge |
| `AviatesAirTracker/Navigation/NavDestinations.cs` | Single source of truth for the nav rail (tag/label/group/native page type) |
| `AviatesAirTracker/Theme/Tokens.xaml` | Shared nav-rail color resources (sidebar bg, accent brushes) ported from the old `app.css` `:root` block. The `#0A0D17`/`#4D9BEC` brand colors themselves are applied inline per-page in XAML/ViewModels — no single global brand resource dictionary yet |
| `AviatesAirTracker/Services/NativeMapRenderer.cs` | Native Mapsui map rendering layer (analogue of the old Leaflet interop) |
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
| `Services/Efb/EfbServer.cs` | Embedded Kestrel LAN server for the EFB companion (REST + WebSocket + pairing) |
| `Services/Efb/EfbStateService.cs` | Telemetry→`EfbSnapshot` bridge feeding the EFB |
| `Core/Efb/EfbPerformanceCalculator.cs` | EFB takeoff/landing performance (training-grade) |
| `EfbWeb/` | Embedded zero-dependency EFB tablet frontend (HTML/CSS/JS) |

## Technology Stack

- **.NET 8** (`net8.0-windows10.0.19041.0`), plain **`Microsoft.NET.Sdk`** with `UseWinUI=true` (x64 Windows only, self-contained, `win-x64`), C# 12, nullable reference types enabled — no WPF anywhere in this project
- **Windows App SDK / WinUI 3** — `Microsoft.WindowsAppSDK` (pinned; 1.6/1.7 fail to build without Visual Studio) + `Microsoft.Windows.SDK.BuildTools`
- **CommunityToolkit.Mvvm** — `[ObservableProperty]`, `[RelayCommand]`
- **Mapsui.WinUI** (+ `Mapsui.Tiling`, `Mapsui.Nts`) — native map rendering, no Leaflet/JS interop at all
- **RestSharp + Newtonsoft.Json** — SimBrief OFP import, backend HTTP
- **DiscordRichPresence** — Discord game presence
- **NAudio** (WASAPI capture/render) + **Concentus** (Opus) + **NEbml** (WebM mux/demux) — native voice pipeline
- **SharpDX.DirectInput** — HOTAS/joystick PTT binding
- **`FrameworkReference Microsoft.AspNetCore.App`** — embedded Kestrel host for the EFB companion server only (not a UI framework dependency)
- **Serilog** — rolling daily logs in `logs/` (7-day retention)

## Frontend Design Rules

**Always invoke the `frontend-design` skill before writing any XAML.** These rules apply to all UI work:

- **Brand colors:** `#0A0D17` background, `#4D9BEC` accent (the accent hex was updated app-wide during the migration — `#3D7EEE` is the retired previous value, don't reuse it). Never use default Tailwind palette.
- **Typography:** Pair a display/serif with a clean sans. Tight tracking (`-0.03em`) on large headings, generous line-height (`1.7`) on body.
- **Shadows:** Layered, color-tinted shadows with low opacity — never flat `shadow-md`.
- **Gradients:** Layer multiple radial gradients. Add grain/texture via SVG noise filter for depth.
- **Depth system:** Base → elevated → floating surface layers.
- **Animations:** Only animate `transform` and `opacity`. Never `transition-all`. Use spring-style easing.
- **Interactive states:** Every clickable element needs hover, `focus-visible`, and active states.
- **Reference images:** If `images/` contains assets (logos, color guides), use them — no placeholders where real assets exist.
