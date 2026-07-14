# Dispatch Radio System — Phase 2 (Live Voice Relay) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add push-to-talk voice between pilots and a human dispatcher over the existing dispatch radio channel, with distance-based audio degradation, using only infrastructure already deployed (no new services).

**Architecture:** A new `/voice` WebSocket route on the already-deployed `aviates-dispatch-radio` Durable Object relays binary audio frames and JSON speaking-indicator markers with zero server-side processing — eligibility (who receives what) is decided by the existing pilot registry's `state` field. Both `AviatesAirTracker` (via a WebView2/Blazor JS interop module) and `AviatesAirWebsite`'s admin page connect directly to this route, capture mic audio with `MediaRecorder`, and apply a three-layer Web Audio degradation effect on playback driven by the listener's own tracked distance.

**Tech Stack:** Cloudflare Workers/Durable Objects (existing), browser `MediaRecorder`/Web Audio API (both clients, since `BlazorWebView` is WebView2/Chromium), Win32 `SetWindowsHookEx` P/Invoke + `SharpDX.DirectInput` for pilot-side PTT input.

**Implementation notes (refine the approved spec, don't change its architecture):**
- Voice clips are buffered in full and played back on `voice_end` via `AudioContext.decodeAudioData()`, not streamed through `MediaSource`/`SourceBuffer`. Simpler, avoids `appendBuffer` queuing races, small added latency that doesn't matter for bursty PTT traffic.
- Binary audio frames carry a 1-byte-length sender-id prefix (`[idLen][id bytes][opus chunk]`) so a listener can demultiplex two overlapping transmitters into separate buffers. The Durable Object still never inspects this — it's opaque payload to the relay.
- `room.js`'s `webSocketClose` handler is fixed to only evict a pilot from the registry when their *text* (`pilot`-tagged) socket closes, not their new voice socket — otherwise ending a transmission would wrongly make them vanish from the admin's pilot list.

---

## Task 1: Backend — voice relay in the Durable Object

**Files:**
- Modify: `DISPATCH-RADIO-WORKER/src/room.js`

- [ ] **Step 1: Add voice routing to `fetch()`**

In `DISPATCH-RADIO-WORKER/src/room.js`, find:

```js
  async fetch(request) {
    const url = new URL(request.url);
    const isAdmin = url.pathname === '/admin';
    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);

    this.ctx.acceptWebSocket(server, [isAdmin ? 'admin' : 'pilot']);

    if (isAdmin) {
      server.send(JSON.stringify({ pilots: this.registry.list() }));
    }

    return new Response(null, { status: 101, webSocket: client });
  }
```

Replace with:

```js
  async fetch(request) {
    const url = new URL(request.url);
    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);

    if (url.pathname === '/voice') {
      // The entry Worker already validated the admin key (if present) before
      // forwarding here — see worker.js. A pilot voice link carries pilotId
      // instead, used only to look up that pilot's current registry state
      // when deciding relay eligibility (see relayVoiceFrame below). Uses
      // distinct 'voice-pilot'/'voice-admin' tags, not 'pilot'/'admin' —
      // those tags are reserved for the text-protocol sockets, and
      // rebuildRegistry()/webSocketClose() assume every 'pilot'-tagged
      // socket carries a full registry-record attachment. Tagging a voice
      // socket 'pilot' too would corrupt the registry on rebuild.
      const isDispatcherVoice = url.searchParams.has('key');
      const pilotId = url.searchParams.get('pilotId');
      this.ctx.acceptWebSocket(server, [isDispatcherVoice ? 'voice-admin' : 'voice-pilot']);
      if (!isDispatcherVoice && pilotId) {
        server.serializeAttachment({ pilotId });
      }
      return new Response(null, { status: 101, webSocket: client });
    }

    const isAdmin = url.pathname === '/admin';
    this.ctx.acceptWebSocket(server, [isAdmin ? 'admin' : 'pilot']);

    if (isAdmin) {
      server.send(JSON.stringify({ pilots: this.registry.list() }));
    }

    return new Response(null, { status: 101, webSocket: client });
  }
```

- [ ] **Step 2: Branch `webSocketMessage` for voice sockets**

Find:

```js
  async webSocketMessage(ws, message) {
    if (this.ctx.getTags(ws).includes('admin')) return;

    let msg;
```

Replace with:

```js
  async webSocketMessage(ws, message) {
    const tags = this.ctx.getTags(ws);

    if (tags.includes('voice-pilot') || tags.includes('voice-admin')) {
      this.relayVoiceFrame(ws, message);
      return;
    }

    if (tags.includes('admin')) return;

    let msg;
```

- [ ] **Step 3: Add the `relayVoiceFrame` method**

Find the `broadcastToAdmins()` method:

```js
  broadcastToAdmins() {
    const payload = JSON.stringify({ pilots: this.registry.list() });
    for (const ws of this.ctx.getWebSockets('admin')) {
      ws.send(payload);
    }
  }
```

Add immediately after it:

```js

  // Dumb relay — never inspects audio payload content, only decides who
  // receives it. Binary frames are raw audio chunks (with a sender-id
  // envelope the client encodes/decodes); text frames are voice_start/
  // voice_end speaking-indicator markers. Both use the same eligibility
  // rule: every admin voice socket, plus every pilot voice socket whose
  // current registry state isn't acars_only, excluding the sender.
  relayVoiceFrame(senderWs, message) {
    const isBinary = message instanceof ArrayBuffer;
    const isMarker = !isBinary && typeof message === 'string';
    if (!isBinary && !isMarker) return;

    for (const ws of this.ctx.getWebSockets('voice-admin')) {
      if (ws !== senderWs) ws.send(message);
    }

    for (const ws of this.ctx.getWebSockets('voice-pilot')) {
      if (ws === senderWs) continue;
      const data = ws.deserializeAttachment();
      const entry = data?.pilotId ? this.registry.get(data.pilotId) : undefined;
      if (entry && entry.state !== 'acars_only') {
        ws.send(message);
      }
    }
  }
```

- [ ] **Step 4: Fix `webSocketClose` to only evict on the text-channel socket closing**

Find:

```js
  async webSocketClose(ws) {
    const data = ws.deserializeAttachment();
    if (data?.pilotId) {
      this.registry.remove(data.pilotId);
      this.broadcastToAdmins();
    }
  }
```

Replace with:

```js
  async webSocketClose(ws) {
    // Only the text-protocol 'pilot' socket owns registry membership. A
    // pilot's separate voice socket closing (e.g. between transmissions,
    // or a brief voice-channel reconnect) must not evict them from the
    // registry while their text socket is still open — otherwise they'd
    // wrongly vanish from the admin's pilot list mid-flight.
    if (!this.ctx.getTags(ws).includes('pilot')) return;

    const data = ws.deserializeAttachment();
    if (data?.pilotId) {
      this.registry.remove(data.pilotId);
      this.broadcastToAdmins();
    }
  }
```

- [ ] **Step 5: Run the existing test suite to confirm nothing broke**

Run: `cd DISPATCH-RADIO-WORKER && npm test`
Expected: PASS, 7 tests total (unchanged — `distance.js`/`registry.js` aren't touched by this task; `room.js` has no unit tests, verified via `wrangler dev` in Task 3 instead).

- [ ] **Step 6: Commit**

```bash
git add DISPATCH-RADIO-WORKER/src/room.js
git commit -m "feat(dispatch-radio-worker): add voice relay channel to the Durable Object"
```

---

## Task 2: Backend — route `/voice` in the entry Worker

**Files:**
- Modify: `DISPATCH-RADIO-WORKER/src/worker.js`

- [ ] **Step 1: Add `/voice` gating**

Find:

```js
    if (url.pathname === '/admin') {
      const key = url.searchParams.get('key');
      if (env.ADMIN_WS_KEY && key !== env.ADMIN_WS_KEY) {
        return new Response('Unauthorized', { status: 401 });
      }
    } else if (url.pathname !== '/pilot') {
      return new Response('Not found', { status: 404 });
    }
```

Replace with:

```js
    if (url.pathname === '/admin') {
      if (!env.ADMIN_WS_KEY) {
        return new Response('Admin key not configured', { status: 500 });
      }
      const key = url.searchParams.get('key');
      if (key !== env.ADMIN_WS_KEY) {
        return new Response('Unauthorized', { status: 401 });
      }
    } else if (url.pathname === '/voice') {
      // A dispatcher voice link carries `key` (same admin gating as
      // /admin); a pilot voice link carries `pilotId` instead and needs
      // no key, matching /pilot's existing no-auth behavior.
      const key = url.searchParams.get('key');
      if (key !== null) {
        if (!env.ADMIN_WS_KEY) {
          return new Response('Admin key not configured', { status: 500 });
        }
        if (key !== env.ADMIN_WS_KEY) {
          return new Response('Unauthorized', { status: 401 });
        }
      } else if (!url.searchParams.get('pilotId')) {
        return new Response('pilotId required', { status: 400 });
      }
    } else if (url.pathname !== '/pilot') {
      return new Response('Not found', { status: 404 });
    }
```

Note: this also picks up the existing `/admin` fail-closed-on-unset-key fix (currently only present via an inline check inside the DO's routing comment) directly in the Worker, matching the pattern already used by `/admin` in the deployed code.

- [ ] **Step 2: Verify the Worker boots locally**

Run: `cd DISPATCH-RADIO-WORKER && npx wrangler dev --port 8787 --var ADMIN_WS_KEY:test-key` in the background, wait for it to report ready, then:

Run: `curl http://localhost:8787/health`
Expected: `ok`

Run: `curl -s -o /dev/null -w "%{http_code}\n" "http://localhost:8787/voice"`
Expected: `400` (no `key` and no `pilotId`)

Stop the `wrangler dev` process afterward.

- [ ] **Step 3: Commit**

```bash
git add DISPATCH-RADIO-WORKER/src/worker.js
git commit -m "feat(dispatch-radio-worker): route /voice through the entry Worker"
```

---

## Task 3: Backend — local verification of the voice relay

**Files:**
- Create (temporary, not committed): `DISPATCH-RADIO-WORKER/verify-voice-local.mjs`

- [ ] **Step 1: Write the verification script**

Create `DISPATCH-RADIO-WORKER/verify-voice-local.mjs` (temporary — delete after use, do not commit it):

```js
import WebSocket from 'ws';

const PORT = 8787;
const KEY = 'local-verify-key';

function waitForOpen(ws) {
  return new Promise((resolve, reject) => {
    ws.once('open', resolve);
    ws.once('error', reject);
  });
}

function waitForMessage(ws) {
  return new Promise((resolve) => ws.once('message', (d) => resolve(d)));
}

function encodeFrame(senderId, text) {
  const idBytes = Buffer.from(senderId, 'utf8');
  const payload = Buffer.from(text, 'utf8');
  return Buffer.concat([Buffer.from([idBytes.length]), idBytes, payload]);
}

function decodeFrame(buf) {
  const idLen = buf[0];
  const senderId = buf.subarray(1, 1 + idLen).toString('utf8');
  const payload = buf.subarray(1 + idLen).toString('utf8');
  return { senderId, payload };
}

async function main() {
  const results = [];

  // Bring a "clear" pilot (at EGNX) and a "far" pilot (>400nm) into the
  // registry via their text /pilot sockets first, so relay eligibility has
  // real state to check against.
  const clearPilot = new WebSocket(`ws://localhost:${PORT}/pilot`);
  await waitForOpen(clearPilot);
  clearPilot.send(JSON.stringify({ pilotId: 'clear-pilot', callsign: 'AVI001', lat: 52.8311, lon: -1.3281, freq: '123.575' }));
  await waitForMessage(clearPilot);

  const farPilot = new WebSocket(`ws://localhost:${PORT}/pilot`);
  await waitForOpen(farPilot);
  farPilot.send(JSON.stringify({ pilotId: 'far-pilot', callsign: 'AVI002', lat: 61.0, lon: -1.3281, freq: '123.575' }));
  await waitForMessage(farPilot);

  const admin = new WebSocket(`ws://localhost:${PORT}/admin?key=${KEY}`);
  await waitForOpen(admin);
  await waitForMessage(admin); // initial pilots snapshot

  const clearVoice = new WebSocket(`ws://localhost:${PORT}/voice?pilotId=clear-pilot`);
  await waitForOpen(clearVoice);
  const farVoice = new WebSocket(`ws://localhost:${PORT}/voice?pilotId=far-pilot`);
  await waitForOpen(farVoice);
  const adminVoice = new WebSocket(`ws://localhost:${PORT}/voice?key=${KEY}`);
  await waitForOpen(adminVoice);

  // Dispatcher transmits — the clear pilot should hear it, the far pilot must not.
  let farVoiceReceived = false;
  farVoice.once('message', () => { farVoiceReceived = true; });

  const clearVoiceMsgPromise = waitForMessage(clearVoice);
  adminVoice.send(encodeFrame('dispatch', 'fake-opus-chunk'));
  const clearVoiceMsg = await clearVoiceMsgPromise;
  const decoded = decodeFrame(clearVoiceMsg);
  results.push(['clear pilot receives dispatcher voice frame', decoded.senderId === 'dispatch' && decoded.payload === 'fake-opus-chunk']);

  await new Promise((r) => setTimeout(r, 300));
  results.push(['acars_only pilot does NOT receive dispatcher voice frame', farVoiceReceived === false]);

  // Clear pilot transmits — admin should hear it.
  const adminVoiceMsgPromise = waitForMessage(adminVoice);
  clearVoice.send(encodeFrame('clear-pilot', 'pilot-opus-chunk'));
  const adminVoiceMsg = await adminVoiceMsgPromise;
  const adminDecoded = decodeFrame(adminVoiceMsg);
  results.push(['admin receives pilot voice frame', adminDecoded.senderId === 'clear-pilot' && adminDecoded.payload === 'pilot-opus-chunk']);

  // Marker frames relay the same way as binary frames.
  const adminMarkerPromise = waitForMessage(adminVoice);
  clearVoice.send(JSON.stringify({ type: 'voice_start', from: 'clear-pilot' }));
  const adminMarker = await adminMarkerPromise;
  results.push(['marker frames relay to admin too', JSON.parse(adminMarker.toString()).type === 'voice_start']);

  // Registry-safety fix: closing the pilot's VOICE socket must not evict
  // them from the registry (the admin's pilots list) while their text
  // socket is still open.
  const adminSnapshotPromise = new Promise((resolve) => {
    let sawUpdate = false;
    admin.on('message', (d) => {
      sawUpdate = true;
      resolve(JSON.parse(d.toString()));
    });
    setTimeout(() => resolve(sawUpdate ? null : { pilots: [{ pilotId: 'clear-pilot' }, { pilotId: 'far-pilot' }] }), 1500);
  });
  clearVoice.close();
  const afterVoiceClose = await adminSnapshotPromise;
  const stillPresent = afterVoiceClose === null || afterVoiceClose.pilots.some((p) => p.pilotId === 'clear-pilot');
  results.push(['closing a pilot voice socket does not evict them from the registry', stillPresent]);

  admin.close();
  farVoice.close();
  adminVoice.close();
  clearPilot.close();
  farPilot.close();

  console.log('\n=== VOICE VERIFICATION RESULTS ===');
  let allPass = true;
  for (const [name, pass] of results) {
    console.log(`${pass ? 'PASS' : 'FAIL'} - ${name}`);
    if (!pass) allPass = false;
  }
  console.log(allPass ? '\nALL CHECKS PASSED' : '\nSOME CHECKS FAILED');
  process.exit(allPass ? 0 : 1);
}

main().catch((err) => {
  console.error('VERIFICATION ERRORED:', err);
  process.exit(1);
});
```

- [ ] **Step 2: Run the verification**

Run: `cd DISPATCH-RADIO-WORKER && npx wrangler dev --port 8787 --var ADMIN_WS_KEY:local-verify-key` in the background, wait for it to report ready, then:

Run: `cd DISPATCH-RADIO-WORKER && node verify-voice-local.mjs`
Expected: `ALL CHECKS PASSED`, exit code 0.

Stop the `wrangler dev` process afterward.

- [ ] **Step 3: Delete the temporary verification script**

```bash
rm DISPATCH-RADIO-WORKER/verify-voice-local.mjs
```

- [ ] **Step 4: Redeploy the Worker with the voice relay**

This updates the already-live `aviates-dispatch-radio` Worker — confirm with the user before running, per the project's standing rule on `wrangler deploy` requiring explicit go-ahead each time.

Run: `cd DISPATCH-RADIO-WORKER && npx wrangler deploy`
Expected: `Deployed aviates-dispatch-radio triggers` with the same URL as before (`https://aviates-dispatch-radio.dexterhsrees.workers.dev`).

---

## Task 4: Pilot client — PTT settings fields

**Files:**
- Modify: `AviatesAirTracker/Services/SupportServices.cs`

- [ ] **Step 1: Add the settings fields**

In `AviatesAirTracker/Services/SupportServices.cs`, find:

```csharp
    public string DispatchBackendUrl { get; set; } = "";
```

Add immediately after it:

```csharp
    // Push-to-talk binding for the dispatch voice channel. 0 = no keyboard
    // hotkey bound. Guid.Empty / -1 = no joystick button bound. Both start
    // unbound — voice PTT is inert until the pilot sets one in Settings.
    public int PttHotkeyVkCode { get; set; } = 0;
    public string PttHotkeyLabel { get; set; } = "";
    public Guid PttJoystickGuid { get; set; } = Guid.Empty;
    public int PttJoystickButton { get; set; } = -1;
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add AviatesAirTracker/Services/SupportServices.cs
git commit -m "feat(client): add PTT hotkey/joystick settings fields"
```

---

## Task 5: Pilot client — DOM-code to virtual-key mapping

**Files:**
- Create: `AviatesAirTracker/Services/PttKeyMap.cs`

- [ ] **Step 1: Implement the mapping**

Create `AviatesAirTracker/Services/PttKeyMap.cs`:

```csharp
namespace AviatesAirTracker.Services;

// Maps a browser KeyboardEventArgs.Code (stable, layout-independent) to its
// Win32 virtual-key code, for the subset of keys realistic as a PTT bind.
// BlazorWebView is WebView2 (Chromium), so DOM `code` values are the
// standard UI Events KeyboardEvent.code strings, captured directly in
// Settings.razor via @onkeydown — no JS interop needed for this.
public static class PttKeyMap
{
    private static readonly Dictionary<string, int> Map = new()
    {
        ["ControlLeft"] = 0xA2, ["ControlRight"] = 0xA3,
        ["AltLeft"] = 0xA4, ["AltRight"] = 0xA5,
        ["ShiftLeft"] = 0xA0, ["ShiftRight"] = 0xA1,
        ["Space"] = 0x20,
        ["CapsLock"] = 0x14,
        ["Tab"] = 0x09,
        ["Backquote"] = 0xC0,
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73,
        ["F5"] = 0x74, ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77,
        ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
        ["KeyA"] = 0x41, ["KeyB"] = 0x42, ["KeyC"] = 0x43, ["KeyD"] = 0x44,
        ["KeyE"] = 0x45, ["KeyF"] = 0x46, ["KeyG"] = 0x47, ["KeyH"] = 0x48,
        ["KeyI"] = 0x49, ["KeyJ"] = 0x4A, ["KeyK"] = 0x4B, ["KeyL"] = 0x4C,
        ["KeyM"] = 0x4D, ["KeyN"] = 0x4E, ["KeyO"] = 0x4F, ["KeyP"] = 0x50,
        ["KeyQ"] = 0x51, ["KeyR"] = 0x52, ["KeyS"] = 0x53, ["KeyT"] = 0x54,
        ["KeyU"] = 0x55, ["KeyV"] = 0x56, ["KeyW"] = 0x57, ["KeyX"] = 0x58,
        ["KeyY"] = 0x59, ["KeyZ"] = 0x5A,
        ["Digit0"] = 0x30, ["Digit1"] = 0x31, ["Digit2"] = 0x32, ["Digit3"] = 0x33,
        ["Digit4"] = 0x34, ["Digit5"] = 0x35, ["Digit6"] = 0x36, ["Digit7"] = 0x37,
        ["Digit8"] = 0x38, ["Digit9"] = 0x39,
        ["Numpad0"] = 0x60, ["Numpad1"] = 0x61, ["Numpad2"] = 0x62, ["Numpad3"] = 0x63,
        ["Numpad4"] = 0x64, ["Numpad5"] = 0x65, ["Numpad6"] = 0x66, ["Numpad7"] = 0x67,
        ["Numpad8"] = 0x68, ["Numpad9"] = 0x69,
    };

    public static int? ToVirtualKey(string domCode) => Map.TryGetValue(domCode, out var vk) ? vk : null;

    public static string Describe(string domCode) => domCode switch
    {
        "ControlLeft" => "Left Ctrl", "ControlRight" => "Right Ctrl",
        "AltLeft" => "Left Alt", "AltRight" => "Right Alt",
        "ShiftLeft" => "Left Shift", "ShiftRight" => "Right Shift",
        "Space" => "Space", "CapsLock" => "Caps Lock", "Tab" => "Tab", "Backquote" => "`",
        _ when domCode.StartsWith("Key") => domCode[3..],
        _ when domCode.StartsWith("Digit") => domCode[5..],
        _ when domCode.StartsWith("Numpad") => "Numpad " + domCode[6..],
        _ => domCode,
    };
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add AviatesAirTracker/Services/PttKeyMap.cs
git commit -m "feat(client): add DOM-code to virtual-key mapping for PTT binding"
```

---

## Task 6: Pilot client — add the SharpDX.DirectInput dependency

**Files:**
- Modify: `AviatesAirTracker/AviatesAirTracker.csproj`

- [ ] **Step 1: Add the package reference**

In `AviatesAirTracker/AviatesAirTracker.csproj`, find:

```xml
    <!-- Discord Rich Presence -->
    <PackageReference Include="DiscordRichPresence" Version="1.2.1.24" />
```

Add immediately after it:

```xml

    <!-- Joystick/HOTAS input for dispatch radio PTT binding -->
    <PackageReference Include="SharpDX.DirectInput" Version="4.2.0" />
```

- [ ] **Step 2: Restore and build to verify**

Run: `dotnet restore AviatesAirTracker/AviatesAirTracker.csproj`
Expected: `SharpDX.DirectInput` and its `SharpDX` dependency restored successfully.

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add AviatesAirTracker/AviatesAirTracker.csproj
git commit -m "chore(client): add SharpDX.DirectInput for joystick PTT binding"
```

---

## Task 7: Pilot client — PttInputService

**Files:**
- Create: `AviatesAirTracker/Services/PttInputService.cs`

- [ ] **Step 1: Implement the service**

Create `AviatesAirTracker/Services/PttInputService.cs`:

```csharp
using Serilog;
using SharpDX.DirectInput;
using System.Runtime.InteropServices;

namespace AviatesAirTracker.Services;

// ============================================================
// PTT INPUT SERVICE
//
// Two independent input sources, normalized to one PttStateChanged
// event so DispatchRadioService doesn't care which fired:
//
//  - Keyboard: a WH_KEYBOARD_LL global hook, works regardless of
//    which window has focus (MSFS will have focus during flight).
//  - Joystick/HOTAS: SharpDX.DirectInput polling loop, matches the
//    same "works without focus" requirement via a non-exclusive
//    background cooperative level.
//
// Both bindings are unbound by default (VkCode 0 / joystick GUID
// empty) — this service is inert until the pilot sets a binding in
// Settings. Gating "only forward PTT while tuned in" is the
// responsibility of DispatchRadioService, not this service — this
// class only reports raw input state.
// ============================================================

public class PttInputService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private readonly SettingsService _settings;
    private readonly LowLevelKeyboardProc _hookProc;
    private IntPtr _hookHandle = IntPtr.Zero;
    private bool _keyIsDown;

    private readonly DirectInput _directInput = new();
    private Joystick? _joystick;
    private readonly CancellationTokenSource _joystickCts = new();
    private bool _joystickButtonIsDown;
    private IntPtr _windowHandle;

    public event EventHandler<bool>? PttStateChanged;

    public PttInputService(SettingsService settings)
    {
        _settings = settings;
        _hookProc = HookCallback;
        InstallKeyboardHook();
        _ = Task.Run(() => JoystickPollLoopAsync(_joystickCts.Token));
    }

    public void SetWindowHandle(IntPtr hwnd) => _windowHandle = hwnd;

    public IReadOnlyList<(Guid InstanceGuid, string ProductName)> GetConnectedJoysticks()
    {
        return _directInput
            .GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly)
            .Select(d => (d.InstanceGuid, d.InstanceName))
            .ToList();
    }

    // Polls the given device for up to the token's lifetime, returning the
    // index of the first button pressed, or null on cancellation/timeout.
    // Caller supplies a timeout via the token (see Settings.razor).
    public async Task<int?> CaptureNextJoystickButtonAsync(Guid deviceGuid, CancellationToken token)
    {
        using var device = new Joystick(_directInput, deviceGuid);
        if (_windowHandle != IntPtr.Zero)
            device.SetCooperativeLevel(_windowHandle, CooperativeLevelFlags.NonExclusive | CooperativeLevelFlags.Background);
        device.Acquire();

        try
        {
            while (!token.IsCancellationRequested)
            {
                device.Poll();
                var state = device.GetCurrentState();
                for (int i = 0; i < state.Buttons.Length; i++)
                {
                    if (state.Buttons[i]) return i;
                }
                await Task.Delay(30, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out or cancelled — no button captured
        }
        return null;
    }

    private void InstallKeyboardHook()
    {
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc,
            GetModuleHandle(curModule?.ModuleName ?? ""), 0);
        if (_hookHandle == IntPtr.Zero)
            Log.Warning("[PttInput] Failed to install global keyboard hook");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            int boundVk = _settings.Settings.PttHotkeyVkCode;

            if (boundVk != 0 && vkCode == boundVk)
            {
                bool isDown = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
                bool isUp = wParam == WM_KEYUP || wParam == WM_SYSKEYUP;

                if (isDown && !_keyIsDown)
                {
                    _keyIsDown = true;
                    PttStateChanged?.Invoke(this, true);
                }
                else if (isUp && _keyIsDown)
                {
                    _keyIsDown = false;
                    PttStateChanged?.Invoke(this, false);
                }
            }
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private async Task JoystickPollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var guid = _settings.Settings.PttJoystickGuid;
                int buttonIndex = _settings.Settings.PttJoystickButton;

                if (guid == Guid.Empty || buttonIndex < 0)
                {
                    await Task.Delay(500, token);
                    continue;
                }

                if (_joystick == null || _joystick.Information.InstanceGuid != guid)
                {
                    _joystick?.Dispose();
                    _joystick = new Joystick(_directInput, guid);
                    if (_windowHandle != IntPtr.Zero)
                        _joystick.SetCooperativeLevel(_windowHandle, CooperativeLevelFlags.NonExclusive | CooperativeLevelFlags.Background);
                    _joystick.Acquire();
                }

                _joystick.Poll();
                var state = _joystick.GetCurrentState();
                bool isDown = buttonIndex < state.Buttons.Length && state.Buttons[buttonIndex];

                if (isDown && !_joystickButtonIsDown)
                {
                    _joystickButtonIsDown = true;
                    PttStateChanged?.Invoke(this, true);
                }
                else if (!isDown && _joystickButtonIsDown)
                {
                    _joystickButtonIsDown = false;
                    PttStateChanged?.Invoke(this, false);
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[PttInput] Joystick poll error (device may have been unplugged)");
                _joystick?.Dispose();
                _joystick = null;
            }

            await Task.Delay(30, token);
        }
    }

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero) UnhookWindowsHookEx(_hookHandle);
        _joystickCts.Cancel();
        _joystick?.Dispose();
        _directInput.Dispose();
        Log.Debug("[PttInput] Disposed");
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add AviatesAirTracker/Services/PttInputService.cs
git commit -m "feat(client): add PttInputService for keyboard hotkey and joystick PTT"
```

---

## Task 8: Pilot client — wire PttInputService into DI and the window handle

**Files:**
- Modify: `AviatesAirTracker/App.xaml.cs`
- Modify: `AviatesAirTracker/ViewModels/MainViewModel.cs`

- [ ] **Step 1: Register the singleton**

In `AviatesAirTracker/App.xaml.cs`, find:

```csharp
        services.AddSingleton<AcarsPositionService>();
        services.AddSingleton<DispatchRadioService>();
```

Replace with:

```csharp
        services.AddSingleton<AcarsPositionService>();
        services.AddSingleton<PttInputService>();
        services.AddSingleton<DispatchRadioService>();
```

- [ ] **Step 2: Dispose it on exit**

Find:

```csharp
        _serviceProvider.GetService<AcarsPositionService>()?.Dispose();
        _serviceProvider.GetService<DispatchRadioService>()?.Dispose();
```

Replace with:

```csharp
        _serviceProvider.GetService<AcarsPositionService>()?.Dispose();
        _serviceProvider.GetService<PttInputService>()?.Dispose();
        _serviceProvider.GetService<DispatchRadioService>()?.Dispose();
```

- [ ] **Step 3: Forward the window handle from MainViewModel**

In `AviatesAirTracker/ViewModels/MainViewModel.cs`, find:

```csharp
    private readonly SimConnectManager _simConnect;
```

Replace with:

```csharp
    private readonly SimConnectManager _simConnect;
    private readonly PttInputService _pttInput;
```

Find the constructor signature:

```csharp
    public MainViewModel(
        SimConnectManager simConnect, FlightSessionManager session,
        AlertService alertService, SettingsService settings,
        TelemetryProcessor telemetryProcessor, SimBriefService simBriefSvc,
        ExportService exportSvc,
        DashboardViewModel dashboard, LiveFlightViewModel liveFlight,
        MapViewModel map, LandingAnalysisViewModel landingAnalysis,
        StatisticsViewModel statistics, SettingsViewModel settingsVm,
        ReplayViewModel replay, TelemetryViewModel telemetry)
    {
        _simConnect = simConnect; _session = session;
```

Replace with:

```csharp
    public MainViewModel(
        SimConnectManager simConnect, FlightSessionManager session,
        AlertService alertService, SettingsService settings,
        TelemetryProcessor telemetryProcessor, SimBriefService simBriefSvc,
        ExportService exportSvc, PttInputService pttInput,
        DashboardViewModel dashboard, LiveFlightViewModel liveFlight,
        MapViewModel map, LandingAnalysisViewModel landingAnalysis,
        StatisticsViewModel statistics, SettingsViewModel settingsVm,
        ReplayViewModel replay, TelemetryViewModel telemetry)
    {
        _simConnect = simConnect; _session = session; _pttInput = pttInput;
```

Find:

```csharp
    public void SetWindowHandle(IntPtr hwnd) => _simConnect.SetWindowHandle(hwnd);
```

Replace with:

```csharp
    public void SetWindowHandle(IntPtr hwnd)
    {
        _simConnect.SetWindowHandle(hwnd);
        _pttInput.SetWindowHandle(hwnd);
    }
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
git add AviatesAirTracker/App.xaml.cs AviatesAirTracker/ViewModels/MainViewModel.cs
git commit -m "feat(client): wire PttInputService into DI and the window handle"
```

---

## Task 9: Pilot client — Settings.razor PTT binding UI

**Files:**
- Modify: `AviatesAirTracker/Pages/Settings.razor`

- [ ] **Step 1: Inject PttInputService**

Find:

```razor
@inject SettingsService SettingsService
@inject AviatesBackendClient BackendClient
@inject UpdateService UpdateService
@inject IJSRuntime JS
```

Replace with:

```razor
@inject SettingsService SettingsService
@inject AviatesBackendClient BackendClient
@inject UpdateService UpdateService
@inject PttInputService PttInput
@inject IJSRuntime JS
```

- [ ] **Step 2: Add the binding fields to the Dispatch Radio panel**

Find:

```razor
                <div style="background:var(--surface-2);border-radius:var(--r-md);padding:10px 13px;font-size:12px;color:var(--text-muted);line-height:1.5;">
                    Tune COM1 to <strong>123.575 MHz</strong> in MSFS to connect to dispatch. Leave this blank if you don't have a dispatch backend URL yet.
                </div>
            </div>
        </div>
```

Replace with:

```razor
                <div style="background:var(--surface-2);border-radius:var(--r-md);padding:10px 13px;font-size:12px;color:var(--text-muted);line-height:1.5;">
                    Tune COM1 to <strong>123.575 MHz</strong> in MSFS to connect to dispatch. Leave this blank if you don't have a dispatch backend URL yet.
                </div>
                <div class="form-group">
                    <label class="form-label">PTT Hotkey</label>
                    <div style="display:flex;gap:8px;align-items:center;">
                        <input class="form-input" style="flex:1;" readonly
                               value="@(_capturingHotkey ? "Press a key…" : PttHotkeyLabel)"
                               @onkeydown="OnCaptureHotkey" @onkeydown:preventDefault="true"
                               @onfocus="() => _capturingHotkey = true"
                               @onblur="() => _capturingHotkey = false" />
                        <button class="btn btn-secondary" type="button" @onclick="ClearHotkey">Clear</button>
                    </div>
                    <div style="font-size:12px;color:var(--text-muted);margin-top:4px;">Click the field, then press the key you want to hold to talk. Works even while MSFS has focus.</div>
                </div>
                <div class="form-group">
                    <label class="form-label">PTT Joystick Button</label>
                    <div style="display:flex;gap:8px;align-items:center;">
                        <select class="form-input" style="flex:1;" @bind="_selectedJoystickGuid">
                            <option value="">-- No joystick --</option>
                            @foreach (var device in _joysticks)
                            {
                                <option value="@device.InstanceGuid">@device.ProductName</option>
                            }
                        </select>
                        <button class="btn btn-secondary" type="button" @onclick="BindJoystickButtonAsync"
                                disabled="@(string.IsNullOrEmpty(_selectedJoystickGuid) || _capturingJoystick)">
                            @(_capturingJoystick ? "Press a button…" : "Bind Button")
                        </button>
                    </div>
                    @if (SettingsService.Settings.PttJoystickButton >= 0)
                    {
                        <div style="font-size:12px;color:var(--text-muted);margin-top:4px;">Bound: button @(SettingsService.Settings.PttJoystickButton + 1)</div>
                    }
                </div>
            </div>
        </div>
```

- [ ] **Step 3: Add the code-behind**

Find:

```csharp
    protected override void OnInitialized()
    {
        UpdateNameDisplay();
```

Replace with:

```csharp
    private bool _capturingHotkey;
    private bool _capturingJoystick;
    private string _selectedJoystickGuid = "";
    private IReadOnlyList<(Guid InstanceGuid, string ProductName)> _joysticks = Array.Empty<(Guid, string)>();

    private string PttHotkeyLabel =>
        SettingsService.Settings.PttHotkeyVkCode == 0
            ? "Unbound — click and press a key"
            : SettingsService.Settings.PttHotkeyLabel;

    private void OnCaptureHotkey(KeyboardEventArgs e)
    {
        var vk = PttKeyMap.ToVirtualKey(e.Code);
        if (vk is null) return;

        SettingsService.Settings.PttHotkeyVkCode = vk.Value;
        SettingsService.Settings.PttHotkeyLabel = PttKeyMap.Describe(e.Code);
        SettingsService.Save();
        _capturingHotkey = false;
    }

    private void ClearHotkey()
    {
        SettingsService.Settings.PttHotkeyVkCode = 0;
        SettingsService.Settings.PttHotkeyLabel = "";
        SettingsService.Save();
    }

    private async Task BindJoystickButtonAsync()
    {
        if (string.IsNullOrEmpty(_selectedJoystickGuid) || !Guid.TryParse(_selectedJoystickGuid, out var guid))
            return;

        _capturingJoystick = true;
        StateHasChanged();

        using var captureCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var button = await PttInput.CaptureNextJoystickButtonAsync(guid, captureCts.Token);

        if (button is not null)
        {
            SettingsService.Settings.PttJoystickGuid = guid;
            SettingsService.Settings.PttJoystickButton = button.Value;
            SettingsService.Save();
        }

        _capturingJoystick = false;
        await InvokeAsync(StateHasChanged);
    }

    protected override void OnInitialized()
    {
        UpdateNameDisplay();

        _joysticks = PttInput.GetConnectedJoysticks();
        if (SettingsService.Settings.PttJoystickGuid != Guid.Empty)
            _selectedJoystickGuid = SettingsService.Settings.PttJoystickGuid.ToString();
```

Note: this leaves the rest of the original `OnInitialized()` body (the `AcarsKey`/`PilotName` checks, version resolution, `UpdateService.DownloadProgressChanged` subscription) unchanged, immediately following what's shown above.

- [ ] **Step 4: Build to verify**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
git add AviatesAirTracker/Pages/Settings.razor
git commit -m "feat(client): add PTT hotkey and joystick binding UI to Settings"
```

---

## Task 10: Pilot client — voice channel events on DispatchRadioService

**Files:**
- Modify: `AviatesAirTracker/Services/DispatchRadioService.cs`

- [ ] **Step 1: Add the new events and the PttInputService dependency**

Find:

```csharp
    private readonly FlightSessionManager _session;
    private readonly SettingsService _settings;
```

Replace with:

```csharp
    private readonly FlightSessionManager _session;
    private readonly SettingsService _settings;
    private readonly PttInputService _pttInput;
```

Find:

```csharp
    public DispatchCommsState CommsState { get; private set; } = DispatchCommsState.Untuned;
    public double? DistanceNm { get; private set; }

    public event EventHandler<DispatchCommsState>? CommsStateChanged;

    public DispatchRadioService(FlightSessionManager session, SettingsService settings)
    {
        _session = session;
        _settings = settings;
        _session.TelemetryUpdated += OnTelemetryUpdated;
        Log.Information("[DispatchRadio] Service initialized, VHF={Freq}", VhfFrequencyMhz);
    }
```

Replace with:

```csharp
    public DispatchCommsState CommsState { get; private set; } = DispatchCommsState.Untuned;
    public double? DistanceNm { get; private set; }

    public event EventHandler<DispatchCommsState>? CommsStateChanged;
    public event EventHandler<double>? DistanceUpdated;
    public event EventHandler<VoiceChannelInfo>? VoiceChannelConnectRequested;
    public event EventHandler? VoiceChannelDisconnectRequested;
    public event EventHandler? PttPressed;
    public event EventHandler? PttReleased;

    public DispatchRadioService(FlightSessionManager session, SettingsService settings, PttInputService pttInput)
    {
        _session = session;
        _settings = settings;
        _pttInput = pttInput;
        _session.TelemetryUpdated += OnTelemetryUpdated;
        _pttInput.PttStateChanged += OnPttStateChanged;
        Log.Information("[DispatchRadio] Service initialized, VHF={Freq}", VhfFrequencyMhz);
    }

    // Only forward PTT while actually tuned in — you can't transmit on a
    // frequency you're not tuned to.
    private void OnPttStateChanged(object? sender, bool pressed)
    {
        if (!_tuned) return;
        if (pressed) PttPressed?.Invoke(this, EventArgs.Empty);
        else PttReleased?.Invoke(this, EventArgs.Empty);
    }
```

- [ ] **Step 2: Raise the voice channel connect/disconnect events on tune/untune**

Find:

```csharp
        if (isTuned && !_tuned)
        {
            _tuned = true;
            Interlocked.Increment(ref _connectionGeneration);
            var cts = new CancellationTokenSource();
            _cts = cts;
            _connectionLoopTask = Task.Run(() => MaintainConnectionAsync(cts.Token));
        }
        else if (!isTuned && _tuned)
        {
            _tuned = false;
            long myGeneration = Interlocked.Read(ref _connectionGeneration);
            _ = DisconnectAsync(myGeneration);
        }
```

Replace with:

```csharp
        if (isTuned && !_tuned)
        {
            _tuned = true;
            Interlocked.Increment(ref _connectionGeneration);
            var cts = new CancellationTokenSource();
            _cts = cts;
            _connectionLoopTask = Task.Run(() => MaintainConnectionAsync(cts.Token));
            VoiceChannelConnectRequested?.Invoke(this, new VoiceChannelInfo(_settings.Settings.PilotId, _settings.Settings.DispatchBackendUrl));
        }
        else if (!isTuned && _tuned)
        {
            _tuned = false;
            long myGeneration = Interlocked.Read(ref _connectionGeneration);
            _ = DisconnectAsync(myGeneration);
            VoiceChannelDisconnectRequested?.Invoke(this, EventArgs.Empty);
        }
```

- [ ] **Step 3: Raise DistanceUpdated whenever a new state message arrives**

Find:

```csharp
            DistanceNm = msg.DistanceNm;
            CommsStateChanged?.Invoke(this, CommsState);
```

Replace with:

```csharp
            DistanceNm = msg.DistanceNm;
            CommsStateChanged?.Invoke(this, CommsState);
            DistanceUpdated?.Invoke(this, msg.DistanceNm);
```

- [ ] **Step 4: Add the VoiceChannelInfo record and unsubscribe on Dispose**

Find:

```csharp
    public void Dispose()
    {
        _session.TelemetryUpdated -= OnTelemetryUpdated;
        _cts?.Cancel();
        _socket?.Dispose();
        Log.Debug("[DispatchRadio] Disposed");
    }
}
```

Replace with:

```csharp
    public void Dispose()
    {
        _session.TelemetryUpdated -= OnTelemetryUpdated;
        _pttInput.PttStateChanged -= OnPttStateChanged;
        _cts?.Cancel();
        _socket?.Dispose();
        Log.Debug("[DispatchRadio] Disposed");
    }
}

