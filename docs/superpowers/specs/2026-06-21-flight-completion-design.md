# Flight Completion System — Design Spec
*AviatesAirTracker · 2026-06-21*

## Overview

Implement the full post-flight lifecycle: gate-in detection, booking close-out, SimBrief plan persistence, slide-in completion panel, live stats wiring, and rank alignment. All pieces hook off the existing `FlightCompleted` event — no new detection logic required.

---

## 1. Architecture

```
FlightSessionManager.OnFlightComplete()
  ├── [existing] Sets BlockInTime, FuelArrivalLbs, Status=Completed
  ├── [existing] SubmitPirepAsync()  ←  backend auto-handles rank-up Discord DM
  ├── [NEW]      BookingService.CompleteActiveBookingAsync()
  └── [existing] fires FlightCompleted event
                        │
          ┌─────────────┼──────────────────┐
          ▼             ▼                  ▼
    Acars.razor   PilotHubViewModel    (future subscribers)
    shows panel   RefreshAsync() →
                  PilotStatsService
                  updates all stats
```

### Files changed

| File | Change |
|------|--------|
| `BACKEND-API/worker.js` | Add `PATCH /api/bookings/:id` — set status `"completed"` |
| `Services/BookingService.cs` | Add `CompleteActiveBookingAsync()` |
| `Services/FlightSessionManager.cs` | Call `CompleteActiveBookingAsync()` in `OnFlightComplete()` |
| `Services/SupportServices.cs` | Add SimBrief persistence methods; update rank tiers |
| `Pages/Acars.razor` | Subscribe to `FlightCompleted`, persist SimBrief plan, show slide-in panel |
| `Pages/Dashboard.razor` | Wire stats to `PilotHubViewModel` instead of hardcoded strings |
| `Pages/Flights.razor` | Verify / wire to `JsonFlightRepository` (logbook) |

---

## 2. Booking Completion

### Backend — `PATCH /api/bookings/:id`

Added to `handleBookingsApi` in `worker.js`.

- Auth: Bearer ACARS key (same as all other booking endpoints)
- Fetch booking; verify `acars_key` matches caller
- Reject if `status !== "confirmed"` with 409
- Update: `status = "completed"`, `completed_at = new Date().toISOString()`
- Return `{ success: true }`

### Client — `BookingService.CompleteActiveBookingAsync()`

```
1. If ActiveBooking == null → return (no-op)
2. PATCH /api/bookings/{ActiveBooking.Id} with Bearer ACARS key
3. SetPositionAsync(ActiveBooking.DestIata)   ← moves home base to arrival
4. ActiveBooking = null
```

Failures are logged but do not throw — a network error shouldn't prevent the local flight record from being saved.

### Wiring — `FlightSessionManager.OnFlightComplete()`

Call `CompleteActiveBookingAsync()` after `SubmitPirepAsync()`. Fire-and-forget (`_ = CompleteActiveBookingAsync()`), matching the existing pattern.

---

## 3. SimBrief Plan Persistence

`SettingsService` gains two methods:

```csharp
Task SaveSimBriefPlanAsync(SimBriefFlightPlan? plan)
Task<SimBriefFlightPlan?> LoadSimBriefPlanAsync()
```

Both operate on `AppData\Roaming\AviatesAirTracker\simbrief_plan.json`.  
`SaveSimBriefPlanAsync(null)` deletes the file.

### Acars.razor changes

| Moment | Action |
|--------|--------|
| `OnInitializedAsync` | If `_flightPlan == null`, call `LoadSimBriefPlanAsync()` and assign if non-null |
| After `ImportSimBrief()` succeeds | Call `SaveSimBriefPlanAsync(_flightPlan)` |
| After flight completes | Call `SaveSimBriefPlanAsync(null)` — plan has been flown, start fresh |

---

## 4. Slide-in Completion Panel

### State variables (Acars.razor)

```csharp
bool _showCompletionPanel = false;
FlightRecord? _completedRecord = null;
string? _rankUp = null;          // non-null if pilot ranked up this flight
string _rankBefore = "";         // captured before FlightCompleted fires
```

