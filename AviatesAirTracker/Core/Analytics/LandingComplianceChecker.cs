using AviatesAirTracker.Core.Efb;
using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Models;

namespace AviatesAirTracker.Core.Analytics;

// ============================================================
// LANDING COMPLIANCE CHECKER
//
// The landing-specific half of the "held for review" feature (the flight-wide half —
// cruise speed, excessive climb/descent rate, late departure — lives in
// FlightComplianceMonitor). Evaluates one completed LandingResult against the same
// stabilized-approach criteria real airline SOPs use:
//   - Flaps in the landing configuration by 1000ft AGL
//   - Gear down by 1000ft AGL
//   - Approach speed stabilized (within a Vref-based band) by 1000ft AGL
//   - No bounce
//   - Touchdown within a reasonable distance of the runway centerline
//   - Touchdown within the normal touchdown zone (not a long/short landing)
//   - Vertical speed at touchdown under 800fpm
//
// TRAINING-GRADE, same caveat AircraftPerformanceProfiles/EfbPerformanceCalculator already
// carry: flap "configured" is a blunt FlapsPercent >= 70% threshold (SimConnect's own flap
// percent is already normalized 0-100% regardless of how many physical detents the type has,
// so this is a legitimate simplification, not a made-up number), and the approach-speed band
// reuses the exact Vref-scaling formula EfbPerformanceCalculator.CalculateLanding uses
// (Vref × √(weight/MLW) × flap factor) rather than a second, drifting copy of it — just without
// EfbPerformanceCalculator's wind/density-altitude corrections, since those aren't available at
// evaluation time.
//
// Never blocks the landing/PIREP itself — only decides whether ComplianceViolations ends up
// non-empty, which is what flags the flight for review.
// ============================================================

public static class LandingComplianceChecker
{
    private const double HardLandingVsFpm = 800.0;
    private const double StabilizationAglFt = 1000.0;
    private const double ApproachSpeedMarginKts = 15.0;   // stabilized window is [Vref, Vref+15]
    private const double CenterlineToleranceFt = 60.0;
    private const double TouchdownZoneMinFt = 200.0;
    private const double TouchdownZoneMaxFt = 3000.0;