public record VoiceChannelInfo(string PilotId, string BackendUrl);
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: 0 errors

- [ ] **Step 6: Commit**

```bash
git add AviatesAirTracker/Services/DispatchRadioService.cs
git commit -m "feat(client): add voice channel lifecycle and PTT events to DispatchRadioService"
```

---

## Task 11: Pilot client — `wwwroot/js/voice.js`

**Files:**
- Create: `AviatesAirTracker/wwwroot/js/voice.js`
- Modify: `AviatesAirTracker/wwwroot/index.html`

- [ ] **Step 1: Implement the JS module**

Create `AviatesAirTracker/wwwroot/js/voice.js`:

```javascript
// ============================================================
// AVIATES VOICE — dispatch radio push-to-talk audio
//
// Owns its own WebSocket to the /voice route on the same
// Cloudflare Worker used for the text dispatch protocol. Chunks
// are captured via MediaRecorder (audio/webm;codecs=opus) and
// prefixed with a 1-byte-length sender-id envelope so a listener
// receiving overlapping transmissions can demultiplex them into
// separate playback buffers. See
// docs/superpowers/specs/2026-07-14-dispatch-radio-phase2-voice-design.md.
//
// Each transmission is buffered in full (not streamed via
// MediaSource) and played back as a single decoded clip once the
// sender's voice_end marker arrives (or after a 2s chunk-silence
// timeout) — simpler and more robust than segment-by-segment
// streaming for short PTT bursts, at the cost of a small added
// latency most listeners won't notice.
// ============================================================

(function () {
  const CLEAR_NM = 200;
  const DEGRADED_NM = 400;
  const NOISE_GAIN_CEILING = 0.35;
  const SESSION_TIMEOUT_MS = 2000;

  const RECONNECT_DELAY_MS = 5000;

  let ws = null;
  let myPilotId = null;
  let myBackendUrl = null;
  let shouldStayConnected = false;
  let reconnectTimer = null;
  let currentDistanceNm = 0;
  let audioCtx = null;
  let mediaStream = null;
  let mediaRecorder = null;

  /** senderId -> { chunks: ArrayBuffer[], timeoutHandle: number|null } */
  const activeSessions = new Map();

  function ensureAudioContext() {
    if (!audioCtx) audioCtx = new AudioContext();
    return audioCtx;
  }

  function encodeVoiceFrame(senderId, chunkArrayBuffer) {
    const idBytes = new TextEncoder().encode(senderId);
    const out = new Uint8Array(1 + idBytes.length + chunkArrayBuffer.byteLength);
    out[0] = idBytes.length;
    out.set(idBytes, 1);
    out.set(new Uint8Array(chunkArrayBuffer), 1 + idBytes.length);
    return out.buffer;
  }

  function decodeVoiceFrame(arrayBuffer) {
    const bytes = new Uint8Array(arrayBuffer);
    const idLen = bytes[0];
    const senderId = new TextDecoder().decode(bytes.subarray(1, 1 + idLen));
    const chunk = bytes.subarray(1 + idLen).slice().buffer;
    return { senderId, chunk };
  }

  function computeDegradationFactor(distanceNm) {
    if (distanceNm <= CLEAR_NM) return 0;
    if (distanceNm >= DEGRADED_NM) return 1;
    return (distanceNm - CLEAR_NM) / (DEGRADED_NM - CLEAR_NM);
  }

  function getStaticNoiseBuffer(ctx) {
    if (ctx._aviatesNoiseBuffer) return ctx._aviatesNoiseBuffer;
    const length = ctx.sampleRate * 2;
    const buffer = ctx.createBuffer(1, length, ctx.sampleRate);
    const data = buffer.getChannelData(0);
    let last = 0;
    for (let i = 0; i < length; i++) {
      const white = Math.random() * 2 - 1;
      last = (last + 0.15 * white) / 1.15;
      data[i] = last * 3.5;
    }
    ctx._aviatesNoiseBuffer = buffer;
    return buffer;
  }

  function scheduleDropouts(ctx, gainNode, degradationFactor, durationSec) {
    if (degradationFactor <= 0) return;
    const avgIntervalSec = 20 - (17 * degradationFactor);
    let t = 0;
    while (t < durationSec) {
      t += avgIntervalSec * (0.5 + Math.random());
      if (t >= durationSec) break;
      const dipStart = ctx.currentTime + t;
      gainNode.gain.setValueAtTime(1, dipStart);
      gainNode.gain.linearRampToValueAtTime(0.15, dipStart + 0.05);
      gainNode.gain.linearRampToValueAtTime(1, dipStart + 0.15);
    }
  }

  function playDegradedClip(ctx, audioBuffer, degradationFactor) {
    const source = ctx.createBufferSource();
    source.buffer = audioBuffer;

    const bandpass = ctx.createBiquadFilter();
    bandpass.type = 'bandpass';
    bandpass.frequency.value = 1650;
    bandpass.Q.value = 0.7;

    const voiceGain = ctx.createGain();
    voiceGain.gain.value = 1.0;

    const merger = ctx.createGain();

    source.connect(bandpass);
    bandpass.connect(voiceGain);
    voiceGain.connect(merger);

    if (degradationFactor > 0) {
      const noiseGain = ctx.createGain();
      noiseGain.gain.value = degradationFactor * NOISE_GAIN_CEILING;
      const noiseSource = ctx.createBufferSource();
      noiseSource.buffer = getStaticNoiseBuffer(ctx);
      noiseSource.loop = true;
      noiseSource.connect(noiseGain);
      noiseGain.connect(merger);
      noiseSource.start();
      noiseSource.stop(ctx.currentTime + audioBuffer.duration);
    }

    merger.connect(ctx.destination);
    scheduleDropouts(ctx, merger, degradationFactor, audioBuffer.duration);

    source.start();
    source.stop(ctx.currentTime + audioBuffer.duration);
  }

  async function playSession(senderId) {
    const session = activeSessions.get(senderId);
    if (!session || session.chunks.length === 0) return;
    activeSessions.delete(senderId);

    try {
      const blob = new Blob(session.chunks, { type: 'audio/webm;codecs=opus' });
      const arrayBuffer = await blob.arrayBuffer();
      const ctx = ensureAudioContext();
      const audioBuffer = await ctx.decodeAudioData(arrayBuffer);
      playDegradedClip(ctx, audioBuffer, computeDegradationFactor(currentDistanceNm));
    } catch (err) {
      console.error('[aviatesVoice] Failed to decode/play clip from', senderId, err);
    }
  }

  function getOrCreateSession(senderId) {
    let session = activeSessions.get(senderId);
    if (!session) {
      session = { chunks: [], timeoutHandle: null };
      activeSessions.set(senderId, session);
    }
    if (session.timeoutHandle) clearTimeout(session.timeoutHandle);
    session.timeoutHandle = setTimeout(() => playSession(senderId), SESSION_TIMEOUT_MS);
    return session;
  }

  function handleIncomingBinary(arrayBuffer) {
    const { senderId, chunk } = decodeVoiceFrame(arrayBuffer);
    const session = getOrCreateSession(senderId);
    session.chunks.push(chunk);
  }

  function handleIncomingMarker(json) {
    let msg;
    try {
      msg = JSON.parse(json);
    } catch {
      return;
    }
    if (msg.type === 'voice_end' && msg.from) {
      const session = activeSessions.get(msg.from);
      if (session) {
        if (session.timeoutHandle) clearTimeout(session.timeoutHandle);
        playSession(msg.from);
      }
    }
    // voice_start needs no action here — getOrCreateSession() lazily starts
    // a fresh buffer for that sender on the first chunk that follows.
  }

  // Mirrors DispatchRadioService's own 5s reconnect loop for the /pilot
  // text socket — a dropped /voice socket must recover on its own while
  // still tuned in, not wait for the next full tune/untune cycle.
  function openSocket() {
    if (!shouldStayConnected) return;
    ws = new WebSocket(`${myBackendUrl}/voice?pilotId=${encodeURIComponent(myPilotId)}`);
    ws.binaryType = 'arraybuffer';
    ws.onmessage = (event) => {
      if (event.data instanceof ArrayBuffer) handleIncomingBinary(event.data);
      else handleIncomingMarker(event.data);
    };
    ws.onclose = () => {
      ws = null;
      if (shouldStayConnected) {
        reconnectTimer = setTimeout(openSocket, RECONNECT_DELAY_MS);
      }
    };
    ws.onerror = () => { /* onclose always follows onerror for a WebSocket */ };
  }

  window.aviatesVoice = {
    connect(pilotId, backendUrl) {
      if (shouldStayConnected) return;
      myPilotId = pilotId;
      myBackendUrl = backendUrl;
      shouldStayConnected = true;
      openSocket();
    },

    disconnect() {
      shouldStayConnected = false;
      if (reconnectTimer) { clearTimeout(reconnectTimer); reconnectTimer = null; }
      if (mediaRecorder) this.stopTransmit();
      if (ws) { ws.close(); ws = null; }
      activeSessions.forEach((s) => { if (s.timeoutHandle) clearTimeout(s.timeoutHandle); });
      activeSessions.clear();
    },

    updateDistance(distanceNm) {
      currentDistanceNm = distanceNm;
    },

    async startTransmit() {
      if (!ws || ws.readyState !== WebSocket.OPEN || mediaRecorder) return;
      try {
        mediaStream = await navigator.mediaDevices.getUserMedia({ audio: true });
      } catch (err) {
        console.error('[aviatesVoice] Microphone access denied', err);
        return;
      }
      mediaRecorder = new MediaRecorder(mediaStream, { mimeType: 'audio/webm;codecs=opus' });
      mediaRecorder.ondataavailable = async (event) => {
        if (event.data.size === 0 || !ws || ws.readyState !== WebSocket.OPEN) return;
        const arrayBuffer = await event.data.arrayBuffer();
        ws.send(encodeVoiceFrame(myPilotId, arrayBuffer));
      };
      ws.send(JSON.stringify({ type: 'voice_start', from: myPilotId }));
      mediaRecorder.start(100);
    },

    stopTransmit() {
      if (mediaRecorder) {
        mediaRecorder.stop();
        mediaRecorder = null;
      }
      if (mediaStream) {
        mediaStream.getTracks().forEach((t) => t.stop());
        mediaStream = null;
      }
      if (ws && ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify({ type: 'voice_end', from: myPilotId }));
      }
    },
  };
})();
```

