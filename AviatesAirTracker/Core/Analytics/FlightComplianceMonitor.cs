using AviatesAirTracker.Core.SimConnect;

namespace AviatesAirTracker.Core.Analytics;

// ============================================================
// FLIGHT COMPLIANCE MONITOR
//
// The flight-wide half of the "held for review" feature (the landing-specific half —
// flaps/gear/speed by 1000ft, centerline, touchdown zone, hard landing, bounce — lives in
// LandingComplianceChecker). Runs at 20Hz for the whole flight via
// FlightSessionManager.OnTelemetryReceived, tracking peak values rather than reacting to any
// single sample, then produces the violation list once at flight completion.
//
// Tracked separately from LandingComplianceChecker because these checks can trip at any point
// in the flight, not just the last few minutes of approach — TelemetryProcessor's own history
// buffer only covers the last 30 minutes, which isn't enough for a long-haul flight, so this
// has to accumulate live instead of reading back from history after the fact.
//
// Cruise speed bounds are a deliberately simple, fleet-category-aware safety net (Turboprop vs.
// everything else), not real per-type Vmo/stall-speed data — there's no such table anywhere in
// this project yet (AircraftPerformanceProfiles only has V1/Vr/V2/Vref), and building one is a
// bigger undertaking than this pass's "not too advanced but advanced" scope.
// ============================================================

public class FlightComplianceMonitor
{
    private const double MaxVerticalSpeedFpm = 8000.0;

    private double _maxAbsVerticalSpeed;
    private double _minCruiseIasKts = double.MaxValue;
    private double _maxCruiseIasKts;

    public void Reset()
    {
        _maxAbsVerticalSpeed = 0;
        _minCruiseIasKts = double.MaxValue;
        _maxCruiseIasKts = 0;
    }

    public void Process(TelemetrySnapshot snap)
    {
        if (!snap.IsOnGround)
        {
            var absVs = Math.Abs(snap.VerticalSpeedFPM);
            if (absVs > _maxAbsVerticalSpeed) _maxAbsVerticalSpeed = absVs;
        }

        if (snap.Phase == FlightPhase.Cruise)
        {
            if (snap.IASKts < _minCruiseIasKts) _minCruiseIasKts = snap.IASKts;
            if (snap.IASKts > _maxCruiseIasKts) _maxCruiseIasKts = snap.IASKts;
        }
    }

    /// <summary>Call once at flight completion. <paramref name="aircraftCategory"/> is
    /// EfbAircraftProfile.Category ("Boeing"/"Airbus"/"Turboprop"/null) for the flown type.</summary>
    public List<string> Finalize(string? aircraftCategory)
    {
        var violations = new List<string>();

        if (_maxAbsVerticalSpeed > MaxVerticalSpeedFpm)
        {
            violations.Add(
                $"Excessive climb/descent rate observed during the flight: {_maxAbsVerticalSpeed:F0} fpm (limit {MaxVerticalSpeedFpm:F0} fpm).");
        }

        if (_maxCruiseIasKts > 0)
        {
            var (lowKts, highKts) = CruiseBounds(aircraftCategory);
            if (_minCruiseIasKts < double.MaxValue && _minCruiseIasKts < lowKts)
                violations.Add($"Dangerously low airspeed observed at cruise: {_minCruiseIasKts:F0} kt.");
            if (_maxCruiseIasKts > highKts)
                violations.Add($"Dangerously high airspeed observed at cruise: {_maxCruiseIasKts:F0} kt.");
        }

        return violations;
    }

    private static (double Low, double High) CruiseBounds(string? category) => category switch
    {
        "Turboprop" => (150.0, 300.0),
        _ => (250.0, 530.0), // Boeing/Airbus jets, and the unknown-type fallback
    };
}
