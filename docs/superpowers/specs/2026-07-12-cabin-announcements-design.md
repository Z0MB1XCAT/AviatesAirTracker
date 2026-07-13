# Cabin Announcement System — Design Spec

**Date:** 2026-07-12
**Status:** Approved, pending implementation

## Overview

Automatic cabin PA announcements (welcome, safety briefing, seatbelt sign,
pre-departure/pre-landing prep, deboarding, etc.) triggered by real MSFS
telemetry and flight-session state. Ten distinct cues, twelve audio files
(one cue has a cruise/climb variant and a descending variant).

The audio files themselves are **not** part of this work — they don't exist
yet. This spec covers the plumbing that plays them automatically once they
do. Missing files are handled gracefully (skipped + logged) so the feature
is fully testable before any MP3s are recorded.

## Non-goals

- Recording/sourcing the actual MP3 audio — out of scope, user-provided.
- Exact-second timing on the four "N minutes before an undetected future
  event" cues (see Timing Approximations below) — these are best-effort
  proxies, not real countdowns.
- Replay-mode playback — announcements only fire off live SimConnect
  telemetry via `FlightSessionManager`, not the separate replay pipeline.

## Timing approximations (agreed with user)

Four cues describe "X minutes before an event that hasn't happened yet."
Two are computed with real ETA math; two use nearest-detectable-event
proxies because no predictive data exists pre-departure:

| Cue | Requested timing | Approach |
|---|---|---|
| Welcome | 5-10 min before pushback | Proxy: fires when engines start |
| Cabin secure (takeoff) | 10-15 min before takeoff | Proxy: fires when taxi begins |
| Seats for takeoff | 1-2 min before takeoff | Proxy: fires when aircraft lines up on the runway |
| Cabin secure (landing) | 10-15 min before landing | Real ETA: distance to `ArrivalLat/Lon` ÷ groundspeed |

## Architecture

```
FlightSessionManager (existing)
  ├─ TelemetryUpdated (20Hz)      ──┐
  ├─ SessionStateChanged            │
  ├─ FlightCompleted                ├─→ CabinAnnouncementService (new singleton)
  └─ CurrentFlight.PlannedFlight    │        │
LandingAnalyzer.LandingDetected  ──┘        │
SettingsService (enabled/volume) ───────────┘
                                              │ AnnouncementRequested(fileName)
                                              ▼
                                   MainLayout.razor (existing, always mounted)
                                              │ JS.InvokeVoidAsync("aviatesCabin.enqueue", ...)
                                              ▼
                                   window.aviatesCabin (new, interop.js)
                                   HTMLAudioElement queue, 25s gap between clips
```

`CabinAnnouncementService` does not touch `FlightPhaseDetector`'s state
machine — it reads `snap.Phase` (already populated by `TelemetryProcessor`
before `TelemetryUpdated` fires) rather than adding new phase logic. This
keeps the carefully-tuned phase state machine untouched.

### New SimConnect variables

Two variables get appended to the **end** of `AircraftState` (never
inserted mid-struct — a rejected/unsupported variable silently shifts every
subsequent field, which previously broke landing detection). Corresponding
`Add()` calls appended as the last two entries in
`RegisterDataDefinitions()`:

- `PUSHBACK STATE` (enum: 0=straight, 1=left, 2=right, 3=none)
- `CABIN SEATBELTS ALERT SWITCH` (bool)

Known limitation: some third-party liveries never report `PUSHBACK STATE`,
in which case the safety-briefing cue simply won't fire for that aircraft.
No fallback heuristic is planned for this — noted as an accepted gap.

### CabinAnnouncementService

New singleton, registered in `App.xaml.cs` alongside the other services.
Constructor takes `FlightSessionManager`, `LandingAnalyzer`,
`SettingsService`. Exposes:

```csharp
public event EventHandler<string>? AnnouncementRequested; // relative path under wwwroot/audio/cabin/
```

