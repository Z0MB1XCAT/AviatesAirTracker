namespace AviatesAirTracker.Core.Efb;

// ============================================================
// EFB PERFORMANCE CALCULATOR
//
// Simplified, training-grade takeoff & landing performance. Uses the
// reference data in AircraftPerformanceProfiles and applies textbook
// corrections for weight, flap, density altitude, wind and slope.
//
// ⚠ NOT certified data. For simulator airmanship practice only.
// The model:
//   • V-speeds scale with √(weight ratio) and a flap factor.
//   • Field length scales with (weight ratio)² × density × flap ×
//     wind × slope factors — the classic first-order approximations.
// ============================================================

// ── Requests ────────────────────────────────────────────────

public sealed class TakeoffPerfRequest
{
    public string AircraftCode { get; set; } = "";
    public double WeightLbs { get; set; }
    public string FlapSetting { get; set; } = "";   // label from profile.TakeoffFlaps
    public double RunwayLengthM { get; set; }
    public double RunwayElevationFt { get; set; }
    public double RunwayHeadingDeg { get; set; }
    public double WindDirDeg { get; set; }
    public double WindKts { get; set; }
    public double OatC { get; set; }
    public double QnhMb { get; set; } = 1013.25;
    public double RunwaySlopePct { get; set; }       // + uphill
    public bool WetRunway { get; set; }
}

public sealed class LandingPerfRequest
{
    public string AircraftCode { get; set; } = "";
    public double WeightLbs { get; set; }
    public string FlapSetting { get; set; } = "";   // label from profile.LandingFlaps
    public double RunwayLengthM { get; set; }
    public double RunwayElevationFt { get; set; }
    public double RunwayHeadingDeg { get; set; }
    public double WindDirDeg { get; set; }
    public double WindKts { get; set; }
    public double OatC { get; set; }
    public double QnhMb { get; set; } = 1013.25;
    public double RunwaySlopePct { get; set; }       // + uphill (helps stopping)
    public bool WetRunway { get; set; }
    public string Autobrake { get; set; } = "MED";   // LOW | MED | MAX
}

// ── Responses ───────────────────────────────────────────────

public sealed class TakeoffPerfResult
{
    public bool Ok { get; set; } = true;
    public string AircraftCode { get; set; } = "";
    public string AircraftName { get; set; } = "";
    public string FlapSetting { get; set; } = "";
    public int V1 { get; set; }
    public int Vr { get; set; }
    public int V2 { get; set; }
    public int TakeoffDistanceM { get; set; }        // TODR incl. margin
    public int RequiredRunwayM { get; set; }
    public double RunwayLengthM { get; set; }
    public double MarginM { get; set; }
    public double HeadwindKts { get; set; }
    public double CrosswindKts { get; set; }
    public double DensityAltitudeFt { get; set; }
    public double WeightLbs { get; set; }
    public string Status { get; set; } = "OK";       // OK | CAUTION | UNSAFE
    public List<string> Warnings { get; set; } = [];
}

public sealed class LandingPerfResult
{
    public bool Ok { get; set; } = true;
    public string AircraftCode { get; set; } = "";
    public string AircraftName { get; set; } = "";
    public string FlapSetting { get; set; } = "";
    public int Vref { get; set; }
    public int Vapp { get; set; }
    public int LandingDistanceM { get; set; }        // LDR incl. factor
    public double RunwayLengthM { get; set; }
    public double MarginM { get; set; }
    public double HeadwindKts { get; set; }
    public double CrosswindKts { get; set; }
    public double DensityAltitudeFt { get; set; }
    public double WeightLbs { get; set; }
    public string Autobrake { get; set; } = "MED";
    public string Status { get; set; } = "OK";
    public List<string> Warnings { get; set; } = [];
}

