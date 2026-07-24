# AviatesAir EFB Companion

A professional airline-style **Electronic Flight Bag** that runs inside the
AviatesAir desktop client and serves a virtual tablet to any device on the same
network — iPad, phone, second monitor, or another PC.

The desktop app hosts a small web server on the LAN; the pilot opens the shown
address (or scans the QR/enters the pairing code) and the device becomes an
AviatesAir tablet with live flight data, performance tools, checklists, manuals,
ops messaging and a remote-monitor view.

---

## How it works (architecture)

```
MSFS → SimConnect → TelemetryProcessor → FlightSessionManager
                                              │  (TelemetryUpdated / SessionStateChanged)
                                              ▼
                                      EfbStateService  ──folds──►  EfbSnapshot
                                              │
        SimBriefService.CurrentPlan ──────────┤
        BookingService.ActiveBooking ─────────┘
                                              ▼
   ┌─────────────────────  EfbServer (embedded Kestrel, 0.0.0.0:random)  ─────────────────────┐
   │  /efb/ws        WebSocket — pushes EfbSnapshot / checklists / messages at ~4 Hz          │
   │  /efb/api/*     token-guarded REST (state, flightplan, perf, checklists, manuals, wx…)   │
   │  /  (static)    embedded tablet SPA (index.html + efb.css + app.js)                      │
   └──────────────────────────────────────────────────────────────────────────────────────────┘
                                              ▲
                              LAN device browser (tablet / phone / monitor)
```

The EFB is a **new, self-contained module**. It does not modify the telemetry
pipeline, ACARS, SimBrief, or the backend — it only *consumes* existing services,
so it works identically with the live sim or the `SimConnectStub` fallback.

### Key files

| File | Purpose |
|------|---------|
| `Services/Efb/EfbServer.cs` | Embedded Kestrel host: REST + WebSocket + pairing/auth + embedded static serving + LAN discovery |
| `Services/Efb/EfbStateService.cs` | Subscribes to the telemetry pipeline; folds telemetry + SimBrief + booking into `EfbSnapshot`; throttled broadcast |
| `Services/Efb/EfbChecklistService.cs` | Live checklist state (shared across devices) with per-item timestamps |
| `Services/Efb/EfbManualsService.cs` | Built-in SOP/manual library with categories, search, bookmarks, recents |
| `Core/Efb/EfbModels.cs` | Wire DTOs (`EfbSnapshot`, `EfbFlightPlan`, `EfbWaypoint`) + `GeoMath` great-circle helpers |
| `Core/Efb/AircraftPerformanceProfiles.cs` | Reference performance data for the fleet (B738, A20N, A21X, B77W, B789, A35F, AT76) |
| `Core/Efb/EfbPerformanceCalculator.cs` | Takeoff & landing performance model (V-speeds, field length, warnings) |
| `Core/Efb/EfbChecklistLibrary.cs` | Default airline checklists (one per phase) |
| `Core/Efb/QrCode.cs` | Dependency-free QR encoder for the pairing code (Settings panel) |
| `Pages/Settings.razor` | Desktop **EFB Companion** panel: status, QR, pairing code, start/stop |
| `EfbWeb/index.html`, `efb.css`, `app.js`, `favicon.svg` | The tablet frontend — **embedded resources** (ship inside the single-file exe) |

Registration lives in `App.xaml.cs` (`ConfigureServices` + startup/shutdown).

---

## Pairing & security

- On first run the server generates a random **pairing secret** (persisted in
  settings) from which it derives a stable **device token** and a **6-digit
  pairing code**.
- The server binds `0.0.0.0` on a **random free port** (or `AppSettings.EfbPort`
  if set) and advertises `http://<lan-ip>:<port>/#t=<token>` for each LAN
  interface.
- The desktop **Settings → EFB Companion** panel shows a live status, the
  address, the 6-digit code, a **scannable QR** of the pairing URL, and
  **Start/Stop** controls. The QR is produced by a self-contained C# encoder
  (`Core/Efb/QrCode.cs`) — no external service or library.
- Two ways to connect:
  1. Open the URL / scan the QR — the token rides in the URL hash and is stored
     in the device's `localStorage`.
  2. Open `http://<lan-ip>:<port>/` and type the 6-digit code — the device
     exchanges it for the token via `POST /efb/api/pair`.
- Every `/efb/api/*` call (except `hello`/`pair`) and the WebSocket require the
  token (`?t=`, `X-EFB-Token` header, or `Authorization: Bearer`). Static assets
  are open so the browser can load the shell.

