# Dispatch Radio Backend — Cloudflare Workers Port — Design Spec
**Date:** 2026-07-13
**Scope:** Replace the Node.js/Oracle-VM backend built in Phase 1 (`docs/superpowers/specs/2026-07-12-dispatch-radio-phase1-design.md`) with a Cloudflare Worker + Durable Object, so the dispatch radio system can run on a genuinely free tier that doesn't require billing/card details. The wire protocol is unchanged — this is a hosting swap, not a redesign.

---

## Background

Phase 1 shipped a working Node.js backend (`VOICE-BACKEND/`) meant to run on an Oracle Cloud free-tier VM. Oracle (like AWS, GCP, and Azure) requires a credit card for identity verification even on its always-free tier, which the user is not willing to provide. Cloudflare Workers' free plan requires no card and now supports SQLite-backed Durable Objects on that free plan, which fits this workload well: a Durable Object is exactly Cloudflare's pattern for a single stateful coordination point holding a live WebSocket registry (the same pattern used for chat rooms/presence systems).

**Constraint carried over from a prior incident:** a previous Wrangler deploy wiped environment variables and bindings on the existing `BACKEND-API` worker. This design keeps the new backend as a completely separate Cloudflare Worker, isolated from `BACKEND-API` in every way — separate `wrangler.toml`, separate deploy target, separate secrets. No command run for this feature touches `BACKEND-API`'s configuration. Any actual `wrangler login` or `wrangler deploy` execution requires the user's explicit go-ahead each time; nothing here runs against live Cloudflare infrastructure without that confirmation.

## What Doesn't Change

- **Protocol:** identical to Phase 1. Pilot → server: `{ pilotId, callsign, lat, lon, freq }` over `/pilot`. Server → pilot: `{ state, distanceNm }`. Server → admin sockets over `/admin?key=...`: `{ pilots: [{ pilotId, callsign, distanceNm, state }] }`.
- **Distance/classification logic:** haversine great-circle distance from EGNX (52.8311, -1.3281), bucketed into `clear` (<200nm) / `degraded` (200–400nm) / `acars_only` (>400nm).
- **Stale session sweep:** sessions with no update in 15s are dropped and reported to admins as disconnected.
- **Admin key gating:** a shared-secret query-string key protects the `/admin` feed, same as Phase 1.
- **Client and admin-page code:** `AviatesAirTracker/Services/DispatchRadioService.cs` and `AviatesAirWebsite/src/app/admin/dispatch/page.tsx` need **zero code changes** — only the `DispatchBackendUrl` setting and the `NEXT_PUBLIC_DISPATCH_ADMIN_WS_URL`/`NEXT_PUBLIC_DISPATCH_ADMIN_WS_KEY` env vars need to point at the new Worker's URL once deployed.

## What Changes

### 1. Repository layout
- Delete `VOICE-BACKEND/` (the Node.js/Oracle implementation — fully superseded, git history preserves it).
- New folder `DISPATCH-RADIO-WORKER/` at the repo root (sibling to `BACKEND-API/`), containing:
  - `wrangler.toml` — `name = "aviates-dispatch-radio"`, a Worker completely separate from `BACKEND-API`.
  - `src/distance.js` — ported verbatim from Phase 1 (pure JS, no Node-specific APIs, so no changes needed beyond the file move).
  - `src/registry.js` — ported with one change: instead of being instantiated once per Node process, its logic lives inside the Durable Object instance's in-memory state (see below).
  - `src/room.js` — the `DispatchRadioRoom` Durable Object class.
  - `src/worker.js` — the entry Worker: routes `/health`, `/pilot`, `/admin` to the single Durable Object instance.
  - `test/*.test.js` — plain Vitest tests (no Workers-specific test pool needed) covering `distance.js`/`registry.js`, the same scenarios as Phase 1's Node test suite. The `room.js`/`worker.js` Durable Object integration behavior is verified separately via `wrangler dev` plus a scripted WebSocket client (see Testing Plan) rather than an automated test-framework harness.