### Trigger flow

Acars.razor subscribes to two events: `FlightCompleted` (on `FlightSessionManager`) and `DataRefreshed` (on `PilotHubViewModel`). Both are subscribed in `OnInitializedAsync` and unsubscribed in `DisposeAsync`.

**In the `FlightCompleted` handler:**
1. Capture `_rankBefore = PilotHubViewModel.CurrentRank` (before stats refresh)
2. Set `_completedRecord = flightRecord`
3. Return — do not show the panel yet; wait for stats to refresh

**In the `DataRefreshed` handler** (fires after `PilotHubViewModel.RefreshAsync()` completes):
4. If `_completedRecord == null`, return (DataRefreshed from an unrelated refresh — ignore)
5. Compare `PilotHubViewModel.CurrentRank` to `_rankBefore` — if different, set `_rankUp`
6. Call `SaveSimBriefPlanAsync(null)` to clear the persisted plan
7. Set `_showCompletionPanel = true`, call `StateHasChanged()`

### Panel layout

```
┌─────────────────────────┐
│ ✦ FLIGHT COMPLETE       │
│ EGLL → KJFK             │
│ A380 · VAV064           │
├─────────────────────────┤
│ Landing VS   −182 fpm   │
│ Score          94/100   │
│ Air Time         7h 32m │
│ Distance       3,459 nm │
│ Fuel Used    12,400 lbs │
├─────────────────────────┤
│ 🎖 RANK UP              │  ← only shown if _rankUp != null
│ [new rank name]         │
├─────────────────────────┤
│       [Dismiss]         │
└─────────────────────────┘
```

### Animation

CSS: `position: fixed; right: 0; top: 0; height: 100vh; width: 340px`  
Slide-in via `transform: translateX(100%)` → `translateX(0)` on `opacity` and `transform` only.  
Spring-style easing: `cubic-bezier(0.34, 1.56, 0.64, 1)`.

---

## 5. Dashboard + Logbook Live Wiring

### Dashboard.razor

Inject `PilotHubViewModel`. Subscribe to `DataRefreshed` event in `OnInitializedAsync`, unsubscribe in `DisposeAsync`. Call `StateHasChanged()` on event.

Replace hardcoded values with:
- Total flights → `PilotHubViewModel.TotalFlights`
- Total air hours → `PilotHubViewModel.TotalAirHours`
- Current rank → `PilotHubViewModel.CurrentRank`
- Average landing score → `PilotHubViewModel.AverageLandingScore`

### Flights.razor (Logbook)

Read from `IFlightRepository.GetAllAsync()` filtered to `Status == FlightStatus.Completed`.  
If currently hardcoded, wire the same way as Dashboard. Verified/fixed during implementation.

---

## 6. Rank Alignment

Update `PilotStatsService.ComputeAsync()` (and any other client-side rank computation in `PilotHubViewModel`) to match the server's 7-tier table exactly:

| Min hours (inclusive) | Max hours (exclusive) | Rank |
|---|---|---|
| 0 | 25 | Cadet |
| 25 | 75 | Junior FO |
| 75 | 200 | FO |
| 200 | 450 | Senior FO |
| 450 | 800 | Junior Captain |
| 800 | 1500 | Captain |
| 1500 | ∞ | Senior Captain |

This ensures the rank shown in-app matches what the Discord bot announces.

---

## 7. Discord Notifications

No client-side changes needed. The backend `POST /api/v1/pireps` handler already:
1. Accepts the PIREP
2. Fetches the pilot's current total hours (ACARS + manual)
3. Computes new rank via `computeRank()`
4. If rank changed: calls `syncDiscordRank()` → removes old Discord role, assigns new role, sends DM, posts to `#promotions` channel

The client just needs to ensure `air_minutes` is correctly populated in the PIREP payload (it already is via `FlightRecord.AirTime`).

---

## Out of Scope

- Matching flight route to booking by ICAO/IATA lookup (using active booking as ground truth instead — Option A)
- Manual PIREP flow
- Any changes to the replay, telemetry, or map pages
