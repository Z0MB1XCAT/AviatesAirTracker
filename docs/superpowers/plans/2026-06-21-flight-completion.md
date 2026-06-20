# Flight Completion System — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the full post-flight lifecycle — gate-in detection already exists; this plan wires booking close-out, SimBrief plan persistence, a slide-in completion panel, rank alignment, and live Dashboard stats.

**Architecture:** `FlightSessionManager.OnFlightComplete()` already fires `FlightCompleted` and calls `SubmitPirepAsync`. We extend it to also call `BookingService.CompleteActiveBookingAsync()`. `Acars.razor` subscribes to `FlightCompleted` + `PilotHubViewModel.DataRefreshed` to show the slide-in panel once stats have refreshed. `SettingsService` gains two methods for SimBrief plan persistence. Dashboard wires to the existing `PilotHubViewModel`.

**Tech Stack:** .NET 8 Blazor Hybrid, C# 12, CommunityToolkit.Mvvm, System.Text.Json, Serilog, Cloudflare Worker (JS), Supabase

**Spec:** `docs/superpowers/specs/2026-06-21-flight-completion-design.md`

---

## File Map

| File | Change |
|------|--------|
| `BACKEND-API/worker.js` | Add `PATCH /api/bookings/:id` inside `handleBookingsApi` |
| `AviatesAirTracker/Services/BookingService.cs` | Add `CompleteActiveBookingAsync()` |
| `AviatesAirTracker/Services/FlightSessionManager.cs` | Add `BookingService` field + constructor param; call completion in `OnFlightComplete()` |
| `AviatesAirTracker/Services/SupportServices.cs` | Add `SaveSimBriefPlanAsync` / `LoadSimBriefPlanAsync` to `SettingsService`; update rank tiers in `PilotStatsService` |
| `AviatesAirTracker/Pages/Acars.razor` | Inject `PilotHubViewModel`; add state fields; subscribe to events; persist SimBrief plan; add slide-in completion panel HTML |
| `AviatesAirTracker/wwwroot/css/app.css` | Add completion panel CSS |
| `AviatesAirTracker/Pages/Dashboard.razor` | Inject `PilotHubViewModel`; subscribe to `DataRefreshed`; replace hardcoded stat values |

---

## Task 1 — Supabase: add `completed_at` column to `bookings`

**Files:**
- Migration via Supabase MCP

- [ ] **Step 1: Check if `completed_at` already exists**

  Use the Supabase MCP `list_tables` tool. Look at the `bookings` table columns. If `completed_at` is already present, skip to Task 2.

- [ ] **Step 2: Apply migration**

  Use the Supabase MCP `apply_migration` tool with name `add_booking_completed_at` and SQL:

  ```sql
  ALTER TABLE bookings ADD COLUMN IF NOT EXISTS completed_at TIMESTAMPTZ;
  ```

- [ ] **Step 3: Commit worker change note**

  ```bash
  git commit --allow-empty -m "chore: verified Supabase bookings.completed_at column exists"
  ```

---

## Task 2 — Backend: `PATCH /api/bookings/:id`

**Files:**
- Modify: `BACKEND-API/worker.js` — inside `handleBookingsApi`, before the final `return json({ error: "Not found" }, 404, corsHeaders)` (around line 2247)

- [ ] **Step 1: Add the PATCH handler**

  Open `BACKEND-API/worker.js`. Find the line:
  ```js
  return json({ error: "Not found" }, 404, corsHeaders);
  ```
  that is inside `handleBookingsApi` (the last line before the `} catch` block). Insert this block **before** it:

  ```js
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
      return json({ error: `Booking is already ${booking.status}` }, 409, corsHeaders);

    await sbUpdate(env, "bookings", `id=eq.${bookingId}`, {
      status:       "completed",
      completed_at: new Date().toISOString(),
    });
    return json({ success: true }, 200, corsHeaders);
  }
  ```

