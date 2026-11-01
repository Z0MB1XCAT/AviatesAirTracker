using AviatesAirTracker.Models;
using L = AviatesAirTracker.Core.Analytics.LandingReportBuilder.Limits;

namespace AviatesAirTracker.Core.Analytics;

// ============================================================
// LANDING ASSESSMENT — the debrief behind the score.
//
// The score says how good a landing was; this says what to work on. Every check is measured
// against the limit it's judged by and rated pass / caution / critical, with one line of
// coaching for anything that isn't a pass. The overall rating is the worst finding, so a
// landing can score 85 and still be rated Critical (say, a butter touchdown off an approach
// that was unstable at 500 ft).
//
// Part of LandingReport (the shared projection), so the desktop Landing Analysis page and the
// EFB read the same findings. Training-grade, same limits as LandingReportBuilder.Limits.
// ============================================================

public static class CheckStatus
{
    public const string Pass = "pass";
    public const string Caution = "caution";
    public const string Critical = "critical";
    public const string NotMeasured = "na";
}

public sealed class LandingCheck
{
    public string Group { get; set; } = "";     // "Approach" | "Touchdown" | "Rollout and wind"
    public string Name { get; set; } = "";
    public string Measured { get; set; } = "";
    public string Target { get; set; } = "";
    public string Status { get; set; } = CheckStatus.Pass;
    public string Advice { get; set; } = "";
}

public sealed class LandingAssessment
{
    /// <summary>"Critical", "Needs work", "Good" or "Excellent".</summary>
    public string Rating { get; set; } = "";
    public string RatingColor { get; set; } = "#3EC7AC";
    public string Summary { get; set; } = "";
    public int CriticalCount { get; set; }
    public int CautionCount { get; set; }
    public List<LandingCheck> Checks { get; set; } = [];

    // Approach-quality figures the checks are built from, also plotted on the analysis page.
    public double? GlidepathMaxDeviationDeg { get; set; }   // + = above the 3° path
    public double? GlidepathMaxDeviationAglFt { get; set; }
    public double? GearDownAglFt { get; set; }               // highest AGL in the trace with the gear down
    public double? LandingFlapsAglFt { get; set; }           // AGL at which landing flap was first set
}

public static class LandingAssessmentBuilder
{
    private const double GlideslopeDegPerDot = 0.35;   // ILS: 2 dots ≈ 0.7°
    private const double ReferencePathDeg = 3.0;

