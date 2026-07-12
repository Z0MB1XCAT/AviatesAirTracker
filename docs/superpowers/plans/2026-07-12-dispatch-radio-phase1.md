# Dispatch Radio System — Phase 1 (Foundation) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the foundation of the dispatch radio system — an Oracle-hosted Node.js backend that tracks tuned pilots and classifies them as clear/degraded/ACARS-only by distance from EGNX, a pilot-client module in AviatesAirTracker that detects VHF tuning and reports position, and a read-only admin view in AviatesAirWebsite.

**Architecture:** Three components, three repos. `VOICE-BACKEND/` (new Node.js service, this repo) owns distance calculation and session state, reachable over two WebSocket endpoints (`/pilot`, `/admin`). `AviatesAirTracker/Services/DispatchRadioService.cs` (new) watches `COM1Frequency` on the existing 20Hz telemetry tick and opens/closes a socket to the backend. `AviatesAirWebsite/src/app/admin/dispatch/` (new page) subscribes to the admin socket and renders a live table. No voice audio and no Hoppies integration in this round — see `docs/superpowers/specs/2026-07-12-dispatch-radio-phase1-design.md`.

**Tech Stack:** Node.js 20 (built-in `node:test`, `ws` package) for the backend; C# 12 / .NET 8 (`System.Net.WebSockets.ClientWebSocket`, no new NuGet dependency) for the pilot client; Next.js/TypeScript (native browser `WebSocket`) for the admin page.

---

## Task 1: Backend — distance calculation & state classification

**Files:**
- Create: `VOICE-BACKEND/package.json`
- Create: `VOICE-BACKEND/src/distance.js`
- Test: `VOICE-BACKEND/src/distance.test.js`

- [ ] **Step 1: Scaffold the package**

Create `VOICE-BACKEND/package.json`:

```json
{
  "name": "aviates-dispatch-backend",
  "version": "0.1.0",
  "type": "module",
  "private": true,
  "engines": { "node": ">=20" },
  "scripts": {
    "start": "node index.js",
    "test": "node --test src/"
  },
  "dependencies": {
    "ws": "^8.18.0"
  }
}
```

Run: `cd VOICE-BACKEND && npm install`
Expected: `ws` installed, `node_modules/` and `package-lock.json` created.

- [ ] **Step 2: Write the failing test**

Create `VOICE-BACKEND/src/distance.test.js`:

```js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { haversineNm, classify } from './distance.js';

test('haversineNm returns 0 for identical points', () => {
  assert.equal(haversineNm(52.8311, -1.3281, 52.8311, -1.3281), 0);
});

test('haversineNm: one degree of latitude is ~60nm (definition of a nautical mile)', () => {
  const d = haversineNm(0, 0, 1, 0);
  assert.ok(Math.abs(d - 60) < 0.5, `expected ~60nm, got ${d}`);
});

test('haversineNm: one degree of longitude at the equator is ~60nm', () => {
  const d = haversineNm(0, 0, 0, 1);
  assert.ok(Math.abs(d - 60) < 0.5, `expected ~60nm, got ${d}`);
});

test('classify buckets distance into clear/degraded/acars_only at the configured thresholds', () => {
  assert.equal(classify(50, 200, 400), 'clear');
  assert.equal(classify(199.9, 200, 400), 'clear');
  assert.equal(classify(200, 200, 400), 'degraded');
  assert.equal(classify(399.9, 200, 400), 'degraded');
  assert.equal(classify(400, 200, 400), 'acars_only');
  assert.equal(classify(1000, 200, 400), 'acars_only');
});
```

- [ ] **Step 2b: Run test to verify it fails**

Run: `cd VOICE-BACKEND && npm test`
Expected: FAIL with `Cannot find module './distance.js'`

- [ ] **Step 3: Implement `distance.js`**

Create `VOICE-BACKEND/src/distance.js`:

```js
const EARTH_RADIUS_NM = 3440.065;

export function haversineNm(lat1, lon1, lat2, lon2) {
  const toRad = (deg) => (deg * Math.PI) / 180;
  const dLat = toRad(lat2 - lat1);
  const dLon = toRad(lon2 - lon1);
  const a =
    Math.sin(dLat / 2) ** 2 +
    Math.cos(toRad(lat1)) * Math.cos(toRad(lat2)) * Math.sin(dLon / 2) ** 2;
  const c = 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
  return EARTH_RADIUS_NM * c;
}

export function classify(distanceNm, clearRangeNm, degradedRangeNm) {
  if (distanceNm < clearRangeNm) return 'clear';
  if (distanceNm < degradedRangeNm) return 'degraded';
  return 'acars_only';
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd VOICE-BACKEND && npm test`
Expected: PASS, 4 tests

