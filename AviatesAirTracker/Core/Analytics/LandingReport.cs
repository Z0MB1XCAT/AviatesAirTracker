using AviatesAirTracker.Models;

namespace AviatesAirTracker.Core.Analytics;

// ============================================================
// LANDING REPORT — the single shared projection of a landing.
//
// Both the desktop touchdown modal (LandingResultModal.razor) and
// the EFB "Landing" app render from THIS type, so the two can never
// drift apart in wording, grading, colours or which figures appear.
//
// Anything the pilot sees about a landing should be added here —
// not in one of the two UIs.
// ============================================================

public sealed class LandingReportFactor
{
    public string Label { get; set; } = "";
    public double Score { get; set; }
    public double Max { get; set; }
    public double Pct => Max > 0 ? Math.Clamp(Score / Max * 100, 0, 100) : 0;
    public string Color { get; set; } = "#22C55E";
}

/// <summary>One plotted sample: the trace point plus its along-track distance from touchdown.</summary>
public sealed class LandingReportPoint
{
    public double T { get; set; }            // s from touchdown (negative = before)
    public double DistNm { get; set; }       // along-track NM from touchdown (negative = before)
    public double AglFt { get; set; }
    public double IasKts { get; set; }
    public double GsKts { get; set; }
    public double VsFpm { get; set; }
    public double PitchDeg { get; set; }
    public double BankDeg { get; set; }
    public double FlapsPct { get; set; }
    public bool GearDown { get; set; }
    public bool OnGround { get; set; }
}

/// <summary>A stabilised-approach gate (1000 ft / 500 ft AGL) as read from the trace.</summary>
public sealed class LandingGate
{
    public int AglFt { get; set; }
    /// <summary>False when the trace starts below this height (nothing to check).</summary>
    public bool Recorded { get; set; }
    public double DistNm { get; set; }
    public double IasKts { get; set; }
    public double VsFpm { get; set; }
    public double BankDeg { get; set; }
    public double FlapsPct { get; set; }
    public bool GearDown { get; set; }
    public bool ConfigOk { get; set; }
    public bool SinkOk { get; set; }
    public bool BankOk { get; set; }
    /// <summary>Only judged at 1000 ft (LandingComplianceChecker's Vref window); null = not judged.</summary>
    public bool? SpeedOk { get; set; }
    public bool Stable => Recorded && ConfigOk && SinkOk && BankOk && SpeedOk != false;
}

/// <summary>A flight-data-monitoring style exceedance. Severity: warning | caution | info.</summary>
public sealed class LandingEvent
{
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Severity { get; set; } = "info";
}

