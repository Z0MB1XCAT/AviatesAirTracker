using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Models;

namespace AviatesAirTracker.Tests;

public class LandingReportFlightDataTests
{
    private const double Tan3Ft = 318.44;   // ft of height per NM on a 3° path

    /// <summary>
    /// A straight-in 3° approach flown northbound at 140 kt from ~1490 ft (2 min out) to a touchdown at
    /// T=0 (AGL 0 exactly at the touchdown point), then a 20 s rollout.
    /// </summary>
    private static List<LandingTracePoint> Approach(Func<double, LandingTracePoint, LandingTracePoint>? tweak = null)
    {
        var pts = new List<LandingTracePoint>();
        const double gsNmPerS = 140.0 / 3600;
        for (int t = -120; t <= 20; t++)
        {
            double d = t <= 0 ? t * gsNmPerS : t * gsNmPerS * 0.4;           // slower on the rollout
            var p = new LandingTracePoint
            {
                T = t,
                Lat = 47 + d / 60.0,
                Lon = 8.5,
                AglFt = t < 0 ? Math.Round(-d * Tan3Ft) : 0,
                IasKts = t <= 0 ? 138 : 138 - t * 5,
                GsKts = t <= 0 ? 140 : 140 - t * 5,
                VsFpm = t < -3 ? -740 : t < 0 ? -160 : 0,
                PitchDeg = 2.5,
                BankDeg = 0.5,
                FlapsPct = 100,
                GearDown = true,
                OnGround = t >= 0,
            };
            pts.Add(tweak?.Invoke(t, p) ?? p);
        }
        return pts;
    }

    private static LandingResult Landing(double vs = -180) => new()
    {
        Timestamp = DateTime.UtcNow,
        AirportICAO = "LSZH",
        RunwayIdentifier = "34",
        VerticalSpeedFPM = vs,
        TouchdownPitchDeg = 2.5,
        TouchdownBankDeg = 0.5,
        FlareDetected = true,
        ApproachWasStable = true,
        LandingScore = 90,
    };

    [Fact]
    public void Trace_GetsAlongTrackDistance_ZeroAtTouchdown()
    {
        var rep = LandingReportBuilder.Build(Landing(), trace: Approach());
        Assert.Equal(141, rep.Trace.Count);
        Assert.Equal(0, rep.Trace.Single(p => p.T == 0).DistNm, 3);
        Assert.True(rep.Trace.First().DistNm < -4.5);                // 120 s at 140 kt ≈ 4.7 NM
        Assert.True(rep.Trace.Last().DistNm > 0);
        Assert.True(rep.Trace.Zip(rep.Trace.Skip(1)).All(z => z.First.DistNm <= z.Second.DistNm));
    }

    [Fact]
    public void TouchdownDistance_IsEstimatedFromThe50FtCrossing_WhenTheRunwayIsUnknown()
    {
        var rep = LandingReportBuilder.Build(Landing(), trace: Approach());
        Assert.True(rep.TouchdownDistanceEstimated);
        // 50 ft on a 3° path is 50/318.44 NM = ~954 ft before the touchdown point
        Assert.InRange(rep.TouchdownDistanceFt!.Value, 930, 980);
    }

    [Fact]
    public void DetectedThresholdDistance_WinsOverTheEstimate()
    {
        var l = Landing(); l.ThresholdDistanceFt = 1400;
        var rep = LandingReportBuilder.Build(l, trace: Approach());
        Assert.False(rep.TouchdownDistanceEstimated);
        Assert.Equal(1400, rep.TouchdownDistanceFt);
    }

    [Fact]
    public void StableApproach_PassesBothGates_AndRaisesNoEvents()
    {
        var rep = LandingReportBuilder.Build(Landing(), trace: Approach());
        Assert.Equal([1000, 500], rep.Gates.Select(g => g.AglFt));
        Assert.All(rep.Gates, g => { Assert.True(g.Recorded); Assert.True(g.Stable); });
        Assert.Empty(rep.Events);
        Assert.Equal(740, rep.MaxSinkBelow1000Fpm);
    }

    [Fact]
    public void GearUpAt1000_IsACaution_FlapsShortAt500_IsAWarning()
    {
        var trace = Approach((t, p) =>
        {
            if (p.AglFt > 500) p.GearDown = false;          // gear came down late, between the gates
            if (p.AglFt is <= 600 and > 300) p.FlapsPct = 40;
            return p;
        });
        var rep = LandingReportBuilder.Build(Landing(), trace: trace);
        Assert.False(rep.Gates[0].ConfigOk);
        Assert.False(rep.Gates[1].ConfigOk);
        var ev = Assert.Single(rep.Events, e => e.Code == "UNSTAB");
        Assert.Equal("warning", ev.Severity);                // a 500 ft gate failure is the serious one
        Assert.Contains("gear not down", ev.Detail);
        Assert.Contains("flaps 40%", ev.Detail);
    }

    [Theory]
    [InlineData(-650, "caution")]
    [InlineData(-850, "warning")]
    public void HardLanding_IsGradedByTheFdmLimits(double vs, string severity)
    {
        var rep = LandingReportBuilder.Build(Landing(vs), trace: Approach());
        var ev = rep.Events.First();                         // warnings sort first
        Assert.Equal("HARD", ev.Code);
        Assert.Equal(severity, ev.Severity);
    }

    [Fact]
    public void EventsCover_Pitch_Bank_Bounce_Long_Tailwind_Crab()
    {
        var l = Landing();
        l.TouchdownPitchDeg = 9.4;
        l.TouchdownBankDeg = -5.5;
        l.BounceCount = 1;
        l.ThresholdDistanceFt = 3300;
        l.HeadwindComponent = -12;
        l.TouchdownHeadingTrueDeg = 355;
        l.TouchdownTrackTrueDeg = 5;
        var rep = LandingReportBuilder.Build(l, trace: Approach());
        var codes = rep.Events.ToDictionary(e => e.Code, e => e.Severity);
        Assert.Equal("warning", codes["PITCH"]);
        Assert.Equal("warning", codes["BANK"]);
        Assert.Equal("caution", codes["BOUNCE"]);
        Assert.Equal("warning", codes["LONG"]);
        Assert.Equal("warning", codes["TAILWIND"]);
        Assert.Equal("caution", codes["CRAB"]);
        Assert.Equal(-10, rep.CrabAngleDeg);                 // wraps across north
        Assert.True(rep.Events.TakeWhile(e => e.Severity == "warning").Count() == 4);
    }

    [Fact]
    public void WithoutATrace_GatesAreNotRecorded_AndNothingIsInvented()
    {
        var l = Landing(); l.ApproachWasStable = false;
        var rep = LandingReportBuilder.Build(l);
        Assert.Empty(rep.Trace);
        Assert.All(rep.Gates, g => Assert.False(g.Recorded));
        Assert.Null(rep.TouchdownDistanceFt);
        Assert.Null(rep.CrabAngleDeg);
        Assert.Equal("caution", rep.Events.Single(e => e.Code == "UNSTAB").Severity);
    }
}