- [ ] **Step 2: Include the script**

In `AviatesAirTracker/wwwroot/index.html`, find:

```html
    <script src="js/interop.js"></script>
```

Replace with:

```html
    <script src="js/interop.js"></script>
    <script src="js/voice.js"></script>
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: 0 errors (this file isn't compiled, but the build copies `wwwroot/` — confirms no path typo breaks the build).

- [ ] **Step 4: Commit**

```bash
git add AviatesAirTracker/wwwroot/js/voice.js AviatesAirTracker/wwwroot/index.html
git commit -m "feat(client): add voice.js — capture, relay transport, and degraded playback"
```

---

## Task 12: Pilot client — WebView2 microphone permission

**Files:**
- Modify: `AviatesAirTracker/MainWindow.xaml`
- Modify: `AviatesAirTracker/MainWindow.xaml.cs`

- [ ] **Step 1: Name the BlazorWebView control**

In `AviatesAirTracker/MainWindow.xaml`, find:

```xml
        <blazor:BlazorWebView Grid.Row="1"
                              HostPage="wwwroot\index.html"
                              Services="{DynamicResource services}">
```

Replace with:

```xml
        <blazor:BlazorWebView Grid.Row="1"
                              x:Name="BlazorHost"
                              HostPage="wwwroot\index.html"
                              Services="{DynamicResource services}">
