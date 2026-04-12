// ============================================================
// AviatesAir — Cloudflare Worker (ES Module)
// Handles: auth, ACARS, messaging, AND the new /api/routes Supabase API
//
// Bindings required in wrangler.toml:
//   KV:      RATE_LIMIT_KV, MESSAGES_KV, FLIGHTS_KV, ACARS_KV,
//            SESSIONS_KV, PIREPS_KV, TELEMETRY_KV, ACARS_LOGS
//   Secrets: JWT_SECRET, RESEND_API_KEY, ADMIN_SECRET, UPLOAD_SECRET
//            SUPABASE_URL, SUPABASE_SERVICE_KEY
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
  acarsAuth:    { count: 10,  window: 60 },
  acarsSend:    { count: 60,  window: 60 },
  flightUpdate: { count: 120, window: 60 },
  messagesSend: { count: 30,  window: 60 },
  pirepSubmit:  { count: 20,  window: 60 },
  msgSend:      { count: 60,  window: 60 },
};

const ALLOWED_ORIGINS = [
  "https://z0mb1xcat.github.io",
  "https://flyaviatesair.uk",
  "https://www.flyaviatesair.uk",
  "http://localhost:3000",
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

// ─── SHARED AUTH HELPER ──────────────────────────────────────────────────────
// Validates an ACARS key against Supabase. Returns the user row or null.
async function verifyAcarsKey(env, key) {
  if (!key) return null;
  const rows = await sbGet(env, `users?acars_key=eq.${encodeURIComponent(key)}&select=email,first_name,last_name,disabled&limit=1`);
  if (!rows || rows.length === 0) return null;
  const user = rows[0];
  if (user.disabled) return null;
  return { acarsKey: key, email: user.email, user };
}

// ─── ENTRY POINT ─────────────────────────────────────────────────────────────
export default {
  async fetch(request, env, ctx) {
    const origin        = request.headers.get("Origin");
    const allowedOrigin = ALLOWED_ORIGINS.includes(origin) ? origin : null;

    const corsHeadersBase = {
      "Access-Control-Allow-Methods": "GET, POST, DELETE, OPTIONS",
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
    if (pathname.startsWith("/api/fleet")) {
      return handleFleetApi(request, env, corsHeaders, ctx);
    }
    if (pathname.startsWith("/api/public")) {
      return handlePublicApi(request, env, corsHeaders, ctx);
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

        const rows = await sbGet(env, `users?email=eq.${encodeURIComponent(email)}&select=id,email,first_name,last_name,pw_salt,pw_derived,acars_key,verified,disabled&limit=1`);
        if (!rows || rows.length === 0)
          return json({ success: false, error: "Invalid credentials" }, 401, corsHeaders);

        const user = rows[0];
        if (!user.verified) return json({ success: false, error: "Email not verified. Please check your inbox." }, 403, corsHeaders);
        if (user.disabled)  return json({ success: false, error: "Account disabled" }, 403, corsHeaders);

        const derived = await deriveKeyPBKDF2(password, user.pw_salt);
        if (!timingSafeEqual(uint8ArrayToB64(derived), user.pw_derived))
          return json({ success: false, error: "Invalid credentials" }, 401, corsHeaders);

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

      // ── ACARS AUTH ──────────────────────────────────────────────────────────
      if (request.method === "POST" && pathname === "/api/acars/auth") {
        const userAgent     = request.headers.get("User-Agent") || "";
        const isACARSClient = userAgent.includes("AviatesAir-ACARS-Client") || userAgent.includes("Python");

        if (!isACARSClient)
          return json({ success: false, error: "Invalid client. Please use official ACARS client." }, 403, corsHeaders);

        if (!(await rateLimit(env, ctx, `acarsauth:${ip}`, LIMITS.acarsAuth)))
          return rateLimitResponse(corsHeaders);

        const body = await safeJson(request);
        const key  = body?.acarsKey;
        if (!key) return json({ success: false, error: "Missing acarsKey" }, 400, corsHeaders);

        const rows = await sbGet(env, `users?acars_key=eq.${encodeURIComponent(key)}&select=email,first_name,last_name,simbrief,disabled&limit=1`);
        if (!rows || rows.length === 0)
          return json({ success: false, error: "Invalid ACARS key" }, 401, corsHeaders);

        const user = rows[0];
        if (user.disabled) return json({ success: false, error: "ACARS key suspended" }, 403, corsHeaders);

        return json({ success: true, email: user.email, firstName: user.first_name, lastName: user.last_name, simbrief: user.simbrief || "" }, 200, corsHeaders);
      }

      // ── FRIEND CODE RESOLVE ─────────────────────────────────────────────────
      if (request.method === "GET" && pathname === "/api/friends/resolve") {
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

      return json({ success: false, error: "Not found" }, 404, corsHeaders);

    } catch (e) {
      console.error("Worker error:", e);
      return json({ success: false, error: "Internal server error" }, 500, corsHeaders);
    }
  },

  // ── Daily cleanup ───────────────────────────────────────────────────────────
  // Wire up the cron trigger in Cloudflare Dashboard → Workers → your worker →
  // Triggers → Cron Triggers → Add: "0 3 * * *"
  // Or in wrangler.toml:  [triggers]  crons = ["0 3 * * *"]
  async scheduled(_event, env, ctx) {
    if (!env.SUPABASE_URL || !env.SUPABASE_SERVICE_KEY) return;
    ctx.waitUntil(
      sbRpc(env, "cleanup_expired_records", {})
        .catch(e => console.error("[Cleanup] scheduled cleanup failed:", e))
    );
  },
};

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

      const rows    = await sbRpc(env, "get_routes_summary");
      const summary = { mainline: 0, cargo: 0, cityhopper: 0, total: 0 };
      for (const row of (rows || [])) {
        summary[row.fleet_group] = Number(row.count);
        summary.total += Number(row.count);
      }
      return cachedJsonResponse(ctx, request, { summary }, 300, corsHeaders);
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

      return cachedJsonResponse(ctx, request, { data: rows[0] }, 300, corsHeaders);
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
          return cachedJsonResponse(ctx, request, { meta: { count: 0, limit: 200, offset: 0 }, data: [] }, 300, corsHeaders);
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

      return cachedJsonResponse(ctx, request, { meta: { count: result.count, limit, offset }, data: result.data || [] }, 300, corsHeaders);
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


  try {

    // GET /api/events/my?acarsKey=... — personalized, never cache
    if (method === "GET" && pathname === "/api/events/my") {
      const acarsKey  = url.searchParams.get("acarsKey") || "";
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

      return cachedJsonResponse(ctx, request, { data: rows[0] }, 300, corsHeaders);
    }

    // GET /api/events?filter=upcoming|past|all
    if (method === "GET" && pathname === "/api/events") {
      const cached = await fromCache(request, corsHeaders);
      if (cached) return cached;

      const filter = (url.searchParams.get("filter") || "upcoming").toLowerCase();
      const rows   = await sbRpc(env, "get_events_list", { p_filter: filter });
      return cachedJsonResponse(ctx, request, { data: rows || [] }, 300, corsHeaders);
    }

    // POST /api/events (create event)
    if (method === "POST" && pathname === "/api/events") {
      const body = await safeJson(request);
      if (!body) return json({ error: "Invalid JSON" }, 400, corsHeaders);

      const principal = await verifyAcarsKey(env, body.acarsKey || "");
      if (!principal) return json({ error: "Invalid or missing ACARS key" }, 401, corsHeaders);

      const title               = (body.title               || "").trim().substring(0, 100);
      const description         = (body.description         || "").trim().substring(0, 500);
      const eventDate           = (body.eventDate           || "").trim();
      const timeUtc             = (body.timeUtc             || "").trim().substring(0, 10);
      const route               = (body.route               || "").trim().substring(0, 100);
      const aircraftRestriction = (body.aircraftRestriction || "").trim().substring(0, 100);
      const rankRestriction     = (body.rankRestriction     || "").trim().substring(0, 100);
      const maxParticipants     = Math.max(0, Math.min(9999, parseInt(body.maxParticipants || "0", 10) || 0));

      if (!title) return json({ error: "Title is required" }, 400, corsHeaders);
      if (!eventDate || !/^\d{4}-\d{2}-\d{2}$/.test(eventDate)) {
        return json({ error: "Valid event date (YYYY-MM-DD) required" }, 400, corsHeaders);
      }

      const { user } = principal;
      const createdByName = `${user.first_name} ${user.last_name}`.trim();
      const createdAt     = new Date().toISOString();

      const rows = await sbInsert(env, "events", {
        title, description, event_date: eventDate, time_utc: timeUtc,
        route, aircraft_restriction: aircraftRestriction,
        rank_restriction: rankRestriction, created_by: body.acarsKey,
        created_by_name: createdByName, created_at: createdAt,
        max_participants: maxParticipants, status: "upcoming",
      });

      return json({ success: true, id: rows[0]?.id }, 200, corsHeaders);
    }

    // POST /api/events/:id/register|unregister
    const regMatch = pathname.match(/^\/api\/events\/(\d+)\/(register|unregister)$/);
    if (method === "POST" && regMatch) {
      const eventId = parseInt(regMatch[1], 10);
      const action  = regMatch[2];

      const body      = await safeJson(request);
      const principal = await verifyAcarsKey(env, body?.acarsKey || "");
      if (!principal) return json({ error: "Invalid or missing ACARS key" }, 401, corsHeaders);

      const eventRows = await sbRpc(env, "get_event_by_id", { p_id: eventId });
      if (!eventRows || eventRows.length === 0) return json({ error: "Event not found" }, 404, corsHeaders);
      const event = eventRows[0];

      const acarsKey = body.acarsKey;

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

      const routeId      = parseInt(body.route_id, 10);
      const callsign     = (body.callsign      || "").trim().toUpperCase().substring(0, 10);
      const aircraftType = (body.aircraft_type || "").trim().substring(0, 20);
      const registration = (body.registration  || "").trim().substring(0, 20) || null;
      const scheduledDep = (body.scheduled_dep || "").trim();

      if (!routeId || isNaN(routeId)) return json({ error: "Valid route_id required"            }, 400, corsHeaders);
      if (!callsign)                  return json({ error: "callsign required"                  }, 400, corsHeaders);
      if (!aircraftType)              return json({ error: "aircraft_type required"              }, 400, corsHeaders);
      if (!scheduledDep)              return json({ error: "scheduled_dep required"              }, 400, corsHeaders);

      if (!/^VAV\d{1,4}[A-Z]{0,2}$/.test(callsign) || callsign.length > 7) {
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

      // Check route exists
      const routeRows = await sbGet(env, `routes?id=eq.${routeId}&select=id&limit=1`);
      if (!routeRows || routeRows.length === 0) {
        return json({ error: "Route not found" }, 404, corsHeaders);
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
        acars_key: principal.acarsKey, route_id: routeId, callsign,
        aircraft_type: aircraftType, registration, scheduled_dep: scheduledDep,
        status: "confirmed", created_at: createdAt,
      });
      const newId = inserted[0]?.id;

      const created = await sbRpc(env, "get_booking_by_id", { p_id: newId });
      return json(created?.[0] ?? inserted[0], 200, Object.assign({ "Cache-Control": "no-store" }, corsHeaders));
    }

    // DELETE /api/bookings/:id
    const deleteMatch = pathname.match(/^\/api\/bookings\/(\d+)$/);
    if (method === "DELETE" && deleteMatch) {
      const principal = await authenticate();
      if (!principal) return json({ error: "Invalid or missing ACARS key" }, 401, corsHeaders);

      const bookingId = parseInt(deleteMatch[1], 10);
      if (isNaN(bookingId) || bookingId < 1) return json({ error: "Invalid booking id" }, 400, corsHeaders);

      const rows = await sbGet(env, `bookings?id=eq.${bookingId}&select=id,acars_key,status&limit=1`);
      if (!rows || rows.length === 0) return json({ error: "Booking not found" }, 404, corsHeaders);

      const booking = rows[0];
      if (booking.acars_key !== principal.acarsKey) return json({ error: "Forbidden" }, 403, corsHeaders);
      if (booking.status === "cancelled")           return json({ error: "Booking is already cancelled" }, 409, corsHeaders);

      await sbUpdate(env, "bookings", `id=eq.${bookingId}`, { status: "cancelled" });
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
        `aircraft_registrations?type_code=eq.${encodeURIComponent(typeCode)}&select=registration,type_code,status,msn,delivery_date,expected_delivery,hub_icao,total_flights,total_hours_tenths,notes&order=status.asc,registration.asc`
      );
      return cachedJsonResponse(ctx, request, rows || [], 600, corsHeaders);
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

      return cachedJsonResponse(ctx, request, { pilots, total: pilots.length }, 300, corsHeaders);
    }

    // GET /api/public/stats — aggregate VA stats
    // currentlyFlying / flyingToday / fleetActivity will be populated once the ACARS app is live.
    if (pathname === "/api/public/stats") {
      const cached = await fromCache(request, corsHeaders);
      if (cached) return cached;

      const [userRows, routeRows] = await Promise.all([
        sbGet(env, `users?verified=eq.true&disabled=eq.false&select=id`),
        sbGet(env, `routes?select=id,fleet_group`),
      ]);

      const routes = routeRows || [];

      return cachedJsonResponse(ctx, request, {
        totalPilots:    (userRows || []).length,
        currentlyFlying: 0,   // populated by ACARS app (not yet live)
        flyingToday:     0,   // populated by ACARS app (not yet live)
        routes: {
          total:      routes.length,
          mainline:   routes.filter(r => r.fleet_group === "mainline").length,
          cargo:      routes.filter(r => r.fleet_group === "cargo").length,
          cityhopper: routes.filter(r => r.fleet_group === "cityhopper").length,
        },
        fleetActivity: {      // pilots currently flying per subsidiary — ACARS app pending
          mainline:   0,
          cargo:      0,
          cityhopper: 0,
        },
      }, 300, corsHeaders);
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
//  POST   /api/v1/pireps                  — submit a completed PIREP
//  GET    /api/v1/pilots/:id/stats        — aggregate stats for a pilot
//  GET    /api/v1/routes/validate         — check dep/arr against route network
//  POST   /api/v1/messages               — send a direct or broadcast message
//  GET    /api/v1/messages/inbox          — fetch DMs addressed to calling pilot
//  GET    /api/v1/messages/broadcast      — fetch latest broadcast messages
// ============================================================

// Derive a stable 32-hex-char pilot ID from an email address.
// Mirrors the same derivation used in /api/friends/resolve.
async function computePilotId(email) {
  const buf = await crypto.subtle.digest("SHA-256", new TextEncoder().encode("AVIATES_ID:" + email));
  return Array.from(new Uint8Array(buf)).slice(0, 16).map(b => b.toString(16).padStart(2, "0")).join("");
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
    const landingVs = parseInt(body.landing_vs_fpm, 10) || 0;
    const score     = Math.min(100, Math.max(0, parseFloat(body.landing_score) || 0));

    const inserted = await sbInsert(env, "pireps", {
      acars_key:       acarsKey,
      flight_number:   (body.flight_number  || "").trim().substring(0, 20),
      callsign:        (body.callsign        || "").trim().substring(0, 20),
      departure_icao:  depIcao,
      arrival_icao:    arrIcao,
      aircraft_type:   (body.aircraft_type   || "").trim().substring(0, 50),
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

    return json({ success: true, id: inserted[0]?.id }, 200, corsHeaders);
  }

  // ── GET /api/v1/pilots/:id/stats ─────────────────────────────────────────
  const statsMatch = pathname.match(/^\/api\/v1\/pilots\/([^/]+)\/stats$/);
  if (request.method === "GET" && statsMatch) {
    const principal = await verifyAcarsKey(env, acarsKey);
    if (!principal) return json({ success: false, error: "Unauthorized" }, 401, corsHeaders);

    const rows = await sbRpc(env, "get_pilot_stats", { p_acars_key: acarsKey });
    const row  = rows?.[0];

    return json(row ?? {
      total_flights: 0,
      total_hours:   0,
      total_nm:      0,
      avg_score:     0,
      rank:          "Student Pilot",
      rank_pct:      0,
    }, 200, corsHeaders);
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
    const rows = await sbGet(env,
      `messages?recipient_id=eq.${encodeURIComponent(myPilotId)}&type=eq.direct&is_moderated=eq.false&order=sent_at.desc&limit=100&select=id,sender_id,sender_name,recipient_id,content,sent_at`
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

// Rate limit using KV. The counter write is non-blocking (ctx.waitUntil) so
// the response is not delayed waiting for KV to acknowledge the write.
async function rateLimit(env, ctx, key, rule) {
  const now  = Math.floor(Date.now() / 1000);
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

async function sendVerificationEmail(email, token, firstName, env) {
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
            <h1 style="margin:0 0 20px;font-size:32px;font-weight:800;letter-spacing:-0.03em;color:#f0e6cc;line-height:1.2;">Welcome aboard,<br>${firstName}.</h1>
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