- [ ] **Step 2: Verify the file is valid JS**

  Check that the inserted block is inside `handleBookingsApi`, before `return json({ error: "Not found" }...)` and before `} catch (e) {`. The DELETE match block above it ends with `return json({ success: true }, 200, corsHeaders);` — the new block goes right after.

- [ ] **Step 3: Commit**

  ```bash
  git add BACKEND-API/worker.js
  git commit -m "feat(api): PATCH /api/bookings/:id marks booking completed"
  ```

---

## Task 3 — `BookingService`: add `CompleteActiveBookingAsync()`

**Files:**
- Modify: `AviatesAirTracker/Services/BookingService.cs` — after `CancelBookingAsync` (around line 246)

- [ ] **Step 1: Add the method**

  After the closing `}` of `CancelBookingAsync`, add:

  ```csharp
  // =====================================================
  // COMPLETE ACTIVE BOOKING
  // Marks the active booking as completed on the backend,
  // advances the pilot's home base to the arrival airport,
  // and clears ActiveBooking. Fire-and-forget safe.
  // =====================================================

  public async Task CompleteActiveBookingAsync()
  {
      if (ActiveBooking == null) return;

      var booking  = ActiveBooking;
      var acarsKey = _settings.Settings.AcarsKey;

      // Clear immediately — UI should not show a stale active booking
      ActiveBooking = null;

      if (string.IsNullOrEmpty(acarsKey)) return;

      try
      {
          _http.DefaultRequestHeaders.Authorization =
              new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", acarsKey);

          var response = await _http.PatchAsync(
              $"/api/bookings/{booking.Id}",
              new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

          if (response.IsSuccessStatusCode)
              Log.Information("[BookingService] Booking {Id} completed", booking.Id);
          else
              Log.Warning("[BookingService] Complete booking {Id} failed: {Status}",
                  booking.Id, response.StatusCode);
      }
      catch (Exception ex)
      {
          Log.Warning(ex, "[BookingService] CompleteActiveBookingAsync error");
      }

      // Update home base regardless of API success
      if (!string.IsNullOrEmpty(booking.DestIata))
          await SetPositionAsync(booking.DestIata);
  }
  ```

- [ ] **Step 2: Build**

  ```bash
  dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
  ```
  Expected: 0 errors, same pre-existing warnings as before (14–28).

- [ ] **Step 3: Commit**

  ```bash
  git add AviatesAirTracker/Services/BookingService.cs
  git commit -m "feat(booking): CompleteActiveBookingAsync closes booking and advances home base"
  ```

---

## Task 4 — `FlightSessionManager`: inject `BookingService` + wire completion

**Files:**
- Modify: `AviatesAirTracker/Services/FlightSessionManager.cs`

- [ ] **Step 1: Add the field**

  After line 53 (`private readonly AviatesBackendClient _backend;`), add:

  ```csharp
  private readonly BookingService _bookingService;
  ```

- [ ] **Step 2: Add constructor parameter and assignment**

  The constructor signature is at line 76. Change:
  ```csharp
  public FlightSessionManager(
      ...
      AviatesBackendClient backend)
  {
      ...
      // end of existing assignments
  ```
  to:
  ```csharp
  public FlightSessionManager(
      TelemetryProcessor telemetry,
      FlightPhaseDetector phaseDetector,
      LandingAnalyzer landingAnalyzer,
      RouteTracker routeTracker,
      FuelAnalyzer fuelAnalyzer,
      IFlightRepository flightRepo,
      ILandingRepository landingRepo,
      AlertService alertService,
      SettingsService settings,
      RunwayDetector runwayDetector,
      ApproachMonitor approachMonitor,
      AviatesBackendClient backend,
      BookingService bookingService)          // ← new
  {
  ```
  And inside the constructor body, after `_backend = backend;`, add:
  ```csharp
  _bookingService = bookingService;
  ```

