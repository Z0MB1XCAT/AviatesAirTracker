# Dispatch Radio System — Phase 1 (Foundation) — Design Spec
**Date:** 2026-07-12
**Scope:** Foundation only — Oracle VM, backend skeleton, distance/routing logic, pilot-client wiring, and a read-only admin view. No live voice audio (Phase 2) and no Hoppies ACARS fallback (separate follow-on spec) are part of this round.

---

## Background

The goal is a realistic dispatcher radio system for the virtual airline: pilots tune a VHF/HF frequency in MSFS, and depending on distance from the dispatch station, comms should be clear, degraded, or unavailable (falling back to ACARS text). The full system spans voice relay, audio effects, and a human dispatcher console — but those depend on foundational pieces (session tracking, distance calculation, and the tune-detection client) that don't yet exist anywhere in the codebase.

This spec covers only that foundation. Two follow-on rounds are already anticipated and explicitly out of scope here:
- **Phase 2:** live voice relay (Mumble) and distance-based audio degradation.
- **Hoppies integration:** ACARS text fallback via the real-world Hoppies network, using a provided logon code, plus a per-pilot `hoppies_acars_key` column in Supabase.

## Decisions Made

| Question | Decision |
|---|---|
| Pilot client host | Inside this app (`AviatesAirTracker`), not a separate plugin — COM1 frequency is already polled at 20Hz via SimConnect. |
| Backend host | New standalone Node.js service on an Oracle Cloud free-tier VM. The existing Cloudflare Worker (`BACKEND-API/worker.js`) is not touched — it keeps handling bookings, PIREPs, and messaging. |
| Voice server (Phase 2, not built yet) | Mumble — mature, low-latency, minimal custom code vs. a hand-rolled WebRTC SFU. |
| Dispatcher console | Web-based, added to the existing Next.js site at `AviatesAirWebsite`, under its admin section. |
| Oracle account | Not yet provisioned — this round includes setup steps. |
| Dispatch station location | EGNX (East Midlands Airport), 52.8311° N, 1.3281° W. |
| Frequencies | VHF **123.575 MHz** (chosen because it falls in the civil airband but is not allocated to any real-world station or airport) and HF backup **6750 kHz** (arbitrary, not a real-world ARINC/Volmet channel). |

## Components

### 1. Pilot client — `AviatesAirTracker/Services/DispatchRadioService.cs` (new)
- Registered as a singleton in `App.xaml.cs` alongside the other services, following the existing DI pattern.
- Subscribes to the same 20Hz telemetry tick already flowing through `FlightSessionManager`. Reads `AircraftState.COM1Frequency` (see `Core/SimConnect/SimConnectDefinitions.cs:99`) each tick.
- State machine: `Untuned → Tuned` transition when COM1 frequency matches 123.575 (VHF) or the HF backup within a small epsilon (frequency comparisons are floating point). On transition to `Tuned`, opens a WebSocket to the backend. On transition back to `Untuned`, closes it cleanly.
- While `Tuned`, sends a position update once per second (not 20Hz — this is coarse distance tracking, not flight telemetry) with pilot ID, callsign, lat/lon, and active frequency.
- Receives `{ state, distanceNm }` pushes from the backend and exposes them as observable properties for later UI use (Phase 2 will surface this on-screen; this round just needs the plumbing).
- Reconnect behavior mirrors the existing SimConnect auto-reconnect: retry every 5 seconds if the socket drops while still tuned, matching the pattern documented in `CLAUDE.md`'s threading model table.

### 2. Backend — `VOICE-BACKEND/` (new top-level folder, sibling to `BACKEND-API/`)
- Small Node.js service (Express + `ws`), deployed to the Oracle VM via systemd, not Cloudflare Workers — Workers can't hold the kind of persistent, stateful socket registry this needs.
- Session registry: in-memory map of `pilotId → { callsign, lat, lon, freq, lastSeen }`, keyed by WebSocket connection.
- Distance calculation: haversine great-circle distance from each pilot's position to the fixed EGNX coordinate. Pure function, unit-testable without any I/O.
- Routing/decision logic: buckets distance into
  - `< 200nm` → `clear`
  - `200–400nm` → `degraded`
  - `> 400nm` → `acars_only`
- Pushes the bucket + distance back to the originating pilot socket on every update, and broadcasts the full registry to any connected admin sockets (for the dispatcher console).
- Sweeps the registry every few seconds; any session with no update in 15s is dropped and reported to admin sockets as disconnected.

### 3. Admin view — `AviatesAirWebsite/src/app/admin/dispatch/` (new page)
- Read-only in this round: a live table of tuned pilots (callsign, distance, comms state), fed by a WebSocket subscription to the Oracle backend's admin channel.
- No voice controls, no message-sending UI yet — those arrive with Phase 2 and the Hoppies round respectively.

## Protocol

WebSocket, JSON frames, one connection per pilot session.

Client → server, ~1/sec while tuned:
```json
{ "pilotId": "string", "callsign": "string", "lat": 0.0, "lon": 0.0, "freq": "123.575" }
```

Server → client, on each processed update:
```json
{ "state": "clear" | "degraded" | "acars_only", "distanceNm": 0.0 }
```

Server → admin sockets, on any registry change:
```json
{ "pilots": [ { "pilotId": "...", "callsign": "...", "distanceNm": 0.0, "state": "clear" } ] }
```

## Error Handling

- **Client socket drop while tuned:** retry every 5s; no interruption to flight recording or any other subsystem — this is an isolated add-on service.
- **Backend stale session:** dropped after 15s of silence, reported to admin view as disconnected rather than left as a ghost entry.
- **Tuning away:** clean socket close, no error path — this is the expected common case.
- **Frequency epsilon matching:** COM frequencies from SimConnect are floating point; comparisons use a small tolerance (e.g. ±0.001 MHz) rather than exact equality.

## Testing Plan

- Unit tests for the haversine + bucketing function in the Node backend, using known coordinate pairs with pre-computed expected distances.
- Manual end-to-end test: tune COM1 to 123.575 while parked at EGNX in MSFS → confirm `clear` state appears on the admin page. Use MSFS's slew/teleport to reposition >400nm away → confirm the state transitions to `acars_only` and the admin page updates live within a few seconds.
- Manual reconnect test: kill the Oracle backend process while a client is tuned and connected → confirm the client retries every 5s and recovers automatically once the service restarts.

## Explicitly Out of Scope for This Round

- Actual voice audio, Mumble server setup, and distance-based static/degradation effects.
- Hoppies ACARS text fallback and the `hoppies_acars_key` Supabase column.
- Any dispatcher-side voice controls or message-sending UI.
- Multiple dispatch stations or per-pilot frequency assignments (deliberately out of scope per the original brief's own "red flags" list).
