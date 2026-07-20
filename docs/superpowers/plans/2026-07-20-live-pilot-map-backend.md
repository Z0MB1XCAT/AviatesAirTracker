# Live Pilot Map — Backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the unused, minimal KV-backed `live_positions` blob with a `LiveMapRoom` Durable Object that carries richer per-pilot flight data (route/waypoints, dep/dest, a messaging-compatible pilot ID) at a 30-second cadence, without touching the free-tier 1,000-writes/day KV quota, plus a KV-backed opt-out flag for privacy.

**Architecture:** A single global `LiveMapRoom` Durable Object (SQLite-backed storage) holds an in-memory registry of currently-flying pilots, exposed to the rest of `worker.js` via internal `fetch()` calls (no WebSockets — plain request/response, since 30s polling doesn't need push). Three new/modified POST endpoints (`flight-start`, `position`, `flight-end`) write into it; the existing `GET /api/acars/live-positions` reads from it instead of KV. A new `PATCH /api/v1/acars/live-sharing` endpoint toggles a KV-backed opt-out flag that both new write endpoints check before registering a pilot.

**Tech Stack:** Cloudflare Workers (ES Module), Durable Objects (SQLite-backed), no build step — this file is deployed by pasting into the Cloudflare dashboard's code editor.

**A note on testing approach (deviation from the skill's default TDD structure):** `BACKEND-API/worker.js` is a single 3,237-line file with no test harness, no `wrangler.toml`, and no local dev setup anywhere in this repo — per `CLAUDE.md`, this project has no automated tests, and that has held for every one of this file's existing ~40 endpoints. Introducing a new test framework for only the new code in this one file, while the surrounding 3,237 lines remain untested, would be inconsistent and wouldn't actually be exercised against the real dependencies that matter here (`verifyAcarsKey` calls Supabase; there is no local Supabase stand-in). Each task below is instead verified with `node --check` (real syntax validation — catches typos/malformed code before it's pasted into the dashboard) plus an explicit manual review against the code shown. Task 8 documents exact manual smoke-test steps for the user to run against their own deployment, since that's the only way to exercise `verifyAcarsKey`/Supabase/the real Durable Object together.

**Also note:** deploying `worker.js` (pasting it into the Cloudflare dashboard) and creating the `LIVE_MAP_ROOM` Durable Object Namespace binding are manual, dashboard-only actions — see Task 8. Nothing in Tasks 1–7 deploys anything; they only edit the local file.

---

## Task 1: Add the `LiveMapRoom` Durable Object and its registry logic

**Files:**
- Modify: `BACKEND-API/worker.js` (add near the top, after the existing `const RESET_RATE_LIMIT_TTL_S` block around line 30, and add an `import` at the very top of the file)

- [ ] **Step 1: Add the `cloudflare:workers` import**

At the very top of `BACKEND-API/worker.js` (line 1, before the existing header comment block), add:

```js
import { DurableObject } from "cloudflare:workers";

```

- [ ] **Step 2: Add the registry class and Durable Object class**

Insert this block after the `const RESET_RATE_LIMIT_TTL_S = 2592000; // 30 days` line (around line 30 in the current file):

```js
// ============================================================
//  LIVE MAP — Durable Object (LiveMapRoom)
//
//  Holds live in-flight pilot positions in memory + SQLite storage.
//  Replaces the old KV-backed `live_positions` blob so a 30-second
//  update cadence doesn't count against the 1,000 writes/day KV quota
//  (Durable Object storage is not subject to that cap).
//
//  No WebSockets here — this is plain request/response via fetch(),
//  called internally from the main Worker. 30-second polling doesn't
//  need push, so there's no hibernation/reconnect complexity to manage.
// ============================================================

const LIVE_MAP_STALE_MS = 90 * 1000;
const LIVE_MAP_SWEEP_INTERVAL_MS = 30 * 1000;

class LiveMapRegistry {
  constructor(now = () => Date.now()) {
    this._now = now;
    this._pilots = new Map(); // pilotId -> record
  }

  flightStart(pilotId, meta) {
    const existing = this._pilots.get(pilotId) || {};
    this._pilots.set(pilotId, { ...existing, ...meta, pilotId, updatedAt: this._now() });
  }

  updatePosition(pilotId, pos) {
    const existing = this._pilots.get(pilotId) || { pilotId };
    this._pilots.set(pilotId, { ...existing, ...pos, updatedAt: this._now() });
  }

  remove(pilotId) {
    this._pilots.delete(pilotId);
  }

  sweepStale(maxAgeMs) {
    const cutoff = this._now() - maxAgeMs;
    for (const [pilotId, record] of this._pilots) {
      if (record.updatedAt < cutoff) this._pilots.delete(pilotId);
    }
  }

  list() {
    return Array.from(this._pilots.values());
  }

  snapshot() {
    return Array.from(this._pilots.entries());
  }

  hydrate(entries) {
    this._pilots = new Map(entries);
  }
}

export class LiveMapRoom extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    this.ctx = ctx;
    this.registry = new LiveMapRegistry();
    this.hydrated = false;
  }

  async ensureHydrated() {
    if (this.hydrated) return;
    const stored = await this.ctx.storage.get("pilots");
    if (stored) this.registry.hydrate(stored);
    this.hydrated = true;
    const alarm = await this.ctx.storage.getAlarm();
    if (alarm === null) {
      await this.ctx.storage.setAlarm(Date.now() + LIVE_MAP_SWEEP_INTERVAL_MS);
    }
  }

  async persist() {
    await this.ctx.storage.put("pilots", this.registry.snapshot());
  }

  async fetch(request) {
    await this.ensureHydrated();
    const url = new URL(request.url);

    if (request.method === "POST" && url.pathname === "/flight-start") {
      const body = await request.json();
      this.registry.flightStart(body.pilotId, body.meta);
      await this.persist();
      return new Response("ok");
    }

    if (request.method === "POST" && url.pathname === "/position") {
      const body = await request.json();
      this.registry.updatePosition(body.pilotId, body.pos);
      await this.persist();
      return new Response("ok");
    }

    if (request.method === "POST" && url.pathname === "/flight-end") {
      const body = await request.json();
      this.registry.remove(body.pilotId);
      await this.persist();
      return new Response("ok");
    }

    if (request.method === "GET" && url.pathname === "/list") {
      this.registry.sweepStale(LIVE_MAP_STALE_MS);
      await this.persist();
      return new Response(JSON.stringify(this.registry.list()), {
        headers: { "Content-Type": "application/json" },
      });
    }

    return new Response("Not found", { status: 404 });
  }

  async alarm() {
    await this.ensureHydrated();
    this.registry.sweepStale(LIVE_MAP_STALE_MS);
    await this.persist();
    await this.ctx.storage.setAlarm(Date.now() + LIVE_MAP_SWEEP_INTERVAL_MS);
  }
}

// Internal helper used by route handlers below to talk to the single
// global LiveMapRoom instance.
async function liveMapRoomFetch(env, path, init) {
  const id = env.LIVE_MAP_ROOM.idFromName("global");
  const stub = env.LIVE_MAP_ROOM.get(id);
  return stub.fetch(new Request("https://live-map-room" + path, init));
}

async function isLiveSharingOptedOut(env, pilotId) {
  if (!env.ACARS_KV) return false;
  const val = await env.ACARS_KV.get(`live_share_opt_out:${pilotId}`);
  return val === "true";
}
```

- [ ] **Step 3: Verify syntax**

Run: `node --check BACKEND-API/worker.js`
Expected: no output, exit code 0. (This only validates JavaScript syntax — it does not execute the file or touch Cloudflare/Supabase.)

- [ ] **Step 4: Commit**

```bash
git add BACKEND-API/worker.js
git commit -m "feat(backend): add LiveMapRoom Durable Object and registry logic"
```

---

## Task 2: Update the bindings header comment and add rate-limit rules

**Files:**
- Modify: `BACKEND-API/worker.js:1-13` (header comment) and the `LIMITS` object (currently lines ~19-32)

- [ ] **Step 1: Update the bindings comment**

Find the header comment block (now starting a couple of lines down, after the new `import`):

```js
// ============================================================
// AviatesAir — Cloudflare Worker (ES Module)
// Handles: auth, ACARS, messaging, AND the new /api/routes Supabase API
//
// Bindings required in wrangler.toml:
//   KV:      RATE_LIMIT_KV, MESSAGES_KV, FLIGHTS_KV, ACARS_KV,
//            SESSIONS_KV, PIREPS_KV, TELEMETRY_KV, ACARS_LOGS
//   Secrets: JWT_SECRET, RESEND_API_KEY, ADMIN_SECRET, UPLOAD_SECRET
//            SUPABASE_URL, SUPABASE_SERVICE_KEY, ADMIN_JWT_SECRET
//            DISCORD_BOT_TOKEN, DISCORD_CHANNEL_ID
//   Env:     ADMIN_JWT_EXPIRY (optional, default: 604800 = 7 days)
//
// D1 bindings (ROUTES_DB, FLEET_DB) are NO LONGER needed — data lives in Supabase.
// USERS_KV is NO LONGER needed — user auth reads/writes Supabase directly.
// ============================================================
```

Replace it with:

```js
// ============================================================
// AviatesAir — Cloudflare Worker (ES Module)
// Handles: auth, ACARS, messaging, AND the new /api/routes Supabase API
//
// Bindings required (configured in the Cloudflare dashboard — this
// worker has no wrangler.toml, so these are NOT auto-provisioned):
//   KV:              RATE_LIMIT_KV, MESSAGES_KV, FLIGHTS_KV, ACARS_KV,
//                    SESSIONS_KV, PIREPS_KV, TELEMETRY_KV, ACARS_LOGS
//   Durable Object:  LIVE_MAP_ROOM -> class LiveMapRoom (this file)
//                    Requires a "new class" migration the first time
//                    this binding is created in the dashboard.
//   Secrets:         JWT_SECRET, RESEND_API_KEY, ADMIN_SECRET, UPLOAD_SECRET
//                    SUPABASE_URL, SUPABASE_SERVICE_KEY, ADMIN_JWT_SECRET
//                    DISCORD_BOT_TOKEN, DISCORD_CHANNEL_ID
//   Env:             ADMIN_JWT_EXPIRY (optional, default: 604800 = 7 days)
//
// D1 bindings (ROUTES_DB, FLEET_DB) are NO LONGER needed — data lives in Supabase.
// USERS_KV is NO LONGER needed — user auth reads/writes Supabase directly.
// ============================================================
```

- [ ] **Step 2: Add new rate-limit rules**

Find the `LIMITS` object:

```js
const LIMITS = {
  signup:       { count: 3,   window: 60 },
  login:        { count: 5,   window: 60 },
  pwreset:      { count: 5,   window: 60 },
  acarsAuth:    { count: 10,  window: 60 },
  acarsSend:    { count: 60,  window: 60 },
  flightUpdate: { count: 120, window: 60 },
  messagesSend: { count: 30,  window: 60 },
  pirepSubmit:  { count: 20,  window: 60 },
  msgSend:      { count: 60,  window: 60 },
  resolve:      { count: 10,  window: 60 },
  manualPirep:  { count: 5,   window: 86400 },
  eventCreate:  { count: 5,   window: 600 },
};
```

Add three entries (`flightStart`, `flightEnd`, `liveSharing`) so it reads:

```js
const LIMITS = {
  signup:       { count: 3,   window: 60 },
  login:        { count: 5,   window: 60 },
  pwreset:      { count: 5,   window: 60 },
  acarsAuth:    { count: 10,  window: 60 },
  acarsSend:    { count: 60,  window: 60 },
  flightUpdate: { count: 120, window: 60 },
  flightStart:  { count: 20,  window: 60 },
  flightEnd:    { count: 20,  window: 60 },
  liveSharing:  { count: 10,  window: 60 },
  messagesSend: { count: 30,  window: 60 },
  pirepSubmit:  { count: 20,  window: 60 },
  msgSend:      { count: 60,  window: 60 },
  resolve:      { count: 10,  window: 60 },
  manualPirep:  { count: 5,   window: 86400 },
  eventCreate:  { count: 5,   window: 600 },
};
```

- [ ] **Step 3: Verify syntax**

Run: `node --check BACKEND-API/worker.js`
Expected: no output, exit code 0.

- [ ] **Step 4: Commit**

```bash
git add BACKEND-API/worker.js
git commit -m "chore(backend): document LIVE_MAP_ROOM binding, add rate limits for flight-start/end/live-sharing"
```

---

## Task 3: Add `POST /api/v1/acars/flight-start`

**Files:**
- Modify: `BACKEND-API/worker.js` — inside `handleV1Api`, immediately before the existing `POST /api/v1/acars/position` block (search for `// ── POST /api/v1/acars/position ──`)

- [ ] **Step 1: Add the endpoint**

Insert this block directly above the existing `// ── POST /api/v1/acars/position ──` comment:

```js
  // ── POST /api/v1/acars/flight-start ───────────────────────────────────────
  if (request.method === "POST" && pathname === "/api/v1/acars/flight-start") {
    if (!env.RATE_LIMIT_KV) return json({ success: false, error: "Rate limit KV not configured" }, 500, corsHeaders);

    const principal = await verifyAcarsKey(env, acarsKey);
    if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);
    if (!(await rateLimit(env, ctx, `flightstart:${acarsKey}`, LIMITS.flightStart)))
      return rateLimitResponse(corsHeaders);

    const body = await safeJson(request);
    if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

    const pilotId = await computePilotId(principal.user.email);
    if (await isLiveSharingOptedOut(env, pilotId))
      return json({ success: true, shared: false }, 200, corsHeaders);

    if (!env.LIVE_MAP_ROOM) return json({ success: false, error: "Live map not configured" }, 500, corsHeaders);

    // ICAO (4-letter), matching the convention already used by /api/v1/pireps'
    // departure_icao/arrival_icao and SimBriefFlightPlan.DepartureICAO/ArrivalICAO —
    // not IATA, which this codebase reserves for a pilot's home-base airport field.
    const departureIcao = (body.departure_icao || "").trim().toUpperCase().substring(0, 4);
    const arrivalIcao   = (body.arrival_icao   || "").trim().toUpperCase().substring(0, 4);
    const callsign      = (body.callsign       || "").trim().substring(0, 10);
    const waypoints     = Array.isArray(body.waypoints)
      ? body.waypoints.slice(0, 200).map(w => ({
          lat: parseFloat(w.lat) || 0,
          lon: parseFloat(w.lon) || 0,
          id:  String(w.id || "").substring(0, 10),
        }))
      : null;

    await liveMapRoomFetch(env, "/flight-start", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        pilotId,
        meta: {
          pilotName: `${principal.user.first_name} ${principal.user.last_name}`.trim(),
          departureIcao, arrivalIcao, callsign, waypoints,
        },
      }),
    });

    return json({ success: true }, 200, corsHeaders);
  }

```

Note this depends on `computePilotId`, which is already defined later in the file (function declarations are hoisted, so this works regardless of file order — but keep it visually close to the position endpoint since they're conceptually paired).

- [ ] **Step 2: Verify syntax**

Run: `node --check BACKEND-API/worker.js`
Expected: no output, exit code 0.

- [ ] **Step 3: Commit**

```bash
git add BACKEND-API/worker.js
git commit -m "feat(backend): add POST /api/v1/acars/flight-start endpoint"
```

---

## Task 4: Modify `POST /api/v1/acars/position` to route through the Durable Object

**Files:**
- Modify: `BACKEND-API/worker.js` — the existing `POST /api/v1/acars/position` block

- [ ] **Step 1: Replace the endpoint body**

Find the existing block:

```js
  // ── POST /api/v1/acars/position ──────────────────────────────────────────
  if (request.method === "POST" && pathname === "/api/v1/acars/position") {
    if (!env.RATE_LIMIT_KV) return json({ success: false, error: "Rate limit KV not configured" }, 500, corsHeaders);

    const principal = await verifyAcarsKey(env, acarsKey);
    if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);
    // acarsKey is guaranteed non-empty here — verifyAcarsKey returns null for empty keys
    if (!(await rateLimit(env, ctx, `pos:${acarsKey}`, LIMITS.flightUpdate)))
      return rateLimitResponse(corsHeaders);

    const body = await safeJson(request);
    if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

    if (!env.ACARS_KV) return json({ success: false, error: "Position KV not configured" }, 500, corsHeaders);

    const lat   = isFinite(parseFloat(body.latitude))  ? parseFloat(body.latitude)  : null;
    const lon   = isFinite(parseFloat(body.longitude)) ? parseFloat(body.longitude) : null;
    if (lat === null || lon === null)
      return json({ success: false, error: "Invalid position data" }, 400, corsHeaders);
    const alt   = parseInt(body.altitude, 10)   || 0;
    const speed = parseInt(body.speed, 10)      || 0;
    const phase = (body.phase || "unknown").substring(0, 30);

    // Hash the ACARS key so raw keys are never stored in the public manifest
    const keyBuf   = await crypto.subtle.digest("SHA-256", new TextEncoder().encode("POS:" + acarsKey));
    const pilotKey = Array.from(new Uint8Array(keyBuf)).slice(0, 8).map(b => b.toString(16).padStart(2, "0")).join("");

    // Single-key manifest: last-write-wins race is acceptable for this VA's scale (<50 concurrent pilots)
    const raw = await env.ACARS_KV.get("live_positions").catch(() => null);
    let manifest;
    try { manifest = raw ? JSON.parse(raw) : {}; } catch { manifest = {}; }

    manifest[pilotKey] = {
      name:       `${principal.user.first_name} ${principal.user.last_name}`.trim(),
      lat, lon, alt, speed, phase,
      updated_at: new Date().toISOString(),
    };

    await env.ACARS_KV.put("live_positions", JSON.stringify(manifest), { expirationTtl: 900 });
    return json({ success: true }, 200, corsHeaders);
  }
```

Replace it entirely with:

```js
  // ── POST /api/v1/acars/position ──────────────────────────────────────────
  if (request.method === "POST" && pathname === "/api/v1/acars/position") {
    if (!env.RATE_LIMIT_KV) return json({ success: false, error: "Rate limit KV not configured" }, 500, corsHeaders);

    const principal = await verifyAcarsKey(env, acarsKey);
    if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);
    // acarsKey is guaranteed non-empty here — verifyAcarsKey returns null for empty keys
    if (!(await rateLimit(env, ctx, `pos:${acarsKey}`, LIMITS.flightUpdate)))
      return rateLimitResponse(corsHeaders);

    const body = await safeJson(request);
    if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

    const lat = isFinite(parseFloat(body.latitude))  ? parseFloat(body.latitude)  : null;
    const lon = isFinite(parseFloat(body.longitude)) ? parseFloat(body.longitude) : null;
    if (lat === null || lon === null)
      return json({ success: false, error: "Invalid position data" }, 400, corsHeaders);
    const alt     = parseInt(body.altitude, 10) || 0;
    const speed   = parseInt(body.speed, 10)    || 0;
    const heading = parseInt(body.heading, 10)  || 0;
    const phase   = (body.phase || "unknown").substring(0, 30);

    const pilotId = await computePilotId(principal.user.email);
    if (await isLiveSharingOptedOut(env, pilotId))
      return json({ success: true, shared: false }, 200, corsHeaders);

    if (!env.LIVE_MAP_ROOM) return json({ success: false, error: "Live map not configured" }, 500, corsHeaders);

    await liveMapRoomFetch(env, "/position", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        pilotId,
        pos: {
          pilotName: `${principal.user.first_name} ${principal.user.last_name}`.trim(),
          lat, lon, alt, speed, heading, phase,
        },
      }),
    });

    return json({ success: true }, 200, corsHeaders);
  }
```

Note the behavior change: `pilotId` is now `computePilotId(email)` (matches messaging) instead of the old anonymous `SHA-256("POS:"+acarsKey)` hash, and `heading` is a new field. This is a breaking shape change to `live_positions`, which is safe because nothing currently consumes it (confirmed during research — `GET /api/public/stats` hardcodes zeros, no other backend or frontend code reads this endpoint).

- [ ] **Step 2: Verify syntax**

Run: `node --check BACKEND-API/worker.js`
Expected: no output, exit code 0.

- [ ] **Step 3: Commit**

```bash
git add BACKEND-API/worker.js
git commit -m "feat(backend): route position updates through LiveMapRoom, add heading, use messaging-compatible pilot ID"
```

---

## Task 5: Add `POST /api/v1/acars/flight-end`

**Files:**
- Modify: `BACKEND-API/worker.js` — inside `handleV1Api`, immediately after the (now-modified) `POST /api/v1/acars/position` block

- [ ] **Step 1: Add the endpoint**

Insert this block directly after the closing `}` of the `POST /api/v1/acars/position` block (and before the `// ── DELETE /api/v1/friends/:pilotId ──` comment):

```js
  // ── POST /api/v1/acars/flight-end ─────────────────────────────────────────
  if (request.method === "POST" && pathname === "/api/v1/acars/flight-end") {
    const principal = await verifyAcarsKey(env, acarsKey);
    if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);
    if (!(await rateLimit(env, ctx, `flightend:${acarsKey}`, LIMITS.flightEnd)))
      return rateLimitResponse(corsHeaders);

    if (!env.LIVE_MAP_ROOM) return json({ success: false, error: "Live map not configured" }, 500, corsHeaders);

    const pilotId = await computePilotId(principal.user.email);
    await liveMapRoomFetch(env, "/flight-end", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ pilotId }),
    });

    return json({ success: true }, 200, corsHeaders);
  }

```

- [ ] **Step 2: Verify syntax**

Run: `node --check BACKEND-API/worker.js`
Expected: no output, exit code 0.

- [ ] **Step 3: Commit**

```bash
git add BACKEND-API/worker.js
git commit -m "feat(backend): add POST /api/v1/acars/flight-end endpoint"
```

---

## Task 6: Add `PATCH /api/v1/acars/live-sharing` opt-out toggle

**Files:**
- Modify: `BACKEND-API/worker.js` — inside `handleV1Api`, immediately after the new `flight-end` block

- [ ] **Step 1: Add the endpoint**

Insert this block directly after the `flight-end` block from Task 5:

```js
  // ── PATCH /api/v1/acars/live-sharing ──────────────────────────────────────
  if (request.method === "PATCH" && pathname === "/api/v1/acars/live-sharing") {
    const principal = await verifyAcarsKey(env, acarsKey);
    if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);
    if (!(await rateLimit(env, ctx, `livesharing:${acarsKey}`, LIMITS.liveSharing)))
      return rateLimitResponse(corsHeaders);

    const body = await safeJson(request);
    if (!body || typeof body.enabled !== "boolean")
      return json({ success: false, error: "enabled (boolean) required" }, 400, corsHeaders);

    if (!env.ACARS_KV) return json({ success: false, error: "Position KV not configured" }, 500, corsHeaders);

    const pilotId = await computePilotId(principal.user.email);
    if (body.enabled) {
      await env.ACARS_KV.delete(`live_share_opt_out:${pilotId}`);
    } else {
      await env.ACARS_KV.put(`live_share_opt_out:${pilotId}`, "true");
      if (env.LIVE_MAP_ROOM) {
        await liveMapRoomFetch(env, "/flight-end", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ pilotId }),
        });
      }
    }

    return json({ success: true }, 200, corsHeaders);
  }

```

This immediately removes the pilot from the live map when they turn sharing off (not just stops future updates), matching the spec's requirement that toggling off mid-flight takes effect right away.

- [ ] **Step 2: Verify syntax**

Run: `node --check BACKEND-API/worker.js`
Expected: no output, exit code 0.

- [ ] **Step 3: Commit**

```bash
git add BACKEND-API/worker.js
git commit -m "feat(backend): add live-sharing opt-out toggle endpoint"
```

---

## Task 7: Modify `GET /api/acars/live-positions` to read from the Durable Object

**Files:**
- Modify: `BACKEND-API/worker.js:627-644` (approximate — the top-level `if (pathname === "/api/acars/live-positions")` block, not inside `handleV1Api`)

- [ ] **Step 1: Replace the handler**

Find:

```js
    if (pathname === "/api/acars/live-positions") {
      if (request.method !== "GET") return json({ error: "Method not allowed" }, 405, corsHeaders);
      if (!env.ACARS_KV) return json({ positions: [] }, 200, corsHeaders);
      const raw = await env.ACARS_KV.get("live_positions").catch(() => null);
      if (!raw) return json({ positions: [] }, 200, corsHeaders);
      let manifest;
      try {
        manifest = JSON.parse(raw);
      } catch {
        return json({ positions: [] }, 200, corsHeaders);
      }
      const cutoff   = Date.now() - 15 * 60 * 1000;
      // NaN from malformed updated_at compares false — entry silently excluded, which is safe
      const positions = Object.entries(manifest)
        .filter(([, v]) => new Date(v.updated_at).getTime() > cutoff)
        .map(([id, v]) => ({ id, ...v }));
      return json({ positions }, 200, corsHeaders);
    }
```

Replace it with:

```js
    if (pathname === "/api/acars/live-positions") {
      if (request.method !== "GET") return json({ error: "Method not allowed" }, 405, corsHeaders);
      if (!env.LIVE_MAP_ROOM) return json({ positions: [] }, 200, corsHeaders);
      const res = await liveMapRoomFetch(env, "/list", { method: "GET" }).catch(() => null);
      const positions = res ? await res.json().catch(() => []) : [];
      return json({ positions }, 200, corsHeaders);
    }
```

Note the response shape changes from `{id, name, lat, lon, alt, speed, phase, updated_at}` to `{pilotId, pilotName, lat, lon, alt, speed, heading, phase, departureIcao, arrivalIcao, callsign, waypoints, updatedAt}` — safe, since (as established during design) nothing consumes this endpoint today.

- [ ] **Step 2: Verify syntax**

Run: `node --check BACKEND-API/worker.js`
Expected: no output, exit code 0.

- [ ] **Step 3: Commit**

```bash
git add BACKEND-API/worker.js
git commit -m "feat(backend): serve GET /api/acars/live-positions from LiveMapRoom instead of KV"
```

---

## Task 8: Manual deployment and smoke test (user-run, not automated)

**This task is not executed as part of this plan.** It documents exactly what the user needs to do once they're ready to go live, since deployment here is a manual dashboard action (per standing project convention — see `CLAUDE.md` and prior sessions' notes on `worker.js`'s manual-upload workflow) and should only happen with explicit confirmation, not as a side effect of finishing a coding task.

**Deployment steps (for the user to run themselves):**

1. Open the `aviatesair-api` Worker in the Cloudflare dashboard and go to its code editor.
2. Paste the full updated contents of `BACKEND-API/worker.js`.
3. Before saving/deploying: go to **Settings → Bindings** (or **Durable Objects**, depending on dashboard version) and add a new binding:
   - Type: Durable Object Namespace
   - Variable name: `LIVE_MAP_ROOM`
   - Class: `LiveMapRoom`
   - Since this is the first time this class is deployed, the dashboard will prompt for a migration — choose "new class" (SQLite-backed storage).
4. Save and deploy.

**Smoke test (for the user to run themselves, with a real ACARS key), after deployment:**

```bash
# Replace YOUR_ACARS_KEY with a real key from your Settings page.
ACARS_KEY="YOUR_ACARS_KEY"
BASE="https://<your-worker-domain>"

# 1. Start a flight
curl -s -X POST "$BASE/api/v1/acars/flight-start" \
  -H "Authorization: Bearer $ACARS_KEY" -H "Content-Type: application/json" \
  -d '{"departure_icao":"KJFK","arrival_icao":"KLAX","callsign":"AVI001","waypoints":[{"lat":40.6,"lon":-73.8,"id":"KJFK"}]}'
# Expected: {"success":true}

# 2. Send a position tick
curl -s -X POST "$BASE/api/v1/acars/position" \
  -H "Authorization: Bearer $ACARS_KEY" -H "Content-Type: application/json" \
  -d '{"latitude":40.0,"longitude":-80.0,"altitude":35000,"speed":460,"heading":270,"phase":"cruise"}'
# Expected: {"success":true}

# 3. Confirm it shows up on the public feed
curl -s "$BASE/api/acars/live-positions"
# Expected: {"positions":[{"pilotId":"...","pilotName":"...","departureIcao":"KJFK","arrivalIcao":"KLAX", ...}]}

# 4. End the flight
curl -s -X POST "$BASE/api/v1/acars/flight-end" \
  -H "Authorization: Bearer $ACARS_KEY" -H "Content-Type: application/json"
# Expected: {"success":true}

# 5. Confirm it's gone
curl -s "$BASE/api/acars/live-positions"
# Expected: {"positions":[]}

# 6. Toggle opt-out and confirm a subsequent position POST is silently ignored
curl -s -X PATCH "$BASE/api/v1/acars/live-sharing" \
  -H "Authorization: Bearer $ACARS_KEY" -H "Content-Type: application/json" \
  -d '{"enabled":false}'
curl -s -X POST "$BASE/api/v1/acars/position" \
  -H "Authorization: Bearer $ACARS_KEY" -H "Content-Type: application/json" \
  -d '{"latitude":40.0,"longitude":-80.0,"altitude":35000,"speed":460,"heading":270,"phase":"cruise"}'
# Expected: {"success":true,"shared":false}
curl -s "$BASE/api/acars/live-positions"
# Expected: {"positions":[]}

# 7. Re-enable for future testing
curl -s -X PATCH "$BASE/api/v1/acars/live-sharing" \
  -H "Authorization: Bearer $ACARS_KEY" -H "Content-Type: application/json" \
  -d '{"enabled":true}'
```

---

## Explicitly Not Part of This Plan

- Pasting `worker.js` into the Cloudflare dashboard and deploying it — a manual action for the user, per the project's established manual-deploy workflow. Do not do this automatically; ask first if asked to help with it directly.
- Creating the `LIVE_MAP_ROOM` Durable Object Namespace binding in the dashboard — dashboard-only, cannot be scripted from this repo.
- Running the Task 8 smoke-test `curl` commands against the real deployed Worker — these hit live production infrastructure with a real ACARS key and should be run by the user, not automatically.
- Any Supabase schema change — this plan deliberately avoids one by keeping the opt-out flag in KV.