- [ ] **Step 3: Wire completion in `OnFlightComplete()`**

  Find `OnFlightComplete()` (line 321). After the line:
  ```csharp
  _ = SubmitPirepSafeAsync(completedFlight);
  ```
  Add:
  ```csharp
  _ = _bookingService.CompleteActiveBookingAsync();
  ```

- [ ] **Step 4: Build**

  ```bash
  dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
  ```
  Expected: 0 errors. (.NET DI resolves `BookingService` automatically — it is already registered as a singleton in `App.xaml.cs`.)

- [ ] **Step 5: Commit**

  ```bash
  git add AviatesAirTracker/Services/FlightSessionManager.cs
  git commit -m "feat(session): inject BookingService and complete active booking on flight end"
  ```

---

## Task 5 — `SupportServices`: SimBrief persistence + 7-tier rank

**Files:**
- Modify: `AviatesAirTracker/Services/SupportServices.cs`

- [ ] **Step 1: Add `using System.Text.Json;` if not present**

  Check the top of `SupportServices.cs` for `using System.Text.Json;`. If absent, add it (below the existing `using` block).

- [ ] **Step 2: Add SimBrief persistence to `SettingsService`**

  Inside the `SettingsService` class, after the `RefreshFriendCode()` method (around line 299), add:

  ```csharp
  private static readonly string SimBriefPlanPath = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
      "AviatesAirTracker", "simbrief_plan.json");

  public async Task SaveSimBriefPlanAsync(AviatesAirTracker.Models.SimBriefFlightPlan? plan)
  {
      try
      {
          Directory.CreateDirectory(Path.GetDirectoryName(SimBriefPlanPath)!);
          if (plan == null)
          {
              if (File.Exists(SimBriefPlanPath)) File.Delete(SimBriefPlanPath);
              return;
          }
          var json = JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true });
          await File.WriteAllTextAsync(SimBriefPlanPath, json);
          Log.Debug("[Settings] SimBrief plan saved to disk");
      }
      catch (Exception ex)
      {
          Log.Warning(ex, "[Settings] Failed to save SimBrief plan");
      }
  }

  public async Task<AviatesAirTracker.Models.SimBriefFlightPlan?> LoadSimBriefPlanAsync()
  {
      try
      {
          if (!File.Exists(SimBriefPlanPath)) return null;
          var json = await File.ReadAllTextAsync(SimBriefPlanPath);
          return JsonSerializer.Deserialize<AviatesAirTracker.Models.SimBriefFlightPlan>(json);
      }
      catch (Exception ex)
      {
          Log.Warning(ex, "[Settings] Failed to load SimBrief plan");
          return null;
      }
  }
  ```

- [ ] **Step 3: Update rank tiers in `PilotStatsService.ComputeAsync()`**

  Find the rank switch at lines ~382–389:
  ```csharp
  stats.Rank = stats.TotalHoursBlock switch
  {
      < 25 => "Student Pilot",
      < 100 => "First Officer",
      < 500 => "Senior First Officer",
      < 1000 => "Captain",
      _ => "Senior Captain"
  };
  ```
  Replace with:
  ```csharp
  stats.Rank = stats.TotalHoursAir switch
  {
      < 25   => "Cadet",
      < 75   => "Junior FO",
      < 200  => "FO",
      < 450  => "Senior FO",
      < 800  => "Junior Captain",
      < 1500 => "Captain",
      _      => "Senior Captain"
  };
  ```
  Note: Rank is now based on **air hours** (`TotalHoursAir`) to match the user's requirement and align with the server's `syncDiscordRank` logic.

- [ ] **Step 4: Build**

  ```bash
  dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
  ```
  Expected: 0 errors.

- [ ] **Step 5: Commit**

  ```bash
  git add AviatesAirTracker/Services/SupportServices.cs
  git commit -m "feat(settings): SimBrief plan persistence; align rank tiers to 7-tier server scale"
  ```

---

## Task 6 — `Acars.razor`: plan persistence + completion panel

**Files:**
- Modify: `AviatesAirTracker/Pages/Acars.razor`
- Modify: `AviatesAirTracker/wwwroot/css/app.css`