public sealed class LandingReport
{
    // Identity / context
    public string Id { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
    public string AirportIcao { get; set; } = "";
    public string Runway { get; set; } = "";
    public string FlightNumber { get; set; } = "";
    public string AircraftType { get; set; } = "";
    public string Route { get; set; } = "";
    /// <summary>Optional decorative aircraft photo for the result surface; null → gradient only.</summary>
    public string? AircraftImagePath { get; set; }

    // Headline
    public int Score { get; set; }
    public string Grade { get; set; } = "";
    public string GradeColor { get; set; } = "#22C55E";
    /// <summary>The fun one — how buttery was it.</summary>
    public string ButterRating { get; set; } = "";
    public string ButterBlurb { get; set; } = "";
    public string ButterColor { get; set; } = "#22C55E";

    // Touchdown parameters (already absolute / display-ready)
    public double VerticalSpeedFpm { get; set; }   // magnitude
    public double IasKts { get; set; }
    public double GroundSpeedKts { get; set; }
    public double PitchDeg { get; set; }
    public double BankDeg { get; set; }            // magnitude
    public double HeadwindKts { get; set; }
    public double CrosswindKts { get; set; }       // magnitude
    public double WindDirectionDeg { get; set; }
    public double WindSpeedKts { get; set; }

    // Quality flags
    public bool StableApproach { get; set; }
    public bool FlareDetected { get; set; }
    public int BounceCount { get; set; }

    // Compliance -- see LandingComplianceChecker. RequiresReview drives the "held for review"
    // banner; ComplianceViolations are the human-readable reasons shown under it.
    public bool RequiresReview { get; set; }
    public List<string> ComplianceViolations { get; set; } = [];
    public bool FlapsConfiguredBy1000Ft { get; set; } = true;
    public bool GearDownBy1000Ft { get; set; } = true;
    public bool SpeedStabilizedBy1000Ft { get; set; } = true;
    public double CenterlineDeviationFt { get; set; }

    // Rollout / misc
    public double RolloutFt { get; set; }
    public double ThresholdFt { get; set; }
    public double FuelLbs { get; set; }
    public double DecelerationKtPerSec { get; set; }

    public List<LandingReportFactor> Factors { get; set; } = [];

    // Flight-data view (the EFB Landing app) — empty/null for landings without a trace
    public List<LandingReportPoint> Trace { get; set; } = [];
    public List<LandingGate> Gates { get; set; } = [];
    public List<LandingEvent> Events { get; set; } = [];
    /// <summary>Touchdown point past the threshold: RunwayDetector's figure, else estimated from the trace.</summary>
    public double? TouchdownDistanceFt { get; set; }
    /// <summary>True when <see cref="TouchdownDistanceFt"/> is the 50 ft-crossing estimate.</summary>
    public bool TouchdownDistanceEstimated { get; set; }
    /// <summary>Heading minus ground track at touchdown (+ = nose right of track); null if not recorded.</summary>
    public double? CrabAngleDeg { get; set; }
    public double MaxSinkBelow1000Fpm { get; set; }
    public double MaxBankBelow500Deg { get; set; }

    // Approach targets the landing was judged against (0 = unknown for this aircraft/landing).
    public double TargetSpeedMinKts { get; set; }
    public double TargetSpeedMaxKts { get; set; }
    public double LandingFlapTargetPct { get; set; }

    /// <summary>The debrief: every check against its limit, rated, with coaching.</summary>
    public LandingAssessment Assessment { get; set; } = new();

    // Career context (filled when history is supplied)
    public bool IsPersonalBest { get; set; }
    public int RankAmongLandings { get; set; }     // 1 = best
    public int TotalLandings { get; set; }
    public int AverageScore { get; set; }
}

public static class LandingReportBuilder
{
    // Decorative aircraft photos — only these three exist under wwwroot/images.
    // AircraftType may be an ICAO designator or a fleet code, so common variants
    // are mapped to the same photo. Anything else → null (no photo, gradient only).
    private static readonly Dictionary<string, string> _aircraftPhotos = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A319"] = "images/fleet-a319.webp",
        ["A320"] = "images/fleet-a320neo.webp", ["A20N"] = "images/fleet-a320neo.webp", ["A320neo"] = "images/fleet-a320neo.webp",
        ["A380"] = "images/fleet-a380.webp", ["A388"] = "images/fleet-a380.webp", ["A380-800"] = "images/fleet-a380.webp",
    };

    private static string? PhotoFor(string? type)
        => type is not null && _aircraftPhotos.TryGetValue(type, out var p) ? p : null;