- [ ] **Step 5: Commit**

```bash
git add VOICE-BACKEND/package.json VOICE-BACKEND/package-lock.json VOICE-BACKEND/src/distance.js VOICE-BACKEND/src/distance.test.js
git commit -m "feat(voice-backend): add haversine distance and state classification"
```

---

## Task 2: Backend — session registry with stale sweep

**Files:**
- Create: `VOICE-BACKEND/src/registry.js`
- Test: `VOICE-BACKEND/src/registry.test.js`

- [ ] **Step 1: Write the failing test**

Create `VOICE-BACKEND/src/registry.test.js`:

```js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { SessionRegistry } from './registry.js';

test('upsert adds a session with lastSeen from the injected clock', () => {
  const registry = new SessionRegistry(() => 1000);
  registry.upsert('pilot1', { callsign: 'AVI123', lat: 1, lon: 2 });
  assert.equal(registry.get('pilot1').lastSeen, 1000);
  assert.equal(registry.get('pilot1').callsign, 'AVI123');
});

test('sweepStale removes sessions older than maxAgeMs and returns their ids', () => {
  let now = 0;
  const registry = new SessionRegistry(() => now);
  registry.upsert('stale-pilot', { callsign: 'AVI001' });
  now = 20000;
  registry.upsert('fresh-pilot', { callsign: 'AVI002' });

  const removed = registry.sweepStale(15000);

  assert.deepEqual(removed, ['stale-pilot']);
  assert.equal(registry.get('stale-pilot'), undefined);
  assert.equal(registry.get('fresh-pilot').callsign, 'AVI002');
});

test('list returns a plain array suitable for broadcasting to admin sockets', () => {
  const registry = new SessionRegistry();
  registry.upsert('pilot1', { callsign: 'AVI123', distanceNm: 42, state: 'clear' });
  assert.deepEqual(registry.list(), [
    { pilotId: 'pilot1', callsign: 'AVI123', distanceNm: 42, state: 'clear' },
  ]);
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd VOICE-BACKEND && npm test`
Expected: FAIL with `Cannot find module './registry.js'`

- [ ] **Step 3: Implement `registry.js`**

Create `VOICE-BACKEND/src/registry.js`:

```js
export class SessionRegistry {
  constructor(now = () => Date.now()) {
    this._now = now;
    this._sessions = new Map(); // pilotId -> { callsign, lat, lon, freq, distanceNm, state, lastSeen }
  }

  upsert(pilotId, data) {
    const existing = this._sessions.get(pilotId) || {};
    this._sessions.set(pilotId, { ...existing, ...data, lastSeen: this._now() });
  }

  remove(pilotId) {
    this._sessions.delete(pilotId);
  }

  get(pilotId) {
    return this._sessions.get(pilotId);
  }

  sweepStale(maxAgeMs) {
    const cutoff = this._now() - maxAgeMs;
    const removed = [];
    for (const [pilotId, session] of this._sessions) {
      if (session.lastSeen < cutoff) {
        this._sessions.delete(pilotId);
        removed.push(pilotId);
      }
    }
    return removed;
  }

  list() {
    return Array.from(this._sessions.entries()).map(([pilotId, s]) => ({
      pilotId,
      callsign: s.callsign,
      distanceNm: s.distanceNm,
      state: s.state,
    }));
  }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd VOICE-BACKEND && npm test`
Expected: PASS, 7 tests total

- [ ] **Step 5: Commit**

```bash
git add VOICE-BACKEND/src/registry.js VOICE-BACKEND/src/registry.test.js
git commit -m "feat(voice-backend): add session registry with stale-session sweep"
```

---

## Task 3: Backend — WebSocket server (pilot + admin channels)

**Files:**
- Create: `VOICE-BACKEND/src/config.js`
- Create: `VOICE-BACKEND/src/server.js`
- Test: `VOICE-BACKEND/src/server.test.js`

- [ ] **Step 1: Create the config module**

Create `VOICE-BACKEND/src/config.js`:

```js
export const config = {
  port: Number(process.env.PORT || 8080),
  dispatchLat: 52.8311,
  dispatchLon: -1.3281,
  clearRangeNm: 200,
  degradedRangeNm: 400,
  staleSessionMs: 15000,
  sweepIntervalMs: 5000,
  adminKey: process.env.ADMIN_WS_KEY || '',
};
```

- [ ] **Step 2: Write the failing integration test**

Create `VOICE-BACKEND/src/server.test.js`:

```js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import WebSocket from 'ws';
import { createServer } from './server.js';

function testConfig(overrides = {}) {
  return {
    dispatchLat: 52.8311,
    dispatchLon: -1.3281,
    clearRangeNm: 200,
    degradedRangeNm: 400,
    staleSessionMs: 15000,
    sweepIntervalMs: 100000, // long enough to not interfere with these tests
    adminKey: '',
    ...overrides,
  };
}

function waitForOpen(ws) {
  return new Promise((resolve) => ws.once('open', resolve));
}

function waitForMessage(ws) {
  return new Promise((resolve) => ws.once('message', (data) => resolve(JSON.parse(data.toString()))));
}

function waitForClose(ws) {
  return new Promise((resolve) => ws.once('close', resolve));
}

test('pilot connecting at EGNX receives a clear state, and the admin feed reflects it', async () => {
  const { httpServer, close } = createServer(testConfig());
  await new Promise((resolve) => httpServer.listen(0, resolve));
  const port = httpServer.address().port;

  const adminWs = new WebSocket(`ws://localhost:${port}/admin`);
  await waitForOpen(adminWs);
  await waitForMessage(adminWs); // initial empty snapshot

  const pilotWs = new WebSocket(`ws://localhost:${port}/pilot`);
  await waitForOpen(pilotWs);

  const adminUpdatePromise = waitForMessage(adminWs);
  pilotWs.send(JSON.stringify({ pilotId: 'p1', callsign: 'AVI123', lat: 52.8311, lon: -1.3281, freq: '123.575' }));

  const pilotReply = await waitForMessage(pilotWs);
  assert.equal(pilotReply.state, 'clear');
  assert.ok(pilotReply.distanceNm < 1);

  const adminUpdate = await adminUpdatePromise;
  assert.equal(adminUpdate.pilots.length, 1);
  assert.equal(adminUpdate.pilots[0].callsign, 'AVI123');
  assert.equal(adminUpdate.pilots[0].state, 'clear');

  pilotWs.close();
  adminWs.close();
  close();
});

test('admin connections without the correct key are rejected when adminKey is configured', async () => {
  const { httpServer, close } = createServer(testConfig({ adminKey: 'secret' }));
  await new Promise((resolve) => httpServer.listen(0, resolve));
  const port = httpServer.address().port;

  const badAdminWs = new WebSocket(`ws://localhost:${port}/admin?key=wrong`);
  await waitForClose(badAdminWs);

  close();
});
```

- [ ] **Step 3: Run test to verify it fails**

Run: `cd VOICE-BACKEND && npm test`
Expected: FAIL with `Cannot find module './server.js'`

- [ ] **Step 4: Implement `server.js`**

Create `VOICE-BACKEND/src/server.js`:

```js
import { createServer as createHttpServer } from 'node:http';
import { WebSocketServer } from 'ws';
import { haversineNm, classify } from './distance.js';
import { SessionRegistry } from './registry.js';

