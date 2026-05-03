# Backend Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add three missing endpoints to `BACKEND-API/worker.js` so friend removal, live pilot position reporting, and the public position read all work end-to-end.

**Architecture:** All three changes are additions to the single `worker.js` Cloudflare Worker. The GET endpoint slots into the top-level route dispatch (unauthenticated, before the v1 gate). The DELETE stub and POST position handler are both added inside `handleV1Api` (authenticated via Bearer ACARS key). Positions are stored as a single JSON manifest in the existing `ACARS_KV` namespace with a 15-minute TTL — no Supabase changes needed.

**Tech Stack:** Cloudflare Workers (ES Modules), Cloudflare KV (`ACARS_KV`), Web Crypto API (SHA-256 for pilot key hashing). No C# changes, no Supabase migrations.

---

## Files

| File | Change |
|---|---|
| `BACKEND-API/worker.js` | Add GET `/api/acars/live-positions` at top-level dispatch (line ~211), add `DELETE /api/v1/friends/:pilotId` stub + `POST /api/v1/acars/position` inside `handleV1Api` (before line 1191) |

`AcarsPositionService.cs` — read-only verification only. Already fully implemented, no edits required.

---

## Task 1: Add public live-positions read endpoint

**Files:**
- Modify: `BACKEND-API/worker.js` — top-level route dispatch, before line 211

This is a public GET that reads the `live_positions` KV manifest and returns all pilots active within the last 15 minutes. It goes in the top-level `fetch` handler, before the `/api/v1/` dispatch block, so it requires no ACARS key and the website can call it freely.

- [ ] **Step 1: Add the route**

In `BACKEND-API/worker.js`, find this block (around line 208–213):

```javascript
    if (pathname.startsWith("/api/public")) {
      return handlePublicApi(request, env, corsHeaders, ctx);
    }
    if (pathname.startsWith("/api/v1/")) {
      return handleV1Api(request, env, ctx, url, pathname, corsHeaders);
    }
```

Replace it with:

```javascript
    if (pathname.startsWith("/api/public")) {
      return handlePublicApi(request, env, corsHeaders, ctx);
    }
    if (pathname === "/api/acars/live-positions") {
      if (request.method !== "GET") return json({ error: "Method not allowed" }, 405, corsHeaders);
      if (!env.ACARS_KV) return json({ positions: [] }, 200, corsHeaders);
      const raw = await env.ACARS_KV.get("live_positions").catch(() => null);
      if (!raw) return json({ positions: [] }, 200, corsHeaders);
      const manifest = JSON.parse(raw);
      const cutoff   = Date.now() - 15 * 60 * 1000;
      const positions = Object.entries(manifest)
        .filter(([, v]) => new Date(v.updated_at).getTime() > cutoff)
        .map(([id, v]) => ({ id, ...v }));
      return json({ positions }, 200, corsHeaders);
    }
    if (pathname.startsWith("/api/v1/")) {
      return handleV1Api(request, env, ctx, url, pathname, corsHeaders);
    }
```

- [ ] **Step 2: Commit**

```bash
git add BACKEND-API/worker.js
git commit -m "feat(worker): add GET /api/acars/live-positions public endpoint"
```

---

## Task 2: Add DELETE /api/v1/friends/:pilotId stub

**Files:**
- Modify: `BACKEND-API/worker.js` — inside `handleV1Api`, before line 1191

Auth-gated stub. Returns 200 OK immediately — no server-side state, `JsonFriendRepository` is the source of truth. This stops `RemoveFriendAsync` in the C# client from throwing a silent network error.

- [ ] **Step 1: Add the stub**

In `BACKEND-API/worker.js`, find the final return inside `handleV1Api` (around line 1191):

```javascript
  return json({ error: "Not found" }, 404, corsHeaders);
}
```

Insert the stub **immediately before** that line:

```javascript
  // ── DELETE /api/v1/friends/:pilotId ──────────────────────────────────────
  const friendsDeleteMatch = pathname.match(/^\/api\/v1\/friends\/([^/]+)$/);
  if (request.method === "DELETE" && friendsDeleteMatch) {
    const principal = await verifyAcarsKey(env, acarsKey);
    if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);
    return json({ success: true }, 200, corsHeaders);
  }

  return json({ error: "Not found" }, 404, corsHeaders);
}
```

- [ ] **Step 2: Commit**

```bash
git add BACKEND-API/worker.js
git commit -m "feat(worker): add DELETE /api/v1/friends/:pilotId auth-gated stub"
```

---

## Task 3: Add POST /api/v1/acars/position

**Files:**
- Modify: `BACKEND-API/worker.js` — inside `handleV1Api`, before the friends delete block added in Task 2

Reads the `live_positions` KV manifest, upserts the pilot entry (keyed by 8-byte SHA-256 hash of their ACARS key so raw keys are never in the public manifest), and writes back with a 15-minute TTL. The C# client (`AviatesBackendClient.SendPositionReportAsync`) already sends `{ latitude, longitude, altitude, speed, phase, timestamp }` with a Bearer header — this handler consumes exactly that shape.