```

- [ ] **Step 2: Auto-approve microphone permission requests**

In `AviatesAirTracker/MainWindow.xaml.cs`, find:

```csharp
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _viewModel.SetWindowHandle(hwnd);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }
```

Replace with:

```csharp
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _viewModel.SetWindowHandle(hwnd);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

        // Voice PTT needs getUserMedia() inside the Blazor page's WebView2
        // context. WebView2 blocks microphone access by default; without
        // this handler, getUserMedia() would hang waiting on a permission
        // prompt the user has no way to see or answer.
        await BlazorHost.EnsureCoreWebView2Async();
        BlazorHost.WebView.CoreWebView2.PermissionRequested += OnPermissionRequested;
    }

    private void OnPermissionRequested(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2PermissionRequestedEventArgs e)
    {
        if (e.PermissionKind == Microsoft.Web.WebView2.Core.CoreWebView2PermissionKind.Microphone)
        {
            e.State = Microsoft.Web.WebView2.Core.CoreWebView2PermissionState.Allow;
        }
    }
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: 0 errors. `Microsoft.Web.WebView2.Core` types come transitively via the already-referenced `Microsoft.AspNetCore.Components.WebView.Wpf` package — if the build reports the namespace unresolved, add `<PackageReference Include="Microsoft.Web.WebView2" Version="1.0.2210.55" />` next to the existing WebView package reference in the `.csproj` and rebuild.

