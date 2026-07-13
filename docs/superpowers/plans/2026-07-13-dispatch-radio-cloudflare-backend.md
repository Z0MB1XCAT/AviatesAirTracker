# Dispatch Radio Backend — Cloudflare Workers Port — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Node.js/Oracle-VM dispatch radio backend with a Cloudflare Worker + Durable Object that runs entirely on Cloudflare's free tier (no credit card), verified fully locally — no deployment happens as part of this plan.

**Architecture:** A new, fully isolated Cloudflare Worker (`DISPATCH-RADIO-WORKER/`, name `aviates-dispatch-radio`) routes `/pilot` and `/admin` WebSocket upgrades to a single global `DispatchRadioRoom` Durable Object using the WebSocket Hibernation API. The wire protocol (message shapes, EGNX coordinates, distance thresholds, admin key gating) is identical to the Node.js version it replaces, so no client or admin-page code changes.

**Tech Stack:** Cloudflare Workers, Durable Objects (SQLite-backed, WebSocket Hibernation API), Wrangler CLI, plain Vitest for pure-logic unit tests.

**A note on testing approach (deviation from the design spec's literal wording):** The spec mentions `@cloudflare/vitest-pool-workers` for all tests. This plan uses plain Vitest (no Workers pool) for `distance.js`/`registry.js` instead, since those are pure JS functions with zero Workers-runtime dependencies — no need for a specialized test pool. For the Durable Object/WebSocket integration behavior, this plan uses `wrangler dev` (the standard local Workers runtime) plus a small scripted WebSocket client, mirroring the exact technique already proven for the Node.js backend in Phase 1, rather than an automated `vitest-pool-workers` WebSocket test whose exact API surface is newer and less battle-tested. This is a deliberate reliability choice — real, verified-by-execution code beats a guessed-at test-framework API. The spec will be amended to match once this plan is approved.

**Also note:** this plan verifies everything against the **local** Workers runtime only (`wrangler dev`). It does not run `wrangler login` or `wrangler deploy` — actual deployment is a separate, explicitly-confirmed step outside this plan, per the design spec and the user's standing instruction not to run deploy commands without direct confirmation each time.

---

## Task 1: Scaffold the Worker package

**Files:**
- Create: `DISPATCH-RADIO-WORKER/package.json`
- Create: `DISPATCH-RADIO-WORKER/vitest.config.js`
- Create: `DISPATCH-RADIO-WORKER/.gitignore`

- [ ] **Step 1: Create the package manifest**

Create `DISPATCH-RADIO-WORKER/package.json`:

```json
{
  "name": "aviates-dispatch-radio-worker",
  "version": "0.1.0",
  "private": true,
  "type": "module",
  "scripts": {
    "test": "vitest run",
    "dev": "wrangler dev",
    "deploy": "wrangler deploy"
  }
}
```

- [ ] **Step 2: Install dependencies**

Run: `cd DISPATCH-RADIO-WORKER && npm install -D wrangler vitest`
Expected: `wrangler` and `vitest` installed, `node_modules/` and `package-lock.json` created. This may take a minute — wrangler is a large package.

- [ ] **Step 3: Configure Vitest**

Create `DISPATCH-RADIO-WORKER/vitest.config.js`:

```js
export default {
  test: {
    include: ['test/**/*.test.js'],
  },
};
```

- [ ] **Step 4: Ignore node_modules and Wrangler's local state**

Create `DISPATCH-RADIO-WORKER/.gitignore`:

```
node_modules/
.wrangler/
```

- [ ] **Step 5: Commit**

```bash
git add DISPATCH-RADIO-WORKER/package.json DISPATCH-RADIO-WORKER/package-lock.json DISPATCH-RADIO-WORKER/vitest.config.js DISPATCH-RADIO-WORKER/.gitignore
git commit -m "chore(dispatch-radio-worker): scaffold Cloudflare Worker package"
```

---

## Task 2: Distance calculation and classification

**Files:**
- Create: `DISPATCH-RADIO-WORKER/src/distance.js`
- Test: `DISPATCH-RADIO-WORKER/test/distance.test.js`

- [ ] **Step 1: Write the failing test**

Create `DISPATCH-RADIO-WORKER/test/distance.test.js`:

```js
import { describe, it, expect } from 'vitest';
import { haversineNm, classify } from '../src/distance.js';

describe('haversineNm', () => {
  it('returns 0 for identical points', () => {
    expect(haversineNm(52.8311, -1.3281, 52.8311, -1.3281)).toBe(0);
  });

  it('treats one degree of latitude as ~60nm (definition of a nautical mile)', () => {
    const d = haversineNm(0, 0, 1, 0);
    expect(Math.abs(d - 60)).toBeLessThan(0.5);
  });

  it('treats one degree of longitude at the equator as ~60nm', () => {
    const d = haversineNm(0, 0, 0, 1);
    expect(Math.abs(d - 60)).toBeLessThan(0.5);
  });
});

describe('classify', () => {
  it('buckets distance into clear/degraded/acars_only at the configured thresholds', () => {
    expect(classify(50, 200, 400)).toBe('clear');
    expect(classify(199.9, 200, 400)).toBe('clear');
    expect(classify(200, 200, 400)).toBe('degraded');
    expect(classify(399.9, 200, 400)).toBe('degraded');
    expect(classify(400, 200, 400)).toBe('acars_only');
    expect(classify(1000, 200, 400)).toBe('acars_only');
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd DISPATCH-RADIO-WORKER && npm test`
Expected: FAIL with a "Cannot find module" or "Failed to resolve import" error for `../src/distance.js`

- [ ] **Step 3: Implement distance.js**

Create `DISPATCH-RADIO-WORKER/src/distance.js`:

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

Run: `cd DISPATCH-RADIO-WORKER && npm test`
Expected: PASS, 4 tests

- [ ] **Step 5: Commit**

```bash
git add DISPATCH-RADIO-WORKER/src/distance.js DISPATCH-RADIO-WORKER/test/distance.test.js
git commit -m "feat(dispatch-radio-worker): add haversine distance and state classification"
```

---

## Task 3: Session registry

**Files:**
- Create: `DISPATCH-RADIO-WORKER/src/registry.js`
- Test: `DISPATCH-RADIO-WORKER/test/registry.test.js`

- [ ] **Step 1: Write the failing test**

Create `DISPATCH-RADIO-WORKER/test/registry.test.js`:

```js
import { describe, it, expect } from 'vitest';
import { SessionRegistry } from '../src/registry.js';

describe('SessionRegistry', () => {
  it('upsert adds a session with lastSeen from the injected clock', () => {
    const registry = new SessionRegistry(() => 1000);
    registry.upsert('pilot1', { callsign: 'AVI123', lat: 1, lon: 2 });
    expect(registry.get('pilot1').lastSeen).toBe(1000);
    expect(registry.get('pilot1').callsign).toBe('AVI123');
  });

  it('sweepStale removes sessions older than maxAgeMs and returns their ids', () => {
    let now = 0;
    const registry = new SessionRegistry(() => now);
    registry.upsert('stale-pilot', { callsign: 'AVI001' });
    now = 20000;
    registry.upsert('fresh-pilot', { callsign: 'AVI002' });

    const removed = registry.sweepStale(15000);

    expect(removed).toEqual(['stale-pilot']);
    expect(registry.get('stale-pilot')).toBeUndefined();
    expect(registry.get('fresh-pilot').callsign).toBe('AVI002');
  });

  it('list returns a plain array suitable for broadcasting to admin sockets', () => {
    const registry = new SessionRegistry();
    registry.upsert('pilot1', { callsign: 'AVI123', distanceNm: 42, state: 'clear' });
    expect(registry.list()).toEqual([
      { pilotId: 'pilot1', callsign: 'AVI123', distanceNm: 42, state: 'clear' },
    ]);
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd DISPATCH-RADIO-WORKER && npm test`
Expected: FAIL with a "Cannot find module" or "Failed to resolve import" error for `../src/registry.js`

- [ ] **Step 3: Implement registry.js**

Create `DISPATCH-RADIO-WORKER/src/registry.js`:

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

Run: `cd DISPATCH-RADIO-WORKER && npm test`
Expected: PASS, 7 tests total

- [ ] **Step 5: Commit**

```bash
git add DISPATCH-RADIO-WORKER/src/registry.js DISPATCH-RADIO-WORKER/test/registry.test.js
git commit -m "feat(dispatch-radio-worker): add session registry with stale-session sweep"
```

---

## Task 4: The Durable Object (DispatchRadioRoom)

**Files:**
- Create: `DISPATCH-RADIO-WORKER/wrangler.toml`
- Create: `DISPATCH-RADIO-WORKER/src/room.js`

**Critical context — read before writing code:** Cloudflare's WebSocket Hibernation API discards a Durable Object's in-memory JavaScript state (including any plain `Map`/class field) whenever the object hibernates due to inactivity — only the WebSocket connections themselves survive, tracked by the Workers runtime independently of your object's JS heap. When a message arrives on a hibernating socket, the runtime re-runs the DO's constructor from scratch and delivers the message to `webSocketMessage`. This means `this.registry` (an in-memory `SessionRegistry`) is **not** reliable as a source of truth by itself — it must be rebuilt on every wake by reading back per-socket data that was explicitly persisted with `ws.serializeAttachment(data)` at write time, recovered via `ws.deserializeAttachment()`. The pattern below (`rebuildRegistry()`, called in the constructor and again at the top of `alarm()`) exists specifically to handle this — do not simplify it away.

- [ ] **Step 1: Write the Wrangler config**

Create `DISPATCH-RADIO-WORKER/wrangler.toml`:

```toml
name = "aviates-dispatch-radio"
main = "src/worker.js"
compatibility_date = "2026-07-13"

[[durable_objects.bindings]]
name = "DISPATCH_ROOM"
class_name = "DispatchRadioRoom"

[[migrations]]
tag = "v1"
new_sqlite_classes = ["DispatchRadioRoom"]
```

- [ ] **Step 2: Implement the Durable Object**

Create `DISPATCH-RADIO-WORKER/src/room.js`:

```js
import { DurableObject } from 'cloudflare:workers';
import { haversineNm, classify } from './distance.js';
import { SessionRegistry } from './registry.js';

const DISPATCH_LAT = 52.8311;
const DISPATCH_LON = -1.3281;
const CLEAR_RANGE_NM = 200;
const DEGRADED_RANGE_NM = 400;
const STALE_SESSION_MS = 15000;
const SWEEP_INTERVAL_MS = 5000;

export class DispatchRadioRoom extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    this.ctx = ctx;

    // In-memory state does not survive hibernation. This registry is a
    // rebuildable cache, not a source of truth — rebuildRegistry() below
    // reconstructs it from live hibernatable sockets' attachments on every
    // wake (cold start or resume-from-hibernation both re-run this constructor).
    this.registry = new SessionRegistry();
    this.rebuildRegistry();

    this.ctx.blockConcurrencyWhile(async () => {
      const alarm = await this.ctx.storage.getAlarm();
      if (alarm === null) {
        await this.ctx.storage.setAlarm(Date.now() + SWEEP_INTERVAL_MS);
      }
    });
  }

  rebuildRegistry() {
    for (const ws of this.ctx.getWebSockets('pilot')) {
      const data = ws.deserializeAttachment();
      if (data?.pilotId) {
        this.registry.upsert(data.pilotId, data);
      }
    }
  }

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

  async webSocketMessage(ws, message) {
    if (this.ctx.getTags(ws).includes('admin')) return;

    let msg;
    try {
      msg = JSON.parse(typeof message === 'string' ? message : new TextDecoder().decode(message));
    } catch {
      return;
    }
    if (!msg || typeof msg !== 'object' || !msg.pilotId) return;

    const distanceNm = haversineNm(DISPATCH_LAT, DISPATCH_LON, msg.lat, msg.lon);
    const state = classify(distanceNm, CLEAR_RANGE_NM, DEGRADED_RANGE_NM);

    const record = {
      pilotId: msg.pilotId,
      callsign: msg.callsign,
      lat: msg.lat,
      lon: msg.lon,
      freq: msg.freq,
      distanceNm,
      state,
      lastSeen: Date.now(),
    };

    // Persist to the socket itself so a future hibernation/wake cycle can
    // rebuild this pilot's entry (see rebuildRegistry above).
    ws.serializeAttachment(record);
    this.registry.upsert(msg.pilotId, record);

    ws.send(JSON.stringify({ state, distanceNm }));
    this.broadcastToAdmins();
  }

  async webSocketClose(ws) {
    const data = ws.deserializeAttachment();
    if (data?.pilotId) {
      this.registry.remove(data.pilotId);
      this.broadcastToAdmins();
    }
  }

  async webSocketError(ws) {
    await this.webSocketClose(ws);
  }

  async alarm() {
    this.rebuildRegistry();

    const now = Date.now();
    let removedAny = false;
    for (const ws of this.ctx.getWebSockets('pilot')) {
      const data = ws.deserializeAttachment();
      if (data?.lastSeen && now - data.lastSeen > STALE_SESSION_MS) {
        this.registry.remove(data.pilotId);
        ws.close(1000, 'stale session');
        removedAny = true;
      }
    }
    if (removedAny) this.broadcastToAdmins();

    await this.ctx.storage.setAlarm(Date.now() + SWEEP_INTERVAL_MS);
  }

  broadcastToAdmins() {
    const payload = JSON.stringify({ pilots: this.registry.list() });
    for (const ws of this.ctx.getWebSockets('admin')) {
      ws.send(payload);
    }
  }
}
```

- [ ] **Step 3: Commit**

```bash
git add DISPATCH-RADIO-WORKER/wrangler.toml DISPATCH-RADIO-WORKER/src/room.js
git commit -m "feat(dispatch-radio-worker): add DispatchRadioRoom Durable Object with hibernation-safe state"
```

(No automated test here — this file has no meaningful behavior to unit-test in isolation from a real Workers runtime. It's verified end-to-end in Task 6.)

---

## Task 5: The entry Worker (routing + admin key check)

**Files:**
- Create: `DISPATCH-RADIO-WORKER/src/worker.js`

- [ ] **Step 1: Implement the entry Worker**

Create `DISPATCH-RADIO-WORKER/src/worker.js`:

```js
export { DispatchRadioRoom } from './room.js';

export default {
  async fetch(request, env) {
    const url = new URL(request.url);

    if (url.pathname === '/health') {
      return new Response('ok', { status: 200 });
    }

    if (url.pathname === '/admin') {
      const key = url.searchParams.get('key');
      if (env.ADMIN_WS_KEY && key !== env.ADMIN_WS_KEY) {
        return new Response('Unauthorized', { status: 401 });
      }
    } else if (url.pathname !== '/pilot') {
      return new Response('Not found', { status: 404 });
    }

    const id = env.DISPATCH_ROOM.idFromName('global');
    const stub = env.DISPATCH_ROOM.get(id);
    return stub.fetch(request);
  },
};
```

- [ ] **Step 2: Verify the Worker boots locally**

Run: `cd DISPATCH-RADIO-WORKER && npx wrangler dev --port 8787 &` (or use your tool's background-run capability), wait a couple seconds for it to start, then:

Run: `curl http://localhost:8787/health`
Expected: `ok`

Stop the dev server afterward (kill the background process).

If `wrangler dev` fails to start with a Durable Object binding/migration error, the `wrangler.toml` from Task 4 has a mistake — fix it there, not here.

- [ ] **Step 3: Commit**

```bash
git add DISPATCH-RADIO-WORKER/src/worker.js
git commit -m "feat(dispatch-radio-worker): add entry Worker routing and admin key gate"
```

---

## Task 6: Local end-to-end verification

**Files:**
- Create (temporary, not committed): a local verification script

This task has no unit tests to write — it's a scripted integration check against the real local Workers runtime (`wrangler dev`), the same technique used to verify the Node.js backend in Phase 1. Nothing here touches deployed/production Cloudflare infrastructure.

- [ ] **Step 1: Install a WebSocket client for the verification script**

Run: `cd DISPATCH-RADIO-WORKER && npm install -D ws`

- [ ] **Step 2: Write the verification script**

Create `DISPATCH-RADIO-WORKER/verify-local.mjs` (temporary — delete after use, do not commit it):

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
  return new Promise((resolve) => ws.once('message', (d) => resolve(JSON.parse(d.toString()))));
}

async function main() {
  const results = [];

  const admin = new WebSocket(`ws://localhost:${PORT}/admin?key=${KEY}`);
  await waitForOpen(admin);
  const initialSnapshot = await waitForMessage(admin);
  results.push(['admin connects with valid key + gets initial snapshot', Array.isArray(initialSnapshot.pilots) && initialSnapshot.pilots.length === 0]);

  const badAdmin = new WebSocket(`ws://localhost:${PORT}/admin?key=wrong`);
  const badAdminRejected = await new Promise((resolve) => {
    badAdmin.once('unexpected-response', (req, res) => resolve(res.statusCode === 401));
    badAdmin.once('error', () => resolve(true));
    setTimeout(() => resolve(false), 2000);
  });
  results.push(['admin with wrong key is rejected', badAdminRejected]);

  const pilot = new WebSocket(`ws://localhost:${PORT}/pilot`);
  await waitForOpen(pilot);
  const adminUpdate1Promise = waitForMessage(admin);
  pilot.send(JSON.stringify({ pilotId: 'e2e-pilot', callsign: 'AVI999', lat: 52.8311, lon: -1.3281, freq: '123.575' }));
  const pilotReply1 = await waitForMessage(pilot);
  results.push(['pilot at EGNX gets state=clear', pilotReply1.state === 'clear']);
  results.push(['pilot at EGNX gets distanceNm < 1', pilotReply1.distanceNm < 1]);

  const adminUpdate1 = await adminUpdate1Promise;
  results.push(['admin feed reflects the pilot (clear)', adminUpdate1.pilots.length === 1 && adminUpdate1.pilots[0].callsign === 'AVI999' && adminUpdate1.pilots[0].state === 'clear']);

  const adminUpdate2Promise = waitForMessage(admin);
  pilot.send(JSON.stringify({ pilotId: 'e2e-pilot', callsign: 'AVI999', lat: 61.0, lon: -1.3281, freq: '123.575' }));
  const pilotReply2 = await waitForMessage(pilot);
  results.push(['pilot teleported >400nm gets state=acars_only', pilotReply2.state === 'acars_only']);
  results.push(['distanceNm now > 400', pilotReply2.distanceNm > 400]);

  const adminUpdate2 = await adminUpdate2Promise;
  results.push(['admin feed reflects updated state (acars_only)', adminUpdate2.pilots[0].state === 'acars_only']);

  const adminUpdate3Promise = waitForMessage(admin);
  pilot.close();
  const adminUpdate3 = await adminUpdate3Promise;
  results.push(['admin feed drops pilot on disconnect', adminUpdate3.pilots.length === 0]);

  const malformedTest = await new Promise((resolve) => {
    const p2 = new WebSocket(`ws://localhost:${PORT}/pilot`);
    p2.once('open', () => {
      p2.send('null');
      p2.send('42');
      p2.send(JSON.stringify({ pilotId: 'e2e-pilot-2', callsign: 'AVI888', lat: 52.8311, lon: -1.3281, freq: '123.575' }));
      p2.once('message', (d) => resolve(JSON.parse(d.toString()).state === 'clear'));
      setTimeout(() => resolve(false), 3000);
    });
  });
  results.push(['malformed payloads do not crash the server', malformedTest]);

  admin.close();

  console.log('\n=== VERIFICATION RESULTS ===');
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

- [ ] **Step 3: Run the verification**

Run: `cd DISPATCH-RADIO-WORKER && npx wrangler dev --port 8787 --var ADMIN_WS_KEY:local-verify-key` in the background, wait for it to report ready, then:

(Note: the Worker reads `env.ADMIN_WS_KEY` from Cloudflare's env binding, not from `process.env` — a shell-level `ADMIN_WS_KEY=...` prefix would do nothing here. The `--var` flag is how `wrangler dev` injects a local-only value for that binding without touching `wrangler.toml` or requiring an authenticated account.)

Run: `cd DISPATCH-RADIO-WORKER && node verify-local.mjs`
Expected: `ALL CHECKS PASSED`, exit code 0.

Stop the `wrangler dev` process afterward.

- [ ] **Step 4: Delete the temporary verification script**

```bash
rm DISPATCH-RADIO-WORKER/verify-local.mjs
```

**Known limitation, worth stating plainly rather than glossing over:** this verification exercises the full protocol against the real local Workers runtime, but it does not — and cannot easily — force an actual hibernation-evict-and-reload cycle to prove `rebuildRegistry()` works under real hibernation. That specific property rests on following Cloudflare's documented hibernation pattern correctly (verified via research during design, not via a local test), and will be implicitly exercised the first time a real deployed instance goes quiet for a while between pilots. If something's ever wrong with it, the symptom would be: pilot list on the admin page unexpectedly resets to empty even though pilots are still connected and sending updates.

- [ ] **Step 5: Commit the ws devDependency**

```bash
git add DISPATCH-RADIO-WORKER/package.json DISPATCH-RADIO-WORKER/package-lock.json
git commit -m "chore(dispatch-radio-worker): add ws as a devDependency for local verification scripts"
```

---

## Task 7: Remove the superseded Node.js backend

**Files:**
- Delete: `VOICE-BACKEND/` (entire directory)

- [ ] **Step 1: Delete the old backend**

```bash
git rm -r VOICE-BACKEND
```

- [ ] **Step 2: Commit**

```bash
git commit -m "chore: remove Node.js/Oracle dispatch backend, superseded by DISPATCH-RADIO-WORKER"
```

---

## Task 8: Point the Phase 1 spec at its replacement

**Files:**
- Modify: `docs/superpowers/specs/2026-07-12-dispatch-radio-phase1-design.md`

- [ ] **Step 1: Add a superseded notice**

At the very top of `docs/superpowers/specs/2026-07-12-dispatch-radio-phase1-design.md`, immediately after the `**Scope:**` line, add:

```markdown

> **Superseded:** the backend described here (Node.js on an Oracle Cloud VM) was replaced by a Cloudflare Worker + Durable Object — see `docs/superpowers/specs/2026-07-13-dispatch-radio-cloudflare-backend-design.md`. The protocol, distance/threshold logic, and client/admin-page integration described below are unchanged and still accurate; only the "Backend host" decision and the Oracle deployment steps no longer apply.
```

- [ ] **Step 2: Commit**

```bash
git add docs/superpowers/specs/2026-07-12-dispatch-radio-phase1-design.md
git commit -m "docs: mark phase 1 backend hosting section as superseded by the Cloudflare port"
```

---

## Explicitly Not Part of This Plan

- `wrangler login` (Cloudflare account authentication) — a one-time interactive step for the user to run, or to run together with explicit confirmation.
- `wrangler secret put ADMIN_WS_KEY` and `wrangler deploy` — real deployment actions against live Cloudflare infrastructure. Do not run these as part of executing this plan. Once this plan's tasks are complete and verified locally, stop and ask the user before running either.
- Updating `AviatesAirTracker`'s `DispatchBackendUrl` setting or `AviatesAirWebsite`'s `NEXT_PUBLIC_DISPATCH_ADMIN_WS_*` env vars to point at the deployed Worker — that only makes sense after an actual deployment has happened.
