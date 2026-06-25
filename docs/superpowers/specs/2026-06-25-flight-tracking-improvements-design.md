# Flight Tracking Improvements — Design Spec
**Date:** 2026-06-25  
**Scope:** 8 improvements split across two implementation rounds.

---

## Background

The flight tracker records flights correctly, but several UX and data-flow gaps make the experience feel incomplete:

- The post-landing score modal (`LandingResultModal`) never appears despite being fully wired.
- Manually ending a flight doesn't always show the celebration panel.
- The sidebar and ACARS connect button give no feedback about MSFS connection state.
- Active/broken flights cannot be deleted — only completed ones can request deletion.
- Flight records show a truncated GUID instead of the route's scheduled flight number.
- Flight history loads from a JSON file on disk (already persisted), but historical flights recorded before the cache existed are invisible until Supabase sync is added.
- The Discord bot only DMs on rank-up; no general flight completion notification exists.

---

## Round 1 — Client-side fixes (C# / Razor only)

### 1.1 Landing Modal Never Appearing

**Root cause:** `LandingAnalyzer.FinalizeLanding()` is gated on rollout speed dropping below 30kt *while `_capturingRollout` is active*. `HandleTouchdown()` sets `_capturingRollout = true` at touchdown. `FinalizeLanding()` must fire before `FlightSessionManager.Reset()` clears the analyzer — but `Reset()` is called inside `OnFlightComplete()`, which fires when speed < 1kt (parking/engines off). In practice the 30kt gate fires first, but if `HandleTouchdown` is never triggered (e.g. gear not reported as touching down in some aircraft) the whole chain fails silently.

**Fix:** Fire `LandingDetected` immediately inside `HandleTouchdown()` with the touchdown snapshot, removing the rollout gate. `RolloutDistanceFt` and `ThresholdDistanceFt` default to 0, and the modal already guards these with `> 10` checks. This guarantees the modal appears on every touchdown regardless of rollout telemetry. `FinalizeLanding()` can be kept for rollout analytics but is no longer the trigger for the modal.

**Files:** `Core/Analytics/LandingAnalyzer.cs`

---

### 1.2 Manual End Flight — Celebration Panel

**Current state:** `ConfirmEndFlight()` calls `Session.EndFlightManually()` → fires `FlightCompleted` → `OnFlightCompleted` in Acars.razor sets `_showCompletionPanel = true`. The code path exists for both auto and manual ends.

**Fix (defensive):** Verify `_completedRecord` is set before `_showCompletionPanel` flips true (currently it is). Add an explicit `InvokeAsync(StateHasChanged)` call immediately after setting the panel visible, outside any try/catch that could suppress it. Also ensure the `ObjectDisposedException` guard doesn't swallow the show-panel line when the page is still active.

**Files:** `Pages/Acars.razor`

---

### 1.3 Sidebar Online/Offline Indicator

**Current state:** `MainLayout.razor` renders a hardcoded `"Offline Mode"` with a disconnected dot.

**Design:**
- Inject `SimConnectManager` into `MainLayout.razor`.
- Add `_connStatus` field (type `SimConnectionStatus`).
- In `OnInitialized()`, subscribe to `SimConnect.ConnectionStatusChanged` and read `SimConnect.ConnectionStatus` for the initial state.
- Render three states:

| State | Dot class | Text |
|-------|-----------|------|
| Disconnected | `disconnected` (red) | Offline Mode |
| Connecting | `connecting` (amber, pulsing) | Connecting… |
| Connected | `connected` (green, breathing) | MSFS Connected |

- Unsubscribe in `DisposeAsync()`.

**Files:** `Shared/MainLayout.razor`, `wwwroot/css/app.css` (`.conn-dot.connected`, `.conn-dot.connecting` states if not already present)

---

### 1.4 ACARS Connect Button Visual Feedback

**Current state:** Button calls `SimConnect.TryConnect()` with no state change — feels unresponsive.

**Design:**
- Track `_connectingInProgress` bool in Acars.razor.
- On `TryConnect()`: set `_connectingInProgress = true`, call `SimConnect.TryConnect()`, call `StateHasChanged()`.
- In `OnConnectionStatusChanged` (already subscribed): reset `_connectingInProgress = false`.
- Button renders as disabled with "Connecting…" text while `_connectingInProgress`.
- Auto-reset after 10 seconds via existing reconnect timer (no extra timer needed).

**Files:** `Pages/Acars.razor`

---

### 1.5 Delete Active / Broken Flights

**Current state:** The delete button is hidden for `FlightStatus.InProgress` flights. Broken ghost flights cannot be removed.

**Design:** Two deletion paths:

| Flight status | Deletion path |
|---------------|---------------|
| `Completed` / `Diverted` / `Aborted` | Existing approval-queue flow (unchanged) |
| `InProgress` | Direct hard-delete — single confirm dialog, no reason required |

For InProgress delete:
- Show a "Force Remove" button next to active flights.
- Confirm modal: "This will permanently remove the active flight record. This cannot be undone."
- On confirm: call `FlightRepo.DeleteAsync(flight.Id)` directly. If `Session.CurrentFlight?.Id == flight.Id`, also call `Session.Reset()` to clear the active session.
- No entry in the deletion-request queue.

