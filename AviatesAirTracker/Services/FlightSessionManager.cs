using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Core.Efb;
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
    // Fired once, right after FlightCompleted, only when the completed flight tripped one or
    // more compliance checks (landing-level via LandingComplianceChecker, or flight-wide via
    // FlightComplianceMonitor/the late-departure check below). Never blocks PIREP submission --
    // ShellWindow subscribes to this purely to show the pilot the "held for review" dialog.
    public event EventHandler<FlightRecord>? FlightHeldForReview;

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
    private readonly BookingService _bookingService;
    private readonly FlightTrailService _flightTrail;
    private readonly FlightComplianceMonitor _complianceMonitor;
    private readonly EventReminderService _eventReminder;
    private readonly PirepSyncService _pirepSync;

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
        AviatesBackendClient backend,
        BookingService bookingService,
        FlightTrailService flightTrail,
        FlightComplianceMonitor complianceMonitor,
        EventReminderService eventReminder,
        PirepSyncService pirepSync)
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
        _bookingService = bookingService;
        _flightTrail = flightTrail;
        _complianceMonitor = complianceMonitor;
        _eventReminder = eventReminder;
        _pirepSync = pirepSync;

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
        {
            _landingAnalyzer.SetAircraftType(CurrentFlight?.AircraftType);
            _landingAnalyzer.Process(snap);
            _complianceMonitor.Process(snap);
        }

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
                // Leave Idle only when there is a flight to record: a resumed one, or an
                // activated booking. Leaving it for an untracked flight used to run the whole
                // state machine with no FlightRecord -- landings were scored and saved against no
                // flight, and OnFlightComplete then returned before Reset(), stranding State at
                // Complete so nothing more was tracked until the app was restarted. Re-checked
                // every tick, so activating the booking with the engines already running (on the
                // ground) starts tracking at that point instead of never.
                if (snap.EnginesRunning && CanStartTracking(snap))
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

    private bool _loggedUntracked;

    private bool CanStartTracking(TelemetrySnapshot snap)
    {
        if (CurrentFlight != null) return true;
        if (_bookingService.ActiveBooking is { IsActivated: true } && snap.IsOnGround)
        {
            _loggedUntracked = false;
            return true;
        }

        if (!_loggedUntracked)
        {
            _loggedUntracked = true;
            Log.Information("[FlightSession] Engines running with no activated booking -- not tracking");
        }
        return false;
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
        // Do not start a fresh record if a flight is already active (e.g. resumed from recovery)
        // -- checked first and unconditionally, so an already-legitimate in-progress flight is
        // never interrupted by the activation gate below regardless of its current state.
        if (CurrentFlight != null) return;

        // No tracking begins until the pilot has activated a booking (Pilot Hub's Activate
        // button) -- AcarsPage shows a matching "activate to access" gate so this is never a
        // surprise, but the real enforcement lives here: MSFS keeps running normally either way,
        // this app just never creates a flight record for it. Synchronous, no network call --
        // ActiveBooking is kept current by App.xaml.cs's startup hydration (survives a restart
        // between activating and flying) and by every place that creates/activates a booking, so
        // this is safe to read directly on the telemetry thread.
        if (_bookingService.ActiveBooking is not { IsActivated: true })
        {
            Log.Information("[FlightSession] Engines started with no activated booking -- not tracking");
            return;
        }

        Log.Information("[FlightSession] Engines started, creating flight record");

        CurrentFlight = new FlightRecord
        {
            FlightNumber = "",   // populated later via AircraftIdentReceived
            Callsign = "",        // populated later via AircraftIdentReceived
            AircraftTitle = "",
            FuelDepartureLbs = snap.Raw.FuelTotalLbs,
            Status = FlightStatus.InProgress
        };

        // Pre-populate from the active booking so the logbook shows the route number
        // (e.g. "VAV103") instead of a truncated GUID. AircraftIdentReceived is not yet
        // wired, so this is currently the sole source for FlightNumber and Callsign.
        if (_bookingService.ActiveBooking is { } booking)
        {
            if (!string.IsNullOrEmpty(booking.RouteFlightNumber))
                CurrentFlight.FlightNumber = booking.RouteFlightNumber;
            else if (!string.IsNullOrEmpty(booking.RouteCallsign))
                CurrentFlight.FlightNumber = booking.RouteCallsign;
            if (!string.IsNullOrEmpty(booking.Callsign))
                CurrentFlight.Callsign = booking.Callsign;
            if (!string.IsNullOrEmpty(booking.Registration))
                CurrentFlight.AircraftRegistration = booking.Registration;
            CurrentFlight.ScheduledDepUtc = booking.ScheduledDepUtc;

            // Also pre-populate route ICAOs from the booking (OriginIata/DestIata are named
            // "iata" but actually hold ICAO codes -- see route-iata-columns-hold-icao memory).
            // Without this, DepartureICAO/ArrivalICAO stayed empty until AssignSimBriefPlan ran,
            // which never happens for a pilot who starts engines before importing a SimBrief
            // plan -- the exact cause of Discord Rich Presence showing "????" for the whole
            // flight instead of the real route (DiscordPresenceService.BuildDetailsLine reads
            // these same fields). AssignSimBriefPlan still overwrites these later if a plan
            // does get loaded, so this is purely a fallback, not a conflicting source of truth.
            if (!string.IsNullOrEmpty(booking.OriginIata))
                CurrentFlight.DepartureICAO = booking.OriginIata;
            if (!string.IsNullOrEmpty(booking.DestIata))
                CurrentFlight.ArrivalICAO = booking.DestIata;
            if (!string.IsNullOrEmpty(booking.AircraftType))
                CurrentFlight.AircraftType = booking.AircraftType;
        }

        // Event hours multiplier: only armed if the pilot confirmed attendance at
        // EventReminderService's 10-minutes-before popup for a registered event, and only
        // consumed once -- a second flight later the same day starts with no multiplier again.
        // Falls back to the charter multiplier only if no event was armed -- a flight is never
        // both (a charter is booking-driven, an event flight is reminder-driven; checking event
        // first just resolves the near-impossible case where both happen to be set).
        var pendingEvent = _eventReminder.ConsumePendingEventFlight();
        if (pendingEvent != null)
        {
            CurrentFlight.EventId = pendingEvent.EventId;
            CurrentFlight.IsOfficialEvent = pendingEvent.IsOfficial;
            CurrentFlight.HoursMultiplier = pendingEvent.Multiplier;
            Log.Information("[FlightSession] Flight tagged for event {EventId} ({Multiplier}x hours)",
                pendingEvent.EventId, pendingEvent.Multiplier);
        }
        else if (_bookingService.IsActiveBookingCharter)
        {
            CurrentFlight.IsCharterFlight = true;
            CurrentFlight.HoursMultiplier = 0.5;
            Log.Information("[FlightSession] Flight tagged as charter (0.5x hours)");
        }

        _fuelAtDeparture = snap.Raw.FuelTotalLbs;
        _flightRepo.SetCurrentFlight(CurrentFlight);
        _ = _flightRepo.SaveAsync(CurrentFlight);
    }

    private void OnTaxiStarted(TelemetrySnapshot snap)
    {
        _blockOutTime = snap.Timestamp;
        // A flight resumed after an app restart already has its real block-out time; the
        // PreFlight → Taxiing pass on resume must not restart the block clock from now.
        if (CurrentFlight != null && CurrentFlight.BlockOutTime == default)
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

            // RunwayDetector only matches its small built-in database of major hubs, so most
            // landings leave LandingAnalyzer's AirportICAO blank. Backfill from the active
            // flight's arrival ICAO (known from the booking/SimBrief plan) so the airport shown
            // in Pilot Hub/landing history is the real one instead of a placeholder.
            if (string.IsNullOrWhiteSpace(landing.AirportICAO))
                landing.AirportICAO = CurrentFlight.ArrivalICAO;

            CurrentFlight.LandingTime = landing.Timestamp;
            CurrentFlight.AllLandings.Add(landing);
            if (CurrentFlight.PrimaryLanding == null)
                CurrentFlight.PrimaryLanding = landing;
        }

        // Persist FIRST — the landing must survive even if the live popup is
        // suppressed or missed; the EFB "Landing" app reads from this history.
        _ = _landingRepo.SaveAsync(landing);

        // MINOR: AppSettings.ShowLandingResult was defined but never honoured.
        if (_settings.Settings.ShowLandingResult)
            _alertService.ShowLandingResult(landing);

        Log.Information("[FlightSession] LANDING: Score={Score} VS={VS:F0}fpm", 
            landing.LandingScore, landing.VerticalSpeedFPM);
    }

    private void OnBounceDetected(object? sender, BounceEvent bounce)
    {
        if (!_settings.Settings.ShowApproachAlerts) return;
        _alertService.ShowAlert("Bounced landing", $"Bounce {bounce.BounceNumber} after touchdown.",
            AlertLevel.Warning, TimeSpan.FromSeconds(6));
    }

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEvent evt)
    {
        Log.Debug("[FlightSession] Phase: {Prev} → {Current}", evt.Previous, evt.Current);
    }

    private void OnFlightComplete(TelemetrySnapshot snap)
    {
        if (CurrentFlight == null)
        {
            // Nothing to finalize, but the state machine must still return to Idle --
            // UpdateSessionState has no way out of Complete.
            Reset();
            return;
        }

        // Force-finalize a landing that touched down but hadn't finished its rollout yet
        // (e.g. pilot ends the flight right after touchdown). Must happen before the flight
        // record is built/broadcast and before Reset() nulls CurrentFlight, otherwise
        // PrimaryLanding is dropped and the UI shows a blank landing rate/score.
        _landingAnalyzer.FinalizePendingLanding();

        CurrentFlight.BlockInTime = snap.Timestamp;
        CurrentFlight.FuelArrivalLbs = snap.Raw.FuelTotalLbs;
        CurrentFlight.FuelUsedLbs = _fuelAtDeparture - snap.Raw.FuelTotalLbs;
        CurrentFlight.MaxAltitudeFt = _fuelAnalyzer.MaxAltitude;
        CurrentFlight.FlightPath = _routeTracker.GetRecordedPath();
        CurrentFlight.ActualDistanceNm = _routeTracker.TotalDistanceNm;
        CurrentFlight.Status = FlightStatus.Completed;

        // Flight-wide compliance: cruise speed / excessive climb-descent rate (FlightComplianceMonitor,
        // tracked live all flight since TelemetryProcessor's own history buffer only covers the last
        // 30 minutes -- not enough for a long-haul flight) plus a late-departure check here, using the
        // booking snapshot captured BEFORE CompleteActiveBookingAsync() below clears it. Landing-level
        // violations (hard landing, flaps/gear/speed by 1000ft, centerline, touchdown zone, bounce)
        // were already evaluated per-landing by LandingComplianceChecker inside LandingAnalyzer and are
        // already sitting on each LandingResult.ComplianceViolations -- FlightRecord.RequiresReview
        // reads those directly, nothing further to merge here.
        var activeBookingForLateCheck = _bookingService.ActiveBooking;
        var profile = AircraftPerformanceProfiles.Find(CurrentFlight.AircraftType);
        var flightViolations = _complianceMonitor.Finalize(profile?.Category);
        if (activeBookingForLateCheck != null &&
            CurrentFlight.BlockOutTime > activeBookingForLateCheck.ScheduledDepUtc.AddMinutes(20))
        {
            var lateBy = CurrentFlight.BlockOutTime - activeBookingForLateCheck.ScheduledDepUtc;
            flightViolations.Add($"Departed {lateBy.TotalMinutes:F0} minutes after the scheduled departure time.");
        }
        CurrentFlight.FlightComplianceViolations = flightViolations;

        _ = _flightRepo.UpdateAsync(CurrentFlight);

        Log.Information("[FlightSession] FLIGHT COMPLETE: {Dep}→{Arr} | Block: {Block} | Score: {Score}",
            CurrentFlight.DepartureICAO,
            CurrentFlight.ArrivalICAO,
            CurrentFlight.BlockTime,
            CurrentFlight.PrimaryLanding?.LandingScore ?? 0);

        FlightCompleted?.Invoke(this, CurrentFlight);

        if (CurrentFlight.RequiresReview)
        {
            Log.Warning("[FlightSession] Flight held for review: {Reasons}",
                string.Join(" | ", CurrentFlight.FlightComplianceViolations
                    .Concat(CurrentFlight.AllLandings.SelectMany(l => l.ComplianceViolations))));
            FlightHeldForReview?.Invoke(this, CurrentFlight);
        }

        // Capture reference before Reset() nulls CurrentFlight, then submit PIREP async
        var completedFlight = CurrentFlight;
        Reset();
        _ = SubmitPirepSafeAsync(completedFlight);
        _ = _bookingService.CompleteActiveBookingAsync();
    }

    private async Task SubmitPirepSafeAsync(FlightRecord flight)
    {
        try
        {
            await _pirepSync.SubmitAsync(flight, announce: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[FlightSession] PIREP submission failed -- will retry on next startup (flight {Id})", flight.Id);
        }
    }

    // =====================================================
    // APPROACH ALERTS
    // =====================================================

    private void CheckApproachAlerts(TelemetrySnapshot snap)
    {
        if (!_settings.Settings.ShowApproachAlerts) return;
        // AlertService drops an identical alert while one is still showing, so calling this at
        // 20 Hz raises each one once per 6 s, not 20 times a second.
        foreach (var alert in snap.ApproachAlerts)
            _alertService.ShowAlert("Unstable approach", DescribeStabilityFlag(alert),
                AlertLevel.Warning, TimeSpan.FromSeconds(6));
    }

    private static string DescribeStabilityFlag(string flag) => flag switch
    {
        nameof(StabilityFlag.HighDescentRate)     => "Sink rate is above 1,000 fpm.",
        nameof(StabilityFlag.GearUp)              => "Landing gear is not down.",
        nameof(StabilityFlag.FlapsNotSet)         => "Flaps are not in the landing setting.",
        nameof(StabilityFlag.ExcessiveBank)       => "Bank angle is too steep.",
        nameof(StabilityFlag.LocalizerDeviation)  => "Off the localizer.",
        nameof(StabilityFlag.GlideSlopeDeviation) => "Off the glideslope.",
        nameof(StabilityFlag.Overspeed)           => "Speed is above the approach target.",
        nameof(StabilityFlag.Underspeed)          => "Speed is below the approach target.",
        _                                         => flag,
    };

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
        // Persistent, unbounded trail (independent of RouteTracker's per-flight lifecycle) --
        // gated internally on SimConnect being Connected, never cleared by Reset()/end-flight.
        _flightTrail.AddPoint(point);
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
            _landingAnalyzer.FinalizePendingLanding();
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

    /// <summary>Re-attach live tracking to an interrupted in-progress flight from a previous session.</summary>
    public void ResumeFlight(FlightRecord record)
    {
        if (record == null) return;
        CurrentFlight = record;
        _flightRepo.SetCurrentFlight(record);
        _routeTracker.Restore(record.FlightPath);
        _hasRecordedTakeoff = record.TakeoffTime != default;
        Serilog.Log.Information("[FlightSession] Resumed interrupted flight {Id} {Dep}->{Arr}",
            record.Id, record.DepartureICAO, record.ArrivalICAO);
    }

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
        _complianceMonitor.Reset();
        _flightRepo.SetCurrentFlight(null);
    }
}
