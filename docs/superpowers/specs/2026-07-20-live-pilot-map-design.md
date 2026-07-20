# Live Pilot Map — Design Spec

**Date:** 2026-07-20
**Status:** Approved, ready for implementation planning

## Summary

Add a live map showing pilots currently flying on Aviates Air, visible both on the public website (a dedicated page + homepage teaser) and inside the desktop app's existing map page. Clicking a pilot shows their details (speed, altitude, heading, departure/destination, route/waypoints) and a Message button. On the app, Message adds the pilot as a friend and jumps into a conversation with them. On the website, Message redirects to `/login` (the website has no messaging UI).

## Current State (research findings)

- A minimal live-position pipeline already exists: the app POSTs `{lat, lon, alt, speed, phase}` to `/api/v1/acars/position` every 5 minutes while airborne; the worker stores it in a KV blob (`live_positions`, 15-min TTL) keyed by an anonymous hash of the ACARS key; `GET /api/acars/live-positions` reads it back. **No consumer uses this today** — `GET /api/public/stats` hardcodes `currentlyFlying: 0`.
- Correction to an earlier research pass: `worker.js` (`C:\AviatesAirTracker\BACKEND-API\worker.js`) is the **only** copy — there is no second copy in the website repo. Verified via direct file search in `C:\AviatesAirWebsite` (only `node_modules` hits, nothing under version control, no `wrangler.toml` anywhere in that repo). The "Open implementation notes" section below is updated accordingly.
- The position payload has no flight ID, callsign, dep/dest, or route/waypoints, and its anonymous pilot-key hash (`SHA-256("POS:"+acarsKey)`) does **not** match the hash messaging uses for `PilotId` (`SHA-256("AVIATES_ID:"+email)`) — a map pilot can't be matched to a messageable identity without a backend change.
- No Supabase table stores live position or in-flight state — the only "live" state is the ephemeral KV blob above.
- `DISPATCH-RADIO-WORKER` already runs a Durable Object (`DispatchRadioRoom`) with hibernatable WebSockets, an in-memory session registry, and a stale-session sweep via `ctx.storage.alarm()` — a proven low-cost realtime pattern in this codebase, though currently scoped only to dispatch-radio distance/voice relay.
- The app's `MapPage.razor` + `wwwroot/js/interop.js` (`window.aviatesMap`) render exactly one aircraft: marker, flown path, and planned route are all singleton fields, not keyed by pilot.
- `RouteTracker.cs` (flown path + SimBrief waypoints) is entirely local — never uploaded to the backend.
- Messaging (`MessagingService.cs`, `Messages.razor`) is friend-code gated: `SendDirectAsync` requires a `recipientId` that today only comes from a friend already added via a 9-character Friend Code exchange. There's no existing path to message an arbitrary pilot without that exchange.
- The website (Next.js 15, App Router) has a static-route 3D globe (`globe.gl`) on `/routes` showing scheduled route arcs — visual reference, but no live layer and no calls to the live-position endpoints anywhere in the codebase today. Login flow (`AuthContext`, `/login`) is functional and reusable as the Message-button redirect target.

## Backend constraint driving the architecture

Cloudflare Workers free tier caps KV writes at **1,000/day**. The existing 5-minute-interval position pings already consume part of that budget; a 30-second cadence (needed for a "reasonably live" feel) would blow through it with even a handful of multi-hour flights if left on KV.

## Architecture

**New Durable Object: `LiveMapRoom`** (single global instance, same instantiation pattern as `DISPATCH_ROOM` — `env.LIVE_MAP_ROOM.idFromName('global')`).