- [ ] **Step 4: Commit**

```bash
git add AviatesAirTracker/MainWindow.xaml AviatesAirTracker/MainWindow.xaml.cs
git commit -m "feat(client): auto-approve WebView2 microphone permission for voice PTT"
```

---

## Task 13: Pilot client — wire DispatchRadioService voice events to JS interop

**Files:**
- Modify: `AviatesAirTracker/Shared/MainLayout.razor`

- [ ] **Step 1: Inject DispatchRadioService**

Find:

```razor
@inject AviatesAirTracker.Core.SimConnect.SimConnectManager SimConnectMgr
@inject IJSRuntime JS
```

Replace with:

```razor
@inject AviatesAirTracker.Core.SimConnect.SimConnectManager SimConnectMgr
@inject DispatchRadioService DispatchRadio
@inject IJSRuntime JS
```

- [ ] **Step 2: Subscribe in OnInitialized**

Find:

```csharp
        SimConnectMgr.ConnectionStatusChanged += OnSimConnectionChanged;
        _connStatus = SimConnectMgr.ConnectionStatus;
        RefreshHeader();
```

Replace with:

```csharp
        SimConnectMgr.ConnectionStatusChanged += OnSimConnectionChanged;
        DispatchRadio.VoiceChannelConnectRequested += OnVoiceChannelConnectRequested;
        DispatchRadio.VoiceChannelDisconnectRequested += OnVoiceChannelDisconnectRequested;
        DispatchRadio.DistanceUpdated += OnVoiceDistanceUpdated;
        DispatchRadio.PttPressed += OnPttPressed;
        DispatchRadio.PttReleased += OnPttReleased;
        _connStatus = SimConnectMgr.ConnectionStatus;
        RefreshHeader();
```

