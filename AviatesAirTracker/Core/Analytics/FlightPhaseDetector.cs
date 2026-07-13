using AviatesAirTracker.Core.SimConnect;
using Serilog;

namespace AviatesAirTracker.Core.Analytics;

public class FlightPhaseDetector
{
    private FlightPhase _currentPhase = FlightPhase.Parked;
    private DateTime _phaseEnteredAt = DateTime.UtcNow;
    private double _takeoffAltitude = 0;
    private double _cruiseAltitudeSample = 0;
    private int _cruiseSamples = 0;
    private bool _hasLeftGround = false;
    // Consecutive samples with VS below DESCENT_VS_FPM. A momentary VS dip during climb-out
    // (AP altitude capture, windshear, level acceleration) must not flip a climb phase into
    // Descent/Approach — require a sustained descent before leaving the climb family.
    private int _sustainedDescentSamples = 0;
    private const int DESCENT_CONFIRM_SAMPLES = 40; // 40 × 50ms = 2s at 20Hz
    // MSFS 2024 sometimes reports SIM ON GROUND = 0 for seconds at a time during pushback/taxi.
    // _hasLeftGround must only arm after sustained, plausible flight — otherwise a gate flicker
    // classified the parked aircraft as "Landing" (seen in the 2026-07-06 logs).
    private int _airborneSamples = 0;
    private const int AIRBORNE_CONFIRM_SAMPLES = 40; // 2s at 20Hz
    private const double AIRBORNE_MIN_AGL_FT = 100.0;

    public event EventHandler<FlightPhaseChangedEvent>? PhaseChanged;

    public FlightPhase CurrentPhase => _currentPhase;

    private const double TAXI_SPEED_KTS = 5.0;
    private const double TAKEOFF_SPEED_KTS = 80.0;
    private const double INITIAL_CLIMB_AGL = 400.0;
    private const double CLIMB_AGL = 1500.0;
    private const double CRUISE_VS_BAND_FPM = 200.0;
    private const double DESCENT_VS_FPM = -300.0;
    private const double APPROACH_AGL = 5000.0;
    private const double FINAL_APP_AGL = 1500.0;
    private const double FLARE_AGL = 100.0;
    private const double APPROACH_MIN_IAS_KTS = 60.0;

    public FlightPhase Detect(TelemetrySnapshot current, TelemetrySnapshot? previous)
    {
        var phase = ClassifyPhase(current, previous);

        if (phase != _currentPhase)
        {
            var prev = _currentPhase;
            _currentPhase = phase;
            _phaseEnteredAt = DateTime.UtcNow;

            var evt = new FlightPhaseChangedEvent
            {
                Previous = prev,
                Current = phase,
                AltitudeMSL = current.AltitudeMSL,
                AltitudeAGL = current.AltitudeAGL,
                GroundSpeed = current.GroundSpeedKts,
                Timestamp = DateTime.UtcNow
            };

            PhaseChanged?.Invoke(this, evt);
            Log.Information("[PhaseDetector] {Prev} -> {Current} | Alt: {Alt:F0}ft AGL | GS: {GS:F0}kt",
                prev, phase, current.AltitudeAGL, current.GroundSpeedKts);
        }

        return _currentPhase;
    }