Holds an in-memory registry keyed by `pilotId` (using the **same** `computePilotId` hash messaging already uses, fixing today's identity mismatch), each entry shaped:

```
{
  pilotId, pilotName, lat, lon, altitude, speed, heading, phase,
  originIata, destIata, callsign,
  waypoints: [{ lat, lon, id }] | null,
  updatedAt
}
```

Storing and reading through the DO (instead of KV) removes this feature from the KV daily-write quota entirely — DO in-memory/SQLite storage isn't subject to that cap, and Worker request volume at this endpoint stays well within the 100k/day free request quota.

### Endpoints

- **`POST /api/v1/acars/flight-start`** (new) — sent once on the Parked→Taxi phase transition, and again if the active SimBrief plan changes mid-flight. Payload: `originIata, destIata, callsign, waypoints`. Registers/updates the pilot's flight metadata in `LiveMapRoom`.
- **`POST /api/v1/acars/position`** (existing route, extended payload, interval shortened from 5 min to 30s) — `{lat, lon, altitude, speed, heading, phase}` ticks. Routed to `LiveMapRoom` instead of KV.
- **`POST /api/v1/acars/flight-end`** (new) — sent on landing/session teardown, or immediately if the pilot toggles live-sharing off mid-flight. Removes the pilot from the room.
- **`GET /api/acars/live-positions`** (existing route, now served from `LiveMapRoom`'s in-memory snapshot instead of KV) — returns `{ positions: [...] }` per the shape above. Public, unauthenticated, matches current access. Pilots with live-sharing disabled are excluded server-side, not just hidden client-side.

**Staleness:** `LiveMapRoom` self-sweeps entries with no update in 90 seconds (covers app crash / network loss), independent of explicit `flight-end` calls.

**Rate limiting:** new endpoints reuse the worker's existing per-isolate in-memory rate-limit buckets (not KV-backed), consistent with how other high-frequency endpoints are protected today.

## App changes (AviatesAirTracker)

- **Settings.razor:** new toggle "Share my live position on the map" (default **on**). Turning it off while a flight is active immediately fires `flight-end`; while off, `AcarsPositionService` skips `flight-start`/`position` entirely.
- **AcarsPositionService:** interval drops to 30s while airborne/taxi; calls `flight-start` on Parked→Taxi (and on SimBrief plan changes), `flight-end` on landing/session end or opt-out toggle.
- **MapPage.razor / interop.js:** add a second, dictionary-keyed (`pilotId` → marker/path state) layer for other pilots, visually distinct from the local aircraft (different icon/dimmed style). Polls `GET /api/acars/live-positions` every 30s and diffs markers in/out. If a pilot has `waypoints`, draw their route as a dashed line using the same rendering path already built for the local pilot's SimBrief route.
- **Click interaction:** clicking another pilot's marker opens a details panel (styled like the existing HUD panels) — name, speed, altitude, heading, departure/destination, route line.
- **Message button:** calls a new "quick-add by pilotId" path in `MessagingService` (adds the clicked pilot as a friend without requiring their Friend Code — an intentional relaxation of the existing code-exchange gate, confirmed acceptable by the user), then reuses the existing `SelectFriendAndSwitchToInbox` flow to land directly in a conversation with them on the Messages page.

## Website changes (AviatesAirWebsite)

- **New public page `/live-map`** (under `src/app/(main)/`), added to the public nav. 2D Leaflet map (matching the app's map styling, not the 3D globe used on `/routes`), polling `GET /api/acars/live-positions` every 30s. No auth required.
- **Click interaction:** same details panel shape as the app (name, speed, altitude, heading, dep/dest, route line if present).
- **Message button:** always routes to `/login`, regardless of the visitor's auth state — there is no messaging UI on the website today.
- **Homepage teaser widget:** small "N pilots flying now" widget on the public homepage, linking through to `/live-map`. Shows a "No pilots currently flying" state when the count is zero (same empty state used on the full page).

## Error handling

- Failed position POSTs (worker unreachable) retry silently on the next 30s tick — no user-facing error, matches current behavior.
- Failed `GET /api/acars/live-positions` polls (app or website) keep showing the last-known snapshot with a small "live data unavailable" indicator, rather than clearing the map or showing an error dialog.
- Stale pilots (crash/network loss, no explicit `flight-end`) are removed by `LiveMapRoom`'s 90-second sweep.

## Testing

No automated test suite exists in either repo (per project convention) — verification is manual:
1. Fly a test session in the app; confirm `flight-start`/`position`/`flight-end` fire at the correct phase transitions and that the Settings opt-out toggle behaves (including mid-flight toggle-off).
2. Simulate a second pilot (manual POST to the position/flight-start endpoints) to verify the app's multi-pilot map layer, click → details panel, and Message → auto-friend → Messages-page flow end-to-end.
3. Verify the website's `/live-map` page and homepage teaser widget render, poll, and update against the same live data, including the zero-pilots empty state.

## Open implementation notes for the plan

- `worker.js` (`BACKEND-API/worker.js`, single source of truth — no second copy exists) is deployed **manually** by the user via the Cloudflare dashboard's code editor. Editing the repo file does not deploy it; the user must paste the updated file into the dashboard and redeploy themselves.
- Introducing the new `LiveMapRoom` Durable Object requires a one-time manual step in the Cloudflare dashboard: adding a Durable Object Namespace binding (name `LIVE_MAP_ROOM`, class `LiveMapRoom`) with a "new class" migration. This cannot be scripted from the repo since there is no `wrangler.toml` for this worker — bindings are dashboard-managed.
- `computePilotId` must be reused (not reimplemented) for the position payload's `pilotId` so it lines up with the messaging system's existing identity.
- The privacy opt-out is enforced via a KV flag (reusing `ACARS_KV`, keyed by `pilotId`) rather than a new Supabase column — it's a low-frequency write (once per toggle, not per tick), avoids a production schema migration, and is checked server-side on every `flight-start`/`position` write so a modified client can't bypass it.
