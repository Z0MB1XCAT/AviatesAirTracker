using AviatesAirTracker.Core.Efb;
using System.Linq;
using Xunit;

namespace AviatesAirTracker.Tests;

/// <summary>Every AviatesAir fleet type has its own performance data, is found by the names
/// SimBrief and add-ons use, and never borrows another type's numbers.</summary>
public class EfbFleetPerformanceTests
{
    // The twelve SimBrief designators SimBriefService maps the fleet to.
    public static TheoryData<string> Fleet => new() { "A319", "A20N", "A359", "A388", "AT76", "AT45", "B736", "B738", "B38M", "B739", "B748", "B77F" };

    [Theory]
    [MemberData(nameof(Fleet))]
    public void EveryFleetTypeHasItsOwnProfile(string code)
    {
        var p = AircraftPerformanceProfiles.Find(code);
        Assert.NotNull(p);
        Assert.Equal(code, p!.Code);
        Assert.True(p.Fleet);
        Assert.True(p.MlwLbs < p.MtowLbs && p.OewLbs < p.MlwLbs, $"{code}: weights out of order");
        Assert.True(p.V1Ref <= p.VrRef && p.VrRef <= p.V2Ref, $"{code}: V-speeds out of order");
    }

    [Theory]
    [InlineData("A350", "A359")]
    [InlineData("Airbus A380-842", "A388")]
    [InlineData("PMDG 737-800 BW", "B738")]
    [InlineData("737 MAX 8", "B38M")]
    [InlineData("B739ER", "B739")]
    [InlineData("ATR 42-600", "AT45")]
    [InlineData("ATR72", "AT76")]
    [InlineData("Boeing 777F", "B77F")]
    [InlineData("747-8F", "B748")]
    [InlineData("a20n", "A20N")]
    public void AliasesAndAddOnTitlesResolve(string name, string expected) =>
        Assert.Equal(expected, AircraftPerformanceProfiles.Find(name)?.Code);

    [Theory]
    [InlineData("C172")]
    [InlineData("A333")]
    [InlineData("")]
    public void UnknownTypesGetNoData_NotAnotherTypesNumbers(string name) =>
        Assert.Null(AircraftPerformanceProfiles.Find(name));

    [Theory]
    [MemberData(nameof(Fleet))]
    public void TakeoffAtMtowOnALongRunway_IsSaneForEveryType(string code)
    {
        var p = AircraftPerformanceProfiles.Find(code)!;
        var r = EfbPerformanceCalculator.CalculateTakeoff(new TakeoffPerfRequest
        {
            AircraftCode = code, WeightLbs = p.MtowLbs, FlapSetting = "OPT", RunwayLengthM = 4000,
            RunwayElevationFt = 100, RunwayHeadingDeg = 270, WindDirDeg = 270, WindKts = 0, OatC = 15, QnhMb = 1013,
        });
        Assert.True(r.Ok, string.Join("; ", r.Warnings));
        Assert.InRange(r.V2, 95, 200);
        Assert.True(r.V1 <= r.Vr && r.Vr <= r.V2);
        Assert.True(r.RequiredRunwayM <= 4000, $"{code} needs {r.RequiredRunwayM} m at MTOW");
    }

    [Fact]
    public void TheA380NeedsMoreRunwayThanTheA319()
    {
        double Req(string c) => EfbPerformanceCalculator.CalculateTakeoff(new TakeoffPerfRequest
        {
            AircraftCode = c, WeightLbs = AircraftPerformanceProfiles.Find(c)!.MtowLbs, FlapSetting = "Config 2", RunwayLengthM = 4000,
            RunwayElevationFt = 100, RunwayHeadingDeg = 270, WindDirDeg = 270, OatC = 15, QnhMb = 1013, ForceToga = true,
        }).RequiredRunwayM;
        Assert.True(Req("A388") > Req("A319"));
    }

    [Fact]
    public void FailureListsFollowEachFamilysSystems()
    {
        var b737 = EfbLandingFailures.ForAircraft("B738").Select(f => f.Id).ToList();
        Assert.Contains("b737-hyd-b", b737);
        Assert.DoesNotContain("hyd-g", b737);               // no Airbus green system on a 737

        var a350 = EfbLandingFailures.ForAircraft("A359").Select(f => f.Id).ToList();
        Assert.Contains("hyd-gy", a350);
        Assert.DoesNotContain("hyd-gb", a350);              // the A350 has no blue system

        var atr = EfbLandingFailures.ForAircraft("AT45").Select(f => f.Id).ToList();
        Assert.Contains("atr-hyd-g", atr);
        Assert.DoesNotContain("brk-autobrk", atr);          // no autobrake fitted
        Assert.DoesNotContain("eng-rev", atr);

        Assert.Contains("b777-hyd-r", EfbLandingFailures.ForAircraft("B77F").Select(f => f.Id));
        Assert.Contains("b747-hyd-4", EfbLandingFailures.ForAircraft("B748").Select(f => f.Id));
    }

    private static LandingPerfRequest Landing(string code, string brake = "MED", params string[] failures) => new()
    {
        AircraftCode = code, WeightLbs = AircraftPerformanceProfiles.Find(code)!.MlwLbs * 0.9, RunwayLengthM = 3500,
        RunwayElevationFt = 100, RunwayHeadingDeg = 270, WindDirDeg = 270, WindKts = 0, OatC = 15, QnhMb = 1013,
        RunwayCondition = 6, Autobrake = brake, Failures = failures.ToList(),
    };

    [Fact]
    public void AFailureFromAnotherFamilyIsIgnored()
    {
        var r = EfbPerformanceCalculator.CalculateLanding(Landing("B738", "MED", "hyd-g"));
        Assert.Empty(r.AppliedFailures);
        Assert.Equal(1.0, r.FailureFactor);
    }

    [Fact]
    public void Loss737SystemB_ForcesManualBraking()
    {
        var r = EfbPerformanceCalculator.CalculateLanding(Landing("B738", "MED", "b737-hyd-b"));
        Assert.Equal("MANUAL", r.Autobrake);
        Assert.Equal(new[] { "b737-hyd-b" }, r.AppliedFailures);
    }

    [Fact]
    public void TheAtrAlwaysBrakesManually_WithoutAnAutobrakeWarning()
    {
        var r = EfbPerformanceCalculator.CalculateLanding(Landing("AT76", "MED"));
        Assert.Equal("MANUAL", r.Autobrake);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("Autobrake"));
        Assert.InRange(r.Vref, 90, 125);
    }
}
