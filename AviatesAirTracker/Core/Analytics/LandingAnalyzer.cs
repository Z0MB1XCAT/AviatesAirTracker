using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Models;
using Serilog;

namespace AviatesAirTracker.Core.Analytics;

// ============================================================
// LANDING ANALYZER
//
// Advanced landing detection and scoring system.
// Captures touchdown parameters and generates a score.
//
// Detection algorithm:
//   1. Monitor approach phase for stability
//   2. Detect flare (pitch increasing, VS reducing)
//   3. Detect touchdown (SimOnGround transitions false→true)
//   4. Capture all touchdown parameters
//   5. Score the landing
//   6. Detect bounce (air→ground→air within 3 seconds)
//   7. Monitor rollout and deceleration
// ============================================================

public class LandingAnalyzer
{
    // =====================================================
    // EVENTS
    // =====================================================
    public event EventHandler<LandingResult>? LandingDetected;
    public event EventHandler<BounceEvent>? BounceDetected;
    public event EventHandler<FlareEvent>? FlareDetected;

    // =====================================================
    // APPROACH MONITORING STATE
    // =====================================================
    private bool _inApproach;
    private DateTime _approachStartTime;
    private bool _stableApproach;
    private List<TelemetrySnapshot> _approachHistory = [];

    // =====================================================
    // TOUCHDOWN STATE MACHINE
    // =====================================================
    private bool _previouslyOnGround;
    private bool _hasBeenAirborne;   // Guard: prevent spurious touchdown on first ground tick at startup
    private DateTime _touchdownTime;
    private DateTime _airborneTime;
    private bool _flareDetected;
    private int _bounceCount;

    // Pre-touchdown samples for capture
    private readonly Queue<TelemetrySnapshot> _preTouchdownBuffer = new();
    private const int PRE_TOUCHDOWN_SAMPLES = 40; // 2 seconds before @ 20Hz

    // Post-touchdown samples for rollout analysis
    private readonly List<TelemetrySnapshot> _rolloutSamples = [];
    private bool _capturingRollout;
    private double _touchdownGroundSpeed;
    private double _touchdownAltitude;

    // Touchdown snapshot
    private TelemetrySnapshot? _touchdownSnapshot;

    // Runway info (populated by RunwayDetector)
    private RunwayInfo? _detectedRunway;

    // =====================================================
    // FLARE DETECTION
    // =====================================================
    private double _minVS_InApproach = double.MaxValue;  // CRIT-07: was 0, caused false flare detection during takeoff
    private bool _vsIncreasing = false;  // VS moving toward 0 = flare

    // =====================================================
    // THRESHOLDS
    // =====================================================
    private const double APPROACH_AGL_THRESHOLD = 5000.0;
    private const double FLARE_AGL_THRESHOLD = 100.0;
    private const double BOUNCE_AIRBORNE_THRESHOLD = 3.0;   // Seconds in air after touchdown = bounce
    private const double ROLLOUT_END_SPEED = 30.0;           // Below this = vacating

    public void SetRunwayInfo(RunwayInfo? runway) => _detectedRunway = runway;

    // =====================================================
    // MAIN PROCESSING — called at 20Hz
    // =====================================================

    public void Process(TelemetrySnapshot snap)
    {
        // Maintain pre-touchdown buffer
        _preTouchdownBuffer.Enqueue(snap);
        if (_preTouchdownBuffer.Count > PRE_TOUCHDOWN_SAMPLES)
            _preTouchdownBuffer.Dequeue();

        // Approach monitoring
        if (snap.Phase == FlightPhase.Approach || 
            snap.Phase == FlightPhase.FinalApproach)
        {
            MonitorApproach(snap);
        }

        // Flare detection
        if (snap.AltitudeAGL < FLARE_AGL_THRESHOLD && !snap.IsOnGround)
        {
            DetectFlare(snap);
        }

        // Touchdown detection (ground state transition)
        bool currentlyOnGround = snap.IsOnGround;
        if (!currentlyOnGround)
            _hasBeenAirborne = true;

        if (currentlyOnGround && !_previouslyOnGround && _hasBeenAirborne)
        {
            HandleTouchdown(snap);
        }
        else if (!currentlyOnGround && _previouslyOnGround)
        {
            HandleBecomingAirborne(snap);
        }

        // Rollout capture
        if (_capturingRollout && currentlyOnGround)
        {
            _rolloutSamples.Add(snap);

            // End rollout capture when slowed sufficiently
            if (snap.GroundSpeedKts < ROLLOUT_END_SPEED)
            {
                FinalizeLanding();
            }
        }

        _previouslyOnGround = currentlyOnGround;
    }

    // =====================================================
    // APPROACH STABILITY MONITORING
    // =====================================================