    /// <summary>
    /// Projects a raw <see cref="LandingResult"/> into the shared report.
    /// <paramref name="history"/> is optional — supply it to populate the
    /// personal-best / ranking context.
    /// </summary>
    public static LandingReport Build(
        LandingResult r,
        FlightRecord? flight = null,
        IReadOnlyList<LandingResult>? history = null,
        IReadOnlyList<LandingTracePoint>? trace = null)
    {
        var (butter, blurb, butterColor) = Butter(r.VerticalSpeedFPM, r.BounceCount);

        var report = new LandingReport
        {
            Id = r.Id.ToString(),
            TimestampUtc = r.Timestamp,
            AirportIcao = string.IsNullOrWhiteSpace(r.AirportICAO) ? "----" : r.AirportICAO,
            Runway = string.IsNullOrWhiteSpace(r.RunwayIdentifier) ? "RWY --" : r.RunwayIdentifier,
            FlightNumber = flight?.FlightNumber ?? "",
            AircraftType = flight?.AircraftType ?? "",
            AircraftImagePath = PhotoFor(flight?.AircraftType),
            Route = flight != null && !string.IsNullOrEmpty(flight.DepartureICAO)
                ? $"{flight.DepartureICAO} → {flight.ArrivalICAO}" : "",

            Score = r.LandingScore,
            Grade = r.LandingGrade,
            GradeColor = r.LandingGradeColor,
            ButterRating = butter,
            ButterBlurb = blurb,
            ButterColor = butterColor,

            VerticalSpeedFpm = Math.Abs(r.VerticalSpeedFPM),
            IasKts = r.IASKts,
            GroundSpeedKts = r.GroundSpeedKts,
            PitchDeg = r.TouchdownPitchDeg,
            BankDeg = Math.Abs(r.TouchdownBankDeg),
            HeadwindKts = r.HeadwindComponent,
            CrosswindKts = Math.Abs(r.CrosswindComponent),
            WindDirectionDeg = r.WindDirectionDeg,
            WindSpeedKts = r.WindSpeedKts,

            StableApproach = r.ApproachWasStable,
            FlareDetected = r.FlareDetected,
            BounceCount = r.BounceCount,

            RequiresReview = r.RequiresReview,
            ComplianceViolations = r.ComplianceViolations,
            FlapsConfiguredBy1000Ft = r.FlapsConfiguredBy1000Ft,
            GearDownBy1000Ft = r.GearDownBy1000Ft,
            SpeedStabilizedBy1000Ft = r.SpeedStabilizedBy1000Ft,
            CenterlineDeviationFt = r.CenterlineDeviationFt,

            RolloutFt = r.RolloutDistanceFt,
            ThresholdFt = r.ThresholdDistanceFt,
            FuelLbs = r.FuelOnTouchdownLbs,
            DecelerationKtPerSec = r.AverageDecelerationKtPerSec,

            TargetSpeedMinKts = r.ApproachSpeedMinKts,
            TargetSpeedMaxKts = r.ApproachSpeedMaxKts,
            LandingFlapTargetPct = r.LandingFlapTargetPct,
        };

        var b = r.ScoreBreakdown;
        report.Factors =
        [
            Factor("Vertical Speed",     b.VerticalSpeedScore, 30),
            Factor("Pitch Attitude",     b.PitchScore,         20),
            Factor("Bank Angle",         b.BankScore,          15),
            Factor("Speed Management",   b.SpeedScore,         15),
            Factor("Crosswind",          b.CrosswindScore,     10),
            Factor("Approach Stability", b.StabilityScore,     10),
        ];

        ApplyFlightData(report, r, trace ?? r.Trace);
        report.Assessment = LandingAssessmentBuilder.Build(report, r);

        if (history is { Count: > 0 })
        {
            var scores = history.Select(h => h.LandingScore).ToList();
            report.TotalLandings = scores.Count;
            report.AverageScore = (int)Math.Round(scores.Average());
            report.RankAmongLandings = scores.Count(s => s > r.LandingScore) + 1;
            report.IsPersonalBest = report.RankAmongLandings == 1 && scores.Count > 1;
        }

        return report;
    }

    // ═══ Flight-data view ═══════════════════════════════════
    // AviatesAir's FDM event limits. Operators set their own limits (that is how real FDM
    // programmes work); these follow common airline practice and line up with
    // LandingComplianceChecker where the two overlap (hard landing 800 fpm, centreline 60 ft,
    // touchdown zone 200–3000 ft). Training-grade — not an aircraft limitation.
    public static class Limits
    {
        public const double HardCautionFpm = 600, HardWarningFpm = 800;
        public const double PitchHighCaution = 7.5, PitchHighWarning = 9.0;   // tail-strike margin
        public const double BankTdCaution = 3.0, BankTdWarning = 5.0;          // wingtip / pod clearance
        public const double LongCautionFt = 2500, LongWarningFt = 3000, ShortWarningFt = 200;
        public const double CentrelineCautionFt = 30, CentrelineWarningFt = 60;
        public const double SinkBelow1000Caution = 1000, SinkBelow1000Warning = 1500;
        public const double BankBelow500Caution = 10, BankBelow500Warning = 15;
        public const double TailwindWarningKts = 10, CrosswindCautionKts = 25;
        public const double CrabCautionDeg = 5;
        public const double GateMaxSinkFpm = 1000, GateMaxBankDeg = 10, GateMinFlapsPct = 70;
    }

