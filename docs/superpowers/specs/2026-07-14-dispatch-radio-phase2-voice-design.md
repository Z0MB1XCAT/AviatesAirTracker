# Dispatch Radio System — Phase 2 (Live Voice Relay) — Design Spec
**Date:** 2026-07-14
**Scope:** Add live push-to-talk voice between pilots and a human dispatcher over the existing dispatch radio channel, with distance-based audio degradation. Builds directly on the foundation and Cloudflare backend port already shipped — see `docs/superpowers/specs/2026-07-12-dispatch-radio-phase1-design.md` and `docs/superpowers/specs/2026-07-13-dispatch-radio-cloudflare-backend-design.md`. Hoppies ACARS text fallback remains a separate, later spec.

> **Amended during implementation planning (2026-07-14):** two details below were refined once the actual protocol/engineering tradeoffs became concrete, and this doc wasn't updated inline at the time — recorded here instead of silently drifting. (1) Playback uses a simple buffer-then-decode approach (`AudioContext.decodeAudioData()` once the sender's `voice_end` arrives), not the `MediaSource`/`SourceBuffer` streaming approach described in the Components section — MediaSource's segment-queuing has real race conditions and the buffer-then-play tradeoff (a little added latency per PTT burst) is a better fit for bursty radio traffic. (2) The dispatcher console does **not** apply distance-based degradation to incoming audio — only the constant radio band-pass filter applies there. The dispatcher sits at the station itself (0nm from EGNX by definition), so unlike a pilot, they have no "own distance" to derive a degradation factor from; the "reciprocal degradation" language further down this doc was the original intent but doesn't have a clean implementation without either inventing a distance-of-transmitter model (a bigger design change) or hardcoding degradation to a pilot's own distance regardless of who's listening (inconsistent with how the rest of the system already treats "signal quality" as a property of the listener's own position, per Phase 1). Implemented as: dispatcher always hears clear (but still radio-timbred) audio, full 3-layer degradation only applies pilot-side.

---

## Background

Phase 1 and the Cloudflare backend port built the foundation: pilots tune COM1 to 123.575, the `aviates-dispatch-radio` Worker (Durable Object `DispatchRadioRoom`) tracks their position and classifies them `clear` (<200nm) / `degraded` (200–400nm) / `acars_only` (>400nm) relative to EGNX, and a read-only admin page shows who's tuned in. None of that carries actual voice — this round adds the audio.

The original Phase 1 spec called for a self-hosted Mumble server on an Oracle Cloud VM. Oracle was already ruled out once (requires a credit card even on its free tier) and the same constraint applies to any other persistent-VM host. Rather than introduce a new paid or card-gated service, this spec reuses the Cloudflare Worker infrastructure already deployed and verified: a new `/voice` WebSocket route on the same Durable Object relays raw audio bytes with no server-side processing. This trades away some of what a real WebRTC SFU (jitter buffering, adaptive bitrate) would offer, but that tradeoff fits: this is push-to-talk radio traffic (bursty, one speaker at a time), not full-duplex conferencing, so the simpler relay is a good match rather than a compromise.

## Decisions Made

| Question | Decision |
|---|---|
| Voice server hosting | Reuse the existing `aviates-dispatch-radio` Cloudflare Worker + `DispatchRadioRoom` Durable Object. No new service, account, or signup. |
| Dispatcher | A real human operator (VA staff) on `AviatesAirWebsite`'s `/admin/dispatch` page — not automated. |
| PTT semantics | Push-to-talk, party-line. Whoever transmits is heard by everyone currently `clear` or `degraded` (dispatcher included), not just a dispatcher↔pilot private call. No server-side floor control — overlapping transmissions are allowed and simply overlap on playback, mirroring real radio "doubling" rather than queuing or blocking. |
| Pilot PTT input | A configurable global keyboard hotkey (Win32 low-level keyboard hook, works regardless of which window has focus) **and** an optional joystick/HOTAS button (via `SharpDX.DirectInput`). Both unbound by default. |
| Audio transport | Binary WebSocket frames over a new `/voice` route on the same Durable Object used for `/pilot`/`/admin`. The Durable Object does a dumb byte relay — it never decodes, inspects, or modifies audio payloads. |
| Audio codec/container | Browser `MediaRecorder` (`audio/webm;codecs=opus`), ~100ms timeslice chunks. Reassembled on the receiving end via MediaSource Extensions for gapless playback. |
| Degradation | Applied entirely client-side via the Web Audio API, driven by the `state`/`distanceNm` each client already receives over the existing `/pilot` protocol. The server never touches audio content, so it can't apply distance-based effects even if it wanted to. |
| Client platform note | `AviatesAirTracker`'s Blazor UI runs inside a `BlazorWebView`, which is WebView2 (Chromium) on Windows. It has full `getUserMedia`/`MediaRecorder`/Web Audio support already — the pilot client uses the same browser APIs as the website, via JS interop, not a native .NET audio/WebRTC library. |

## Components

### 1. Backend — `DISPATCH-RADIO-WORKER/src/room.js`, `src/worker.js` (modified)

- **New hibernation tag `voice`**, alongside the existing `pilot`/`admin` tags. A pilot's voice socket connects to `/voice?pilotId=<id>`; the dispatcher's voice socket connects to `/voice?key=<ADMIN_WS_KEY>` (same gating as `/admin` today).
- On connect, a pilot voice socket immediately `serializeAttachment({ pilotId })` (mirrors the existing hibernation-safety pattern in `rebuildRegistry()`), so the DO can map a voice socket back to that pilot's current `state` in the registry without needing a separate lookup structure.
- `webSocketMessage(ws, message)` on a `voice`-tagged socket branches on frame type:
  - **Binary (`ArrayBuffer`)** — an audio chunk. Relay verbatim (no parsing, no re-encoding) to: all `admin`-tagged voice sockets, plus all `pilot`-tagged voice sockets whose current registry `state` is `clear` or `degraded` — excluding the sender itself.
  - **Text (JSON)** — a speaking-indicator marker, `{"type":"voice_start"|"voice_end","from":"<pilotId>"|"dispatch"}`. Relayed via the same eligibility rule as audio frames, so the dispatcher and in-range pilots know who's currently transmitting before the first audio chunk arrives.
  - Anything else (malformed frame) is ignored, matching the existing `/pilot` malformed-payload handling.
- `worker.js`: route `/voice` through the same upgrade-forwarding logic as `/pilot`/`/admin`. A pilot connection (no `key` param, has `pilotId`) is let through unconditionally, same as `/pilot` today; a dispatcher connection (has `key`) is checked against `env.ADMIN_WS_KEY` before forwarding, same as `/admin` today, including the existing fail-closed behavior when the secret is unset.
- No new persistent storage, no new alarm — this reuses the existing registry and sweep entirely. A stale/dropped pilot is already removed from the registry by the existing sweep, which naturally excludes them from future voice relay eligibility too.

### 2. Pilot client (`AviatesAirTracker`)

- **`Services/PttInputService.cs` (new)** — singleton, registered in `App.xaml.cs` alongside the other services.
  - Keyboard: installs a `WH_KEYBOARD_LL` hook via P/Invoke (`SetWindowsHookEx`) to detect the configured hotkey system-wide, regardless of focused window (MSFS will have focus during flight).
  - Joystick: polls `SharpDX.DirectInput` devices on a background loop (new NuGet dependency, same pattern as existing dependencies like `DiscordRichPresence`) for the configured button.
  - Both sources normalize to one `event EventHandler<bool> PttStateChanged` (`true` = pressed), so downstream code doesn't care which input fired.
  - Both bindings are **unbound by default** — voice PTT is inert until the pilot sets one in Settings.
- **`Services/DispatchRadioService.cs` (modified)** — opens a second WebSocket to `/voice?pilotId=<id>` alongside the existing `/pilot` socket, on the same tune/untune transitions and 5s reconnect pattern already implemented. Subscribes to `PttInputService.PttStateChanged`: on press, sends a `voice_start` marker and calls into JS interop to start capture; on release, calls stop-capture and sends `voice_end`.
- **`wwwroot/js/voice.js` (new)** — exposes `window.aviatesVoice`, mirroring the existing `window.aviatesMap` interop pattern:
  - `startCapture()` / `stopCapture()`: wraps `getUserMedia({ audio: true })` + `MediaRecorder` (`audio/webm;codecs=opus`, `timeslice=100`), streaming each `dataavailable` chunk back to C# via `DotNetObjectReference` for forwarding over the `/voice` socket. (Chunks pass through .NET only to reach the WebSocket — no processing happens there.)
  - Receive side: on a `voice_start` marker for a given sender, creates a `MediaSource` + `SourceBuffer` (`audio/webm; codecs="opus"`) and an `<audio>` element playing `URL.createObjectURL(mediaSource)`. Each incoming binary chunk for that sender is appended via `sourceBuffer.appendBuffer()`. On `voice_end` (or a timeout if the end marker is lost), the `MediaSource` stream is ended and the element cleaned up.
  - The degradation effect chain (see below) is inserted between the `<audio>` element and its output via a `MediaElementAudioSourceNode`.
- **WebView2 microphone permission** — `MainWindow.xaml.cs` gets a new `CoreWebView2.PermissionRequested` handler (nothing like this exists yet) that auto-approves microphone access for the app's own local origin. Without this, `getUserMedia` would silently fail or hang on a permission prompt the user can't see.
- **`Pages/Settings.razor` (modified)** — the existing "Dispatch Radio" panel gains: a "PTT Hotkey" field (click to capture, shows the bound key, Clear button) and a "PTT Joystick Button" field (device dropdown + "press a button to bind" capture), both writing to new `AppSettings` fields (`PttHotkey`, `PttJoystickGuid`, `PttJoystickButton`).

### 3. Dispatcher console (`AviatesAirWebsite/src/app/admin/dispatch/page.tsx`, modified)

- A hold-to-talk control (spacebar while the page has focus, or mouse-down on a visible button) using the same `getUserMedia`/`MediaRecorder`/MediaSource pattern as the pilot client, connected to `/voice?key=<the existing admin key already in `.env.local`>`.
- A "transmitting: `<callsign>`" indicator, driven by received `voice_start`/`voice_end` markers — shows which pilot (or "DISPATCH" for the operator's own outgoing transmission, mirrored locally) is currently talking.
- Same degradation effect chain as the pilot client applies here too, since the dispatcher is just as much a distance-based listener as any pilot (a pilot near the edge of "degraded" sounds equally degraded to the dispatcher as the dispatcher would sound to that pilot).

## Audio Degradation Model

A single `degradationFactor` — `0` at 200nm, `1` at 400nm, linear interpolation between — computed client-side from `distanceNm` (already streamed via the existing `/pilot` protocol). The listener's own distance drives the effect applied to *everything they receive*, regardless of who's transmitting. Three layers, chained in this order between the `MediaElementAudioSourceNode` and the destination:

1. **Radio timbre (constant, not distance-scaled)** — a `BiquadFilterNode` band-pass, roughly 300Hz–3000Hz, applied to every received transmission including at `clear` range. This alone is what makes it sound like a radio rather than hi-fi VoIP.
2. **Static hiss (scales with `degradationFactor`)** — a short looped, band-passed noise buffer (filtered toward pink noise, not harsh white noise) mixed in via a dedicated `GainNode` whose gain rises linearly with `degradationFactor`, capped at a ceiling that never exceeds the voice signal's own gain. Zero at `clear`, present but not overwhelming at the far edge of `degraded`.
3. **Occasional dropouts (frequency scales with `degradationFactor`)** — brief ~150ms gain dips on the final mixed output, scheduled on a randomized timer whose average interval shrinks as `degradationFactor` rises (sparse near 200nm, roughly every few seconds near 400nm). This is the only event-based effect and is deliberately kept sparse even at maximum range.

No pitch-shifting, no vocoder/robotic distortion, no hard clipping — these read as gimmicky rather than a realistic weakening signal. The three layers above mirror what an actual VHF signal does as it fades: always band-limited, rising noise floor, occasional fades — nothing more.

## Protocol (additions to the existing spec)

WebSocket, new route `/voice`.

Pilot connects: `wss://.../voice?pilotId=<same id used on /pilot>`
Dispatcher connects: `wss://.../voice?key=<ADMIN_WS_KEY>`

Binary frames: raw `audio/webm;codecs=opus` chunks, relayed byte-for-byte, no envelope.

Text frames (JSON), sent immediately before/after a transmission:
```json
{ "type": "voice_start", "from": "<pilotId>" }
{ "type": "voice_end", "from": "<pilotId>" }
```
Dispatcher's own transmissions use `"from": "dispatch"`.

## Error Handling

- **Mic permission denied** (browser or WebView2) — PTT control visibly disabled with a clear message ("Microphone access denied — check permissions"), not a silent failure where pressing PTT does nothing.
- **`/voice` socket drops while tuned** — same 5s reconnect loop already used for `/pilot`, logged the same way (`[DispatchRadio] Connection error, retrying in 5s`).
- **Lost `voice_end` marker** (e.g. socket drop mid-transmission) — receiving clients time out an active speaking session after ~2s of no new audio chunks, ending the `MediaSource` stream and clearing the "transmitting" indicator rather than leaving a stuck state.
- **`acars_only` pilots** — excluded at the relay level in `room.js`, so they never receive voice frames at all. This is enforced server-side (not a client-side mute they could bypass by inspecting frames), consistent with the existing text-channel design where `acars_only` is a hard boundary.
- **Overlapping transmissions** — no server-side floor control (see Decisions table); if two people transmit at once, both audio streams play and overlap for listeners, same as real radio "doubling." No special handling needed beyond what MediaSource playback of two concurrent sender sessions already does naturally.

## Testing Plan

- **Backend**: extend the existing local `wrangler dev` verification script (same technique used for the text channel in the Cloudflare port) to open a `/voice` socket, push binary frames, and assert they're relayed only to sockets whose registry state is `clear`/`degraded`, never to an `acars_only` pilot's socket.
- **Manual end-to-end**: two machines (or one machine + a browser tab acting as dispatcher) — one side transmits, confirm the other hears it with degradation matching the *listener's* distance bucket (not the transmitter's), and confirm an `acars_only`-range pilot hears nothing at all.
- **Degradation tuning**: with a known fixed distance (e.g. teleport to exactly 300nm, the midpoint of the degraded band), confirm the static/dropout intensity is audibly present but not overwhelming, adjusting the gain ceiling and dropout interval constants by ear if needed — these are tuning constants, not something to get exactly right on the first pass.
- **PTT input**: confirm the global hotkey fires while MSFS has focus (not just the tracker window), and confirm a bound joystick button behaves identically to the keyboard hotkey.

## Explicitly Out of Scope for This Round

- Hoppies ACARS text fallback for `acars_only` pilots — separate, later spec (per Phase 1).
- Multiple dispatch stations or per-pilot frequency assignments — still deliberately out of scope per the original brief.
- HF frequency detection — still deferred (no standard MSFS HF simvar), per Phase 1.
- Voice activity detection / auto-PTT (open-mic) — explicitly rejected in favor of manual PTT.
- Recording or playback history of past transmissions.
- Any change to the existing `/pilot`/`/admin` text protocol, distance/classification logic, or the admin page's existing pilot table — this round only adds voice alongside what's already there.