public static class EfbPerformanceCalculator
{
    public static TakeoffPerfResult CalculateTakeoff(TakeoffPerfRequest r)
    {
        var p = AircraftPerformanceProfiles.Find(r.AircraftCode);
        if (p == null)
            return new TakeoffPerfResult { Ok = false, Status = "UNSAFE", Warnings = { $"Unknown aircraft '{r.AircraftCode}'." } };

        double weight = r.WeightLbs > 0 ? r.WeightLbs : p.RefWeightLbs;
        var flap = ResolveFlap(p.TakeoffFlaps, r.FlapSetting);

        var (head, cross) = WindComponents(r.RunwayHeadingDeg, r.WindDirDeg, r.WindKts);
        double densAlt = DensityAltitudeFt(r.RunwayElevationFt, r.OatC, r.QnhMb);

        // V-speeds: √(weight ratio) × flap factor, plus a small density bump.
        double wf = Math.Sqrt(Math.Max(weight, 1) / p.RefWeightLbs);
        double densSpeedFactor = 1.0 + Math.Max(0, densAlt) / 100_000.0; // ~+1kt per 1000ft ≈ tiny
        double v2 = p.V2Ref * wf * flap.VSpeedFactor * densSpeedFactor;
        double vr = p.VrRef * wf * flap.VSpeedFactor * densSpeedFactor;
        double v1 = p.V1Ref * wf * flap.VSpeedFactor * densSpeedFactor;
        // keep the classic V1 ≤ VR ≤ V2 ordering
        vr = Math.Max(vr, v1);
        v2 = Math.Max(v2, vr);

        // Field length: (weight ratio)² × density × flap × wind × slope.
        double weightFactor = Math.Pow(weight / p.MtowLbs, 2.0);
        double densFactor = 1.0 + Math.Max(0, densAlt) / 26_000.0; // ~+4% per 1000ft DA
        double windFactor = WindDistanceFactor(head);
        double slopeFactor = 1.0 + r.RunwaySlopePct * 0.03;        // ~+3% per 1% uphill
        double wetFactor = r.WetRunway ? 1.15 : 1.0;

        double todr = p.TodrRefM * weightFactor * densFactor * flap.DistanceFactor
                      * windFactor * slopeFactor * wetFactor;
        // 15% regulatory-style margin on the raw ground roll estimate.
        double required = todr * 1.15;

        var res = new TakeoffPerfResult
        {
            AircraftCode = p.Code,
            AircraftName = p.Name,
            FlapSetting = flap.Label,
            V1 = (int)Math.Round(v1),
            Vr = (int)Math.Round(vr),
            V2 = (int)Math.Round(v2),
            TakeoffDistanceM = (int)Math.Round(todr),
            RequiredRunwayM = (int)Math.Round(required),
            RunwayLengthM = r.RunwayLengthM,
            HeadwindKts = Math.Round(head),
            CrosswindKts = Math.Round(cross),
            DensityAltitudeFt = Math.Round(densAlt),
            WeightLbs = weight,
        };
        res.MarginM = r.RunwayLengthM > 0 ? Math.Round(r.RunwayLengthM - required) : 0;

        // Warnings & status
        if (weight > p.MtowLbs)
            res.Warnings.Add($"Weight {weight:N0} lb exceeds MTOW {p.MtowLbs:N0} lb.");
        if (head < 0)
            res.Warnings.Add($"Tailwind component {-head:F0} kt — increases takeoff distance.");
        if (Math.Abs(cross) > 25)
            res.Warnings.Add($"Crosswind {Math.Abs(cross):F0} kt is high for most types.");
        if (r.RunwayLengthM > 0 && required > r.RunwayLengthM)
            res.Warnings.Add($"Required {required:N0} m exceeds runway {r.RunwayLengthM:N0} m.");
        if (densAlt > 6000)
            res.Warnings.Add($"High density altitude {densAlt:N0} ft — degraded performance.");

        res.Status = Status(res.Warnings, r.RunwayLengthM > 0 && required > r.RunwayLengthM, weight > p.MtowLbs);
        return res;
    }