> The connection is **local-network only** and token-guarded. Because it binds
> all interfaces, Windows Firewall may prompt on first start — allow it on
> **private** networks. To lock it down further, set a fixed `EfbPort` and add a
> firewall rule scoped to your LAN subnet.

### Settings (`AppSettings`)

| Setting | Default | Meaning |
|---------|---------|---------|
| `EfbEnabled` | `true` | Master switch for the companion |
| `EfbAutoStart` | `true` | Start the server on app launch |
| `EfbPort` | `0` | `0` = random free port each launch |
| `EfbPairingSecret` | *(generated)* | Seed for the device token + pairing code |
| `EfbTheme` | `aviates` | Last theme chosen on a paired device |

---

## REST + WebSocket API

Base: `http://<host>:<port>`  ·  Auth: token via `?t=`, `X-EFB-Token`, or Bearer.

| Method & path | Purpose |
|---------------|---------|
| `GET  /efb/api/hello` | Server info + `tokenValid` (unauthenticated) |
| `POST /efb/api/pair` | `{code}` → `{token}` |
| `GET  /efb/api/state` | Latest `EfbSnapshot` |
| `GET  /efb/api/flightplan` | `EfbFlightPlan` from the loaded SimBrief OFP |
| `GET  /efb/api/aircraft` | Fleet performance profiles (for dropdowns) |
| `POST /efb/api/perf/takeoff` | `TakeoffPerfRequest` → `TakeoffPerfResult` |
| `POST /efb/api/perf/landing` | `LandingPerfRequest` → `LandingPerfResult` |
| `GET  /efb/api/checklists` | All checklists with live state |
| `POST /efb/api/checklists/item` | `{checklistId,itemId,checked}` |
| `POST /efb/api/checklists/reset` | `{checklistId?}` (omit id = reset all) |
| `GET  /efb/api/manuals?category=&q=` | Categories + document summaries + recents |
| `GET  /efb/api/manuals/{id}` | Full document (markdown) |
| `POST /efb/api/manuals/{id}/bookmark` | Toggle bookmark |
| `GET  /efb/api/weather?icao=` | `{metar, taf}` (aviationweather.gov) |
| `GET  /efb/api/messages` | Full messaging state: my pilot id/name/friend code, unread count, friends, message history |
| `POST /efb/api/messages/send` | `{recipientId,content}` (DM) or `{broadcast:true,content}` |
| `POST /efb/api/messages/read` | `{id}` — marks a message read |
| `POST /efb/api/friends/add` | `{code}` — add a friend by Friend Code |
| `GET  /efb/api/traffic` | Live AviatesAir traffic for the map |
| `POST /efb/api/flightplan/import` | `{username}` — SimBrief import, **EFB-only** |
| `GET  /efb/api/ofp` | `{hasPdf, pdfUrl}` for the OFP app |
| `GET/POST/DELETE /efb/api/wallpaper` | Serve / upload / reset the custom wallpaper |
| `GET/POST /efb/api/theme` | Read / set the EFB theme |
| `POST /efb/api/flight/end` | End the active flight (mirrors the desktop button) |
| `WS   /efb/ws` | Push stream: `{type:"snapshot"\|"flightplan"\|"checklists"\|"messages", data}` |

The frontend prefers the WebSocket and automatically falls back to REST polling
(`/efb/api/state` every 1.5 s) if the socket drops, then reconnects.

---

## Frontend — the virtual tablet

`EfbWeb/` is a vanilla-JS app embedded in the assembly, modelled on an **iOS
springboard**: a wallpaper, a live flight **widget**, and discrete **apps** that
open full-screen. It uses the iPad's native SF Pro with monospaced numerals for an
authentic instrument feel, and ships four themes (**Aviates / Boeing / Airbus /
Minimal**) plus dark/light.

**Home screen**
- **Wallpaper** — the pilot can upload their own image (Settings → Wallpaper). It is
  stored on the desktop host (`%AppData%\AviatesAirTracker\efb_wallpaper.img`) and
  served to every paired device, so it persists and is shared. "Reset" restores the
  built-in gradient.
- **Flight widget** — the remote-monitoring view lives *on the wallpaper*, not in an
  app: callsign, phase, route, flight level and "Landing in 1h 12m", updated live.
  Tapping it opens the Flight app.
- **App icons** + a dock (Flight · Map · Messages · Settings).