> **Before writing any HTML/CSS:** Invoke the `frontend-design` skill and read `DESIGN.md`. The panel must follow brand rules: `#0A0D17` bg, `#3D7EEE` accent, animate `transform`/`opacity` only, spring easing `cubic-bezier(0.34, 1.56, 0.64, 1)`, layered shadows.

- [ ] **Step 1: Add `PilotHubViewModel` injection**

  In the `@inject` block at the top of `Acars.razor`, add:
  ```razor
  @inject AviatesAirTracker.ViewModels.PilotHubViewModel PilotHub
  ```

- [ ] **Step 2: Add completion panel state fields**

  In the `@code { }` block, near the top with the other private state fields, add:
  ```csharp
  // Flight completion panel
  private bool         _showCompletionPanel = false;
  private FlightRecord? _completedRecord    = null;
  private string?      _rankUp             = null;
  private string       _rankBefore         = "";
  ```

- [ ] **Step 3: Load persisted plan in `OnInitializedAsync`**

  Inside `OnInitializedAsync`, after the existing init code (but before any `auto-import` SimBrief call), add:
  ```csharp
  // Load persisted SimBrief plan from previous session if none active
  if (_flightPlan == null)
      _flightPlan = await SettingsService.LoadSimBriefPlanAsync();
  ```

- [ ] **Step 4: Subscribe to events in `OnInitializedAsync`**

  After the plan load, add:
  ```csharp
  Session.FlightCompleted += OnFlightCompleted;
  PilotHub.DataRefreshed  += OnStatsRefreshed;
  ```

- [ ] **Step 5: Unsubscribe in `DisposeAsync`**

  Find the `DisposeAsync` method. Add alongside the existing unsubscriptions:
  ```csharp
  Session.FlightCompleted -= OnFlightCompleted;
  PilotHub.DataRefreshed  -= OnStatsRefreshed;
  ```

- [ ] **Step 6: Save plan after successful SimBrief import**

  Find the `ImportSimBrief()` method (or equivalent). Locate where `_flightPlan` is assigned after a successful fetch. Immediately after, add:
  ```csharp
  await SettingsService.SaveSimBriefPlanAsync(_flightPlan);
  ```

- [ ] **Step 7: Add event handlers**

  In the `@code` block, add:
  ```csharp
  private void OnFlightCompleted(object? sender, FlightRecord record)
  {
      // Capture rank BEFORE stats refresh so we can detect a rank-up
      _rankBefore      = PilotHub.PilotRank;
      _completedRecord = record;
      // Panel is shown in OnStatsRefreshed once PilotHubViewModel has updated
  }

  private async void OnStatsRefreshed()
  {
      // Only act if a flight just completed (not a routine refresh)
      if (_completedRecord == null) return;

      _rankUp = PilotHub.PilotRank != _rankBefore ? PilotHub.PilotRank : null;

      // Clear the persisted plan — it has been flown
      await SettingsService.SaveSimBriefPlanAsync(null);
      _flightPlan = null;

      _showCompletionPanel = true;
      await InvokeAsync(StateHasChanged);
  }

  private void DismissCompletionPanel()
  {
      _showCompletionPanel = false;
      _completedRecord     = null;
      _rankUp              = null;
  }

  private string FormatVS(FlightRecord r)
  {
      if (r.PrimaryLanding == null) return "—";
      var vs = (int)r.PrimaryLanding.VerticalSpeedFPM;
      return vs >= 0 ? $"+{vs} fpm" : $"{vs} fpm";
  }

  private string LandingVsClass(FlightRecord r)
  {
      if (r.PrimaryLanding == null) return "neutral";
      var abs = Math.Abs(r.PrimaryLanding.VerticalSpeedFPM);
      return abs <= 150 ? "green" : abs <= 300 ? "amber" : "red";
  }

  private static string FormatAirTime(TimeSpan t)
      => t.TotalHours >= 1
          ? $"{(int)t.TotalHours}h {t.Minutes:00}m"
          : $"{t.Minutes}m";
  ```

