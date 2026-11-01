using System.Xml.Linq;
using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Models;
using AviatesAirTracker.Platform;
using AviatesAirTracker.Services;
using AviatesAirTracker.Shell;

namespace AviatesAirTracker.Tests;

public class WindowPlacementLogicTests
{
    private static readonly PixelRect Laptop = new(0, 0, 1366, 728);   // 1366×768 minus taskbar
    private static readonly PixelRect Desktop = new(0, 0, 2560, 1400);

    [Fact]
    public void First_launch_on_a_small_screen_fits_inside_it()
    {
        var r = WindowPlacementLogic.Fit(null, Laptop);
        Assert.True(r.Width <= Laptop.Width && r.Height <= Laptop.Height);
        Assert.True(r.X >= 0 && r.Y >= 0);
    }

    [Fact]
    public void First_launch_on_a_big_screen_is_the_default_size_centred()
    {
        var r = WindowPlacementLogic.Fit(null, Desktop);
        Assert.Equal((1440, 900), (r.Width, r.Height));
        Assert.Equal((2560 - 1440) / 2, r.X);
    }

    [Fact]
    public void Saved_bounds_that_fit_are_kept()
    {
        var saved = new PixelRect(100, 80, 1500, 1000);
        Assert.Equal(saved, WindowPlacementLogic.Fit(saved, Desktop));
    }

    [Fact]
    public void Saved_bounds_off_screen_are_pulled_back_on()
    {
        var r = WindowPlacementLogic.Fit(new PixelRect(3000, 900, 1500, 1000), Desktop);
        Assert.True(r.Right <= Desktop.Right && r.Bottom <= Desktop.Bottom);
    }
}

public class SimExeXmlTests
{
    private const string Exe = @"C:\Program Files\AviatesAir\AviatesAirTracker.exe";

    private static XDocument WithOtherAddon() => XDocument.Parse("""
        <?xml version="1.0" encoding="Windows-1252"?>
        <SimBase.Document Type="SimConnect" version="1,0">
          <Descr>Auto launch</Descr>
          <Launch.Addon><Name>FSUIPC7</Name><Disabled>False</Disabled><Path>C:\FSUIPC7\FSUIPC7.exe</Path></Launch.Addon>
        </SimBase.Document>
        """);

    [Fact]
    public void Adding_keeps_other_addons_and_appends_ours_once()
    {
        XDocument? doc = WithOtherAddon();
        Assert.True(StartupLaunchService.WithAddon(ref doc, Exe));
        Assert.False(StartupLaunchService.WithAddon(ref doc, Exe)); // already there: no change

        var addons = doc!.Root!.Elements("Launch.Addon").ToList();
        Assert.Equal(2, addons.Count);
        Assert.Equal("FSUIPC7", (string?)addons[0].Element("Name"));
        Assert.Equal(Exe, (string?)addons[1].Element("Path"));
        Assert.Equal(StartupLaunchService.BackgroundArg, (string?)addons[1].Element("CommandLine"));
    }

    [Fact]
    public void Adding_with_no_file_creates_a_valid_document()
    {
        XDocument? doc = null;
        Assert.True(StartupLaunchService.WithAddon(ref doc, Exe));
        Assert.Equal("SimBase.Document", doc!.Root!.Name.LocalName);
        Assert.Single(doc.Root.Elements("Launch.Addon"));
    }

    [Fact]
    public void A_moved_exe_updates_the_existing_entry_in_place()
    {
        XDocument? doc = WithOtherAddon();
        StartupLaunchService.WithAddon(ref doc, @"C:\Old\AviatesAirTracker.exe");
        Assert.True(StartupLaunchService.WithAddon(ref doc, Exe));
        Assert.Equal(2, doc!.Root!.Elements("Launch.Addon").Count());
    }

    [Fact]
    public void Removing_takes_out_only_ours()
    {
        XDocument? doc = WithOtherAddon();
        StartupLaunchService.WithAddon(ref doc, Exe);
        Assert.True(StartupLaunchService.WithoutAddon(doc!));
        Assert.Equal("FSUIPC7", (string?)Assert.Single(doc!.Root!.Elements("Launch.Addon")).Element("Name"));
        Assert.False(StartupLaunchService.WithoutAddon(doc!));
    }
}

public class PreflightReadinessTests
{
    private static ReadinessInputs Ready() => new(
        SimConnected: true, BookingActivated: true, BookingOrigin: "EGLL", BookingDestination: "LFPG",
        BookingAircraftType: "A20N", PlanOrigin: "EGLL", PlanDestination: "LFPG",
        PlanOriginLat: 51.4700, PlanOriginLon: -0.4543, AircraftLat: 51.4710, AircraftLon: -0.4600,
        OnGround: true, EnginesRunning: false, SimAircraftTitle: "FlyByWire A320neo", SimAircraftModel: "A20N");