    public static LandingAssessment Build(LandingReport rep, LandingResult r)
    {
        var a = new LandingAssessment();
        var before = rep.Trace.Where(p => p.T < 0).ToList();
        var checks = a.Checks;

        void Add(string group, string name, string measured, string target, string status, string advice = "") =>
            checks.Add(new LandingCheck { Group = group, Name = name, Measured = measured, Target = target, Status = status, Advice = advice });

        string Band() => r.ApproachSpeedMinKts > 0
            ? $"{r.ApproachSpeedMinKts:F0}-{r.ApproachSpeedMaxKts:F0} kt"
            : "Vref to Vref+15";

        // ── Approach ──────────────────────────────────────────────
        const string Approach = "Approach";
        var g1000 = rep.Gates.FirstOrDefault(g => g.AglFt == 1000);
        var g500 = rep.Gates.FirstOrDefault(g => g.AglFt == 500);
        const string StableTarget = "Landing config, sink ≤ 1,000 fpm, bank ≤ 10°, on speed";

        if (g1000 is { Recorded: true })
            Add(Approach, "Stabilised by 1,000 ft", g1000.Stable ? "Stable" : Capitalise(string.Join(", ", LandingReportBuilder.GateIssues(g1000))),
                StableTarget, g1000.Stable ? CheckStatus.Pass : CheckStatus.Caution,
                g1000.Stable ? "" : "Have the aircraft configured and on speed before 1,000 ft. In IMC, an unstable approach at 1,000 ft is a go-around.");
        else
            Add(Approach, "Stabilised by 1,000 ft", "Not recorded", StableTarget, CheckStatus.NotMeasured);

        if (g500 is { Recorded: true })
            Add(Approach, "Stabilised by 500 ft", g500.Stable ? "Stable" : Capitalise(string.Join(", ", LandingReportBuilder.GateIssues(g500))),
                StableTarget, g500.Stable ? CheckStatus.Pass : CheckStatus.Critical,
                g500.Stable ? "" : "Not stable by 500 ft is a mandatory go-around. Fly the missed approach rather than land from it.");
        else
            Add(Approach, "Stabilised by 500 ft", "Not recorded", StableTarget, CheckStatus.NotMeasured);

        // Configuration
        var flapTarget = r.LandingFlapTargetPct > 0 ? r.LandingFlapTargetPct : L.GateMinFlapsPct;
        var configMeasured = g1000 is { Recorded: true }
            ? $"Gear {(g1000.GearDown ? "down" : "up")}, flaps {g1000.FlapsPct:F0}%"
            : (r.GearDownBy1000Ft && r.FlapsConfiguredBy1000Ft ? "Set" : "Not set");
        var configStatus = !r.GearDownBy1000Ft ? CheckStatus.Critical
            : !r.FlapsConfiguredBy1000Ft ? CheckStatus.Caution
            : CheckStatus.Pass;
        Add(Approach, "Landing configuration by 1,000 ft", configMeasured, $"Gear down, flaps ≥ {flapTarget:F0}%", configStatus,
            configStatus switch
            {
                CheckStatus.Critical => "The gear was still up at 1,000 ft. Gear down by the final approach fix at the latest.",
                CheckStatus.Caution => "Landing flap went in late. Select it before 1,000 ft so the speed has time to settle.",
                _ => "",
            });

        // Speed at the 1,000 ft gate
        if (g1000 is { Recorded: true })
        {
            var speedOk = r.SpeedStabilizedBy1000Ft;
            Add(Approach, "Speed at 1,000 ft", $"{g1000.IasKts:F0} kt", Band(),
                speedOk ? CheckStatus.Pass : CheckStatus.Caution,
                speedOk ? "" : g1000.IasKts > r.ApproachSpeedMaxKts && r.ApproachSpeedMaxKts > 0
                    ? "Fast at the gate. Plan the deceleration earlier, or use speedbrakes/gear before 1,000 ft."
                    : "Outside the approach-speed window at the gate. Have the target speed set before 1,000 ft.");
        }

        // Glidepath (3° reference to the touchdown point)
        var glide = GlidepathDeviation(before);
        a.GlidepathMaxDeviationDeg = glide?.Deg;
        a.GlidepathMaxDeviationAglFt = glide?.Agl;
        if (glide is { } gp)
        {
            var dots = Math.Abs(gp.Deg) / GlideslopeDegPerDot;
            var dir = gp.Deg > 0 ? "high" : "low";
            var status = dots >= 2 ? CheckStatus.Critical : dots >= 1 ? CheckStatus.Caution : CheckStatus.Pass;
            Add(Approach, "Glidepath (1,000 to 200 ft)",
                dots < 0.2 ? "On the path" : $"{dots:F1} dot {dir} at {gp.Agl:F0} ft",
                "Within 1 dot of a 3° path", status,
                status == CheckStatus.Pass ? "" : gp.Deg > 0
                    ? "High on the approach, which usually ends in a long or firm landing. Correct with power and pitch early, not with a dive near the ground."
                    : "Low on the approach. Low and slow close to the ground is the riskiest place to be; add power and rejoin the path.");
        }
        else
        {
            Add(Approach, "Glidepath (1,000 to 200 ft)", "Not recorded", "Within 1 dot of a 3° path", CheckStatus.NotMeasured);
        }

        Add(Approach, "Sink rate below 1,000 ft", before.Count > 0 ? $"{rep.MaxSinkBelow1000Fpm:F0} fpm max" : "Not recorded",
            $"Below {L.SinkBelow1000Caution:F0} fpm",
            before.Count == 0 ? CheckStatus.NotMeasured
            : rep.MaxSinkBelow1000Fpm >= L.SinkBelow1000Warning ? CheckStatus.Critical
            : rep.MaxSinkBelow1000Fpm >= L.SinkBelow1000Caution ? CheckStatus.Caution : CheckStatus.Pass,
            rep.MaxSinkBelow1000Fpm >= L.SinkBelow1000Caution ? "Too steep close to the ground. A 3° path at approach speed needs roughly 700-800 fpm." : "");

        Add(Approach, "Bank below 500 ft", before.Count > 0 ? $"{rep.MaxBankBelow500Deg:F0}° max" : "Not recorded",
            $"Below {L.BankBelow500Caution:F0}°",
            before.Count == 0 ? CheckStatus.NotMeasured
            : rep.MaxBankBelow500Deg >= L.BankBelow500Warning ? CheckStatus.Critical
            : rep.MaxBankBelow500Deg >= L.BankBelow500Caution ? CheckStatus.Caution : CheckStatus.Pass,
            rep.MaxBankBelow500Deg >= L.BankBelow500Caution ? "Large bank corrections low down mean the lateral path wasn't settled. Be aligned by 500 ft." : "");

        // ── Touchdown ─────────────────────────────────────────────
        const string Touchdown = "Touchdown";
        var vs = rep.VerticalSpeedFpm;
        Add(Touchdown, "Touchdown rate", $"{vs:F0} fpm", $"Below {L.HardCautionFpm:F0} fpm",
            vs >= L.HardWarningFpm ? CheckStatus.Critical : vs >= L.HardCautionFpm ? CheckStatus.Caution : CheckStatus.Pass,
            vs >= L.HardWarningFpm ? "Hard landing: the aircraft needs an inspection. Start the flare earlier and hold the attitude rather than chasing the runway."
            : vs >= L.HardCautionFpm ? "Firm. A slightly earlier, smoother flare (around 20-30 ft) takes most of this out." : "");

        if (r.ApproachSpeedMinKts > 0)
        {
            var low = rep.IasKts < r.ApproachSpeedMinKts - 10;
            var high = rep.IasKts > r.ApproachSpeedMaxKts;
            Add(Touchdown, "Touchdown speed", $"{rep.IasKts:F0} kt", $"About {r.ApproachSpeedMinKts - 5:F0}-{r.ApproachSpeedMinKts:F0} kt",
                low || high ? CheckStatus.Caution : CheckStatus.Pass,
                high ? "Fast at touchdown: the aircraft floats and lands long. Close the thrust levers by about 20 ft."
                : low ? "Slow at touchdown: tail-strike and hard-landing risk. Keep the speed up until the flare." : "");
        }
        else
        {
            var pct = rep.Factors.FirstOrDefault(f => f.Label == "Speed Management")?.Pct ?? 100;
            Add(Touchdown, "Touchdown speed", $"{rep.IasKts:F0} kt", "Near Vref", pct < 40 ? CheckStatus.Caution : CheckStatus.Pass,
                pct < 40 ? "Speed at touchdown was well away from the reference speed for this aircraft." : "");
        }

        Add(Touchdown, "Pitch at touchdown", $"{rep.PitchDeg:F1}°", $"0° to {L.PitchHighCaution:F1}°",
            rep.PitchDeg >= L.PitchHighWarning ? CheckStatus.Critical
            : rep.PitchDeg >= L.PitchHighCaution || rep.PitchDeg < 0 ? CheckStatus.Caution : CheckStatus.Pass,
            rep.PitchDeg >= L.PitchHighCaution ? "High nose attitude eats into tail clearance. Don't hold the aircraft off by over-rotating."
            : rep.PitchDeg < 0 ? "Nose-low touchdown risks landing on the nosewheel first. Flare to a positive attitude." : "");

        Add(Touchdown, "Bank at touchdown", $"{rep.BankDeg:F1}°", $"Below {L.BankTdCaution:F0}°",
            rep.BankDeg >= L.BankTdWarning ? CheckStatus.Critical : rep.BankDeg >= L.BankTdCaution ? CheckStatus.Caution : CheckStatus.Pass,
            rep.BankDeg >= L.BankTdCaution ? "Wings weren't level. In a crosswind, keep the into-wind wing only slightly low." : "");

        var hasRunway = !string.IsNullOrWhiteSpace(r.RunwayIdentifier) && !r.RunwayIdentifier.Contains('?');
        if (hasRunway)
        {
            var cl = Math.Abs(r.CenterlineDeviationFt);
            Add(Touchdown, "Centreline", cl < 3 ? "On the centreline" : $"{cl:F0} ft {(r.CenterlineDeviationFt > 0 ? "right" : "left")}",
                $"Within {L.CentrelineCautionFt:F0} ft",
                cl >= L.CentrelineWarningFt ? CheckStatus.Critical : cl >= L.CentrelineCautionFt ? CheckStatus.Caution : CheckStatus.Pass,
                cl >= L.CentrelineCautionFt ? "Off the centreline at touchdown. Track the centreline from the threshold and use the rudder to stay on it in the flare." : "");
        }
        else
        {
            Add(Touchdown, "Centreline", "Not measured", $"Within {L.CentrelineCautionFt:F0} ft", CheckStatus.NotMeasured,
                "This runway isn't in the tracker's runway database, so the offset couldn't be measured.");
        }

        if (rep.TouchdownDistanceFt is { } td)
        {
            var est = rep.TouchdownDistanceEstimated;
            var status = td > L.LongWarningFt ? CheckStatus.Critical
                : td > L.LongCautionFt ? CheckStatus.Caution
                : !est && td < L.ShortWarningFt ? CheckStatus.Critical
                : CheckStatus.Pass;
            Add(Touchdown, "Touchdown point", $"{td:N0} ft past the threshold{(est ? " (est.)" : "")}",
                $"{L.ShortWarningFt:F0}-{L.LongCautionFt:N0} ft", status,
                status == CheckStatus.Pass ? "" : td > L.LongCautionFt
                    ? "Long landing: less runway left to stop. Aim for the touchdown markers and don't hold off in the flare."
                    : "Short landing: touching down this close to the threshold leaves no margin for a low approach.");
        }
        else
        {
            Add(Touchdown, "Touchdown point", "Not measured", $"{L.ShortWarningFt:F0}-{L.LongCautionFt:N0} ft", CheckStatus.NotMeasured);
        }

        Add(Touchdown, "Flare", r.FlareDetected ? "Flared" : "No flare", "Rate of descent reduced before touchdown",
            r.FlareDetected ? CheckStatus.Pass : CheckStatus.Caution,
            r.FlareDetected ? "" : "The sink rate wasn't reduced before the wheels touched. Start a gentle flare at around 20-30 ft.");

        Add(Touchdown, "Bounces", r.BounceCount == 0 ? "None" : $"{r.BounceCount}", "None",
            r.BounceCount >= 2 ? CheckStatus.Critical : r.BounceCount == 1 ? CheckStatus.Caution : CheckStatus.Pass,
            r.BounceCount > 0 ? "After a bounce, hold the attitude and let it settle, or go around if it's a big one. Don't push the nose down." : "");

        if (rep.CrabAngleDeg is { } crab)
            Add(Touchdown, "Crab at touchdown", $"{Math.Abs(crab):F0}°", $"Below {L.CrabCautionDeg:F0}°",
                Math.Abs(crab) >= L.CrabCautionDeg ? CheckStatus.Caution : CheckStatus.Pass,
                Math.Abs(crab) >= L.CrabCautionDeg ? "Landed with the nose pointing off the runway heading. Kick off the drift with rudder just before touchdown." : "");

        // ── Rollout and wind ──────────────────────────────────────
        const string Rollout = "Rollout and wind";
        if (rep.RolloutFt > 0)
            Add(Rollout, "Rollout", $"{rep.RolloutFt:N0} ft, {rep.DecelerationKtPerSec:F1} kt/s", "Information only", CheckStatus.Pass);

        var tail = -rep.HeadwindKts;
        Add(Rollout, "Wind component",
            $"{(rep.HeadwindKts >= 0 ? $"{rep.HeadwindKts:F0} kt head" : $"{tail:F0} kt tail")}, {rep.CrosswindKts:F0} kt cross",
            $"Tailwind < {L.TailwindWarningKts:F0} kt, crosswind < {L.CrosswindCautionKts:F0} kt",
            tail >= L.TailwindWarningKts ? CheckStatus.Critical
            : rep.CrosswindKts >= L.CrosswindCautionKts ? CheckStatus.Caution : CheckStatus.Pass,
            tail >= L.TailwindWarningKts ? "Landing with this much tailwind is outside normal limits. Use the other runway direction."
            : rep.CrosswindKts >= L.CrosswindCautionKts ? "Strong crosswind. Check the aircraft's demonstrated crosswind before accepting the runway." : "");

        // ── Rating ────────────────────────────────────────────────
        a.CriticalCount = checks.Count(c => c.Status == CheckStatus.Critical);
        a.CautionCount = checks.Count(c => c.Status == CheckStatus.Caution);
        (a.Rating, a.RatingColor) = (a.CriticalCount, a.CautionCount) switch
        {
            ( > 0, _) => ("Critical", "#EF4444"),
            (0, > 2) => ("Needs work", "#F59E0B"),
            (0, > 0) => ("Good", "#4D9BEC"),
            _ => rep.Score >= 85 ? ("Excellent", "#3EC7AC") : ("Good", "#4D9BEC"),
        };

        var worst = checks.Where(c => c.Status == CheckStatus.Critical).Select(c => c.Name.ToLowerInvariant())
            .Concat(checks.Where(c => c.Status == CheckStatus.Caution).Select(c => c.Name.ToLowerInvariant()))
            .Take(3).ToList();
        a.Summary = (a.CriticalCount, a.CautionCount) switch
        {
            (0, 0) => "Every check passed. A by-the-book approach and landing.",
            ( > 0, _) => $"{Count(a.CriticalCount, "critical finding")}{(a.CautionCount > 0 ? $" and {Count(a.CautionCount, "caution")}" : "")}. Start with: {string.Join(", ", worst)}.",
            _ => $"{Count(a.CautionCount, "caution")}. Worth tidying up: {string.Join(", ", worst)}.",
        };

        a.GearDownAglFt = before.Where(p => p.GearDown).Select(p => (double?)p.AglFt).DefaultIfEmpty(null).Max();
        var firstLandingFlap = before.FirstOrDefault(p => p.FlapsPct >= flapTarget);
        a.LandingFlapsAglFt = firstLandingFlap?.AglFt;

        return a;
    }

    /// <summary>
    /// Largest angular deviation from a 3° path to the touchdown point between 1,000 and 200 ft
    /// AGL (+ = high). Null when the trace has no positions to measure distance from.
    /// </summary>
    internal static (double Deg, double Agl)? GlidepathDeviation(IReadOnlyList<LandingReportPoint> before)
    {
        (double Deg, double Agl)? worst = null;
        foreach (var p in before)
        {
            if (p.AglFt is < 200 or > 1000) continue;
            var distFt = -p.DistNm * 6076.12;
            if (distFt < 1500) continue; // no positions recorded, or too close to the aim point to judge
            var angle = Math.Atan2(p.AglFt, distFt) * 180 / Math.PI;
            var dev = angle - ReferencePathDeg;
            if (worst is null || Math.Abs(dev) > Math.Abs(worst.Value.Deg))
                worst = (Math.Round(dev, 2), p.AglFt);
        }
        return worst;
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