- [ ] **Step 8: Add slide-in panel markup**

  Add at the very end of the markup section (before the closing tag of the page root element), inside an `@if` guard:

  ```razor
  @if (_showCompletionPanel && _completedRecord != null)
  {
      <div class="completion-overlay" @onclick="DismissCompletionPanel">
          <div class="completion-panel" @onclick:stopPropagation="true">

              <div class="completion-header">
                  <span class="completion-eyebrow">✦ FLIGHT COMPLETE</span>
                  <div class="completion-route">
                      @_completedRecord.DepartureICAO<span class="route-arrow-sep">→</span>@_completedRecord.ArrivalICAO
                  </div>
                  <div class="completion-sub">
                      @_completedRecord.AircraftType
                      @if (!string.IsNullOrEmpty(_completedRecord.Callsign))
                      {
                          <span> · @_completedRecord.Callsign</span>
                      }
                  </div>
              </div>

              <div class="completion-stats">
                  <div class="completion-stat">
                      <span class="completion-stat-label">Landing VS</span>
                      <span class="completion-stat-value vs-@LandingVsClass(_completedRecord)">
                          @FormatVS(_completedRecord)
                      </span>
                  </div>
                  <div class="completion-stat">
                      <span class="completion-stat-label">Score</span>
                      <span class="completion-stat-value score-val">
                          @(_completedRecord.PrimaryLanding?.LandingScore.ToString() ?? "—")<span class="stat-denom">/100</span>
                      </span>
                  </div>
                  <div class="completion-stat">
                      <span class="completion-stat-label">Air Time</span>
                      <span class="completion-stat-value">@FormatAirTime(_completedRecord.AirTime)</span>
                  </div>
                  <div class="completion-stat">
                      <span class="completion-stat-label">Distance</span>
                      <span class="completion-stat-value">
                          @(_completedRecord.ActualDistanceNm.ToString("F0"))<span class="stat-denom"> nm</span>
                      </span>
                  </div>
                  <div class="completion-stat">
                      <span class="completion-stat-label">Fuel Used</span>
                      <span class="completion-stat-value">
                          @(_completedRecord.FuelUsedLbs.ToString("F0"))<span class="stat-denom"> lbs</span>
                      </span>
                  </div>
              </div>

              @if (_rankUp != null)
              {
                  <div class="completion-rankup">
                      <span class="rankup-icon">🎖</span>
                      <div>
                          <div class="rankup-label">Rank Up!</div>
                          <div class="rankup-value">@_rankUp</div>
                      </div>
                  </div>
              }

              <button class="completion-dismiss" @onclick="DismissCompletionPanel">Dismiss</button>

          </div>
      </div>
  }
  ```

