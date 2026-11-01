using AviatesAirTracker.Core.Efb;
using System.Linq;
using Xunit;

namespace AviatesAirTracker.Tests;

public class EfbTakeoffPerformanceTests
{
    // A320neo at 60.6 t on a 2,100 m sea-level runway — the Fenix EFB's own demo case.
    private static TakeoffPerfRequest A320(double weightLbs = 133_600, double rwyM = 2100, string flap = "Config 1+F") => new()
    {
        AircraftCode = "A20N", WeightLbs = weightLbs, FlapSetting = flap,
        RunwayLengthM = rwyM, RunwayElevationFt = 120, RunwayHeadingDeg = 280,
        WindDirDeg = 280, WindKts = 0, OatC = 16, QnhMb = 1015,
    };

    [Fact]
    public void LongRunway_TypicalWeight_GivesFlexWithinTheAssumedTempWindow()
    {
        var r = EfbPerformanceCalculator.CalculateTakeoff(A320());
        Assert.Equal("FLEX", r.Thrust);
        Assert.NotNull(r.FlexTempC);
        Assert.InRange(r.FlexTempC!.Value, r.MinFlexTempC, r.MaxFlexTempC);
        Assert.True(r.RequiredRunwayM <= r.AvailableRunwayM);
    }

    [Fact]
    public void ForceToga_SkipsFlex()
    {
        var req = A320(); req.ForceToga = true;
        var r = EfbPerformanceCalculator.CalculateTakeoff(req);
        Assert.Equal("TOGA", r.Thrust);
        Assert.Null(r.FlexTempC);
        Assert.Equal("TOGA", r.LimitCode);
    }

    [Fact]
    public void RunwayTooShortAtFullThrust_IsTogaAndUnsafe()
    {
        var r = EfbPerformanceCalculator.CalculateTakeoff(A320(weightLbs: 170_000, rwyM: 1200));
        Assert.Null(r.FlexTempC);
        Assert.Equal("UNSAFE", r.Status);
    }

    [Fact]
    public void HeavierAircraft_NeverGetsAHigherFlex()
    {
        var light = EfbPerformanceCalculator.CalculateTakeoff(A320(weightLbs: 125_000, rwyM: 1800));
        var heavy = EfbPerformanceCalculator.CalculateTakeoff(A320(weightLbs: 160_000, rwyM: 1800));
        Assert.True((heavy.FlexTempC ?? int.MinValue) <= (light.FlexTempC ?? int.MinValue));
    }

    [Fact]
    public void TakeoffShift_ReducesAvailableRunwayAndFlex()
    {
        var full = EfbPerformanceCalculator.CalculateTakeoff(A320(weightLbs: 150_000, rwyM: 2000));
        var req = A320(weightLbs: 150_000, rwyM: 2000); req.TakeoffShiftM = 600;
        var shifted = EfbPerformanceCalculator.CalculateTakeoff(req);
        Assert.Equal(1400, shifted.AvailableRunwayM);
        Assert.True((shifted.FlexTempC ?? int.MinValue) <= (full.FlexTempC ?? int.MinValue));
    }

    [Fact]
    public void OptimumConfig_IsAtLeastAsGoodAsEveryDetent()
    {
        var opt = EfbPerformanceCalculator.CalculateTakeoff(A320(weightLbs: 160_000, rwyM: 1900, flap: "OPT"));
        Assert.True(opt.OptimumFlapChosen);
        foreach (var f in AircraftPerformanceProfiles.Find("A20N")!.TakeoffFlaps)
        {
            var each = EfbPerformanceCalculator.CalculateTakeoff(A320(weightLbs: 160_000, rwyM: 1900, flap: f.Label));
            Assert.True((opt.FlexTempC ?? int.MinValue) >= (each.FlexTempC ?? int.MinValue));
        }
    }

    [Theory]
    [InlineData(29.5, "DN0.2")]
    [InlineData(25.0, "UP0.7")]
    [InlineData(28.5, "UP0.0")]
    public void Ths_FromCg(double cg, string expected)
    {
        var req = A320(); req.CgPct = cg;
        Assert.Equal(expected, EfbPerformanceCalculator.CalculateTakeoff(req).Ths);
    }

