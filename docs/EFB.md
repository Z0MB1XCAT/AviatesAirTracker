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
| `Services/Efb/EfbChecklistService.cs` | Live checklist state keyed set/checklist/item (shared across devices), per-item timestamps, the auto/manual set choice |
| `Services/Efb/EfbManualsService.cs` | Serves the document library (embedded `/Manuals/*.md` + generated QRH checklists): bookmarks, recents, **read & acknowledge** per revision — persisted in `%AppData%\AviatesAirTracker\efb_manuals.json` |
| `Core/Efb/EfbManuals.cs` | Library model: manuals (CN · OM-A · OM-B · QRH · GUIDE), front-matter parser, section anchors, full-text search, QRH normal checklists generated from `EfbChecklistLibrary` |
| `Core/Efb/EfbCompanyPolicy.cs` | Company flap restrictions (CN-002/CN-003) applied by the perf calculator, the `/efb/api/aircraft` flap lists and `LandingComplianceChecker` |
| `Manuals/*.md` | The manuals themselves — markdown with front matter, the single source for the EFB and the PDFs |
| `Core/Efb/EfbModels.cs` | Wire DTOs (`EfbSnapshot`, `EfbFlightPlan`, `EfbWaypoint`) + `GeoMath` great-circle helpers |
| `Core/Efb/AircraftPerformanceProfiles.cs` | Reference performance data for all 12 fleet types (A319, A20N, A359, A388, B736, B738, B739, B38M, B77F, B748, AT45, AT76) plus a few non-fleet types (A21X, B77W, B789, A35F). Each profile has aliases (SimBrief codes, add-on titles). `Find` returns null for an unknown type: nothing ever borrows another type's numbers |
| `Core/Efb/EfbPerformanceCalculator.cs` | Takeoff & landing performance model (V-speeds, field length, FLEX/assumed temp, OPT CONF, trim, warnings) |
| `Core/Efb/EfbLandingFailures.cs` | Training-grade in-flight failure catalogue for Arrival Perf (distance factor + ΔVREF per failure), per aircraft family in that family's QRH naming: Airbus G/Y/B (no blue on the A350/A380), 737 A/B/standby, 777/787 L/C/R, 747 1–4, ATR blue/green. Failures that remove the autobrake are flagged; the ATR always brakes manually |
| `Core/Efb/EfbAirportData.cs` | Parses aviationweather.gov airport data into per-end runway records for the Departure Perf picker |
| `Core/Efb/EfbAtcGeo.cs` | Resolves VATSIM/IVAO ATC callsigns to airports (DEL/GND/TWR/ATIS), TRACON polygons (APP) and FIR/UIR polygons (CTR/FSS) from the VATSpy + SimAware datasets |
| `Services/Efb/EfbNetworkTrafficService.cs` | VATSIM + IVAO pilots, ATC and ATIS (30 s cache), staffed sectors, sector geometry; downloads/caches the sector datasets weekly under `%AppData%\AviatesAirTracker\efb_cache` |
| `Core/Efb/EfbAcars.cs` | The Hoppie ACARS wire formats: response parsing (peek/poll/inforeq), CPDLC `/data2/` packets and their response sets, the PDC request text |
| `Services/Efb/EfbDatalinkService.cs` | The ACARS app's datalink terminal under the pilot's own Hoppie logon: inbox peek (45–75 s, only while a device is connected; peek leaves messages for an in-sim ACARS unit), telex, inforeq, PDC, CPDLC logon/requests/responses |
| `Services/Efb/EfbMapWeatherService.cs` | SIGMET/AIRMET polygons and METAR flight-category points for the map (aviationweather.gov, 5 min cache) |
| `Core/Efb/EfbChecklistLibrary.cs` | Per-aircraft normal checklist sets (A319/A320neo, A350, A380, 737NG, 737 MAX, 777F, 747-8, ATR 72/42-600 + a generic fallback) and the aircraft-type resolver |
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
| `EfbWeightUnit` | `KG` | The weight unit every EFB app shows (`KG` or `LB`); set in the tablet's Settings › Units and shared by all devices |

---

## REST + WebSocket API

Base: `http://<host>:<port>`  ·  Auth: token via `?t=`, `X-EFB-Token`, or Bearer.