    private static void ApplyFlightData(LandingReport rep, LandingResult r, IReadOnlyList<LandingTracePoint> trace)
    {
        rep.Trace = WithDistance(trace);

        if (r.TouchdownHeadingTrueDeg != 0 || r.TouchdownTrackTrueDeg != 0)
            rep.CrabAngleDeg = Math.Round(((r.TouchdownHeadingTrueDeg - r.TouchdownTrackTrueDeg + 540) % 360) - 180, 1);

        if (r.ThresholdDistanceFt > 0)
            rep.TouchdownDistanceFt = Math.Round(r.ThresholdDistanceFt);
        else if (EstimateTouchdownDistanceFt(rep.Trace) is { } est)
        {
            rep.TouchdownDistanceFt = Math.Round(est);
            rep.TouchdownDistanceEstimated = true;
        }

        var before = rep.Trace.Where(p => p.T < 0).ToList();
        rep.MaxSinkBelow1000Fpm = before.Where(p => p.AglFt is > 50 and <= 1000 && p.VsFpm < 0)
                                        .Select(p => -p.VsFpm).DefaultIfEmpty(0).Max();
        rep.MaxBankBelow500Deg = before.Where(p => p.AglFt is > 10 and <= 500)
                                       .Select(p => Math.Abs(p.BankDeg)).DefaultIfEmpty(0).Max();

        rep.Gates = [Gate(before, 1000, r.SpeedStabilizedBy1000Ft), Gate(before, 500, null)];
        rep.Events = Events(rep, r);
    }

    /// <summary>Along-track distance of every sample from the touchdown sample (haversine, NM).</summary>
    private static List<LandingReportPoint> WithDistance(IReadOnlyList<LandingTracePoint> trace)
    {
        var pts = trace.OrderBy(p => p.T).ToList();
        if (pts.Count == 0) return [];
        int td = pts.FindIndex(p => p.T >= 0);
        if (td < 0) td = pts.Count - 1;
        var dist = new double[pts.Count];
        for (int i = td - 1; i >= 0; i--) dist[i] = dist[i + 1] - Nm(pts[i], pts[i + 1]);
        for (int i = td + 1; i < pts.Count; i++) dist[i] = dist[i - 1] + Nm(pts[i - 1], pts[i]);
        return pts.Select((p, i) => new LandingReportPoint
        {
            T = p.T, DistNm = Math.Round(dist[i], 4), AglFt = p.AglFt, IasKts = p.IasKts, GsKts = p.GsKts,
            VsFpm = p.VsFpm, PitchDeg = p.PitchDeg, BankDeg = p.BankDeg, FlapsPct = p.FlapsPct,
            GearDown = p.GearDown, OnGround = p.OnGround,
        }).ToList();
    }