export function createServer(config) {
  const httpServer = createHttpServer((req, res) => {
    if (req.url === '/health') {
      res.writeHead(200, { 'Content-Type': 'text/plain' });
      res.end('ok');
      return;
    }
    res.writeHead(404);
    res.end();
  });

  const registry = new SessionRegistry();
  const adminSockets = new Set();

  const pilotWss = new WebSocketServer({ noServer: true });
  const adminWss = new WebSocketServer({ noServer: true });

  httpServer.on('upgrade', (req, socket, head) => {
    const url = new URL(req.url, 'http://localhost');
    if (url.pathname === '/pilot') {
      pilotWss.handleUpgrade(req, socket, head, (ws) => pilotWss.emit('connection', ws));
    } else if (url.pathname === '/admin') {
      if (config.adminKey && url.searchParams.get('key') !== config.adminKey) {
        socket.destroy();
        return;
      }
      adminWss.handleUpgrade(req, socket, head, (ws) => adminWss.emit('connection', ws));
    } else {
      socket.destroy();
    }
  });

  function broadcastToAdmins() {
    const payload = JSON.stringify({ pilots: registry.list() });
    for (const ws of adminSockets) {
      if (ws.readyState === ws.OPEN) ws.send(payload);
    }
  }

  pilotWss.on('connection', (ws) => {
    let pilotId = null;

    ws.on('message', (raw) => {
      let msg;
      try {
        msg = JSON.parse(raw.toString());
      } catch {
        return;
      }
      if (!msg.pilotId) return;

      pilotId = msg.pilotId;

      const distanceNm = haversineNm(config.dispatchLat, config.dispatchLon, msg.lat, msg.lon);
      const state = classify(distanceNm, config.clearRangeNm, config.degradedRangeNm);

      registry.upsert(pilotId, {
        callsign: msg.callsign,
        lat: msg.lat,
        lon: msg.lon,
        freq: msg.freq,
        distanceNm,
        state,
      });

      ws.send(JSON.stringify({ state, distanceNm }));
      broadcastToAdmins();
    });

    ws.on('close', () => {
      if (pilotId) {
        registry.remove(pilotId);
        broadcastToAdmins();
      }
    });
  });

  adminWss.on('connection', (ws) => {
    adminSockets.add(ws);
    ws.send(JSON.stringify({ pilots: registry.list() }));
    ws.on('close', () => adminSockets.delete(ws));
  });

  const sweepInterval = setInterval(() => {
    const removed = registry.sweepStale(config.staleSessionMs);
    if (removed.length > 0) broadcastToAdmins();
  }, config.sweepIntervalMs);

  function close() {
    clearInterval(sweepInterval);
    httpServer.close();
  }

  return { httpServer, close };
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `cd VOICE-BACKEND && npm test`
Expected: PASS, 9 tests total

- [ ] **Step 6: Commit**

```bash
git add VOICE-BACKEND/src/config.js VOICE-BACKEND/src/server.js VOICE-BACKEND/src/server.test.js
git commit -m "feat(voice-backend): add pilot/admin WebSocket server with keyed admin channel"
```

---

## Task 4: Backend — entry point

**Files:**
- Create: `VOICE-BACKEND/index.js`
- Modify: `VOICE-BACKEND/.gitignore`

- [ ] **Step 1: Create the entry point**

Create `VOICE-BACKEND/index.js`:

```js
import { createServer } from './src/server.js';
import { config } from './src/config.js';

const { httpServer } = createServer(config);
httpServer.listen(config.port, () => {
  console.log(`Dispatch backend listening on :${config.port}`);
});
```

- [ ] **Step 2: Ignore node_modules**

Create `VOICE-BACKEND/.gitignore`:

```
node_modules/
```

- [ ] **Step 3: Verify it runs locally**

Run: `cd VOICE-BACKEND && ADMIN_WS_KEY=test-key node index.js &`
Then: `curl http://localhost:8080/health`
Expected: `ok`, then stop the background process (`kill %1` or Ctrl+C).

- [ ] **Step 4: Commit**

```bash
git add VOICE-BACKEND/index.js VOICE-BACKEND/.gitignore
git commit -m "feat(voice-backend): add entry point"
```

---

## Task 5: Oracle VM provisioning and deployment

**Files:**
- Create: `VOICE-BACKEND/deploy/aviates-dispatch-backend.service`
- Create: `VOICE-BACKEND/README.md`

- [ ] **Step 1: Write the systemd unit**

Create `VOICE-BACKEND/deploy/aviates-dispatch-backend.service`:

```ini
[Unit]
Description=Aviates Air dispatch radio backend
After=network.target

[Service]
Type=simple
WorkingDirectory=/opt/aviates-dispatch-backend
Environment=PORT=8080
Environment=ADMIN_WS_KEY=CHANGE_ME
ExecStart=/usr/bin/node index.js
Restart=on-failure
RestartSec=5
User=aviates

[Install]
WantedBy=multi-user.target
```

- [ ] **Step 2: Write the setup walkthrough**

Create `VOICE-BACKEND/README.md`:

```markdown
# Dispatch Radio Backend — Oracle Setup

## 1. Create the Oracle Cloud account and VM

1. Sign up for an Oracle Cloud Free Tier account at oracle.com/cloud/free.
2. In the console, go to **Compute → Instances → Create Instance**.
3. Choose the **Always Free** shape: `VM.Standard.A1.Flex` (1 OCPU / 6GB is plenty) or `VM.Standard.E2.1.Micro`.
4. Image: **Canonical Ubuntu 22.04**.
5. Add your SSH public key under "Add SSH keys" (generate one locally with `ssh-keygen` if you don't have one).
6. Create the instance and note its **public IP address**.

## 2. Open the port

1. In the console, go to your VM's subnet → **Security Lists** → the default list → **Add Ingress Rule**.
   - Source CIDR: `0.0.0.0/0`
   - Destination port range: `8080`
   - Protocol: TCP
2. SSH into the VM: `ssh ubuntu@<your-vm-ip>`
3. Ubuntu's firewall is off by default on Oracle images, but if `ufw` is active: `sudo ufw allow 8080/tcp`

## 3. Install Node.js 20

```bash
curl -fsSL https://deb.nodesource.com/setup_20.x | sudo -E bash -
sudo apt-get install -y nodejs
node --version   # confirm v20.x
```

## 4. Deploy the backend

```bash
sudo useradd -r -s /usr/sbin/nologin aviates
sudo mkdir -p /opt/aviates-dispatch-backend
sudo chown ubuntu:ubuntu /opt/aviates-dispatch-backend
```

From your machine, copy the `VOICE-BACKEND/` folder contents to the VM (or `git clone` the repo there and `cd` into `VOICE-BACKEND/`):

```bash
scp -r VOICE-BACKEND/* ubuntu@<your-vm-ip>:/opt/aviates-dispatch-backend/
```

On the VM:

```bash
cd /opt/aviates-dispatch-backend
npm ci --omit=dev
sudo chown -R aviates:aviates /opt/aviates-dispatch-backend
```

## 5. Set the admin key and install the service

Edit the systemd unit to set a real admin key (this gates the `/admin` WebSocket feed — anyone with this key can see all tuned pilots' positions, so keep it secret):

```bash
sudo cp deploy/aviates-dispatch-backend.service /etc/systemd/system/
sudo nano /etc/systemd/system/aviates-dispatch-backend.service
# replace ADMIN_WS_KEY=CHANGE_ME with a long random value
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now aviates-dispatch-backend
sudo systemctl status aviates-dispatch-backend
```

## 6. Verify

```bash
curl http://localhost:8080/health
# from your own machine:
curl http://<your-vm-ip>:8080/health
```

Both should print `ok`. Once confirmed, set:
- `AviatesAirTracker` → Settings → Dispatch Backend URL → `ws://<your-vm-ip>:8080`
- `AviatesAirWebsite` → `.env.local` → `NEXT_PUBLIC_DISPATCH_ADMIN_WS_URL=ws://<your-vm-ip>:8080/admin` and `NEXT_PUBLIC_DISPATCH_ADMIN_WS_KEY=<the same admin key>`
```

- [ ] **Step 3: Commit**

```bash
git add VOICE-BACKEND/deploy/aviates-dispatch-backend.service VOICE-BACKEND/README.md
git commit -m "docs(voice-backend): add Oracle VM setup and systemd deployment guide"
```

---

## Task 6: Pilot client — add the settings field

**Files:**
- Modify: `AviatesAirTracker/Services/SupportServices.cs` (the `AppSettings` class, near line 357)

- [ ] **Step 1: Add the setting**

In `AviatesAirTracker/Services/SupportServices.cs`, find:

```csharp
    public string BackendApiUrl { get; set; } = "https://acars.flyaviatesair.uk";
```

Add immediately after it:

```csharp
    public string DispatchBackendUrl { get; set; } = "";
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: 0 errors (this repo has no automated test suite — build success plus the manual verification in Task 11 is the verification method here, per `CLAUDE.md`).

- [ ] **Step 3: Commit**

```bash
git add AviatesAirTracker/Services/SupportServices.cs
git commit -m "feat(client): add DispatchBackendUrl setting"
```

---

## Task 7: Pilot client — settings UI field

**Files:**
- Modify: `AviatesAirTracker/Pages/Settings.razor`

- [ ] **Step 1: Add a Dispatch Radio panel**

In `Settings.razor`, in the right column (same column as the "ACARS Configuration" `panel-card` you can find via the `ACARS Configuration` heading), add a new panel immediately after it:

```razor
        <!-- Dispatch Radio -->
        <div class="panel-card anim-up-d2">
            <div class="form-section-title">Dispatch Radio</div>
            <div style="display:flex;flex-direction:column;gap:var(--sp-4);">
                <div class="form-group">
                    <label class="form-label">Dispatch Backend URL</label>
                    <input class="form-input" type="text"
                           @bind="SettingsService.Settings.DispatchBackendUrl"
                           placeholder="ws://<oracle-vm-ip>:8080" />
                </div>
                <div style="background:var(--surface-2);border-radius:var(--r-md);padding:10px 13px;font-size:12px;color:var(--text-muted);line-height:1.5;">
                    Tune COM1 to <strong>123.575 MHz</strong> in MSFS to connect to dispatch. Leave this blank if you don't have a dispatch backend URL yet.
                </div>
            </div>
        </div>
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add AviatesAirTracker/Pages/Settings.razor
git commit -m "feat(client): add Dispatch Radio settings panel"
```

---

## Task 8: Pilot client — DispatchRadioService

**Files:**
- Create: `AviatesAirTracker/Services/DispatchRadioService.cs`

- [ ] **Step 1: Implement the service**

Create `AviatesAirTracker/Services/DispatchRadioService.cs`:

```csharp
using AviatesAirTracker.Core.SimConnect;
using Serilog;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AviatesAirTracker.Services;

// ============================================================
// DISPATCH RADIO SERVICE
//
// Watches COM1 frequency on the existing 20Hz telemetry tick.
// When tuned to the dispatch VHF frequency, opens a WebSocket
// to the dispatch backend, reports position once/sec, and
// exposes the clear/degraded/acars_only state pushed back.
//
// Mirrors AcarsPositionService in structure: subscribes to
// FlightSessionManager.TelemetryUpdated and throttles outbound
// calls with an Interlocked tick counter.
// ============================================================

public enum DispatchCommsState { Untuned, Clear, Degraded, AcarsOnly }

public class DispatchRadioService : IDisposable
{
    private const double VhfFrequencyMhz = 123.575;
    private const double FrequencyEpsilon = 0.001;
    private const int PositionSendIntervalSeconds = 1;
    private const int ReconnectDelaySeconds = 5;

    private readonly FlightSessionManager _session;
    private readonly SettingsService _settings;

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _connectionLoopTask;
    private volatile bool _tuned;
    private long _lastSendTicks;

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

    private void OnTelemetryUpdated(object? sender, TelemetrySnapshot snap)
    {
        bool isTuned = Math.Abs(snap.Raw.COM1Frequency - VhfFrequencyMhz) < FrequencyEpsilon;

        if (isTuned && !_tuned)
        {
            _tuned = true;
            _cts = new CancellationTokenSource();
            _connectionLoopTask = Task.Run(() => MaintainConnectionAsync(_cts.Token));
        }
        else if (!isTuned && _tuned)
        {
            _tuned = false;
            _ = DisconnectAsync();
        }

        if (!_tuned) return;

        var now = DateTime.UtcNow;
        if ((now.Ticks - Interlocked.Read(ref _lastSendTicks)) < TimeSpan.TicksPerSecond * PositionSendIntervalSeconds)
            return;
        Interlocked.Exchange(ref _lastSendTicks, now.Ticks);

        _ = SendPositionAsync(snap.Latitude, snap.Longitude);
    }

    private async Task MaintainConnectionAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _tuned)
        {
            try
            {
                var url = _settings.Settings.DispatchBackendUrl;
                if (string.IsNullOrWhiteSpace(url))
                {
                    await Task.Delay(TimeSpan.FromSeconds(ReconnectDelaySeconds), token);
                    continue;
                }

                _socket = new ClientWebSocket();
                await _socket.ConnectAsync(new Uri($"{url}/pilot"), token);
                Log.Information("[DispatchRadio] Connected to dispatch backend");
                await ReceiveLoopAsync(_socket, token);
            }
            catch (OperationCanceledException)
            {
                // Expected when untuning or disposing
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DispatchRadio] Connection error, retrying in {Delay}s", ReconnectDelaySeconds);
            }

            if (!token.IsCancellationRequested && _tuned)
                await Task.Delay(TimeSpan.FromSeconds(ReconnectDelaySeconds), token);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[4096];
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) break;

            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            HandleServerMessage(json);
        }
    }

    private void HandleServerMessage(string json)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<DispatchStateMessage>(json);
            if (msg == null) return;

            CommsState = msg.State switch
            {
                "clear" => DispatchCommsState.Clear,
                "degraded" => DispatchCommsState.Degraded,
                "acars_only" => DispatchCommsState.AcarsOnly,
                _ => CommsState,
            };
            DistanceNm = msg.DistanceNm;
            CommsStateChanged?.Invoke(this, CommsState);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DispatchRadio] Failed to parse server message: {Json}", json);
        }
    }

    private async Task SendPositionAsync(double lat, double lon)
    {
        if (_socket?.State != WebSocketState.Open) return;

        var callsign = string.IsNullOrEmpty(_session.CurrentFlight?.Callsign)
            ? _settings.Settings.PilotId
            : _session.CurrentFlight!.Callsign;

        var payload = new DispatchPositionMessage
        {
            PilotId = _settings.Settings.PilotId,
            Callsign = callsign,
            Lat = lat,
            Lon = lon,
            Freq = VhfFrequencyMhz.ToString("F3"),
        };

        try
        {
            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[DispatchRadio] Send failed (non-critical, will retry next tick)");
        }
    }

    private async Task DisconnectAsync()
    {
        CommsState = DispatchCommsState.Untuned;
        DistanceNm = null;
        CommsStateChanged?.Invoke(this, CommsState);

        _cts?.Cancel();

        if (_socket?.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "untuned", CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[DispatchRadio] Close error (non-critical)");
            }
        }

        _socket?.Dispose();
        _socket = null;
    }

    public void Dispose()
    {
        _session.TelemetryUpdated -= OnTelemetryUpdated;
        _cts?.Cancel();
        _socket?.Dispose();
        Log.Debug("[DispatchRadio] Disposed");
    }
}

internal class DispatchPositionMessage
{
    [JsonPropertyName("pilotId")]  public string PilotId  { get; set; } = "";
    [JsonPropertyName("callsign")] public string Callsign { get; set; } = "";
    [JsonPropertyName("lat")]      public double Lat      { get; set; }
    [JsonPropertyName("lon")]      public double Lon       { get; set; }
    [JsonPropertyName("freq")]     public string Freq      { get; set; } = "";
}

internal class DispatchStateMessage
{
    [JsonPropertyName("state")]      public string State      { get; set; } = "";
    [JsonPropertyName("distanceNm")] public double DistanceNm  { get; set; }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add AviatesAirTracker/Services/DispatchRadioService.cs
git commit -m "feat(client): add DispatchRadioService for VHF tune detection and position reporting"
```

---

## Task 9: Pilot client — DI registration

**Files:**
- Modify: `AviatesAirTracker/App.xaml.cs`

- [ ] **Step 1: Register the singleton**

Find:

```csharp
        services.AddSingleton<AcarsPositionService>();
```

Add immediately after it:

```csharp
        services.AddSingleton<DispatchRadioService>();
```

- [ ] **Step 2: Eagerly resolve it at startup**

Find:

```csharp
            // Resolve to subscribe to TelemetryUpdated (constructor wires the event)
            _serviceProvider.GetRequiredService<AcarsPositionService>();
```

Add immediately after it:

```csharp

            // Resolve to subscribe to TelemetryUpdated (constructor wires the event)
            _serviceProvider.GetRequiredService<DispatchRadioService>();
```

- [ ] **Step 3: Dispose it on exit**

Find:

```csharp
        _serviceProvider.GetService<AcarsPositionService>()?.Dispose();
```

Add immediately after it:

```csharp
        _serviceProvider.GetService<DispatchRadioService>()?.Dispose();
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
git add AviatesAirTracker/App.xaml.cs
git commit -m "feat(client): wire DispatchRadioService into the DI container"
```

---

## Task 10: Admin view — read-only dispatch page

**Files:**
- Modify: `AviatesAirWebsite/src/lib/constants.ts`
- Create: `AviatesAirWebsite/src/app/admin/dispatch/page.tsx`
- Modify: `AviatesAirWebsite/src/app/admin/page.tsx`

- [ ] **Step 1: Add the config constants**

In `AviatesAirWebsite/src/lib/constants.ts`, add after the `WORKER_URL` line:

```ts
export const DISPATCH_ADMIN_WS_URL = process.env.NEXT_PUBLIC_DISPATCH_ADMIN_WS_URL || '';
export const DISPATCH_ADMIN_WS_KEY = process.env.NEXT_PUBLIC_DISPATCH_ADMIN_WS_KEY || '';
```

- [ ] **Step 2: Create the dispatch admin page**

Create `AviatesAirWebsite/src/app/admin/dispatch/page.tsx`:

```tsx
'use client';

import { useEffect, useState } from 'react';
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

export default function DispatchRadioPage() {
  const { token } = useAdminAuth();
  const [pilots, setPilots] = useState<DispatchPilot[]>([]);
  const [connected, setConnected] = useState(false);

  useEffect(() => {
    if (!token || !DISPATCH_ADMIN_WS_URL) return;

    const ws = new WebSocket(`${DISPATCH_ADMIN_WS_URL}?key=${encodeURIComponent(DISPATCH_ADMIN_WS_KEY)}`);
    ws.onopen = () => setConnected(true);
    ws.onclose = () => setConnected(false);
    ws.onmessage = (event) => {
      const data = JSON.parse(event.data);
      if (Array.isArray(data.pilots)) setPilots(data.pilots);
    };

    return () => ws.close();
  }, [token]);

  return (
    <div className="p-8">
      <h1 className="text-3xl font-bold mb-2">Dispatch Radio</h1>
      <p className="text-sm text-gray-500 mb-6">
        {connected ? 'Connected to dispatch backend' : 'Disconnected from dispatch backend'}
      </p>

      {!DISPATCH_ADMIN_WS_URL ? (
        <div className="bg-white rounded-lg shadow p-6 text-center text-gray-500 text-sm italic">
          Set NEXT_PUBLIC_DISPATCH_ADMIN_WS_URL to enable this page.
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
                  <td className="px-4 py-2 font-semibold">{pilot.callsign}</td>
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

- [ ] **Step 3: Add the dashboard tile**

In `AviatesAirWebsite/src/app/admin/page.tsx`, add this card before the closing `</div>` of the grid (after the "Manual PIREPs" card):

```tsx
        <div className="bg-white p-6 rounded-lg shadow">
          <h2 className="text-xl font-bold mb-2">📡 Dispatch Radio</h2>
          <p className="text-gray-600 mb-4">View pilots currently tuned to the dispatch frequency.</p>
          <a href="/admin/dispatch" className="text-blue-600 hover:underline font-medium">
            Go to Dispatch Radio →
          </a>
        </div>
```

- [ ] **Step 4: Verify the build**

Run: `cd AviatesAirWebsite && npm run build`
Expected: build succeeds with no type errors.

- [ ] **Step 5: Commit**

```bash
cd AviatesAirWebsite
git add src/lib/constants.ts src/app/admin/dispatch/page.tsx src/app/admin/page.tsx
git commit -m "feat(admin): add read-only dispatch radio view"
```

---

## Task 11: End-to-end manual verification

No automated end-to-end test exists across three separate applications (a Node backend, a WPF/Blazor desktop app, and a Next.js site) — this task is a manual checklist, run after Tasks 1–10 are deployed together.

- [ ] **Step 1: Start the backend locally**

Run: `cd VOICE-BACKEND && ADMIN_WS_KEY=test-key node index.js`

- [ ] **Step 2: Point the desktop app at it**

In AviatesAirTracker → Settings → Dispatch Radio, set Dispatch Backend URL to `ws://localhost:8080`.

- [ ] **Step 3: Point the admin page at it**

In `AviatesAirWebsite/.env.local`, set:
```
NEXT_PUBLIC_DISPATCH_ADMIN_WS_URL=ws://localhost:8080/admin
NEXT_PUBLIC_DISPATCH_ADMIN_WS_KEY=test-key
```
Run: `cd AviatesAirWebsite && npm run dev`, then open `/admin/dispatch` in a browser (logged in as admin).

- [ ] **Step 4: Tune in and confirm clear state**

With MSFS running and the tracker app connected, tune COM1 to 123.575 while parked at or near EGNX. Confirm the admin page shows your callsign with a "Clear" badge within a few seconds.

- [ ] **Step 5: Confirm distance-based state change**

Use MSFS's slew mode or the flight-planner "teleport to" feature to reposition more than 400nm from EGNX (52.8311, -1.3281) while still tuned. Confirm the admin page updates the same pilot row to "ACARS Only" within a few seconds, without needing to retune the radio.

- [ ] **Step 6: Confirm reconnect behavior**

Stop the backend process (Ctrl+C) while still tuned. Confirm no errors surface in the tracker app's UI and that Serilog's log file (`logs/`) shows `[DispatchRadio] Connection error, retrying in 5s` repeating. Restart the backend and confirm the client automatically reconnects and the admin page shows the pilot again within ~5-10 seconds.

- [ ] **Step 7: Confirm untuning cleans up**

Tune COM1 away from 123.575. Confirm the pilot's row disappears from the admin page within a few seconds (either via the immediate close or the 15s stale sweep).
