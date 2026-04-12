 Aviates Air Tracker — Exhaustive Bug Report

  🔴 Critical Issues (11)

  ID: CRIT-01
  File: ViewModels/ChildViewModels.cs:664
  Issue: ReplayViewModel uses System.Timers.Timer — fires on ThreadPool, not UI thread. Setting [ObservableProperty]
    values from it crashes the app every time replay is used (InvalidOperationException).
  ────────────────────────────────────────
  ID: CRIT-02
  File: Services/RunwayDetector.cs:190
  Issue: UpdateForApproach calls Detect() but discards the result — AND is never called anywhere. Every LandingResult
    will always show "RWY??" and "????". The entire runway detection system is dead code.
  ────────────────────────────────────────
  ID: CRIT-03
  File: ViewModels/ChildViewModels.cs:445 + Services/FlightSessionManager.cs:259
  Issue: Every landing is saved twice to the repository — once in FlightSessionManager.OnLandingDetected and again in
    LandingAnalysisViewModel.AddLanding. Stats/averages are computed on doubled data.
  ────────────────────────────────────────
  ID: CRIT-04
  File: Core/SimConnect/TelemetryProcessor.cs:132
  Issue: Crosswind sign is inverted. Wind from the left shows as positive (right) and vice versa. All landing scores and

    crosswind displays are wrong.
  ────────────────────────────────────────
  ID: CRIT-05
  File: Core/Analytics/FlightPhaseDetector.cs:87
  Issue: _hasLeftGround is set on every frame but never read. The intended guard against Parked → Takeoff re-cycling
    after landing is completely absent.
  ────────────────────────────────────────
  ID: CRIT-06
  File: Core/Analytics/FlightPhaseDetector.cs:131
  Issue: FinalApproach requires gearDown == true. Fixed-gear aircraft (Cessna 172, ATR, etc.) have GearPosition = 0
    always → Landing phase never fires for all GA aircraft. Flare detection and landing scoring are broken for the
  entire
     GA fleet.
  ────────────────────────────────────────
  ID: CRIT-07
  File: Core/Analytics/LandingAnalyzer.cs:68
  Issue: _minVS_InApproach initialised to 0 causes false flare detections during takeoff climb on touch-and-go flights,
    contaminating the approach history buffer. Should be double.MaxValue.
  ────────────────────────────────────────
  ID: CRIT-08
  File: Core/Data/DataRepositories.cs:119
  Issue: GetCurrentFlight/SetCurrentFlight have no lock, unlike every other method in the class. Written from the
    SimConnect thread, read from the UI thread — data race.
  ────────────────────────────────────────
  ID: CRIT-09
  File: Core/SimConnect/SimConnectManager.cs:410
  Issue: PollTelemetry calls _scAssembly.GetType(...) and Enum.Parse(...) via reflection at 20Hz (144,000 calls/2hr
    flight). These values never change — cache them once at startup.
  ────────────────────────────────────────
  ID: CRIT-10
  File: Views/ViewCodeBehinds.cs:269
  Issue: RenderFrame catch block logs the exception then re-throws it — any Mapsui tile/projection error causes an
    unhandled DispatcherUnhandledException that crashes the app. Remove the throw;.
  ────────────────────────────────────────
  ID: CRIT-11
  File: ViewModels/ChildViewModels.cs:57,477
  Issue: DashboardViewModel.RefreshAsync and StatisticsViewModel.RefreshAsync are both async void. Repository exceptions

    silently crash the app with no ability to catch them. Change to async Task.

  ---
  🟠 Major Issues (13)

  ┌──────────┬─────────────────────────────────┬─────────────────────────────────────────────────────────────────────┐
  │    ID    │              File               │                                Issue                                │
  ├──────────┼─────────────────────────────────┼─────────────────────────────────────────────────────────────────────┤
  │          │                                 │ Flight only transitions Landed → Complete if parking brake is set.  │
  │ MAJOR-01 │ FlightSessionManager.cs:167     │ Most MSFS pilots never set it — most flights are recorded as        │
  │          │                                 │ InProgress forever, statistics show 0 completed flights.            │
  ├──────────┼─────────────────────────────────┼─────────────────────────────────────────────────────────────────────┤
  │          │                                 │ FlightRecord.FlightPath and RouteTracker._recordedPath diverge —    │
  │ MAJOR-02 │ FlightSessionManager.cs:242     │ the flight record receives pre-takeoff points, the route tracker    │
  │          │                                 │ doesn't start until wheels-up. Two inconsistent paths.              │
  ├──────────┼─────────────────────────────────┼─────────────────────────────────────────────────────────────────────┤
  │          │                                 │ _bufferCount is not thread-safe. Concurrent ClearHistory +          │
  │ MAJOR-03 │ TelemetryProcessor.cs:144       │ AddToHistory can make the counter go negative, causing unbounded    │
  │          │                                 │ queue growth.                                                       │
  ├──────────┼─────────────────────────────────┼─────────────────────────────────────────────────────────────────────┤
  │          │                                 │ StabilityChecker and ApproachMonitor are registered in DI but never │
  │ MAJOR-04 │ App.xaml.cs:94                  │  called anywhere in the pipeline. The GEAR DOWN alert and full      │
  │          │                                 │ stability checks are completely inoperative.                        │
  ├──────────┼─────────────────────────────────┼─────────────────────────────────────────────────────────────────────┤
  │          │                                 │ Bounce detection: _airborneTime is never reset in                   │
  │ MAJOR-05 │ LandingAnalyzer.cs:208          │ ResetForNextLanding. First flight after app start correctly handles │
  │          │                                 │  it, but a bounce of ≥3s resets to a "new primary landing" and      │
  │          │                                 │ zeroes _bounceCount, losing the bounce record.                      │
  ├──────────┼─────────────────────────────────┼─────────────────────────────────────────────────────────────────────┤
  │          │                                 │ Fuel is multiplied by 2.20462 (kg→lbs) unconditionally, but         │
  │ MAJOR-06 │ SimBriefService.cs:111          │ SimBrief returns the unit configured in the pilot's OFP. Pilots     │
  │          │                                 │ using lbs in SimBrief get ~2.2× the actual fuel load shown.         │
  ├──────────┼─────────────────────────────────┼─────────────────────────────────────────────────────────────────────┤
  │          │                                 │ DashboardViewModel calls GetRecentAsync(5) — TotalFlightsText       │
  │ MAJOR-07 │ ChildViewModels.cs:59           │ always shows max "5", career hours and distance are computed on     │
  │          │                                 │ only 5 flights. A pilot with 50 flights sees ~90% wrong career      │
  │          │                                 │ stats.                                                              │
  ├──────────┼─────────────────────────────────┼─────────────────────────────────────────────────────────────────────┤
  │          │                                 │ BlockTime and AirTime computed properties return deeply negative    │
  │ MAJOR-08 │ Models/FlightModels.cs:44       │ values (DateTime.MinValue - DateTime.UtcNow) for in-progress        │
  │          │                                 │ flights. No guard.                                                  │
  ├──────────┼─────────────────────────────────┼─────────────────────────────────────────────────────────────────────┤
  │ MAJOR-09 │ Services/SupportServices.cs:41  │ TotalFuelBurnedLbs can go negative if pilot refuels mid-session     │
  │          │                                 │ (MSFS reload) — start fuel becomes lower than current fuel.         │
  ├──────────┼─────────────────────────────────┼─────────────────────────────────────────────────────────────────────┤
  │          │                                 │ FuelLeftMainLbs / FuelRightMainLbs fields are registered as         │
  │ MAJOR-10 │ SimConnectDefinitions.cs:67     │ "gallons" in SimConnectManager but named/treated as lbs. Unit       │
  │          │                                 │ mismatch — fuel quantities are gallons everywhere they display as   │
  │          │                                 │ lbs.                                                                │
  ├──────────┼─────────────────────────────────┼─────────────────────────────────────────────────────────────────────┤
  │          │                                 │ EMA filter values are computed (_vsFilter.Filter(rawVS)) but the    │
  │ MAJOR-11 │ TelemetryProcessor.cs:109       │ return value is assigned to _ — the filtered values are never used. │
  │          │                                 │  All downstream code sees raw noisy SimConnect data.                │
  ├──────────┼─────────────────────────────────┼─────────────────────────────────────────────────────────────────────┤
  │          │                                 │ _landingAnalyzer.Process(snap) is called at 20Hz before any flight  │
  │ MAJOR-12 │ FlightSessionManager.cs:112     │ exists (State == Idle). Pre-touchdown buffer fills with parked/taxi │
  │          │                                 │  telemetry, contaminating approach history.                         │
  ├──────────┼─────────────────────────────────┼─────────────────────────────────────────────────────────────────────┤
  │          │                                 │ At 20Hz during approach, ShowAlert spawns a new Task.Delay timer    │
  │ MAJOR-13 │ Services/SupportServices.cs:161 │ per call — up to 60 concurrent pending timers simultaneously with a │
  │          │                                 │  3s duration, plus a second removal timer in MainViewModel.         │
  └──────────┴─────────────────────────────────┴─────────────────────────────────────────────────────────────────────┘

  ---
  🟡 Minor Issues (20)

  Key highlights:
  - MINOR-01: SimConnect reconnect timer starts before the HWND is available (wasteful)
  - MINOR-02: Duplicate redundant if (vs > CRUISE_VS_BAND_FPM) blocks — first branch is dead code
  - MINOR-04: Short-haul flights below 8000ft stay in "Climb" phase forever while cruising
  - MINOR-07: Settings, crash logs, and Serilog logs all use relative paths → UnauthorizedAccessException when installed
   to Program Files
  - MINOR-11: Approach speed check hardcoded to 100–180 kts — every GA aircraft (Cessna at 65kts) is always scored as
  "unstable approach"
  - MINOR-14: Full fuel tank shows orange instead of green in the fuel gauge
  - MINOR-17: AgentDebug.cs writes a debug-9ee785.log file to disk on every page navigation in production builds
  - MINOR-19: InMemoryPilotRepository has zero thread safety (unlike the other two repositories)
  - MINOR-20: Multiple _ = repo.SaveAsync(...) fire-and-forgets silently discard exceptions

  ---
  Top Priorities to Fix First

  1. CRIT-01 — Replay crashes immediately (threading)
  2. CRIT-06 — GA aircraft (Cessna, ATR) never complete a landing
  3. MAJOR-07 — Dashboard always shows "5 total flights"
  4. MINOR-07 — Settings never save when installed to Program Files
  5. CRIT-03 — All landings saved twice, corrupting every statistic
  6. CRIT-10 — Map tile errors crash the whole app
  7. MAJOR-01 — Flights never marked complete without parking brake