- [ ] **Step 9: Add CSS to `app.css`**

  Append to `AviatesAirTracker/wwwroot/css/app.css`:

  ```css
  /* ── Flight Completion Slide-in Panel ───────────────── */
  .completion-overlay {
      position: fixed;
      inset: 0;
      z-index: 9000;
      pointer-events: auto;
  }

  .completion-panel {
      position: fixed;
      top: 0;
      right: 0;
      height: 100vh;
      width: 340px;
      background: linear-gradient(180deg, #111827 0%, #0f172a 100%);
      border-left: 2px solid rgba(61, 126, 238, 0.35);
      box-shadow:
          -8px 0 48px rgba(0, 0, 0, 0.6),
          -2px 0 16px rgba(61, 126, 238, 0.08),
          inset 1px 0 0 rgba(61, 126, 238, 0.05);
      display: flex;
      flex-direction: column;
      gap: 0;
      padding: 32px 24px 28px;
      overflow-y: auto;
      animation: completionSlideIn 0.5s cubic-bezier(0.34, 1.56, 0.64, 1) both;
  }

  @keyframes completionSlideIn {
      from { transform: translateX(100%); opacity: 0; }
      to   { transform: translateX(0);   opacity: 1; }
  }

  .completion-header {
      margin-bottom: 24px;
  }

  .completion-eyebrow {
      display: block;
      font-size: 10px;
      letter-spacing: 0.15em;
      color: #3D7EEE;
      text-transform: uppercase;
      font-weight: 700;
      margin-bottom: 8px;
  }

  .completion-route {
      font-size: 26px;
      font-weight: 900;
      color: #fff;
      letter-spacing: -0.03em;
      line-height: 1.1;
  }

  .route-arrow-sep {
      margin: 0 6px;
      color: #3D7EEE;
      font-weight: 400;
  }

  .completion-sub {
      font-size: 12px;
      color: #64748b;
      margin-top: 6px;
  }

  .completion-stats {
      display: flex;
      flex-direction: column;
      gap: 6px;
      margin-bottom: 20px;
  }

  .completion-stat {
      background: #0A0D17;
      border-radius: 10px;
      padding: 11px 14px;
      display: flex;
      justify-content: space-between;
      align-items: center;
  }

  .completion-stat-label {
      font-size: 11px;
      color: #64748b;
      text-transform: uppercase;
      letter-spacing: 0.05em;
  }

  .completion-stat-value {
      font-size: 16px;
      font-weight: 800;
      color: #fff;
  }

  .completion-stat-value.vs-green  { color: #22c55e; }
  .completion-stat-value.vs-amber  { color: #f59e0b; }
  .completion-stat-value.vs-red    { color: #ef4444; }
  .completion-stat-value.vs-neutral { color: #94a3b8; }
  .completion-stat-value.score-val  { color: #3D7EEE; }

  .stat-denom {
      font-size: 11px;
      font-weight: 500;
      color: #64748b;
  }

  .completion-rankup {
      background: linear-gradient(135deg, rgba(139, 92, 246, 0.12), rgba(61, 126, 238, 0.08));
      border: 1px solid rgba(139, 92, 246, 0.3);
      border-radius: 10px;
      padding: 14px;
      display: flex;
      align-items: center;
      gap: 12px;
      margin-bottom: 20px;
  }

  .rankup-icon { font-size: 22px; line-height: 1; }

  .rankup-label {
      font-size: 10px;
      font-weight: 700;
      color: #a78bfa;
      text-transform: uppercase;
      letter-spacing: 0.1em;
      margin-bottom: 3px;
  }

  .rankup-value {
      font-size: 14px;
      font-weight: 800;
      color: #c4b5fd;
  }

  .completion-dismiss {
      margin-top: auto;
      width: 100%;
      background: #3D7EEE;
      border: none;
      border-radius: 10px;
      padding: 13px;
      color: #fff;
      font-size: 13px;
      font-weight: 700;
      cursor: pointer;
      letter-spacing: 0.03em;
      transition: opacity 0.15s ease, transform 0.15s ease;
  }

  .completion-dismiss:hover  { opacity: 0.85; }
  .completion-dismiss:active { transform: scale(0.97); }
  .completion-dismiss:focus-visible {
      outline: 2px solid #3D7EEE;
      outline-offset: 2px;
  }
  ```

- [ ] **Step 10: Build**

  ```bash
  dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
  ```
  Expected: 0 errors.

- [ ] **Step 11: Commit**

  ```bash
  git add AviatesAirTracker/Pages/Acars.razor AviatesAirTracker/wwwroot/css/app.css
  git commit -m "feat(acars): SimBrief persistence across reloads + flight completion slide-in panel"
  ```

---

## Task 7 — `Dashboard.razor`: live stats wiring

**Files:**
- Modify: `AviatesAirTracker/Pages/Dashboard.razor`

- [ ] **Step 1: Add directives at the top**

  After `@page "/"`, add:
  ```razor
  @using AviatesAirTracker.ViewModels
  @inject PilotHubViewModel PilotHub
  @implements IDisposable
  ```