| Method & path | Purpose |
|---------------|---------|
| `GET  /efb/api/hello` | Server info + `tokenValid` (unauthenticated) |
| `POST /efb/api/pair` | `{code}` → `{token}` |
| `GET  /efb/api/state` | Latest `EfbSnapshot` |
| `GET  /efb/api/flightplan` | `EfbFlightPlan` from the loaded SimBrief OFP |
| `GET  /efb/api/aircraft` | Performance profiles for the dropdowns: code, name, category, weights, `aliases`, `fleet`, company-permitted flaps |
| `POST /efb/api/perf/takeoff` | `TakeoffPerfRequest` → `TakeoffPerfResult` (incl. FLEX temp, OPT CONF, TOPL, THS/trim, F/S/O, THR RED/ACC) |
| `POST /efb/api/perf/landing` | `LandingPerfRequest` → `LandingPerfResult` (RWYCC, braking/reverser/autoland, failures, VLS/VAPP + APPR COR, LDR, 1.15 LDR, dispatch distance, per-autobrake results, F/S/O) |
| `GET  /efb/api/perf/landing/failures?type=` | The landing-distance-procedure catalogue for that aircraft's family (`family`, categories, failures with distance factor, ΔVREF and `noAutobrake`); without `type`, every failure |
| `GET  /efb/api/checklists?type=` | The active checklist set as a bundle: `{setId,setName,style,source,aircraftType,autoSetId,manual,sets[],checklists[]}` (type defaults to the flight's) |
| `POST /efb/api/checklists/select` | `{setId}` — pin a set; `"auto"`/null follows the aircraft type again |
| `POST /efb/api/checklists/item` | `{setId?,checklistId,itemId,checked}` (setId defaults to the active set) |
| `POST /efb/api/checklists/complete` | `{setId?,checklistId}` — check every remaining item (CHKL OVRD) |
| `POST /efb/api/checklists/reset` | `{setId?,checklistId?}` (omit checklistId = reset the whole set) |
| `GET  /efb/api/landings` | `{hasLanding, latest, history[]}` — `latest` is the full `LandingReport` incl. trace/gates/events; `history` (25) is the picker, built without traces |
| `GET  /efb/api/landings/{id}` | One full `LandingReport` (with its trace) for a landing picked from the history |
| `GET  /efb/api/manuals` | `{manuals[], documents[] (summary + ack/bookmark status), recent[], pending, aircraftType, aircraftSetId}` |
| `GET  /efb/api/manuals/search?q=` | Section-level hits `{docId,title,manual,chapter,section,anchor,snippet}` |
| `GET  /efb/api/manuals/{id}` | `{document, status}` — full markdown + sections |
| `POST /efb/api/manuals/{id}/bookmark` | Toggle bookmark → status |
| `POST /efb/api/manuals/{id}/ack` | Acknowledge a Company NOTAM at its current revision → `{status, pending}` |
| `GET  /efb/api/weather?icao=` | `{metar, taf}` (aviationweather.gov) |
| `GET  /efb/api/wx/station?icao=` | The Weather app's station bundle: `{icao, metar, taf, airport, atis[] (network, callsign, letter, frequency, text), generatedAtis, atc[] (network, callsign, facility, frequency, name), fetchedUtc}`; ATIS/ATC from VATSIM + IVAO, `generatedAtis` only when no station is online |
| `GET  /efb/api/airport?icao=` | `EfbAirportInfo` — field elevation, ARP, and every runway **end** (ident, length/width m, true heading, surface), from the aviationweather.gov airport API; cached per ICAO |
| `GET  /efb/api/messages` | Full messaging state: my pilot id/name/friend code, `hasKey` (ACARS key set), unread count, friends, message history (each with `synced`: confirmed by the backend, or still queued) |
| `POST /efb/api/messages/send` | `{recipientId,content}` (DM) or `{broadcast:true,content}` |
| `POST /efb/api/messages/read` | `{id}` or `{ids:[...]}` — marks one message, or a whole conversation, read |
| `POST /efb/api/friends/add` | `{code}` — add a friend by Friend Code |
| `POST /efb/api/friends/add-pilot` | `{pilotId,pilotName}` — add a pilot seen in live AviatesAir traffic (no code needed, like the desktop live map's Message action) |
| `POST /efb/api/friends/remove` | `{pilotId}` — remove from the friends list (history is kept) |
| `GET  /efb/api/traffic` | Live AviatesAir traffic for the map |
| `GET  /efb/api/network` | VATSIM + IVAO pilots, ATC/ATIS (facility, airport, sector keys, frequency, ATIS text) and staffed sectors |
| `GET  /efb/api/network/geometry?keys=` | Outlines for sector keys (`FIR:EGTT`, `TRACON:A80:ATL`), simplified; the tablet caches them |
| `GET  /efb/api/wx/sigmets` | International + US SIGMET/AIRMET polygons |
| `GET  /efb/api/wx/metars?minLat=&minLon=&maxLat=&maxLon=` | METAR stations with flight category (box capped at 30°×40°) |
| `POST /efb/api/flightplan/import` | `{username}` — SimBrief import, **EFB-only** |
| `GET  /efb/api/ofp` | `EfbBriefing` — the Pilot Brief package: identity, schedule, fuel breakdown and weights vs limits (in the OFP's own unit), SID/route/STAR, DEP/DEST/ALTN METAR+TAF, NOTAMs, OFP text, PDF link, %MAC. Fetched on demand, not pushed over the WS |
| `GET/POST/DELETE /efb/api/wallpaper` | Serve / upload / reset the custom wallpaper |
| `GET/POST /efb/api/theme` | Read / set the EFB theme |
| `GET/POST /efb/api/prefs` | `{weightUnit}` (`KG`/`LB`): the shared EFB units; a change is pushed to every device (`prefs` over the WS) |
| `GET  /efb/api/devices` | Devices connected now (`name` from the User-Agent, IP, since), plus the pairing code and URL for pairing another one |
| `POST /efb/api/pairing/reset` | New pairing secret: new token and 6-digit code, every device has to pair again |
| `GET  /efb/api/acars` | Datalink state: callsign, logon set, CPDLC station/state/next unit, last inbox check, unread, messages (`dir`, `station`, `type`, `kind`, `text`, CPDLC `min`/`mrn`/`ra`, `responses`, `open`, `response`), and the automatic company messages (`ops`) |
| `POST /efb/api/acars/poll` | Peek the inbox now |
| `POST /efb/api/acars/telex` | `{to,text}` (VAOPS = company dispatch) |
| `POST /efb/api/acars/info` | `{kind: METAR\|TAF\|ATIS, icao}` via Hoppie inforeq; the answer is stored as an inbound message |
| `POST /efb/api/acars/pdc` | `{station,aircraftType,destination,departure,stand,atis}`: `REQUEST PREDEP CLEARANCE …` telex |
| `POST /efb/api/acars/cpdlc/logon` · `/logoff` · `/request` · `/respond` | `{station}` · `{}` · `{text}` · `{id,response}` (WILCO/UNABLE/STANDBY/ROGER/AFFIRM/NEGATIVE as the uplink allows) |
| `POST /efb/api/acars/read` | `{ids:[...]}` marks inbound messages read |
| `POST /efb/api/flight/end` | End the active flight (mirrors the desktop button) |
| `WS   /efb/ws` | Push stream: `{type:"snapshot"\|"flightplan"\|"checklists"\|"messages", data}` |

The frontend prefers the WebSocket and automatically falls back to REST polling
(`/efb/api/state` every 1.5 s) if the socket drops, then reconnects.

---

## Frontend — the virtual tablet

`EfbWeb/` is a vanilla-JS app embedded in the assembly, modelled on an **iPad
airline EFB home screen** (the Fenix A320 EFB is the reference): a scenic
wallpaper, a centred grid of app icons, the airline wordmark, and discrete **apps**
that open full-screen. It uses the iPad's native SF Pro with monospaced numerals
for an authentic instrument feel, and ships four themes (**Aviates / Boeing /
Airbus / Minimal**) plus dark/light.

**Home screen**
- **Status bar** — transparent, drawn over the wallpaper: `DD Mon YYYY  HH:MML / HH:MMZ`
  on the left; on the right, signal bars for the desktop link (4 = live WebSocket,
  1 = REST polling fallback), a link glyph for the simulator, a location arrow for a
  valid aircraft position, and the device battery **only** where the browser exposes
  it (Chromium/Android — hidden on iPad Safari rather than faked). While a flight is
  active a compact callsign/phase/route readout sits centred (hidden on phones).
- **Wallpaper** — the bundled default is `EfbWeb/wallpaper.jpg`: an airliner winglet over a
  sunset cloud deck by Maria Sleptsova on Unsplash (Unsplash License: free to use, no credit
  required; cropped to 2400×1800 for 4:3 tablets). The pilot can upload their own image (Settings → Wallpaper, or the
  home screen's **Change wallpaper** button). It is stored on the desktop host
  (`%AppData%\AviatesAirTracker\efb_wallpaper.img`) and served to every paired device,
  so it persists and is shared. "Reset" returns to the bundled wallpaper.
- **App icons** — 15 full-tile SVG illustrations (`TILE_ART` in `app.js`), 8 per row on
  landscape tablets; the AviatesAir icon uses the real wordmark's "A" (`mark-a.png`).
  The small line-art `ICONS` set is kept for in-app controls.
- **No sidebar** — an open app owns the whole screen under the status bar, like a real iPad
  EFB. The **home indicator** at the bottom (tap, swipe up, or `Esc`) returns to the
  springboard. Apps listed in `FULL_APPS` (currently AviatesAir, Departure Perf, Arrival Perf, Pilot Brief and Map) draw their own chrome;
  the rest keep the slim name/route/actions header.
- **Wordmark** — `logo-wordmark.webp` (the cropped logo) over "Electronic Flight Bag";
  tapping it opens the Flight app.
- **Utility buttons** (bottom-right) — Enter/Exit full screen (hidden where the
  Fullscreen API isn't available) and Change wallpaper.

**Apps**

| App | What it does |
|-----|--------------|
| AviatesAir | The airline's flight dashboard, laid out like the flyPad Dashboard. **Your Flight**: identity bar + phase, DEP→ARR with a progress track (aircraft, T/D marker, NM flown/to go), next waypoint · ETE · ETA · ALTN · CRZ, **OOOI** (Out/Off/On/In) against STD/STA with delay chips + block/air time, a **vertical profile** (T/C, T/D, aircraft at distance/altitude), live flight-data tiles and aircraft configuration (blanked when the sim is disconnected). **Important Information**: DEP/ARR METAR (summary tiles or raw with flyPad's airline colour scheme — cyan ICAO, amber caution, red warning, grey trend/remarks), fuel & descent advisor, the checklists for the current phase (tap to open), last landing, shortcuts. No plan → one-tap EFB-only SimBrief import |
| Departure Perf | Standalone takeoff tool modelled on the Fenix EFB / Airbus Flysmart: Airport Info (ICAO → real runway list, TO shift, dry/wet), Aircraft Configuration (OPT/manual flaps, force TOGA, anti-ice, packs, TOW, MACTOW, **Sync Loadsheet** from the OFP — MACTOW comes from the same W&B model as the Hoppies loadsheet for seeded types), TRANS ALT (from the OFP's `origin/trans_alt` when present, else typed), Weather (**Sync Live WX** from the live METAR, VRB winds taken conservatively). Output is the aircraft's own FMS page — Airbus **MCDU PERF TAKE OFF** or Boeing **CDU TAKEOFF REF** — plus TOPL / wind / margin / limitation tiles, a runway diagram and the METAR + runway-info strip |
| Arrival Perf | In-flight landing tool modelled on the Fenix EFB Arrival Perf / Flysmart landing module. **Runway** (ICAO → real runway list, minimums BARO/RADIO, GRF runway condition code **6 DRY … 1 POOR**, suggested from the METAR), **Weather** (Refresh METAR / Apply METAR), **Aircraft Config** (landing weight from ZFW + live predicted arrival fuel or the OFP, flaps, autobrake LO/MED/HI/MAN or 2/3/MAX AUTO, reverser MAX/IDLE/NO REV, manual/autoland, A/THR), **Ldg Distance Procedure** (in-flight failures by category — factors multiply, the largest ΔVREF applies; training values, not QRH). Output: Airbus **MCDU PERF APPR** or Boeing **CDU APPROACH REF**, VAPP / LDR / 1.15 LDR / margin tiles, 1.15 LDR for every braking mode, and Fenix's vertical runway with LDA · LDR · 1.15 LDR |
| Weather | Airport weather in the style of ForeFlight / Navigraph. Station tabs for **DEP / ARR / ALTN** (from the plan) plus recent searches, each with its flight-category dot, and an ICAO search. **ATIS**: live VATSIM and IVAO ATIS with the information letter, frequency and network, arrival/departure tabs where both exist, a **NEW ATIS** banner when the letter changes, and chips for the online ATC (DEL/GND/TWR/DEP/APP/CTR); when nothing is online, an ATIS generated from the METAR. **Decoded METAR**: flight category (VFR/MVFR/IFR/LIFR), observation age, a wind dial drawn over the real runways (gusts, variable arc), visibility and RVR, temperature/dew-point spread with fog and icing hints, QNH in hPa and inHg, a cloud-layer chart with the ceiling marked, and weather/trend chips. **Runway winds**: every runway end with head/tail and crosswind components (incl. gusts), sorted with the into-wind runway tagged. **TAF**: an hourly category timeline with TEMPO/PROB rows and the ETD/ETA marked, a forecast summary at ETA, and the decoded change groups. **Hazards**: SIGMETs covering the field. Raw METAR/TAF are colourised and copyable. Refreshes every 2 minutes |
| ACARS | A datalink terminal on the real Hoppie network, laid out like an airline ACARS page with an A320-style **DCDU**. Folders (All · Inbox · Sent · CPDLC · Company · Weather · Ops log) with unread and **OPEN** counts; the selected message on a black DCDU screen with its **WILCO / UNABLE / STANDBY** (or ROGER, AFFIRM/NEGATIVE) keys, MIN/MRN, reply and copy. Composer: **Telex** (VAOPS company dispatch, where the AI dispatcher answers, or any station), **Clearance** (the `REQUEST PREDEP CLEARANCE` PDC request with stand and ATIS, previewed), **Info** (METAR / TAF / VATSIM ATIS), **CPDLC** (logon with the next unit from a HANDOVER, a request builder for level, direct, speed, heading or free text with a DUE TO reason, logoff). Runs under the pilot's own logon code; the inbox is peeked so an in-sim ACARS unit on the same callsign still gets everything. The **Ops log** is the automatic company messages the tracker sent |
| Landing | A flight-data-monitoring touchdown report: score ring + grade + butter meter; touchdown V/S, IAS, GS, pitch, bank, **crab**, head/tailwind, crosswind coloured against the FDM limits; **FDM events** (hard landing, high/low pitch, bank, bounce, long/short landing, centreline, sink rate below 1000 ft, bank below 500 ft, unstabilised approach, tailwind, crosswind, crab, no flare) as WARNING/CAUTION/NOTE; the **approach profile** (altitude AGL with the magenta 3° reference and the 1000/500 ft gates, IAS/GS, V/S with the −1000 fpm line, pitch/bank — scrub for a readout, distance or time axis); a **touchdown-zone** runway diagram (TDZ, aiming point, touchdown point with centreline offset, rollout to 30 kt, runway remaining — runway size from the airport data); the **stabilised-approach gates** table and the score breakdown. Any of the last 25 landings can be re-opened; landings without a trace show the parts that don't need one |
| Scratchpad | ForeFlight-style scratchpads: a stack of pages (a new flight starts with CRAFT, ATIS, Takeoff Brief, Approach Brief and Blank; add Holding, Frequencies, Taxi Route, lined, grid or a typed note). Pen (pressure-sensitive with a stylus, speed-sensitive by finger), highlighter, stroke eraser and text stamps (UTC, squawk, QNH, runway, SID…), 4 colours × 3 sizes, per-page undo/redo (Ctrl+Z / Ctrl+Shift+Z; P/H/E/T tool keys), palm rejection once a stylus is seen, Night/Day paper with adaptive ink, and faint **hints** from the OFP / perf apps printed in the template boxes. Ink is stored in page units (1000-wide) so it survives rotation. Pages are per device (`localStorage` `efbScratchPages`) |
| Approach! | A calm one-thumb mini game for an EFB held at an angle: **hold anywhere** (or Space / ↑) to climb, release to descend, with vertical speed eased so nothing is twitchy; optional **DRAG** control. Fly wide magenta "highway in the sky" gates (centre band = PERFECT, a small CLOSE margin still counts), then every 8 gates land on a runway following the **PAPI**; a ground-effect flare assist settles a hands-off touchdown at ~250 fpm (SMOOTH) and a held flare earns BUTTER. Missing a gate or touching terrain costs a go-around (life), never instant death. RELAXED (5 lives, wide gates, slower) / CLASSIC (3 lives). Legs between AviatesAir airports get slightly faster; bests per mode + best touchdown fpm in `localStorage`. dt-based; the loop runs only while the app is open and pauses when the tablet is hidden |
| Map | Full-screen moving map (Navigraph / flyPad / VATSIM Radar style): Esri dark/light/satellite base; a tool stack (layers, follow aircraft, fit route, zoom); a layers sheet with live counts. **Weather**: RainViewer radar with a 10-min loop player, IR satellite, SIGMET/AIRMET polygons, METAR flight-category dots (zoom ≥ 6). **Traffic**: AviatesAir pilots, and separate toggles for **VATSIM traffic, VATSIM ATC, IVAO traffic, IVAO ATC** — aircraft on one canvas layer, ATC as staffed FIR/TRACON sectors plus airport D/G/T/A/ATIS badges. **Overlays**: flight-plan route with waypoint labels, ownship + 5-min track vector, range rings, callsign labels. Tap anything for a details card (flight, ATC with frequencies + ATIS text, sector, SIGMET, METAR); ownship strip shows GS/ALT/TRK/distance/ETA. Layer choices persist per device |
| Messages | The **same** messaging system as the desktop app, laid out as an iPad-style split view (one pane at a time on a phone). **Conversations**: the **Crew Room** (the airline-wide broadcast channel) pinned on top, then direct threads newest first, with avatars, online/flying dots, last-message previews, drafts and unread counts; threads from pilots who aren't in your list still show up. Search covers names, friend codes and message text. **Thread**: day separators, grouped bubbles, a **New messages** line, Zulu + local times, a sent tick or a queued clock per message, big emoji, and a jump-to-latest button. **Composer**: auto-growing, Enter to send / Shift+Enter for a new line, a 2,000-character counter, quick replies, and **Share my flight** (callsign, route, type, phase, level, GS, ETA from the live snapshot). Language-filter and missing-key errors show inline and keep the text. **Details**: friend code (copy), rank, a pilot's live flight (from AviatesAir traffic), conversation stats, add/remove. **New message**: My pilots · **Flying now** (anyone in live traffic) · **Friend code** (auto-formatted XXXX-XXXX). Live over the WebSocket without losing the draft or scroll. Crew Room read state is kept per device (the host can't mark broadcasts read) |
| Pilot Brief | The briefing package, laid out like the Fenix EFB's Pilot Brief (big document tabs + one viewer): **Flight Plan** (the SimBrief OFP text on paper, split at its page breaks, zoomable; rebuilt from text nodes so the OFP HTML can't script the EFB), **Summary** (dispatch release: route/times, fuel breakdown, ZFW/TOW/LDW vs limits, PIC acceptance), **Weather** (DEP/DEST/ALTN with VFR/MVFR/IFR/LIFR category, decoded tiles, formatted TAF, live refresh), **NOTAMs** (A/B/C/E parsed, airport filters, read tracking + unread badge), **Documents** (OFP PDF in-viewer, computed preliminary loadsheet). With no plan it offers a one-tap EFB-only SimBrief import |
| Checklist | Electronic normal checklist for the aircraft being flown, in its manufacturer's style: Airbus ECL (ALL CAPS, `(BOTH)`/`PF/PM` call-outs, the black line before post-clearance items), Boeing ECL (QRH sentence case, CHKL OVRD/RESET soft keys) and the ATR C/L. Pending lines white, done lines green, the current line boxed; tap a line, CHECK or Space. `___` values the EFB knows (V-speeds, flaps, stab trim, VREF, autobrake, fuel, squawk) are filled in cyan. Set follows the booking/OFP/sim type (AUTO) or a manual pick, shared across devices; the phase-relevant list is flagged NOW and shown on the dashboard. **For simulation use only** |
| Manuals | The AviatesAir document library, laid out like an airline document reader: **Company NOTAMs** (read & acknowledge, home-screen badge until done), **OM-A** (policy, flight planning, fuel, weather & minima, SOPs, approach & landing, FDM & review rules, online ops, abnormal policy), **OM-B** (fleet overview + one chapter per type), **QRH** (immediate actions + normal checklists generated from the Checklists library) and **Pilot Guides**. Library tree with MUST READ / YOUR AIRCRAFT / bookmarks, reader with revision & effective block, on-this-page TOC with scroll-spy, text size, cross-document links, full-text search to the section. **Export PDF** opens the print book (`?print=<manual|doc id|ALL>`: cover, list of effective documents, contents, page numbers) — use the print dialog's Save as PDF |
| Settings | iPadOS Settings layout (one page at a time on a phone). **Display** (theme, shared; dark/light, per device), **Wallpaper** (live preview, upload, default), **Units** (one weight unit, KG or LB, for every app and device), **Flight plan** (EFB-only SimBrief import), **Active flight** (end flight, two taps), **Datalink** (Hoppie status), **Devices** (who is connected, the pairing code to add another, forget this device, unpair all), **About** (version, connection, data sources) |

> The Map is the one online app: Leaflet, Esri tiles and RainViewer radar load on the
> device only when it opens; VATSIM/IVAO, SIGMET/METAR and the VATSpy/SimAware sector
> data are fetched by the host (the sector data is cached on disk, CC BY-SA 4.0, credited
> in the map attribution). Every other app works fully
> offline on the LAN, and the Map degrades to a clear message with no internet.

### App design system (`ui.css`)

The inside of every app follows one family, picked with the user in November 2026: **an airline's
own iPad crew app** (BA / Emirates trip screens, Flighty), light or dark from Settings › Display,
built from iPadOS parts. `ui.css` loads after `efb.css` and holds:

- **Tokens** (`--s-*`, light and dark sets on `.app-host`). The older per-app styles read the
  `--c-*` cockpit tokens, which are mapped onto `--s-*`, so nothing is left hard-wired dark.
- **Parts**: `.fx` frame (scroll area + tab bar), `.hero` photo header with one plain sentence,
  `.lt` large title, `.w` widgets in a `.wg` grid of mixed sizes, `.gl`/`.row` inset grouped lists,
  `.sg` segmented control, `.bt` buttons, `.tabs` tab bar, `.sv` split view (sidebar + detail).
- **Brand face**: the flight number only, in Saira ExtraBold Italic, bundled as
  `saira-italic-800.woff2` (SIL OFL, `Saira-OFL.txt`) so the EFB stays offline-safe.
- **Instrument screens stay instruments**: the perf MCDU, the checklist ECL page, the ACARS DCDU,
  the landing approach trace and the runway drawings get a scoped dark palette in either mode.

App shapes: AviatesAir (photo hero + boarding pass + widgets; tabs for Times, Fuel, Weather, Live
data), Weather (iPad Weather-style hero, widgets, runway list, TAF timeline), Landing (photo of the
landing airport, score large, widgets and lists), Pilot Brief and Settings (split view; OFP and
loadsheet stay paper), ACARS (Mail-style list, DCDU, native composer).

**Photography** (`photoFor()` / `heroPhoto()` in `app.js`): the lead image of the place's
Wikipedia article, fetched from the tablet when online (`origin=*` CORS, free licences, JPEG only),
with the author and licence from Commons shown as the credit line and linked to the file page.
Cached per device for 30 days. Offline, or with no usable image, the header shows the wallpaper.
The map widget uses Esri Light/Dark Gray tiles framed on the route, with the route drawn over them.

### Flight mode, alerts, keep-awake and interface size

All client-side, in `app.js` (the host is untouched):

- **Flight mode** — `fmDerive()` turns each snapshot into `ground → departure → flight →
  approach → landed`. 1,000 ft AGL is the line both ways, with a 300 ft band (approach → flight
  only above 1,300 ft) so a level-off near it doesn't flap; climbing back above 1,300 ft from
  approach is a go-around. Each change re-labels the **home dock** (five apps for the phase,
  `FM_DOCK`) and shows a banner; touchdown shows "Landed … your landing report is ready".
- **Sterile cockpit** — chat (DMs, Crew Room) is held during taxi, below 10,000 ft MSL and on the
  approach, shown as a STERILE chip with the count in the status bar, then released as one summary
  banner. ACARS and CPDLC are never held.
- **Alerts** — `notify()` banners for new messages and ACARS uplinks when you're not in that app
  (an open CPDLC instruction stays 20 s). Tap opens the app on that conversation/message.
- **Keep screen on** — during a flight until on blocks. Wake Lock where the page is a secure
  context, otherwise (plain-HTTP LAN, so iPad/Android in practice) a muted 16×16 looping inline
  video embedded as a data URI. Needs one tap after it is wanted.
- **Interface size** — CSS `zoom` on `#app`. Auto scales up on screens ≥1,400 px wide (up to
  1.6×); fixed sizes 90–150 %. Breakpoints follow the real viewport, so zoom is capped at
  `innerWidth/1100`. Code that maps pointer or rect coordinates uses `uiScaleOf(el)` / `uiZ()`.

Alert, sterile-cockpit and keep-awake switches are per device (Settings › Flight mode,
`localStorage efbFlightMode`); interface size is `efbUiSize`.

### One landing report, two surfaces

The desktop touchdown modal (`Shared/LandingResultModal.razor`) and the EFB
**Landing** app both render from the same projection —
`Core/Analytics/LandingReport.cs` — so their figures, grading, butter rating and
wording can never drift apart. **Add anything pilot-facing about a landing to
`LandingReport`, not to either UI.**

The flight-data view (`Trace`, `Gates`, `Events`, `TouchdownDistanceFt`, `CrabAngleDeg`) is
built in `LandingReportBuilder.ApplyFlightData`. `LandingAnalyzer` records the trace
phase-independently (1 Hz for the last 150 s, 4 Hz for the last 10 s, frozen at the primary
touchdown, then 4 Hz → 1 Hz through the rollout, bounces included). `LandingResult.Trace` is
`[JsonIgnore]`: `JsonLandingRepository` stores each trace separately in
`%AppData%\AviatesAirTracker\landing_traces\{id}.json` (read back via
`ILandingRepository.GetTraceAsync`), so `landings.json` and `flights.json` stay small and the
backend upload is unchanged. When `RunwayDetector` doesn't know the runway, the touchdown
distance is estimated from the 50 ft crossing (threshold crossing height) and flagged as such.
Event limits live in `LandingReportBuilder.Limits` (AviatesAir's own FDM limits, aligned with
`LandingComplianceChecker` where they overlap — not aircraft limitations).

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

## Dev server

`tools/EfbDevServer` runs the tablet without the desktop app: it serves `AviatesAirTracker/EfbWeb`
from disk (edit, refresh) on the same `/efb/api` + `/efb/ws` surface, with the app's real code linked
in (performance calculator, aircraft and failure data, checklist service, manuals library, landing
report, ATIS/METAR/TAF, airport, SIGMET and VATSIM/IVAO services). Only the live inputs are simulated:
a flight (VAV123 EGLL→LSZH, A320neo, cruising and looping every 40 min), messages, traffic and ACARS
replies. Any token is accepted (no pairing); state is in memory. Not part of the solution and never shipped.

```bash
dotnet run --project tools/EfbDevServer -- --port 8765
```

Add `--lan` to open it on a real tablet on the same network.

To test flight mode, alerts and sterile cockpit, run a whole flight gate to gate (15 minutes,
or faster) and inject traffic:

```bash
curl -X POST localhost:8765/efb/dev/scenario -H "Content-Type: application/json" -d '{"name":"full","speed":10}'
curl -X POST localhost:8765/efb/dev/incoming -H "Content-Type: application/json" -d '{"kind":"dm"}'
```

Scenarios: `cruise` (default), `full`, `none` (no flight); also `--scenario full` on start.
Incoming kinds: `dm`, `crew`, `company`, `cpdlc`. `window.efbDebug()` in the browser console shows
the current mode, sterile state, held count, keep-awake and zoom.

## Extending

### Add an aircraft performance profile
Append an `EfbAircraftProfile` to `AircraftPerformanceProfiles.All` in
`Core/Efb/AircraftPerformanceProfiles.cs`. The `/aircraft` endpoint, the perf
dropdowns and the calculator pick it up automatically. Provide reference weights,
V-speeds at a reference weight, reference field lengths, flap tables and `Aliases`
(the SimBrief code and add-on titles that mean this type). Then map its code to a failure
family in `EfbLandingFailures.FamilyOf`, and add it to `EfbFleetPerformanceTests.Fleet` if
AviatesAir operates it. Weight and balance (`Core/Analytics/WeightBalanceDatabase.cs`) is
separate and deliberately only holds verified types; without it the EFB shows MACTOW as an
estimate and asks the pilot to enter it from the loadsheet.

### Add / customise a checklist
Checklists live in `Core/Efb/EfbChecklistLibrary.cs` as one `EfbChecklistSet` per fleet
family. Build items with `I(challenge, response, who?, note?)` (or `L(...)` for an item
below the Airbus line) and give every checklist a **phase key** (`preflight`, `before-start`,
`after-start`, `before-taxi`, `taxi`, `before-takeoff`, `after-takeoff`, `cruise`, `descent`,
`approach`, `landing`, `after-landing`, `shutdown`, `secure`) — the dashboard and the NOW
flag map flight phases onto those keys. Item ids are slugs of the challenge text, so
renaming a challenge resets its check state. A new type: add its ICAO code to a set's
`Types` (or a new set to `Sets`) and a fallback in `Resolve` if free-text names need it.
Sources for each set are recorded in its `Source` string and shown on the tablet.

### Add or revise a manual
Manuals are markdown files in `AviatesAirTracker/Manuals/` (embedded via
`<EmbeddedResource Include="Manuals\*.md" />`). Each starts with front matter —
`id`, `manual` (CN · OM-A · OM-B · QRH · GUIDE), `chapter`, `title`, `summary`, `tags`,
`revision`, `effective` (yyyy-mm-dd), `order`, optional `ack: true` (Company NOTAMs) and
`aircraft:` (checklist set ids, pins the document under YOUR AIRCRAFT). The dialect:
`##`/`###` headings (anchored), paragraphs, `-`/`1.` lists, tables, `> **NOTE|CAUTION|WARNING**`
callouts, `**bold**`, `*italic*`, `` `code` ``, `---`, and links `[text](doc:om-a-05#anchor)`.
**Bump `revision`** when a document changes — acknowledged notices then ask to be acknowledged again.
`EfbManualsTests` checks every document parses and every `doc:` link resolves.

A company restriction that the tools must enforce (like CN-002/CN-003) also goes in
`Core/Efb/EfbCompanyPolicy.cs`.

**PDF edition:** EFB → Manuals → *Export PDF* (any device), or headless:
`chrome --headless=new --no-pdf-header-footer --virtual-time-budget=30000 --print-to-pdf=OM-A.pdf "http://<efb-host>/?print=OM-A&auto=0"`
from a browser profile that is already paired (the print book calls the token-guarded API).
The PDFs generated for Discord are in `docs/manuals/`.

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
+ pilot accounts; performance data refinement per airframe.
