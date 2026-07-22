# Hoppies ACARS Integration — Design Spec

Date: 2026-07-22
Status: Approved for planning

## Summary

Add a real Hoppies ACARS network integration that sends automated, professionally-formatted telex messages to a pilot's ACARS-capable addon/FMC (e.g. PMDG CDU MSG page, FSHoppie) at key points in a flight: pre-departure loadsheet + takeoff speeds, pushback reminder, airborne/landing calls, a post-flight summary, and situational advisories (static conflict/volcano/wildfire hazard list + live weather crosswind/visibility checks). Delivery uses the real Hoppies telex API (`hoppie.nl/acars/system/connect.html`) under the VA's company logon code, addressed to the pilot's VA-assigned callsign. Alongside this, fixes the CityHopper/Cargo/Mainline callsign-prefix classification, which already exists (`Subsidiary.IcaoPrefix` in `RoutesService.cs`) but has a stale/incomplete fleet-type list.

## Goals

- One-way (company → pilot) automated ACARS telexes over the real Hoppies network.
- Timing-based triggers off SimBrief's scheduled departure time (loadsheet 15 min prior, takeoff speeds ~1-2 min after the loadsheet, pushback reminder 5 min prior).
- Flight-phase-based triggers (airborne call, landing call, post-flight engine-shutdown summary).
- Situational advisories: a hand-maintained hazard-zone list (conflict zones, volcanic activity, wildfires) plus live METAR-derived crosswind/visibility warnings — scoped to whichever airport (departure/arrival) is still relevant given the flight's current progress.
- Settings UI: pilot's own Hoppies code (stored, validated, not yet used for sending), a master enable toggle, and per-category send toggles.
- Fix callsign-prefix classification so CityHopper/Cargo flights get `VAH`/`VAC` instead of falling through to `VAV`.

## Non-goals

