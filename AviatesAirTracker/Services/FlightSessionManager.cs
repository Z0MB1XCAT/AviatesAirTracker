using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Models;
using AviatesAirTracker.Services;
using Serilog;
using System.Threading;

namespace AviatesAirTracker.Services;

// ============================================================
// FLIGHT SESSION MANAGER
//
// Top-level orchestrator for a flight session.
// Coordinates between all subsystems:
//   - TelemetryProcessor → enriched snapshots
//   - FlightPhaseDetector → phase transitions
//   - LandingAnalyzer → touchdown events
//   - RouteTracker → path recording
//   - FuelAnalyzer → burn tracking
//   - DataRepository → persistence
//
// State machine:
//   Idle → PreFlight → Taxiing → Airborne → 
//   Approach → Landed → PostFlight → Complete
// ============================================================

public class FlightSessionManager
{
    // =====================================================
    // EVENTS
    // =====================================================
    public event EventHandler<FlightSessionState>? SessionStateChanged;
    public event EventHandler<FlightRecord>? FlightCompleted;
    public event EventHandler<TelemetrySnapshot>? TelemetryUpdated;

    // =====================================================
    // DEPENDENCIES
    // =====================================================
    private readonly TelemetryProcessor _telemetry;
    private readonly FlightPhaseDetector _phaseDetector;
    private readonly LandingAnalyzer _landingAnalyzer;
    private readonly RouteTracker _routeTracker;
    private readonly FuelAnalyzer _fuelAnalyzer;
    private readonly IFlightRepository _flightRepo;
    private readonly ILandingRepository _landingRepo;
    private readonly AlertService _alertService;
    private readonly SettingsService _settings;
    // CRIT-02 / MAJOR-04: RunwayDetector and ApproachMonitor were registered in DI but never wired in.
    private readonly RunwayDetector _runwayDetector;
    private readonly ApproachMonitor _approachMonitor;
    private readonly AviatesBackendClient _backend;

    // =====================================================
    // STATE
    // =====================================================
    public FlightSessionState State { get; private set; } = FlightSessionState.Idle;
    public FlightRecord? CurrentFlight { get; private set; }
    public TelemetrySnapshot? LatestTelemetry { get; private set; }

    private DateTime _blockOutTime;
    private double _fuelAtDeparture;
    private bool _hasRecordedTakeoff;
    private bool _hasRecordedLanding;
    private int _pathSampleCounter = 0;
    private const int PATH_SAMPLE_INTERVAL = 10; // Every 10 samples = ~2Hz path recording
    // Debounce for Taxiing→Airborne: require N consecutive off-ground samples at flying speed
    private int _offGroundCounter = 0;
    private const int AIRBORNE_CONFIRM_SAMPLES = 5; // 5 × 50ms = 0.25 s at 20Hz