    public static LandingPerfResult CalculateLanding(LandingPerfRequest r)
    {
        var p = AircraftPerformanceProfiles.Find(r.AircraftCode);
        if (p == null)
            return new LandingPerfResult { Ok = false, Status = "UNSAFE", Warnings = { $"Unknown aircraft '{r.AircraftCode}'." } };

        double weight = r.WeightLbs > 0 ? r.WeightLbs : p.MlwLbs;
        var flap = ResolveFlap(p.LandingFlaps, r.FlapSetting);

        var (head, cross) = WindComponents(r.RunwayHeadingDeg, r.WindDirDeg, r.WindKts);
        double densAlt = DensityAltitudeFt(r.RunwayElevationFt, r.OatC, r.QnhMb);

        // Vref scales with √(weight/MLW); Vapp adds a wind additive (half headwind,
        // clamped 5–15 kt) as per common airline SOP.
        double wf = Math.Sqrt(Math.Max(weight, 1) / p.MlwLbs);
        double vref = p.VrefLandingRef * wf * flap.VSpeedFactor;
        double windAdditive = Math.Clamp(head > 0 ? head / 2.0 : 0, 5, 15);
        double vapp = vref + windAdditive;

        // Landing distance: (weight/MLW) × density × flap × wind × autobrake × surface.
        double weightFactor = weight / p.MlwLbs;
        double densFactor = 1.0 + Math.Max(0, densAlt) / 30_000.0;
        double windFactor = WindDistanceFactor(head);
        double abFactor = r.Autobrake.ToUpperInvariant() switch
        {
            "MAX" => 0.82,
            "LOW" => 1.20,
            _ => 1.00, // MED
        };
        double slopeFactor = 1.0 - r.RunwaySlopePct * 0.02; // uphill helps stopping
        double wetFactor = r.WetRunway ? 1.4 : 1.0;

        double ldr = p.LdrRefM * weightFactor * densFactor * flap.DistanceFactor
                     * windFactor * abFactor * slopeFactor;
        // Dispatch factor: 1.67 dry / 1.92 wet (classic 60%/67% field-length rules).
        double factored = ldr * (r.WetRunway ? 1.92 : 1.67) * (r.WetRunway ? wetFactor / 1.4 : 1.0);

        var res = new LandingPerfResult
        {
            AircraftCode = p.Code,
            AircraftName = p.Name,
            FlapSetting = flap.Label,
            Vref = (int)Math.Round(vref),
            Vapp = (int)Math.Round(vapp),
            LandingDistanceM = (int)Math.Round(factored),
            RunwayLengthM = r.RunwayLengthM,
            HeadwindKts = Math.Round(head),
            CrosswindKts = Math.Round(cross),
            DensityAltitudeFt = Math.Round(densAlt),
            WeightLbs = weight,
            Autobrake = r.Autobrake.ToUpperInvariant(),
        };
        res.MarginM = r.RunwayLengthM > 0 ? Math.Round(r.RunwayLengthM - factored) : 0;

        if (weight > p.MlwLbs)
            res.Warnings.Add($"Weight {weight:N0} lb exceeds MLW {p.MlwLbs:N0} lb — overweight landing.");
        if (head < 0)
            res.Warnings.Add($"Tailwind component {-head:F0} kt — increases landing distance.");
        if (Math.Abs(cross) > 25)
            res.Warnings.Add($"Crosswind {Math.Abs(cross):F0} kt is high for most types.");
        if (r.WetRunway)
            res.Warnings.Add("Wet runway — reduced braking, factored distance applied.");
        if (r.RunwayLengthM > 0 && factored > r.RunwayLengthM)
            res.Warnings.Add($"Factored landing distance {factored:N0} m exceeds runway {r.RunwayLengthM:N0} m.");

        res.Status = Status(res.Warnings, r.RunwayLengthM > 0 && factored > r.RunwayLengthM, weight > p.MlwLbs);
        return res;
    }

    // ── Physics helpers ─────────────────────────────────────

    /// <returns>(headwind, crosswind) in kt — headwind positive, crosswind +right.</returns>
    private static (double head, double cross) WindComponents(double runwayHdg, double windDir, double windKts)
    {
        double angle = (windDir - runwayHdg) * Math.PI / 180.0;
        double head = windKts * Math.Cos(angle);
        double cross = windKts * Math.Sin(angle);
        return (head, cross);
    }

    /// <summary>Headwind shortens, tailwind lengthens. Textbook: -1.5%/kt head, +5%/kt tail.</summary>
    private static double WindDistanceFactor(double headwind)
    {
        if (headwind >= 0) return Math.Max(0.5, 1.0 - headwind * 0.015);
        return 1.0 + (-headwind) * 0.05;
    }

    /// <summary>Density altitude from field elevation, OAT and QNH (approximate).</summary>
    private static double DensityAltitudeFt(double elevationFt, double oatC, double qnhMb)
    {
        double pressureAlt = elevationFt + (1013.25 - qnhMb) * 27.0;
        double isaTempC = 15.0 - 0.001981 * elevationFt;   // ISA lapse ~1.98°C/1000ft
        return pressureAlt + 118.8 * (oatC - isaTempC);
    }

    private static FlapSetting ResolveFlap(IReadOnlyList<FlapSetting> flaps, string label)
    {
        if (!string.IsNullOrWhiteSpace(label))
        {
            var match = flaps.FirstOrDefault(f => string.Equals(f.Label, label, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        // default to the reference detent (DistanceFactor closest to 1.0)
        return flaps.OrderBy(f => Math.Abs(f.DistanceFactor - 1.0)).First();
    }

    private static string Status(List<string> warnings, bool exceedsRunway, bool overweight)
    {
        if (exceedsRunway || overweight) return "UNSAFE";
        return warnings.Count > 0 ? "CAUTION" : "OK";
    }
}
