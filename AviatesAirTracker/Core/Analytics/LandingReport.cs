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

    // Rollout / misc
    public double RolloutFt { get; set; }
    public double ThresholdFt { get; set; }
    public double FuelLbs { get; set; }
    public double DecelerationKtPerSec { get; set; }

    public List<LandingReportFactor> Factors { get; set; } = [];

    // Career context (filled when history is supplied)
    public bool IsPersonalBest { get; set; }
    public int RankAmongLandings { get; set; }     // 1 = best
    public int TotalLandings { get; set; }
    public int AverageScore { get; set; }
}

public static class LandingReportBuilder
{
    /// <summary>
    /// Projects a raw <see cref="LandingResult"/> into the shared report.
    /// <paramref name="history"/> is optional — supply it to populate the
    /// personal-best / ranking context.
    /// </summary>
    public static LandingReport Build(
        LandingResult r,
        FlightRecord? flight = null,
        IReadOnlyList<LandingResult>? history = null)
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

            RolloutFt = r.RolloutDistanceFt,
            ThresholdFt = r.ThresholdDistanceFt,
            FuelLbs = r.FuelOnTouchdownLbs,
            DecelerationKtPerSec = r.AverageDecelerationKtPerSec,
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
            <= 200 => ("SMOOTH", "Nicely flown — barely felt it.", "#A3D34A"),
            <= 320 => ("SOLID", "A proper airline landing.", "#EAB308"),
            <= 450 => ("FIRM", "Positive touchdown. The tyres are fine.", "#F97316"),
            <= 600 => ("HARD", "That one woke everyone up.", "#EF4444"),
            _      => ("CARRIER LANDING", "Check for a tailhook. And the gear.", "#EF4444"),
        };
    }
}