Also: switch `IFlightDeletionRepository` from `InMemoryFlightDeletionRepository` to a new `JsonFlightDeletionRepository` so pending deletion requests survive restarts. The JSON path: `%APPDATA%\AviatesAirTracker\deletion_requests.json`.

**Files:** `Pages/Flights.razor`, `Core/Data/DataRepositories.cs`, `App.xaml.cs`

---

### 1.6 Flight Number from Active Booking

**Current state:** `FlightRecord.FlightNumber` starts as `""` and only gets populated if MSFS ATC callsign fires via SimConnect. The logbook falls back to a truncated GUID.

**Design:** In `FlightSessionManager.OnEnginesStarted()`, after creating `CurrentFlight`:

```csharp
if (_bookingService.ActiveBooking is { } booking)
{
    if (!string.IsNullOrEmpty(booking.RouteCallsign))
        CurrentFlight.FlightNumber = booking.RouteCallsign; // e.g. "VAV103"
    if (!string.IsNullOrEmpty(booking.Callsign))
        CurrentFlight.Callsign = booking.Callsign;
}
```

`booking.RouteCallsign` holds the route's scheduled callsign (set from `route.Callsign` in `BookingService`). SimConnect-received ident still overwrites this if it fires later — that's fine, it's more specific.

**Files:** `Services/FlightSessionManager.cs`

---

## Round 2 — Backend additions (worker.js + client fetch)

### 2.1 GET `/api/v1/pireps` Endpoint (worker.js)

New endpoint alongside the existing POST:

```
GET /api/v1/pireps?limit=100
Authorization: Bearer <acars_key>
```

- Verifies ACARS key via `verifyAcarsKey`.
- Queries `pireps` table filtered by `acars_key`, ordered by `submitted_at desc`, limited to 100.
- Returns:

```json
{
  "pireps": [
    {
      "id": 42,
      "flight_number": "VAV103",
      "callsign": "VAV103",
      "departure_icao": "EGLL",
      "arrival_icao": "KJFK",
      "aircraft_type": "A320",
      "block_out_time": "2026-06-24T10:00:00Z",
      "block_in_time":  "2026-06-24T17:12:00Z",
      "block_minutes":  432,
      "air_minutes":    390,
      "distance_nm":    3450,
      "fuel_used_lbs":  18400,
      "landing_vs_fpm": -142,
      "landing_score":  87,
      "submitted_at":   "2026-06-24T17:15:00Z",
      "status":         "accepted"
    }
  ]
}
```

**File:** `BACKEND-API/worker.js`

---

### 2.2 Load Supabase History on Startup (client)

`AviatesBackendClient` gets `FetchMyPirepsAsync(acarsKey) → List<RemotePirepRecord>`.

In `App.xaml.cs`, after `RetryPendingPirepsAsync`, call a new `SyncPirepHistoryAsync(sp)`:
- Fetch remote pireps.
- Load existing local flights.
- For each remote pirep not already in local (match by `flight_number` + `block_out_time` within 60s), create a `FlightRecord` with:
  - `Status = Completed`
  - `SyncedToBackend = true`
  - All available fields populated
  - `PrimaryLanding` constructed from `landing_vs_fpm` / `landing_score` (minimal data)
- Save new records to `IFlightRepository`.

This is fire-and-forget; startup is not blocked.

**Files:** `Core/Backend/AviatesBackendClient.cs`, `App.xaml.cs`

---

### 2.3 Discord Flight Completion DM (worker.js)

After every successful PIREP `sbInsert`, add a fire-and-forget Discord DM alongside the existing rank check:

```js
ctx.waitUntil((async () => {
  // existing rank-up check ...

  // New: flight completion DM
  const dmBody = formatFlightCompletionDM(body, firstName);
  await sendDiscordDm(env, du.discord_id, dmBody);
})());
```

DM message format:
```
✈️ Flight Filed
VAV103 · EGLL → KJFK
Block time: 7h 12m · 3,450 nm
Landing: 87/100 · -142 fpm
```

- Skip if `discord_id` is null (handled by existing guard).
- Separate from rank-up DM — both can fire on the same flight.
- Helper: `formatFlightCompletionDM(pirepBody, firstName)` returns the embed/content string.

**File:** `BACKEND-API/worker.js`

---

## Implementation Order

### Round 1 (all client, can be done in parallel tasks)
1. Landing modal fix — `LandingAnalyzer.cs`
2. Manual end panel fix — `Acars.razor`
3. Sidebar status — `MainLayout.razor` + `app.css`
4. Connect button feedback — `Acars.razor`
5. Delete active flights + `JsonFlightDeletionRepository` — `Flights.razor`, `DataRepositories.cs`, `App.xaml.cs`
6. Flight number from booking — `FlightSessionManager.cs`

### Round 2 (backend + client fetch)
7. GET `/api/v1/pireps` — `worker.js`
8. Startup history sync — `AviatesBackendClient.cs`, `App.xaml.cs`
9. Discord completion DM — `worker.js`

---

## Out of Scope

- Full logbook UI redesign (existing table stays).
- Supabase writes for deletion requests (handled locally via JSON file).
- Offline retry for Supabase history fetch (startup-only, best-effort).
