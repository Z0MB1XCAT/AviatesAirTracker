import { DurableObject } from "cloudflare:workers";

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
//   Workers AI:      AI (Settings → Bindings → Add → Workers AI) — the AI
//                    dispatcher's main model. Optional env: WORKERS_AI_MODEL.
//                    OPENROUTER_API_KEY (+ OPENROUTER_MODEL) is the fallback.
//   Secrets:         JWT_SECRET, RESEND_API_KEY, ADMIN_SECRET, UPLOAD_SECRET
//                    SUPABASE_URL, SUPABASE_SERVICE_KEY, ADMIN_JWT_SECRET
//                    DISCORD_BOT_TOKEN, DISCORD_CHANNEL_ID
//   Env:             ADMIN_JWT_EXPIRY (optional, default: 604800 = 7 days)
//
// D1 bindings (ROUTES_DB, FLEET_DB) are NO LONGER needed — data lives in Supabase.
// USERS_KV is NO LONGER needed — user auth reads/writes Supabase directly.
// ============================================================

const PBKDF2_ITERS = 100000;
const DERIVED_BYTES = 32;
const ACARS_KEY_LEN = 28;

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
  friendAdd:    { count: 10,  window: 60 },
  flightReview: { count: 10,  window: 60 },
  chatStart:    { count: 5,   window: 600 },
  chatMessage:  { count: 30,  window: 60 },
  aiDispatchReply: { count: 60, window: 86400 },
};

const RESET_RATE_LIMIT_TTL_S = 2592000; // 30 days

// ── Pilot subsidiary selection ────────────────────────────────────────────────
// Same lowercase vocabulary as routes.fleet_group and aircraft_types.subsidiary
// (lowercased) — see the "add_pilot_subsidiary_selection" migration.
const SUBSIDIARIES = ["mainline", "cityhopper", "cargo"];
// Only a move TO cityhopper/cargo counts against this cap -- moving back to
// mainline is always free (see POST /api/portal/subsidiary's own comment).
const SUBSIDIARY_MONTHLY_MOVE_LIMIT = 2;

// Counts this pilot's moves to a non-mainline subsidiary so far in the current
// UTC calendar month, by querying subsidiary_change_log directly rather than
// maintaining a separate counter column -- always self-correcting across month
// boundaries with no reset job needed.
async function subsidiaryMovesThisMonth(env, acarsKey) {
  const monthStart = new Date();
  monthStart.setUTCDate(1);
  monthStart.setUTCHours(0, 0, 0, 0);
  return sbCount(env, "subsidiary_change_log",
    `acars_key=eq.${encodeURIComponent(acarsKey)}&to_subsidiary=neq.mainline&changed_at=gte.${encodeURIComponent(monthStart.toISOString())}`);
}

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

const AIRCRAFT_CRUISE_SPEEDS = {
  B736: 450,   // Boeing 737-600 (knots)
  B738: 455,   // Boeing 737-800 (knots)
  B739: 455,   // Boeing 737-900 (knots)
  B38M: 460,   // Boeing 737 MAX 8 (knots)
  B748: 490,   // Boeing 747-400 (knots)
  B77F: 485,   // Boeing 777-F (knots)
  A319: 450,   // Airbus A319 (knots)
  A320: 455,   // Airbus A320 (knots)
  A350: 488,   // Airbus A350 (knots)
  A380: 488,   // Airbus A380 (knots)
  ATR42: 275,  // ATR 42 (knots)
  ATR72: 280,  // ATR 72 (knots)
};

const RANKS = [
  { rank: 'Cadet',                min: 0,    max: 25   },
  { rank: 'Junior First Officer', min: 25,   max: 75   },
  { rank: 'First Officer',        min: 75,   max: 200  },
  { rank: 'Senior First Officer', min: 200,  max: 450  },
  { rank: 'Junior Captain',       min: 450,  max: 800  },
  { rank: 'Captain',              min: 800,  max: 1500 },
  { rank: 'Senior Captain',       min: 1500, max: null },
];

function computeRank(totalHours) {
  const h = Number(totalHours) || 0;
  for (let i = RANKS.length - 1; i >= 0; i--) {
    const tier = RANKS[i];
    if (h >= tier.min) {
      const pct = tier.max === null
        ? 100
        : Math.min(100, ((h - tier.min) / (tier.max - tier.min)) * 100);
      return { rank: tier.rank, rank_pct: Math.round(pct * 10) / 10 };
    }
  }
  return { rank: 'Cadet', rank_pct: 0 };
}

const DISCORD_RANK_ROLE_MAP = {
  'Cadet':                '1472288730910953768',
  'Junior First Officer': '1472289074160079032',
  'First Officer':        '1472289185250410832',
  'Senior First Officer': '1472289260852740117',
  'Junior Captain':       '1472289319631982696',
  'Captain':              '1472289415421497354',
  'Senior Captain':       '1472289568588959855',
};

const DISCORD_ALL_RANK_ROLE_IDS = Object.values(DISCORD_RANK_ROLE_MAP);

async function syncDiscordRank(env, discordId, acarsKey, newRank, firstName) {
  const roleId = DISCORD_RANK_ROLE_MAP[newRank];
  if (!roleId || !env.DISCORD_BOT_TOKEN || !env.DISCORD_GUILD_ID) return;

  const headers    = { 'Authorization': `Bot ${env.DISCORD_BOT_TOKEN}`, 'Content-Type': 'application/json' };
  const memberBase = `https://discord.com/api/v10/guilds/${env.DISCORD_GUILD_ID}/members/${discordId}`;

  // Remove all rank roles, then assign the correct one
  await Promise.allSettled(
    DISCORD_ALL_RANK_ROLE_IDS.map(id =>
      fetch(`${memberBase}/roles/${id}`, { method: 'DELETE', headers })
    )
  );
  await fetch(`${memberBase}/roles/${roleId}`, { method: 'PUT', headers });

  // Persist the new rank so future checks have a baseline
  await sbUpdate(env, 'users', `acars_key=eq.${encodeURIComponent(acarsKey)}`, { discord_rank: newRank });

  // DM the pilot
  try {
    const dmRes = await fetch('https://discord.com/api/v10/users/@me/channels', {
      method: 'POST', headers,
      body: JSON.stringify({ recipient_id: discordId }),
    });
    if (dmRes.ok) {
      const { id: dmChannelId } = await dmRes.json();
      await fetch(`https://discord.com/api/v10/channels/${dmChannelId}/messages`, {
        method: 'POST', headers,
        body: JSON.stringify({
          content: `✈️ Congratulations ${firstName}! You've been promoted to **${newRank}** at AviatesAir. Keep flying!`,
        }),
      });
    }
  } catch (e) {
    console.error('Discord DM failed:', e);
  }

  // Post public announcement
  try {
    await fetch(`https://discord.com/api/v10/channels/${env.DISCORD_PROMOTIONS_CHANNEL_ID}/messages`, {
      method: 'POST', headers,
      body: JSON.stringify({
        embeds: [{
          title:       'Pilot Promotion',
          description: `✈️ **${firstName}** has been promoted to **${newRank}**! Congratulations!`,
          color:       0x1a5276,
        }],
      }),
    });
  } catch (e) {
    console.error('Discord announcement failed:', e);
  }
}

// Formats a short flight-completion DM message.
function formatFlightCompletionDM(body, firstName) {
  const fn   = (body.flight_number  || '').trim() || 'Unknown';
  const dep  = (body.departure_icao || '').trim().toUpperCase();
  const arr  = (body.arrival_icao   || '').trim().toUpperCase();
  const mins = parseInt(body.block_minutes, 10) || 0;
  const h    = Math.floor(mins / 60);
  const m    = mins % 60;
  const block = h > 0 ? `${h}h ${m}m` : `${m}m`;
  const dist  = (parseInt(body.distance_nm, 10) || 0).toLocaleString('en-US');
  const score = Math.round(parseFloat(body.landing_score) || 0);
  const vs    = parseInt(body.landing_vs_fpm, 10) || 0;
  return (
    `✈️ **Flight Filed** — Hey ${firstName}!\n` +
    `**${fn}** · ${dep} → ${arr}\n` +
    `Block time: ${block} · ${dist} nm\n` +
    `Landing: ${score}/100 · ${vs} fpm`
  );
}

// Wraps fetch with a single retry on Discord 429 rate-limit responses.
async function discordFetch(url, options) {
  const res = await fetch(url, options);
  if (res.status === 429) {
    const retryAfter = parseFloat(res.headers.get('Retry-After') || '1');
    console.warn(`Discord rate-limited — retrying ${url} after ${retryAfter}s`);
    await new Promise(r => setTimeout(r, retryAfter * 1000));
    return fetch(url, options);
  }
  return res;
}

// Opens (or reuses) a DM channel to a Discord user and sends a message.
async function sendDiscordDm(env, discordId, content) {
  if (!env.DISCORD_BOT_TOKEN) return;
  const headers = {
    'Authorization': `Bot ${env.DISCORD_BOT_TOKEN}`,
    'Content-Type': 'application/json',
  };
  try {
    const dmRes = await discordFetch('https://discord.com/api/v10/users/@me/channels', {
      method: 'POST', headers,
      body: JSON.stringify({ recipient_id: discordId }),
    });
    if (!dmRes.ok) return;
    const { id: channelId } = await dmRes.json();
    await discordFetch(`https://discord.com/api/v10/channels/${channelId}/messages`, {
      method: 'POST', headers,
      body: JSON.stringify({ content }),
    });
  } catch (e) {
    console.error('Discord DM failed:', e);
  }
}

// ─── DISTANCE CALCULATION ───────────────────────────────────────────────────────
/**
 * Calculate great circle distance between two geographic points using haversine formula.
 * @param {number} lat1 - Latitude of point 1 (degrees, -90 to 90)
 * @param {number} lon1 - Longitude of point 1 (degrees, -180 to 180)
 * @param {number} lat2 - Latitude of point 2 (degrees, -90 to 90)
 * @param {number} lon2 - Longitude of point 2 (degrees, -180 to 180)
 * @returns {number} Distance in nautical miles (rounded to nearest integer)
 */
function calculateDistance(lat1, lon1, lat2, lon2) {
  // Validate inputs — return 0 if any are non-finite
  if (!isFinite(lat1) || !isFinite(lon1) || !isFinite(lat2) || !isFinite(lon2)) {
    return 0;
  }

  const R = 3440.065; // Earth's radius in nautical miles
  const dLat = (lat2 - lat1) * (Math.PI / 180);
  const dLon = (lon2 - lon1) * (Math.PI / 180);
  const a = Math.sin(dLat / 2) * Math.sin(dLat / 2) +
    Math.cos(lat1 * (Math.PI / 180)) * Math.cos(lat2 * (Math.PI / 180)) *
    Math.sin(dLon / 2) * Math.sin(dLon / 2);
  const c = 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
  return Math.round(R * c);
}

// ─── AIRPORT DATA FETCHING ──────────────────────────────────────────────────────
// Fetch airport data from OpenFlights database and cache in KV
// Returns { latitude, longitude } or null if not found
async function getAirportData(env, icaoCode) {
  const cacheKey = `airport:${icaoCode}`;

  // Try to get from KV cache first
  try {
    const cached = await env.RATE_LIMIT_KV.get(cacheKey);
    if (cached) {
      return JSON.parse(cached);
    }
  } catch (e) {
    // KV read failed, continue to fetch
  }

  // Fetch airport database from OpenFlights (MIT licensed, public data)
  // Source: https://github.com/mwgg/Airports
  try {
    const response = await fetch('https://raw.githubusercontent.com/mwgg/Airports/master/airports.json', {
      cf: { cacheTtl: 86400 } // Cache at Cloudflare edge for 24 hours
    });

    if (!response.ok) throw new Error(`OpenFlights API returned ${response.status}`);

    const airportDb = await response.json();
    const upperCode = icaoCode.toUpperCase();

    // Search through the object to find matching airport by ICAO or IATA code
    let airport = null;
    for (const airportId in airportDb) {
      const ap = airportDb[airportId];
      if (ap.icao === upperCode || ap.iata === upperCode) {
        airport = ap;
        break;
      }
    }

    if (!airport) {
      return null;
    }

    const data = {
      latitude: parseFloat(airport.lat),
      longitude: parseFloat(airport.lon),
      name: airport.name || airport.iata || upperCode,
      iata: airport.iata || '',
    };

    // Cache for 30 days (2592000 seconds)
    try {
      await env.RATE_LIMIT_KV.put(cacheKey, JSON.stringify(data), { expirationTtl: 2592000 });
    } catch (e) {
      // KV write failed, continue (data is still valid for this request)
    }

    return data;
  } catch (e) {
    console.error(`Error fetching airport ${icaoCode}:`, e);
    return null;
  }
}

const ALLOWED_ORIGINS = [
  "https://z0mb1xcat.github.io",
  "https://flyaviatesair.uk",
  "https://www.flyaviatesair.uk",
  "http://localhost:3000",
  "http://localhost:3001",
];

const VALID_FLEET_GROUPS = ["mainline", "cargo", "cityhopper"];

// ─── SUPABASE HELPERS ────────────────────────────────────────────────────────

function sbHeaders(env) {
  return {
    "apikey":        env.SUPABASE_SERVICE_KEY,
    "Authorization": `Bearer ${env.SUPABASE_SERVICE_KEY}`,
    "Content-Type":  "application/json",
  };
}

async function sbGet(env, path) {
  const res = await fetch(`${env.SUPABASE_URL}/rest/v1/${path}`, {
    headers: sbHeaders(env),
  });
  if (!res.ok) throw new Error(`Supabase GET /${path} → ${res.status}`);
  return res.json();
}

async function sbRpc(env, fn, params = {}) {
  const res = await fetch(`${env.SUPABASE_URL}/rest/v1/rpc/${fn}`, {
    method:  "POST",
    headers: sbHeaders(env),
    body:    JSON.stringify(params),
  });
  if (!res.ok) throw new Error(`Supabase RPC ${fn} → ${res.status}`);
  return res.json();
}