    [Fact]
    public void Everything_in_order_is_all_ready()
    {
        var items = PreflightReadiness.Evaluate(Ready());
        Assert.All(items, i => Assert.Equal(ReadinessState.Ready, i.State));
    }

    [Fact]
    public void No_activated_booking_blocks()
    {
        var items = PreflightReadiness.Evaluate(Ready() with { BookingActivated = false });
        Assert.Contains(items, i => i.Title == "Booking activated" && i.State == ReadinessState.Blocked);
    }

    [Fact]
    public void Aircraft_at_the_wrong_airport_is_flagged_with_the_distance()
    {
        var item = PreflightReadiness.Evaluate(Ready() with { AircraftLat = 53.35, AircraftLon = -2.27 })
            .Single(i => i.Title == "At the departure airport");
        Assert.Equal(ReadinessState.Warning, item.State);
        Assert.Contains("nm from EGLL", item.Detail);
    }

    [Fact]
    public void A_plan_for_a_different_route_is_flagged()
    {
        var item = PreflightReadiness.Evaluate(Ready() with { PlanOrigin = "EGKK" }).Single(i => i.Title == "Flight plan");
        Assert.Equal(ReadinessState.Warning, item.State);
    }

    [Fact]
    public void Airborne_blocks_tracking()
    {
        Assert.Contains(PreflightReadiness.Evaluate(Ready() with { OnGround = false }),
            i => i.State == ReadinessState.Blocked && i.Title == "On the ground");
    }

    [Theory]
    [InlineData("A20N", "FlyByWire A320neo", "A20N", true)]
    [InlineData("A320", "Airbus A320 Neo", null, true)]
    [InlineData("B738", "PMDG 737-800", "B738", true)]
    [InlineData("B77W", "Boeing 777-300ER", null, true)]
    [InlineData("A20N", "PMDG 737-800", "B738", false)]
    [InlineData("A20N", null, null, null)]
    public void Aircraft_family_match(string booked, string? title, string? model, bool? expected) =>
        Assert.Equal(expected, PreflightReadiness.AircraftMatches(booked, title, model));
}

public class CareerStatsLogicTests
{
    private static FlightRecord Flight(string dep, string arr, string type, DateTime outUtc, double hours, double nm = 300,
        double multiplier = 1.0, bool review = false) => new()
    {
        DepartureICAO = dep, ArrivalICAO = arr, AircraftType = type,
        BlockOutTime = outUtc, BlockInTime = outUtc.AddHours(hours),
        ActualDistanceNm = nm, Status = FlightStatus.Completed, HoursMultiplier = multiplier,
        FlightComplianceViolations = review ? ["late"] : [],
    };

    [Fact]
    public void Totals_months_aircraft_routes_and_countries()
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var flights = new[]
        {
            Flight("EGLL", "LFPG", "A320", now.AddDays(-3), 1.5),
            Flight("LFPG", "EGLL", "A320", now.AddDays(-2), 1.5),
            Flight("EGLL", "LFPG", "A320", now.AddMonths(-2), 1.0),
            Flight("EGLL", "KJFK", "B77W", now.AddDays(-1), 8.0, 3000, multiplier: 2.0),
            Flight("EGLL", "EDDF", "A320", now.AddDays(-1), 1.0, review: true), // held for review: not counted
        };
        var s = CareerStatsLogic.Compute(flights, [], now);

        Assert.Equal(4, s.Flights);
        Assert.Equal(12.0, s.BlockHours, 3);
        Assert.Equal(20.0, s.CreditedHours, 3);
        Assert.Equal(12, s.Months.Count);
        Assert.Equal(11.0, s.Months[^1].Hours, 3);          // October
        Assert.Equal("B77W", s.Aircraft[0].Type);           // most hours first
        Assert.Equal(("EGLL", "LFPG", 2), (s.Routes[0].From, s.Routes[0].To, s.Routes[0].Flights));
        Assert.Equal(3, s.Countries);                        // GB, FR, US
        Assert.Equal(3000, s.Farthest!.ActualDistanceNm);
    }

    [Fact]
    public void Grades_use_the_same_bands_as_pilot_hub()
    {
        Assert.Equal("A", CareerStatsLogic.Grade(90));
        Assert.Equal("B", CareerStatsLogic.Grade(75));
        Assert.Equal("C", CareerStatsLogic.Grade(60));
        Assert.Equal("D", CareerStatsLogic.Grade(40));
        Assert.Equal("F", CareerStatsLogic.Grade(39));
    }
}

