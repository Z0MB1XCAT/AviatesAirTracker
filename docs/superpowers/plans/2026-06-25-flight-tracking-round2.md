# Flight Tracking Round 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `GET /api/v1/pireps` backend endpoint, sync Supabase PIREP history into the local flight cache on startup, and send a Discord DM on every flight completion.

**Architecture:** Task 7 adds the read endpoint to the Cloudflare Worker. Task 8 adds `FetchMyPirepsAsync` to the existing C# backend client and calls it from `App.xaml.cs` in a fire-and-forget startup routine that merges remote PIREPs not already in the local JSON cache. Task 9 restructures the existing post-PIREP `ctx.waitUntil` block to always send a completion DM, then conditionally fire the existing rank-up DM.

**Tech Stack:** Cloudflare Worker (JS), .NET 8 C#, RestSharp, Newtonsoft.Json, Supabase PostgREST, Discord REST API v10.

---

## File Map

| File | Change |
|------|--------|
| `BACKEND-API/worker.js` | Task 7: add GET handler; Task 9: add helpers + restructure waitUntil |
| `AviatesAirTracker/Core/Backend/AviatesBackendClient.cs` | Task 8: add `FetchMyPirepsAsync` + models |
| `AviatesAirTracker/App.xaml.cs` | Task 8: add + call `SyncPirepHistoryAsync` |

---

## Task 7: GET /api/v1/pireps — backend read endpoint

**Files:**
- Modify: `BACKEND-API/worker.js` (after line ~2565, inside `handleV1Api`)

The endpoint must:
- Verify the Bearer ACARS key via the existing `verifyAcarsKey` helper
- Query the `pireps` Supabase table filtered by `acars_key`, ordered by `submitted_at desc`, capped at `limit` (default 100, max 100)
- Return `{ pireps: [...] }` with the exact field list below

### Step 1: Locate the insertion point

- [ ] Open `BACKEND-API/worker.js` and find the end of the `POST /api/v1/pireps` handler.
  It ends at approximately line 2565 with:
  ```javascript
  return json({ success: true, id: inserted[0]?.id }, 200, corsHeaders);
  }
  ```
  The next block is `// ── GET /api/v1/pilots/:id/stats`.

### Step 2: Insert the GET /api/v1/pireps handler

- [ ] Insert the following block **between** the closing `}` of the POST handler and the existing `// ── GET /api/v1/pilots/:id/stats` comment:

```javascript
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

```

### Step 3: Update the comment block above `handleV1Api`

- [ ] Find the comment block around line 2465–2474 that lists the V1 API surface. Add the new GET line so it reads:

```javascript
//  POST   /api/v1/pireps                  — submit a completed PIREP
//  GET    /api/v1/pireps                  — fetch caller's PIREP history (limit=100)
//  GET    /api/v1/pilots/:id/stats        — aggregate stats for a pilot
```

### Step 4: Deploy and verify

- [ ] Run a local sanity check — confirm the file parses by opening it in an editor. There are no automated tests; visual review is sufficient.

### Step 5: Commit

- [ ] Stage and commit:
```bash
git add BACKEND-API/worker.js
git commit -m "feat(backend): add GET /api/v1/pireps endpoint for PIREP history

Returns up to 100 PIREPs for the authenticated pilot ordered by
submitted_at desc. Used by the client to sync history on startup.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"
```

---

## Task 8: Startup PIREP history sync — C# client + App.xaml.cs

**Files:**
- Modify: `AviatesAirTracker/Core/Backend/AviatesBackendClient.cs` (append after `RemoveFriendAsync`)
- Modify: `AviatesAirTracker/App.xaml.cs` (add `SyncPirepHistoryAsync` + fire-and-forget call)

### Step 1: Add `FetchMyPirepsAsync` and models to AviatesBackendClient.cs