    public static void Evaluate(
        LandingResult result,
        IReadOnlyList<TelemetrySnapshot> approachHistory,
        EfbAircraftProfile? profile,
        RunwayInfo? runway,
        string? aircraftType = null)
    {
        // "Landing configuration" is the company minimum landing flap for the type (CN-003:
        // 737 FLAPS 25, A320 family CONF 3), else the generic 70 % — a legal minimum-flap
        // landing must never be held for review.
        double landingFlapPercent = EfbCompanyPolicy.MinLandingFlapPercent(aircraftType ?? profile?.Code);

        var violations = new List<string>();

        // ---- Hard landing ----
        if (Math.Abs(result.VerticalSpeedFPM) > HardLandingVsFpm)
        {
            violations.Add(
                $"Hard landing: touchdown vertical speed was {Math.Abs(result.VerticalSpeedFPM):F0} fpm (limit {HardLandingVsFpm:F0} fpm).");
        }

        // ---- Bounce ----
        if (result.BounceCount > 0)
        {
            violations.Add(
                $"Bounced landing ({result.BounceCount} bounce{(result.BounceCount > 1 ? "s" : "")} detected).");
        }

        // ---- Flaps / gear / speed by 1000ft AGL ----
        // approachHistory is recorded in chronological (descending-altitude) order from
        // LandingAnalyzer.MonitorApproach, so the last sample still above 1000ft AGL is the
        // closest recorded moment to the actual 1000ft crossing.
        var at1000 = approachHistory
            .Where(s => s.AltitudeAGL <= StabilizationAglFt)
            .OrderByDescending(s => s.AltitudeAGL)
            .FirstOrDefault();

        if (at1000 != null)
        {
            result.FlapsConfiguredBy1000Ft = at1000.Raw.FlapsPercent >= landingFlapPercent;
            if (!result.FlapsConfiguredBy1000Ft)
            {
                violations.Add(
                    $"Flaps were not in the landing configuration by 1,000 ft AGL ({at1000.Raw.FlapsPercent:F0}% extended).");
            }

            result.GearDownBy1000Ft = at1000.GearDown;
            if (!result.GearDownBy1000Ft)
                violations.Add("Landing gear was not down by 1,000 ft AGL.");

            var landingFlap = profile?.LandingFlaps.Count > 0 ? profile.LandingFlaps[^1] : null;
            if (landingFlap != null && profile != null)
            {
                var weight = at1000.Raw.GrossWeight > 0 ? at1000.Raw.GrossWeight : profile.MlwLbs;
                var weightFactor = Math.Sqrt(Math.Max(weight, 1) / profile.MlwLbs);
                var vref = profile.VrefLandingRef * weightFactor * landingFlap.VSpeedFactor;
                // Where a company minimum landing flap exists, the band spans every permitted
                // detent: VREF at full flap … VREF at the minimum flap + 15.
                var minFlap = EfbCompanyPolicy.For(aircraftType ?? profile.Code) != null
                    ? EfbCompanyPolicy.LandingFlaps(profile)[0] : landingFlap;
                var minSpeed = vref;
                var maxSpeed = profile.VrefLandingRef * weightFactor * minFlap.VSpeedFactor + ApproachSpeedMarginKts;

                result.SpeedStabilizedBy1000Ft = at1000.IASKts >= minSpeed && at1000.IASKts <= maxSpeed;
                if (!result.SpeedStabilizedBy1000Ft)
                {
                    violations.Add(
                        $"Approach speed was not stabilized by 1,000 ft AGL ({at1000.IASKts:F0} kt; expected {minSpeed:F0}-{maxSpeed:F0} kt).");
                }
            }
        }
        // else: no approach-history sample ever recorded at/below 1000ft AGL (a very short or
        // steep approach) -- leave the three booleans at their default `true` rather than
        // penalizing for missing data.

        // ---- Centerline + touchdown zone (only when a runway was actually detected) ----
        if (runway != null)
        {
            result.CenterlineDeviationFt = SignedCenterlineOffsetFt(
                result.TouchdownLatitude, result.TouchdownLongitude, runway);

            if (Math.Abs(result.CenterlineDeviationFt) > CenterlineToleranceFt)
            {
                violations.Add(
                    $"Touchdown was {Math.Abs(result.CenterlineDeviationFt):F0} ft " +
                    $"{(result.CenterlineDeviationFt > 0 ? "right" : "left")} of the runway centerline.");
            }

            if (result.ThresholdDistanceFt < TouchdownZoneMinFt || result.ThresholdDistanceFt > TouchdownZoneMaxFt)
            {
                violations.Add(
                    $"Touchdown point was {result.ThresholdDistanceFt:F0} ft from the threshold, outside the normal touchdown zone.");
            }
        }

        result.ComplianceViolations = violations;
    }

    // Cross-track distance from the runway's extended centerline line, in feet. Positive = right
    // of centerline, negative = left — same sign convention TelemetryProcessor.
    // ComputeWindComponents already uses for crosswind ("positive = from the right").
    private static double SignedCenterlineOffsetFt(double lat, double lon, RunwayInfo runway)
    {
        var bearingToPoint = InitialBearingDeg(runway.ThresholdLatitude, runway.ThresholdLongitude, lat, lon);
        var distance = HaversineDistanceFt(runway.ThresholdLatitude, runway.ThresholdLongitude, lat, lon);
        var angleDiff = ((bearingToPoint - runway.HeadingTrue) + 540) % 360 - 180;
        return distance * Math.Sin(angleDiff * Math.PI / 180.0);
    }

    private static double InitialBearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = lat1 * Math.PI / 180;
        var phi2 = lat2 * Math.PI / 180;
        var dLambda = (lon2 - lon1) * Math.PI / 180;
        var y = Math.Sin(dLambda) * Math.Cos(phi2);
        var x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLambda);
        return (Math.Atan2(y, x) * 180.0 / Math.PI + 360) % 360;
    }

    // Same Haversine constant/formula LandingAnalyzer.HaversineDistanceFt already uses for
    // ThresholdDistanceFt -- duplicated here rather than made public there, since that method is
    // a private implementation detail of a different class.
    private static double HaversineDistanceFt(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 20925524.9; // Earth radius in feet
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }
}