### 2. Durable Object design
- **Single global instance:** the entry Worker always resolves the same Durable Object ID via `idFromName("global")` — one dispatch station, one registry, matching Phase 1's single-EGNX-station design. No per-tenant sharding needed for this scale.
- **In-memory only, no persistent storage:** the registry is ephemeral live-pilot state (mirrors Phase 1's design intent — a dropped connection just means the pilot reconnects and re-reports). The Durable Object's SQLite storage backend is required by Cloudflare's platform (new DO classes must declare a storage backend) but the room class doesn't write anything to it; state lives in a plain in-memory `Map` for the object's lifetime, matching Phase 1's `SessionRegistry` class almost exactly.
- **WebSocket Hibernation API:** the room uses `state.acceptWebSocket(ws, tags)` plus the DO class's `webSocketMessage(ws, message)` / `webSocketClose(ws, code, reason)` handlers, instead of Phase 1's `ws.on('message', ...)` event-listener style. This is Cloudflare's recommended pattern for long-lived WebSocket connections — it lets the object hibernate (no compute charged) while sockets stay open, and is the idiomatic way to write this on Workers rather than a stylistic choice.
- Pilot sockets and admin sockets are tagged (via hibernation tags, e.g. `"pilot:<pilotId>"` / `"admin"`) so the room can tell them apart and broadcast to all admin-tagged sockets on any registry change, exactly like Phase 1's `adminSockets` Set did.

### 3. Entry Worker routing
- `GET /health` → returns `"ok"` directly from the entry Worker (no need to hit the Durable Object for a liveness check).
- `GET /pilot` (WebSocket upgrade) → forwarded to the Durable Object, which accepts the hibernatable WebSocket.
- `GET /admin?key=...` (WebSocket upgrade) → the entry Worker checks the key against the `ADMIN_WS_KEY` secret **before** forwarding to the Durable Object (same gating point as Phase 1's admin key check), so a bad key never reaches the room.

### 4. Secrets and configuration
- `ADMIN_WS_KEY` is set via `wrangler secret put ADMIN_WS_KEY` (not a plain `vars` entry) — consistent with treating it as sensitive, and avoids it ever being visible in `wrangler.toml` or dashboard plaintext config.
- `wrangler.toml` declares the Durable Object binding/migration (`[[migrations]]` with `new_sqlite_classes = ["DispatchRadioRoom"]`) and the `[[durable_objects.bindings]]` entry pointing the entry Worker at the room class.

## Error Handling

Unchanged from Phase 1's intent, adapted to the new runtime:
- **Malformed pilot payload:** the room validates the parsed message is a non-null object with a `pilotId` before processing (same guard as the Phase 1 fix for the `null`/bare-number crash) — a bad frame is ignored, not fatal.
- **Bad admin key:** entry Worker rejects the upgrade before it reaches the Durable Object (equivalent to Phase 1's `ws.close(1008, ...)` rejection, but even earlier in the pipeline).
- **Stale sessions:** swept on a periodic Durable Object alarm (`state.storage.setAlarm(...)`, Cloudflare's mechanism for "wake me up later" inside a hibernating object) firing roughly every 5s, mirroring Phase 1's `setInterval` sweep.

## Testing Plan

- Unit tests for `distance.js`/`registry.js` logic — same test cases as Phase 1, run under plain Vitest (these files have no Workers-runtime dependencies, so no specialized test pool is needed).
- Integration check: connect a pilot and an admin WebSocket to a `wrangler dev`-hosted local instance via a small scripted client, send a position update, assert the `clear`/`degraded`/`acars_only` classification and admin broadcast — same scenarios as Phase 1's `server.test.js`, but run as a one-off verification script rather than an automated `vitest-pool-workers` suite, since that tool's WebSocket/Durable-Object testing API is newer and less certain to get exactly right than proven-by-execution local verification.
- No automated test or verification script ever runs against real deployed Cloudflare infrastructure — everything runs against the local Workers runtime (`workerd`) that `wrangler dev` spins up.
- One known gap: local verification cannot easily force an actual hibernation-evict-and-reload cycle, so the Durable Object's hibernation-safe state rebuild (see Durable Object design, above) is verified by following Cloudflare's documented pattern correctly rather than by a local test proving it. This gets implicitly exercised once real traffic hits a deployed instance and it goes quiet between pilots.
- Manual end-to-end verification (once actually deployed, with the user's explicit go-ahead) mirrors Phase 1's Task 11: tune COM1 to 123.575 near EGNX, confirm "clear" on the admin page; reposition >400nm away, confirm "acars_only".

## Explicitly Out of Scope

- Any change to `BACKEND-API/worker.js` or its deployment.
- Any change to the dispatch radio protocol, thresholds, or client/admin-page code.
- Actually running `wrangler login` or `wrangler deploy` as part of this spec/plan — those are deploy actions requiring explicit user confirmation at execution time, not something a plan pre-authorizes.
- Voice audio and Hoppies ACARS integration (still separate, later specs, per Phase 1's spec).