- [ ] **Step 3: Add the handlers**

Find:

```csharp
    private void OnSimConnectionChanged(object? sender, SimConnectionStatus status)
    {
        _ = InvokeAsync(() => { _connStatus = status; StateHasChanged(); });
    }
```

Add immediately after it:

```csharp

    private void OnVoiceChannelConnectRequested(object? sender, VoiceChannelInfo info)
    {
        _ = InvokeAsync(async () =>
        {
            try { await JS.InvokeVoidAsync("aviatesVoice.connect", info.PilotId, info.BackendUrl); }
            catch (Exception ex) { Log.Warning(ex, "[DispatchRadio] Failed to connect voice channel"); }
        });
    }

    private void OnVoiceChannelDisconnectRequested(object? sender, EventArgs e)
    {
        _ = InvokeAsync(async () =>
        {
            try { await JS.InvokeVoidAsync("aviatesVoice.disconnect"); }
            catch (Exception ex) { Log.Warning(ex, "[DispatchRadio] Failed to disconnect voice channel"); }
        });
    }

    private void OnVoiceDistanceUpdated(object? sender, double distanceNm)
    {
        _ = InvokeAsync(async () =>
        {
            try { await JS.InvokeVoidAsync("aviatesVoice.updateDistance", distanceNm); }
            catch (Exception ex) { Log.Debug(ex, "[DispatchRadio] Failed to update voice distance"); }
        });
    }

    private void OnPttPressed(object? sender, EventArgs e)
    {
        _ = InvokeAsync(async () =>
        {
            try { await JS.InvokeVoidAsync("aviatesVoice.startTransmit"); }
            catch (Exception ex) { Log.Warning(ex, "[DispatchRadio] Failed to start voice transmit"); }
        });
    }

    private void OnPttReleased(object? sender, EventArgs e)
    {
        _ = InvokeAsync(async () =>
        {
            try { await JS.InvokeVoidAsync("aviatesVoice.stopTransmit"); }
            catch (Exception ex) { Log.Warning(ex, "[DispatchRadio] Failed to stop voice transmit"); }
        });
    }
```