- [ ] **Step 2: Add `@code` block**

  If no `@code` block exists, add one at the end of the file:
  ```razor
  @code {
      protected override async Task OnInitializedAsync()
      {
          PilotHub.DataRefreshed += OnDataRefreshed;
          await PilotHub.RefreshAsync();
      }

      private void OnDataRefreshed() => InvokeAsync(StateHasChanged);

      public void Dispose() => PilotHub.DataRefreshed -= OnDataRefreshed;
  }
  ```

- [ ] **Step 3: Replace hardcoded stat values**

  Find the stat strip (around lines 66–115). The three stat cards currently show hardcoded `2`, `14`, and `82`. Replace:

  **Flights Completed card** — find `<span class="stat-value">2</span>` and replace:
  ```razor
  <span class="stat-value">@PilotHub.TotalFlights</span>
  ```
  Remove the `<span class="stat-unit">/ 8</span>` (no quota data available).

  **Block Hours card** — find `<span class="stat-value">14</span>` and replace:
  ```razor
  <span class="stat-value">@PilotHub.BlockHours</span>
  ```
  Change the card label from "Block Hours" to "Air Hours" (change `<div class="stat-meta">Block Hours</div>` → `Air Hours`).

  **Avg Landing card** — find `<span class="stat-value">82</span>` and replace:
  ```razor
  <span class="stat-value">@PilotHub.AvgLandingScore</span>
  ```

  Remove all three `<span class="stat-trend ...">` chips (no trend data) — delete those lines.

- [ ] **Step 4: Build**

  ```bash
  dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
  ```
  Expected: 0 errors.

- [ ] **Step 5: Commit**

  ```bash
  git add AviatesAirTracker/Pages/Dashboard.razor
  git commit -m "feat(dashboard): wire stat cards to live PilotHubViewModel data"
  ```

---

## Task 8 — Verify logbook (`Flights.razor`)

**Files:**
- Read-only check: `AviatesAirTracker/Pages/Flights.razor`

- [ ] **Step 1: Confirm live wiring**

  `Flights.razor` already injects `IFlightRepository` and uses `@_flights.Count` — it is already live. No code changes needed.

  If during review you discover it does NOT reload on `FlightCompleted`, add this to its `OnInitializedAsync` / `Dispose`:
  ```csharp
  Session.FlightCompleted += async (_, _) => {
      _flights = await FlightRepo.GetAllAsync();
      await InvokeAsync(StateHasChanged);
  };
  ```
  But confirm first before making changes.

- [ ] **Step 2: Final build + smoke check**

  ```bash
  dotnet build AviatesAirTracker/AviatesAirTracker.csproj -c Debug
  ```
  Expected: 0 errors.

  Run the app (`.\build.ps1 -Run`) and verify:
  1. Dashboard shows `—` stats initially, then real numbers after `PilotHub.RefreshAsync()` finishes.
  2. Import a SimBrief plan in ACARS, navigate away, return — plan should still be loaded.
  3. After a simulated flight completes (engines off at gate), the slide-in panel appears from the right.
  4. Dismissing the panel hides it and clears state.
  5. If rank changed, the purple rank-up strip is visible in the panel.
  6. After completion, navigating to Flights shows the new entry.

---

## Spec Coverage Check

| Spec requirement | Task |
|-----------------|------|
| Gate-in detection | Existing (no change needed) |
| Booking completion API | Task 2 (backend) + Task 3 (BookingService) |
| Booking wired on flight end | Task 4 (FlightSessionManager) |
| SimBrief persistence | Task 5 (SupportServices) + Task 6 (Acars.razor) |
| Slide-in completion panel | Task 6 |
| Rank-up detection in panel | Task 6 (`OnStatsRefreshed`) |
| 7-tier rank alignment | Task 5 |
| Dashboard live stats | Task 7 |
| Logbook live | Task 8 |
| Discord rank-up notification | Already handled by backend PIREP handler — no client change needed |