- [ ] Open `AviatesAirTracker/Core/Backend/AviatesBackendClient.cs`. Find the `SendPositionReportAsync` method — it is the last method in the class. Insert the new method **before** `SendPositionReportAsync`, then append the two new model classes **after** the existing `RouteValidationResult` class at the bottom of the file.

**New method** (insert before `SendPositionReportAsync`):
```csharp
    // =====================================================
    // PIREP HISTORY
    // =====================================================

    /// <summary>
    /// Fetches the pilot's PIREP history from the backend (up to 100 entries).
    /// Used on startup to hydrate the local flight cache with records from Supabase.
    /// </summary>
    public async Task<List<RemotePirepRecord>> FetchMyPirepsAsync(string acarsKey)
    {
        try
        {
            var request = new RestRequest("/api/v1/pireps")
                .AddQueryParameter("limit", "100")
                .AddHeader("Authorization", $"Bearer {acarsKey}");

            var response = await _client.ExecuteAsync(request);

            if (!response.IsSuccessful || string.IsNullOrEmpty(response.Content))
            {
                Log.Debug("[BackendClient] FetchMyPireps non-success: {Status}", response.StatusCode);
                return [];
            }

            var wrapper = JsonConvert.DeserializeObject<PirepListResponse>(response.Content);
            return wrapper?.Pireps ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[BackendClient] FetchMyPireps error");
            return [];
        }
    }
```

**New model classes** (append at the very end of the file, after `RouteValidationResult`):
```csharp
public class RemotePirepRecord
{
    [JsonProperty("id")]             public int       Id            { get; set; }
    [JsonProperty("flight_number")]  public string    FlightNumber  { get; set; } = "";
    [JsonProperty("callsign")]       public string    Callsign      { get; set; } = "";
    [JsonProperty("departure_icao")] public string    DepartureICAO { get; set; } = "";
    [JsonProperty("arrival_icao")]   public string    ArrivalICAO   { get; set; } = "";
    [JsonProperty("aircraft_type")]  public string    AircraftType  { get; set; } = "";
    [JsonProperty("block_out_time")] public DateTime? BlockOutTime  { get; set; }
    [JsonProperty("block_in_time")]  public DateTime? BlockInTime   { get; set; }
    [JsonProperty("block_minutes")]  public int       BlockMinutes  { get; set; }
    [JsonProperty("air_minutes")]    public int       AirMinutes    { get; set; }
    [JsonProperty("distance_nm")]    public int       DistanceNm    { get; set; }
    [JsonProperty("fuel_used_lbs")]  public int       FuelUsedLbs   { get; set; }
    [JsonProperty("landing_vs_fpm")] public int       LandingVsFpm  { get; set; }
    [JsonProperty("landing_score")]  public double    LandingScore  { get; set; }
    [JsonProperty("submitted_at")]   public DateTime  SubmittedAt   { get; set; }
    [JsonProperty("status")]         public string    Status        { get; set; } = "";
}

internal class PirepListResponse
{
    [JsonProperty("pireps")] public List<RemotePirepRecord> Pireps { get; set; } = [];
}
```

### Step 2: Build to verify AviatesBackendClient.cs compiles

- [ ] Run:
```
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```
Expected: `Build succeeded.` 0 errors. Fix any errors before continuing.

### Step 3: Add `SyncPirepHistoryAsync` to App.xaml.cs

- [ ] Open `AviatesAirTracker/App.xaml.cs`. Find `RetryPendingPirepsAsync` (the static method starting around line 119). Add the new static method **immediately after** the closing `}` of `RetryPendingPirepsAsync`:

```csharp
    private static async Task SyncPirepHistoryAsync(IServiceProvider sp)
    {
        try
        {
            var settings = sp.GetRequiredService<SettingsService>();
            var key      = settings.Settings.AcarsKey.Trim();
            if (string.IsNullOrEmpty(key)) return;

            var backend = sp.GetRequiredService<AviatesBackendClient>();
            var flights = sp.GetRequiredService<IFlightRepository>();

            var remote = await backend.FetchMyPirepsAsync(key);
            if (remote.Count == 0) return;

            var local = await flights.GetAllAsync();
            int added = 0;

            foreach (var r in remote)
            {
                // Skip if already in local cache — match by flight number + block-out within 60 s
                bool exists = local.Any(f =>
                    f.FlightNumber == r.FlightNumber &&
                    r.BlockOutTime.HasValue &&
                    Math.Abs((f.BlockOutTime - r.BlockOutTime.Value).TotalSeconds) < 60);
                if (exists) continue;

                var record = new FlightRecord
                {
                    FlightNumber    = r.FlightNumber,
                    Callsign        = r.Callsign,
                    DepartureICAO   = r.DepartureICAO,
                    ArrivalICAO     = r.ArrivalICAO,
                    AircraftType    = r.AircraftType,
                    BlockOutTime    = r.BlockOutTime ?? DateTime.UtcNow,
                    BlockInTime     = r.BlockInTime  ?? DateTime.UtcNow,
                    FuelUsedLbs     = r.FuelUsedLbs,
                    ActualDistanceNm = r.DistanceNm,
                    Status          = FlightStatus.Completed,
                    SyncedToBackend = true,
                };

                if (r.LandingVsFpm != 0 || r.LandingScore > 0)
                {
                    record.PrimaryLanding = new LandingResult
                    {
                        VerticalSpeedFPM = r.LandingVsFpm,
                        LandingScore     = (int)r.LandingScore,
                        AirportICAO      = r.ArrivalICAO,
                    };
                }

                await flights.SaveAsync(record);
                added++;
            }

            if (added > 0)
                Log.Information("[Startup] Synced {Count} historical PIREP(s) from Supabase", added);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Startup] PIREP history sync failed (non-critical)");
        }
    }
```

### Step 4: Call `SyncPirepHistoryAsync` in `OnStartup`

- [ ] In `OnStartup`, find the line:
```csharp
_ = RetryPendingPirepsAsync(_serviceProvider);
```
Add the sync call immediately after it:
```csharp
_ = RetryPendingPirepsAsync(_serviceProvider);
_ = SyncPirepHistoryAsync(_serviceProvider);   // hydrate local cache from Supabase history
```

### Step 5: Build to verify App.xaml.cs compiles

- [ ] Run:
```
dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
```
Expected: `Build succeeded.` 0 errors.

### Step 6: Commit

- [ ] Stage and commit:
```bash
git add AviatesAirTracker/Core/Backend/AviatesBackendClient.cs
git add AviatesAirTracker/App.xaml.cs
git commit -m "feat(client): sync Supabase PIREP history into local cache on startup

FetchMyPirepsAsync fetches up to 100 remote PIREPs; SyncPirepHistoryAsync
merges any not already in the local JSON cache. Fire-and-forget, never
blocks startup. Restores flight history after reinstall.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"
```

---

## Task 9: Discord flight completion DM — worker.js

**Files:**
- Modify: `BACKEND-API/worker.js`
  - Add two helper functions near `syncDiscordRank`
  - Restructure the `ctx.waitUntil` block inside `POST /api/v1/pireps`

### Background

The current `ctx.waitUntil` block inside `POST /api/v1/pireps` (around line 2540) exits early with `return` if the pilot's rank hasn't changed — meaning a completion DM would never fire on flights where no rank-up occurred. The fix: always send the completion DM first, then separately check for rank-up.

### Step 1: Add two helper functions after `syncDiscordRank`

- [ ] Open `BACKEND-API/worker.js`. Find `syncDiscordRank` (line ~84). It ends around line 137 with:
```javascript
  console.error('Discord announcement failed:', e);
  }
}
```

Add the two new helpers **immediately after** that closing `}`:

```javascript
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

// Opens (or reuses) a DM channel to a Discord user and sends a message.
async function sendDiscordDm(env, discordId, content) {
  if (!env.DISCORD_BOT_TOKEN) return;
  const headers = {
    'Authorization': `Bot ${env.DISCORD_BOT_TOKEN}`,
    'Content-Type': 'application/json',
  };
  try {
    const dmRes = await fetch('https://discord.com/api/v10/users/@me/channels', {
      method: 'POST', headers,
      body: JSON.stringify({ recipient_id: discordId }),
    });
    if (!dmRes.ok) return;
    const { id: channelId } = await dmRes.json();
    await fetch(`https://discord.com/api/v10/channels/${channelId}/messages`, {
      method: 'POST', headers,
      body: JSON.stringify({ content }),
    });
  } catch (e) {
    console.error('Discord DM failed:', e);
  }
}
```

### Step 2: Restructure the ctx.waitUntil block in POST /api/v1/pireps

- [ ] Find the existing `ctx.waitUntil` block inside `POST /api/v1/pireps` (around lines 2540–2562). It currently reads:

```javascript
    // Fire-and-forget rank check — never blocks the PIREP response
    ctx.waitUntil((async () => {
      try {
        const discordRows = await sbGet(env,
          `users?acars_key=eq.${encodeURIComponent(acarsKey)}&select=discord_id,discord_rank,first_name&limit=1`
        );
        const du = discordRows?.[0];
        if (!du?.discord_id) return;

        // Must include both ACARS and manual hours — same as /api/portal/profile
        const [statsRows, manualRows] = await Promise.all([
          sbRpc(env, 'get_pilot_stats',        { p_acars_key: acarsKey }),
          sbRpc(env, 'get_manual_pirep_stats', { p_acars_key: acarsKey }),
        ]);
        const totalHours = (Number(statsRows?.[0]?.total_hours)  || 0)
                         + (Number(manualRows?.[0]?.manual_hours) || 0);
        const { rank: newRank } = computeRank(totalHours);
        if (newRank === du.discord_rank) return;

        await syncDiscordRank(env, du.discord_id, acarsKey, newRank, du.first_name || 'Pilot');
      } catch (e) {
        console.error('PIREP rank sync failed:', e);
      }
    })());
```

Replace the entire block (from `// Fire-and-forget rank check` through the closing `})());`) with:

```javascript
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
```

### Step 3: Verify the file parses cleanly

- [ ] Open `BACKEND-API/worker.js` in an editor and confirm there are no obvious syntax errors (mismatched braces) around the two edit sites.

### Step 4: Commit

- [ ] Stage and commit:
```bash
git add BACKEND-API/worker.js
git commit -m "feat(discord): send flight completion DM on every filed PIREP

formatFlightCompletionDM + sendDiscordDm helpers added.
ctx.waitUntil restructured so completion DM always fires;
rank-up DM fires separately only when rank changes.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"
```

---

## Self-Review

**Spec coverage:**
- ✅ Task 7 — GET `/api/v1/pireps` with ACARS key auth, limit param, correct field list
- ✅ Task 8 — `FetchMyPirepsAsync`, `RemotePirepRecord` model, `SyncPirepHistoryAsync` in App.xaml.cs after `RetryPendingPirepsAsync`, fire-and-forget
- ✅ Task 9 — `formatFlightCompletionDM`, `sendDiscordDm` helpers, restructured `ctx.waitUntil` so completion DM always fires regardless of rank change

**Placeholder scan:** No TBDs, all code blocks are complete.

**Type consistency:**
- `RemotePirepRecord.LandingScore` is `double`; cast to `(int)` when assigning to `LandingResult.LandingScore` (which is `int`) — correct.
- `formatFlightCompletionDM` receives `body` (the parsed PIREP JSON object) — available in scope at the `ctx.waitUntil` call site — correct.
- `sendDiscordDm` is called with `(env, du.discord_id, string)` — matches definition — correct.