- [ ] **Step 1: Add the position write handler**

In `BACKEND-API/worker.js`, find the friends delete block you added in Task 2:

```javascript
  // ── DELETE /api/v1/friends/:pilotId ──────────────────────────────────────
  const friendsDeleteMatch = pathname.match(/^\/api\/v1\/friends\/([^/]+)$/);
```

Insert the position handler **immediately before** it:

```javascript
  // ── POST /api/v1/acars/position ──────────────────────────────────────────
  if (request.method === "POST" && pathname === "/api/v1/acars/position") {
    const principal = await verifyAcarsKey(env, acarsKey);
    if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

    const body = await safeJson(request);
    if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

    const lat   = parseFloat(body.latitude)     || 0;
    const lon   = parseFloat(body.longitude)    || 0;
    const alt   = parseInt(body.altitude, 10)   || 0;
    const speed = parseInt(body.speed, 10)      || 0;
    const phase = (body.phase || "unknown").substring(0, 30);

    // Hash the ACARS key so raw keys are never stored in the public manifest
    const keyBuf   = await crypto.subtle.digest("SHA-256", new TextEncoder().encode("POS:" + acarsKey));
    const pilotKey = Array.from(new Uint8Array(keyBuf)).slice(0, 8).map(b => b.toString(16).padStart(2, "0")).join("");

    const raw      = await env.ACARS_KV.get("live_positions").catch(() => null);
    const manifest = raw ? JSON.parse(raw) : {};

    manifest[pilotKey] = {
      name:       `${principal.user.first_name} ${principal.user.last_name}`.trim(),
      lat, lon, alt, speed, phase,
      updated_at: new Date().toISOString(),
    };

    await env.ACARS_KV.put("live_positions", JSON.stringify(manifest), { expirationTtl: 900 });
    return json({ success: true }, 200, corsHeaders);
  }

  // ── DELETE /api/v1/friends/:pilotId ──────────────────────────────────────
  const friendsDeleteMatch = pathname.match(/^\/api\/v1\/friends\/([^/]+)$/);
```

- [ ] **Step 2: Commit**

```bash
git add BACKEND-API/worker.js
git commit -m "feat(worker): add POST /api/v1/acars/position KV manifest write"
```

---

## Task 4: Deploy and verify

**Files:** None — deploy + smoke test only.

The Cloudflare Worker is deployed via the Cloudflare dashboard (no `wrangler.toml` present in repo). Replace `YOUR_ACARS_KEY` below with the key from the app's Settings page.

- [ ] **Step 1: Deploy `worker.js` to Cloudflare**

Open the Cloudflare dashboard → Workers & Pages → your worker → Edit code → paste the updated `worker.js` → Save and Deploy.

Or, if `wrangler` CLI is configured locally:
```bash
cd BACKEND-API && wrangler deploy
```

- [ ] **Step 2: Smoke test — live-positions (empty, no pilots online)**

```powershell
Invoke-RestMethod -Uri "https://acars.flyaviatesair.uk/api/acars/live-positions" -Method GET | ConvertTo-Json
```

Expected response:
```json
{ "positions": [] }
```

- [ ] **Step 3: Smoke test — POST position (write a position)**

```powershell
$headers = @{ Authorization = "Bearer YOUR_ACARS_KEY"; "Content-Type" = "application/json" }
$body = '{"latitude":51.47,"longitude":-0.46,"altitude":35000,"speed":450,"phase":"Cruise"}'
Invoke-RestMethod -Uri "https://acars.flyaviatesair.uk/api/v1/acars/position" -Method POST -Headers $headers -Body $body | ConvertTo-Json
```

Expected response:
```json
{ "success": true }
```

- [ ] **Step 4: Smoke test — live-positions (should now show the pilot)**

```powershell
Invoke-RestMethod -Uri "https://acars.flyaviatesair.uk/api/acars/live-positions" -Method GET | ConvertTo-Json -Depth 5
```

Expected response (fields match what was posted):
```json
{
  "positions": [
    {
      "id": "<8-byte hex hash>",
      "name": "Your Name",
      "lat": 51.47,
      "lon": -0.46,
      "alt": 35000,
      "speed": 450,
      "phase": "Cruise",
      "updated_at": "<ISO timestamp>"
    }
  ]
}
```

- [ ] **Step 5: Smoke test — DELETE friend (stub)**

```powershell
$headers = @{ Authorization = "Bearer YOUR_ACARS_KEY" }
Invoke-RestMethod -Uri "https://acars.flyaviatesair.uk/api/v1/friends/some-pilot-id" -Method DELETE -Headers $headers | ConvertTo-Json
```

Expected response:
```json
{ "success": true }
```

- [ ] **Step 6: Commit verification note**

```bash
git commit --allow-empty -m "chore: verify backend integration endpoints live on Cloudflare"
```