- Two-way messaging (pilot replying via Hoppies, polling the pilot's own inbox). The pilot's personal Hoppies code is captured now for a future feature but unused for sending in this build.
- Real-time news-based war/conflict detection. Handled via a static, hand-maintained hazard list instead.
- User-configurable weather thresholds (crosswind/visibility limits are hardcoded defaults for this build).
- Full CG/weight-and-balance engine for arbitrary aircraft. Only the fleet types listed below get computed MAC/LI; everything else omits those fields rather than fabricating numbers.

## Architecture

New components, following the existing singleton-service DI pattern used elsewhere in the app (`FuelAnalyzer`, `RunwayDetector`, `AviatesBackendClient`):

1. **`Core/Backend/HoppiesAcarsClient.cs`** — RestSharp wrapper around Hoppies' `connect.html` telex endpoint. Holds the VA company logon code as a `private const string` (hardcoded, matching the existing pattern of hardcoding the backend base URL as a const — accepted risk, per explicit decision below). One method:
   ```csharp
   Task<bool> SendTelexAsync(string toCallsign, string message);
   ```
   Request: `GET https://www.hoppie.nl/acars/system/connect.html?logon=<const>&from=<VA OPS station id>&to=<toCallsign>&type=telex&packet=<message>`. Best-effort: try/catch, Serilog `[HoppiesAcars]` prefix, failures logged at `Warning` and swallowed (non-critical, matches `AviatesBackendClient`'s style for non-critical calls).

2. **`Services/HazardZoneService.cs`** — loads/reloads `%AppData%\AviatesAirTracker\hazard_zones.json` (seeded with an example template on first run). Exact-ICAO lookup. Manual reload method so the file can be hand-edited without an app restart.
   ```json
   [
     { "icao": "OSDI", "type": "conflict", "message": "ACTIVE CONFLICT ZONE - EXERCISE EXTREME CAUTION" },
     { "icao": "BIKF", "type": "volcano", "message": "VOLCANIC ACTIVITY REPORTED IN VICINITY - MONITOR NOTAMS" }
   ]
   ```
   `type` is one of `conflict` / `volcano` / `wildfire` (informational only — all types render through the same advisory message template).

3. **`Services/WeatherAdvisoryService.cs`** — wraps `SimBriefService.FetchMetarAsync(icao)` (already live, aviationweather.gov, no key). Adds a raw-METAR parser for:
   - Wind group (`28015KT` / `28015G25KT`) → direction/speed/gust.
   - Visibility group (`1/2SM` / `0400`) → statute miles / meters.
   - Crosswind computed against the actual runway heading when known (via `RunwayDetector`); falls back to flagging on raw sustained/gust wind speed when no specific runway is known yet.
   - Opportunistic parsing of `VA` (volcanic ash) / `FU` (smoke) remark codes, but these are not the primary mechanism for volcano/wildfire advisories — `HazardZoneService`'s static list is.
   - Hardcoded thresholds for this build: sustained wind ≥25kt or gusts ≥35kt, visibility <1SM/<1600m.

4. **`Services/HoppiesMessageComposer.cs`** — pure formatting: builds each message body (see Message Formats below) from the domain data handed to it (no I/O, no state).

5. **`Services/HoppiesAcarsOrchestrator.cs`** — scheduling brain. Subscribes to:
   - `SimBriefService`'s flight-plan-loaded event (for departure advisories + arming the loadsheet/pushback/takeoff-speed timers)
   - `FlightPhaseDetector.PhaseChanged` (airborne call on first `InitialClimb`; arrival-advisory check on first `Descent`/`TopOfDescent`)
   - `LandingAnalyzer.LandingDetected` (landing call, carries `LandingResult` for the rate)
   - `FlightSessionManager.FlightCompleted` (post-flight summary, carries `FlightRecord`)
   - `FlightSessionManager.TelemetryUpdated`, tick-gated (~every 30s, same pattern as `AcarsPositionService`) for the wall-clock loadsheet/takeoff-speed/pushback checks against `SimBriefFlightPlan.ScheduledDepartureUtc`

   Persists per-flight sent-state to `%AppData%\AviatesAirTracker\hoppies_state.json` (cleared on new plan/flight start) so a mid-flight app restart doesn't re-send anything. Raises a `MessageSent` event (timestamp, category, message text) for the UI log. Reads `AppSettings.HoppiesEnabled` + per-category toggles before doing anything, and no-ops entirely when the master toggle is off.

6. **Settings additions** — see below.

7. **`Acars.razor` in-app log** — new card, reverse-chronological list fed by `HoppiesAcarsOrchestrator.MessageSent`, using the existing `InvokeAsync(() => StateHasChanged())` cross-thread pattern. Not persisted across restarts (only the dedupe state persists).

## Settings

New fields on `AppSettings` (`Services/SupportServices.cs`):

| Field | Type | Default | Purpose |
|---|---|---|---|
| `HoppiesPilotCode` | string | `""` | Pilot's personal Hoppies logon code. Stored + validated (lightweight poll call), not used for sending yet. |
| `HoppiesEnabled` | bool | `false` | Master toggle. |
| `HoppiesSendLoadsheet` | bool | `true` | Includes takeoff-speeds follow-up. |
| `HoppiesSendTimingReminders` | bool | `true` | Pushback reminder. |
| `HoppiesSendAirborneLandingCalls` | bool | `true` | |
| `HoppiesSendPostFlightSummary` | bool | `true` | |
| `HoppiesSendWeatherAdvisories` | bool | `true` | |
| `HoppiesSendHazardAdvisories` | bool | `true` | |

New "Hoppies ACARS" card in `Settings.razor`, near the existing "ACARS Configuration" section: pilot-code text input + verify affordance (mirrors the `AcarsKey`/`_isVerified` pattern), master toggle, six category checkboxes (disabled when master toggle is off). Saves immediately on change, matching the existing toggle/select save pattern on that page.

## Message triggers & timing

| Trigger | Source | Notes |
|---|---|---|
| Loadsheet | Wall-clock vs `ScheduledDepartureUtc`, `-15min` | Fires immediately if the plan loads after the threshold has already passed. Skipped if `ScheduledDepartureUtc` is null. |
| Takeoff speeds | Independent one-shot timer, 1-2 min after loadsheet fires | Separate telex, not bundled into the loadsheet. |
| Pushback reminder | Wall-clock vs `ScheduledDepartureUtc`, `-5min` | Independent sent-state from loadsheet. |
| Airborne call | `PhaseChanged` → first entry to `InitialClimb` | One-shot per flight. |
| Landing call | `LandingAnalyzer.LandingDetected` | Carries `LandingResult` directly. |
| Post-flight summary | `FlightSessionManager.FlightCompleted` | Pulls flight time, flight number, callsign, landing rate, route from `FlightRecord`. |
| Departure advisories (weather + hazard) | On SimBrief plan load | Only ever evaluated once, before/at loadsheet time — naturally stops being relevant once departed. |
| Arrival advisories (weather + hazard) | First `PhaseChanged` → `Descent`/`TopOfDescent` | Only evaluated once inbound. |

## Message formats

Plain-text telex bodies (Hoppies has no rich formatting):

**Loadsheet** (15 min before departure):
```
LOADSHEET
<FLIGHT NUMBER> <DDMMMYY>
<DEP ICAO> <ARR ICAO> <REGISTRATION>
.........................
ZFW  <val>
TOW  <val>
PAX  <count>
MACZFW <val>      } only when known (SimBrief-provided or WeightBalanceDatabase)
MACTOW <val>      }
LIZFW  <val>      }
LITOW  <val>      }
FUEL IN TANKS <val>
END
```

**Takeoff speeds** (1-2 min after loadsheet):
```
TAKEOFF DATA - <CALLSIGN>
V1: <val> VR: <val> V2: <val>
FLAPS: <if available> RWY: <if available>
```

**Pushback reminder** (5 min before departure):
```
<CALLSIGN>, SCHEDULED OFF-BLOCK IN 5 MIN (<HHMM>Z). PREPARE FOR PUSHBACK.
```

**Airborne call:**
```
<CALLSIGN> AIRBORNE <DEP> <HHMM>Z. HAVE A SAFE FLIGHT.
```

**Landing call:**
```
<CALLSIGN> TOUCHDOWN <ARR> <HHMM>Z. WELCOME TO <ARR>.
```

**Post-flight summary** (engine shutdown):
```
FLIGHT SUMMARY - <FLIGHT NUMBER> / <CALLSIGN>
ROUTE: <DEP>-<ARR>
BLOCK TIME: <hh:mm> AIR TIME: <hh:mm>
LANDING RATE: <VS> FPM (<GRADE>)
THANK YOU FOR FLYING WITH US
```

**Hazard advisory:**
```
SECURITY ADVISORY - <ICAO>
<hazard text from hazard_zones.json>
EXERCISE CAUTION AND FOLLOW COMPANY/ATC GUIDANCE
```

**Weather advisory:**
```
WX ADVISORY - <ICAO>
<condition, e.g. "CROSSWIND 28KT EXCEEDS RECOMMENDED LIMITS" or "VISIBILITY 400M - LOW VIS OPS MAY APPLY">
REVIEW LATEST METAR/TAF BEFORE <DEP/ARR>
```

## Loadsheet MAC/LI data pipeline

1. Extend `SimBriefService.ParseSimBriefJson` to also read `weights.est_zfw`, `weights.pax_count`, `weights.cargo` into new `SimBriefFlightPlan` fields (`ZeroFuelWeightKg`, `PaxCount`, `CargoKg`).
2. Check whether the parsed OFP data (or `OFPText`) already includes computed CG/MAC/LI for that specific format — use verbatim if present (most accurate, since SimBrief computed it).
3. Otherwise, fall back to a new `Core/Analytics/WeightBalanceDatabase.cs` — a small hand-maintained table (BEW, LEMAC, MAC length, standard arms) keyed by aircraft type, covering only the VA's actual fleet (see below). Computes MACZFW/MACTOW/LIZFW/LITOW from ZFW/TOW.
4. If the aircraft type isn't in the table and SimBrief didn't provide the data, the four MAC/LI lines are omitted entirely — never fabricated for an unknown type.

## Callsign prefix fix

`Subsidiary.CityHopperTypes`/`CargoTypes` in `RoutesService.cs` already implements `VAH`/`VAC`/`VAV` prefix selection (commits `f939842`/`1af5ae6`) but the fleet-type lists are stale, and matching is exact-string (case/whitespace sensitive), which silently falls through to `VAV` on any mismatch (e.g. `"ATR72"` vs `"ATR 72"`).

Corrected fleet, per your latest list:
- **CityHopper (`VAH`):** A319, B736, ATR72, ATR 42
- **Cargo (`VAC`):** B77F
- **Mainline (`VAV`, default):** A320neo, A350, A380, B38M, B739ER, B748

Matching is normalized (trim + case-insensitive + whitespace-collapsed) on both the lookup-table entries and the incoming `aircraftType` string before comparing, so spacing/casing differences stop causing silent fallthrough to `VAV`.

This same corrected fleet list feeds `WeightBalanceDatabase` (MAC/LI section above) and is the basis for the Hoppies delivery callsign (VA-assigned booking callsign, per the Recipient Callsign decision below).

## Key decisions

- **Delivery target:** real Hoppies network (`connect.html` telex API), not an in-app-only simulation.
- **Recipient callsign:** the VA-assigned booking callsign (`FlightRecord`/`FlightBooking.Callsign`), not the SimConnect ATC ID or a manually-typed field. Requires the pilot's own FMC/addon to be configured with the matching callsign to actually receive it.
- **Company logon code:** hardcoded as a source constant in `HoppiesAcarsClient` (accepted: it's in git history and technically extractable from the compiled EXE, per explicit user decision — not treated as a high-value secret).
- **Pilot's personal Hoppies code:** captured and validated in Settings now, but not used for sending in this build — reserved for a future two-way feature.
- **Hazard detection:** static, hand-maintained `hazard_zones.json` — no live news/data feed. Same mechanism serves conflict zones, volcanic activity, and wildfires.
- **Weather data:** existing `SimBriefService.FetchMetarAsync` (aviationweather.gov, no key) — no new external dependency.
- **Late/past-due timing triggers:** fire immediately once detected rather than being silently skipped.
- **Restart safety:** per-flight sent-state persisted to disk so a mid-flight relaunch doesn't duplicate messages.
- **In-app history:** simple session-only log on `Acars.razor`, fed by an orchestrator event; not persisted (only dedupe state persists).
- **CG/MAC:** SimBrief-provided data first, hand-maintained `WeightBalanceDatabase` fallback for known fleet types, omitted entirely otherwise — never fabricated for an unknown aircraft.
- **Settings granularity:** one master toggle + six independent per-category checkboxes.

## Open questions / follow-ups (explicitly out of scope for this build)

- Two-way Hoppies messaging (pilot replies, inbox polling).
- User-configurable weather thresholds.
- Expanding `WeightBalanceDatabase` beyond the current fleet list as new aircraft types are added to the VA.