    private void MonitorApproach(TelemetrySnapshot snap)
    {
        if (!_inApproach)
        {
            _inApproach = true;
            _approachStartTime = DateTime.UtcNow;
            _approachHistory.Clear();
            _minVS_InApproach = snap.VerticalSpeedFPM;
        }

        _approachHistory.Add(snap);

        // Track minimum (most negative) VS
        if (snap.VerticalSpeedFPM < _minVS_InApproach)
            _minVS_InApproach = snap.VerticalSpeedFPM;

        // Check for stabilized approach
        CheckApproachStability(snap);
    }

    private void CheckApproachStability(TelemetrySnapshot snap)
    {
        // Stabilization criteria (standard airline: by 1000ft AAL)
        // MINOR-11: was 100-180 kts, excluding all GA aircraft (Cessna at 65kts). Broadened to cover GA.
        bool speedOk = snap.IASKts >= 40 && snap.IASKts <= 200;
        bool vsOk = snap.VerticalSpeedFPM >= -1500 && snap.VerticalSpeedFPM <= -100;
        // MINOR-11 / CRIT-06: Fixed-gear aircraft always have GearPosition=0; check actual positions too
        bool configOk = snap.GearDown || (snap.Raw.GearLeftTouchdown > 50 && snap.Raw.GearRightTouchdown > 50);
        bool bankOk = Math.Abs(snap.Raw.Bank) <= 5.0;

        _stableApproach = speedOk && vsOk && configOk && bankOk;
        snap.ApproachStable = _stableApproach;

        // Build alerts
        snap.ApproachAlerts.Clear();
        if (!speedOk) snap.ApproachAlerts.Add($"SPEED {snap.IASKts:F0}kt");
        if (!vsOk) snap.ApproachAlerts.Add($"VS {snap.VerticalSpeedFPM:F0}fpm");
        if (!configOk) snap.ApproachAlerts.Add("GEAR UP");
        if (!bankOk) snap.ApproachAlerts.Add($"BANK {snap.Raw.Bank:F1}°");
    }

    // =====================================================
    // FLARE DETECTION
    // =====================================================

    private void DetectFlare(TelemetrySnapshot snap)
    {
        if (_flareDetected) return;

        // Flare = VS becoming less negative (reducing from peak)
        bool vsImproving = snap.VerticalSpeedFPM > _minVS_InApproach + 100;

        if (vsImproving && snap.AltitudeAGL < FLARE_AGL_THRESHOLD)
        {
            _flareDetected = true;
            Log.Information("[LandingAnalyzer] FLARE detected at {AGL:F0}ft AGL, VS={VS:F0}fpm, Pitch={Pitch:F1}°",
                snap.AltitudeAGL, snap.VerticalSpeedFPM, snap.Raw.Pitch);

            FlareDetected?.Invoke(this, new FlareEvent
            {
                AGL = snap.AltitudeAGL,
                VerticalSpeedFPM = snap.VerticalSpeedFPM,
                PitchDegrees = snap.Raw.Pitch,
                IASKts = snap.IASKts,
                Timestamp = snap.Timestamp
            });
        }
    }

    // =====================================================
    // TOUCHDOWN HANDLING
    // =====================================================

    private void HandleTouchdown(TelemetrySnapshot snap)
    {
        // Check if this is a bounce (very short air time)
        double airTime = (snap.Timestamp - _airborneTime).TotalSeconds;
        bool isBounce = _touchdownSnapshot != null && airTime < BOUNCE_AIRBORNE_THRESHOLD;

        if (isBounce)
        {
            _bounceCount++;
            Log.Warning("[LandingAnalyzer] BOUNCE #{Count} detected! Air time: {AirTime:F1}s", 
                _bounceCount, airTime);
            
            BounceDetected?.Invoke(this, new BounceEvent
            {
                BounceNumber = _bounceCount,
                AirTimeSec = airTime,
                VerticalSpeedFPM = snap.VerticalSpeedFPM,
                GroundSpeed = snap.GroundSpeedKts
            });
        }
        else
        {
            // Primary touchdown
            _bounceCount = 0;
            _touchdownSnapshot = snap;
            _touchdownTime = snap.Timestamp;
            _touchdownGroundSpeed = snap.GroundSpeedKts;

            // Compute wind components at touchdown
            var (headwind, crosswind) = TelemetryProcessor.ComputeWindComponents(
                snap.Raw.HeadingTrue,
                snap.Raw.WindDirection,
                snap.Raw.WindSpeed);

            Log.Information("[LandingAnalyzer] TOUCHDOWN: VS={VS:F0}fpm | IAS={IAS:F0}kt | Pitch={Pitch:F1}° | Bank={Bank:F1}° | HW={HW:F0}kt | XW={XW:F0}kt",
                snap.VerticalSpeedFPM,
                snap.IASKts,
                snap.Raw.Pitch,
                snap.Raw.Bank,
                headwind,
                crosswind);
        }

        // Start rollout capture
        _rolloutSamples.Clear();
        _capturingRollout = true;
    }