public class QuickSearchLogicTests
{
    private static readonly QuickSearchLogic.PageEntry[] Pages =
    [
        new("flights", "Flights", ["logbook", "pirep"]),
        new("landings", "Landings", ["touchdown", "butter"]),
        new("settings", "Settings", ["push to talk", "tray"]),
    ];

    [Fact]
    public void Finds_pages_by_name_and_by_keyword()
    {
        Assert.Equal("landings", QuickSearchLogic.Search("land", Pages, [], [], [])[0].Target);
        Assert.Equal("settings", QuickSearchLogic.Search("tray", Pages, [], [], [])[0].Target);
        Assert.Equal("flights", QuickSearchLogic.Search("logbook", Pages, [], [], [])[0].Target);
    }

    [Fact]
    public void Finds_flights_and_landings_by_airport()
    {
        var landing = new LandingResult { AirportICAO = "EGLL", RunwayIdentifier = "27L", LandingScore = 88, Timestamp = DateTime.UtcNow };
        var flight = new FlightRecord { FlightNumber = "VAV103", DepartureICAO = "LFPG", ArrivalICAO = "EGLL", BlockOutTime = DateTime.UtcNow, PrimaryLanding = landing };

        var results = QuickSearchLogic.Search("EGLL", Pages, [], [flight], [landing]);
        Assert.Contains(results, r => r.Kind == QuickSearchKind.Flight && r.Target == landing.Id.ToString());
        Assert.Contains(results, r => r.Kind == QuickSearchKind.Landing && r.Target == landing.Id.ToString());
    }

    [Fact]
    public void Empty_query_returns_nothing() => Assert.Empty(QuickSearchLogic.Search("  ", Pages, [], [], []));
}

public class LandingAssessmentTests
{
    private static LandingResult Landing(double vs = -180, int score = 88) => new()
    {
        Timestamp = DateTime.UtcNow, AirportICAO = "EGLL", RunwayIdentifier = "27L",
        VerticalSpeedFPM = vs, IASKts = 134, TouchdownPitchDeg = 4, TouchdownBankDeg = 0.5,
        FlareDetected = true, LandingScore = score, ThresholdDistanceFt = 1200, CenterlineDeviationFt = 8,
        ApproachSpeedMinKts = 133, ApproachSpeedMaxKts = 148, LandingFlapTargetPct = 100,
        ScoreBreakdown = new LandingScoreBreakdown { VerticalSpeedScore = 28, PitchScore = 18, BankScore = 15, SpeedScore = 14, CrosswindScore = 9, StabilityScore = 9, TotalScore = score },
    };

    [Fact]
    public void A_clean_landing_has_no_findings()
    {
        var a = LandingReportBuilder.Build(Landing()).Assessment;
        Assert.Equal(0, a.CriticalCount);
        Assert.Equal(0, a.CautionCount);
        Assert.Equal("Excellent", a.Rating);
    }

    [Fact]
    public void A_hard_landing_is_rated_critical_even_with_a_decent_score()
    {
        var a = LandingReportBuilder.Build(Landing(vs: -950, score: 70)).Assessment;
        Assert.Equal("Critical", a.Rating);
        Assert.Contains(a.Checks, c => c.Name == "Touchdown rate" && c.Status == CheckStatus.Critical && c.Advice.Length > 0);
    }

    [Fact]
    public void Gear_not_down_by_1000_ft_is_critical()
    {
        var l = Landing();
        l.GearDownBy1000Ft = false;
        var a = LandingReportBuilder.Build(l).Assessment;
        Assert.Contains(a.Checks, c => c.Name == "Landing configuration by 1,000 ft" && c.Status == CheckStatus.Critical);
    }

    [Fact]
    public void Glidepath_deviation_is_measured_against_a_3_degree_path()
    {
        // 3 NM out at 1,000 ft is close to a 3° path (~955 ft); 2 NM out at 900 ft is high.
        var trace = new List<LandingReportPoint>
        {
            new() { T = -60, DistNm = -3.0, AglFt = 955 },
            new() { T = -40, DistNm = -2.0, AglFt = 900 },
        };
        var dev = LandingAssessmentBuilder.GlidepathDeviation(trace);
        Assert.NotNull(dev);
        Assert.True(dev!.Value.Deg > 0.8);   // well above the path
        Assert.Equal(900, dev.Value.Agl);
    }
}
