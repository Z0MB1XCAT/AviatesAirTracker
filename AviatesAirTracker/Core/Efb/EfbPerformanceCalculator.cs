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
//   • Reduced thrust uses the assumed-temperature method airline tools
//     use (Airbus FLEX / Boeing SEL TEMP): the highest temperature, between
//     the flat-rating temperature and the max flex, at which the takeoff
//     computed *as if the OAT were that temperature* still fits the runway.
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

    // Airline-EFB takeoff inputs (all optional — defaults are the normal line setup: FLEX allowed, packs on, no anti-ice).
    public bool ForceToga { get; set; }              // skip FLEX, full rated thrust
    public bool PacksOn { get; set; } = true;        // bleed for packs costs a little thrust
    public string AntiIce { get; set; } = "OFF";     // OFF | ENG | ENG+WING
    public double CgPct { get; set; } = 28;          // TOW CG in %MAC → THS / stab trim
    public double TakeoffShiftM { get; set; }        // intersection departure: TORA lost behind you
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
    public bool WetRunway { get; set; }              // legacy: = RWYCC 5 when RunwayCondition is 0
    public string Autobrake { get; set; } = "MED";   // LOW | MED | MAX | MANUAL

    // In-flight landing inputs (Fenix / Flysmart landing module)
    public int RunwayCondition { get; set; }         // RWYCC 6 dry … 1 poor (0 = from WetRunway)
    public string Reverser { get; set; } = "IDLE";   // MAX | IDLE | NONE
    public bool Autoland { get; set; }
    public bool AutoThrust { get; set; } = true;
    public List<string> Failures { get; set; } = []; // EfbLandingFailures ids
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

    public string Category { get; set; } = "";       // Airbus | Boeing | Turboprop → MCDU vs CDU page
    public bool OptimumFlapChosen { get; set; }      // FlapSetting was "OPT" and this detent won
    public string Thrust { get; set; } = "TOGA";     // FLEX | TOGA
    public int? FlexTempC { get; set; }              // assumed temperature, null when TOGA
    public int MinFlexTempC { get; set; }            // flat-rating temp (or OAT+1) at this field
    public int MaxFlexTempC { get; set; }
    public string LimitCode { get; set; } = "";      // RWY | MAX FLEX | MTOW | TOGA
    public double AvailableRunwayM { get; set; }     // TORA − TO shift
    public double TakeoffShiftM { get; set; }
    public double PerfLimitedWeightLbs { get; set; } // TOPL: heaviest TOW that fits at full thrust
    public string Ths { get; set; } = "";            // Airbus THS, e.g. "UP0.8" / "DN0.2"
    public double StabTrimUnits { get; set; }        // Boeing stab trim
    public int FlapRetractKts { get; set; }          // Airbus F / S / green dot (O); 0 for others
    public int SlatRetractKts { get; set; }
    public int GreenDotKts { get; set; }
    public int ThrRedAltFt { get; set; }             // THR RED / ACC / EO ACC, ft MSL
    public int AccAltFt { get; set; }
    public int EngOutAccAltFt { get; set; }
}

public sealed class LandingPerfResult
{
    public bool Ok { get; set; } = true;
    public string AircraftCode { get; set; } = "";
    public string AircraftName { get; set; } = "";
    public string FlapSetting { get; set; } = "";
    public string Category { get; set; } = "";       // Airbus | Boeing | Turboprop → MCDU vs CDU page
    public int Vref { get; set; }                    // Airbus VLS / Boeing VREF, incl. failure ΔVREF
    public int Vapp { get; set; }
    public int WindCorrectionKts { get; set; }       // APPR COR / wind additive
    public int DeltaVrefKts { get; set; }            // from failures
    public int ActualDistanceM { get; set; }         // LDR: unfactored required distance, selected braking
    public int OperationalDistanceM { get; set; }    // 1.15 × LDR: in-flight operational margin
    public int LandingDistanceM { get; set; }        // dispatch: LD × 1.67 dry / 1.92 wet-contaminated
    public int RunwayCondition { get; set; }         // RWYCC used
    public string Reverser { get; set; } = "IDLE";
    public bool Autoland { get; set; }
    public double FailureFactor { get; set; } = 1;
    public List<string> AppliedFailures { get; set; } = [];
    public Dictionary<string, int> DistanceByBrake { get; set; } = [];   // 1.15 × LDR per braking mode
    public int FlapRetractKts { get; set; }          // Airbus go-around F / S / green dot
    public int SlatRetractKts { get; set; }
    public int GreenDotKts { get; set; }
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