    private void HandleBecomingAirborne(TelemetrySnapshot snap)
    {
        _airborneTime = snap.Timestamp;
    }

    // =====================================================
    // LANDING FINALIZATION AND SCORING
    // =====================================================

    private void FinalizeLanding()
    {
        if (_touchdownSnapshot == null)
        {
            _capturingRollout = false;
            return;
        }

        _capturingRollout = false;

        var tdSnap = _touchdownSnapshot;
        var (headwind, crosswind) = TelemetryProcessor.ComputeWindComponents(
            tdSnap.Raw.HeadingTrue,
            tdSnap.Raw.WindDirection,
            tdSnap.Raw.WindSpeed);

        // Calculate rollout distance and deceleration
        double rolloutDistance = CalculateRolloutDistance();
        double avgDeceleration = CalculateAverageDeceleration();

        // Calculate landing score
        var score = CalculateLandingScore(tdSnap, headwind, crosswind);

        // Calculate distance from threshold (if runway data available)
        double thresholdDistance = 0;
        if (_detectedRunway != null)
        {
            thresholdDistance = CalculateThresholdDistance(tdSnap, _detectedRunway);
        }

        var result = new LandingResult
        {
            Timestamp = tdSnap.Timestamp,

            // Core touchdown parameters
            VerticalSpeedFPM = tdSnap.VerticalSpeedFPM,
            GroundSpeedKts = tdSnap.GroundSpeedKts,
            IASKts = tdSnap.IASKts,
            TouchdownPitchDeg = tdSnap.Raw.Pitch,
            TouchdownBankDeg = tdSnap.Raw.Bank,
            TouchdownLatitude = tdSnap.Latitude,
            TouchdownLongitude = tdSnap.Longitude,

            // Wind
            HeadwindComponent = headwind,
            CrosswindComponent = crosswind,
            WindSpeedKts = tdSnap.Raw.WindSpeed,
            WindDirectionDeg = tdSnap.Raw.WindDirection,

            // Approach data
            ApproachWasStable = _stableApproach,
            FlareDetected = _flareDetected,
            BounceCount = _bounceCount,

            // Rollout
            RolloutDistanceFt = rolloutDistance,
            AverageDecelerationKtPerSec = avgDeceleration,

            // Runway
            RunwayIdentifier = _detectedRunway?.Identifier ?? "RWY??",
            AirportICAO = _detectedRunway?.AirportICAO ?? "????",
            ThresholdDistanceFt = thresholdDistance,

            // Score
            LandingScore = score.TotalScore,
            ScoreBreakdown = score,

            // Fuel
            FuelOnTouchdownLbs = tdSnap.Raw.FuelTotalLbs
        };

        Log.Information("[LandingAnalyzer] Landing complete. Score: {Score}/100 | VS: {VS:F0}fpm | GS: {GS:F0}kt",
            result.LandingScore, result.VerticalSpeedFPM, result.GroundSpeedKts);

        LandingDetected?.Invoke(this, result);

        // Reset for next landing
        ResetForNextLanding();
    }

    // =====================================================
    // LANDING SCORE ALGORITHM
    // Max 100 points
    // =====================================================