Internal one-shot flags (one bool per cue) reset when `FlightCompleted`
fires. A `CancellationTokenSource` scoped to the current flight cancels any
in-flight `Task.Delay` timers (the 20s disarmed-doors→deboarding gap and
the 2.5 min post-landing timer) if the flight resets before they elapse.

Seatbelt-sign detection additionally enforces a 60s debounce after firing,
so turbulence-driven rapid toggling doesn't spam the queue.

## Trigger table

| # | File | Trigger | Phase/state gate |
|---|---|---|---|
| 1 | `welcome.mp3` | `SessionStateChanged` → PreFlight | Engines just started |
| 2 | `safety_briefing.mp3` | `PUSHBACK STATE` 3 → {0,1,2} | — |
| 3 | `cabin_secure_takeoff.mp3` | `SessionStateChanged` → Taxiing | — |
| 4 | `seats_takeoff.mp3` | `ON ANY RUNWAY` false→true, low groundspeed | `FlightSessionState.Taxiing` |
| 5 | `after_takeoff.mp3` | AGL crosses ~2,500 ft climbing | After takeoff recorded (`_hasRecordedTakeoff`) |
| 6a | `seatbelt_sign_on.mp3` | `CABIN SEATBELTS ALERT SWITCH` rising edge | `Phase` ∈ {Climb, Cruise} |
| 6b | `seatbelt_sign_on_descending.mp3` | Same rising edge | `Phase` ∈ {TopOfDescent, Descent, Approach, FinalApproach} |
| 7 | `cabin_secure_landing.mp3` | ETA to `ArrivalLat/Lon` ≤ 15 min (great-circle distance ÷ groundspeed) | Descending; falls back to 10,000 ft AGL crossing if no SimBrief plan loaded |
| 8 | `seats_landing.mp3` | AGL descends through 3,000 ft | `Phase` ∈ {Approach, FinalApproach} |
| 9 | `landing_announcement.mp3` | Fixed 2.5 min timer from `LandingDetected` | Cancelled if flight resets first |
| 10a | `disarmed_doors.mp3` | Engines-running true→false | `FlightSessionState` ∈ {Landed, Complete} |
| 10b | `deboarding.mp3` | 20s after 10a | Same cancellation guard |

Each cue fires at most once per flight.

## Settings

New section in `Settings.razor`, new fields on `AppSettings`:

```csharp
public bool CabinAnnouncementsEnabled { get; set; } = true;
public int CabinAnnouncementsVolume { get; set; } = 80; // 0-100
```

When disabled, `CabinAnnouncementService` still runs its detection logic
internally (flags stay consistent) but does not raise
`AnnouncementRequested`.

## Playback (interop.js)

New `window.aviatesCabin` module, separate from the existing `aviatesAudio`
(which handles the departure-alert chime via Web Audio API buffers — kept
as-is, unrelated). Uses `HTMLAudioElement` for simplicity and native
`ended` event support:

```js
window.aviatesCabin = {
  _queue: [],
  _playing: false,
  enqueue(url, volumePct) { /* push; play immediately if idle */ },
  _playNext() { /* new Audio(url), set volume, play(), on ended -> setTimeout(this._playNext, 25000) */ }
};
```

A 404/decode failure on a given clip logs and advances the queue rather
than blocking or falling back to a synthesized tone (unlike
`aviatesAudio`'s chime fallback — these are meant to be real recordings).

## File manifest

Twelve MP3s go in `AviatesAirTracker/wwwroot/audio/cabin/` (new directory):

```
welcome.mp3
safety_briefing.mp3
cabin_secure_takeoff.mp3
seats_takeoff.mp3
after_takeoff.mp3
seatbelt_sign_on.mp3
seatbelt_sign_on_descending.mp3
cabin_secure_landing.mp3
seats_landing.mp3
landing_announcement.mp3
disarmed_doors.mp3
deboarding.mp3
```

## Testing

No automated test suite exists in this project. Verification is manual:
build, run against MSFS (or the SimConnect stub with synthetic telemetry
if available), and step through a flight confirming each cue fires at the
right point, respects the settings toggle/volume, and that missing files
don't crash playback.
