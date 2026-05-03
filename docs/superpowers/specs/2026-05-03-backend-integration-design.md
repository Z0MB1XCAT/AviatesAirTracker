# Backend Integration Design
_2026-05-03_

## Context

The BACKLOG listed FEAT-A (SQLite) and several backend stubs as unimplemented. A codebase audit revealed the situation is far better than the backlog suggested:

- Local persistence already exists via `JsonFlightRepository`, `JsonLandingRepository`, `JsonFriendRepository`, `JsonMessageRepository` — all registered in DI, persisting to `%AppData%\AviatesAirTracker\`.
- `AviatesBackendClient.cs` HTTP code is fully written for all methods.
- PIREP submission is wired end-to-end: `FlightSessionManager.OnFlightComplete` → `SubmitPirepAsync` + startup retry loop in `App.xaml.cs`.
- Messaging (send/receive/poll) is fully wired via `MessagingService`.
- Stats (`get_pilot_stats` Supabase RPC) exists and matches `RemotePilotStats` exactly.

**Only two backend endpoints are missing from `worker.js`:**
1. `DELETE /api/v1/friends/:pilotId` — friend removal stub
2. `POST /api/v1/acars/position` — live position reporting
3. `GET /api/acars/live-positions` — public read for website/app map

No SQLite migration, no Supabase migrations, no C# changes required.

---

## Changes

### 1. Friends Delete Stub — `handleV1Api`

Add before the final `404` return in `handleV1Api`:

```javascript
const friendsMatch = pathname.match(/^\/api\/v1\/friends\/([^/]+)$/);
if (request.method === "DELETE" && friendsMatch) {
  const principal = await verifyAcarsKey(env, acarsKey);
  if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);
  return json({ success: true }, 200, corsHeaders);
}
```

- Auth-gated (Bearer ACARS key required).
- No server-side state. The local `JsonFriendRepository` remains the source of truth.
- Stops `RemoveFriendAsync` from silently failing with a network error.

### 2. Live Position Write — `handleV1Api`

`POST /api/v1/acars/position` — authenticated.

- Reads `live_positions` manifest key from `ACARS_KV`.
- Merges the pilot's entry (keyed by 8-byte SHA-256 hash of their ACARS key — raw key never exposed).
- Writes back with `expirationTtl: 900` (15 minutes).
- Input: `{ latitude, longitude, altitude, speed, phase }` — matches `AviatesBackendClient.SendPositionReportAsync` exactly.
- Client self-throttles to once per 5 minutes (enforced in `SendPositionReportAsync` via `_lastPositionReport`).

Pilot entry shape in manifest:
```json
{
  "name": "Dexter Rees",
  "lat": 51.47,
  "lon": -0.46,
  "alt": 35000,
  "speed": 450,
  "phase": "Cruise",
  "updated_at": "2026-05-03T22:00:00.000Z"
}
```

### 3. Live Position Read — top-level routing

`GET /api/acars/live-positions` — public, no auth.

- Added to top-level route dispatch (before the `handleV1Api` path) so the website can call it without credentials.
- Guards against `env.ACARS_KV` being undefined (returns empty array), then reads manifest, filters to entries with `updated_at` within last 15 minutes.
- Returns `{ positions: [{ id, name, lat, lon, alt, speed, phase, updated_at }, ...] }`.
- `id` is the hashed pilot key (safe to expose publicly).

---

## What Does Not Change

| Item | Status |
|---|---|
| `JsonFlightRepository` / `JsonLandingRepository` | Already active in DI |
| `SubmitPirepAsync` | Fully wired, Supabase `pireps` table ready |
| `FetchStatsAsync` | Works — `get_pilot_stats` RPC verified |
| `ValidateRouteAsync` | Works — endpoint exists in worker |
| `SendMessageAsync` / `FetchInboxAsync` / `FetchBroadcastsAsync` | Fully wired via `MessagingService` |
| `ResolveFriendCodeAsync` | Works |
| Supabase schema | No migrations needed |
| C# client code | No changes needed |

---

## Implementation Notes

- `ACARS_KV` binding is already declared in `wrangler.toml` — no new bindings needed.
- `AcarsPositionService.cs` is registered and resolved at startup — verify during implementation that it wires `TelemetryUpdated` → `SendPositionReportAsync` correctly.
- The KV manifest TTL (900s) means the entire `live_positions` key expires if no pilot sends a report for 15 minutes. This is intentional — stale data auto-clears.
- Race condition risk on manifest read/merge/write is negligible at this VA's scale (<50 concurrent pilots ever).
- When a public position map is built for the website, the `GET /api/acars/live-positions` endpoint is ready to consume with no further backend changes.