- [ ] **Step 4: Unsubscribe in DisposeAsync**

Find:

```csharp
        SimConnectMgr.ConnectionStatusChanged -= OnSimConnectionChanged;
        MessagingService.UnreadCountChanged   -= OnUnreadCountChanged;
```

Replace with:

```csharp
        SimConnectMgr.ConnectionStatusChanged -= OnSimConnectionChanged;
        DispatchRadio.VoiceChannelConnectRequested -= OnVoiceChannelConnectRequested;
        DispatchRadio.VoiceChannelDisconnectRequested -= OnVoiceChannelDisconnectRequested;
        DispatchRadio.DistanceUpdated -= OnVoiceDistanceUpdated;
        DispatchRadio.PttPressed -= OnPttPressed;
        DispatchRadio.PttReleased -= OnPttReleased;
        MessagingService.UnreadCountChanged   -= OnUnreadCountChanged;
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: 0 errors

- [ ] **Step 6: Commit**

```bash
git add AviatesAirTracker/Shared/MainLayout.razor
git commit -m "feat(client): wire DispatchRadioService voice events to JS interop in MainLayout"
```

---

## Task 14: Dispatcher console — voice PTT, playback, and speaking indicator

**Files:**
- Modify: `AviatesAirWebsite/src/app/admin/dispatch/page.tsx`

- [ ] **Step 1: Replace the file with the full updated version**

Replace the entire contents of `AviatesAirWebsite/src/app/admin/dispatch/page.tsx` with:

```tsx
'use client';

import { useEffect, useRef, useState } from 'react';
import { useAdminAuth } from '@/context/AdminAuthContext';
import { DISPATCH_ADMIN_WS_URL, DISPATCH_ADMIN_WS_KEY } from '@/lib/constants';

interface DispatchPilot {
  pilotId: string;
  callsign: string;
  distanceNm: number;
  state: 'clear' | 'degraded' | 'acars_only';
}

const STATE_LABELS: Record<DispatchPilot['state'], string> = {
  clear: 'Clear',
  degraded: 'Degraded',
  acars_only: 'ACARS Only',
};

const STATE_STYLES: Record<DispatchPilot['state'], string> = {
  clear: 'bg-green-100 text-green-800',
  degraded: 'bg-amber-100 text-amber-800',
  acars_only: 'bg-red-100 text-red-800',
};

// Guards against malformed records from the WS feed reaching render — a missing/
// non-numeric distanceNm or an unrecognized state would otherwise throw during
// render (toFixed on non-number, undefined STATE_LABELS/STATE_STYLES lookup) with
// no error boundary in this app to catch it.
function isValidPilot(p: unknown): p is DispatchPilot {
  if (typeof p !== 'object' || p === null) return false;
  const candidate = p as Record<string, unknown>;
  return (
    typeof candidate.pilotId === 'string' &&
    typeof candidate.callsign === 'string' &&
    typeof candidate.distanceNm === 'number' &&
    Number.isFinite(candidate.distanceNm) &&
    typeof candidate.state === 'string' &&
    candidate.state in STATE_LABELS
  );
}

function voiceWsUrl(): string {
  return DISPATCH_ADMIN_WS_URL.replace('/admin', '/voice');
}

// Binary voice frames carry a 1-byte-length sender-id prefix so a listener
// can demultiplex overlapping transmitters into separate playback buffers —
// same envelope format used by the pilot client's voice.js. The Durable
// Object relay never inspects this; it's opaque payload to the server.
function encodeVoiceFrame(senderId: string, chunk: ArrayBuffer): ArrayBuffer {
  const idBytes = new TextEncoder().encode(senderId);
  const out = new Uint8Array(1 + idBytes.length + chunk.byteLength);
  out[0] = idBytes.length;
  out.set(idBytes, 1);
  out.set(new Uint8Array(chunk), 1 + idBytes.length);
  return out.buffer;
}

function decodeVoiceFrame(buf: ArrayBuffer): { senderId: string; chunk: ArrayBuffer } {
  const bytes = new Uint8Array(buf);
  const idLen = bytes[0];
  const senderId = new TextDecoder().decode(bytes.subarray(1, 1 + idLen));
  const chunk = bytes.subarray(1 + idLen).slice().buffer;
  return { senderId, chunk };
}

// The dispatcher sits at the station (0nm from itself), so incoming audio
// always plays with no distance-based degradation — only the constant radio
// band-pass applies, matching how a pilot at "clear" range would sound too.
function playClip(ctx: AudioContext, audioBuffer: AudioBuffer) {
  const source = ctx.createBufferSource();
  source.buffer = audioBuffer;
  const bandpass = ctx.createBiquadFilter();
  bandpass.type = 'bandpass';
  bandpass.frequency.value = 1650;
  bandpass.Q.value = 0.7;
  source.connect(bandpass);
  bandpass.connect(ctx.destination);
  source.start();
  source.stop(ctx.currentTime + audioBuffer.duration);
}

interface VoiceSession {
  chunks: ArrayBuffer[];
  timeoutHandle: ReturnType<typeof setTimeout> | null;
}

const SESSION_TIMEOUT_MS = 2000;