async function sbInsert(env, table, data) {
  const res = await fetch(`${env.SUPABASE_URL}/rest/v1/${table}`, {
    method:  "POST",
    headers: { ...sbHeaders(env), "Prefer": "return=representation" },
    body:    JSON.stringify(data),
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    const err  = new Error(`Supabase INSERT ${table} → ${res.status}`);
    err.pgCode = body.code;   // e.g. "23505" for unique violation
    err.detail = body.message;
    throw err;
  }
  return res.json();
}

// Insert-or-update-on-conflict via PostgREST's merge-duplicates upsert. `onConflict` is a
// comma-separated column list matching an existing unique constraint (e.g. friendships'
// `unique (acars_key, friend_pilot_id)`).
async function sbUpsert(env, table, data, onConflict) {
  const res = await fetch(`${env.SUPABASE_URL}/rest/v1/${table}?on_conflict=${encodeURIComponent(onConflict)}`, {
    method:  "POST",
    headers: { ...sbHeaders(env), "Prefer": "resolution=merge-duplicates,return=representation" },
    body:    JSON.stringify(data),
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    const err  = new Error(`Supabase UPSERT ${table} → ${res.status}`);
    err.pgCode = body.code;
    err.detail = body.message;
    throw err;
  }
  return res.json();
}

async function sbUpdate(env, table, filter, data) {
  const res = await fetch(`${env.SUPABASE_URL}/rest/v1/${table}?${filter}`, {
    method:  "PATCH",
    headers: sbHeaders(env),
    body:    JSON.stringify(data),
  });
  if (!res.ok) throw new Error(`Supabase PATCH ${table} → ${res.status}`);
  return res.status;
}

async function sbDelete(env, path) {
  const res = await fetch(`${env.SUPABASE_URL}/rest/v1/${path}`, {
    method:  "DELETE",
    headers: sbHeaders(env),
  });
  if (!res.ok) throw new Error(`Supabase DELETE /${path} → ${res.status}`);
  return res.status;
}

// ── Route aircraft ───────────────────────────────────────────────────────────
// A route can be flown by several aircraft (routes.aircraft, see the
// routes_multiple_aircraft migration): [{type, fleet_group, flight_number?, frequency?,
// est_block_time_minutes?}]. The single-aircraft columns mirror the first entry. Rows written
// before that migration (or read through a select that omits the column) fall back to a
// one-entry list built from those columns.
function routeAircraftList(row) {
  if (Array.isArray(row.aircraft) && row.aircraft.length > 0) return row.aircraft;
  if (!row.aircraft_type) return [];
  const entry = { type: row.aircraft_type, fleet_group: row.fleet_group };
  if (row.flight_number) entry.flight_number = row.flight_number;
  if (row.frequency) entry.frequency = row.frequency;
  if (row.est_block_time_minutes) entry.est_block_time_minutes = row.est_block_time_minutes;
  return [entry];
}

// "ATR 72" and "ATR72", "b738" and "B738" are the same aircraft.
function sameAircraftType(a, b) {
  const norm = (s) => String(s || "").toUpperCase().replace(/[^A-Z0-9]/g, "");
  return norm(a) !== "" && norm(a) === norm(b);
}

// Validates the admin panel's aircraft list. Returns { list } or { error }.
function parseRouteAircraftList(raw) {
  if (!Array.isArray(raw) || raw.length === 0) return { error: "Add at least one aircraft" };
  if (raw.length > 12) return { error: "A route can have at most 12 aircraft" };
  const list = [];
  for (let i = 0; i < raw.length; i++) {
    const e = raw[i] || {};
    const type = String(e.type || "").trim().substring(0, 20);
    const fleet = String(e.fleet_group || "").trim().toLowerCase();
    if (!type) return { error: `Aircraft ${i + 1}: choose a type` };
    if (!VALID_FLEET_GROUPS.includes(fleet)) return { error: `Aircraft ${i + 1}: choose Mainline, CityHopper or Cargo` };
    if (list.some(x => sameAircraftType(x.type, type))) return { error: `${type} is listed twice on this route` };
    const entry = { type, fleet_group: fleet };
    const flightNumber = String(e.flight_number || "").trim().toUpperCase().substring(0, 10);
    if (flightNumber) entry.flight_number = flightNumber;
    const frequency = String(e.frequency || "").trim().substring(0, 30);
    if (frequency) entry.frequency = frequency;
    const block = parseInt(e.est_block_time_minutes, 10);
    if (block > 0) entry.est_block_time_minutes = block;
    list.push(entry);
  }
  return { list };
}

// The single-aircraft columns for a list (its first entry). The database trigger does the
// same; set here too so the response is right even before that migration runs.
function primaryAircraftColumns(list) {
  const p = list[0];
  return {
    aircraft_type: p.type,
    fleet_group: p.fleet_group,
    flight_number: p.flight_number || null,
    frequency: p.frequency || null,
    est_block_time_minutes: p.est_block_time_minutes || null,
  };
}

function transformRouteRecord(row) {
  return {
    id: row.id,
    departure: row.origin_iata,
    arrival: row.dest_iata,
    aircraft: row.aircraft_type,
    aircraftList: routeAircraftList(row),
    distance: row.distance_km,
    flightTime: row.est_block_time_minutes,
    originName: row.origin_name,
    destName: row.dest_name,
    originLat: row.origin_lat,
    originLon: row.origin_lon,
    destLat: row.dest_lat,
    destLon: row.dest_lon,
    flightNumber: row.flight_number,
    frequency: row.frequency,
    notes: row.notes,
    fleet_group: row.fleet_group,
    createdAt: row.created_at,
  };
}

// Returns the total row count matching a filter using Prefer: count=exact
async function sbCount(env, table, filter) {
  const res = await fetch(`${env.SUPABASE_URL}/rest/v1/${table}?${filter}&select=id`, {
    method:  "GET",
    headers: { ...sbHeaders(env), "Prefer": "count=exact" },
  });
  if (!res.ok) throw new Error(`Supabase COUNT ${table} → ${res.status}`);
  const range = res.headers.get("content-range") || "*/0";
  const match = range.match(/\/(\d+)/);
  return match ? parseInt(match[1], 10) : 0;
}

// ─── EDGE CACHE HELPERS ──────────────────────────────────────────────────────
// Uses Cloudflare's Cache API to store Supabase responses at the edge datacenter.
// This avoids hitting Supabase on every request for data that rarely changes.
// CORS headers are stripped before caching (they vary by request origin) and
// re-applied from the live request context when serving a cache hit.

function applyCors(response, corsHeaders) {
  const h = new Headers(response.headers);
  for (const [k, v] of Object.entries(corsHeaders)) h.set(k, v);
  return new Response(response.body, { status: response.status, headers: h });
}

// Try to serve a cached response. Returns null on a miss.
async function fromCache(request, corsHeaders) {
  const cached = await caches.default.match(request);
  if (!cached) return null;
  return applyCors(cached, corsHeaders);
}

// Build and cache a JSON response (without CORS), return one with CORS.
// The cache write is non-blocking via ctx.waitUntil.
function cachedJsonResponse(ctx, cacheKey, data, maxAge, corsHeaders) {
  const body = JSON.stringify(data);
  const baseHeaders = {
    "Content-Type":  "application/json",
    "Cache-Control": `public, max-age=${maxAge}, stale-while-revalidate=${maxAge * 2}`,
  };
  // Store without CORS headers so any origin can be served from the same entry
  ctx.waitUntil(
    caches.default.put(cacheKey, new Response(body, { status: 200, headers: baseHeaders }))
  );
  return new Response(body, {
    status: 200,
    headers: Object.assign({}, baseHeaders, corsHeaders),
  });
}

const WORKER_BASE_URL = 'https://acars.flyaviatesair.uk';

// Fire-and-forget global edge cache purge via Cloudflare Cache Purge API.
// ctx.waitUntil keeps the Worker alive until the purge request completes.
// No-op when secrets are absent — safe to deploy before secrets are added.
function purgeCache(env, ctx, paths) {
  if (!env.CLOUDFLARE_ZONE_ID || !env.CLOUDFLARE_CACHE_TOKEN) return;
  const files = paths.map(p => `${WORKER_BASE_URL}${p}`);
  ctx.waitUntil(
    fetch(`https://api.cloudflare.com/client/v4/zones/${env.CLOUDFLARE_ZONE_ID}/purge_cache`, {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${env.CLOUDFLARE_CACHE_TOKEN}`,
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({ files }),
    }).catch(e => console.error('Cache purge failed:', e))
  );
}

// ─── SHARED AUTH HELPER ──────────────────────────────────────────────────────
// Validates an ACARS key against Supabase. Returns the user row or null.
// Results are memoised per-isolate for 60s: the ACARS client polls messages
// every 30s and pings positions in flight, so without this every request costs
// a users-table round-trip. Trade-off: disabling a user can take up to 60s to
// propagate on a warm isolate.
const AUTH_CACHE_TTL_MS = 60_000;
const AUTH_CACHE_MAX    = 500;
const authCache = new Map(); // acarsKey → { expires, principal | null }

async function verifyAcarsKey(env, key) {
  if (!key) return null;

  const now = Date.now();
  const hit = authCache.get(key);
  if (hit && hit.expires > now) return hit.principal;

  const rows = await sbGet(env, `users?acars_key=eq.${encodeURIComponent(key)}&select=email,first_name,last_name,verified,disabled&limit=1`);
  const user = rows && rows.length > 0 ? rows[0] : null;
  // verified === false blocks accounts that never confirmed their email —
  // signup hands out the ACARS key before verification, so without this check
  // the email step is bypassable for all API access.
  const principal = (user && !user.disabled && user.verified !== false)
    ? { acarsKey: key, email: user.email, user }
    : null;

  if (authCache.size >= AUTH_CACHE_MAX) authCache.clear();
  authCache.set(key, { expires: now + AUTH_CACHE_TTL_MS, principal });
  return principal;
}

// ─── DISCORD STATUS BOT ──────────────────────────────────────────────────────

async function checkService(url, name, fetchOptions = {}) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 5000);
  const start = Date.now();
  try {
    const res = await fetch(url, { signal: controller.signal, ...fetchOptions });
    const latencyMs = Date.now() - start;
    clearTimeout(timer);
    if (res.status >= 500) return { name, status: 'red',    latencyMs, detail: `HTTP ${res.status}` };
    if (!res.ok)           return { name, status: 'yellow', latencyMs, detail: `HTTP ${res.status}` };
    if (latencyMs >= 2000) return { name, status: 'yellow', latencyMs, detail: `${latencyMs}ms` };
    return { name, status: 'green', latencyMs, detail: `${latencyMs}ms` };
  } catch (e) {
    clearTimeout(timer);
    const latencyMs = Date.now() - start;
    if (e.name === 'AbortError') return { name, status: 'red', latencyMs, detail: 'Timeout after 5s' };
    return { name, status: 'red', latencyMs, detail: 'No response' };
  }
}

function buildStatusEmbed(results) {
  const EMOJI   = { green: '🟢', yellow: '🟡', red: '🔴' };
  const COLOR   = { green: 0x2ecc71, yellow: 0xf1c40f, red: 0xe74c3c };
  const SUMMARY = { green: 'All systems operational', yellow: 'Degraded performance', red: 'Service disruption' };
  const SERVICE_META = {
    website:  { label: 'Website',  url: 'flyaviatesair.uk' },
    api:      { label: 'API',      url: 'acars.flyaviatesair.uk' },
    database: { label: 'Database', url: 'Supabase' },
  };
  let worstStatus = results.reduce((worst, r) => {
    if (r.status === 'red')                       return 'red';
    if (r.status === 'yellow' && worst !== 'red') return 'yellow';
    return worst;
  }, 'green');
  if (!COLOR[worstStatus]) worstStatus = 'red';
  const fields = results.map(r => {
    const meta  = SERVICE_META[r.name] || { label: r.name, url: r.name };
    const emoji = EMOJI[r.status] || '❓';
    return { name: `${emoji} ${meta.label}`, value: `${meta.url} — ${r.detail}`, inline: false };
  });
  const footer = new Date().toUTCString().replace('GMT', 'UTC');
  return {
    embeds: [{
      title:       '🛫 AviatesAir, System Status',
      description: SUMMARY[worstStatus],
      color:       COLOR[worstStatus],
      fields,
      footer: { text: `Last checked: ${footer}` },
    }],
  };
}

async function postOrUpdateStatusEmbed(env, embedPayload) {
  const KV_KEY    = 'discord:status_message_id';
  const channelId = env.DISCORD_CHANNEL_ID;
  const token     = env.DISCORD_BOT_TOKEN;
  const base      = `https://discord.com/api/v10/channels/${channelId}/messages`;
  const headers   = { 'Authorization': `Bot ${token}`, 'Content-Type': 'application/json' };

  let messageId = null;
  try { messageId = await env.ACARS_KV.get(KV_KEY); } catch (e) { console.error('Discord status: KV read failed:', e); }

  if (messageId) {
    try {
      const patchRes = await fetch(`${base}/${messageId}`, { method: 'PATCH', headers, body: JSON.stringify(embedPayload) });
      if (patchRes.ok) return;
      console.error('Discord status: PATCH failed (status', patchRes.status, '), falling back to POST');
    } catch (e) { console.error('Discord status: PATCH threw, falling back to POST:', e); }
  }

  const postRes = await fetch(base, { method: 'POST', headers, body: JSON.stringify(embedPayload) });
  if (!postRes.ok) { console.error('Discord status: POST failed, status:', postRes.status, await postRes.text()); return; }
  const msg = await postRes.json();
  if (msg?.id) {
    try { await env.ACARS_KV.put(KV_KEY, msg.id); } catch (e) { console.error('Discord status: KV write failed:', e); }
  } else { console.error('Discord status: POST response missing msg.id:', msg); }
}

// ─── ENTRY POINT ─────────────────────────────────────────────────────────────
export default {
  async fetch(request, env, ctx) {
    const origin        = request.headers.get("Origin");
    const allowedOrigin = ALLOWED_ORIGINS.includes(origin) ? origin : null;

    const corsHeadersBase = {
      "Access-Control-Allow-Methods": "GET, POST, PUT, PATCH, DELETE, OPTIONS",
      "Access-Control-Allow-Headers": "Content-Type, Authorization",
    };
    // Only set CORS origin for known origins. For requests with no Origin header
    // (ACARS C# client, etc.) allow *. Unknown browser origins get no header — browser blocks them.
    let corsOriginHeader;
    if (allowedOrigin) {
      // Known browser origin — enable credentials so httpOnly cookies can be set/sent
      corsOriginHeader = {
        "Access-Control-Allow-Origin":      allowedOrigin,
        "Access-Control-Allow-Credentials": "true",
      };
    } else if (!origin) {
      corsOriginHeader = { "Access-Control-Allow-Origin": "*" };
    } else {
      corsOriginHeader = {};
    }
    const corsHeaders = Object.assign(corsOriginHeader, corsHeadersBase);

    if (request.method === "OPTIONS") {
      return new Response(null, { status: 200, headers: corsHeaders });
    }

    const url      = new URL(request.url);
    const pathname = url.pathname;
    const ip       = request.headers.get("CF-Connecting-IP") || "unknown";

    if (pathname.startsWith("/api/routes") || pathname.startsWith("/api/airports")) {
      return handleRoutesApi(request, env, corsHeaders, ctx);
    }
    if (pathname.startsWith("/api/events")) {
      return handleEventsApi(request, env, corsHeaders, ctx);
    }
    if (pathname.startsWith("/api/bookings")) {
      return handleBookingsApi(request, env, corsHeaders);
    }
    if (pathname === "/api/pilot/position") {
      return handlePilotPositionApi(request, env, corsHeaders);
    }
    if (pathname.startsWith("/api/fleet")) {
      return handleFleetApi(request, env, corsHeaders, ctx);
    }
    if (pathname === "/api/news") {
      if (request.method !== "GET") return json({ error: "Method not allowed" }, 405, corsHeaders);
      const cached = await fromCache(request, corsHeaders);
      if (cached) return cached;
      const rows = await sbGet(env, "news_articles?order=date.desc&select=*").catch(() => []);
      const data = (rows || []).map(row => ({
        id: String(row.id),
        title: row.title,
        category: row.category || undefined,
        date: row.date,
        summary: row.summary,
        content: row.content,
        thumbnail: row.thumbnail,
        thumbnailAlt: row.title,
        heroImage: row.hero_image || undefined,
      }));
      return cachedJsonResponse(ctx, request, { success: true, data }, 3600, corsHeaders);
    }
    if (pathname.startsWith("/api/public")) {
      return handlePublicApi(request, env, corsHeaders, ctx);
    }
    if (pathname === "/api/acars/live-positions") {
      if (request.method !== "GET") return json({ error: "Method not allowed" }, 405, corsHeaders);
      if (!env.LIVE_MAP_ROOM) return json({ positions: [] }, 200, corsHeaders);
      const res = await liveMapRoomFetch(env, "/list", { method: "GET" }).catch(() => null);
      const positions = res ? await res.json().catch(() => []) : [];
      return json({ positions }, 200, corsHeaders);
    }
    // ── SUPPORT CHAT (public — no ACARS key, a website visitor may not be a pilot) ──
    // A chat is identified by its numeric id plus a random visitor_token (returned
    // once, on creation) that the browser keeps in localStorage — this is the only
    // "auth" a visitor has, matching the low-stakes, no-login nature of a support
    // widget. Admin replies go through the separate JWT-gated /admin/support/* block
    // further down. Lives here (top level, before the /api/v1/ dispatch below) rather
    // than inside handleV1Api, since these paths are /api/support/... not /api/v1/... —
    // handleV1Api is only ever reached for pathnames that actually start with /api/v1/.
    if (pathname === "/api/support/chat" || pathname.match(/^\/api\/support\/chat\/\d+\/messages$/)) {
      return handleSupportChatApi(request, env, ctx, url, pathname, corsHeaders);
    }

    if (pathname.startsWith("/api/v1/")) {
      return handleV1Api(request, env, ctx, url, pathname, corsHeaders);
    }

    // All other endpoints need Supabase + secrets
    if (!env.RATE_LIMIT_KV) {
      return json({ success: false, error: "Rate limit KV not configured" }, 500, corsHeaders);
    }
    if (!env.SUPABASE_URL || !env.SUPABASE_SERVICE_KEY) {
      return json({ success: false, error: "Supabase not configured" }, 500, corsHeaders);
    }
    if (!env.JWT_SECRET || !env.RESEND_API_KEY) {
      return json({ success: false, error: "Secrets missing" }, 500, corsHeaders);
    }

    try {

      // ── SIGNUP ──────────────────────────────────────────────────────────────
      if (request.method === "POST" && pathname === "/api/signup") {
        if (!(await rateLimit(env, ctx, `signup:${ip}`, LIMITS.signup)))
          return rateLimitResponse(corsHeaders);

        const body = await safeJson(request);
        if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

        const email     = (body.email || "").toLowerCase().trim();
        const password  = body.password  || "";
        const firstName = body.firstName || "";
        const lastName  = body.lastName  || "";
        const simbrief  = body.simbrief  || "";

        if (!isValidEmail(email))    return json({ success: false, error: "Invalid email"    }, 400, corsHeaders);
        if (!password)               return json({ success: false, error: "Missing password"  }, 400, corsHeaders);
        if (password.length > 128)   return json({ success: false, error: "Password too long" }, 400, corsHeaders);
        if (!firstName || !lastName) return json({ success: false, error: "Missing name"      }, 400, corsHeaders);

        const salt    = crypto.getRandomValues(new Uint8Array(16));
        const saltB64 = uint8ArrayToB64(salt);
        const derived = await deriveKeyPBKDF2(password, saltB64);
        const acarsKey = generateAcarsKey();
        const newFriendCode = await generateFriendCode(acarsKey);
        const verifyToken = crypto.randomUUID();
        const verifyExpiry = new Date(Date.now() + 3600 * 1000).toISOString();

        try {
          await sbInsert(env, "users", {
            email,
            first_name: firstName,
            last_name:  lastName,
            simbrief,
            pw_salt:    saltB64,
            pw_derived: uint8ArrayToB64(derived),
            acars_key:  acarsKey,
            friend_code: newFriendCode,
            pilot_id:   await computePilotId(email),
            verified:   false,
            disabled:   false,
            created_at: new Date().toISOString(),
            verify_token: verifyToken,
            verify_token_expires_at: verifyExpiry,
          });
        } catch (e) {
          if (e.pgCode === "23505") {
            return json({ success: false, error: "User exists" }, 409, corsHeaders);
          }
          throw e;
        }

        const emailOk = await sendVerificationEmail(email, verifyToken, firstName, env);
        if (!emailOk) {
          console.error("Verification email failed to send for:", email);
          return json({ success: false, error: "Account created but failed to send verification email. Please contact support." }, 500, corsHeaders);
        }

        return json({ success: true, message: "Verification email sent. Please check your inbox.", acarsKey }, 200, corsHeaders);
      }

      // ── EMAIL VERIFICATION ──────────────────────────────────────────────────
      if (request.method === "GET" && pathname === "/verify") {
        const token = url.searchParams.get("token");
        if (!token) return new Response("Invalid link", { status: 400, headers: corsHeaders });

        const rows = await sbGet(env, `users?verify_token=eq.${encodeURIComponent(token)}&select=id,verify_token_expires_at&limit=1`);
        if (!rows || rows.length === 0)
          return new Response("Link expired or invalid", { status: 400, headers: corsHeaders });

        const row = rows[0];
        if (new Date(row.verify_token_expires_at) < new Date())
          return new Response("Link expired or invalid", { status: 400, headers: corsHeaders });

        await sbUpdate(env, "users", `id=eq.${row.id}`, {
          verified: true,
          verify_token: null,
          verify_token_expires_at: null,
        });

        return Response.redirect("https://flyaviatesair.uk/login?verified=true", 302);
      }

      // ── LOGIN ───────────────────────────────────────────────────────────────
      if (request.method === "POST" && pathname === "/api/login") {
        if (!(await rateLimit(env, ctx, `login:${ip}`, LIMITS.login)))
          return rateLimitResponse(corsHeaders);

        const body = await safeJson(request);
        if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

        const email    = (body.email || "").toLowerCase().trim();
        const password = body.password || "";

        if (!isValidEmail(email))     return json({ success: false, error: "Invalid email"    }, 400, corsHeaders);
        if (!password)                return json({ success: false, error: "Missing password"  }, 400, corsHeaders);
        if (password.length > 128)    return json({ success: false, error: "Password too long" }, 400, corsHeaders);

        const rows = await sbGet(env, `users?email=eq.${encodeURIComponent(email)}&select=id,email,first_name,last_name,pw_salt,pw_derived,acars_key,verified,disabled,subsidiary&limit=1`);
        const user = (rows && rows.length > 0) ? rows[0] : null;

        // Always run PBKDF2 to prevent timing-based user enumeration.
        // For non-existent users, derive against a per-email dummy salt so pre-computation
        // across accounts is not possible even if the source is exposed.
        const dummySalt = btoa(email).substring(0, 24).padEnd(24, "=");
        const salt = user ? user.pw_salt : dummySalt;
        const derived = await deriveKeyPBKDF2(password, salt);

        if (!user || !timingSafeEqual(uint8ArrayToB64(derived), user.pw_derived ?? ""))
          return json({ success: false, error: "Invalid credentials" }, 401, corsHeaders);

        if (!user.verified) return json({ success: false, error: "Email not verified. Please check your inbox." }, 403, corsHeaders);
        if (user.disabled)  return json({ success: false, error: "Account disabled" }, 403, corsHeaders);

        // 24-hour JWT — also stored in an httpOnly cookie so it is unreachable by JS
        const exp   = Math.floor(Date.now() / 1000) + 86400;
        const token = await createJWT({ email, exp }, env.JWT_SECRET);

        // Build httpOnly session cookie.
        // Domain=flyaviatesair.uk makes it visible to all subdomains (www, acars, …).
        // In localhost dev the Secure flag is omitted so the cookie still sets.
        const isLocalhost = (origin || "").includes("localhost");
        const cookieParts = [
          `aa_session=${token}`,
          "HttpOnly",
          ...(isLocalhost ? [] : ["Secure"]),
          "SameSite=Lax",
          ...(isLocalhost ? [] : ["Domain=flyaviatesair.uk"]),
          "Max-Age=86400",
          "Path=/",
        ];
        const setCookie = cookieParts.join("; ");

        return new Response(JSON.stringify({
          success: true, session: token, acarsKey: user.acars_key,
          email: user.email, firstName: user.first_name, lastName: user.last_name,
          subsidiary: user.subsidiary || null,
        }), {
          status: 200,
          headers: Object.assign({ "Content-Type": "application/json", "Set-Cookie": setCookie }, corsHeaders),
        });
      }

      // ── LOGOUT ─────────────────────────────────────────────────────────────────
      if (request.method === "POST" && pathname === "/api/logout") {
        // Verify the session cookie before clearing — prevents logout CSRF from
        // clearing a cookie the caller never had. Always returns 200 either way
        // so an attacker learns nothing from the response.
        const cookieHeader = request.headers.get("Cookie") || "";
        const sessionMatch = cookieHeader.match(/(?:^|;\s*)aa_session=([^;]+)/);
        const sessionToken = sessionMatch ? sessionMatch[1] : null;
        if (sessionToken) await verifyJWT(sessionToken, env.JWT_SECRET); // result not used, just validates

        const isLocalhost = (origin || "").includes("localhost");
        const clearCookieParts = [
          "aa_session=",
          "HttpOnly",
          ...(isLocalhost ? [] : ["Secure"]),
          "SameSite=Lax",
          ...(isLocalhost ? [] : ["Domain=flyaviatesair.uk"]),
          "Max-Age=0",
          "Path=/",
        ];
        return new Response(JSON.stringify({ success: true }), {
          status: 200,
          headers: Object.assign({
            "Content-Type": "application/json",
            "Set-Cookie":   clearCookieParts.join("; "),
          }, corsHeaders),
        });
      }

      // ── PASSWORD RESET REQUEST ──────────────────────────────────────────────
      if (request.method === "POST" && pathname === "/api/password-reset/request") {
        if (!(await rateLimit(env, ctx, `pwreset:${ip}`, LIMITS.pwreset)))
          return rateLimitResponse(corsHeaders);

        const body = await safeJson(request);
        if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

        const email = (body.email || "").toLowerCase().trim();
        if (!isValidEmail(email)) return json({ success: false, error: "Invalid email" }, 400, corsHeaders);

        // Check 30-day rate limit
        const kvKey = `pwreset:${email}`;
        const existing = await env.RATE_LIMIT_KV.get(kvKey);
        if (existing) {
          return json({ success: false, error: "Rate limited", retryAfter: existing }, 429, corsHeaders);
        }

        // Look up user — do NOT reveal whether the email exists in error responses
        const rows = await sbGet(env, `users?email=eq.${encodeURIComponent(email)}&select=id,first_name&limit=1`);
        const user = rows && rows.length > 0 ? rows[0] : null;

        if (user) {
          const resetToken = crypto.randomUUID();
          const tokenHash  = await sha256B64(resetToken);
          const resetExpiry = new Date(Date.now() + 3600 * 1000).toISOString();

          await sbUpdate(env, "users", `id=eq.${user.id}`, {
            reset_token: tokenHash,
            reset_token_expires_at: resetExpiry,
          });

          try {
            await sendPasswordResetEmail(email, resetToken, user.first_name, env);
            // Email sent — apply 30-day per-address rate limit
            const retryAfter = new Date(Date.now() + RESET_RATE_LIMIT_TTL_S * 1000).toISOString();
            await env.RATE_LIMIT_KV.put(kvKey, retryAfter, { expirationTtl: RESET_RATE_LIMIT_TTL_S });
          } catch (emailErr) {
            console.error("Password reset email failed for user:", user.id, emailErr);
            // Clear orphaned token so the user can retry
            await sbUpdate(env, "users", `id=eq.${user.id}`, { reset_token: null, reset_token_expires_at: null });
          }
        }

        // Always return success — prevents revealing whether the email is registered
        return json({ success: true }, 200, corsHeaders);
      }

      // ── PASSWORD RESET CONFIRM ──────────────────────────────────────────────
      if (request.method === "POST" && pathname === "/api/password-reset/confirm") {
        if (!(await rateLimit(env, ctx, `pwconfirm:${ip}`, LIMITS.pwreset)))
          return rateLimitResponse(corsHeaders);

        const body = await safeJson(request);
        if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

        const { token, password } = body;

        if (!token || typeof token !== "string" || token.length === 0)
          return json({ success: false, error: "Missing or invalid token" }, 400, corsHeaders);
        if (!password || typeof password !== "string" || password.length < 8 || password.length > 128)
          return json({ success: false, error: "Password must be between 8 and 128 characters" }, 400, corsHeaders);

        const tokenHash = await sha256B64(token);

        const rows = await sbGet(env, `users?reset_token=eq.${encodeURIComponent(tokenHash)}&select=id,reset_token_expires_at&limit=1`);
        const user = rows && rows.length > 0 ? rows[0] : null;

        if (!user || !user.reset_token_expires_at || new Date(user.reset_token_expires_at) < new Date())
          return json({ success: false, error: "Invalid or expired token" }, 400, corsHeaders);

        const salt       = uint8ArrayToB64(crypto.getRandomValues(new Uint8Array(16)));
        const derivedKey = await deriveKeyPBKDF2(password, salt);
        const pwDerived  = uint8ArrayToB64(derivedKey);

        await sbUpdate(env, "users", `id=eq.${user.id}`, {
          pw_salt: salt,
          pw_derived: pwDerived,
          reset_token: null,
          reset_token_expires_at: null,
        });

        return json({ success: true }, 200, corsHeaders);
      }

      // ── ADMIN LOGIN ─────────────────────────────────────────────────────────
      if (request.method === "POST" && pathname === "/admin/login") {
        if (!env.ADMIN_JWT_SECRET) return json({ success: false, error: "Admin not configured" }, 500, corsHeaders);
        if (!env.SUPABASE_URL) return json({ success: false, error: "Supabase not configured" }, 500, corsHeaders);
        if (!env.SUPABASE_SERVICE_KEY) return json({ success: false, error: "Supabase not configured" }, 500, corsHeaders);

        if (!(await rateLimit(env, ctx, `admin-login:${ip}`, LIMITS.login)))
          return rateLimitResponse(corsHeaders);

        const body = await safeJson(request);
        if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

        const email    = (body.email || "").toLowerCase().trim();
        const password = body.password || "";

        if (!isValidEmail(email))     return json({ success: false, error: "Invalid email"    }, 400, corsHeaders);
        if (!password)                return json({ success: false, error: "Missing password"  }, 400, corsHeaders);
        if (password.length > 128)    return json({ success: false, error: "Password too long" }, 400, corsHeaders);

        try {
          // Query admin_users table
          const rows = await sbGet(env, `admin_users?email=eq.${encodeURIComponent(email)}&select=id,email,pw_salt,pw_derived&limit=1`);
          if (!rows || rows.length === 0) {
            return json({ success: false, error: "Invalid credentials" }, 401, corsHeaders);
          }

          const admin = rows[0];

          // Validate password using PBKDF2
          const derived = await deriveKeyPBKDF2(password, admin.pw_salt);
          if (!timingSafeEqual(uint8ArrayToB64(derived), admin.pw_derived)) {
            return json({ success: false, error: "Invalid credentials" }, 401, corsHeaders);
          }

          // Password valid, generate JWT
          const exp   = Math.floor(Date.now() / 1000) + (parseInt(env.ADMIN_JWT_EXPIRY || "604800", 10));
          const token = await createAdminJWT({ email, exp }, env.ADMIN_JWT_SECRET);

          return json({ success: true, token, expiresIn: parseInt(env.ADMIN_JWT_EXPIRY || "604800", 10), email }, 200, corsHeaders);
        } catch (e) {
          console.error("Admin login error:", e);
          return json({ success: false, error: "Login service error" }, 500, corsHeaders);
        }
      }

      // ── ADMIN NEWS CRUD ─────────────────────────────────────────────────────
      if (pathname.startsWith("/admin/news")) {
        const payload = await validateAdminAuth(request, env);
        if (!payload) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

        // GET /admin/news — list all news
        if (request.method === "GET" && pathname === "/admin/news") {
          const rows = await sbGet(env, "news_articles?order=created_at.desc&select=*").catch(() => []);
          return json({ success: true, data: rows || [] }, 200, corsHeaders);
        }

        // POST /admin/news — create article
        if (request.method === "POST" && pathname === "/admin/news") {
          const body = await safeJson(request);
          if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

          const title     = (body.title || "").trim();
          const category  = (body.category || "").trim();
          const date      = (body.date || "").trim();
          const summary   = (body.summary || "").trim();
          const content   = (body.content || "").trim();
          const thumbnail = (body.thumbnail || "").trim();
          const heroImage = (body.heroImage || "").trim();

          if (!title || title.length < 5 || title.length > 200) return json({ success: false, error: "Title must be 5-200 characters" }, 400, corsHeaders);
          if (!category) return json({ success: false, error: "Category is required" }, 400, corsHeaders);
          if (!date) return json({ success: false, error: "Date is required" }, 400, corsHeaders);
          if (!summary || summary.length < 10 || summary.length > 500) return json({ success: false, error: "Summary must be 10-500 characters" }, 400, corsHeaders);
          if (!content || content.length < 20) return json({ success: false, error: "Content is required and must be at least 20 characters" }, 400, corsHeaders);
          if (!thumbnail) return json({ success: false, error: "Thumbnail path is required" }, 400, corsHeaders);

          try {
            const inserted = await sbInsert(env, "news_articles", {
              title, category, date, summary, content, thumbnail, hero_image: heroImage || null,
              created_at: new Date().toISOString(), updated_at: new Date().toISOString(),
            });
            purgeCache(env, ctx, ['/api/news']);
            return json({ success: true, data: inserted[0] }, 201, corsHeaders);
          } catch (e) {
            console.error("News insert error:", e);
            return json({ success: false, error: "Failed to create article" }, 500, corsHeaders);
          }
        }

        // PUT /admin/news/:id — update article
        const updateMatch = pathname.match(/^\/admin\/news\/(\d+)$/);
        if (request.method === "PUT" && updateMatch) {
          const id = parseInt(updateMatch[1], 10);
          const body = await safeJson(request);
          if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

          const updates = {};
          if (body.title) updates.title = body.title.trim();
          if (body.category) updates.category = body.category.trim();
          if (body.date) updates.date = body.date.trim();
          if (body.summary) updates.summary = body.summary.trim();
          if (body.content) updates.content = body.content.trim();
          if (body.thumbnail) updates.thumbnail = body.thumbnail.trim();
          if (body.heroImage !== undefined) updates.hero_image = body.heroImage ? body.heroImage.trim() : null;
          updates.updated_at = new Date().toISOString();

          try {
            await sbUpdate(env, "news_articles", `id=eq.${id}`, updates);
            const updated = await sbGet(env, `news_articles?id=eq.${id}&select=*&limit=1`);
            purgeCache(env, ctx, ['/api/news']);
            return json({ success: true, data: updated[0] }, 200, corsHeaders);
          } catch (e) {
            console.error("News update error:", e);
            return json({ success: false, error: "Failed to update article" }, 500, corsHeaders);
          }
        }

        // DELETE /admin/news/:id — delete article
        const deleteMatch = pathname.match(/^\/admin\/news\/(\d+)$/);
        if (request.method === "DELETE" && deleteMatch) {
          const id = parseInt(deleteMatch[1], 10);
          try {
            await sbDelete(env, `news_articles?id=eq.${id}`);
            purgeCache(env, ctx, ['/api/news']);
            return json({ success: true }, 200, corsHeaders);
          } catch (e) {
            console.error("News delete error:", e);
            return json({ success: false, error: "Failed to delete article" }, 500, corsHeaders);
          }
        }
      }

      // ── ADMIN ROUTES CRUD ───────────────────────────────────────────────────
      if (pathname.startsWith("/admin/routes")) {
        const payload = await validateAdminAuth(request, env);
        if (!payload) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

        // GET /admin/routes — list all routes
        if (request.method === "GET" && pathname === "/admin/routes") {
          const rows = await sbGet(env, "routes?order=origin_iata.asc&select=*").catch(() => []);
          return json({ success: true, data: (rows || []).map(transformRouteRecord) }, 200, corsHeaders);
        }

        // POST /admin/routes — create route
        if (request.method === "POST" && pathname === "/admin/routes") {
          const body = await safeJson(request);
          if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

          const departure     = (body.departure || "").trim().toUpperCase();
          const arrival       = (body.arrival || "").trim().toUpperCase();
          const aircraft      = (body.aircraft || "").trim();
          const distanceKm    = parseInt(body.distance, 10) || 0;
          const flightTime    = parseInt(body.flightTime, 10) || 0;
          const originName    = (body.originName || "").trim();
          const destName      = (body.destName || "").trim();
          const originLat     = body.originLat !== undefined ? parseFloat(body.originLat) : null;
          const originLon     = body.originLon !== undefined ? parseFloat(body.originLon) : null;
          const destLat       = body.destLat !== undefined ? parseFloat(body.destLat) : null;
          const destLon       = body.destLon !== undefined ? parseFloat(body.destLon) : null;
          const flightNumber  = (body.flightNumber || "").trim();
          const frequency     = (body.frequency || "").trim();
          const notes         = (body.notes || "").trim();
          const fleetGroup    = (body.fleet_group || "").trim().toLowerCase();

          if (!departure || !/^[A-Z]{4}$/.test(departure)) return json({ success: false, error: "Departure must be valid 4-letter ICAO code" }, 400, corsHeaders);
          if (!arrival || !/^[A-Z]{4}$/.test(arrival)) return json({ success: false, error: "Arrival must be valid 4-letter ICAO code" }, 400, corsHeaders);
          if (departure === arrival) return json({ success: false, error: "Departure and arrival cannot be the same" }, 400, corsHeaders);
          // The admin panel sends aircraftList (every aircraft flying the route); older callers
          // send the single aircraft/fleet_group pair.
          let aircraftList = null;
          if (body.aircraftList !== undefined) {
            const parsed = parseRouteAircraftList(body.aircraftList);
            if (parsed.error) return json({ success: false, error: parsed.error }, 400, corsHeaders);
            aircraftList = parsed.list;
          } else {
            if (!aircraft) return json({ success: false, error: "Aircraft type is required" }, 400, corsHeaders);
            if (!VALID_FLEET_GROUPS.includes(fleetGroup)) return json({ success: false, error: "Invalid fleet group" }, 400, corsHeaders);
          }
          if (distanceKm <= 0) return json({ success: false, error: "Distance must be greater than 0" }, 400, corsHeaders);

          try {
            const inserted = await sbInsert(env, "routes", {
              origin_iata: departure,
              dest_iata: arrival,
              aircraft_type: aircraft,
              distance_km: distanceKm,
              est_block_time_minutes: flightTime || null,
              origin_name: originName || null,
              dest_name: destName || null,
              origin_lat: originLat,
              origin_lon: originLon,
              dest_lat: destLat,
              dest_lon: destLon,
              flight_number: flightNumber || null,
              frequency: frequency || null,
              notes: notes || null,
              fleet_group: fleetGroup,
              ...(aircraftList ? { aircraft: aircraftList, ...primaryAircraftColumns(aircraftList) } : {}),
            });
            purgeCache(env, ctx, ['/api/routes?limit=500', '/api/routes/summary', '/api/public/stats']);
            return json({ success: true, data: transformRouteRecord(inserted[0]) }, 201, corsHeaders);
          } catch (e) {
            console.error("Route insert error:", e);
            const detail = e.detail || e.message || "Failed to create route";
            return json({ success: false, error: detail }, 500, corsHeaders);
          }
        }

        // PUT /admin/routes/:id — update route
        const updateMatch = pathname.match(/^\/admin\/routes\/(\d+)$/);
        if (request.method === "PUT" && updateMatch) {
          const id = parseInt(updateMatch[1], 10);
          const body = await safeJson(request);
          if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

          const updates = {};
          if (body.departure) updates.origin_iata = body.departure.trim().toUpperCase();
          if (body.arrival) updates.dest_iata = body.arrival.trim().toUpperCase();
          if (body.aircraft) updates.aircraft_type = body.aircraft.trim();
          if (body.distance) updates.distance_km = parseInt(body.distance, 10);
          if (body.flightTime !== undefined) updates.est_block_time_minutes = parseInt(body.flightTime, 10) || null;
          if (body.originName) updates.origin_name = body.originName.trim();
          if (body.destName) updates.dest_name = body.destName.trim();
          if (body.originLat !== undefined && body.originLat !== null) updates.origin_lat = parseFloat(body.originLat);
          if (body.originLon !== undefined && body.originLon !== null) updates.origin_lon = parseFloat(body.originLon);
          if (body.destLat !== undefined && body.destLat !== null) updates.dest_lat = parseFloat(body.destLat);
          if (body.destLon !== undefined && body.destLon !== null) updates.dest_lon = parseFloat(body.destLon);
          if (body.flightNumber !== undefined) updates.flight_number = body.flightNumber ? body.flightNumber.trim() : null;
          if (body.frequency !== undefined) updates.frequency = body.frequency ? body.frequency.trim() : null;
          if (body.notes !== undefined) updates.notes = body.notes ? body.notes.trim() : null;
          if (body.fleet_group !== undefined) {
            const rawFleetGroup = (body.fleet_group || "").trim().toLowerCase();
            if (!VALID_FLEET_GROUPS.includes(rawFleetGroup)) {
              return json({ success: false, error: "Invalid fleet group" }, 400, corsHeaders);
            }
            updates.fleet_group = rawFleetGroup;
          }
          if (body.aircraftList !== undefined) {
            const parsed = parseRouteAircraftList(body.aircraftList);
            if (parsed.error) return json({ success: false, error: parsed.error }, 400, corsHeaders);
            updates.aircraft = parsed.list;
            Object.assign(updates, primaryAircraftColumns(parsed.list));
          }

          try {
            await sbUpdate(env, "routes", `id=eq.${id}`, updates);
            const updated = await sbGet(env, `routes?id=eq.${id}&select=*&limit=1`);
            purgeCache(env, ctx, ['/api/routes?limit=500', '/api/routes/summary', '/api/public/stats', `/api/routes/${id}`]);
            return json({ success: true, data: transformRouteRecord(updated[0]) }, 200, corsHeaders);
          } catch (e) {
            console.error("Route update error:", e);
            return json({ success: false, error: "Failed to update route" }, 500, corsHeaders);
          }
        }

        // DELETE /admin/routes/:id — delete route
        const deleteMatch = pathname.match(/^\/admin\/routes\/(\d+)$/);
        if (request.method === "DELETE" && deleteMatch) {
          const id = parseInt(deleteMatch[1], 10);
          try {
            await sbDelete(env, `routes?id=eq.${id}`);
            purgeCache(env, ctx, ['/api/routes?limit=500', '/api/routes/summary', '/api/public/stats', `/api/routes/${id}`]);
            return json({ success: true }, 200, corsHeaders);
          } catch (e) {
            console.error("Route delete error:", e);
            return json({ success: false, error: "Failed to delete route" }, 500, corsHeaders);
          }
        }
      }

      // ── ADMIN USERS (ACARS ACCOUNTS) ────────────────────────────────────────
      if (pathname.startsWith("/admin/users")) {
        const payload = await validateAdminAuth(request, env);
        if (!payload) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

        // GET /admin/users — list all pilots
        if (request.method === "GET" && pathname === "/admin/users") {
          const rows = await sbGet(env, "users?order=created_at.desc&select=id,email,first_name,last_name,acars_key,verified,disabled,created_at").catch(() => []);
          return json({ success: true, data: rows || [] }, 200, corsHeaders);
        }

        // PUT /admin/users/:id/disable — toggle disabled flag
        const disableMatch = pathname.match(/^\/admin\/users\/(\d+)\/disable$/);
        if (request.method === "PUT" && disableMatch) {
          const id = parseInt(disableMatch[1], 10);
          const body = await safeJson(request);
          if (!body || typeof body.disabled !== "boolean") return json({ success: false, error: "disabled (boolean) required" }, 400, corsHeaders);
          try {
            await sbUpdate(env, "users", `id=eq.${id}`, { disabled: body.disabled });
            const updated = await sbGet(env, `users?id=eq.${id}&select=id,email,first_name,last_name,acars_key,verified,disabled,created_at&limit=1`);
            return json({ success: true, data: updated[0] }, 200, corsHeaders);
          } catch (e) {
            console.error("User disable error:", e);
            return json({ success: false, error: "Failed to update user" }, 500, corsHeaders);
          }
        }

        // GET /admin/users/:id/delete-info — fetch PIREP and booking counts before delete
        const deleteInfoMatch = pathname.match(/^\/admin\/users\/(\d+)\/delete-info$/);
        if (request.method === "GET" && deleteInfoMatch) {
          const id = parseInt(deleteInfoMatch[1], 10);
          try {
            const userRows = await sbGet(env, `users?id=eq.${id}&select=acars_key&limit=1`);
            if (!userRows || userRows.length === 0) return json({ success: false, error: "User not found" }, 404, corsHeaders);
            const acarsKey = userRows[0].acars_key;
            const [pirepCount, bookingCount] = await Promise.all([
              sbCount(env, "pireps",   `acars_key=eq.${encodeURIComponent(acarsKey)}`).catch(() => 0),
              sbCount(env, "bookings", `acars_key=eq.${encodeURIComponent(acarsKey)}`).catch(() => 0),
            ]);
            return json({ success: true, data: { pirepCount, bookingCount } }, 200, corsHeaders);
          } catch (e) {
            console.error("User delete-info error:", e);
            return json({ success: false, error: "Failed to fetch delete info" }, 500, corsHeaders);
          }
        }

        // DELETE /admin/users/:id — hard delete pilot
        const deleteMatch = pathname.match(/^\/admin\/users\/(\d+)$/);
        if (request.method === "DELETE" && deleteMatch) {
          const id = parseInt(deleteMatch[1], 10);
          try {
            await sbDelete(env, `users?id=eq.${id}`);
            return json({ success: true }, 200, corsHeaders);
          } catch (e) {
            console.error("User delete error:", e);
            return json({ success: false, error: "Failed to delete user" }, 500, corsHeaders);
          }
        }
      }

      // ── ADMIN SUPPORT CHAT ───────────────────────────────────────────────────
      if (pathname.startsWith("/admin/support")) {
        const payload = await validateAdminAuth(request, env);
        if (!payload) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

        // GET /admin/support/chats?status=open|closed|all — list chats, newest activity first
        if (request.method === "GET" && pathname === "/admin/support/chats") {
          const status = (url.searchParams.get("status") || "open").toLowerCase();
          const statusFilter = status === "all" ? "" : `&status=eq.${status === "closed" ? "closed" : "open"}`;
          const rows = await sbGet(env,
            `support_chats?order=last_message_at.desc&select=*${statusFilter}`
          ).catch(() => []);
          return json({
            success: true,
            data: (rows || []).map(c => ({
              id: c.id, visitorName: c.visitor_name, visitorEmail: c.visitor_email,
              status: c.status, lastMessageAt: c.last_message_at, lastMessagePreview: c.last_message_preview,
              lastSenderType: c.last_sender_type, adminUnreadCount: c.admin_unread_count,
              assignedAdminEmail: c.assigned_admin_email, createdAt: c.created_at,
            })),
          }, 200, corsHeaders);
        }

        // GET /admin/support/chats/:id/messages — full thread, marks it read by admin
        const adminChatMsgsMatch = pathname.match(/^\/admin\/support\/chats\/(\d+)\/messages$/);
        if (request.method === "GET" && adminChatMsgsMatch) {
          const chatId = parseInt(adminChatMsgsMatch[1], 10);
          try {
            const chatRows = await sbGet(env, `support_chats?id=eq.${chatId}&select=*&limit=1`);
            const chat = chatRows && chatRows[0];
            if (!chat) return json({ success: false, error: "Chat not found" }, 404, corsHeaders);

            const messages = await sbGet(env,
              `support_chat_messages?chat_id=eq.${chatId}&order=id.asc&select=id,sender_type,sender_name,content,created_at`
            );
            if (chat.admin_unread_count > 0)
              ctx.waitUntil(sbUpdate(env, "support_chats", `id=eq.${chatId}`, { admin_unread_count: 0 }).catch(() => {}));

            return json({
              success: true,
              data: {
                chat: {
                  id: chat.id, visitorName: chat.visitor_name, visitorEmail: chat.visitor_email,
                  status: chat.status, assignedAdminEmail: chat.assigned_admin_email, createdAt: chat.created_at,
                },
                messages: (messages || []).map(m => ({
                  id: m.id, senderType: m.sender_type, senderName: m.sender_name,
                  content: m.content, createdAt: m.created_at,
                })),
              },
            }, 200, corsHeaders);
          } catch (e) {
            console.error("Admin support chat thread error:", e);
            return json({ success: false, error: "Failed to load chat" }, 500, corsHeaders);
          }
        }

        // POST /admin/support/chats/:id/messages — admin reply
        const adminChatReplyMatch = pathname.match(/^\/admin\/support\/chats\/(\d+)\/messages$/);
        if (request.method === "POST" && adminChatReplyMatch) {
          const chatId = parseInt(adminChatReplyMatch[1], 10);
          const body = await safeJson(request);
          if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

          const content = (body.content || "").trim().substring(0, 4000);
          if (!content) return json({ success: false, error: "content required" }, 400, corsHeaders);

          try {
            const chatRows = await sbGet(env, `support_chats?id=eq.${chatId}&select=id,visitor_unread_count&limit=1`);
            const chat = chatRows && chatRows[0];
            if (!chat) return json({ success: false, error: "Chat not found" }, 404, corsHeaders);

            await sbInsert(env, "support_chat_messages", {
              chat_id:     chatId,
              sender_type: "admin",
              sender_name: "AviatesAir Support",
              content,
            });
            await sbUpdate(env, "support_chats", `id=eq.${chatId}`, {
              status:                "open",
              last_message_at:       new Date().toISOString(),
              last_message_preview:  content.substring(0, 140),
              last_sender_type:      "admin",
              visitor_unread_count:  (chat.visitor_unread_count || 0) + 1,
              admin_unread_count:    0,
              assigned_admin_email:  payload.email,
            });
            return json({ success: true }, 200, corsHeaders);
          } catch (e) {
            console.error("Admin support chat reply error:", e);
            return json({ success: false, error: "Failed to send reply" }, 500, corsHeaders);
          }
        }

        // POST /admin/support/chats/:id/close — mark resolved
        const adminChatCloseMatch = pathname.match(/^\/admin\/support\/chats\/(\d+)\/close$/);
        if (request.method === "POST" && adminChatCloseMatch) {
          const chatId = parseInt(adminChatCloseMatch[1], 10);
          try {
            await sbUpdate(env, "support_chats", `id=eq.${chatId}`, { status: "closed", closed_at: new Date().toISOString() });
            return json({ success: true }, 200, corsHeaders);
          } catch (e) {
            console.error("Admin support chat close error:", e);
            return json({ success: false, error: "Failed to close chat" }, 500, corsHeaders);
          }
        }

        // POST /admin/support/chats/:id/reopen
        const adminChatReopenMatch = pathname.match(/^\/admin\/support\/chats\/(\d+)\/reopen$/);
        if (request.method === "POST" && adminChatReopenMatch) {
          const chatId = parseInt(adminChatReopenMatch[1], 10);
          try {
            await sbUpdate(env, "support_chats", `id=eq.${chatId}`, { status: "open", closed_at: null });
            return json({ success: true }, 200, corsHeaders);
          } catch (e) {
            console.error("Admin support chat reopen error:", e);
            return json({ success: false, error: "Failed to reopen chat" }, 500, corsHeaders);
          }
        }
      }

      // ── ADMIN EVENTS ────────────────────────────────────────────────────────
      if (pathname.startsWith("/admin/events")) {
        const payload = await validateAdminAuth(request, env);
        if (!payload) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

        // GET /admin/events — list all events
        if (request.method === "GET" && pathname === "/admin/events") {
          const rows = await sbGet(env, "events?order=event_date.desc&select=*").catch(() => []);
          return json({ success: true, data: rows || [] }, 200, corsHeaders);
        }

        // POST /admin/events — create event
        if (request.method === "POST" && pathname === "/admin/events") {
          const body = await safeJson(request);
          if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

          const title = (body.title || "").trim();
          if (!title) return json({ success: false, error: "Title is required" }, 400, corsHeaders);

          const eventDate = (body.event_date || "").trim();
          if (!eventDate) return json({ success: false, error: "event_date is required" }, 400, corsHeaders);

          try {
            const inserted = await sbInsert(env, "events", {
              title,
              description:          (body.description || "").trim(),
              event_date:           eventDate,
              time_utc:             (body.time_utc || "").trim(),
              route:                (body.route || "").trim(),
              aircraft_restriction: (body.aircraft_restriction || "").trim(),
              rank_restriction:     (body.rank_restriction || "").trim(),
              max_participants:     body.max_participants != null ? parseInt(body.max_participants, 10) || 0 : null,
              status:               body.status || "upcoming",
              is_featured:          body.is_featured ? 1 : 0,
              created_by:           "Official",
              created_by_name:      "AviatesAir Admin",
              created_at:           new Date().toISOString(),
            });
            purgeCache(env, ctx, ['/api/events?filter=upcoming', '/api/events?filter=past', '/api/events?filter=all']);
            return json({ success: true, data: inserted[0] }, 201, corsHeaders);
          } catch (e) {
            console.error("Admin event create error:", e);
            return json({ success: false, error: "Failed to create event" }, 500, corsHeaders);
          }
        }
      }

      // ── ADMIN AIRCRAFT REGISTRATIONS ────────────────────────────────────────
      if (pathname.startsWith("/admin/aircraft")) {
        const payload = await validateAdminAuth(request, env);
        if (!payload) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

        // GET /admin/aircraft-types — list types for dropdown
        if (request.method === "GET" && pathname === "/admin/aircraft-types") {
          const rows = await sbGet(env, "aircraft_types?order=type_code.asc&select=type_code,name").catch(() => []);
          return json({ success: true, data: rows || [] }, 200, corsHeaders);
        }

        // GET /admin/aircraft-registrations — list all registrations
        if (request.method === "GET" && pathname === "/admin/aircraft-registrations") {
          const rows = await sbGet(env, "aircraft_registrations?order=registration.asc&select=*").catch(() => []);
          return json({ success: true, data: rows || [] }, 200, corsHeaders);
        }

        // POST /admin/aircraft-registrations — add new registration
        if (request.method === "POST" && pathname === "/admin/aircraft-registrations") {
          const body = await safeJson(request);
          if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

          const registration = (body.registration || "").trim().toUpperCase();
          const typeCode     = (body.type_code || "").trim();
          const status       = (body.status || "active").trim();

          if (!registration) return json({ success: false, error: "registration is required" }, 400, corsHeaders);
          if (!typeCode)      return json({ success: false, error: "type_code is required" }, 400, corsHeaders);

          const validStatuses = ["active", "ordered", "maintenance", "stored", "retired", "inspection"];
          if (!validStatuses.includes(status)) return json({ success: false, error: "Invalid status" }, 400, corsHeaders);

          try {
            const inserted = await sbInsert(env, "aircraft_registrations", {
              registration,
              type_code:         typeCode,
              status,
              msn:               body.msn ? body.msn.trim() : null,
              delivery_date:     body.delivery_date ? body.delivery_date.trim() : null,
              expected_delivery: body.expected_delivery ? body.expected_delivery.trim() : null,
              hub_icao:          body.hub_icao ? body.hub_icao.trim().toUpperCase() : null,
              notes:             body.notes ? body.notes.trim() : null,
              total_flights:     0,
              total_hours_tenths: 0,
            });
            return json({ success: true, data: inserted[0] }, 201, corsHeaders);
          } catch (e) {
            console.error("Aircraft registration create error:", e);
            return json({ success: false, error: "Failed to create registration" }, 500, corsHeaders);
          }
        }

        // PUT /admin/aircraft-registrations/:id — update registration
        const putAircraftMatch = pathname.match(/^\/admin\/aircraft-registrations\/(\d+)$/);
        if (request.method === "PUT" && putAircraftMatch) {
          const id = parseInt(putAircraftMatch[1], 10);
          const body = await safeJson(request);
          if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

          const validStatuses = ["active", "ordered", "maintenance", "stored", "retired", "inspection"];
          const updates = {};
          if (body.type_code !== undefined) updates.type_code = body.type_code.trim();
          if (body.status !== undefined) {
            if (!validStatuses.includes(body.status)) return json({ success: false, error: "Invalid status" }, 400, corsHeaders);
            updates.status = body.status;
          }
          if (body.msn !== undefined) updates.msn = body.msn ? body.msn.trim() : null;
          if (body.delivery_date !== undefined) updates.delivery_date = body.delivery_date ? body.delivery_date.trim() : null;
          if (body.expected_delivery !== undefined) updates.expected_delivery = body.expected_delivery ? body.expected_delivery.trim() : null;
          if (body.hub_icao !== undefined) updates.hub_icao = body.hub_icao ? body.hub_icao.trim().toUpperCase() : null;
          if (body.notes !== undefined) updates.notes = body.notes ? body.notes.trim() : null;

          if (Object.keys(updates).length === 0) return json({ success: false, error: "No fields to update" }, 400, corsHeaders);

          try {
            await sbUpdate(env, "aircraft_registrations", `id=eq.${id}`, updates);
            const updated = await sbGet(env, `aircraft_registrations?id=eq.${id}&select=*&limit=1`);
            return json({ success: true, data: updated[0] }, 200, corsHeaders);
          } catch (e) {
            console.error("Aircraft registration update error:", e);
            return json({ success: false, error: "Failed to update registration" }, 500, corsHeaders);
          }
        }

        // DELETE /admin/aircraft-registrations/:id — delete registration
        const deleteAircraftMatch = pathname.match(/^\/admin\/aircraft-registrations\/(\d+)$/);
        if (request.method === "DELETE" && deleteAircraftMatch) {
          const id = parseInt(deleteAircraftMatch[1], 10);
          try {
            await sbDelete(env, `aircraft_registrations?id=eq.${id}`);
            return json({ success: true }, 200, corsHeaders);
          } catch (e) {
            console.error("Aircraft registration delete error:", e);
            return json({ success: false, error: "Failed to delete registration" }, 500, corsHeaders);
          }
        }
      }

      // ── ADMIN: MANUAL PIREPS ───────────────────────────────────────────────
      if (pathname.startsWith("/admin/manual-pireps")) {
        const payload = await validateAdminAuth(request, env);
        if (!payload) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

        // GET /admin/manual-pireps?status=pending|approved|declined
        if (request.method === "GET" && pathname === "/admin/manual-pireps") {
          const statusFilter = url.searchParams.get("status");
          const validStatuses = ["pending", "approved", "declined"];
          const filter = statusFilter && validStatuses.includes(statusFilter)
            ? `&status=eq.${statusFilter}`
            : "";
          const rows = await sbGet(env,
            `manual_pireps?order=submitted_at.desc&limit=200${filter}&select=id,acars_key,flight_number,departure_icao,arrival_icao,aircraft_type,aircraft_reg,block_minutes,route,notes,vatsim_id,screenshot_url,status,admin_note,reviewed_by,submitted_at,reviewed_at`
          );

          // Enrich with pilot names — one users query for all distinct keys
          // instead of one query per PIREP row (was an N+1 of up to 200 calls)
          const keys = [...new Set((rows || []).map(r => r.acars_key).filter(Boolean))];
          const nameByKey = {};
          if (keys.length > 0) {
            const userRows = await sbGet(env,
              `users?acars_key=in.(${encodeURIComponent(keys.join(','))})&select=acars_key,first_name,last_name`
            ).catch(() => []);
            for (const u of (userRows || [])) nameByKey[u.acars_key] = u;
          }
          const enriched = (rows || []).map(row => {
            const u = nameByKey[row.acars_key] || {};
            return { ...row, pilot_first_name: u.first_name || null, pilot_last_name: u.last_name || null };
          });

          return json({ success: true, data: enriched }, 200, corsHeaders);
        }

        // PUT /admin/manual-pireps/:id/review
        const reviewMatch = pathname.match(/^\/admin\/manual-pireps\/(\d+)\/review$/);
        if (request.method === "PUT" && reviewMatch) {
          const pirepId = parseInt(reviewMatch[1], 10);
          const body = await safeJson(request);
          if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

          const action = body.action;
          if (action !== "approve" && action !== "decline")
            return json({ success: false, error: "action must be 'approve' or 'decline'" }, 400, corsHeaders);

          const note = (body.note || "").trim();
          if (action === "decline" && !note)
            return json({ success: false, error: "A note is required when declining a PIREP" }, 400, corsHeaders);

          // Fetch the full row (not just acars_key) before updating, so a real logbook entry
          // can be built from it below, and so re-approving an already-approved report can't
          // insert a second duplicate flight.
          const existingRows = await sbGet(env, `manual_pireps?id=eq.${pirepId}&select=*&limit=1`);
          const existing = existingRows?.[0];
          if (!existing) return json({ success: false, error: "Manual PIREP not found" }, 404, corsHeaders);
          const wasAlreadyApproved = existing.status === "approved";

          await sbUpdate(env, "manual_pireps", `id=eq.${pirepId}`, {
            status:      action === "approve" ? "approved" : "declined",
            admin_note:  note || null,
            reviewed_by: payload.email || "admin",
            reviewed_at: new Date().toISOString(),
          });

          if (action === 'approve') {
            ctx.waitUntil((async () => {
              try {
                const pilotKey = existing.acars_key;
                if (!pilotKey) return;

                // Land it in the real logbook (the `pireps` table) so an approved manual report
                // shows up as a normal flight in Flights/Pilot Hub on both the app and the
                // website, not just in the separate Manual Logbook review list. Guarded by
                // wasAlreadyApproved above so re-hitting this endpoint never double-inserts.
                if (!wasAlreadyApproved) {
                  try {
                    // Best-effort distance lookup — routes.origin_iata/dest_iata actually hold
                    // ICAO codes in this schema, so this matches directly on the ICAO pair.
                    let distanceNm = 0;
                    try {
                      const routeRows = await sbGet(env,
                        `routes?origin_iata=eq.${encodeURIComponent(existing.departure_icao)}&dest_iata=eq.${encodeURIComponent(existing.arrival_icao)}&select=distance_km&limit=1`
                      );
                      if (routeRows?.[0]?.distance_km) distanceNm = Math.round(routeRows[0].distance_km * 0.539957);
                    } catch { /* non-fatal — falls back to 0 */ }

                    await sbInsert(env, "pireps", {
                      acars_key:       pilotKey,
                      flight_number:   existing.flight_number || "",
                      callsign:        existing.flight_number || existing.aircraft_reg || "",
                      departure_icao:  existing.departure_icao,
                      arrival_icao:    existing.arrival_icao,
                      aircraft_type:   existing.aircraft_type,
                      block_out_time:  null,
                      block_in_time:   null,
                      block_minutes:   existing.block_minutes || 0,
                      air_minutes:     existing.block_minutes || 0,
                      distance_nm:     distanceNm,
                      fuel_used_lbs:   0,
                      max_altitude_ft: 0,
                      landing_vs_fpm:  0,
                      landing_score:   0,
                      route:           existing.route || "",
                      planned_ofp:     "",
                      submitted_at:    existing.submitted_at,
                      status:          "accepted",
                    });
                    // Flag it so get_manual_pirep_stats stops counting these hours — the row
                    // above now IS its hours contribution via get_pilot_stats, and without this
                    // every approval would double-count toward total_hours/rank everywhere that
                    // adds "pireps stats + manual pirep stats" together (portal profile, PIREP
                    // submission rank sync, and this same handler's own rank sync below).
                    await sbUpdate(env, "manual_pireps", `id=eq.${pirepId}`, { migrated_to_pireps: true });
                  } catch (e) {
                    console.error("Manual PIREP -> logbook insert failed (non-fatal):", e);
                  }
                }

                const discordRows = await sbGet(env,
                  `users?acars_key=eq.${encodeURIComponent(pilotKey)}&select=discord_id,discord_rank,first_name&limit=1`
                );
                const du = discordRows?.[0];
                if (!du?.discord_id) return;

                const [statsRows, manualRows] = await Promise.all([
                  sbRpc(env, 'get_pilot_stats',        { p_acars_key: pilotKey }),
                  sbRpc(env, 'get_manual_pirep_stats', { p_acars_key: pilotKey }),
                ]);
                const totalHours = (Number(statsRows?.[0]?.total_hours)  || 0)
                                 + (Number(manualRows?.[0]?.manual_hours) || 0);
                const { rank: newRank } = computeRank(totalHours);
                if (newRank === du.discord_rank) return;

                await syncDiscordRank(env, du.discord_id, pilotKey, newRank, du.first_name || 'Pilot');
              } catch (e) {
                console.error('Manual PIREP rank sync failed:', e);
              }
            })());
          }

          return json({ success: true }, 200, corsHeaders);
        }

        // GET /admin/manual-pireps/:id/screenshot
        const screenshotMatch = pathname.match(/^\/admin\/manual-pireps\/(\d+)\/screenshot$/);
        if (request.method === "GET" && screenshotMatch) {
          const pirepId = parseInt(screenshotMatch[1], 10);
          const rows = await sbGet(env,
            `manual_pireps?id=eq.${pirepId}&select=screenshot_url&limit=1`
          );
          const screenshotUrl = rows?.[0]?.screenshot_url;
          if (!screenshotUrl)
            return json({ success: false, error: "No screenshot for this PIREP" }, 404, corsHeaders);

          const encodedPath = screenshotUrl.split('/').map(encodeURIComponent).join('/');
          const fileRes = await fetch(
            `${env.SUPABASE_URL}/storage/v1/object/pirep-screenshots/${encodedPath}`,
            { headers: { "Authorization": `Bearer ${env.SUPABASE_SERVICE_KEY}` } }
          );
          if (!fileRes.ok) {
            console.error("Screenshot fetch error:", fileRes.status, await fileRes.text().catch(() => ""));
            return json({ success: false, error: "Could not retrieve screenshot" }, 500, corsHeaders);
          }
          return new Response(fileRes.body, {
            status: 200,
            headers: {
              ...corsHeaders,
              "Content-Type": fileRes.headers.get("Content-Type") || "image/jpeg",
              "Cache-Control": "private, max-age=3600",
            },
          });
        }

        return json({ success: false, error: "Not found" }, 404, corsHeaders);
      }

      // ── ADMIN LOOKUP AIRPORTS ───────────────────────────────────────────────
      if (request.method === "POST" && pathname === "/admin/lookup-airports") {
        const payload = await validateAdminAuth(request, env);
        if (!payload) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

        const body = await safeJson(request);
        if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

        const { departure, arrival, aircraft } = body;

        // Validate inputs
        if (!departure || !arrival || !aircraft) {
          return json({ success: false, error: "Missing departure, arrival, or aircraft" }, 400, corsHeaders);
        }

        if (!/^[A-Z]{4}$/.test(departure) || !/^[A-Z]{4}$/.test(arrival)) {
          return json({ success: false, error: "Departure and arrival must be 4-letter ICAO codes" }, 400, corsHeaders);
        }

        if (departure === arrival) {
          return json({ success: false, error: "Departure and arrival cannot be the same" }, 400, corsHeaders);
        }

        // Fetch airport data
        try {
          const [depData, arrData] = await Promise.all([
            getAirportData(env, departure),
            getAirportData(env, arrival),
          ]);

          if (!depData) {
            return json({ success: false, error: `Airport ${departure} not found in airport database` }, 404, corsHeaders);
          }

          if (!arrData) {
            return json({ success: false, error: `Airport ${arrival} not found in airport database` }, 404, corsHeaders);
          }

          // Calculate distance
          const distance = calculateDistance(
            depData.latitude,
            depData.longitude,
            arrData.latitude,
            arrData.longitude
          );

          // Estimate flight time based on aircraft cruise speed
          const cruiseSpeed = AIRCRAFT_CRUISE_SPEEDS[aircraft];
          if (!cruiseSpeed) {
            return json({ success: false, error: `Unknown aircraft type: ${aircraft}` }, 400, corsHeaders);
          }

          const flightTime = Math.round((distance / cruiseSpeed) * 60);

          // Convert distance from nautical miles to kilometers
          const distanceKm = Math.round(distance * 1.852);

          return json({
            success: true,
            data: {
              origin_lat: depData.latitude,
              origin_lon: depData.longitude,
              origin_name: depData.name,
              origin_iata: depData.iata,
              dest_lat: arrData.latitude,
              dest_lon: arrData.longitude,
              dest_name: arrData.name,
              dest_iata: arrData.iata,
              distance_km: distanceKm,
              distance_nm: distance,
              est_block_time_minutes: flightTime,
            },
          }, 200, corsHeaders);
        } catch (e) {
          console.error("POST /admin/lookup-airports:", e);
          return json({ success: false, error: e.message }, 500, corsHeaders);
        }
      }

      // ── ACARS AUTH ──────────────────────────────────────────────────────────
      if (request.method === "POST" && pathname === "/api/acars/auth") {
        if (!(await rateLimit(env, ctx, `acarsauth:${ip}`, LIMITS.acarsAuth)))
          return rateLimitResponse(corsHeaders);

        const body = await safeJson(request);
        const key  = body?.acarsKey;
        if (!key) return json({ success: false, error: "Missing acarsKey" }, 400, corsHeaders);

        const rows = await sbGet(env, `users?acars_key=eq.${encodeURIComponent(key)}&select=email,first_name,last_name,simbrief,disabled,subsidiary&limit=1`);
        if (!rows || rows.length === 0)
          return json({ success: false, error: "Invalid ACARS key" }, 401, corsHeaders);

        const user = rows[0];
        if (user.disabled) return json({ success: false, error: "ACARS key suspended" }, 403, corsHeaders);

        const pilotId = await computePilotId(user.email);
        return json({ success: true, pilotId, email: user.email, firstName: user.first_name, lastName: user.last_name, simbrief: user.simbrief || "", subsidiary: user.subsidiary || null }, 200, corsHeaders);
      }

      // ── FRIEND CODE RESOLVE ─────────────────────────────────────────────────
      if (request.method === "GET" && pathname === "/api/friends/resolve") {
        if (!(await rateLimit(env, ctx, `resolve:${ip}`, LIMITS.resolve)))
          return rateLimitResponse(corsHeaders);
        const code = (url.searchParams.get("code") || "").trim().toUpperCase();
        if (!code || code.length !== 9 || code[4] !== "-") {
          return json({ success: false, error: "Invalid friend code format" }, 400, corsHeaders);
        }

        const auth      = request.headers.get("Authorization") || "";
        const callerKey = auth.startsWith("Bearer ") ? auth.slice(7).trim() : "";
        if (!callerKey) {
          return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);
        }

        // Fetch caller and target in parallel — saves one Supabase round-trip
        const [callerRows, targetRows] = await Promise.all([
          sbGet(env, `users?acars_key=eq.${encodeURIComponent(callerKey)}&select=id&limit=1`),
          sbGet(env, `users?friend_code=eq.${encodeURIComponent(code)}&select=email,first_name,last_name&limit=1`),
        ]);

        if (!callerRows || callerRows.length === 0) {
          return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);
        }
        if (!targetRows || targetRows.length === 0) {
          return json({ success: false, error: "Friend code not found" }, 404, corsHeaders);
        }

        const resolvedUser = targetRows[0];
        const targetEmail  = resolvedUser.email;

        const idBuf = await crypto.subtle.digest(
          "SHA-256", new TextEncoder().encode("AVIATES_ID:" + targetEmail)
        );
        const pilotId = Array.from(new Uint8Array(idBuf))
          .slice(0, 16)
          .map(b => b.toString(16).padStart(2, "0"))
          .join("");

        return json({
          PilotId:    pilotId,
          PilotName:  `${resolvedUser.first_name} ${resolvedUser.last_name}`.trim(),
          FriendCode: code,
          Rank:       "Pilot",
          IsOnline:   false,
          AddedAt:    new Date().toISOString(),
        }, 200, corsHeaders);
      }

      // ── PORTAL: DISCORD CONNECT ────────────────────────────────────────────
      if (request.method === 'GET' && pathname === '/api/portal/discord/connect') {
        const authH     = request.headers.get('Authorization') || '';
        const portalKey = authH.startsWith('Bearer ') ? authH.slice(7).trim() : '';
        if (!portalKey) return json({ success: false, error: 'Unauthorized' }, 401, corsHeaders);

        if (!(await verifyAcarsKey(env, portalKey)))
          return json({ success: false, error: 'Unauthorized' }, 401, corsHeaders);

        if (!env.DISCORD_CLIENT_ID)
          return json({ success: false, error: 'Discord not configured' }, 500, corsHeaders);

        const state = crypto.randomUUID();
        await env.ACARS_KV.put(`discord_state:${state}`, portalKey, { expirationTtl: 300 });

        const params = new URLSearchParams({
          client_id:     env.DISCORD_CLIENT_ID,
          redirect_uri:  'https://acars.flyaviatesair.uk/api/portal/discord/callback',
          response_type: 'code',
          scope:         'identify',
          state,
        });

        return json({ url: `https://discord.com/api/oauth2/authorize?${params}` }, 200, corsHeaders);
      }

      // ── PORTAL: DISCORD CALLBACK ───────────────────────────────────────────
      if (request.method === 'GET' && pathname === '/api/portal/discord/callback') {
        const code  = url.searchParams.get('code')  || '';
        const state = url.searchParams.get('state') || '';
        const errorRedirect = 'https://flyaviatesair.uk/portal/profile?discord=error';

        if (!code || !state) return Response.redirect(errorRedirect, 302);

        const acarsKey = await env.ACARS_KV.get(`discord_state:${state}`).catch(() => null);
        if (!acarsKey) return Response.redirect(errorRedirect, 302);
        await env.ACARS_KV.delete(`discord_state:${state}`);

        // Exchange code for access token
        const tokenRes = await fetch('https://discord.com/api/v10/oauth2/token', {
          method: 'POST',
          headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
          body: new URLSearchParams({
            client_id:     env.DISCORD_CLIENT_ID,
            client_secret: env.DISCORD_CLIENT_SECRET,
            grant_type:    'authorization_code',
            code,
            redirect_uri:  'https://acars.flyaviatesair.uk/api/portal/discord/callback',
          }),
        });
        if (!tokenRes.ok) return Response.redirect(errorRedirect, 302);
        const { access_token } = await tokenRes.json();
        if (!access_token) return Response.redirect(errorRedirect, 302);

        // Fetch the pilot's Discord identity
        const discordRes = await fetch('https://discord.com/api/v10/users/@me', {
          headers: { Authorization: `Bearer ${access_token}` },
        });
        if (!discordRes.ok) return Response.redirect(errorRedirect, 302);
        const discordUser = await discordRes.json();

        // Store Discord identity — clear discord_rank so syncDiscordRank writes a fresh one
        await sbUpdate(env, 'users', `acars_key=eq.${encodeURIComponent(acarsKey)}`, {
          discord_id:       discordUser.id,
          discord_username: discordUser.username,
          discord_rank:     null,
        });

        // Compute current rank and assign the role immediately
        const [statsRows, manualRows, userRows] = await Promise.all([
          sbRpc(env, 'get_pilot_stats',        { p_acars_key: acarsKey }),
          sbRpc(env, 'get_manual_pirep_stats', { p_acars_key: acarsKey }),
          sbGet(env, `users?acars_key=eq.${encodeURIComponent(acarsKey)}&select=first_name&limit=1`),
        ]);
        const totalHours = (Number(statsRows?.[0]?.total_hours)    || 0)
                         + (Number(manualRows?.[0]?.manual_hours)   || 0);
        const { rank: currentRank } = computeRank(totalHours);
        const firstName = userRows?.[0]?.first_name || 'Pilot';

        await syncDiscordRank(env, discordUser.id, acarsKey, currentRank, firstName);

        return Response.redirect('https://flyaviatesair.uk/portal/profile?discord=connected', 302);
      }

      // ── PORTAL: DISCORD DISCONNECT ─────────────────────────────────────────
      if (request.method === 'DELETE' && pathname === '/api/portal/discord/disconnect') {
        const authH     = request.headers.get('Authorization') || '';
        const portalKey = authH.startsWith('Bearer ') ? authH.slice(7).trim() : '';
        if (!portalKey) return json({ success: false, error: 'Unauthorized' }, 401, corsHeaders);

        if (!(await verifyAcarsKey(env, portalKey)))
          return json({ success: false, error: 'Unauthorized' }, 401, corsHeaders);

        await sbUpdate(env, 'users', `acars_key=eq.${encodeURIComponent(portalKey)}`, {
          discord_id:       null,
          discord_username: null,
          discord_rank:     null,
        });

        return json({ success: true }, 200, corsHeaders);
      }

      // ── PORTAL: PROFILE ────────────────────────────────────────────────────
      if (request.method === "GET" && pathname === "/api/portal/profile") {
        const authH     = request.headers.get("Authorization") || "";
        const portalKey = authH.startsWith("Bearer ") ? authH.slice(7).trim() : "";
        if (!portalKey) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

        const [authRows, statsRows, manualRows] = await Promise.all([
          sbGet(env, `users?acars_key=eq.${encodeURIComponent(portalKey)}&select=email,first_name,last_name,simbrief,created_at,disabled,discord_username,subsidiary&limit=1`),
          sbRpc(env, "get_pilot_stats",        { p_acars_key: portalKey }),
          sbRpc(env, "get_manual_pirep_stats", { p_acars_key: portalKey }),
        ]);

        if (!authRows || authRows.length === 0 || authRows[0].disabled)
          return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

        const u      = authRows[0];
        const stats  = statsRows?.[0]  || {};
        const mstats = manualRows?.[0] || {};

        const totalFlights = (Number(stats.total_flights) || 0) + (Number(mstats.manual_flights) || 0);
        const totalHours   = (Number(stats.total_hours)   || 0) + (Number(mstats.manual_hours)   || 0);

        const pilot_id = await computePilotId(u.email);
        const { rank, rank_pct } = computeRank(totalHours);
        return json({
          pilot_id,
          email:            u.email,
          first_name:       u.first_name,
          last_name:        u.last_name,
          simbrief:         u.simbrief || null,
          joined_at:        u.created_at || null,
          total_flights:    totalFlights,
          total_hours:      totalHours,
          total_nm:         Number(stats.total_nm)  || 0,
          avg_score:        Number(stats.avg_score) || 0,
          rank,
          rank_pct,
          discord_username: u.discord_username || null,
          subsidiary: u.subsidiary || null,
        }, 200, corsHeaders);
      }

      // ── PORTAL: SUBSIDIARY INFO (current pick + moves used/remaining this month) ──
      if (request.method === "GET" && pathname === "/api/portal/subsidiary") {
        const authH     = request.headers.get("Authorization") || "";
        const portalKey = authH.startsWith("Bearer ") ? authH.slice(7).trim() : "";
        if (!portalKey) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

        const principal = await verifyAcarsKey(env, portalKey);
        if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

        const userRows = await sbGet(env, `users?acars_key=eq.${encodeURIComponent(portalKey)}&select=subsidiary&limit=1`);
        const subsidiary = userRows?.[0]?.subsidiary || null;
        const movesUsed = await subsidiaryMovesThisMonth(env, portalKey);

        return json({
          subsidiary,
          moves_used_this_month: movesUsed,
          moves_remaining_this_month: Math.max(0, SUBSIDIARY_MONTHLY_MOVE_LIMIT - movesUsed),
        }, 200, corsHeaders);
      }

      // ── PORTAL: SUBSIDIARY CHOOSE/CHANGE ────────────────────────────────────
      // First-ever pick (current subsidiary is null, right after signup) is always free and
      // uncounted. After that, only a move TO cityhopper or cargo counts against the
      // twice-a-month cap -- moving back to mainline is always free/unlimited, per the user's
      // explicit spec ("mainline is excluded from this limit").
      if (request.method === "POST" && pathname === "/api/portal/subsidiary") {
        const authH     = request.headers.get("Authorization") || "";
        const portalKey = authH.startsWith("Bearer ") ? authH.slice(7).trim() : "";
        if (!portalKey) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

        const principal = await verifyAcarsKey(env, portalKey);
        if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

        const body = await safeJson(request);
        if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

        const target = (body.subsidiary || "").trim().toLowerCase();
        if (!SUBSIDIARIES.includes(target))
          return json({ success: false, error: `subsidiary must be one of: ${SUBSIDIARIES.join(", ")}` }, 400, corsHeaders);

        const userRows = await sbGet(env, `users?acars_key=eq.${encodeURIComponent(portalKey)}&select=id,subsidiary&limit=1`);
        const user = userRows?.[0];
        if (!user) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

        const current = user.subsidiary || null;
        if (current === target)
          return json({ success: false, error: `You are already assigned to ${target}.` }, 400, corsHeaders);

        if (current !== null && target !== "mainline") {
          const movesUsed = await subsidiaryMovesThisMonth(env, portalKey);
          if (movesUsed >= SUBSIDIARY_MONTHLY_MOVE_LIMIT) {
            return json({
              success: false,
              error: "You've already used both subsidiary changes this month. You can still move back to Mainline for free, or wait until next month.",
            }, 429, corsHeaders);
          }
        }

        await sbUpdate(env, "users", `id=eq.${user.id}`, { subsidiary: target });
        await sbInsert(env, "subsidiary_change_log", {
          acars_key: portalKey,
          from_subsidiary: current,
          to_subsidiary: target,
          changed_at: new Date().toISOString(),
        });

        return json({ success: true, subsidiary: target }, 200, corsHeaders);
      }

      // ── PORTAL: PIREPS ─────────────────────────────────────────────────────
      if (request.method === "GET" && pathname === "/api/portal/pireps") {
        const authH     = request.headers.get("Authorization") || "";
        const portalKey = authH.startsWith("Bearer ") ? authH.slice(7).trim() : "";
        if (!portalKey) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

        if (!(await verifyAcarsKey(env, portalKey))) {
          return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);
        }

        const rows = await sbGet(env,
          `pireps?acars_key=eq.${encodeURIComponent(portalKey)}&select=id,flight_number,callsign,departure_icao,arrival_icao,aircraft_type,block_minutes,distance_nm,landing_vs_fpm,landing_score,submitted_at,status&order=submitted_at.desc&limit=100`
        );

        return json({ pireps: rows || [] }, 200, corsHeaders);
      }

      // ── PORTAL: MANUAL PIREPS — LIST ───────────────────────────────────────
      if (request.method === "GET" && pathname === "/api/portal/manual-pireps") {
        const authH     = request.headers.get("Authorization") || "";
        const portalKey = authH.startsWith("Bearer ") ? authH.slice(7).trim() : "";
        if (!portalKey) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

        if (!(await verifyAcarsKey(env, portalKey)))
          return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

        const rows = await sbGet(env,
          `manual_pireps?acars_key=eq.${encodeURIComponent(portalKey)}&select=id,flight_number,departure_icao,arrival_icao,aircraft_type,aircraft_reg,block_minutes,route,notes,vatsim_id,screenshot_url,status,admin_note,submitted_at,reviewed_at&order=submitted_at.desc&limit=100`
        );
        return json({ pireps: rows || [] }, 200, corsHeaders);
      }

      // ── PORTAL: MANUAL PIREPS — SUBMIT ─────────────────────────────────────
      if (request.method === "POST" && pathname === "/api/portal/manual-pirep") {
        const authH     = request.headers.get("Authorization") || "";
        const portalKey = authH.startsWith("Bearer ") ? authH.slice(7).trim() : "";
        if (!portalKey) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

        if (!(await verifyAcarsKey(env, portalKey)))
          return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

        if (!(await rateLimit(env, ctx, `manual_pirep:${portalKey}`, LIMITS.manualPirep)))
          return rateLimitResponse(corsHeaders);

        const body = await safeJson(request);
        if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

        const depIcao = (body.departure_icao || "").trim().toUpperCase().substring(0, 4);
        const arrIcao = (body.arrival_icao   || "").trim().toUpperCase().substring(0, 4);
        if (!depIcao || !arrIcao)
          return json({ success: false, error: "departure_icao and arrival_icao are required" }, 400, corsHeaders);

        const aircraftType = (body.aircraft_type || "").trim().substring(0, 50);
        if (!aircraftType)
          return json({ success: false, error: "aircraft_type is required" }, 400, corsHeaders);

        const vatsimId      = (body.vatsim_id      || "").trim().substring(0, 20) || null;
        const screenshotUrl = (body.screenshot_url || "").trim().substring(0, 2048) || null;
        if (!vatsimId && !screenshotUrl)
          return json({ success: false, error: "Either vatsim_id or screenshot_url is required" }, 400, corsHeaders);

        const blockMins = Math.max(0, parseInt(body.block_minutes, 10) || 0);

        const inserted = await sbInsert(env, "manual_pireps", {
          acars_key:      portalKey,
          flight_number:  (body.flight_number || "").trim().substring(0, 20) || null,
          departure_icao: depIcao,
          arrival_icao:   arrIcao,
          aircraft_type:  aircraftType,
          aircraft_reg:   (body.aircraft_reg  || "").trim().substring(0, 20) || null,
          block_minutes:  blockMins,
          route:          (body.route         || "").trim().substring(0, 500) || null,
          notes:          (body.notes         || "").trim().substring(0, 1000) || null,
          vatsim_id:      vatsimId,
          screenshot_url: screenshotUrl,
          status:         "pending",
          submitted_at:   new Date().toISOString(),
        });

        const since      = new Date(Date.now() - 86400000).toISOString();
        const recentRows = await sbGet(env,
          `manual_pireps?acars_key=eq.${encodeURIComponent(portalKey)}&submitted_at=gte.${since}&select=id`
        );
        const remainingToday = Math.max(0, 5 - (recentRows?.length ?? 0));

        return json({ success: true, id: inserted[0]?.id, remainingToday }, 200, corsHeaders);
      }

      // ── PORTAL: MANUAL PIREPS — SCREENSHOT PROXY UPLOAD ────────────────────
      // Browser uploads image to this endpoint; worker forwards to Supabase using
      // the service key so the browser never touches Supabase directly (CORS fix).
      if (request.method === "POST" && pathname === "/api/portal/manual-pirep/screenshot") {
        const authH     = request.headers.get("Authorization") || "";
        const portalKey = authH.startsWith("Bearer ") ? authH.slice(7).trim() : "";
        if (!portalKey) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

        if (!(await verifyAcarsKey(env, portalKey)))
          return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

        const fileBody = await request.arrayBuffer().catch(() => null);
        if (!fileBody || fileBody.byteLength === 0)
          return json({ success: false, error: "Empty file" }, 400, corsHeaders);
        if (fileBody.byteLength > 5 * 1024 * 1024)
          return json({ success: false, error: "Screenshot exceeds 5 MB limit" }, 400, corsHeaders);

        const storagePath = `${portalKey}/${crypto.randomUUID()}.jpg`;
        const uploadRes = await fetch(
          `${env.SUPABASE_URL}/storage/v1/object/pirep-screenshots/${storagePath}`,
          {
            method:  "POST",
            headers: {
              "Authorization": `Bearer ${env.SUPABASE_SERVICE_KEY}`,
              "Content-Type":  "image/jpeg",
              "x-upsert":      "false",
            },
            body: fileBody,
          }
        );
        if (!uploadRes.ok) {
          console.error("Screenshot upload to Supabase failed:", uploadRes.status, await uploadRes.text().catch(() => ""));
          return json({ success: false, error: "Screenshot upload failed" }, 500, corsHeaders);
        }
        return json({ success: true, storagePath }, 200, corsHeaders);
      }

      return json({ success: false, error: "Not found" }, 404, corsHeaders);

    } catch (e) {
      console.error("Worker error:", e);
      return json({ success: false, error: "Internal server error" }, 500, corsHeaders);
    }
  },

  // ── Scheduled handlers ──────────────────────────────────────────────────────
  // Cron triggers (add in Cloudflare Dashboard → Workers → your worker → Triggers):
  //   "0 3 * * *"   — daily cleanup
  //   "*/5 * * * *" — Discord status bot
  //   "*/1 * * * *" — AI flight dispatcher (poll Hoppies VAOPS inbox, reply over telex)
  async scheduled(event, env, ctx) {
    if (event.cron === '0 3 * * *') {
      if (!env.SUPABASE_URL || !env.SUPABASE_SERVICE_KEY) return;
      ctx.waitUntil(
        sbRpc(env, "cleanup_expired_records", {})
          .catch(e => console.error("[Cleanup] scheduled cleanup failed:", e))
      );
    }

    if (event.cron === '*/5 * * * *') {
      if (!env.DISCORD_CHANNEL_ID || !env.DISCORD_BOT_TOKEN || !env.ACARS_KV) {
        console.error('Discord status: missing DISCORD_CHANNEL_ID, DISCORD_BOT_TOKEN, or ACARS_KV');
        return;
      }
      const sbHeaders = {
        'apikey': env.SUPABASE_SERVICE_KEY,
        'Authorization': `Bearer ${env.SUPABASE_SERVICE_KEY}`,
      };
      // 'api' and 'database' probed the exact same Supabase URL — run the
      // check once and reuse the result for both embed fields.
      ctx.waitUntil(
        Promise.all([
          checkService('https://flyaviatesair.uk', 'website'),
          checkService(`${env.SUPABASE_URL}/rest/v1/routes?select=id&limit=1`, 'database', { headers: sbHeaders }),
        ])
          .then(([website, database]) => buildStatusEmbed([website, { ...database, name: 'api' }, database]))
          .then(embed => postOrUpdateStatusEmbed(env, embed))
          .catch(e => console.error('Discord status cron failed:', e))
      );
    }

    if (event.cron === '*/1 * * * *') {
      ctx.waitUntil(
        runAiDispatchCycle(env, ctx).catch(e => console.error('AI dispatch cron failed:', e))
      );
    }
  },
};

// ============================================================
//  AI FLIGHT DISPATCHER — inbound Hoppies ACARS telex → OpenRouter → reply telex
//
//  Runs entirely server-side on a cron trigger, deliberately NOT from the desktop
//  app: the Hoppies inbound queue for the company station (VAOPS) is shared across
//  every pilot running AviatesAirTracker. A client-side poller would see every
//  pilot's messages and could send duplicate replies to the same one — this has to
//  be a single centralized poller, so it lives here next to the other cron jobs.
//
//  Persona: company flight DISPATCH, not ATC. Never issues clearances, headings,
//  altitudes, PDC, or oceanic clearance — those get an in-character decline. No
//  NOTAM feed either — declines those too rather than inventing one.
// ============================================================

const HOPPIE_ENDPOINT = "https://www.hoppie.nl/acars/system/connect.html";
// Same VA operations logon HoppiesAcarsClient.cs uses client-side
// (Core/Backend/HoppiesAcarsClient.cs). Duplicated here because this poll/reply loop
// has to run centrally rather than per-pilot — keep both in sync if this ever rotates.
const DISPATCH_COMPANY_LOGON = "ykWrqTsbepVQAaj";
const DISPATCH_STATION = "VAOPS";
// Same format bookings are validated against at creation time (see POST /api/bookings
// below) — reused here to filter out anything that isn't one of our own pilots before
// spending an LLM call on it.
const DISPATCH_CALLSIGN_RE = /^(VAV|VAC|VAH)\d{1,4}[A-Z]{0,2}$/;
const TELEX_MAX_CHARS = 210; // Hoppies' practical telex limit is ~220 — leave margin.

// Parses Hoppie's poll response format: bare "ok" when the queue is empty, or
// "ok {FACILITY type {packet}} {FACILITY type {packet}} ..." when there are
// messages waiting (confirmed against Hoppie's own connect.html protocol docs).
// Written as a manual brace-depth scanner rather than one regex because packet text
// is free-form pilot input and can itself contain stray braces — a pilot typing a
// literal "{" or "}" in their message can still throw off entry boundaries here;
// worth sending a real test message during setup to confirm this holds up in
// practice rather than trusting the docs alone (this codebase has been bitten
// before by an external API's documented format not matching reality).
function parseHoppiePollResponse(body) {
  const text = (body || "").trim();
  if (!text.startsWith("ok")) return [];
  const rest = text.slice(2).trim();
  const entries = [];
  let i = 0;
  while (i < rest.length) {
    if (rest[i] !== "{") { i++; continue; }
    let depth = 0, start = i;
    for (; i < rest.length; i++) {
      if (rest[i] === "{") depth++;
      else if (rest[i] === "}") { depth--; if (depth === 0) { i++; break; } }
    }
    const inner = rest.slice(start + 1, i - 1);
    const m = inner.match(/^(\S+)\s+(\S+)\s+\{([\s\S]*)\}$/);
    if (m) entries.push({ facility: m[1], type: m[2], packet: m[3] });
  }
  return entries;
}

async function pollHoppieInbound() {
  const url = `${HOPPIE_ENDPOINT}?logon=${encodeURIComponent(DISPATCH_COMPANY_LOGON)}` +
    `&from=${DISPATCH_STATION}&to=${DISPATCH_STATION}&type=poll`;
  const res = await fetch(url);
  const body = await res.text();
  return parseHoppiePollResponse(body);
}

async function sendHoppieTelex(toCallsign, message) {
  const url = `${HOPPIE_ENDPOINT}?logon=${encodeURIComponent(DISPATCH_COMPANY_LOGON)}` +
    `&from=${DISPATCH_STATION}&to=${encodeURIComponent(toCallsign)}&type=telex&packet=${encodeURIComponent(message)}`;
  const res = await fetch(url);
  const body = (await res.text()).trim();
  return res.ok && body.toLowerCase().startsWith("ok");
}

// Splits a reply into <= TELEX_MAX_CHARS chunks on word boundaries (never mid-word —
// this is going out as real ACARS telex text). Hard-capped at 3 telexes per reply so
// a verbose model output can never turn into a spam burst.
function splitTelexChunks(text) {
  const clean = text.replace(/\s+/g, " ").trim();
  if (clean.length <= TELEX_MAX_CHARS) return [clean];
  const words = clean.split(" ");
  const chunks = [];
  let cur = "";
  for (const w of words) {
    if ((cur + " " + w).trim().length > TELEX_MAX_CHARS) {
      if (cur) chunks.push(cur.trim());
      cur = w;
    } else {
      cur = (cur + " " + w).trim();
    }
  }
  if (cur) chunks.push(cur.trim());
  return chunks.slice(0, 3);
}

// Looks up a pilot's live position/route by callsign in the LiveMapRoom Durable
// Object, already fed by the desktop app's existing position/flight-start pushes
// (POST /api/v1/acars/position, /api/v1/acars/flight-start below) — no new
// client-side plumbing needed. Returns null if the callsign isn't currently
// tracked (pilot not flying right now, or the app isn't running).
async function findLiveFlightByCallsign(env, callsign) {
  if (!env.LIVE_MAP_ROOM) return null;
  try {
    const res = await liveMapRoomFetch(env, "/list", { method: "GET" });
    const list = await res.json();
    return list.find(p => (p.callsign || "").toUpperCase() === callsign.toUpperCase()) || null;
  } catch (e) {
    console.error("[AiDispatch] live map lookup failed:", e);
    return null;
  }
}

function buildDispatcherSystemPrompt(flight) {
  const persona = [
    "You are AVIATES OPS, the company flight dispatcher for the AviatesAir virtual airline,",
    "replying to a pilot over the real Hoppies ACARS network as a plain-text telex.",
    "Voice: a real airline dispatcher — terse, professional, standard dispatch phraseology",
    "and abbreviations (RTE, DEP, ARR, WX, ADVS, CONFIRM, FLT, ETA). No greetings, no",
    "sign-off, no pleasantries, no markdown, no emoji.",
    `Keep the ENTIRE reply under ${TELEX_MAX_CHARS} characters — one short telex.`,
    "",
    "You are DISPATCH, not ATC. Never issue or invent a clearance, PDC, oceanic",
    "clearance, heading, squawk, or altitude assignment. If asked for one, decline in",
    "character and tell the pilot to contact ATC / clearance delivery directly.",
    "You have no NOTAM feed — if asked for NOTAMs, tell them to check the OFP.",
  ].join(" ");

  const context = flight
    ? `Live flight data for ${flight.callsign}: departure ${flight.departureIcao || "?"}, ` +
      `arrival ${flight.arrivalIcao || "?"}, position ${flight.lat != null ? flight.lat.toFixed(2) : "?"},` +
      `${flight.lon != null ? flight.lon.toFixed(2) : "?"}, altitude ${flight.alt || 0}ft, ` +
      `ground speed ${flight.speed || 0}kt, phase ${flight.phase || "unknown"}.`
    : "No live telemetry is available for this aircraft right now — answer generally, " +
      "and say so plainly if a precise figure was asked for.";

  return `${persona}\n\n${context}`;
}

// Free OpenRouter models get retired without notice (the original default,
// meta-llama/llama-3.1-8b-instruct:free, disappeared and every reply silently failed)
// and their shared pools are often rate-limited upstream (Google's Gemma pool returns
// 429 at busy times). So: OPENROUTER_MODEL from the dashboard first (set it to a cheap
// paid model such as mistralai/mistral-small-3.2-24b-instruct for dependable replies),
// then free models spread across different providers so one saturated pool can't take
// the dispatcher down. Checked against openrouter.ai/api/v1/models, Oct 2026.
const DISPATCH_FALLBACK_MODELS = [
  "google/gemma-4-31b-it:free",              // Google AI Studio
  "nvidia/nemotron-3.5-lightning:free",      // Nvidia
  "thinkingmachines/inkling-small:free",     // Thinking Machines
  "google/gemma-4-26b-a4b-it:free",          // Google AI Studio
  "nvidia/nemotron-3-super-120b-a12b:free",  // Nvidia
  "liquid/lfm-2.5-2.6b:free",                // Liquid
];

// The secret as an OpenRouter key: forgives the usual paste slips (surrounding quotes,
// a "Bearer " prefix, stray whitespace). OpenRouter answers anything that isn't shaped
// like "sk-or-…" with a misleading "Missing Authentication header".
function openRouterKey(env) {
  return String(env.OPENROUTER_API_KEY || "").trim()
    .replace(/^["']|["']$/g, "")
    .replace(/^Bearer\s+/i, "")
    .trim();
}

// Workers AI: Cloudflare-hosted models on this same account. No API key and no shared
// third-party pool; 10,000 Neurons a day are free (a reply is ~50 Neurons on Llama 3.3
// 70B, ~8 on Llama 3.1 8B), billed at $0.011 / 1,000 beyond that on Workers Paid.
const WORKERS_AI_MODELS = [
  "@cf/meta/llama-3.3-70b-instruct-fp8-fast",
  "@cf/meta/llama-3.1-8b-instruct-fp8-fast",
];

async function callWorkersAi(env, systemPrompt, userMessage) {
  const models = [env.WORKERS_AI_MODEL, ...WORKERS_AI_MODELS].filter((m, i, a) => m && a.indexOf(m) === i);
  const errors = [];
  for (const model of models) {
    try {
      const out = await env.AI.run(model, {
        messages: [
          { role: "system", content: systemPrompt },
          { role: "user", content: userMessage },
        ],
        max_tokens: 300,
        temperature: 0.4,
      });
      // Workers AI text models answer { response }, the OpenAI-compatible ones { choices }
      const raw = out?.response ?? out?.choices?.[0]?.message?.content ?? "";
      const text = (typeof raw === "string" ? raw : JSON.stringify(raw)).trim();
      if (text) return { text, model };
      errors.push(`${model} → empty reply`);
    } catch (e) {
      errors.push(`${model} → ${e && e.message ? e.message : e}`);
    }
  }
  throw new Error(`Workers AI: no model replied. ${errors.join(" | ")}`);
}

// The dispatcher's model: Workers AI when the AI binding exists, then OpenRouter when a
// key is set. Returns { text, model }; throws with every engine's reason if all fail.
async function callDispatcherModel(env, systemPrompt, userMessage) {
  const errors = [];
  if (env.AI) {
    try { return await callWorkersAi(env, systemPrompt, userMessage); }
    catch (e) { errors.push(e.message); }
  }
  if (env.OPENROUTER_API_KEY) {
    try { return { text: await callOpenRouter(env, systemPrompt, userMessage), model: "openrouter" }; }
    catch (e) { errors.push(e.message); }
  }
  if (!errors.length) errors.push("no AI binding and no OPENROUTER_API_KEY configured");
  throw new Error(errors.join(" || "));
}

async function callOpenRouter(env, systemPrompt, userMessage) {
  const key = openRouterKey(env);
  if (!key.startsWith("sk-or-")) {
    // never log the key itself, only what's wrong with its shape
    throw new Error(`OPENROUTER_API_KEY doesn't look like an OpenRouter key (expected "sk-or-v1-…", got ${key.length} characters starting "${key.slice(0, 3)}…"). Create one at openrouter.ai/keys and paste it into the secret on its own.`);
  }
  const models = [env.OPENROUTER_MODEL, ...DISPATCH_FALLBACK_MODELS].filter((m, i, a) => m && a.indexOf(m) === i);
  const errors = [];
  for (const model of models) {
    try {
      const res = await fetch("https://openrouter.ai/api/v1/chat/completions", {
        method: "POST",
        headers: {
          "Authorization": `Bearer ${key}`,
          "Content-Type": "application/json",
        },
        body: JSON.stringify({
          model,
          // the instructions travel in the user turn: some providers (Gemma on Google AI
          // Studio) reject a separate system role
          messages: [{ role: "user", content: `${systemPrompt}\n\nPILOT TELEX:\n${userMessage}` }],
          // every current free model can "think" first: keep that short and out of the
          // reply, and leave room for it so the answer itself isn't cut off (an empty
          // reply was the result before). The telex is still capped at 3 parts later.
          reasoning: { effort: "low", exclude: true },
          max_tokens: 1500,
          temperature: 0.4,
        }),
      });
      if (!res.ok) { errors.push(`${model} → ${res.status} ${(await res.text().catch(() => "")).slice(0, 200)}`); continue; }
      const data = await res.json();
      const text = (data.choices?.[0]?.message?.content || "").trim();
      if (text) return text;
      errors.push(`${model} → empty reply`);
    } catch (e) {
      errors.push(`${model} → ${e}`);
    }
  }
  throw new Error(`OpenRouter: no model replied. ${errors.join(" | ")}`);
}

// One inbound message → one grounded (or ungrounded) dispatcher reply. Never throws —
// any failure is logged and swallowed so one bad message can't break the whole cycle.
async function handleInboundDispatchMessage(env, ctx, entry) {
  const callsign = (entry.facility || "").toUpperCase();
  if (!DISPATCH_CALLSIGN_RE.test(callsign)) return; // not one of our pilots — skip, no LLM spent

  const requestText = (entry.packet || "").trim().slice(0, 500);
  if (!requestText) return;

  if (!(await rateLimit(env, ctx, `ai_dispatch:${callsign}`, LIMITS.aiDispatchReply))) return;

  try {
    const flight = await findLiveFlightByCallsign(env, callsign);
    const systemPrompt = buildDispatcherSystemPrompt(flight);
    const { text: reply, model } = await callDispatcherModel(env, systemPrompt, requestText);
    if (!reply) return;
    console.log(`[AiDispatch] replied to ${callsign} with ${model}`);

    for (const chunk of splitTelexChunks(reply)) {
      await sendHoppieTelex(callsign, chunk);
    }

    if (env.SUPABASE_URL && env.SUPABASE_SERVICE_KEY) {
      ctx.waitUntil(
        sbInsert(env, "dispatch_ai_log", {
          callsign,
          request_text: requestText,
          reply_text: reply,
          matched_live_flight: !!flight,
          sent_at: new Date().toISOString(),
        }).catch(e => console.error("[AiDispatch] log insert failed:", e))
      );
    }
  } catch (e) {
    // log the message as plain text: Cloudflare's log view keeps only the stack of an Error
    // object, which drops the per-model OpenRouter reasons this message carries
    console.error(`[AiDispatch] failed for ${callsign}: ${e && e.message ? e.message : String(e)}`);
    // the poll already consumed the pilot's telex, so tell them rather than go silent
    await sendHoppieTelex(callsign, "AVIATES OPS: UNABLE TO PROCESS YOUR MESSAGE RIGHT NOW. PLEASE RESEND IN A FEW MINUTES.")
      .catch(() => {});
  }
}

async function runAiDispatchCycle(env, ctx) {
  if (env.AI_DISPATCH_ENABLED === "false") return;
  if (!env.AI && !env.OPENROUTER_API_KEY) {
    // nothing could answer, so leave the VAOPS queue unread rather than eat pilots' telexes
    console.error("[AiDispatch] no AI binding and no OPENROUTER_API_KEY — skipping cycle");
    return;
  }

  let entries;
  try {
    entries = await pollHoppieInbound();
  } catch (e) {
    console.error("[AiDispatch] Hoppies poll failed:", e);
    return;
  }

  const telexes = entries.filter(e => e.type === "telex" && e.packet);
  for (const entry of telexes) {
    await handleInboundDispatchMessage(env, ctx, entry);
  }
}

// ============================================================
//  ROUTES API — backed by Supabase (was Cloudflare D1)
// ============================================================
async function handleRoutesApi(request, env, corsHeaders, ctx) {
  if (!env.SUPABASE_URL || !env.SUPABASE_SERVICE_KEY) {
    return json({ success: false, error: "Supabase not configured" }, 500, corsHeaders);
  }

  const url      = new URL(request.url);
  const pathname = url.pathname;

  if (request.method !== "GET") {
    return json({ error: "Method not allowed" }, 405, corsHeaders);
  }

  try {

    // GET /api/airports?iata=CWL,LHR
    if (pathname === "/api/airports") {
      const iataParam = url.searchParams.get("iata") || "";
      const codes = iataParam.split(",").map(c => c.trim().toUpperCase()).filter(Boolean).slice(0, 20);

      if (codes.length === 0) {
        return json({ error: "iata param required" }, 400, corsHeaders);
      }

      const cached = await fromCache(request, corsHeaders);
      if (cached) return cached;

      const rows = await sbRpc(env, "get_airports", { iata_codes: codes });
      return cachedJsonResponse(ctx, request, { data: rows || [] }, 600, corsHeaders);
    }

    // GET /api/routes/summary
    if (pathname === "/api/routes/summary") {
      const cached = await fromCache(request, corsHeaders);
      if (cached) return cached;

      // A route flown by two subsidiaries counts under each, so the total is a plain row
      // count rather than the sum of the per-fleet figures.
      const [rows, total] = await Promise.all([sbRpc(env, "get_routes_summary"), sbCount(env, "routes", "id=gt.0")]);
      const summary = { mainline: 0, cargo: 0, cityhopper: 0, total };
      for (const row of (rows || [])) {
        summary[row.fleet_group] = Number(row.count);
      }
      return cachedJsonResponse(ctx, request, { summary }, 86400, corsHeaders);
    }

    // GET /api/routes/:id
    const idMatch = pathname.match(/^\/api\/routes\/(\d+)$/);
    if (idMatch) {
      const id = parseInt(idMatch[1], 10);
      if (isNaN(id) || id < 1) return json({ error: "Invalid route id" }, 400, corsHeaders);

      const cached = await fromCache(request, corsHeaders);
      if (cached) return cached;

      const rows = await sbGet(env, `routes?id=eq.${id}&select=*&limit=1`);
      if (!rows || rows.length === 0) return json({ error: "Route not found" }, 404, corsHeaders);

      return cachedJsonResponse(ctx, request, { data: rows[0] }, 86400, corsHeaders);
    }

    // GET /api/routes
    if (pathname === "/api/routes") {
      const fleetParam = url.searchParams.get("fleet");
      let fleetFilter  = [];
      if (fleetParam) {
        fleetFilter = fleetParam.split(",")
          .map(f => f.trim().toLowerCase())
          .filter(f => VALID_FLEET_GROUPS.includes(f));
        if (fleetFilter.length === 0) {
          return cachedJsonResponse(ctx, request, { meta: { count: 0, limit: 200, offset: 0 }, data: [] }, 86400, corsHeaders);
        }
      }

      const limit      = Math.min(Math.max(parseInt(url.searchParams.get("limit")  || "200", 10) || 200, 1), 1000);
      const offset     = Math.max(parseInt(url.searchParams.get("offset") || "0",   10) || 0, 0);
      const q          = (url.searchParams.get("q")      || "").trim().substring(0, 50);
      const originParam = (url.searchParams.get("origin") || "").trim().toUpperCase().substring(0, 4);

      const cached = await fromCache(request, corsHeaders);
      if (cached) return cached;

      const result = await sbRpc(env, "get_routes_list", {
        p_fleet:  fleetFilter.length > 0 ? fleetFilter : null,
        p_q:      q      || null,
        p_origin: originParam || null,
        p_limit:  limit,
        p_offset: offset,
      });

      return cachedJsonResponse(ctx, request, { meta: { count: result.count, limit, offset }, data: result.data || [] }, 86400, corsHeaders);
    }

    return json({ error: "Not found" }, 404, corsHeaders);

  } catch (e) {
    console.error("Routes API error:", e);
    return json({ error: "Internal server error" }, 500, corsHeaders);
  }
}

// ============================================================
//  EVENTS API — backed by Supabase (was Cloudflare D1)
// ============================================================
async function handleEventsApi(request, env, corsHeaders, ctx) {
  if (!env.SUPABASE_URL || !env.SUPABASE_SERVICE_KEY) {
    return json({ error: "Supabase not configured" }, 500, corsHeaders);
  }

  const url      = new URL(request.url);
  const pathname = url.pathname;
  const method   = request.method;
  const auth     = request.headers.get("Authorization") || "";
  const acarsKey = auth.startsWith("Bearer ") ? auth.slice(7).trim() : "";

  try {

    // GET /api/events/my — personalized, never cache
    if (method === "GET" && pathname === "/api/events/my") {
      const principal = await verifyAcarsKey(env, acarsKey);
      if (!principal) return json({ error: "Invalid or missing ACARS key" }, 401, corsHeaders);

      const rows = await sbRpc(env, "get_events_by_pilot", { p_acars_key: acarsKey });
      return new Response(
        JSON.stringify({ data: rows || [] }),
        { status: 200, headers: Object.assign({ "Content-Type": "application/json", "Cache-Control": "no-store" }, corsHeaders) }
      );
    }

    // GET /api/events/:id
    const idMatch = pathname.match(/^\/api\/events\/(\d+)$/);
    if (method === "GET" && idMatch) {
      const id = parseInt(idMatch[1], 10);
      if (isNaN(id) || id < 1) return json({ error: "Invalid event id" }, 400, corsHeaders);

      const cached = await fromCache(request, corsHeaders);
      if (cached) return cached;

      const rows = await sbRpc(env, "get_event_by_id", { p_id: id });
      if (!rows || rows.length === 0) return json({ error: "Event not found" }, 404, corsHeaders);

      return cachedJsonResponse(ctx, request, { data: rows[0] }, 3600, corsHeaders);
    }

    // GET /api/events?filter=upcoming|past|all
    if (method === "GET" && pathname === "/api/events") {
      const cached = await fromCache(request, corsHeaders);
      if (cached) return cached;

      const filter = (url.searchParams.get("filter") || "upcoming").toLowerCase();
      const rows   = await sbRpc(env, "get_events_list", { p_filter: filter });
      return cachedJsonResponse(ctx, request, { data: rows || [] }, 3600, corsHeaders);
    }

    // POST /api/events (create event)
    if (method === "POST" && pathname === "/api/events") {
      const principal = await verifyAcarsKey(env, acarsKey);
      if (!principal) return json({ error: "Invalid or missing ACARS key" }, 401, corsHeaders);

      if (!(await rateLimit(env, ctx, `eventcreate:${acarsKey}`, LIMITS.eventCreate)))
        return rateLimitResponse(corsHeaders);

      // One pilot-created event per rolling 30 days. Checked against the DB directly (not the
      // in-memory rate limiter above, which only covers short bursts and resets on isolate
      // recycle) since this window is long enough that it must survive across requests/isolates.
      const monthAgo = new Date(Date.now() - 30 * 24 * 60 * 60 * 1000).toISOString();
      const recentCount = await sbCount(env, "events",
        `created_by=eq.${encodeURIComponent(acarsKey)}&created_at=gt.${encodeURIComponent(monthAgo)}`);
      if (recentCount >= 1) {
        return json({ error: "You can only create 1 event per month" }, 429, corsHeaders);
      }

      const body = await safeJson(request);
      if (!body) return json({ error: "Invalid JSON" }, 400, corsHeaders);

      const title               = (body.title               || "").trim().substring(0, 100);
      const description         = (body.description         || "").trim().substring(0, 500);
      const eventDate           = (body.eventDate           || "").trim();
      const timeUtc             = (body.timeUtc             || "").trim().substring(0, 10);
      const route               = (body.route               || "").trim().substring(0, 100);
      const aircraftRestriction = (body.aircraftRestriction || "").trim().substring(0, 100);

      if (!title) return json({ error: "Title is required" }, 400, corsHeaders);
      if (!eventDate || !/^\d{4}-\d{2}-\d{2}$/.test(eventDate)) {
        return json({ error: "Valid event date (YYYY-MM-DD) required" }, 400, corsHeaders);
      }
      // Reject any date/time already in the past -- server-side backstop for the client's own
      // CalendarDatePicker.MinDate/time check (CreateEventDialog.cs), since a client check alone
      // can always be bypassed by calling this endpoint directly.
      const eventDateTime = new Date(`${eventDate}T${/^\d{2}:\d{2}$/.test(timeUtc) ? timeUtc : "00:00"}:00Z`);
      if (isNaN(eventDateTime.getTime()) || eventDateTime <= new Date()) {
        return json({ error: "Event date/time must be in the future" }, 400, corsHeaders);
      }
      if (containsBlockedContent(title) || containsBlockedContent(description))
        return json({ error: "Event contains inappropriate content" }, 422, corsHeaders);

      const { user } = principal;
      const createdByName = `${user.first_name} ${user.last_name}`.trim();
      const createdAt     = new Date().toISOString();

      // rank_restriction/max_participants are no longer settable by pilots -- every event is
      // open to any rank and unlimited participants now (both columns stay in the table for any
      // existing rows, just never written from here again).
      const rows = await sbInsert(env, "events", {
        title, description, event_date: eventDate, time_utc: timeUtc,
        route, aircraft_restriction: aircraftRestriction,
        rank_restriction: "", created_by: acarsKey,
        created_by_name: createdByName, created_at: createdAt,
        max_participants: 0, status: "upcoming",
      });

      return json({ success: true, id: rows[0]?.id }, 200, corsHeaders);
    }

    // ── GET /api/events/:id/attendees ────────────────────────────────────────
    // Attendee names for the "who's attending" list on an event's card/detail view. Public (no
    // auth) -- matches GET /api/events/:id's own no-auth-required visibility.
    const attendeesMatch = pathname.match(/^\/api\/events\/(\d+)\/attendees$/);
    if (method === "GET" && attendeesMatch) {
      const eventId = parseInt(attendeesMatch[1], 10);
      if (isNaN(eventId) || eventId < 1) return json({ error: "Invalid event id" }, 400, corsHeaders);

      const rows = await sbGet(env,
        `event_registrations?event_id=eq.${eventId}&select=pilot_name,registered_at&order=registered_at.asc`);
      return json({ attendees: rows || [] }, 200, corsHeaders);
    }

    // POST /api/events/:id/register|unregister
    const regMatch = pathname.match(/^\/api\/events\/(\d+)\/(register|unregister)$/);
    if (method === "POST" && regMatch) {
      const principal = await verifyAcarsKey(env, acarsKey);
      if (!principal) return json({ error: "Invalid or missing ACARS key" }, 401, corsHeaders);

      const eventId = parseInt(regMatch[1], 10);
      const action  = regMatch[2];

      const body      = await safeJson(request);

      const eventRows = await sbRpc(env, "get_event_by_id", { p_id: eventId });
      if (!eventRows || eventRows.length === 0) return json({ error: "Event not found" }, 404, corsHeaders);
      const event = eventRows[0];

      if (action === "register") {
        if (event.status === "past" || event.status === "cancelled") {
          return json({ error: "Cannot register for a past or cancelled event" }, 400, corsHeaders);
        }
        if (event.max_participants > 0) {
          const count = await sbCount(env, "event_registrations", `event_id=eq.${eventId}`);
          if (count >= event.max_participants) {
            return json({ error: "This event is full" }, 409, corsHeaders);
          }
        }
        const { user } = principal;
        const pilotName    = `${user.first_name} ${user.last_name}`.trim();
        const registeredAt = new Date().toISOString();
        try {
          await sbInsert(env, "event_registrations", {
            event_id: eventId, acars_key: acarsKey, pilot_name: pilotName, registered_at: registeredAt,
          });
          return json({ success: true, message: "Registered successfully" }, 200, corsHeaders);
        } catch (e) {
          if (e.pgCode === "23505") {
            return json({ error: "You are already registered for this event" }, 409, corsHeaders);
          }
          throw e;
        }
      } else {
        await sbDelete(env, `event_registrations?event_id=eq.${eventId}&acars_key=eq.${acarsKey}`);
        return json({ success: true, message: "Unregistered successfully" }, 200, corsHeaders);
      }
    }

    return json({ error: "Not found" }, 404, corsHeaders);

  } catch (e) {
    console.error("Events API error:", e);
    return json({ error: "Internal server error" }, 500, corsHeaders);
  }
}

// ============================================================
//  BOOKINGS API — backed by Supabase (was Cloudflare D1)
// ============================================================
const MAX_BOOKINGS = 5;

async function handleBookingsApi(request, env, corsHeaders) {
  if (!env.SUPABASE_URL || !env.SUPABASE_SERVICE_KEY) {
    return json({ error: "Supabase not configured" }, 500, corsHeaders);
  }

  const url      = new URL(request.url);
  const pathname = url.pathname;
  const method   = request.method;

  function authenticate() {
    const auth = request.headers.get("Authorization") || "";
    const key  = auth.startsWith("Bearer ") ? auth.slice(7).trim() : "";
    return verifyAcarsKey(env, key);
  }

  try {

    // GET /api/bookings — personalized, never cache
    if (method === "GET" && pathname === "/api/bookings") {
      const principal = await authenticate();
      if (!principal) return json({ error: "Invalid or missing ACARS key" }, 401, corsHeaders);

      const rows = await sbRpc(env, "get_bookings_by_pilot", { p_acars_key: principal.acarsKey });
      return json({ bookings: rows || [] }, 200, Object.assign({ "Cache-Control": "no-store" }, corsHeaders));
    }

    // POST /api/bookings
    if (method === "POST" && pathname === "/api/bookings") {
      const principal = await authenticate();
      if (!principal) return json({ error: "Invalid or missing ACARS key" }, 401, corsHeaders);

      const body = await safeJson(request);
      if (!body) return json({ error: "Invalid JSON" }, 400, corsHeaders);

      const isCharter    = !!body.is_charter;
      const callsign     = (body.callsign      || "").trim().toUpperCase().substring(0, 10);
      const aircraftType = (body.aircraft_type || "").trim().substring(0, 20);
      const registration = (body.registration  || "").trim().substring(0, 20) || null;
      const scheduledDep = (body.scheduled_dep || "").trim();

      if (!callsign)                  return json({ error: "callsign required"     }, 400, corsHeaders);
      if (!aircraftType)              return json({ error: "aircraft_type required" }, 400, corsHeaders);
      if (!scheduledDep)              return json({ error: "scheduled_dep required" }, 400, corsHeaders);

      // Server-side re-check of the grounding the app's own registration dropdown already
      // filters -- the dropdown just hides a grounded tail number, it doesn't stop a request
      // built directly against this endpoint from naming one.
      if (registration) {
        const regRows = await sbGet(env,
          `aircraft_registrations?registration=eq.${encodeURIComponent(registration)}&select=status,grounded_until&limit=1`);
        const reg = regRows?.[0];
        if (reg && reg.status === "inspection" && (!reg.grounded_until || reg.grounded_until > new Date().toISOString())) {
          return json({ error: `${registration} is grounded for inspection${reg.grounded_until ? ` until ${reg.grounded_until}` : ""}` }, 409, corsHeaders);
        }
      }

      if (!/^(VAV|VAC|VAH)\d{1,4}[A-Z]{0,2}$/.test(callsign) || callsign.length > 7) {
        return json({ error: "Invalid callsign format" }, 400, corsHeaders);
      }

      const depDate = new Date(scheduledDep);
      if (isNaN(depDate.getTime()) || depDate <= new Date()) {
        return json({ error: "scheduled_dep must be a future UTC datetime" }, 400, corsHeaders);
      }

      const maxFuture = new Date();
      maxFuture.setDate(maxFuture.getDate() + 90);
      if (depDate > maxFuture) {
        return json({ error: "Bookings cannot be made more than 90 days in advance" }, 400, corsHeaders);
      }

      // A charter must still fly one of our real aircraft (server-side, not just the app's
      // dropdown) — but it can go anywhere, so there's no route-network check for it at all.
      // A normal booking keeps the route-network check it always had.
      let routeId = null;
      let charterOriginIcao = null;
      let charterDestIcao = null;

      if (isCharter) {
        charterOriginIcao = (body.charter_origin_icao || "").trim().toUpperCase().substring(0, 4);
        charterDestIcao   = (body.charter_dest_icao   || "").trim().toUpperCase().substring(0, 4);

        if (!/^[A-Z0-9]{4}$/.test(charterOriginIcao) || !/^[A-Z0-9]{4}$/.test(charterDestIcao)) {
          return json({ error: "Valid 4-letter departure and arrival ICAO codes required" }, 400, corsHeaders);
        }
        if (charterOriginIcao === charterDestIcao) {
          return json({ error: "Departure and arrival must be different airports" }, 400, corsHeaders);
        }

        const typeRows = await sbGet(env, `aircraft_types?type_code=eq.${encodeURIComponent(aircraftType)}&select=type_code&limit=1`);
        if (!typeRows || typeRows.length === 0) {
          return json({ error: "Aircraft type is not part of the AviatesAir fleet" }, 400, corsHeaders);
        }
      } else {
        routeId = parseInt(body.route_id, 10);
        if (!routeId || isNaN(routeId)) return json({ error: "Valid route_id required" }, 400, corsHeaders);

        const routeRows = await sbGet(env, `routes?id=eq.${routeId}&select=*&limit=1`);
        if (!routeRows || routeRows.length === 0) {
          return json({ error: "Route not found" }, 404, corsHeaders);
        }

        // The booked aircraft must be one the route is flown with; its subsidiary (not the
        // route's first aircraft's) is what the pilot's subsidiary is checked against.
        const routeAircraft = routeAircraftList(routeRows[0]);
        const bookedAircraft = routeAircraft.find(e => sameAircraftType(e.type, aircraftType));
        if (routeAircraft.length > 0 && !bookedAircraft) {
          return json({
            error: `This route isn't flown with ${aircraftType}. Choose one of: ${routeAircraft.map(e => e.type).join(", ")}.`,
          }, 400, corsHeaders);
        }

        // Subsidiary lock: a normal (non-charter) booking must be for the pilot's own
        // subsidiary's routes. Charter (the `if` branch above) deliberately has no equivalent
        // check -- per spec, charter always bypasses the subsidiary restriction. A pilot who
        // hasn't chosen a subsidiary yet (subsidiary is null) is blocked from scheduled
        // bookings entirely rather than left unrestricted -- an unrestricted null was this
        // gate's original behavior, but it meant an app-only pilot who never visits the
        // website (the only place the subsidiary picker lives) could book across every
        // subsidiary forever. Charter still works with no subsidiary chosen.
        const pilotRows = await sbGet(env,
          `users?acars_key=eq.${encodeURIComponent(principal.acarsKey)}&select=subsidiary&limit=1`);
        const pilotSubsidiary = pilotRows?.[0]?.subsidiary || null;
        const routeSubsidiary = bookedAircraft?.fleet_group || routeRows[0].fleet_group;
        if (!pilotSubsidiary) {
          return json({
            error: "Choose a subsidiary (Mainline, Cityhopper, or Sky Cargo) at flyaviatesair.uk/portal/profile before booking a scheduled route -- or use Charter, which has no subsidiary restriction.",
          }, 403, corsHeaders);
        }
        if (routeSubsidiary && pilotSubsidiary !== routeSubsidiary) {
          return json({
            error: `This route belongs to a different subsidiary (${routeSubsidiary}). You're currently assigned to ${pilotSubsidiary} -- switch subsidiary in your portal profile, or use Charter to bypass this.`,
          }, 403, corsHeaders);
        }
      }

      // Enforce max active bookings
      const now        = new Date().toISOString();
      const activeCount = await sbCount(env, "bookings",
        `acars_key=eq.${principal.acarsKey}&status=eq.confirmed&scheduled_dep=gt.${encodeURIComponent(now)}`);

      if (activeCount >= MAX_BOOKINGS) {
        return json({ error: `You already have ${MAX_BOOKINGS} active bookings` }, 409, corsHeaders);
      }

      const createdAt = new Date().toISOString();
      const inserted  = await sbInsert(env, "bookings", {
        acars_key: principal.acarsKey, route_id: routeId,
        charter_origin_icao: charterOriginIcao, charter_dest_icao: charterDestIcao,
        callsign, aircraft_type: aircraftType, registration, scheduled_dep: scheduledDep,
        status: "confirmed", created_at: createdAt,
      });
      const newId = inserted[0]?.id;

      const created = await sbRpc(env, "get_booking_by_id", { p_id: newId });
      return json(created?.[0] ?? inserted[0], 200, Object.assign({ "Cache-Control": "no-store" }, corsHeaders));
    }

    // POST /api/bookings/:id/activate — one-way: opens the pilot's SimBrief dispatch (client-side)
    // and locks the booking so it can no longer be cancelled. Idempotency is deliberately NOT
    // offered here (a second call 409s) — there is no "deactivate".
    const activateMatch = pathname.match(/^\/api\/bookings\/(\d+)\/activate$/);
    if (method === "POST" && activateMatch) {
      const principal = await authenticate();
      if (!principal) return json({ error: "Invalid or missing ACARS key" }, 401, corsHeaders);

      const bookingId = parseInt(activateMatch[1], 10);
      if (isNaN(bookingId) || bookingId < 1) return json({ error: "Invalid booking id" }, 400, corsHeaders);

      const rows = await sbGet(env, `bookings?id=eq.${bookingId}&select=id,acars_key,status,activated_at&limit=1`);
      if (!rows || rows.length === 0) return json({ error: "Booking not found" }, 404, corsHeaders);

      const booking = rows[0];
      if (booking.acars_key !== principal.acarsKey) return json({ error: "Forbidden" }, 403, corsHeaders);
      if (booking.status !== "confirmed") return json({ error: "Only a confirmed booking can be activated" }, 409, corsHeaders);
      if (booking.activated_at)           return json({ error: "Booking is already activated" }, 409, corsHeaders);

      await sbUpdate(env, "bookings", `id=eq.${bookingId}`, { activated_at: new Date().toISOString() });

      const updated = await sbRpc(env, "get_booking_by_id", { p_id: bookingId });
      return json(updated?.[0] ?? { success: true }, 200, corsHeaders);
    }

    // DELETE /api/bookings/:id
    const deleteMatch = pathname.match(/^\/api\/bookings\/(\d+)$/);
    if (method === "DELETE" && deleteMatch) {
      const principal = await authenticate();
      if (!principal) return json({ error: "Invalid or missing ACARS key" }, 401, corsHeaders);

      const bookingId = parseInt(deleteMatch[1], 10);
      if (isNaN(bookingId) || bookingId < 1) return json({ error: "Invalid booking id" }, 400, corsHeaders);

      const rows = await sbGet(env, `bookings?id=eq.${bookingId}&select=id,acars_key,status,activated_at&limit=1`);
      if (!rows || rows.length === 0) return json({ error: "Booking not found" }, 404, corsHeaders);

      const booking = rows[0];
      if (booking.acars_key !== principal.acarsKey) return json({ error: "Forbidden" }, 403, corsHeaders);
      if (booking.status === "cancelled")           return json({ error: "Booking is already cancelled" }, 409, corsHeaders);
      if (booking.activated_at)                     return json({ error: "Booking is activated and can no longer be cancelled" }, 409, corsHeaders);

      await sbUpdate(env, "bookings", `id=eq.${bookingId}`, { status: "cancelled" });
      return json({ success: true }, 200, corsHeaders);
    }

    // PATCH /api/bookings/:id — mark booking as completed
    const patchMatch = pathname.match(/^\/api\/bookings\/(\d+)$/);
    if (method === "PATCH" && patchMatch) {
      const principal = await authenticate();
      if (!principal) return json({ error: "Invalid or missing ACARS key" }, 401, corsHeaders);

      const bookingId = parseInt(patchMatch[1], 10);
      if (isNaN(bookingId) || bookingId < 1) return json({ error: "Invalid booking id" }, 400, corsHeaders);

      const rows = await sbGet(env, `bookings?id=eq.${bookingId}&select=id,acars_key,status&limit=1`);
      if (!rows || rows.length === 0) return json({ error: "Booking not found" }, 404, corsHeaders);

      const booking = rows[0];
      if (booking.acars_key !== principal.acarsKey) return json({ error: "Forbidden" }, 403, corsHeaders);
      if (booking.status !== "confirmed")
        return json({ error: "Booking cannot be completed — it is not in confirmed status" }, 409, corsHeaders);

      await sbUpdate(env, "bookings", `id=eq.${bookingId}`, {
        status:       "completed",
        completed_at: new Date().toISOString(),
      });
      return json({ success: true }, 200, corsHeaders);
    }

    return json({ error: "Not found" }, 404, corsHeaders);

  } catch (e) {
    console.error("Bookings API error:", e);
    return json({ error: "Internal server error" }, 500, corsHeaders);
  }
}

// ============================================================
//  FLEET API — backed by Supabase (was Cloudflare D1 FLEET_DB)
// ============================================================
// ============================================================
//  PILOT POSITION API — GET/PATCH /api/pilot/position
// ============================================================
async function handlePilotPositionApi(request, env, corsHeaders) {
  if (!env.SUPABASE_URL || !env.SUPABASE_SERVICE_KEY) {
    return json({ error: "Supabase not configured" }, 500, corsHeaders);
  }

  try {
    const authH    = request.headers.get("Authorization") || "";
    const acarsKey = authH.startsWith("Bearer ") ? authH.slice(7).trim() : "";
    if (!acarsKey) return json({ error: "Unauthorized" }, 401, corsHeaders);

    const authRows = await sbGet(env,
      `users?acars_key=eq.${encodeURIComponent(acarsKey)}&select=current_airport_iata,disabled&limit=1`
    );
    if (!authRows || authRows.length === 0 || authRows[0].disabled) {
      return json({ error: "Unauthorized" }, 401, corsHeaders);
    }

    if (request.method === "GET") {
      return json({ current_airport_iata: authRows[0].current_airport_iata || "" }, 200, corsHeaders);
    }

    if (request.method === "PATCH") {
      const body = await safeJson(request);
      if (!body) return json({ error: "Invalid JSON" }, 400, corsHeaders);

      if (!("current_airport_iata" in body)) {
        return json({ error: "current_airport_iata is required" }, 400, corsHeaders);
      }

      const iata = (body.current_airport_iata ?? "").trim().toUpperCase();
      if (!/^[A-Z]{0,4}$/.test(iata)) {
        return json({ error: "Invalid airport code — must be 0–4 uppercase letters" }, 400, corsHeaders);
      }

      await sbUpdate(env, "users", `acars_key=eq.${encodeURIComponent(acarsKey)}`, {
        current_airport_iata: iata,
      });
      return json({ success: true }, 200, corsHeaders);
    }

    return json({ error: "Method not allowed" }, 405, corsHeaders);

  } catch (e) {
    console.error("Pilot position API error:", e);
    return json({ error: "Internal server error" }, 500, corsHeaders);
  }
}

async function handleFleetApi(request, env, corsHeaders, ctx) {
  if (!env.SUPABASE_URL || !env.SUPABASE_SERVICE_KEY) {
    return json({ error: "Supabase not configured" }, 500, corsHeaders);
  }

  const url      = new URL(request.url);
  const pathname = url.pathname;

  if (request.method !== "GET") {
    return json({ error: "Method not allowed" }, 405, corsHeaders);
  }

  try {

    // GET /api/fleet/stats
    if (pathname === "/api/fleet/stats") {
      const cached = await fromCache(request, corsHeaders);
      if (cached) return cached;

      const stats = await sbRpc(env, "get_fleet_stats");
      return cachedJsonResponse(ctx, request, stats, 600, corsHeaders);
    }

    // GET /api/fleet/types/:typeCode/registrations
    const regMatch = pathname.match(/^\/api\/fleet\/types\/([^/]+)\/registrations$/);
    if (regMatch) {
      const cached = await fromCache(request, corsHeaders);
      if (cached) return cached;

      const typeCode = decodeURIComponent(regMatch[1]);
      const rows     = await sbGet(env,
        `aircraft_registrations?type_code=eq.${encodeURIComponent(typeCode)}&select=registration,type_code,status,msn,delivery_date,expected_delivery,hub_icao,total_flights,total_hours_tenths,notes,grounded_until&order=status.asc,registration.asc`
      );
      // A hard landing (see POST /api/v1/pireps) puts a registration into "inspection" with
      // grounded_until set ~7 days out. It stays excluded from the bookable list until that
      // timestamp passes -- there's no cron to flip status back to "active" automatically, so
      // this is a live check on every read rather than a one-time write. An admin-set
      // "inspection" with no grounded_until (manual, via /admin/aircraft-registrations) stays
      // excluded indefinitely until manually reverted, which is the intended fallback.
      const nowIso = new Date().toISOString();
      const visible = (rows || []).filter(r =>
        r.status !== "inspection" || (r.grounded_until && r.grounded_until <= nowIso));
      return cachedJsonResponse(ctx, request, visible, 600, corsHeaders);
    }

    // GET /api/fleet/types
    if (pathname === "/api/fleet/types") {
      const cached = await fromCache(request, corsHeaders);
      if (cached) return cached;

      const rows = await sbRpc(env, "get_fleet_types");
      return cachedJsonResponse(ctx, request, rows || [], 600, corsHeaders);
    }

    return json({ error: "Not found" }, 404, corsHeaders);

  } catch (e) {
    console.error("Fleet API error:", e);
    return json({ error: "Internal server error" }, 500, corsHeaders);
  }
}

// ============================================================
//  PUBLIC API — unauthenticated, VATSIM audit transparency
// ============================================================
async function handlePublicApi(request, env, corsHeaders, ctx) {
  if (!env.SUPABASE_URL || !env.SUPABASE_SERVICE_KEY) {
    return json({ error: "Supabase not configured" }, 500, corsHeaders);
  }

  const url      = new URL(request.url);
  const pathname = url.pathname;

  if (request.method !== "GET") {
    return json({ error: "Method not allowed" }, 405, corsHeaders);
  }

  try {
    // GET /api/public/roster — sanitised pilot list (no PII)
    // Returns only display name and join date for verified, active pilots.
    // Published for VATSIM audit compliance §9.2.1 / §10.5.
    if (pathname === "/api/public/roster") {
      const cached = await fromCache(request, corsHeaders);
      if (cached) return cached;

      const rows = await sbGet(env,
        `users?verified=eq.true&disabled=eq.false&select=first_name,last_name,created_at&order=created_at.asc`
      );

      const pilots = (rows || []).map(u => ({
        name:   `${u.first_name} ${u.last_name}`.trim(),
        joined: u.created_at,
      }));

      return cachedJsonResponse(ctx, request, { pilots, total: pilots.length }, 600, corsHeaders);
    }

    // GET /api/public/stats — aggregate VA stats
    // currentlyFlying / flyingToday / fleetActivity will be populated once the ACARS app is live.
    if (pathname === "/api/public/stats") {
      const cached = await fromCache(request, corsHeaders);
      if (cached) return cached;

      const [totalPilots, routeSummaryRows, subsidiaryStatsRows] = await Promise.all([
        sbCount(env, "users", "verified=eq.true&disabled=eq.false"),
        sbRpc(env, "get_routes_summary"),
        sbRpc(env, "get_subsidiary_stats"),
      ]);

      const summary = { total: 0, mainline: 0, cargo: 0, cityhopper: 0 };
      for (const row of (routeSummaryRows || [])) {
        const n = Number(row.count);
        summary[row.fleet_group] = n;
        summary.total += n;
      }

      // Per-subsidiary route/fleet/pilot-signed-up counts, for the signup subsidiary-choice
      // screen and the portal's "switch subsidiary" page (see get_subsidiary_stats() migration
      // "add_pilot_subsidiary_selection"). Distinct from `routes`/`fleetActivity` above (existing
      // fields, left untouched) — fleetActivity there means "currently flying", not fleet size.
      const subsidiaries = {};
      for (const row of (subsidiaryStatsRows || [])) {
        subsidiaries[row.subsidiary] = {
          routeCount: Number(row.route_count) || 0,
          fleetCount: Number(row.fleet_count) || 0,
          pilotCount: Number(row.pilot_count) || 0,
        };
      }

      return cachedJsonResponse(ctx, request, {
        totalPilots,
        currentlyFlying: 0,   // populated by ACARS app (not yet live)
        flyingToday:     0,   // populated by ACARS app (not yet live)
        routes: {
          total:      summary.total,
          mainline:   summary.mainline,
          cargo:      summary.cargo,
          cityhopper: summary.cityhopper,
        },
        fleetActivity: {      // pilots currently flying per subsidiary — ACARS app pending
          mainline:   0,
          cargo:      0,
          cityhopper: 0,
        },
        subsidiaries,
      }, 600, corsHeaders);
    }

    return json({ error: "Not found" }, 404, corsHeaders);

  } catch (e) {
    console.error("Public API error:", e);
    return json({ error: "Internal server error" }, 500, corsHeaders);
  }
}

// ============================================================
//  V1 API — ACARS client endpoints (auth via Bearer ACARS key)
//
//  POST   /api/v1/pireps                  — submit a completed PIREP (with an optional
//                                            "landings" array, stored in landings, one row
//                                            per touchdown -- see below)
//  GET    /api/v1/pireps                  — fetch caller's PIREP history (limit=100)
//  GET    /api/v1/landings                — fetch caller's full landing history (limit=200),
//                                            for account-portability: reconstructs local
//                                            Pilot Hub landing detail on a new device
//  GET    /api/v1/pilots/:id/stats        — aggregate stats for a pilot
//  GET    /api/v1/routes/validate         — check dep/arr against route network
//  POST   /api/v1/messages               — send a direct or broadcast message
//  GET    /api/v1/messages/inbox          — fetch DMs addressed to calling pilot
//  GET    /api/v1/messages/broadcast      — fetch latest broadcast messages
//  GET    /api/v1/is-admin                 — whether the caller's email matches admin_users
//  POST   /api/v1/friends                 — add a friend (upsert into friendships)
//  GET    /api/v1/friends                 — fetch caller's friend list, for
//                                            account-portability
//  GET    /api/v1/friends/activity        — caller's friends' most recent flights
//                                            (Dashboard activity feed)
//  DELETE /api/v1/friends/:pilotId        — remove a friend
// ============================================================

// Derive a stable 32-hex-char pilot ID from an email address.
// Mirrors the same derivation used in /api/friends/resolve.
async function computePilotId(email) {
  const buf = await crypto.subtle.digest("SHA-256", new TextEncoder().encode("AVIATES_ID:" + email));
  return Array.from(new Uint8Array(buf)).slice(0, 16).map(b => b.toString(16).padStart(2, "0")).join("");
}

// Clamps/coerces one client-submitted landing into a safe `landings` row. Mirrors the same
// defensive parseInt/parseFloat-with-fallback style already used for the pireps payload above.
function sanitizeLandingRow(l, acarsKey, pirepId) {
  const num = (v, max) => {
    const n = Number(v);
    if (!Number.isFinite(n)) return 0;
    return max !== undefined ? Math.min(Math.max(n, -max), max) : n;
  };
  const int = (v, max) => Math.round(num(v, max));
  return {
    acars_key:             acarsKey,
    pirep_id:              pirepId,
    touchdown_at:          l.touchdown_at || new Date().toISOString(),
    airport_icao:          (l.airport_icao      || "").trim().toUpperCase().substring(0, 4),
    runway_identifier:     (l.runway_identifier || "").trim().toUpperCase().substring(0, 4),
    touchdown_lat:         l.touchdown_lat != null ? num(l.touchdown_lat, 90)  : null,
    touchdown_lon:         l.touchdown_lon != null ? num(l.touchdown_lon, 180) : null,
    vs_fpm:                num(l.vs_fpm, 20000),
    ias_kts:               num(l.ias_kts, 2000),
    gs_kts:                num(l.gs_kts, 2000),
    pitch_deg:             num(l.pitch_deg, 90),
    bank_deg:              num(l.bank_deg, 180),
    headwind_kt:           num(l.headwind_kt, 500),
    crosswind_kt:          num(l.crosswind_kt, 500),
    wind_speed_kt:         num(l.wind_speed_kt, 500),
    wind_dir_deg:          num(l.wind_dir_deg, 360),
    approach_stable:       !!l.approach_stable,
    flare_detected:        !!l.flare_detected,
    bounce_count:          int(l.bounce_count, 100),
    rollout_distance_ft:   num(l.rollout_distance_ft, 100000),
    avg_deceleration:      num(l.avg_deceleration, 1000),
    threshold_distance_ft: num(l.threshold_distance_ft, 100000),
    fuel_on_touchdown_lbs: num(l.fuel_on_touchdown_lbs, 1000000),
    score:                 int(l.score, 100),
    score_vs:              int(l.score_vs, 100),
    score_pitch:           int(l.score_pitch, 100),
    score_bank:            int(l.score_bank, 100),
    score_speed:           int(l.score_speed, 100),
    score_crosswind:       int(l.score_crosswind, 100),
    score_stability:       int(l.score_stability, 100),
  };
}

// Public visitor-facing support-chat endpoints — /api/support/chat and
// /api/support/chat/:id/messages (POST + GET). Not nested under /api/v1/, so this is
// dispatched directly from fetch() rather than through handleV1Api.
async function handleSupportChatApi(request, env, ctx, url, pathname, corsHeaders) {
  const ip = request.headers.get("CF-Connecting-IP") || "unknown";

  // POST /api/support/chat — start a new chat with its first message
  if (request.method === "POST" && pathname === "/api/support/chat") {
    if (!(await rateLimit(env, ctx, `chatstart:${ip}`, LIMITS.chatStart)))
      return rateLimitResponse(corsHeaders);

    const body = await safeJson(request);
    if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

    const name = (body.name || "").trim().substring(0, 80);
    if (!name) return json({ success: false, error: "name required" }, 400, corsHeaders);

    const email = (body.email || "").trim().substring(0, 200);
    if (email && !isValidEmail(email))
      return json({ success: false, error: "Invalid email" }, 400, corsHeaders);

    const message = (body.message || "").trim().substring(0, 2000);
    if (!message) return json({ success: false, error: "message required" }, 400, corsHeaders);
    if (containsBlockedContent(message))
      return json({ success: false, error: "Message contains inappropriate content" }, 422, corsHeaders);

    try {
      const [chat] = await sbInsert(env, "support_chats", {
        visitor_name:          name,
        visitor_email:         email || null,
        last_message_preview:  message.substring(0, 140),
        last_sender_type:      "visitor",
        admin_unread_count:    1,
      });
      await sbInsert(env, "support_chat_messages", {
        chat_id:     chat.id,
        sender_type: "visitor",
        sender_name: name,
        content:     message,
      });
      return json({ success: true, data: { chatId: chat.id, visitorToken: chat.visitor_token } }, 201, corsHeaders);
    } catch (e) {
      console.error("Support chat create error:", e);
      return json({ success: false, error: "Failed to start chat" }, 500, corsHeaders);
    }
  }

  // POST /api/support/chat/:id/messages — visitor sends a follow-up message
  const chatMsgMatch = pathname.match(/^\/api\/support\/chat\/(\d+)\/messages$/);
  if (request.method === "POST" && chatMsgMatch) {
    const chatId = parseInt(chatMsgMatch[1], 10);
    const body = await safeJson(request);
    if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

    const visitorToken = (body.visitorToken || "").trim();
    if (!visitorToken) return json({ success: false, error: "visitorToken required" }, 400, corsHeaders);

    if (!(await rateLimit(env, ctx, `chatmsg:${visitorToken}`, LIMITS.chatMessage)))
      return rateLimitResponse(corsHeaders);

    const content = (body.content || "").trim().substring(0, 2000);
    if (!content) return json({ success: false, error: "content required" }, 400, corsHeaders);
    if (containsBlockedContent(content))
      return json({ success: false, error: "Message contains inappropriate content" }, 422, corsHeaders);

    try {
      const rows = await sbGet(env, `support_chats?id=eq.${chatId}&select=id,visitor_token,visitor_name,status,admin_unread_count&limit=1`);
      const chat = rows && rows[0];
      if (!chat || chat.visitor_token !== visitorToken)
        return json({ success: false, error: "Chat not found" }, 404, corsHeaders);
      if (chat.status === "closed")
        return json({ success: false, error: "This chat has been closed" }, 409, corsHeaders);

      await sbInsert(env, "support_chat_messages", {
        chat_id:     chatId,
        sender_type: "visitor",
        sender_name: chat.visitor_name,
        content,
      });
      await sbUpdate(env, "support_chats", `id=eq.${chatId}`, {
        last_message_at:      new Date().toISOString(),
        last_message_preview: content.substring(0, 140),
        last_sender_type:     "visitor",
        admin_unread_count:   (chat.admin_unread_count || 0) + 1,
      });
      return json({ success: true }, 200, corsHeaders);
    } catch (e) {
      console.error("Support chat message error:", e);
      return json({ success: false, error: "Failed to send message" }, 500, corsHeaders);
    }
  }

  // GET /api/support/chat/:id/messages?visitorToken=...&after=<messageId> — visitor polls
  const chatPollMatch = pathname.match(/^\/api\/support\/chat\/(\d+)\/messages$/);
  if (request.method === "GET" && chatPollMatch) {
    const chatId = parseInt(chatPollMatch[1], 10);
    const visitorToken = (url.searchParams.get("visitorToken") || "").trim();
    if (!visitorToken) return json({ success: false, error: "visitorToken required" }, 400, corsHeaders);

    try {
      const rows = await sbGet(env, `support_chats?id=eq.${chatId}&select=id,visitor_token,status&limit=1`);
      const chat = rows && rows[0];
      if (!chat || chat.visitor_token !== visitorToken)
        return json({ success: false, error: "Chat not found" }, 404, corsHeaders);

      const after = parseInt(url.searchParams.get("after"), 10);
      const filter = Number.isFinite(after)
        ? `chat_id=eq.${chatId}&id=gt.${after}&order=id.asc&select=id,sender_type,sender_name,content,created_at`
        : `chat_id=eq.${chatId}&order=id.asc&select=id,sender_type,sender_name,content,created_at`;
      const messages = await sbGet(env, `support_chat_messages?${filter}`);

      // Visitor has now seen everything up to this point — clear their unread badge.
      ctx.waitUntil(sbUpdate(env, "support_chats", `id=eq.${chatId}`, { visitor_unread_count: 0 }).catch(() => {}));

      return json({
        success: true,
        data: {
          status: chat.status,
          messages: (messages || []).map(m => ({
            id: m.id, senderType: m.sender_type, senderName: m.sender_name,
            content: m.content, createdAt: m.created_at,
          })),
        },
      }, 200, corsHeaders);
    } catch (e) {
      console.error("Support chat poll error:", e);
      return json({ success: false, error: "Failed to load messages" }, 500, corsHeaders);
    }
  }

  return json({ error: "Not found" }, 404, corsHeaders);
}

async function handleV1Api(request, env, ctx, url, pathname, corsHeaders) {
  if (!env.SUPABASE_URL || !env.SUPABASE_SERVICE_KEY) {
    return json({ success: false, error: "Supabase not configured" }, 500, corsHeaders);
  }

  const auth     = request.headers.get("Authorization") || "";
  const acarsKey = auth.startsWith("Bearer ") ? auth.slice(7).trim() : "";

  // ── POST /api/v1/pireps ───────────────────────────────────────────────────
  if (request.method === "POST" && pathname === "/api/v1/pireps") {
    if (!env.RATE_LIMIT_KV) return json({ success: false, error: "Rate limit KV not configured" }, 500, corsHeaders);

    const principal = await verifyAcarsKey(env, acarsKey);
    if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

    if (!(await rateLimit(env, ctx, `pirep:${acarsKey}`, LIMITS.pirepSubmit)))
      return rateLimitResponse(corsHeaders);

    const body = await safeJson(request);
    if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

    const depIcao = (body.departure_icao || "").trim().toUpperCase().substring(0, 4);
    const arrIcao = (body.arrival_icao   || "").trim().toUpperCase().substring(0, 4);
    if (!depIcao || !arrIcao)
      return json({ success: false, error: "departure_icao and arrival_icao required" }, 400, corsHeaders);

    const blockMins = Math.max(0, parseInt(body.block_minutes,   10) || 0);
    const airMins   = Math.max(0, parseInt(body.air_minutes,     10) || 0);
    const distNm    = Math.max(0, parseInt(body.distance_nm,     10) || 0);
    const fuelLbs   = Math.max(0, parseInt(body.fuel_used_lbs,   10) || 0);
    const maxAlt    = Math.max(0, parseInt(body.max_altitude_ft, 10) || 0);
    const landingVs   = parseInt(body.landing_vs_fpm, 10) || 0;
    const score       = Math.min(100, Math.max(0, parseFloat(body.landing_score) || 0));
    const registration = (body.registration || "").trim().toUpperCase().substring(0, 20) || null;

    const inserted = await sbInsert(env, "pireps", {
      acars_key:       acarsKey,
      flight_number:   (body.flight_number  || "").trim().substring(0, 20),
      callsign:        (body.callsign        || "").trim().substring(0, 20),
      departure_icao:  depIcao,
      arrival_icao:    arrIcao,
      aircraft_type:   (body.aircraft_type   || "").trim().substring(0, 50),
      registration:    registration,
      block_out_time:  body.block_out_time   || null,
      block_in_time:   body.block_in_time    || null,
      block_minutes:   blockMins,
      air_minutes:     airMins,
      distance_nm:     distNm,
      fuel_used_lbs:   fuelLbs,
      max_altitude_ft: maxAlt,
      landing_vs_fpm:  landingVs,
      landing_score:   score,
      route:           (body.route           || "").trim().substring(0, 500),
      planned_ofp:     (body.planned_ofp     || "").trim().substring(0, 50),
      submitted_at:    new Date().toISOString(),
      status:          "accepted",
    });

    const pirepId = inserted[0]?.id ?? null;

    // Hard landing (>1000fpm) grounds the specific tail for a week -- no one can book it again
    // until grounded_until passes (enforced by GET /api/fleet/types/:typeCode/registrations and
    // re-checked server-side in POST /api/bookings; FleetService.GetRegistrationsAsync applies
    // the identical filter on its own direct-to-Supabase read since it bypasses this worker
    // entirely). Best-effort: must never fail the PIREP submission itself.
    if (registration && Math.abs(landingVs) > 1000) {
      try {
        const groundedUntil = new Date(Date.now() + 7 * 24 * 60 * 60 * 1000).toISOString();
        await sbUpdate(env, "aircraft_registrations", `registration=eq.${encodeURIComponent(registration)}`, {
          status: "inspection",
          grounded_until: groundedUntil,
        });
        console.log(`Grounded ${registration} for inspection until ${groundedUntil} (hard landing ${landingVs}fpm)`);
      } catch (e) {
        console.error("Aircraft grounding update failed (non-fatal):", e);
      }
    }

    // Optional per-touchdown detail (bounces/go-arounds get their own row, not just the
    // flight's primary landing) -- see sanitizeLandingRow for field clamping. Best-effort:
    // a landings failure must not fail the PIREP submission itself, since the coarse
    // landing_vs_fpm/landing_score columns above already captured the primary landing.
    if (Array.isArray(body.landings) && body.landings.length > 0) {
      try {
        const rows = body.landings.slice(0, 20).map(l => sanitizeLandingRow(l, acarsKey, pirepId));
        await sbInsert(env, "landings", rows);
      } catch (e) {
        console.error("Landings insert failed (non-fatal):", e);
      }
    }

    // Fire-and-forget post-PIREP notifications — never blocks the response
    ctx.waitUntil((async () => {
      try {
        const discordRows = await sbGet(env,
          `users?acars_key=eq.${encodeURIComponent(acarsKey)}&select=discord_id,discord_rank,first_name&limit=1`
        );
        const du = discordRows?.[0];
        if (!du?.discord_id) return;

        const firstName = du.first_name || 'Pilot';

        // Always DM on flight completion
        await sendDiscordDm(env, du.discord_id, formatFlightCompletionDM(body, firstName));

        // Rank-up check — separate DM only if rank changed
        // Must include both ACARS and manual hours — same as /api/portal/profile
        const [statsRows, manualRows] = await Promise.all([
          sbRpc(env, 'get_pilot_stats',        { p_acars_key: acarsKey }),
          sbRpc(env, 'get_manual_pirep_stats', { p_acars_key: acarsKey }),
        ]);
        const totalHours = (Number(statsRows?.[0]?.total_hours)  || 0)
                         + (Number(manualRows?.[0]?.manual_hours) || 0);
        const { rank: newRank } = computeRank(totalHours);
        if (newRank !== du.discord_rank) {
          await syncDiscordRank(env, du.discord_id, acarsKey, newRank, firstName);
        }
      } catch (e) {
        console.error('PIREP post-processing failed:', e);
      }
    })());

    return json({ success: true, id: inserted[0]?.id }, 200, corsHeaders);
  }

  // ── GET /api/v1/pireps ────────────────────────────────────────────────────
  if (request.method === "GET" && pathname === "/api/v1/pireps") {
    const principal = await verifyAcarsKey(env, acarsKey);
    if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

    const limitParam = parseInt(url.searchParams.get("limit") || "100", 10);
    const limit = Math.min(100, Math.max(1, isNaN(limitParam) ? 100 : limitParam));

    const rows = await sbGet(env,
      `pireps?acars_key=eq.${encodeURIComponent(acarsKey)}&order=submitted_at.desc&limit=${limit}` +
      `&select=id,flight_number,callsign,departure_icao,arrival_icao,aircraft_type,` +
      `block_out_time,block_in_time,block_minutes,air_minutes,distance_nm,fuel_used_lbs,` +
      `landing_vs_fpm,landing_score,submitted_at,status`
    );

    return json({ pireps: rows || [] }, 200, corsHeaders);
  }

  // ── GET /api/v1/landings ──────────────────────────────────────────────────
  // Full per-touchdown history, for reconstructing Pilot Hub's landing detail on a new
  // device -- mirrors GET /api/v1/pireps' shape/auth exactly.
  if (request.method === "GET" && pathname === "/api/v1/landings") {
    const principal = await verifyAcarsKey(env, acarsKey);
    if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

    const limitParam = parseInt(url.searchParams.get("limit") || "200", 10);
    const limit = Math.min(200, Math.max(1, isNaN(limitParam) ? 200 : limitParam));

    const rows = await sbGet(env,
      `landings?acars_key=eq.${encodeURIComponent(acarsKey)}&order=touchdown_at.desc&limit=${limit}` +
      `&select=id,pirep_id,touchdown_at,airport_icao,runway_identifier,touchdown_lat,touchdown_lon,` +
      `vs_fpm,ias_kts,gs_kts,pitch_deg,bank_deg,headwind_kt,crosswind_kt,wind_speed_kt,wind_dir_deg,` +
      `approach_stable,flare_detected,bounce_count,rollout_distance_ft,avg_deceleration,` +
      `threshold_distance_ft,fuel_on_touchdown_lbs,score,score_vs,score_pitch,score_bank,` +
      `score_speed,score_crosswind,score_stability`
    );

    return json({ landings: rows || [] }, 200, corsHeaders);
  }

  // ── GET /api/v1/pilots/:id/stats ─────────────────────────────────────────
  const statsMatch = pathname.match(/^\/api\/v1\/pilots\/([^/]+)\/stats$/);
  if (request.method === "GET" && statsMatch) {
    const principal = await verifyAcarsKey(env, acarsKey);
    if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

    const rows = await sbRpc(env, "get_pilot_stats", { p_acars_key: acarsKey });
    const row  = rows?.[0];

    if (!row) {
      return json({
        total_flights: 0,
        total_hours:   0,
        total_nm:      0,
        avg_score:     0,
        rank:          'Cadet',
        rank_pct:      0,
      }, 200, corsHeaders);
    }

    const { rank, rank_pct } = computeRank(row.total_hours);
    return json({ ...row, rank, rank_pct }, 200, corsHeaders);
  }

  // ── GET /api/v1/routes/validate ───────────────────────────────────────────
  if (request.method === "GET" && pathname === "/api/v1/routes/validate") {
    const principal = await verifyAcarsKey(env, acarsKey);
    if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

    const dep = (url.searchParams.get("dep") || "").trim().toUpperCase().substring(0, 4);
    const arr = (url.searchParams.get("arr") || "").trim().toUpperCase().substring(0, 4);
    if (!dep || !arr)
      return json({ approved: false, message: "dep and arr query params required", route_id: null, aircraft_ok: false }, 400, corsHeaders);

    const rows = await sbGet(env,
      `routes?origin_iata=eq.${encodeURIComponent(dep)}&dest_iata=eq.${encodeURIComponent(arr)}&select=id,aircraft_type&limit=1`
    );

    if (!rows || rows.length === 0) {
      return json({ approved: false, message: "Route not in approved network", route_id: null, aircraft_ok: false }, 200, corsHeaders);
    }

    const route = rows[0];
    return json({
      approved:    true,
      message:     "Route approved",
      route_id:    String(route.id),
      aircraft_ok: true,
    }, 200, corsHeaders);
  }

  // ── POST /api/v1/messages ─────────────────────────────────────────────────
  if (request.method === "POST" && pathname === "/api/v1/messages") {
    if (!env.RATE_LIMIT_KV) return json({ success: false, error: "Rate limit KV not configured" }, 500, corsHeaders);

    const principal = await verifyAcarsKey(env, acarsKey);
    if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

    if (!(await rateLimit(env, ctx, `msg:${acarsKey}`, LIMITS.msgSend)))
      return rateLimitResponse(corsHeaders);

    const body = await safeJson(request);
    if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

    const content = (body.content || "").trim().substring(0, 2000);
    if (!content) return json({ success: false, error: "content required" }, 400, corsHeaders);
    if (containsBlockedContent(content))
      return json({ success: false, error: "Message contains inappropriate content" }, 422, corsHeaders);

    const type = (body.type || "direct").toLowerCase();
    if (type !== "direct" && type !== "broadcast")
      return json({ success: false, error: "type must be 'direct' or 'broadcast'" }, 400, corsHeaders);

    const recipientId = (body.recipient_id || "broadcast").trim().substring(0, 100);
    if (type === "direct" && !recipientId)
      return json({ success: false, error: "recipient_id required for direct messages" }, 400, corsHeaders);

    const { email, user } = principal;
    const senderId   = await computePilotId(email);
    const senderName = `${user.first_name} ${user.last_name}`.trim();

    await sbInsert(env, "messages", {
      sender_key:   acarsKey,
      sender_id:    senderId,
      sender_name:  senderName,
      recipient_id: type === "broadcast" ? "broadcast" : recipientId,
      content,
      type,
      sent_at:      body.sent_at || new Date().toISOString(),
    });

    return json({ success: true }, 200, corsHeaders);
  }

  // ── GET /api/v1/messages/inbox ────────────────────────────────────────────
  if (request.method === "GET" && pathname === "/api/v1/messages/inbox") {
    const principal = await verifyAcarsKey(env, acarsKey);
    if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

    const myPilotId = await computePilotId(principal.email);
    // Fetch both messages sent TO me and messages I sent (for conversation threads).
    // Use OR filter: (recipient_id == myPilotId OR sender_id == myPilotId)
    const rows = await sbGet(env,
      `messages?or=(recipient_id.eq.${encodeURIComponent(myPilotId)},sender_id.eq.${encodeURIComponent(myPilotId)})&type=eq.direct&is_moderated=eq.false&order=sent_at.desc&limit=100&select=id,sender_id,sender_name,recipient_id,content,sent_at`
    );

    return json((rows || []).map(m => ({
      Id:          m.id,
      SenderId:    m.sender_id,
      SenderName:  m.sender_name,
      RecipientId: m.recipient_id,
      Content:     m.content,
      SentAt:      m.sent_at,
      ReadAt:      null,
      IsModerated: false,
      Type:        0,  // MessageType.Direct
    })), 200, corsHeaders);
  }

  // ── GET /api/v1/messages/broadcast ───────────────────────────────────────
  if (request.method === "GET" && pathname === "/api/v1/messages/broadcast") {
    const principal = await verifyAcarsKey(env, acarsKey);
    if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

    const rows = await sbGet(env,
      `messages?type=eq.broadcast&is_moderated=eq.false&order=sent_at.desc&limit=50&select=id,sender_id,sender_name,content,sent_at`
    );

    return json((rows || []).map(m => ({
      Id:          m.id,
      SenderId:    m.sender_id,
      SenderName:  m.sender_name,
      RecipientId: "broadcast",
      Content:     m.content,
      SentAt:      m.sent_at,
      ReadAt:      null,
      IsModerated: false,
      Type:        1,  // MessageType.Broadcast
    })), 200, corsHeaders);
  }

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
      ? body.waypoints
          .filter(w => typeof w === "object" && w !== null)
          .slice(0, 200)
          .map(w => ({
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

  // ── GET /api/v1/is-admin ─────────────────────────────────────────────────
  // The client uses this to decide whether to show the Flights page's Pending Review tab
  // (flight-deletion request queue) at all -- matching the caller's own email against
  // admin_users, the same table the separate admin portal login already checks against.
  if (request.method === "GET" && pathname === "/api/v1/is-admin") {
    const principal = await verifyAcarsKey(env, acarsKey);
    if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

    const rows = await sbGet(env,
      `admin_users?email=eq.${encodeURIComponent(principal.email)}&select=id&limit=1`);
    return json({ is_admin: !!(rows && rows.length > 0) }, 200, corsHeaders);
  }

  // ── POST /api/v1/friends ─────────────────────────────────────────────────
  // Adds a friend (the client resolves the friend code to a pilot via /api/friends/resolve
  // first, then calls this with the resolved identity). Upsert-on-conflict so re-adding an
  // existing friend is a harmless no-op rather than a 409/500.
  if (request.method === "POST" && pathname === "/api/v1/friends") {
    const principal = await verifyAcarsKey(env, acarsKey);
    if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

    if (!(await rateLimit(env, ctx, `friendadd:${acarsKey}`, LIMITS.friendAdd)))
      return rateLimitResponse(corsHeaders);

    const body = await safeJson(request);
    if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

    const friendPilotId = (body.pilot_id || "").trim().substring(0, 64);
    if (!friendPilotId) return json({ success: false, error: "pilot_id required" }, 400, corsHeaders);

    try {
      await sbUpsert(env, "friendships", {
        acars_key:       acarsKey,
        friend_pilot_id: friendPilotId,
        friend_name:     (body.friend_name || "").trim().substring(0, 100),
        friend_code:     (body.friend_code || "").trim().toUpperCase().substring(0, 9),
      }, "acars_key,friend_pilot_id");
    } catch (e) {
      console.error("Friendship insert failed:", e);
      return json({ success: false, error: "Could not save friend" }, 500, corsHeaders);
    }

    return json({ success: true }, 200, corsHeaders);
  }

  // ── GET /api/v1/friends ───────────────────────────────────────────────────
  // Fetches the caller's saved friend list, for reconstructing it on a new device.
  if (request.method === "GET" && pathname === "/api/v1/friends") {
    const principal = await verifyAcarsKey(env, acarsKey);
    if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

    const rows = await sbGet(env,
      `friendships?acars_key=eq.${encodeURIComponent(acarsKey)}&order=created_at.desc&limit=200` +
      `&select=friend_pilot_id,friend_name,friend_code,created_at`
    );

    return json({ friends: rows || [] }, 200, corsHeaders);
  }

  // ── GET /api/v1/friends/activity ─────────────────────────────────────────
  // Dashboard's activity feed: the caller's friends' most recent flights, newest first.
  // get_friends_activity resolves friend_pilot_id -> acars_key server-side (via users.pilot_id)
  // so a friend's ACARS key is never seen by the client.
  if (request.method === "GET" && pathname === "/api/v1/friends/activity") {
    const principal = await verifyAcarsKey(env, acarsKey);
    if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

    const limitParam = parseInt(url.searchParams.get("limit") || "3", 10);
    const limit = Math.min(10, Math.max(1, isNaN(limitParam) ? 3 : limitParam));

    const rows = await sbRpc(env, "get_friends_activity", { p_acars_key: acarsKey, p_limit: limit });
    return json({ activity: rows || [] }, 200, corsHeaders);
  }

  // ── DELETE /api/v1/friends/:pilotId ──────────────────────────────────────
  const friendsDeleteMatch = pathname.match(/^\/api\/v1\/friends\/([^/]+)$/);
  if (request.method === "DELETE" && friendsDeleteMatch) {
    const principal = await verifyAcarsKey(env, acarsKey);
    if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);
    const targetPilotId = decodeURIComponent(friendsDeleteMatch[1]).trim();
    if (!targetPilotId) return json({ success: false, error: "pilotId required" }, 400, corsHeaders);
    const existing = await sbGet(env,
      `friendships?acars_key=eq.${encodeURIComponent(acarsKey)}&friend_pilot_id=eq.${encodeURIComponent(targetPilotId)}&select=id&limit=1`
    );
    if (!existing || existing.length === 0)
      return json({ success: false, error: "Friendship not found" }, 404, corsHeaders);
    await sbDelete(env, `friendships?acars_key=eq.${encodeURIComponent(acarsKey)}&friend_pilot_id=eq.${encodeURIComponent(targetPilotId)}`);
    return json({ success: true }, 200, corsHeaders);
  }

  // ── POST /api/v1/flight-review ───────────────────────────────────────────
  // A flight the app's own client-side compliance checks flagged (hard landing, gear/flaps/
  // speed not stabilized by 1000ft, off-centerline or outside the touchdown zone, excessive
  // climb/descent rate, dangerous cruise speed, or a late departure) gets logged here for
  // manual review, along with the pilot's own explanation. This is a logging surface only for
  // now (see the app's own "held for review" dialog) -- nothing here blocks or rejects the
  // PIREP itself.
  if (request.method === "POST" && pathname === "/api/v1/flight-review") {
    const principal = await verifyAcarsKey(env, acarsKey);
    if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

    if (!(await rateLimit(env, ctx, `flightreview:${acarsKey}`, LIMITS.flightReview)))
      return rateLimitResponse(corsHeaders);

    const body = await safeJson(request);
    if (!body) return json({ success: false, error: "Invalid JSON" }, 400, corsHeaders);

    const violations = Array.isArray(body.violations) ? body.violations.map(v => String(v).substring(0, 300)).slice(0, 20) : [];
    if (violations.length === 0) return json({ success: false, error: "violations required" }, 400, corsHeaders);

    try {
      await sbInsert(env, "flight_reviews", {
        acars_key:    acarsKey,
        flight_id:    (body.flight_id || "").trim().substring(0, 64) || null,
        landing_id:   (body.landing_id || "").trim().substring(0, 64) || null,
        violations:   JSON.stringify(violations),
        pilot_reason: (body.pilot_reason || "").trim().substring(0, 2000) || null,
        status:       "pending",
        created_at:   new Date().toISOString(),
      });
    } catch (e) {
      console.error("Flight review insert failed:", e);
      return json({ success: false, error: "Could not save review" }, 500, corsHeaders);
    }

    return json({ success: true }, 200, corsHeaders);
  }

  return json({ error: "Not found" }, 404, corsHeaders);
}

// ============================================================
//  SHARED HELPERS
// ============================================================

function json(data, status = 200, headers = {}) {
  return new Response(JSON.stringify(data), {
    status,
    headers: Object.assign({ "Content-Type": "application/json" }, headers),
  });
}

async function safeJson(req) {
  try { return await req.json(); } catch { return null; }
}

function isValidEmail(email) {
  return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email);
}

function uint8ArrayToB64(u8) {
  let binary = "";
  for (let i = 0; i < u8.length; i++) binary += String.fromCharCode(u8[i]);
  return btoa(binary);
}

async function sha256B64(input) {
  const data = new TextEncoder().encode(input);
  const buf  = await crypto.subtle.digest("SHA-256", data);
  return uint8ArrayToB64(new Uint8Array(buf));
}

async function deriveKeyPBKDF2(password, saltB64) {
  const enc  = new TextEncoder();
  const salt = Uint8Array.from(atob(saltB64), c => c.charCodeAt(0));
  const key  = await crypto.subtle.importKey("raw", enc.encode(password), "PBKDF2", false, ["deriveBits"]);
  const bits = await crypto.subtle.deriveBits({ name: "PBKDF2", salt, iterations: PBKDF2_ITERS, hash: "SHA-256" }, key, DERIVED_BYTES * 8);
  return new Uint8Array(bits);
}

function timingSafeEqual(a, b) {
  if (a.length !== b.length) return false;
  let result = 0;
  for (let i = 0; i < a.length; i++) result |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return result === 0;
}

function generateAcarsKey() {
  const chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
  const r = crypto.getRandomValues(new Uint8Array(ACARS_KEY_LEN));
  let out = "";
  for (let i = 0; i < r.length; i++) out += chars[r[i] % chars.length];
  return out;
}

async function createJWT(payload, secret) {
  const header = btoa(JSON.stringify({ alg: "HS256", typ: "JWT" }));
  const body   = btoa(JSON.stringify(payload));
  const data   = `${header}.${body}`;
  const key    = await crypto.subtle.importKey("raw", new TextEncoder().encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const signature = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(data));
  return `${data}.${uint8ArrayToB64(new Uint8Array(signature))}`;
}

// Verifies an HS256 JWT and returns its payload, or null if invalid/expired.
async function verifyJWT(token, secret) {
  try {
    const parts = token.split(".");
    if (parts.length !== 3) return null;

    const [header, body, sig] = parts;
    const data = `${header}.${body}`;

    const key       = await crypto.subtle.importKey("raw", new TextEncoder().encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["verify"]);
    const sigBytes  = Uint8Array.from(atob(sig.replace(/-/g, "+").replace(/_/g, "/")), c => c.charCodeAt(0));
    const valid     = await crypto.subtle.verify("HMAC", key, sigBytes, new TextEncoder().encode(data));
    if (!valid) return null;

    const payload = JSON.parse(atob(body));
    if (payload.exp && Math.floor(Date.now() / 1000) > payload.exp) return null;

    return payload;
  } catch {
    return null;
  }
}

// ─── ADMIN JWT HELPERS ───────────────────────────────────────────────────────
async function createAdminJWT(payload, secret) {
  const header = btoa(JSON.stringify({ alg: "HS256", typ: "JWT" }));
  const body   = btoa(JSON.stringify(payload));
  const data   = `${header}.${body}`;
  const key    = await crypto.subtle.importKey("raw", new TextEncoder().encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const signature = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(data));
  return `${data}.${uint8ArrayToB64(new Uint8Array(signature))}`;
}

async function verifyAdminJWT(token, secret) {
  return verifyJWT(token, secret); // Reuses existing JWT verification
}

function extractAdminToken(request) {
  const auth = request.headers.get("Authorization") || "";
  return auth.startsWith("Bearer ") ? auth.slice(7).trim() : null;
}

async function validateAdminAuth(request, env) {
  const token = extractAdminToken(request);
  if (!token) return null;
  return await verifyAdminJWT(token, env.ADMIN_JWT_SECRET);
}

const BLOCKED_WORDS_RE = /\b(f+u+c+k+s?|sh[i1]ts?|bulls+h[i1]t|c+u+n+t+s?|n[i1]gg[ae]r?s?|fagg?[o0]ts?|retardeds?|assh[o0]les?|bastards?|wh[o0]res?|slut+s?|k[i1]ke|sp[i1]c|ch[i1]nk|wetback|twat+s?)\b/i;

function containsBlockedContent(text) {
  if (!text) return false;
  const normalized = text
    .replace(/@/g, "a").replace(/\$/g, "s").replace(/0/g, "o")
    .replace(/1/g, "i").replace(/3/g, "e").replace(/4/g, "a")
    .replace(/5/g, "s").replace(/!/g, "i").replace(/\+/g, "t")
    .replace(/\*/g, "").replace(/ph/g, "f")
    .replace(/​/g, "").replace(/‌/g, "").replace(/‍/g, "")
    .replace(/‮/g, "").replace(/﻿/g, "");
  return BLOCKED_WORDS_RE.test(normalized);
}

// Rate limiting. Short windows (≤ 10 min) are counted in per-isolate memory:
// the Workers free plan allows only 1,000 KV writes/day, and a KV read+write
// per rate-limited request would burn that quota on normal traffic alone.
// Per-isolate counters are slightly weaker (each edge location counts
// independently and evicted isolates reset), but bursts from one client hit
// the same isolate, which is what these limits exist to stop.
// Long windows (manual PIREP daily cap) still use KV so they survive restarts.
const memBuckets = new Map(); // key → { count, reset }

async function rateLimit(env, ctx, key, rule) {
  const now = Math.floor(Date.now() / 1000);

  if (rule.window <= 600) {
    let bucket = memBuckets.get(key);
    if (!bucket || now > bucket.reset) {
      bucket = { count: 0, reset: now + rule.window };
      memBuckets.set(key, bucket);
    }
    bucket.count++;
    if (memBuckets.size > 5000) {
      for (const [k, b] of memBuckets) if (now > b.reset) memBuckets.delete(k);
    }
    return bucket.count <= rule.count;
  }

  const raw  = await env.RATE_LIMIT_KV.get(key);
  let bucket = raw ? JSON.parse(raw) : { count: 0, reset: now + rule.window };

  if (now > bucket.reset) {
    bucket.count = 0;
    bucket.reset = now + rule.window;
  }

  bucket.count++;
  ctx.waitUntil(
    env.RATE_LIMIT_KV.put(key, JSON.stringify(bucket), { expirationTtl: rule.window + 5 })
  );
  return bucket.count <= rule.count;
}

function rateLimitResponse(corsHeaders, retryAfter = 60) {
  return new Response(JSON.stringify({ success: false, error: "Rate limit exceeded. Please try again later." }), {
    status: 429,
    headers: Object.assign({
      "Content-Type": "application/json",
      "Retry-After":  String(retryAfter),
    }, corsHeaders),
  });
}

async function generateFriendCode(acarsKey) {
  if (!acarsKey) return "";
  const hash = new Uint8Array(
    await crypto.subtle.digest("SHA-256", new TextEncoder().encode("AVIATES_FC_" + acarsKey))
  );
  const chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
  let code = "";
  for (let i = 0; i < 8; i++) code += chars[hash[i] % chars.length];
  return `${code.slice(0, 4)}-${code.slice(4)}`;
}

function escapeHtml(str) {
  return String(str)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#039;");
}

async function sendVerificationEmail(email, token, firstName, env) {
  const safeFirstName = escapeHtml(firstName);
  const verifyUrl = `https://acars.flyaviatesair.uk/verify?token=${token}`;

  const res = await fetch("https://api.resend.com/emails", {
    method: "POST",
    headers: {
      "Authorization": `Bearer ${env.RESEND_API_KEY}`,
      "Content-Type":  "application/json",
    },
    body: JSON.stringify({
      from:    "AviatesAir <onboarding@flyaviatesair.uk>",
      to:      email,
      subject: "Verify your AviatesAir account",
      html: `<!DOCTYPE html>
<html lang="en">
<head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Verify your AviatesAir account</title></head>
<body style="margin:0;padding:0;background-color:#0d0d0f;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;">
  <table width="100%" cellpadding="0" cellspacing="0" style="background-color:#0d0d0f;padding:40px 16px;">
    <tr><td align="center">
      <table width="600" cellpadding="0" cellspacing="0" style="max-width:600px;width:100%;">

        <!-- Header bar -->
        <tr>
          <td style="background:linear-gradient(135deg,#1a1608 0%,#2a2010 100%);border-radius:12px 12px 0 0;padding:32px 40px;border-bottom:1px solid #d9b67a33;">
            <table width="100%" cellpadding="0" cellspacing="0">
              <tr>
                <td>
                  <span style="font-size:22px;font-weight:800;letter-spacing:-0.03em;color:#d9b67a;">AVIATES</span><span style="font-size:22px;font-weight:300;color:#c8a85a;letter-spacing:0.08em;">AIR</span>
                </td>
                <td align="right">
                  <span style="font-size:11px;letter-spacing:0.15em;text-transform:uppercase;color:#6b5c3a;font-weight:600;">Virtual Airline</span>
                </td>
              </tr>
            </table>
          </td>
        </tr>

        <!-- Hero -->
        <tr>
          <td style="background:#111109;padding:48px 40px 32px;border-left:1px solid #1e1c14;border-right:1px solid #1e1c14;">
            <p style="margin:0 0 12px;font-size:12px;letter-spacing:0.18em;text-transform:uppercase;color:#6b5c3a;font-weight:600;">Account Verification</p>
            <h1 style="margin:0 0 20px;font-size:32px;font-weight:800;letter-spacing:-0.03em;color:#f0e6cc;line-height:1.2;">Welcome aboard,<br>${safeFirstName}.</h1>
            <p style="margin:0;font-size:16px;line-height:1.7;color:#8a8070;">You're one step away from joining the AviatesAir crew. Confirm your email address to activate your pilot account and get access to routes, bookings, and ACARS.</p>
          </td>
        </tr>

        <!-- Divider -->
        <tr>
          <td style="background:#111109;padding:0 40px;border-left:1px solid #1e1c14;border-right:1px solid #1e1c14;">
            <div style="height:1px;background:linear-gradient(to right,transparent,#d9b67a22,transparent);"></div>
          </td>
        </tr>

        <!-- CTA -->
        <tr>
          <td style="background:#111109;padding:36px 40px;border-left:1px solid #1e1c14;border-right:1px solid #1e1c14;" align="center">
            <a href="${verifyUrl}"
               style="display:inline-block;background:linear-gradient(135deg,#d9b67a 0%,#c8a85a 100%);color:#1a1005;text-decoration:none;font-weight:800;font-size:15px;letter-spacing:0.05em;padding:16px 40px;border-radius:8px;box-shadow:0 4px 24px rgba(217,182,122,0.25);">
              VERIFY MY EMAIL
            </a>
            <p style="margin:20px 0 0;font-size:12px;color:#4a4535;">Button not working? Copy this link into your browser:</p>
            <p style="margin:8px 0 0;font-size:11px;color:#6b5c3a;word-break:break-all;">${verifyUrl}</p>
          </td>
        </tr>

        <!-- Info strip -->
        <tr>
          <td style="background:#0e0d0a;padding:20px 40px;border-left:1px solid #1e1c14;border-right:1px solid #1e1c14;">
            <table width="100%" cellpadding="0" cellspacing="0">
              <tr>
                <td width="50%" style="padding-right:16px;">
                  <p style="margin:0;font-size:11px;color:#6b5c3a;text-transform:uppercase;letter-spacing:0.1em;font-weight:600;">Expires</p>
                  <p style="margin:4px 0 0;font-size:13px;color:#8a8070;">1 hour from now</p>
                </td>
                <td width="50%" style="border-left:1px solid #1e1c14;padding-left:16px;">
                  <p style="margin:0;font-size:11px;color:#6b5c3a;text-transform:uppercase;letter-spacing:0.1em;font-weight:600;">Single use</p>
                  <p style="margin:4px 0 0;font-size:13px;color:#8a8070;">Link becomes invalid after use</p>
                </td>
              </tr>
            </table>
          </td>
        </tr>

        <!-- Footer -->
        <tr>
          <td style="background:#0a0a08;padding:24px 40px;border-radius:0 0 12px 12px;border:1px solid #1e1c14;border-top:none;">
            <p style="margin:0;font-size:12px;color:#3a3527;line-height:1.6;">If you didn't create an AviatesAir account, you can safely ignore this email. This message was sent to ${email}.</p>
            <p style="margin:12px 0 0;font-size:11px;color:#2a2520;">&copy; ${new Date().getFullYear()} AviatesAir &mdash; flyaviatesair.uk</p>
          </td>
        </tr>

      </table>
    </td></tr>
  </table>
</body>
</html>`,
    }),
  });
  return res.ok;
}

async function sendPasswordResetEmail(email, token, firstName, env) {
  const safeFirstName = escapeHtml(firstName || "Pilot");
  const resetUrl = `https://flyaviatesair.uk/reset-password?token=${token}`;

  const res = await fetch("https://api.resend.com/emails", {
    method: "POST",
    headers: {
      "Authorization": `Bearer ${env.RESEND_API_KEY}`,
      "Content-Type":  "application/json",
    },
    body: JSON.stringify({
      from:    "AviatesAir <onboarding@flyaviatesair.uk>",
      to:      email,
      subject: "Reset your AviatesAir password",
      html: `<!DOCTYPE html>
<html lang="en">
<head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Reset your AviatesAir password</title></head>
<body style="margin:0;padding:0;background-color:#0d0d0f;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;">
  <table width="100%" cellpadding="0" cellspacing="0" style="background-color:#0d0d0f;padding:40px 16px;">
    <tr><td align="center">
      <table width="600" cellpadding="0" cellspacing="0" style="max-width:600px;width:100%;">
        <tr>
          <td style="background:linear-gradient(135deg,#1a1608 0%,#2a2010 100%);border-radius:12px 12px 0 0;padding:32px 40px;border-bottom:1px solid #d9b67a33;">
            <table width="100%" cellpadding="0" cellspacing="0">
              <tr>
                <td><span style="font-size:22px;font-weight:800;letter-spacing:-0.03em;color:#d9b67a;">AVIATES</span><span style="font-size:22px;font-weight:300;color:#c8a85a;letter-spacing:0.08em;">AIR</span></td>
                <td align="right"><span style="font-size:11px;letter-spacing:0.15em;text-transform:uppercase;color:#6b5c3a;font-weight:600;">Virtual Airline</span></td>
              </tr>
            </table>
          </td>
        </tr>
        <tr>
          <td style="background:#111109;padding:48px 40px 32px;border-left:1px solid #1e1c14;border-right:1px solid #1e1c14;">
            <p style="margin:0 0 12px;font-size:12px;letter-spacing:0.18em;text-transform:uppercase;color:#6b5c3a;font-weight:600;">Password Reset</p>
            <h1 style="margin:0 0 20px;font-size:32px;font-weight:800;letter-spacing:-0.03em;color:#f0e6cc;line-height:1.2;">Reset your password,<br>${safeFirstName}.</h1>
            <p style="margin:0;font-size:16px;line-height:1.7;color:#8a8070;">We received a request to reset the password for your AviatesAir account. Click the button below to set a new password. This link expires in <strong style="color:#d9b67a;">1 hour</strong>.</p>
          </td>
        </tr>
        <tr>
          <td style="background:#111109;padding:0 40px;border-left:1px solid #1e1c14;border-right:1px solid #1e1c14;">
            <div style="height:1px;background:linear-gradient(to right,transparent,#d9b67a22,transparent);"></div>
          </td>
        </tr>
        <tr>
          <td style="background:#111109;padding:36px 40px;border-left:1px solid #1e1c14;border-right:1px solid #1e1c14;" align="center">
            <a href="${resetUrl}" style="display:inline-block;background:linear-gradient(135deg,#d9b67a 0%,#c8a85a 100%);color:#1a1005;text-decoration:none;font-weight:800;font-size:15px;letter-spacing:0.05em;padding:16px 40px;border-radius:8px;box-shadow:0 4px 24px rgba(217,182,122,0.25);">
              RESET MY PASSWORD
            </a>
            <p style="margin:20px 0 0;font-size:12px;color:#4a4535;">Button not working? Copy this link into your browser:</p>
            <p style="margin:8px 0 0;font-size:11px;color:#6b5c3a;word-break:break-all;">${resetUrl}</p>
          </td>
        </tr>
        <tr>
          <td style="background:#111109;padding:0 40px 36px;border-left:1px solid #1e1c14;border-right:1px solid #1e1c14;" align="center">
            <div style="height:1px;background:linear-gradient(to right,transparent,#d9b67a22,transparent);margin-bottom:24px;"></div>
            <p style="margin:0;font-size:12px;color:#4a4535;line-height:1.6;">If you did not request a password reset, you can safely ignore this email. Your password will not change.</p>
          </td>
        </tr>
        <tr>
          <td style="background:#0d0d0b;border-radius:0 0 12px 12px;padding:24px 40px;border:1px solid #1e1c14;border-top:none;" align="center">
            <p style="margin:0;font-size:11px;color:#3a3528;letter-spacing:0.08em;">© ${new Date().getFullYear()} AVIATESAIR · VIRTUAL AIRLINE</p>
          </td>
        </tr>
      </table>
    </td></tr>
  </table>
</body>
</html>`,
    }),
  });

  return res.ok;
}