    // =====================================================
    // INITIALIZATION
    // =====================================================

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
        AviatesBackendClient backend)
    {
        _telemetry = telemetry;
        _phaseDetector = phaseDetector;
        _landingAnalyzer = landingAnalyzer;
        _routeTracker = routeTracker;
        _fuelAnalyzer = fuelAnalyzer;
        _flightRepo = flightRepo;
        _landingRepo = landingRepo;
        _alertService = alertService;
        _settings = settings;
        _runwayDetector = runwayDetector;
        _approachMonitor = approachMonitor;
        _backend = backend;

        // Wire up phase changes
        _phaseDetector.PhaseChanged += OnPhaseChanged;
        _landingAnalyzer.LandingDetected += OnLandingDetected;
        _landingAnalyzer.BounceDetected += OnBounceDetected;
    }

    // =====================================================
    // TELEMETRY PIPELINE ENTRY POINT
    // Called at 20Hz from SimConnectManager → TelemetryProcessor
    // =====================================================

    public void OnTelemetryReceived(TelemetrySnapshot snap)
    {
        LatestTelemetry = snap;

        // Route tracking (lower frequency)
        _pathSampleCounter++;
        if (_pathSampleCounter >= PATH_SAMPLE_INTERVAL)
        {
            _pathSampleCounter = 0;
            RecordPathPoint(snap);
        }

        // MAJOR-12: Only run landing analysis when a flight is actually in progress.
        // Previously called at 20Hz even when Idle, filling the pre-touchdown buffer with taxi telemetry.
        if (State != FlightSessionState.Idle)
            _landingAnalyzer.Process(snap);

        // Fuel tracking
        _fuelAnalyzer.Process(snap);

        // Alert checking + runway detection during approach
        if (snap.Phase == FlightPhase.Approach || snap.Phase == FlightPhase.FinalApproach)
        {
            CheckApproachAlerts(snap);

            // CRIT-02: UpdateForApproach was never called. Wire it in and pass result to LandingAnalyzer.
            var detectedRunway = _runwayDetector.UpdateForApproach(snap);
            if (detectedRunway != null)
                _landingAnalyzer.SetRunwayInfo(detectedRunway);

            // MAJOR-04: ApproachMonitor was registered in DI but never called in the pipeline.
            _approachMonitor.CheckStability(snap);
        }

        // State machine transitions
        UpdateSessionState(snap);

        // Publish
        TelemetryUpdated?.Invoke(this, snap);
    }

    // =====================================================
    // SESSION STATE MACHINE
    // =====================================================

    private void UpdateSessionState(TelemetrySnapshot snap)
    {
        switch (State)
        {
            case FlightSessionState.Idle:
                if (snap.EnginesRunning)
                    TransitionTo(FlightSessionState.PreFlight, snap);
                break;

            case FlightSessionState.PreFlight:
                if (!snap.IsParked && snap.GroundSpeedKts > 3)
                    TransitionTo(FlightSessionState.Taxiing, snap);
                break;

            case FlightSessionState.Taxiing:
                // Require several consecutive off-ground samples with meaningful airspeed
                // to prevent false transitions from single-sample ground-detection glitches
                // (e.g. bumping over a taxiway light or sloped apron).
                if (!snap.IsOnGround && snap.IASKts > 40 && snap.AltitudeAGL > 20)
                    _offGroundCounter++;
                else
                    _offGroundCounter = 0;

                if (_offGroundCounter >= AIRBORNE_CONFIRM_SAMPLES)
                {
                    _offGroundCounter = 0;
                    TransitionTo(FlightSessionState.Airborne, snap);
                }
                break;

            case FlightSessionState.Airborne:
                if (snap.Phase is FlightPhase.Approach or FlightPhase.FinalApproach or FlightPhase.Landing)
                    TransitionTo(FlightSessionState.OnApproach, snap);
                // Fallback: aircraft landed without approach phase ever being detected
                // (e.g. shallow VS approach, gear not reported, or spawned directly on final).
                else if (snap.IsOnGround && snap.Phase is (FlightPhase.Rollout or FlightPhase.Taxi
                                                         or FlightPhase.Vacating or FlightPhase.Parked))
                    TransitionTo(FlightSessionState.Landed, snap);
                break;

            case FlightSessionState.OnApproach:
                if (snap.IsOnGround && snap.Phase is (FlightPhase.Rollout or FlightPhase.Taxi
                                                   or FlightPhase.Vacating or FlightPhase.Parked))
                    TransitionTo(FlightSessionState.Landed, snap);
                else if (!snap.IsOnGround && snap.VerticalSpeedFPM > 500 && snap.AltitudeAGL > 500)
                    TransitionTo(FlightSessionState.Airborne, snap);
                break;

            case FlightSessionState.Landed:
                // MAJOR-01: Previously required parking brake to complete. Most MSFS pilots never set it.
                // Now also complete when engines are off and aircraft has stopped.
                if ((snap.ParkingBrakeSet && snap.GroundSpeedKts < 1) ||
                    (!snap.EnginesRunning && snap.GroundSpeedKts < 1))
                    TransitionTo(FlightSessionState.Complete, snap);
                break;
        }
    }

    private void TransitionTo(FlightSessionState newState, TelemetrySnapshot snap)
    {
        var prev = State;
        State = newState;

        Log.Information("[FlightSession] State: {Prev} → {New}", prev, newState);

        switch (newState)
        {
            case FlightSessionState.PreFlight:
                OnEnginesStarted(snap);
                break;

            case FlightSessionState.Taxiing:
                OnTaxiStarted(snap);
                break;

            case FlightSessionState.Airborne:
                if (!_hasRecordedTakeoff)
                    OnTakeoff(snap);
                break;

            case FlightSessionState.Complete:
                OnFlightComplete(snap);
                break;
        }

        SessionStateChanged?.Invoke(this, newState);
    }

    // =====================================================
    // FLIGHT EVENTS
    // =====================================================

    private void OnEnginesStarted(TelemetrySnapshot snap)
    {
        Log.Information("[FlightSession] Engines started, creating flight record");
        
        CurrentFlight = new FlightRecord
        {
            FlightNumber = "",   // populated later via AircraftIdentReceived
            Callsign = "",        // populated later via AircraftIdentReceived
            AircraftTitle = "",
            FuelDepartureLbs = snap.Raw.FuelTotalLbs,
            Status = FlightStatus.InProgress
        };
        
        _fuelAtDeparture = snap.Raw.FuelTotalLbs;
        _flightRepo.SetCurrentFlight(CurrentFlight);
        _ = _flightRepo.SaveAsync(CurrentFlight);
    }

    private void OnTaxiStarted(TelemetrySnapshot snap)
    {
        _blockOutTime = snap.Timestamp;
        if (CurrentFlight != null)
            CurrentFlight.BlockOutTime = _blockOutTime;
    }

    private void OnTakeoff(TelemetrySnapshot snap)
    {
        _hasRecordedTakeoff = true;
        if (CurrentFlight != null)
        {
            CurrentFlight.TakeoffTime = snap.Timestamp;
            Log.Information("[FlightSession] TAKEOFF recorded at {Time:HH:mm:ss}Z", snap.Timestamp);
        }

        // Clear telemetry history to start fresh from takeoff
        _telemetry.ClearHistory();
        _routeTracker.StartRecording();
    }

    private void OnLandingDetected(object? sender, LandingResult landing)
    {
        _hasRecordedLanding = true;

        if (CurrentFlight != null)
        {
            landing.FlightId = CurrentFlight.Id.ToString();
            CurrentFlight.LandingTime = landing.Timestamp;
            CurrentFlight.AllLandings.Add(landing);
            if (CurrentFlight.PrimaryLanding == null)
                CurrentFlight.PrimaryLanding = landing;
        }

        _ = _landingRepo.SaveAsync(landing);
        _alertService.ShowLandingResult(landing);

        Log.Information("[FlightSession] LANDING: Score={Score} VS={VS:F0}fpm", 
            landing.LandingScore, landing.VerticalSpeedFPM);
    }

    private void OnBounceDetected(object? sender, BounceEvent bounce)
    {
        _alertService.ShowAlert($"BOUNCE #{bounce.BounceNumber} DETECTED", 
            AlertLevel.Warning, TimeSpan.FromSeconds(5));
    }

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEvent evt)
    {
        Log.Debug("[FlightSession] Phase: {Prev} → {Current}", evt.Previous, evt.Current);
    }

    private void OnFlightComplete(TelemetrySnapshot snap)
    {
        if (CurrentFlight == null) return;

        CurrentFlight.BlockInTime = snap.Timestamp;
        CurrentFlight.FuelArrivalLbs = snap.Raw.FuelTotalLbs;
        CurrentFlight.FuelUsedLbs = _fuelAtDeparture - snap.Raw.FuelTotalLbs;
        CurrentFlight.MaxAltitudeFt = _fuelAnalyzer.MaxAltitude;
        CurrentFlight.FlightPath = _routeTracker.GetRecordedPath();
        CurrentFlight.ActualDistanceNm = _routeTracker.TotalDistanceNm;
        CurrentFlight.Status = FlightStatus.Completed;

        _ = _flightRepo.UpdateAsync(CurrentFlight);

        Log.Information("[FlightSession] FLIGHT COMPLETE: {Dep}→{Arr} | Block: {Block} | Score: {Score}",
            CurrentFlight.DepartureICAO,
            CurrentFlight.ArrivalICAO,
            CurrentFlight.BlockTime,
            CurrentFlight.PrimaryLanding?.LandingScore ?? 0);

        FlightCompleted?.Invoke(this, CurrentFlight);

        // Capture reference before Reset() nulls CurrentFlight, then submit PIREP async
        var completedFlight = CurrentFlight;
        Reset();
        _ = SubmitPirepSafeAsync(completedFlight);
    }

    private async Task SubmitPirepSafeAsync(FlightRecord flight)
    {
        var key = _settings.Settings.AcarsKey.Trim();
        if (string.IsNullOrEmpty(key))
        {
            Log.Debug("[FlightSession] PIREP skipped — no ACARS key configured");
            return;
        }

        var success = await _backend.SubmitPirepAsync(flight, key);
        if (success)
        {
            flight.SyncedToBackend = true;
            await _flightRepo.UpdateAsync(flight);
            Log.Information("[FlightSession] PIREP synced to backend for flight {Id}", flight.Id);
        }
        else
        {
            Log.Warning("[FlightSession] PIREP submission failed — will retry on next startup (flight {Id})", flight.Id);
        }
    }

    // =====================================================
    // APPROACH ALERTS
    // =====================================================

    private void CheckApproachAlerts(TelemetrySnapshot snap)
    {
        if (snap.ApproachAlerts.Count > 0)
        {
            foreach (var alert in snap.ApproachAlerts)
            {
                _alertService.ShowAlert($"UNSTABLE: {alert}", AlertLevel.Warning, TimeSpan.FromSeconds(3));
            }
        }
    }

    // =====================================================
    // PATH RECORDING
    // =====================================================

    private void RecordPathPoint(TelemetrySnapshot snap)
    {
        var point = new PathPoint
        {
            Latitude = snap.Latitude,
            Longitude = snap.Longitude,
            AltitudeMSL = snap.AltitudePressure,
            GroundSpeed = snap.GroundSpeedKts,
            VerticalSpeed = snap.VerticalSpeedFPM,
            Phase = snap.Phase,
            Timestamp = snap.Timestamp,
            Heading = (float)snap.Raw.HeadingTrue
        };

        _routeTracker.AddPoint(point);
        // MAJOR-02: FlightPath was receiving pre-takeoff (taxi/preflight) points because RecordPathPoint
        // was called from OnTelemetryReceived unconditionally. Only add to FlightPath after wheels-up
        // so FlightRecord.FlightPath and RouteTracker stay consistent.
        if (_hasRecordedTakeoff)
            CurrentFlight?.FlightPath.Add(point);
    }

    // =====================================================
    // SIMBRIEF PLAN ASSIGNMENT
    // =====================================================

    public void AssignSimBriefPlan(SimBriefFlightPlan plan)
    {
        if (CurrentFlight == null) return;
        CurrentFlight.PlannedFlight = plan;
        CurrentFlight.DepartureICAO = plan.DepartureICAO;
        CurrentFlight.ArrivalICAO = plan.ArrivalICAO;
        CurrentFlight.PlannedRoute = plan.Route;
        CurrentFlight.PlannedDistanceNm = plan.PlannedDistanceNm;
        CurrentFlight.AircraftType = plan.AircraftType;

        _routeTracker.SetPlannedRoute(plan.Waypoints);
        Log.Information("[FlightSession] SimBrief plan assigned: {Dep}→{Arr}", plan.DepartureICAO, plan.ArrivalICAO);
    }

    // =====================================================
    // MANUAL FLIGHT END
    // Called when the pilot clicks "End Flight" in the ACARS page.
    // Behaves identically to the automatic completion path.
    // =====================================================

    public void EndFlightManually()
    {
        if (CurrentFlight == null) return;
        if (State is FlightSessionState.Idle or FlightSessionState.Complete) return;

        if (LatestTelemetry is { } snap)
        {
            TransitionTo(FlightSessionState.Complete, snap);
        }
        else
        {
            // No live telemetry — finalize with minimal data
            CurrentFlight.Status = FlightStatus.Completed;
            CurrentFlight.BlockInTime = DateTime.UtcNow;
            CurrentFlight.FlightPath = _routeTracker.GetRecordedPath();
            CurrentFlight.ActualDistanceNm = _routeTracker.TotalDistanceNm;
            _ = _flightRepo.UpdateAsync(CurrentFlight);
            FlightCompleted?.Invoke(this, CurrentFlight);
            SessionStateChanged?.Invoke(this, FlightSessionState.Complete);
            Reset();
        }

        Log.Information("[FlightSession] Flight ended manually by pilot");
    }

    // =====================================================
    // RESET
    // =====================================================

    public void Reset()
    {
        CurrentFlight = null;
        State = FlightSessionState.Idle;
        _hasRecordedTakeoff = false;
        _hasRecordedLanding = false;
        _pathSampleCounter = 0;
        _offGroundCounter = 0;
        _landingAnalyzer.Reset();
        _phaseDetector.Reset();
        _fuelAnalyzer.Reset();
        _routeTracker.Reset();
        _flightRepo.SetCurrentFlight(null);
    }
}

public enum FlightSessionState
{
    Idle,
    PreFlight,
    Taxiing,
    Airborne,
    OnApproach,
    Landed,
    Complete
}
