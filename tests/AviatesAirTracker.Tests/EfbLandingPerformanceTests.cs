using AviatesAirTracker.Core.Efb;
using System.Linq;
using Xunit;

namespace AviatesAirTracker.Tests;

public class EfbLandingPerformanceTests
{
    // A320neo at 60 t onto a 3,000 m sea-level runway, calm, 15 °C.
    private static LandingPerfRequest A320(int rwycc = 6, string brake = "MED") => new()
    {
        AircraftCode = "A20N", WeightLbs = 132_000, FlapSetting = "Config Full", RunwayLengthM = 3000,
        RunwayElevationFt = 100, RunwayHeadingDeg = 140, WindDirDeg = 140, WindKts = 0, OatC = 15, QnhMb = 1013,
        RunwayCondition = rwycc, Autobrake = brake,
    };
    private static LandingPerfResult Calc(LandingPerfRequest r) => EfbPerformanceCalculator.CalculateLanding(r);

    [Fact]
    public void DryMediumAutobrake_FitsWithTheOperationalMargin()
    {
        var r = Calc(A320());
        Assert.Equal("OK", r.Status);
        Assert.InRange(r.OperationalDistanceM, (int)(r.ActualDistanceM * 1.15) - 1, (int)(r.ActualDistanceM * 1.15) + 1);
        Assert.True(r.OperationalDistanceM < r.RunwayLengthM);
    }

    [Fact]
    public void WorseRunwayCondition_NeverShortensTheStop()
    {
        var prev = 0;
        for (int cc = 6; cc >= 1; cc--)
        {
            var d = Calc(A320(cc)).ActualDistanceM;
            Assert.True(d >= prev, $"RWYCC {cc}: {d} < {prev}");
            prev = d;
        }
    }

    [Fact]
    public void OnADryRunway_AutobrakeSettingsAreDecelerationLimited()
    {
        var b = Calc(A320()).DistanceByBrake;
        Assert.True(b["LOW"] > b["MED"] && b["MED"] > b["MAX"] && b["MAX"] > b["MANUAL"]);
    }

    [Fact]
    public void OnAPoorRunway_FrictionLimitsEveryBrakingMode()
    {
        var b = Calc(A320(rwycc: 1)).DistanceByBrake;
        Assert.Single(b.Values.Distinct());
    }

    [Fact]
    public void MaxReverse_HelpsMostWhenFrictionIsLow()
    {
        int Gain(int cc)
        {
            var none = A320(cc); none.Reverser = "NONE";
            var max = A320(cc); max.Reverser = "MAX";
            return Calc(none).ActualDistanceM - Calc(max).ActualDistanceM;
        }
        Assert.True(Gain(6) > 0);
        Assert.True(Gain(2) > Gain(6));
    }

    [Fact]
    public void Failures_FactorsMultiply_AndTheLargestVrefIncrementApplies()
    {
        var clean = Calc(A320());
        var req = A320(); req.Failures = ["flap-jam", "hyd-g"];
        var f = Calc(req);
        Assert.Equal(25, f.DeltaVrefKts);
        Assert.Equal(clean.Vref + 25, f.Vref);
        Assert.Equal(System.Math.Round(1.35 * 1.25, 2), f.FailureFactor);
        Assert.Equal("CAUTION", f.Status);
        Assert.Equal(new[] { "flap-jam", "hyd-g" }, f.AppliedFailures);
    }

    [Fact]
    public void AntiSkidFailure_ForcesManualBraking()
    {
        var req = A320(brake: "LOW"); req.Failures = ["brk-antiskid"];
        var r = Calc(req);
        Assert.Equal("MANUAL", r.Autobrake);
        Assert.Equal(new[] { "MANUAL" }, r.DistanceByBrake.Keys.ToArray());
        Assert.Contains(r.Warnings, w => w.Contains("manual braking"));
    }

    [Theory]
    [InlineData(0, true, 5)]      // calm, A/THR ON → +5
    [InlineData(0, false, 0)]     // calm, A/THR OFF → no correction
    [InlineData(30, true, 10)]    // ⅓ of 30 kt headwind
    [InlineData(60, true, 15)]    // limited to 15 kt
    public void AirbusApproachCorrection_FollowsTheFcomRule(int headwind, bool athr, int expected)
    {
        var req = A320(); req.WindKts = headwind; req.AutoThrust = athr;
        var r = Calc(req);
        Assert.Equal(expected, r.WindCorrectionKts);
        Assert.Equal(r.Vref + expected, r.Vapp);
    }

    [Fact]
    public void BoeingWindAdditive_IsAtLeastFiveKnots()
    {
        var req = A320(); req.AircraftCode = "B738"; req.FlapSetting = "Flaps 30";
        Assert.Equal(5, Calc(req).WindCorrectionKts);
    }

    [Fact]
    public void RunwayShorterThanLdr_IsUnsafe()
    {
        var req = A320(rwycc: 2); req.RunwayLengthM = 1200;
        Assert.Equal("UNSAFE", Calc(req).Status);
    }
}