export default function DispatchRadioPage() {
  const { token } = useAdminAuth();
  const [pilots, setPilots] = useState<DispatchPilot[]>([]);
  const [connected, setConnected] = useState(false);
  const [hasError, setHasError] = useState(false);
  const [remoteTransmitting, setRemoteTransmitting] = useState<string | null>(null);
  const [isTransmitting, setIsTransmitting] = useState(false);

  const voiceWsRef = useRef<WebSocket | null>(null);
  const audioCtxRef = useRef<AudioContext | null>(null);
  const mediaStreamRef = useRef<MediaStream | null>(null);
  const mediaRecorderRef = useRef<MediaRecorder | null>(null);
  const sessionsRef = useRef<Map<string, VoiceSession>>(new Map());

  useEffect(() => {
    if (!token || !DISPATCH_ADMIN_WS_URL) return;

    const ws = new WebSocket(`${DISPATCH_ADMIN_WS_URL}?key=${encodeURIComponent(DISPATCH_ADMIN_WS_KEY)}`);
    ws.onopen = () => {
      setConnected(true);
      setHasError(false);
    };
    ws.onclose = () => {
      setConnected(false);
      setPilots([]);
    };
    ws.onerror = () => {
      setConnected(false);
      setHasError(true);
      setPilots([]);
    };
    ws.onmessage = (event) => {
      try {
        const data = JSON.parse(event.data);
        if (Array.isArray(data.pilots)) setPilots(data.pilots.filter(isValidPilot));
      } catch (err) {
        console.error('Failed to parse dispatch WS frame:', err);
        return;
      }
    };

    return () => ws.close();
  }, [token]);

  useEffect(() => {
    if (!token || !DISPATCH_ADMIN_WS_URL) return;

    const ws = new WebSocket(`${voiceWsUrl()}?key=${encodeURIComponent(DISPATCH_ADMIN_WS_KEY)}`);
    ws.binaryType = 'arraybuffer';
    voiceWsRef.current = ws;

    const playSession = async (senderId: string) => {
      const session = sessionsRef.current.get(senderId);
      if (!session || session.chunks.length === 0) return;
      sessionsRef.current.delete(senderId);
      try {
        const blob = new Blob(session.chunks, { type: 'audio/webm;codecs=opus' });
        const arrayBuffer = await blob.arrayBuffer();
        if (!audioCtxRef.current) audioCtxRef.current = new AudioContext();
        const audioBuffer = await audioCtxRef.current.decodeAudioData(arrayBuffer);
        playClip(audioCtxRef.current, audioBuffer);
      } catch (err) {
        console.error('[voice] Failed to decode/play clip from', senderId, err);
      }
    };

    ws.onmessage = (event) => {
      if (event.data instanceof ArrayBuffer) {
        const { senderId, chunk } = decodeVoiceFrame(event.data);
        let session = sessionsRef.current.get(senderId);
        if (!session) {
          session = { chunks: [], timeoutHandle: null };
          sessionsRef.current.set(senderId, session);
        }
        if (session.timeoutHandle) clearTimeout(session.timeoutHandle);
        session.timeoutHandle = setTimeout(() => playSession(senderId), SESSION_TIMEOUT_MS);
        session.chunks.push(chunk);
      } else {
        try {
          const msg = JSON.parse(event.data);
          if (msg.type === 'voice_start' && msg.from) setRemoteTransmitting(msg.from);
          if (msg.type === 'voice_end' && msg.from) {
            setRemoteTransmitting((current) => (current === msg.from ? null : current));
            const session = sessionsRef.current.get(msg.from);
            if (session) {
              if (session.timeoutHandle) clearTimeout(session.timeoutHandle);
              playSession(msg.from);
            }
          }
        } catch {
          // ignore malformed marker frames
        }
      }
    };

    return () => {
      ws.close();
      voiceWsRef.current = null;
      sessionsRef.current.forEach((s) => { if (s.timeoutHandle) clearTimeout(s.timeoutHandle); });
      sessionsRef.current.clear();
    };
  }, [token]);

  const startTransmit = async () => {
    const ws = voiceWsRef.current;
    if (!ws || ws.readyState !== WebSocket.OPEN || mediaRecorderRef.current) return;
    try {
      mediaStreamRef.current = await navigator.mediaDevices.getUserMedia({ audio: true });
    } catch (err) {
      console.error('[voice] Microphone access denied', err);
      return;
    }
    const recorder = new MediaRecorder(mediaStreamRef.current, { mimeType: 'audio/webm;codecs=opus' });
    recorder.ondataavailable = async (event) => {
      if (event.data.size === 0 || ws.readyState !== WebSocket.OPEN) return;
      const arrayBuffer = await event.data.arrayBuffer();
      ws.send(encodeVoiceFrame('dispatch', arrayBuffer));
    };
    mediaRecorderRef.current = recorder;
    ws.send(JSON.stringify({ type: 'voice_start', from: 'dispatch' }));
    recorder.start(100);
    setIsTransmitting(true);
  };

  const stopTransmit = () => {
    mediaRecorderRef.current?.stop();
    mediaRecorderRef.current = null;
    mediaStreamRef.current?.getTracks().forEach((t) => t.stop());
    mediaStreamRef.current = null;
    const ws = voiceWsRef.current;
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify({ type: 'voice_end', from: 'dispatch' }));
    }
    setIsTransmitting(false);
  };

  useEffect(() => {
    const isTypingTarget = (target: EventTarget | null) => {
      const el = target as HTMLElement | null;
      return !!el && ['INPUT', 'TEXTAREA'].includes(el.tagName);
    };

    const onKeyDown = (e: KeyboardEvent) => {
      if (e.code !== 'Space' || e.repeat || isTypingTarget(e.target)) return;
      e.preventDefault();
      startTransmit();
    };
    const onKeyUp = (e: KeyboardEvent) => {
      if (e.code !== 'Space' || isTypingTarget(e.target)) return;
      e.preventDefault();
      stopTransmit();
    };
    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('keyup', onKeyUp);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('keyup', onKeyUp);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <div className="p-8">
      <h1 className="text-3xl font-bold mb-2">Dispatch Radio</h1>
      <p className="text-sm text-gray-500 mb-2">
        {connected ? 'Connected to dispatch backend' : 'Disconnected from dispatch backend'}
      </p>

      {DISPATCH_ADMIN_WS_URL && (
        <div className="mb-6 flex items-center gap-4">
          <button
            onMouseDown={startTransmit}
            onMouseUp={stopTransmit}
            onMouseLeave={() => { if (isTransmitting) stopTransmit(); }}
            className={`px-6 py-3 rounded-full font-semibold text-white select-none ${
              isTransmitting ? 'bg-red-600' : 'bg-blue-600 hover:bg-blue-700'
            }`}
          >
            {isTransmitting ? 'Transmitting…' : 'Hold to Talk (or Space)'}
          </button>
          {remoteTransmitting && (
            <span className="text-sm font-medium text-gray-700">Receiving from: {remoteTransmitting}</span>
          )}
        </div>
      )}

      {!DISPATCH_ADMIN_WS_URL ? (
        <div className="bg-white rounded-lg shadow p-6 text-center text-gray-500 text-sm italic">
          Set NEXT_PUBLIC_DISPATCH_ADMIN_WS_URL to enable this page.
        </div>
      ) : hasError ? (
        <div className="bg-white rounded-lg shadow p-6 text-center text-gray-500 text-sm italic">
          Connection error — check the dispatch backend URL/key.
        </div>
      ) : pilots.length === 0 ? (
        <div className="bg-white rounded-lg shadow p-6 text-center text-gray-500 text-sm italic">
          No pilots currently tuned to dispatch
        </div>
      ) : (
        <div className="bg-white rounded-lg shadow overflow-hidden">
          <table className="w-full text-sm">
            <thead className="bg-gray-50 text-left text-gray-500">
              <tr>
                <th className="px-4 py-2">Callsign</th>
                <th className="px-4 py-2">Distance</th>
                <th className="px-4 py-2">Comms</th>
              </tr>
            </thead>
            <tbody>
              {pilots.map((pilot) => (
                <tr key={pilot.pilotId} className="border-t">
                  <td className="px-4 py-2 font-semibold">
                    {pilot.callsign}
                    {remoteTransmitting === pilot.pilotId && (
                      <span className="ml-2 text-xs text-blue-600 font-normal">● on air</span>
                    )}
                  </td>
                  <td className="px-4 py-2">{pilot.distanceNm.toFixed(0)} nm</td>
                  <td className="px-4 py-2">
                    <span className={`px-2 py-0.5 rounded-full text-xs font-medium ${STATE_STYLES[pilot.state]}`}>
                      {STATE_LABELS[pilot.state]}
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
```

- [ ] **Step 2: Verify the build**

Run: `cd AviatesAirWebsite && npm run build`
Expected: build succeeds with no type errors.

- [ ] **Step 3: Commit**

```bash
cd AviatesAirWebsite
git add src/app/admin/dispatch/page.tsx
git commit -m "feat(admin): add voice PTT, playback, and speaking indicator to dispatch page"
```

---

## Task 15: End-to-end manual verification

No automated end-to-end test exists across three separate applications and real audio hardware — this task is a manual checklist, run after Tasks 1–14 are deployed together and the Cloudflare Worker from Task 3 Step 4 has been redeployed.

- [ ] **Step 1: Bind pilot-side PTT**

In `AviatesAirTracker` → Settings → Dispatch Radio, click the PTT Hotkey field and press a key (e.g. Right Ctrl). Confirm the field shows the bound key name. If a joystick/HOTAS is connected, select it and click "Bind Button," then press a physical button within 10 seconds — confirm it shows "Bound: button N."

- [ ] **Step 2: Open the dispatcher console**

Open `/admin/dispatch` on `AviatesAirWebsite`, logged in as admin. Confirm "Connected to dispatch backend" and a visible "Hold to Talk" button.

- [ ] **Step 3: Pilot transmits, dispatcher hears it**

With MSFS running, the tracker connected, and COM1 tuned to 123.575 while at/near EGNX, hold the bound PTT key (or joystick button) and speak, then release. On the dispatcher console, confirm the pilot's row shows "● on air" while held, and that the dispatcher hears the clip shortly after release with no static (since the pilot is in "clear" range, degradation is near zero).

- [ ] **Step 4: Dispatcher transmits, pilot hears it**

Hold Space (or the "Hold to Talk" button) on the dispatcher console and speak, then release. Confirm the pilot hears the clip in the tracker app.

- [ ] **Step 5: Confirm distance-based degradation**

Reposition the aircraft (MSFS slew/teleport) to roughly the midpoint of the degraded band (~300nm from EGNX) while staying tuned. Have the dispatcher transmit again — confirm the pilot now hears audible-but-not-overwhelming static and the constant radio band-pass timbre, clearly more degraded than Step 4 but not distorted or unintelligible.

- [ ] **Step 6: Confirm `acars_only` pilots hear nothing**

Reposition beyond 400nm while tuned. Have the dispatcher transmit — confirm the pilot receives no audio at all (not even silence-with-effects — literally nothing arrives, since the relay excludes them server-side).

- [ ] **Step 7: Confirm the registry-safety fix**

While the pilot is in "clear" range and actively tuned, watch the admin's pilot table during a transmission (voice socket activity) — confirm the pilot's row never disappears from the table due to voice channel activity, only from actually untuning or the existing 15s stale sweep.