    private FlightPhase ClassifyPhase(TelemetrySnapshot s, TelemetrySnapshot? prev)
    {
        bool onGround = s.IsOnGround;
        double agl = s.AltitudeAGL;
        double msl = s.AltitudeMSL;
        double gs = s.GroundSpeedKts;
        double vs = s.VerticalSpeedFPM;
        double ias = s.IASKts;
        bool gearDown = s.GearDown;
        bool parked = s.IsParked || s.ParkingBrakeSet;

        if (onGround)
        {
            bool justTouchedDown = _hasLeftGround && prev is not null && !prev.IsOnGround;

            if (parked && gs < 2)
                return FlightPhase.Parked;

            // Some MSFS landings flip on-ground before we ever see an airborne sample
            // below the flare threshold. Emit Landing on the first air-to-ground tick.
            if (justTouchedDown)
                return FlightPhase.Landing;

            if (gs < TAXI_SPEED_KTS)
                return _currentPhase == FlightPhase.Rollout ? FlightPhase.Vacating :
                       _currentPhase == FlightPhase.Vacating ? FlightPhase.Vacating :
                       FlightPhase.Parked;

            if (gs < TAKEOFF_SPEED_KTS)
            {
                if (_currentPhase == FlightPhase.Rollout ||
                    _currentPhase == FlightPhase.Landing ||
                    _currentPhase == FlightPhase.FinalApproach ||
                    _currentPhase == FlightPhase.Approach ||
                    _currentPhase == FlightPhase.Descent ||
                    _currentPhase == FlightPhase.TopOfDescent)
                    return FlightPhase.Rollout;

                return FlightPhase.Taxi;
            }

            if (_currentPhase == FlightPhase.Landing ||
                _currentPhase == FlightPhase.Rollout ||
                _currentPhase == FlightPhase.FinalApproach ||
                _currentPhase == FlightPhase.Approach ||
                _currentPhase == FlightPhase.Descent ||
                _currentPhase == FlightPhase.TopOfDescent)
                return FlightPhase.Rollout;

            return FlightPhase.Takeoff;
        }

        // Off-ground sample. Only treat it as real flight once it's sustained and plausible —
        // a ground-flag flicker while parked/taxiing must not arm landing detection.
        bool plausiblyFlying = ias >= APPROACH_MIN_IAS_KTS || agl > AIRBORNE_MIN_AGL_FT;
        if (plausiblyFlying)
        {
            if (++_airborneSamples >= AIRBORNE_CONFIRM_SAMPLES)
                _hasLeftGround = true;
        }
        else if (!_hasLeftGround)
        {
            _airborneSamples = 0;
            // Slow/low and never flown: this is a ground-flag glitch at the gate or on a taxiway.
            // Hold the current phase instead of classifying a parked aircraft as airborne.
            return _currentPhase;
        }

        bool climbingOut = _currentPhase == FlightPhase.Takeoff ||
                           _currentPhase == FlightPhase.InitialClimb ||
                           _currentPhase == FlightPhase.Climb;

        // Track sustained descent — used to confirm a genuine climb→descent transition.
        if (vs < DESCENT_VS_FPM)
            _sustainedDescentSamples++;
        else
            _sustainedDescentSamples = 0;

        if (agl < FLARE_AGL)
        {
            if (!climbingOut && _hasLeftGround)
                return FlightPhase.Landing;
        }

        if (agl < INITIAL_CLIMB_AGL && vs > 100)
            return FlightPhase.Takeoff;

        if (agl < CLIMB_AGL && vs > 0)
            return FlightPhase.InitialClimb;

        // Approach phases are only reachable once the climb-out is over. Without this guard a
        // momentary VS dip during climb (AP capture, turbulence) below 5000ft AGL with gear
        // still down classified the departure as FinalApproach/Approach.
        if (agl < APPROACH_AGL && _hasLeftGround && ias >= APPROACH_MIN_IAS_KTS && !climbingOut)
        {
            bool gearActuallyDown = gearDown || (s.Raw.GearLeftTouchdown > 50 && s.Raw.GearRightTouchdown > 50);

            if (agl < FINAL_APP_AGL && gearActuallyDown)
                return FlightPhase.FinalApproach;

            if (vs < DESCENT_VS_FPM)
                return FlightPhase.Approach;

            if (agl < FINAL_APP_AGL && vs < 0)
                return FlightPhase.FinalApproach;
        }

        if (vs > CRUISE_VS_BAND_FPM)
            return FlightPhase.Climb;

        if (vs < DESCENT_VS_FPM)
        {
            // Leaving a climb phase for Descent requires the descent to be sustained (~2s),
            // otherwise a single turbulence/level-off sample bounces the phase around.
            if (climbingOut && _sustainedDescentSamples < DESCENT_CONFIRM_SAMPLES)
                return _currentPhase;

            if (_currentPhase == FlightPhase.Cruise)
                return FlightPhase.TopOfDescent;

            return FlightPhase.Descent;
        }

        if (agl >= CLIMB_AGL && Math.Abs(vs) < CRUISE_VS_BAND_FPM)
        {
            _cruiseAltitudeSample = msl;
            return FlightPhase.Cruise;
        }

        return _currentPhase;
    }

    public void Reset()
    {
        _currentPhase = FlightPhase.Parked;
        _takeoffAltitude = 0;
        _cruiseAltitudeSample = 0;
        _cruiseSamples = 0;
        _hasLeftGround = false;
        _sustainedDescentSamples = 0;
        _airborneSamples = 0;
        _phaseEnteredAt = DateTime.UtcNow;
        Log.Information("[PhaseDetector] State machine reset");
    }

    public TimeSpan TimeInCurrentPhase => DateTime.UtcNow - _phaseEnteredAt;
}

public class FlightPhaseChangedEvent
{
    public FlightPhase Previous { get; set; }
    public FlightPhase Current { get; set; }
    public double AltitudeMSL { get; set; }
    public double AltitudeAGL { get; set; }
    public double GroundSpeed { get; set; }
    public DateTime Timestamp { get; set; }
}