    private static LandingScoreBreakdown CalculateLandingScore(
        TelemetrySnapshot snap, double headwind, double crosswind)
    {
        var breakdown = new LandingScoreBreakdown();

        // ---- 1. VERTICAL SPEED (30 points) ----
        // Perfect: -100 to -250 fpm
        // Good: -251 to -400 fpm
        // Acceptable: -401 to -600 fpm
        // Hard: below -600 fpm
        double vs = Math.Abs(snap.VerticalSpeedFPM);
        breakdown.VerticalSpeedScore = vs switch
        {
            <= 250 => 30,
            <= 400 => 25,
            <= 600 => 15,
            <= 800 => 8,
            <= 1000 => 3,
            _ => 0
        };

        // ---- 2. PITCH ATTITUDE (20 points) ----
        // Perfect: 1.5° to 3.5° nose-up at touchdown
        // Acceptable: 0.5° to 5°
        // Bad: tail strike risk (<0° or >7°)
        double pitch = snap.Raw.Pitch;
        breakdown.PitchScore = pitch switch
        {
            >= 1.5 and <= 3.5 => 20,
            >= 0.5 and <= 5.0 => 15,
            >= 0.0 and <= 7.0 => 8,
            _ => 0
        };

        // ---- 3. BANK ANGLE (15 points) ----
        // Perfect: |bank| < 1°
        // Acceptable: < 3°
        // Warning: < 5°
        // Wingtip strike risk: > 5°
        double bank = Math.Abs(snap.Raw.Bank);
        breakdown.BankScore = bank switch
        {
            < 1.0 => 15,
            < 2.0 => 12,
            < 3.0 => 8,
            < 5.0 => 4,
            _ => 0
        };

        // ---- 4. SPEED MANAGEMENT (15 points) ----
        // Evaluate how close to Vref+5 (rough estimate: 130kt for jet, 60kt for GA)
        // Just score within a reasonable speed band
        double ias = snap.IASKts;
        breakdown.SpeedScore = ias switch
        {
            >= 100 and <= 145 => 15,  // typical jet approach
            >= 80 and <= 160 => 10,
            >= 60 and <= 180 => 5,
            _ => 0
        };

        // ---- 5. CROSSWIND HANDLING (10 points) ----
        // Small crosswind drift at landing = deduction
        double xw = Math.Abs(crosswind);
        breakdown.CrosswindScore = xw switch
        {
            < 2 => 10,
            < 5 => 8,
            < 10 => 6,
            < 15 => 3,
            _ => 1
        };

        // ---- 6. APPROACH STABILITY (10 points) ----
        breakdown.StabilityScore = snap.ApproachStable ? 10 : 2;

        // ---- TOTAL ----
        breakdown.TotalScore = (int)Math.Round(
            breakdown.VerticalSpeedScore +
            breakdown.PitchScore +
            breakdown.BankScore +
            breakdown.SpeedScore +
            breakdown.CrosswindScore +
            breakdown.StabilityScore);

        // Bounce penalty
        // (bounce count tracked separately, deduct from final)

        breakdown.TotalScore = Math.Clamp(breakdown.TotalScore, 0, 100);

        return breakdown;
    }

    // =====================================================
    // ROLLOUT ANALYSIS
    // =====================================================

    private double CalculateRolloutDistance()
    {
        if (_rolloutSamples.Count < 2) return 0;

        double totalDistance = 0;
        for (int i = 1; i < _rolloutSamples.Count; i++)
        {
            var p1 = _rolloutSamples[i - 1];
            var p2 = _rolloutSamples[i];
            totalDistance += HaversineDistanceFt(
                p1.Latitude, p1.Longitude,
                p2.Latitude, p2.Longitude);
        }
        return totalDistance;
    }

    private double CalculateAverageDeceleration()
    {
        if (_rolloutSamples.Count < 2) return 0;

        var first = _rolloutSamples.First();
        var last = _rolloutSamples.Last();
        double deltaV = first.GroundSpeedKts - last.GroundSpeedKts;
        double deltaT = (last.Timestamp - first.Timestamp).TotalSeconds;
        return deltaT > 0 ? deltaV / deltaT : 0;
    }

    private static double CalculateThresholdDistance(TelemetrySnapshot snap, RunwayInfo runway)
    {
        // Distance from touchdown point to runway threshold in feet
        return HaversineDistanceFt(
            snap.Latitude, snap.Longitude,
            runway.ThresholdLatitude, runway.ThresholdLongitude);
    }

    private static double HaversineDistanceFt(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 20925524.9; // Earth radius in feet
        double dLat = (lat2 - lat1) * Math.PI / 180;
        double dLon = (lon2 - lon1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    // =====================================================
    // RESET
    // =====================================================

    private void ResetForNextLanding()
    {
        _touchdownSnapshot = null;
        _flareDetected = false;
        _bounceCount = 0;
        _stableApproach = false;
        _minVS_InApproach = double.MaxValue;  // CRIT-07: was 0
        _airborneTime = default;               // MAJOR-05: was never reset, causing bounce mis-detection
        _approachHistory.Clear();
        _rolloutSamples.Clear();
        _inApproach = false;
    }

    public void Reset()
    {
        ResetForNextLanding();
        _preTouchdownBuffer.Clear();
        _previouslyOnGround = false;
        _hasBeenAirborne = false;
    }
}

// ============================================================
// EVENTS AND RESULT TYPES
// ============================================================

public class FlareEvent
{
    public double AGL { get; set; }
    public double VerticalSpeedFPM { get; set; }
    public double PitchDegrees { get; set; }
    public double IASKts { get; set; }
    public DateTime Timestamp { get; set; }
}

public class BounceEvent
{
    public int BounceNumber { get; set; }
    public double AirTimeSec { get; set; }
    public double VerticalSpeedFPM { get; set; }
    public double GroundSpeed { get; set; }
}