    private static double Nm(LandingTracePoint a, LandingTracePoint b)
    {
        if (a.Lat == 0 && a.Lon == 0 || b.Lat == 0 && b.Lon == 0) return 0;
        const double R = 3440.065;
        double dLat = (b.Lat - a.Lat) * Math.PI / 180, dLon = (b.Lon - a.Lon) * Math.PI / 180;
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(a.Lat * Math.PI / 180) * Math.Cos(b.Lat * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    /// <summary>
    /// Air distance from the 50 ft crossing (the standard threshold crossing height on a
    /// 3° path) to touchdown — how far past the threshold the wheels met the runway when
    /// RunwayDetector didn't know the runway. Null when the trace doesn't span 50 ft.
    /// </summary>
    internal static double? EstimateTouchdownDistanceFt(IReadOnlyList<LandingReportPoint> trace)
    {
        var before = trace.Where(p => p.T <= 0).ToList();
        for (int i = before.Count - 1; i > 0; i--)
        {
            var hi = before[i - 1]; var lo = before[i];
            if (hi.AglFt >= 50 && lo.AglFt < 50)
            {
                double f = (hi.AglFt - 50) / Math.Max(1e-6, hi.AglFt - lo.AglFt);
                double crossNm = hi.DistNm + (lo.DistNm - hi.DistNm) * f;
                double ft = -crossNm * 6076.12;
                return ft is > 0 and < 8000 ? ft : null;
            }
        }
        return null;
    }

    private static LandingGate Gate(List<LandingReportPoint> before, int agl, bool? speedOk)
    {
        var g = new LandingGate { AglFt = agl };
        // the first sample at/below the gate after the trace was above it
        int i = before.FindIndex(p => p.AglFt <= agl);
        if (i <= 0) return g;
        var p = before[i];
        g.Recorded = true;
        g.DistNm = p.DistNm; g.IasKts = p.IasKts; g.VsFpm = p.VsFpm; g.BankDeg = p.BankDeg;
        g.FlapsPct = p.FlapsPct; g.GearDown = p.GearDown;
        g.ConfigOk = p.GearDown && p.FlapsPct >= Limits.GateMinFlapsPct;
        g.SinkOk = -p.VsFpm <= Limits.GateMaxSinkFpm;
        g.BankOk = Math.Abs(p.BankDeg) <= Limits.GateMaxBankDeg;
        g.SpeedOk = speedOk;
        return g;
    }

    private static List<LandingEvent> Events(LandingReport rep, LandingResult r)
    {
        var ev = new List<LandingEvent>();
        void Add(string code, string sev, string title, string detail) =>
            ev.Add(new LandingEvent { Code = code, Severity = sev, Title = title, Detail = detail });

        double vs = rep.VerticalSpeedFpm;
        if (vs >= Limits.HardWarningFpm) Add("HARD", "warning", "HARD LANDING", $"{vs:F0} fpm at touchdown (limit {Limits.HardWarningFpm:F0}). Aircraft inspection required.");
        else if (vs >= Limits.HardCautionFpm) Add("HARD", "caution", "FIRM TOUCHDOWN", $"{vs:F0} fpm at touchdown (caution {Limits.HardCautionFpm:F0}).");

        if (r.TouchdownPitchDeg >= Limits.PitchHighWarning) Add("PITCH", "warning", "HIGH PITCH AT TOUCHDOWN", $"{r.TouchdownPitchDeg:F1}°. Tail-strike risk.");
        else if (r.TouchdownPitchDeg >= Limits.PitchHighCaution) Add("PITCH", "caution", "HIGH PITCH AT TOUCHDOWN", $"{r.TouchdownPitchDeg:F1}°. Reduced tail clearance.");
        else if (r.TouchdownPitchDeg < 0) Add("PITCH", "caution", "NOSE-LOW TOUCHDOWN", $"{r.TouchdownPitchDeg:F1}°. Nosewheel-first risk.");

        if (rep.BankDeg >= Limits.BankTdWarning) Add("BANK", "warning", "BANK AT TOUCHDOWN", $"{rep.BankDeg:F1}°. Wingtip / engine pod clearance.");
        else if (rep.BankDeg >= Limits.BankTdCaution) Add("BANK", "caution", "BANK AT TOUCHDOWN", $"{rep.BankDeg:F1}°.");

        if (r.BounceCount >= 2) Add("BOUNCE", "warning", "MULTIPLE BOUNCES", $"{r.BounceCount} bounces after the first touchdown.");
        else if (r.BounceCount == 1) Add("BOUNCE", "caution", "BOUNCED LANDING", "One bounce after the first touchdown.");

        if (rep.TouchdownDistanceFt is { } td)
        {
            string est = rep.TouchdownDistanceEstimated ? " (est.)" : "";
            if (td > Limits.LongWarningFt) Add("LONG", "warning", "LONG LANDING", $"Touchdown {td:N0} ft past the threshold{est}. Beyond the touchdown zone.");
            else if (td > Limits.LongCautionFt) Add("LONG", "caution", "LONG FLARE", $"Touchdown {td:N0} ft past the threshold{est}.");
            else if (!rep.TouchdownDistanceEstimated && td < Limits.ShortWarningFt) Add("SHORT", "warning", "SHORT LANDING", $"Touchdown only {td:N0} ft past the threshold.");
        }

        double cl = Math.Abs(r.CenterlineDeviationFt);
        if (cl >= Limits.CentrelineWarningFt) Add("CL", "warning", "OFF CENTRELINE", $"{cl:F0} ft {(r.CenterlineDeviationFt > 0 ? "right" : "left")} of the centreline.");
        else if (cl >= Limits.CentrelineCautionFt) Add("CL", "caution", "CENTRELINE DEVIATION", $"{cl:F0} ft {(r.CenterlineDeviationFt > 0 ? "right" : "left")} of the centreline.");

        if (rep.MaxSinkBelow1000Fpm >= Limits.SinkBelow1000Warning) Add("SINK", "warning", "HIGH RATE OF DESCENT", $"{rep.MaxSinkBelow1000Fpm:F0} fpm below 1000 ft AGL.");
        else if (rep.MaxSinkBelow1000Fpm >= Limits.SinkBelow1000Caution) Add("SINK", "caution", "HIGH RATE OF DESCENT", $"{rep.MaxSinkBelow1000Fpm:F0} fpm below 1000 ft AGL.");

        if (rep.MaxBankBelow500Deg >= Limits.BankBelow500Warning) Add("BANKLOW", "warning", "EXCESSIVE BANK BELOW 500 FT", $"{rep.MaxBankBelow500Deg:F0}° maximum.");
        else if (rep.MaxBankBelow500Deg >= Limits.BankBelow500Caution) Add("BANKLOW", "caution", "BANK BELOW 500 FT", $"{rep.MaxBankBelow500Deg:F0}° maximum.");

        var unstable = rep.Gates.Where(g => g.Recorded && !g.Stable).ToList();
        if (unstable.Count > 0)
            Add("UNSTAB", unstable.Any(g => g.AglFt == 500) ? "warning" : "caution", "UNSTABILISED APPROACH",
                string.Join(" · ", unstable.Select(g => $"{g.AglFt} ft: " + string.Join(", ", GateIssues(g)))));
        else if (!r.ApproachWasStable && rep.Gates.All(g => !g.Recorded))
            Add("UNSTAB", "caution", "UNSTABILISED APPROACH", "The approach monitor flagged the approach as unstable.");

        if (r.HeadwindComponent <= -Limits.TailwindWarningKts) Add("TAILWIND", "warning", "TAILWIND LANDING", $"{-r.HeadwindComponent:F0} kt tailwind (limit {Limits.TailwindWarningKts:F0}).");
        if (rep.CrosswindKts >= Limits.CrosswindCautionKts) Add("XWIND", "caution", "STRONG CROSSWIND", $"{rep.CrosswindKts:F0} kt crosswind component.");
        if (rep.CrabAngleDeg is { } crab && Math.Abs(crab) >= Limits.CrabCautionDeg)
            Add("CRAB", "caution", "LANDED IN A CRAB", $"{Math.Abs(crab):F0}° between heading and track. De-crab before touchdown.");
        if (!r.FlareDetected) Add("FLARE", "info", "NO FLARE DETECTED", "Rate of descent was not reduced before touchdown.");

        int Rank(string sv) => sv switch { "warning" => 0, "caution" => 1, _ => 2 };
        return ev.OrderBy(e => Rank(e.Severity)).ToList();
    }

    internal static IEnumerable<string> GateIssues(LandingGate g)
    {
        if (!g.GearDown) yield return "gear not down";
        else if (!g.ConfigOk) yield return $"flaps {g.FlapsPct:F0}%";
        if (!g.SinkOk) yield return $"sink {-g.VsFpm:F0} fpm";
        if (!g.BankOk) yield return $"bank {Math.Abs(g.BankDeg):F0}°";
        if (g.SpeedOk == false) yield return "speed outside VREF…VREF+15";
    }

    private static LandingReportFactor Factor(string label, double score, double max) => new()
    {
        Label = label,
        Score = score,
        Max = max,
        Color = FactorColor(score, max),
    };

    private static string FactorColor(double score, double max)
    {
        double p = max > 0 ? score / max : 0;
        return p switch
        {
            >= 0.85 => "#22C55E",
            >= 0.60 => "#A3D34A",
            >= 0.40 => "#EAB308",
            >= 0.20 => "#F97316",
            _ => "#EF4444",
        };
    }

    /// <summary>The butter meter — rated on touchdown rate, demoted for bounces.</summary>
    private static (string rating, string blurb, string color) Butter(double vsFpm, int bounces)
    {
        double vs = Math.Abs(vsFpm);

        if (bounces >= 2)
            return ("KANGAROO", $"{bounces} bounces. The cabin noticed.", "#EF4444");
        if (bounces == 1 && vs > 250)
            return ("DOUBLE TAP", "Touched, thought about it, touched again.", "#F97316");

        return vs switch
        {
            <= 60  => ("PURE BUTTER", "Did we even land? Spread on toast.", "#22C55E"),
            <= 120 => ("BUTTER", "Smooth as it gets. Cabin crew approve.", "#22C55E"),
            <= 200 => ("SMOOTH", "Nicely flown. Barely felt it.", "#A3D34A"),
            <= 320 => ("SOLID", "A proper airline landing.", "#EAB308"),
            <= 450 => ("FIRM", "Positive touchdown. The tyres are fine.", "#F97316"),
            <= 600 => ("HARD", "That one woke everyone up.", "#EF4444"),
            _      => ("CARRIER LANDING", "Check for a tailhook. And the gear.", "#EF4444"),
        };
    }
}
