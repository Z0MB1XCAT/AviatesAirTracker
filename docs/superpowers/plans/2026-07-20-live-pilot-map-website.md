# Live Pilot Map — Website Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show the live pilot map on the public website, with a details panel and a Message button that sends visitors to `/login`, plus a small "N pilots flying now" teaser on the homepage.

**Important discovery that shapes this plan:** `src/app/(main)/activity/page.tsx` (routed as "Network Activity" in the nav, `C:\AviatesAirWebsite\src\components\layout\DiscoverDropdown.tsx`) already has a dedicated "Live Map — Pilot Positions" card with a "Live tracking coming soon... once the AviatesAir ACARS app is live" placeholder overlay (`ACARS_PENDING = true` gates it). This plan replaces that placeholder with a real Leaflet map — it does **not** create a new page or a new nav link, since the slot and copy already exist for exactly this purpose.

**Scope note:** The same `/activity` page also has "Currently Flying" / "Flying Today" stat cards and a per-fleet "pilots currently flying" breakdown, all driven by `fetchPublicStats()` (`GET /api/public/stats`), which still hardcodes those specific fields to `0` server-side (confirmed in `BACKEND-API/worker.js`). Wiring those numbers to the real live-map data would require changing that endpoint's 10-minute response cache to not stale-cache a fast-changing count — a real design change beyond what was asked. This plan leaves `ACARS_PENDING` and those stat cards untouched and only replaces the map card's content, so those specific numbers will keep reading "ACARS app pending" even though the map above them is live. Worth a follow-up later, not folded into this plan.