        // "OPT" (optimum configuration, as in Flysmart/Fenix): try every detent and keep the
        // one giving the highest FLEX temperature — ties go to the lower V2, and if no detent
        // allows FLEX, the one with the shortest required distance.
        if (string.Equals(r.FlapSetting?.Trim(), "OPT", StringComparison.OrdinalIgnoreCase))
        {
            // OPT only ever considers the detents the company permits (CN-002).
            var best = EfbCompanyPolicy.TakeoffFlaps(p)
                .Select(f => CalculateTakeoffForFlap(p, r, f))
                .OrderByDescending(x => x.FlexTempC ?? int.MinValue)
                .ThenBy(x => x.FlexTempC.HasValue ? x.V2 : x.RequiredRunwayM)
                .First();
            best.OptimumFlapChosen = true;
            return best;
        }
        return CalculateTakeoffForFlap(p, r, ResolveFlap(p.TakeoffFlaps, r.FlapSetting ?? ""));
    }

    private static TakeoffPerfResult CalculateTakeoffForFlap(EfbAircraftProfile p, TakeoffPerfRequest r, FlapSetting flap)
    {
        double weight = r.WeightLbs > 0 ? r.WeightLbs : p.RefWeightLbs;
        var (head, cross) = WindComponents(r.RunwayHeadingDeg, r.WindDirDeg, r.WindKts);
        double shift = Math.Max(0, r.TakeoffShiftM);
        double available = r.RunwayLengthM > 0 ? Math.Max(0, r.RunwayLengthM - shift) : 0;

        // Unfactored takeoff distance at a given (real or assumed) temperature:
        // (weight ratio)² × density × flap × wind × slope × surface × bleed.
        double Todr(double w, double tempC)
        {
            double da = DensityAltitudeFt(r.RunwayElevationFt, tempC, r.QnhMb);
            double densFactor = 1.0 + Math.Max(0, da) / 26_000.0;        // ~+4% per 1000ft DA
            return p.TodrRefM * Math.Pow(w / p.MtowLbs, 2.0) * densFactor * flap.DistanceFactor
                   * WindDistanceFactor(head)
                   * (1.0 + r.RunwaySlopePct * 0.03)                      // ~+3% per 1% uphill
                   * (r.WetRunway ? 1.15 : 1.0)
                   * BleedFactor(r);
        }
        // 15% regulatory-style margin on the raw estimate.
        double Required(double w, double tempC) => Todr(w, tempC) * 1.15;

        // Assumed-temperature window: from the flat-rating temperature (ISA+15, below which
        // an assumed temp buys nothing) or OAT+1 if warmer, up to max flex (ISA+55).
        double isa = 15.0 - 0.001981 * r.RunwayElevationFt;
        int minFlex = (int)Math.Ceiling(Math.Max(isa + 15, r.OatC + 1));
        int maxFlex = (int)Math.Floor(isa + 55);

        int? flex = null;
        if (!r.ForceToga && available > 0 && weight <= p.MtowLbs && Required(weight, r.OatC) <= available)
            for (int t = maxFlex; t >= minFlex; t--)
                if (Required(weight, t) <= available) { flex = t; break; }

        double perfTemp = flex ?? r.OatC;
        double densAlt = DensityAltitudeFt(r.RunwayElevationFt, perfTemp, r.QnhMb);

        // V-speeds: √(weight ratio) × flap factor, plus a small density bump.
        double wf = Math.Sqrt(Math.Max(weight, 1) / p.RefWeightLbs);
        double densSpeedFactor = 1.0 + Math.Max(0, densAlt) / 100_000.0;
        double v2 = p.V2Ref * wf * flap.VSpeedFactor * densSpeedFactor;
        double vr = p.VrRef * wf * flap.VSpeedFactor * densSpeedFactor;
        double v1 = p.V1Ref * wf * flap.VSpeedFactor * densSpeedFactor;
        vr = Math.Max(vr, v1);                                          // V1 ≤ VR ≤ V2
        v2 = Math.Max(v2, vr);

        double todr = Todr(weight, perfTemp);
        double required = todr * 1.15;

        // TOPL: required ∝ W², so the heaviest weight that fits at full thrust is
        // W = MTOW · √(available / required(MTOW)), capped at MTOW.
        double topl = available > 0
            ? Math.Min(p.MtowLbs, p.MtowLbs * Math.Sqrt(available / Required(p.MtowLbs, r.OatC)))
            : p.MtowLbs;

        double densAltOat = DensityAltitudeFt(r.RunwayElevationFt, r.OatC, r.QnhMb);
        int accAlt = RoundUp10(r.RunwayElevationFt + 1500);             // common SOP: 1500 ft AAL

        var res = new TakeoffPerfResult
        {
            AircraftCode = p.Code,
            AircraftName = p.Name,
            Category = p.Category,
            FlapSetting = flap.Label,
            V1 = (int)Math.Round(v1),
            Vr = (int)Math.Round(vr),
            V2 = (int)Math.Round(v2),
            TakeoffDistanceM = (int)Math.Round(todr),
            RequiredRunwayM = (int)Math.Round(required),
            RunwayLengthM = r.RunwayLengthM,
            AvailableRunwayM = available,
            TakeoffShiftM = shift,
            HeadwindKts = Math.Round(head),
            CrosswindKts = Math.Round(cross),
            DensityAltitudeFt = Math.Round(densAltOat),
            WeightLbs = weight,
            Thrust = flex.HasValue ? "FLEX" : "TOGA",
            FlexTempC = flex,
            MinFlexTempC = minFlex,
            MaxFlexTempC = maxFlex,
            PerfLimitedWeightLbs = Math.Round(topl),
            ThrRedAltFt = accAlt,
            AccAltFt = accAlt,
            EngOutAccAltFt = accAlt,
        };
        res.MarginM = available > 0 ? Math.Round(available - required) : 0;
        res.LimitCode = r.ForceToga ? "TOGA"
            : weight > p.MtowLbs ? "MTOW"
            : flex == maxFlex ? "MAX FLEX"
            : "RWY";

        // Trim: Airbus THS in degrees UP/DN, Boeing stab trim units — both linear in %MAC,
        // a representative first-order fit (forward CG → more nose-up).
        double cg = Math.Clamp(r.CgPct, 5, 45);
        double ths = Math.Round((cg - 28.5) * 0.2, 1);
        res.Ths = (ths > 0 ? "DN" : "UP") + Math.Abs(ths).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        res.StabTrimUnits = Math.Round(Math.Clamp(8.4 - 0.13 * cg, 1, 8.5) * 20) / 20;   // nearest 0.05

        // Airbus climb-out speeds: F (flap retract), S (slat retract), green dot (O). For the
        // A320 family, the line-pilot rule of thumb O ≈ 2 × TOW(t) + 85 kt, with S ≈ 0.91·O and
        // F ≈ 0.70·O (Fenix demo: 60.6 t → 144 / 188 / 206). They depend on weight, not on the
        // assumed temperature, so they're NOT scaled off the FLEX-inflated V2. Widebodies have
        // no such rule, so they fall back to V2 ratios. F is always kept above V2.
        if (p.Category == "Airbus")
        {
            double o = p.MtowLbs < 250_000
                ? 2.0 * (weight / 2204.62) + 85
                : v2 * 1.5;
            res.GreenDotKts = (int)Math.Round(o);
            res.SlatRetractKts = (int)Math.Round(o * 0.913);
            res.FlapRetractKts = (int)Math.Round(Math.Max(o * 0.70, v2 + 5));
        }

        // Warnings & status
        if (weight > p.MtowLbs)
            res.Warnings.Add($"Weight {weight:N0} lb exceeds MTOW {p.MtowLbs:N0} lb.");
        if (head < 0)
            res.Warnings.Add($"Tailwind component of {-head:F0} kt increases takeoff distance.");
        if (Math.Abs(cross) > 25)
            res.Warnings.Add($"Crosswind {Math.Abs(cross):F0} kt is high for most types.");
        if (available > 0 && required > available)
            res.Warnings.Add($"Required {required:N0} m exceeds runway available {available:N0} m.");
        if (!r.ForceToga && !flex.HasValue && available > 0 && required <= available)
            res.Warnings.Add("FLEX not possible at this weight. TOGA thrust.");
        if (densAltOat > 6000)
            res.Warnings.Add($"High density altitude of {densAltOat:N0} ft degrades performance.");

        if (EfbCompanyPolicy.IsTakeoffFlapProhibited(p, flap.Label))
            res.Warnings.Insert(0, EfbCompanyPolicy.For(p.Code)!.TakeoffNotice!);
        res.Status = Status(res.Warnings, available > 0 && required > available, weight > p.MtowLbs);
        return res;
    }

    /// <summary>Bleed air for packs / anti-ice costs thrust, i.e. distance (and so FLEX).</summary>
    private static double BleedFactor(TakeoffPerfRequest r)
    {
        double f = r.PacksOn ? 1.02 : 1.0;
        return (r.AntiIce ?? "OFF").ToUpperInvariant() switch
        {
            "ENG" => f * 1.02,
            "ENG+WING" => f * 1.04,
            _ => f,
        };
    }

    private static int RoundUp10(double v) => (int)(Math.Ceiling(v / 10.0) * 10);

    public static LandingPerfResult CalculateLanding(LandingPerfRequest r)
    {
        var p = AircraftPerformanceProfiles.Find(r.AircraftCode);
        if (p == null)
            return new LandingPerfResult { Ok = false, Status = "UNSAFE", Warnings = { $"Unknown aircraft '{r.AircraftCode}'." } };

        double weight = r.WeightLbs > 0 ? r.WeightLbs : p.MlwLbs;
        var flap = ResolveFlap(p.LandingFlaps, r.FlapSetting);
        var (head, cross) = WindComponents(r.RunwayHeadingDeg, r.WindDirDeg, r.WindKts);
        double densAlt = DensityAltitudeFt(r.RunwayElevationFt, r.OatC, r.QnhMb);
        bool airbus = p.Category == "Airbus";

        // RWYCC 6 (dry) … 1 (poor); legacy WetRunway maps to 5 (good / wet).
        int rwycc = r.RunwayCondition is >= 1 and <= 6 ? r.RunwayCondition : (r.WetRunway ? 5 : 6);

        // Failures: distance factors multiply, the largest ΔVREF applies.
        // Only this aircraft family's failures count (a 737 has no "G SYS", an ATR no autobrake).
        var family = EfbLandingFailures.FamilyOf(p.Code);
        var failures = (r.Failures ?? []).Select(EfbLandingFailures.Find)
            .Where(f => f != null && f.AppliesTo(family)).Select(f => f!).ToList();
        double failFactor = failures.Aggregate(1.0, (acc, f) => acc * f.DistanceFactor);
        int deltaVref = failures.Count > 0 ? failures.Max(f => f.DeltaVrefKts) : 0;
        bool noAutobrakeFitted = p.Category == "Turboprop";             // the ATR brakes manually
        bool noAutobrake = noAutobrakeFitted || failures.Any(f => f.NoAutobrake);

        // Approach speeds. VREF/VLS: √(W/MLW) × flap factor — the same scaling
        // LandingComplianceChecker reuses — plus the failure ΔVREF.
        //   Airbus (FCOM): VAPP = VLS + APPR COR, APPR COR = the higher of 5 kt (A/THR ON or
        //                  autoland only) and ⅓ of the headwind, limited to 15 kt.
        //   Boeing:        VREF + ½ steady headwind, 5–20 kt.
        double wf = Math.Sqrt(Math.Max(weight, 1) / p.MlwLbs);
        double vls = p.VrefLandingRef * wf * flap.VSpeedFactor + deltaVref;
        double headForSpeed = Math.Max(0, head);
        double corr = airbus
            ? Math.Min(15, Math.Max(r.AutoThrust || r.Autoland ? 5 : 0, headForSpeed / 3.0))
            : Math.Clamp(headForSpeed / 2.0, 5, 20);
        double vapp = vls + corr;

        // Actual (unfactored) landing distance for one braking mode.
        //   braking: autobrake is deceleration-limited, manual max braking is friction-limited,
        //            so the stop is governed by whichever is worse on this surface.
        double Distance(string brake)
        {
            double friction = rwycc switch { 6 => 1.0, 5 => 1.25, 4 => 1.45, 3 => 1.70, 2 => 2.05, _ => 2.55 };
            const double maxManual = 0.78;                       // max manual braking vs autobrake MED, dry
            double decel = brake switch { "LOW" => 1.32, "MED" => 1.00, "MAX" => 0.86, _ => maxManual };
            double braking = Math.Max(decel, maxManual * friction);
            double reverser = (r.Reverser ?? "IDLE").ToUpperInvariant() switch
            {
                "MAX"  => 1.0 - (0.03 + (6 - rwycc) * 0.025),     // reverse credit grows as friction falls
                "NONE" => 1.0 + (0.02 + (6 - rwycc) * 0.02),
                _      => 1.0,                                    // IDLE reverse: the certified baseline
            };
            // extra energy above VLS+5 (failure ΔVREF is already inside its distance factor)
            double speedFactor = Math.Pow(vapp / (vls + 5), 2);
            return p.LdrRefM * (weight / p.MlwLbs)
                   * (1.0 + Math.Max(0, densAlt) / 30_000.0)
                   * flap.DistanceFactor * WindDistanceFactor(head)
                   * (1.0 - r.RunwaySlopePct * 0.02)               // uphill helps stopping
                   * braking * reverser * speedFactor * failFactor
                   * (r.Autoland ? 1.06 : 1.0);                    // autoland flares longer
        }

        string sel = NormalizeBrake(noAutobrake ? "MANUAL" : r.Autobrake);
        double ld = Distance(sel);
        double ldr = ld * 1.15;                                     // 1.15 × LDR: in-flight operational margin
        double dispatch = ld * (rwycc >= 6 ? 1.67 : 1.92);          // dispatch: 60% / wet 67% rule
        double lda = r.RunwayLengthM;

        var res = new LandingPerfResult
        {
            AircraftCode = p.Code,
            AircraftName = p.Name,
            Category = p.Category,
            FlapSetting = flap.Label,
            Vref = (int)Math.Round(vls),
            Vapp = (int)Math.Round(vapp),
            WindCorrectionKts = (int)Math.Round(corr),
            DeltaVrefKts = deltaVref,
            ActualDistanceM = (int)Math.Round(ld),
            OperationalDistanceM = (int)Math.Round(ldr),
            LandingDistanceM = (int)Math.Round(dispatch),
            RunwayLengthM = lda,
            RunwayCondition = rwycc,
            HeadwindKts = Math.Round(head),
            CrosswindKts = Math.Round(cross),
            DensityAltitudeFt = Math.Round(densAlt),
            WeightLbs = weight,
            Autobrake = sel,
            Reverser = (r.Reverser ?? "IDLE").ToUpperInvariant(),
            Autoland = r.Autoland,
            FailureFactor = Math.Round(failFactor, 2),
            AppliedFailures = failures.Select(f => f.Id).ToList(),
            DistanceByBrake = (noAutobrake ? new[] { "MANUAL" } : new[] { "LOW", "MED", "MAX", "MANUAL" })
                .ToDictionary(b => b, b => (int)Math.Round(Distance(b) * 1.15)),
        };
        res.MarginM = lda > 0 ? Math.Round(lda - ldr) : 0;

        // Airbus approach page speeds: F / S / green dot, from the landing weight (as for takeoff).
        if (airbus)
        {
            double o = p.MtowLbs < 250_000 ? 2.0 * (weight / 2204.62) + 85 : vls * 1.5;
            res.GreenDotKts = (int)Math.Round(o);
            res.SlatRetractKts = (int)Math.Round(o * 0.913);
            res.FlapRetractKts = (int)Math.Round(Math.Max(o * 0.70, vls + 10));
        }

        if (weight > p.MlwLbs)
            res.Warnings.Add($"Weight {weight:N0} lb exceeds MLW {p.MlwLbs:N0} lb (overweight landing).");
        if (head < 0)
            res.Warnings.Add($"Tailwind component of {-head:F0} kt increases landing distance.");
        if (head < -10)
            res.Warnings.Add("Tailwind exceeds the usual 10 kt landing limit.");
        if (Math.Abs(cross) > (rwycc >= 5 ? 33 : rwycc >= 3 ? 25 : 15))
            res.Warnings.Add($"Crosswind {Math.Abs(cross):F0} kt exceeds the guidance for RWYCC {rwycc}.");
        if (noAutobrake && !noAutobrakeFitted && NormalizeBrake(r.Autobrake) != "MANUAL")
            res.Warnings.Add("Autobrake not available with the selected failure, so manual braking is computed.");
        if (lda > 0 && ld > lda)
            res.Warnings.Add($"LDR {ld:N0} m exceeds runway available {lda:N0} m.");
        else if (lda > 0 && ldr > lda)
            res.Warnings.Add($"1.15 × LDR {ldr:N0} m exceeds runway available {lda:N0} m. No operational margin.");

        if (EfbCompanyPolicy.IsLandingFlapBelowMinimum(p, flap.Label))
            res.Warnings.Insert(0, EfbCompanyPolicy.For(p.Code)!.LandingNotice!);

        bool exceeds = lda > 0 && ld > lda;
        res.Status = exceeds || weight > p.MlwLbs ? "UNSAFE"
            : res.Warnings.Count > 0 || failures.Count > 0 || rwycc <= 3 ? "CAUTION" : "OK";
        return res;
    }

    private static string NormalizeBrake(string? b) => (b ?? "MED").Trim().ToUpperInvariant() switch
    {
        "LO" or "LOW" or "1" or "2" => "LOW",
        "HI" or "HIGH" or "MAX" or "MAX AUTO" or "4" => "MAX",
        "MAN" or "MANUAL" or "OFF" => "MANUAL",
        _ => "MED",
    };

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