**Apps**

| App | What it does |
|-----|--------------|
| Flight | Live instrument panel, fuel & configuration, **Descent & Fuel Advisor** (top-of-descent + predicted fuel on arrival) |
| Departure Perf | Takeoff performance (V1/VR/V2, TODR, warnings) — **autofilled from SimBrief** |
| Arrival Perf | Landing performance (Vref/Vapp, factored LDR) — **autofilled from SimBrief** |
| ATIS | Live VATSIM controller ATIS when online, otherwise an ATIS generated from the METAR, plus METAR/TAF |
| ACARS | The Hoppies message log — category, time, success, full text on tap |
| Landing | The touchdown report + butter meter, read from **persisted** history so nothing is ever lost |
| Scratchpad | Clearance notepad — freehand **draw** mode (like paper) or **type** mode |
| Cabin PA | Auto-written boarding/cruise/descent/landing PA scripts using live flight data |
| Approach! | A tiny built-in game. rAF runs only while the app is open |
| Map | Leaflet map — route, own aircraft, and **live AviatesAir traffic**, same data as the desktop live map (no floating detail panel) |
| Messages | The **same** messaging system as the desktop app — friend codes, friends list, DM threads, broadcasts, shared history and unread counts |
| OFP | The SimBrief OFP **PDF** briefing package |
| Checklist | Interactive checklists with timestamps |
| Manuals | SOP / manual library with search + bookmarks |
| Settings | Wallpaper, themes, EFB flight-plan import, end flight, connection |

> Leaflet + map tiles are the one online dependency (loaded lazily, only when the
> Map app is opened, exactly as the desktop app does). Every other app works fully
> offline on the LAN, and the Map degrades to a clear message with no internet.

### One landing report, two surfaces

The desktop touchdown modal (`Shared/LandingResultModal.razor`) and the EFB
**Landing** app both render from the same projection —
`Core/Analytics/LandingReport.cs` — so their figures, grading, butter rating and
wording can never drift apart. **Add anything pilot-facing about a landing to
`LandingReport`, not to either UI.**

Landings are persisted *before* the popup is raised, and the EFB reads from that
history, so a landing is still viewable even if the live modal was suppressed or
missed. `AppSettings.ShowLandingResult` is now honoured (it was previously
defined but never checked).

### Flight plan behaviour

- If a plan is imported in the **ACARS app**, the EFB picks it up automatically —
  dashboard, performance defaults, map route and OFP all fill in.
- A plan imported **inside the EFB** (Settings → Flight Plan) is EFB-only: it is
  stored in `EfbStateService` and deliberately never calls
  `SimBriefService.NotifyPlanLoaded`, so it cannot affect ACARS, the takeoff
  briefing, Hoppies or the desktop app.
- The ACARS plan always wins when both exist.

---

## Extending

### Add an aircraft performance profile
Append an `EfbAircraftProfile` to `AircraftPerformanceProfiles.All` in
`Core/Efb/AircraftPerformanceProfiles.cs`. The `/aircraft` endpoint, the perf
dropdowns and the calculator pick it up automatically. Provide reference weights,
V-speeds at a reference weight, reference field lengths, and flap tables.

### Add / customise a checklist
Append an `EfbChecklistDef` to `EfbChecklistLibrary.DefaultChecklists` in
`Core/Efb/EfbChecklistLibrary.cs`. (A future enhancement can load airline- or
type-specific packs from JSON.)

### Add a manual / SOP
Add an `EfbDocument` to `EfbManualsService.BuildLibrary()`. Documents are
markdown and render on the tablet with headings, tables, lists and code.

---

## ⚠ Performance data disclaimer

The performance module is a **training aid**. The V-speeds and field lengths use
simplified, physically-plausible reference models — **not certified AFM/QRH data**.
Use for simulator airmanship practice only, never real-world dispatch.

---

## Status / roadmap

**Shipped (this module):** embedded server, pairing/auth, live telemetry
WebSocket, flight dashboard, canvas route map, takeoff/landing performance,
interactive checklists with timestamps, manuals/SOP library, remote monitor,
ops messaging, weather (METAR/TAF), themes, end-flight control, and the desktop
**Settings → EFB Companion** panel with scannable QR + start/stop controls.

**Natural fast-follows:** deeper messaging integration with the Supabase backend
+ pilot accounts; per-type checklist packs; loading real company manuals from
disk; performance data refinement per airframe.