**Architecture:** A `LiveMapSection` client component polls the existing public `GET /api/acars/live-positions` endpoint every 30 seconds and owns the selected-pilot state. It renders `LiveMapWrapper` (a `next/dynamic(..., { ssr: false })` wrapper, mirroring the existing `GlobeWrapper`/`GlobeInner` split used for the `/routes` page's 3D globe) around `LiveMapInner`, which imperatively drives a plain Leaflet map (matching the 2D map style already used in the desktop app, per the earlier design decision) — markers for each pilot, a dashed route line where waypoints exist, and a click callback. Clicking a pilot shows `LiveMapPilotPanel` with their details and a Message button that always links to `/login`.

**Tech Stack:** Next.js 15 (App Router), React 19, TypeScript, Tailwind CSS v4, plain `leaflet` (not `react-leaflet` — this project already has a precedent for imperative, non-React-bound map/globe libraries via `globe.gl`, and 30-second marker diffing is simpler to do imperatively than through React reconciliation).

**A note on testing approach:** This repo has no automated test suite (no Jest/Vitest in `package.json`, no test files found). Its own `CLAUDE.md` documents a manual screenshot-verification workflow (`node serve.mjs` + `node screenshot.mjs`) — this plan uses `npm run build` (real TypeScript + Next.js compilation, catches real errors) after each code task, and Task 6 uses the project's own screenshot tooling for a final visual check, consistent with how this repo is actually verified elsewhere.

---

## Task 1: Dependency, types, and API client function

**Files:**
- Modify: `package.json`
- Create: `src/types/liveMap.ts`
- Modify: `src/lib/api.ts`

- [ ] **Step 1: Install Leaflet**

Run: `cd C:\AviatesAirWebsite && npm install leaflet && npm install -D @types/leaflet`
Expected: `leaflet` added to `dependencies`, `@types/leaflet` added to `devDependencies` in `package.json`, `package-lock.json` updated.

- [ ] **Step 2: Add the live-position types**

Create `src/types/liveMap.ts`:

```ts
export interface LiveWaypoint {
  lat: number;
  lon: number;
  id: string;
}

export interface LivePilotPosition {
  pilotId: string;
  pilotName: string;
  lat: number;
  lon: number;
  alt: number;
  speed: number;
  heading: number;
  phase: string;
  departureIcao?: string;
  arrivalIcao?: string;
  callsign?: string;
  waypoints?: LiveWaypoint[];
  updatedAt: string;
}
```

- [ ] **Step 3: Add `fetchLivePositions` to the API client**

In `src/lib/api.ts`, add this import to the top of the file (alongside the existing type imports):

```ts
import type { LivePilotPosition } from '@/types/liveMap';
```

Add this function at the end of the file, after `disconnectDiscord`:

```ts
// ── Live map ─────────────────────────────────────────────────────────────

export async function fetchLivePositions(): Promise<LivePilotPosition[]> {
  try {
    const res = await fetch(`${WORKER_URL}/api/acars/live-positions`, { cache: 'no-store' });
    if (!res.ok) return [];
    const data = await res.json();
    return (data.positions ?? []) as LivePilotPosition[];
  } catch {
    return [];
  }
}
```

This follows the same defensive, never-throw pattern as `fetchBookingRoutes`/`fetchRegistrations` in the same file — the live map should degrade to "no pilots shown" on any network hiccup, not show an error page.

- [ ] **Step 4: Build check**

Run: `cd C:\AviatesAirWebsite && npm run build`
Expected: build succeeds (this only adds a type file and one unused-so-far function, so no behavior changes yet).

- [ ] **Step 5: Commit**

```bash
git add package.json package-lock.json src/types/liveMap.ts src/lib/api.ts
git commit -m "feat(web): add leaflet dependency, live position types, and API client function"
```

---

## Task 2: `LiveMapInner` — the imperative Leaflet map

**Files:**
- Create: `src/components/activity/LiveMapInner.tsx`

- [ ] **Step 1: Create the component**

Create `src/components/activity/LiveMapInner.tsx`:

```tsx
'use client';

import { useEffect, useRef } from 'react';
import L from 'leaflet';
import 'leaflet/dist/leaflet.css';
import type { LivePilotPosition } from '@/types/liveMap';

interface Props {
  positions: LivePilotPosition[];
  onPilotClick: (pilot: LivePilotPosition) => void;
}

export default function LiveMapInner({ positions, onPilotClick }: Props) {
  const containerRef = useRef<HTMLDivElement>(null);
  const mapRef = useRef<L.Map | null>(null);
  const markersRef = useRef<Map<string, L.CircleMarker>>(new Map());
  const routesRef = useRef<Map<string, L.Polyline>>(new Map());
  const onPilotClickRef = useRef(onPilotClick);
  onPilotClickRef.current = onPilotClick;

  // Map init/teardown — runs once. This component only ever mounts client-side
  // (its parent wraps it in next/dynamic with ssr: false), so touching `window`
  // and `document` here at module scope and in this effect is safe.
  useEffect(() => {
    if (!containerRef.current || mapRef.current) return;

    const map = L.map(containerRef.current, {
      center: [51.5, -0.1],
      zoom: 3,
      zoomControl: false,
      attributionControl: false,
    });
    L.tileLayer('https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png', {
      subdomains: 'abcd',
      maxZoom: 19,
    }).addTo(map);
    mapRef.current = map;

    return () => {
      map.remove();
      mapRef.current = null;
    };
  }, []);

  // Marker/route diffing — runs whenever the polled position list changes.
  useEffect(() => {
    const map = mapRef.current;
    if (!map) return;

    const currentIds = new Set(positions.map(p => p.pilotId));

    for (const [id, marker] of markersRef.current) {
      if (!currentIds.has(id)) {
        marker.remove();
        markersRef.current.delete(id);
      }
    }
    for (const [id, route] of routesRef.current) {
      if (!currentIds.has(id)) {
        route.remove();
        routesRef.current.delete(id);
      }
    }

    for (const pos of positions) {
      let marker = markersRef.current.get(pos.pilotId);
      if (!marker) {
        marker = L.circleMarker([pos.lat, pos.lon], {
          radius: 6,
          color: '#c4973e',
          fillColor: '#c4973e',
          fillOpacity: 0.85,
          weight: 2,
        }).addTo(map);
        marker.on('click', () => onPilotClickRef.current(pos));
        markersRef.current.set(pos.pilotId, marker);
      } else {
        marker.setLatLng([pos.lat, pos.lon]);
      }

      let route = routesRef.current.get(pos.pilotId);
      if (pos.waypoints && pos.waypoints.length > 0) {
        const latlngs: [number, number][] = pos.waypoints.map(w => [w.lat, w.lon]);
        if (!route) {
          route = L.polyline(latlngs, { color: '#c4973e', weight: 1.5, opacity: 0.4, dashArray: '4,5' }).addTo(map);
          routesRef.current.set(pos.pilotId, route);
        } else {
          route.setLatLngs(latlngs);
        }
      } else if (route) {
        route.remove();
        routesRef.current.delete(pos.pilotId);
      }
    }
  }, [positions]);

  return <div ref={containerRef} className="activity-map-canvas" />;
}
```

- [ ] **Step 2: Build check**

Run: `cd C:\AviatesAirWebsite && npm run build`
Expected: build succeeds. If you see a type error on `L.Map`/`L.CircleMarker`/`L.Polyline`, confirm `@types/leaflet` installed correctly in Task 1.

- [ ] **Step 3: Commit**

```bash
git add src/components/activity/LiveMapInner.tsx
git commit -m "feat(web): add imperative Leaflet map component for live pilot positions"
```

---

## Task 3: `LiveMapWrapper` — the client-only dynamic import boundary

**Files:**
- Create: `src/components/activity/LiveMapWrapper.tsx`

- [ ] **Step 1: Create the wrapper**

Create `src/components/activity/LiveMapWrapper.tsx`, mirroring `src/components/routes/GlobeWrapper.tsx`'s exact pattern:

```tsx
'use client';

import dynamic from 'next/dynamic';
import type { LivePilotPosition } from '@/types/liveMap';

const LiveMapInner = dynamic(() => import('./LiveMapInner'), { ssr: false });

interface Props {
  positions: LivePilotPosition[];
  onPilotClick: (pilot: LivePilotPosition) => void;
}

export default function LiveMapWrapper(props: Props) {
  return <LiveMapInner {...props} />;
}
```

- [ ] **Step 2: Build check**

Run: `cd C:\AviatesAirWebsite && npm run build`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/components/activity/LiveMapWrapper.tsx
git commit -m "feat(web): add dynamic ssr:false wrapper for the live map"
```

---

## Task 4: `LiveMapPilotPanel` — details panel with the Message button

**Files:**
- Create: `src/components/activity/LiveMapPilotPanel.tsx`

- [ ] **Step 1: Create the panel**

Create `src/components/activity/LiveMapPilotPanel.tsx`:

```tsx
'use client';

import Link from 'next/link';
import type { LivePilotPosition } from '@/types/liveMap';

interface Props {
  pilot: LivePilotPosition;
  onClose: () => void;
}

export default function LiveMapPilotPanel({ pilot, onClose }: Props) {
  return (
    <div className="activity-map-panel">
      <button className="activity-map-panel-close" onClick={onClose} aria-label="Close pilot details">×</button>
      <p className="activity-map-panel-name">{pilot.pilotName}</p>
      <p className="activity-map-panel-route">
        {pilot.departureIcao || '----'} → {pilot.arrivalIcao || '----'}
      </p>
      <div className="activity-map-panel-grid">
        <span>ALT</span><span>{pilot.alt.toLocaleString()} ft</span>
        <span>GS</span><span>{pilot.speed} kts</span>
        <span>HDG</span><span>{pilot.heading}°</span>
      </div>
      {/* Messaging isn't built on the website yet — always send visitors to log in,
          regardless of whether they already have a portal session. */}
      <Link href="/login" className="btn btn-primary activity-map-panel-msg">Message</Link>
    </div>
  );
}
```

- [ ] **Step 2: Build check**

Run: `cd C:\AviatesAirWebsite && npm run build`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/components/activity/LiveMapPilotPanel.tsx
git commit -m "feat(web): add live map pilot details panel with Message-to-login button"
```

---

## Task 5: `LiveMapSection` — polling, state, and wiring into the Activity page

**Files:**
- Create: `src/components/activity/LiveMapSection.tsx`
- Modify: `src/app/(main)/activity/page.tsx`
- Modify: `src/app/globals.css`

- [ ] **Step 1: Create the section component**

Create `src/components/activity/LiveMapSection.tsx`:

```tsx
'use client';

import { useCallback, useEffect, useState } from 'react';
import { fetchLivePositions } from '@/lib/api';
import type { LivePilotPosition } from '@/types/liveMap';
import LiveMapWrapper from './LiveMapWrapper';
import LiveMapPilotPanel from './LiveMapPilotPanel';

export default function LiveMapSection() {
  const [positions, setPositions] = useState<LivePilotPosition[]>([]);
  const [selected, setSelected] = useState<LivePilotPosition | null>(null);
  const [loaded, setLoaded] = useState(false);

  const poll = useCallback(async () => {
    const data = await fetchLivePositions();
    setPositions(data);
    setLoaded(true);
  }, []);

  useEffect(() => {
    poll();
    const id = setInterval(poll, 30000);
    return () => clearInterval(id);
  }, [poll]);

  // Keep the open panel's data fresh, and close it if that pilot has landed/vanished.
  useEffect(() => {
    if (!selected) return;
    setSelected(positions.find(p => p.pilotId === selected.pilotId) ?? null);
  }, [positions, selected]);

  return (
    <>
      <LiveMapWrapper positions={positions} onPilotClick={setSelected} />

      {loaded && positions.length === 0 && (
        <div className="activity-map-overlay">
          <div className="activity-map-pending">
            <div className="activity-map-pending-icon">
              <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
                <path d="M12 2L2 7l10 5 10-5-10-5z" /><path d="M2 17l10 5 10-5" /><path d="M2 12l10 5 10-5" />
              </svg>
            </div>
            <p className="activity-map-pending-title">No pilots currently flying</p>
            <p className="activity-map-pending-body">
              Check back soon — live positions appear here as pilots take off on the network.
            </p>
          </div>
        </div>
      )}

      {selected && <LiveMapPilotPanel pilot={selected} onClose={() => setSelected(null)} />}
    </>
  );
}
```

- [ ] **Step 2: Wire it into the Activity page**

In `src/app/(main)/activity/page.tsx`, add the import at the top:

```tsx
import LiveMapSection from '@/components/activity/LiveMapSection';
```

Find the "Live map" block:

```tsx
        <ScrollReveal delay={1} className="activity-map-card card">
          {/* SVG grid / chart backdrop */}
          <svg
            className="activity-map-grid"
            viewBox="0 0 800 400"
            preserveAspectRatio="xMidYMid slice"
            aria-hidden="true"
          >
            {/* Longitude lines */}
            {[0,1,2,3,4,5,6,7,8].map(i => (
              <line key={`v${i}`} x1={i * 100} y1="0" x2={i * 100} y2="400" stroke="rgba(255,255,255,0.04)" strokeWidth="1"/>
            ))}
            {/* Latitude lines */}
            {[0,1,2,3,4].map(i => (
              <line key={`h${i}`} x1="0" y1={i * 100} x2="800" y2={i * 100} stroke="rgba(255,255,255,0.04)" strokeWidth="1"/>
            ))}
            {/* Diagonal accent */}
            <line x1="0" y1="400" x2="800" y2="0" stroke="rgba(196,151,62,0.04)" strokeWidth="1"/>
            <line x1="0" y1="0" x2="800" y2="400" stroke="rgba(196,151,62,0.04)" strokeWidth="1"/>
            {/* Faint radar rings centered roughly on UK/Europe */}
            <circle cx="380" cy="180" r="60"  fill="none" stroke="rgba(196,151,62,0.06)" strokeWidth="1"/>
            <circle cx="380" cy="180" r="120" fill="none" stroke="rgba(196,151,62,0.04)" strokeWidth="1"/>
            <circle cx="380" cy="180" r="200" fill="none" stroke="rgba(196,151,62,0.03)" strokeWidth="1"/>
          </svg>

          {/* Pending overlay */}
          <div className="activity-map-overlay">
            <div className="activity-map-pending">
              <div className="activity-map-pending-icon">
                <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
                  <path d="M12 2L2 7l10 5 10-5-10-5z"/><path d="M2 17l10 5 10-5"/><path d="M2 12l10 5 10-5"/>
                </svg>
              </div>
              <p className="activity-map-pending-title">Live tracking coming soon</p>
              <p className="activity-map-pending-body">
                Pilot positions will appear here once the AviatesAir ACARS app is live.
                The map will update in real-time as pilots fly on the VATSIM network.
              </p>
            </div>
          </div>
        </ScrollReveal>
```

Replace it with:

```tsx
        <ScrollReveal delay={1} className="activity-map-card card">
          <LiveMapSection />
        </ScrollReveal>
```

This removes the now-unused SVG grid backdrop along with the pending overlay — `LiveMapSection` supplies its own empty state (reusing the same `activity-map-overlay`/`activity-map-pending` classes with different copy) when there are zero pilots flying.

- [ ] **Step 3: Add CSS for the map canvas and details panel**

In `src/app/globals.css`, find:

```css
.activity-map-pending-body { font-size: 13px; color: var(--text-dim); line-height: 1.65; }
```

Add immediately after it:

```css
/* Leaflet adds its own `leaflet-container` class directly onto this same div
   (it does not wrap it in a child element) — compound selector, not descendant. */
.activity-map-canvas { position: absolute; inset: 0; width: 100%; height: 100%; }
.activity-map-canvas.leaflet-container { background: #0a0d12; }

.activity-map-panel {
  position: absolute; top: 16px; right: 16px; z-index: 20;
  min-width: 190px; background: rgba(6,10,15,0.92);
  border: 1px solid var(--line); border-radius: 10px;
  padding: 14px 16px; backdrop-filter: blur(6px);
}
.activity-map-panel-close {
  position: absolute; top: 8px; right: 10px;
  background: none; border: none; color: var(--text-dim);
  font-size: 16px; line-height: 1; cursor: pointer; padding: 4px;
}
.activity-map-panel-close:hover { color: var(--text); }
.activity-map-panel-close:focus-visible { outline: 2px solid var(--gold-lt); outline-offset: 2px; }
.activity-map-panel-name { font-family: var(--font-head); font-size: 15px; font-weight: 700; color: var(--text); margin-bottom: 2px; padding-right: 20px; }
.activity-map-panel-route { font-size: 12px; color: var(--text-dim); margin-bottom: 10px; }
.activity-map-panel-grid {
  display: grid; grid-template-columns: auto 1fr; gap: 4px 10px;
  font-size: 12px; color: var(--text-dim); margin-bottom: 12px;
}
.activity-map-panel-grid span:nth-child(odd) { color: var(--muted); letter-spacing: 0.04em; }
.activity-map-panel-grid span:nth-child(even) { color: var(--text); font-weight: 600; text-align: right; }
.activity-map-panel-msg { display: flex; width: 100%; justify-content: center; text-decoration: none; }
```

- [ ] **Step 4: Build check**

Run: `cd C:\AviatesAirWebsite && npm run build`
Expected: build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/components/activity/LiveMapSection.tsx src/app/(main)/activity/page.tsx src/app/globals.css
git commit -m "feat(web): wire the live map into the Activity page's existing map card"
```

---

## Task 6: Homepage "N pilots flying now" teaser

**Files:**
- Create: `src/components/home/FlyingNowTeaser.tsx`
- Modify: `src/app/(main)/page.tsx`
- Modify: `src/app/globals.css`

- [ ] **Step 1: Create the teaser component**

Create `src/components/home/FlyingNowTeaser.tsx`:

```tsx
'use client';

import { useEffect, useState } from 'react';
import Link from 'next/link';
import { fetchLivePositions } from '@/lib/api';

export default function FlyingNowTeaser() {
  const [count, setCount] = useState<number | null>(null);

  useEffect(() => {
    let active = true;

    const poll = async () => {
      const positions = await fetchLivePositions();
      if (active) setCount(positions.length);
    };

    poll();
    const id = setInterval(poll, 30000);
    return () => { active = false; clearInterval(id); };
  }, []);

  const label = count === null
    ? 'Checking live flights…'
    : count === 0
      ? 'No pilots flying right now'
      : `${count} pilot${count === 1 ? '' : 's'} flying now`;

  return (
    <Link href="/activity" className="flying-now-teaser">
      <span className={`flying-now-dot${count ? ' flying-now-dot--active' : ''}`} />
      <span className="flying-now-label">{label}</span>
      <span className="flying-now-arrow" aria-hidden="true">→</span>
    </Link>
  );
}
```

- [ ] **Step 2: Insert it on the homepage**

In `src/app/(main)/page.tsx`, add the import at the top:

```tsx
import FlyingNowTeaser from '@/components/home/FlyingNowTeaser';
```

Find the boundary between the hero section and the About section:

```tsx
        </div>
      </section>

      <section className="container section">
        <div className="grid-two">
          <ScrollReveal>
            <p className="section-label">About</p>
```

Replace with:

```tsx
        </div>
      </section>

      <div className="container">
        <FlyingNowTeaser />
      </div>

      <section className="container section">
        <div className="grid-two">
          <ScrollReveal>
            <p className="section-label">About</p>
```

- [ ] **Step 3: Add CSS for the teaser**

In `src/app/globals.css`, add this near the `.activity-map-panel-msg` rule added in Task 5 (or any top-level location — it's a standalone component style):

```css
.flying-now-teaser {
  display: inline-flex; align-items: center; gap: 10px;
  margin: 24px 0; padding: 10px 18px;
  background: var(--bg-3); border: 1px solid var(--line); border-radius: 999px;
  font-size: 13px; color: var(--text-dim); text-decoration: none;
  transition: border-color 0.15s, background 0.15s;
}
.flying-now-teaser:hover { border-color: var(--gold-glow); background: var(--gold-dim); color: var(--text); }
.flying-now-teaser:focus-visible { outline: 2px solid var(--gold-lt); outline-offset: 2px; }
.flying-now-dot { width: 7px; height: 7px; border-radius: 50%; background: var(--text-dim); flex-shrink: 0; }
.flying-now-dot--active { background: #5fd97a; box-shadow: 0 0 0 3px rgba(95,217,122,0.18); }
.flying-now-arrow { transition: transform 0.15s; }
.flying-now-teaser:hover .flying-now-arrow { transform: translateX(2px); }
```

- [ ] **Step 4: Build check**

Run: `cd C:\AviatesAirWebsite && npm run build`
Expected: build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/components/home/FlyingNowTeaser.tsx src/app/(main)/page.tsx src/app/globals.css
git commit -m "feat(web): add homepage 'pilots flying now' teaser linking to the Activity page"
```

---

## Task 7: Manual visual verification (not automated — needs the dev server and a live backend)

**This task is not executed as part of this plan** — it needs the backend from `docs/superpowers/plans/2026-07-20-live-pilot-map-backend.md` actually deployed (or at least a couple of manual `curl` position posts against it, per that plan's Task 8) so there's real data to look at. Document for the user to run once ready, using this repo's own screenshot workflow from `CLAUDE.md`:

1. `node serve.mjs` (starts the dev server on `localhost:3000`).
2. `node screenshot.mjs http://localhost:3000` — check the new homepage teaser renders and its pilot count matches reality (0 if nothing is flying).
3. `node screenshot.mjs http://localhost:3000/activity live-map` — check the map renders in the existing map card, markers appear where expected, and clicking a marker (manually, in a real browser — screenshots won't capture the click interaction) opens the details panel with correct data and a working Message → `/login` link.
4. Confirm the "No pilots currently flying" empty state renders correctly when the feed is empty (stop sending test position data and re-check).

---

## Explicitly Not Part of This Plan

- Deploying the backend changes this plan depends on — a prerequisite, not something this plan does.
- Changing `/api/public/stats`'s `currentlyFlying`/`flyingToday`/`fleetActivity` fields or its 10-minute cache — see the "Scope note" above; this would need its own design decision about caching a fast-changing count and wasn't part of the original request.
- Deploying the website (Vercel) — this plan only verifies with `npm run build` and the local dev server.
- Task 7's manual visual verification — documented for the user, not run automatically, since it needs a live backend with real position data to look at.