    [Fact]
    public void Airbus_GetsClimbOutSpeedsInOrder_BoeingDoesNot()
    {
        var a = EfbPerformanceCalculator.CalculateTakeoff(A320());
        Assert.True(a.V2 < a.FlapRetractKts && a.FlapRetractKts < a.SlatRetractKts && a.SlatRetractKts < a.GreenDotKts);

        var req = A320(); req.AircraftCode = "B738"; req.FlapSetting = "Flaps 5";
        var b = EfbPerformanceCalculator.CalculateTakeoff(req);
        Assert.Equal(0, b.GreenDotKts);
        Assert.InRange(b.StabTrimUnits, 1, 8.5);
    }

    [Fact]
    public void A320GreenDot_FollowsTheWeightRule_NotTheFlexInflatedV2()
    {
        // 60.6 t → O = 2·60.6 + 85 ≈ 206 kt regardless of FLEX (Fenix demo: 144 / 188 / 206)
        var flex = EfbPerformanceCalculator.CalculateTakeoff(A320());
        var req = A320(); req.ForceToga = true;
        var toga = EfbPerformanceCalculator.CalculateTakeoff(req);
        Assert.Equal(206, flex.GreenDotKts);
        Assert.Equal(flex.GreenDotKts, toga.GreenDotKts);
        Assert.Equal(188, flex.SlatRetractKts);
    }

    [Fact]
    public void BleedsCostDistance()
    {
        var off = A320(weightLbs: 150_000); off.PacksOn = false;
        var ice = A320(weightLbs: 150_000); ice.AntiIce = "ENG+WING";
        Assert.True(EfbPerformanceCalculator.CalculateTakeoff(off).RequiredRunwayM
                  < EfbPerformanceCalculator.CalculateTakeoff(ice).RequiredRunwayM);
    }
}

public class WeightBalanceAliasTests
{
    [Fact]
    public void IcaoDesignator_A20N_UsesTheA320neoProfile()
    {
        var byName = AviatesAirTracker.Core.Analytics.WeightBalanceDatabase.Compute("A320neo", 58_100, 64_200)!;
        var byIcao = AviatesAirTracker.Core.Analytics.WeightBalanceDatabase.Compute("A20N", 58_100, 64_200)!;
        Assert.Equal(byName.MacTowPercent, byIcao.MacTowPercent);
    }

    [Fact]
    public void B738_IsNotGuessedFromTheMax8Profile()
    {
        Assert.Null(AviatesAirTracker.Core.Analytics.WeightBalanceDatabase.Compute("B738", 60_000, 70_000));
    }
}

public class EfbAirportDataTests
{
    private const string Egll = """
        [{"icaoId":"EGLL","name":"LONDON/HEATHROW AIRPORT","lat":51.4719,"lon":-0.4638,"elev":26,"magdec":"0",
          "runways":[{"id":"09L/27R","dimension":"12802x164","surface":"H","alignment":89},
                     {"id":"09R/27L","dimension":"12008x164","surface":"H","alignment":89},
                     {"id":"H1","dimension":"60x60","surface":"C","alignment":null}]}]
        """;

    [Fact]
    public void SplitsEachStripIntoBothTakeoffDirections_AndSkipsHelipads()
    {
        var a = EfbAirportData.Parse(Egll)!;
        Assert.Equal(new[] { "09L", "27R", "09R", "27L" }, a.Runways.Select(r => r.Ident).ToArray());
        var r27r = a.Runways.Single(r => r.Ident == "27R");
        Assert.Equal(269, r27r.HeadingTrueDeg);
        Assert.Equal(3902, r27r.LengthM);          // 12,802 ft
        Assert.Equal(50, r27r.WidthM);             // 164 ft
        Assert.Equal("09L/27R", r27r.Strip);
    }

    [Fact]
    public void ElevationIsConvertedFromMetres()
    {
        Assert.Equal(85, EfbAirportData.Parse(Egll)!.ElevationFt);
    }

    [Theory]
    [InlineData("08E", 8)]
    [InlineData("2W", -2)]
    [InlineData("0", 0)]
    public void MagneticVariation(string magdec, double expected)
    {
        var json = $$"""[{"icaoId":"TEST","elev":0,"magdec":"{{magdec}}","runways":[]}]""";
        Assert.Equal(expected, EfbAirportData.Parse(json)!.MagVarDeg);
    }

    [Fact]
    public void UnknownAirport_IsNull()
    {
        Assert.Null(EfbAirportData.Parse("[]"));
        Assert.Null(EfbAirportData.Parse("not json"));
    }
}
