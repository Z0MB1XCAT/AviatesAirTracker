using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using AviatesAirTracker.Core.Efb;
using AviatesAirTracker.Models;
using AviatesAirTracker.Services.Efb;

// ── the one thing the linked ATIS service needs from SimBriefService: a raw METAR ──
namespace AviatesAirTracker.Services
{
    public class SimBriefService
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
        public async Task<string?> FetchMetarAsync(string icao)
        {
            try { return (await Http.GetStringAsync($"https://aviationweather.gov/api/data/metar?ids={icao}&format=raw")).Trim(); }
            catch { return null; }
        }
    }
}

/// <summary>VAV123 EGLL→LSZH in an A320neo, cruising along its route. Progress runs from 25 %
/// to 75 % and loops every 40 minutes, so the map, dashboard and ETAs keep moving.</summary>
sealed class DevFlight
{
    public const string Type = "A20N", Callsign = "VAV123";
    private static readonly DateTime Start = DateTime.UtcNow;
    private static readonly (string Id, string Type, double Lat, double Lon, int Alt)[] Route =
    [
        ("EGLL", "apt", 51.4706, -0.4619, 0), ("CPT", "vor", 51.4920, -1.2196, 6000), ("KENET", "wpt", 51.3586, -1.6347, 11000),
        ("ADKIK", "wpt", 50.6250, -1.1053, 24000), ("LARSO", "wpt", 49.4700, 0.7000, 36000), ("ABUDO", "wpt", 49.1700, 3.1500, 36000),
        ("SUL", "vor", 48.6300, 5.6200, 36000), ("TRA", "vor", 47.6894, 8.4369, 12000), ("LSZH", "apt", 47.4647, 8.5492, 0),
    ];
    private static readonly double[] Cum = Cumulative();
    public static double TotalNm => Cum[^1];

    public EfbFlightPlan Plan { get; } = new()
    {
        HasPlan = true, DepartureIcao = "EGLL", ArrivalIcao = "LSZH", AlternateIcao = "LSGG",
        Route = "CPT5J CPT UL9 KENET UN14 ADKIK UM605 LARSO UL613 ABUDO UN852 SUL UZ669 TRA TRA2W",
        AircraftType = Type, Registration = "G-VAVA", CruiseAltitudeFt = 36000, CruiseMach = 0.78, CostIndex = 30,
        PlannedDistanceNm = Math.Round(TotalNm), PlannedFlightTimeMin = 82, RampFuelLbs = 13_889, LandingFuelLbs = 5_950,
        TakeoffWeightKg = 64_210, ZeroFuelWeightKg = 58_420, PaxCount = 162, CargoKg = 1_840,
        DepartureRunway = "27R", ArrivalRunway = "14", DepartureElevationFt = 83, DepartureRunwayLengthM = 3_902,
        DepartureTransAltFt = 6000, FlightNumber = Callsign, DepartureName = "London Heathrow", ArrivalName = "Zurich",
        Sid = "CPT5J", Star = "TRA2W", ArrivalElevationFt = 1_416, ArrivalRunwayLengthM = 3_300,
        ScheduledDepartureUtc = Start.AddMinutes(-40), ScheduledArrivalUtc = Start.AddMinutes(55), MacTowPercent = 27.8,
        Waypoints = Route.Select((w, i) => new EfbWaypoint { Seq = i, Ident = w.Id, Type = w.Type, Latitude = w.Lat, Longitude = w.Lon, AltitudeFt = w.Alt }).ToList(),
    };

    private string _depMetar = "", _depTaf = "", _arrMetar = "", _arrTaf = "", _altMetar = "", _altTaf = "";
    public async Task LoadWeatherAsync(EfbAtisService atis)
    {
        try
        {
            var r = await Task.WhenAll(atis.GetAsync("EGLL"), atis.GetAsync("LSZH"), atis.GetAsync("LSGG"));
            (_depMetar, _depTaf, _arrMetar, _arrTaf, _altMetar, _altTaf) = (r[0].Metar, r[0].Taf, r[1].Metar, r[1].Taf, r[2].Metar, r[2].Taf);
            Plan.DepartureMetar = _depMetar; Plan.ArrivalMetar = _arrMetar;
        }
        catch { /* offline: the plan just has no METARs */ }
    }

    private static double[] Cumulative()
    {
        var c = new double[Route.Length];
        for (int i = 1; i < Route.Length; i++)
            c[i] = c[i - 1] + GeoMath.DistanceNm(Route[i - 1].Lat, Route[i - 1].Lon, Route[i].Lat, Route[i].Lon);
        return c;
    }

    /// <summary>"cruise" (default): the flight at FL360, looping 25 %→75 % of the route every 40 min.
    /// "full": a whole flight compressed into 15 minutes, gate to gate, to exercise flight mode
    /// (1,000 ft on the climb, 1,000 ft on the approach, touchdown, taxi-in, parked).
    /// "none": no flight at all (the EFB before a booking).</summary>
    public string Scenario { get; set; } = "cruise";
    private DateTime _scenarioStart = DateTime.UtcNow;
    private double _speed = 1;
    public void SetScenario(string s, double speed = 1) { Scenario = s is "full" or "none" ? s : "cruise"; _scenarioStart = DateTime.UtcNow; _speed = Math.Clamp(speed, 1, 60); }

    public EfbSnapshot Snapshot()
    {
        if (Scenario == "none") return new EfbSnapshot { ServerTimeUtc = DateTime.UtcNow, SimConnected = false, HasActiveFlight = false };
        if (Scenario == "full") return FullFlight();
        var now = DateTime.UtcNow;
        double p = 0.25 + ((now - Start).TotalSeconds % 2400) / 2400 * 0.5;
        double flown = p * TotalNm, remaining = TotalNm - flown;
        int seg = Math.Clamp(Array.FindIndex(Cum, c => c > flown), 1, Route.Length - 1);
        var (a, b) = (Route[seg - 1], Route[seg]);
        double f = (flown - Cum[seg - 1]) / Math.Max(0.001, Cum[seg] - Cum[seg - 1]);
        double lat = a.Lat + (b.Lat - a.Lat) * f, lon = a.Lon + (b.Lon - a.Lon) * f;
        double trk = GeoMath.InitialBearingDeg(lat, lon, b.Lat, b.Lon);
        const double gs = 446, todNm = 115;
        bool descending = remaining < todNm;
        double alt = descending ? Math.Max(9000, 36000 * remaining / todNm) : 36000;
        double fuel = 13_889 - 360 - 5_120 * (flown / gs);
        int ete = (int)Math.Round(remaining / gs * 60 + 6);
        double nextNm = Cum[seg] - flown;
        return new EfbSnapshot
        {
            ServerTimeUtc = now, SimConnected = true, HasActiveFlight = true, SessionState = "Airborne",
            Callsign = Callsign, FlightNumber = Callsign, Registration = Plan.Registration, AircraftType = Type,
            AircraftName = "Airbus A320neo", DepartureIcao = "EGLL", DepartureName = "London Heathrow",
            ArrivalIcao = "LSZH", ArrivalName = "Zurich", AlternateIcao = "LSGG",
            Latitude = lat, Longitude = lon, AltitudeFt = Math.Round(alt), AltitudeAglFt = Math.Round(alt - 300),
            IasKts = descending ? 290 : 262, TasKts = 452, GroundKts = gs, Mach = 0.782, HeadingDeg = (trk + 358) % 360, TrackDeg = trk,
            VerticalSpeedFpm = descending ? -2200 : 0, PitchDeg = descending ? -1.5 : 2.4, BankDeg = 0,
            Phase = descending ? "Descent" : "Cruise", PhaseColor = descending ? "#F59E0B" : "#22C55E",
            FuelLbs = Math.Round(fuel), FuelPct = fuel / 41_000, FuelBurnPph = 5_120,
            Gear = "UP", FlapsPct = 0, SpoilersPct = 0, ApMaster = true, ApModes = descending ? "AP1 A/THR NAV DES" : "AP1 A/THR NAV ALT CRZ", Squawk = "4621",
            WindDirDeg = 245, WindKts = 38, HeadwindKts = -12, CrosswindKts = 36, OatC = -54, QnhMb = 1013,
            PlannedDistanceNm = Math.Round(TotalNm), DistanceRemainingNm = Math.Round(remaining), ProgressPct = Math.Round(p * 100, 1),
            EteMinutes = ete, EtaUtc = now.AddMinutes(ete), CruiseAltitudeFt = 36000,
            BearingToDestDeg = GeoMath.InitialBearingDeg(lat, lon, Route[^1].Lat, Route[^1].Lon),
            NextWaypoint = b.Id, NextWaypointNm = Math.Round(nextNm, 1), NextWaypointEtaUtc = now.AddMinutes(nextNm / gs * 60),
            ScheduledOutUtc = Plan.ScheduledDepartureUtc, ScheduledInUtc = Plan.ScheduledArrivalUtc,
            BlockOutUtc = Start.AddMinutes(-36), TakeoffUtc = Start.AddMinutes(-23),
            TodDistanceNm = Math.Max(0, Math.Round(remaining - todNm)), TodMinutes = (int)Math.Max(0, (remaining - todNm) / gs * 60),
            DescendNow = descending, RequiredDescentFtPerNm = Math.Round(alt / Math.Max(1, remaining - 10)),
            PredictedLandingFuelLbs = Math.Round(fuel - 5_120 * (remaining / gs) - 300), PlannedLandingFuelLbs = Plan.LandingFuelLbs,
            FuelStatus = "OK",
        };
    }

    // Gate-to-gate timeline in seconds: parked, taxi, take-off roll, climb (through 1,000 ft AGL at
    // ~t+215), cruise, descent, approach (through 1,000 ft AGL at ~t+760), touchdown, taxi-in, parked.
    private static readonly (double T, string Phase, string Session, double Agl, double Ias, double Vs, string Gear, double Flaps)[] Timeline =
    [
        (0,   "Parked",        "PreFlight",  0,     0,   0,     "DOWN", 0),
        (60,  "Taxi",          "Taxiing",    0,     15,  0,     "DOWN", 40),
        (180, "Takeoff",       "Taxiing",    0,     150, 0,     "DOWN", 40),
        (200, "InitialClimb",  "Airborne",   200,   155, 2400,  "DOWN", 40),
        (240, "Climb",         "Airborne",   2400,  220, 2600,  "UP",   0),
        (330, "Cruise",        "Airborne",   35700, 262, 0,     "UP",   0),
        (520, "Descent",       "Airborne",   35700, 290, -2400, "UP",   0),
        (690, "Approach",      "OnApproach", 3000,  180, -900,  "DOWN", 70),
        (790, "FinalApproach", "OnApproach", 500,   138, -720,  "DOWN", 100),
        (810, "Landing",       "Landed",     0,     135, 0,     "DOWN", 100),
        (830, "Rollout",       "Landed",     0,     40,  0,     "DOWN", 100),
        (850, "Taxi",          "Landed",     0,     15,  0,     "DOWN", 0),
        (900, "Parked",        "Complete",   0,     0,   0,     "DOWN", 0),
    ];

    private EfbSnapshot FullFlight()
    {
        var now = DateTime.UtcNow;
        double t = Math.Min(900, (now - _scenarioStart).TotalSeconds * _speed);
        int i = Math.Max(0, Array.FindLastIndex(Timeline, k => k.T <= t));
        var k0 = Timeline[i];
        var k1 = Timeline[Math.Min(i + 1, Timeline.Length - 1)];
        double f = k1.T > k0.T ? (t - k0.T) / (k1.T - k0.T) : 0;
        double Lerp(double a, double b) => a + (b - a) * f;
        double agl = Lerp(k0.Agl, k1.Agl), ias = Lerp(k0.Ias, k1.Ias);
        // progress along the route only while airborne (200 s → 810 s)
        double p = Math.Clamp((t - 200) / 610, 0, 1), flown = p * TotalNm, remaining = TotalNm - flown;
        int seg = Math.Clamp(Array.FindIndex(Cum, c => c >= flown), 1, Route.Length - 1);
        var (a, b) = (Route[seg - 1], Route[seg]);
        double sf = (flown - Cum[seg - 1]) / Math.Max(0.001, Cum[seg] - Cum[seg - 1]);
        double lat = a.Lat + (b.Lat - a.Lat) * sf, lon = a.Lon + (b.Lon - a.Lon) * sf;
        double trk = GeoMath.InitialBearingDeg(lat, lon, b.Lat, b.Lon);
        bool onGround = agl < 1 && k0.Phase is not ("InitialClimb" or "Climb" or "Cruise" or "Descent" or "Approach" or "FinalApproach");
        double elev = t < 500 ? 83 : 1416;
        double fuel = 13_889 - 360 * Math.Min(1, t / 180) - 5_120 * (p * 82 / 60);
        int ete = (int)Math.Round(remaining / 446 * 60);
        string colour = k0.Phase switch
        {
            "Cruise" => "#22C55E", "Approach" or "FinalApproach" => "#F97316", "Landing" => "#EF4444",
            "Climb" or "InitialClimb" => "#4D9BEC", "Descent" => "#EAB308", "Takeoff" => "#22D3EE", _ => "#4A5568",
        };
        return new EfbSnapshot
        {
            ServerTimeUtc = now, SimConnected = true, HasActiveFlight = k0.Session != "Complete", SessionState = k0.Session,
            Callsign = Callsign, FlightNumber = Callsign, Registration = Plan.Registration, AircraftType = Type,
            AircraftName = "Airbus A320neo", DepartureIcao = "EGLL", DepartureName = "London Heathrow",
            ArrivalIcao = "LSZH", ArrivalName = "Zurich", AlternateIcao = "LSGG",
            Latitude = lat, Longitude = lon, AltitudeFt = Math.Round(agl + elev), AltitudeAglFt = Math.Round(agl),
            IasKts = Math.Round(ias), TasKts = Math.Round(ias * (1 + agl / 60000)), GroundKts = Math.Round(ias * (1 + agl / 70000)),
            Mach = Math.Round(ias / 661 * (1 + agl / 50000), 3), HeadingDeg = trk, TrackDeg = trk,
            VerticalSpeedFpm = Math.Round(Lerp(k0.Vs, k1.Vs)), PitchDeg = k0.Vs > 0 ? 12 : k0.Vs < 0 ? -1 : 0, BankDeg = 0,
            Phase = k0.Phase, PhaseColor = colour,
            FuelLbs = Math.Round(fuel), FuelPct = fuel / 41_000, FuelBurnPph = onGround ? 900 : 5_120,
            Gear = k0.Gear, FlapsPct = k0.Flaps, SpoilersPct = 0, ApMaster = agl > 1500, ApModes = agl > 1500 ? "AP1 A/THR NAV" : "", Squawk = "4621",
            WindDirDeg = 245, WindKts = 12, HeadwindKts = 8, CrosswindKts = 9, OatC = 15 - agl / 500, QnhMb = 1013,
            PlannedDistanceNm = Math.Round(TotalNm), DistanceRemainingNm = Math.Round(remaining), ProgressPct = Math.Round(p * 100, 1),
            EteMinutes = ete, EtaUtc = now.AddMinutes(ete), CruiseAltitudeFt = 36000,
            BearingToDestDeg = GeoMath.InitialBearingDeg(lat, lon, Route[^1].Lat, Route[^1].Lon),
            NextWaypoint = b.Id, NextWaypointNm = Math.Round(Cum[seg] - flown, 1),
            ScheduledOutUtc = Plan.ScheduledDepartureUtc, ScheduledInUtc = Plan.ScheduledArrivalUtc,
            BlockOutUtc = t >= 60 ? _scenarioStart.AddSeconds(60 / _speed) : null, TakeoffUtc = t >= 200 ? _scenarioStart.AddSeconds(200 / _speed) : null,
            LandingUtc = t >= 810 ? _scenarioStart.AddSeconds(810 / _speed) : null, BlockInUtc = t >= 900 ? _scenarioStart.AddSeconds(900 / _speed) : null,
            PlannedLandingFuelLbs = Plan.LandingFuelLbs, FuelStatus = "OK",
        };
    }

    public EfbBriefing Briefing() => new()
    {
        HasPlan = true, Units = "KGS", FlightNumber = Callsign, Callsign = Callsign, Registration = Plan.Registration,
        AircraftType = Type, AircraftName = "A320-251N", DepartureIcao = "EGLL", DepartureName = "London Heathrow",
        ArrivalIcao = "LSZH", ArrivalName = "Zurich", AlternateIcao = "LSGG", AlternateName = "Geneva",
        DepartureRunway = "27R", ArrivalRunway = "14", Sid = "CPT5J", Star = "TRA2W", Route = Plan.Route,
        CruiseAltitudeFt = 36000, CostIndex = 30, DistanceNm = Math.Round(TotalNm), AvgWindComponentKts = -12,
        ScheduledOutUtc = Plan.ScheduledDepartureUtc, ScheduledInUtc = Plan.ScheduledArrivalUtc, BlockTimeMin = 95, AirTimeMin = 82,
        GeneratedAtUtc = Start.AddHours(-1),
        TaxiFuel = 200, TripFuel = 3_420, ContingencyFuel = 171, AlternateFuel = 1_210, FinalReserveFuel = 1_080, ExtraFuel = 219,
        MinTakeoffFuel = 5_881, PlanTakeoffFuel = 6_100, BlockFuel = 6_300, LandingFuel = 2_680,
        Zfw = 58_420, MaxZfw = 64_300, Tow = 64_520, MaxTow = 79_000, Ldw = 61_100, MaxLdw = 67_400,
        Payload = 15_200, PaxCount = 162, Cargo = 1_840, MacTowPercent = 27.8, MacZfwPercent = 28.4,
        DepartureMetar = _depMetar, DepartureTaf = _depTaf, ArrivalMetar = _arrMetar, ArrivalTaf = _arrTaf,
        AlternateMetar = _altMetar, AlternateTaf = _altTaf,
        Notams =
        [
            "A1234/26 NOTAMN Q) EGTT/QMXLC/IV/M/A/000/999/5128N00027W005 A) EGLL B) 2610010600 C) 2611302200 E) TWY B BTN B5 AND B7 CLSD DUE WIP",
            "B0456/26 NOTAMN Q) LSAS/QILAS/I/NBO/A/000/999/4728N00833E005 A) LSZH B) 2610150000 C) 2611150000 E) ILS RWY 14 GP UNSERVICEABLE, LOC AVBL",
            "A0789/26 NOTAMN Q) LSAS/QFAXX/IV/NBO/A/000/999/4614N00606E005 A) LSGG B) 2610200500 C) 2611200500 E) DE-ICING AVBL ONLY ON STANDS 31-36",
        ],
        OfpHtml = "<div class=\"ofp\"><pre>[ OFP ]\nVAV123   EGLL-LSZH   A20N G-VAVA\n\nPLANNED ROUTE\n" + Plan.Route +
                  "\n\nFUEL       KGS\nTRIP      3420\nCONT       171\nALTN      1210\nFINRES    1080\nEXTRA      219\nTAXI       200\nBLOCK     6300</pre></div>",
    };
}

/// <summary>Messages, friends, manuals status, ACARS and the sample landing history.</summary>
sealed class DevState
{
    public const string MeId = "p-me", MyName = "Dexter Rees";
    public object Prefs = new { weightUnit = "KG" };
    public string Theme = "aviates";
    public byte[]? Wallpaper;
    public DevManuals Manuals { get; }
    public DevAcars Acars { get; } = new();
    public List<EfbFriend> Friends { get; } =
    [
        new() { PilotId = "p-sara", PilotName = "Sara Lindqvist", FriendCode = "R2NH-8WCE", Rank = "Captain", IsOnline = true },
        new() { PilotId = "p-tom", PilotName = "Tom Okafor", FriendCode = "M9TZ-3KDA", Rank = "First Officer" },
        new() { PilotId = "p-jules", PilotName = "Jules Moreau", FriendCode = "H4PB-7YRF", Rank = "Senior First Officer" },
    ];
    public List<EfbMessageDto> Messages { get; } = [];
    public List<LandingResult> Landings { get; }

    private static readonly Dictionary<string, string> Names = new()
    {
        [MeId] = MyName, ["p-sara"] = "Sara Lindqvist", ["p-tom"] = "Tom Okafor", ["p-jules"] = "Jules Moreau",
        ["p-ana"] = "Ana Costa", ["p-ben"] = "Ben Hartley",
    };

    public DevState(string manualsDir)
    {
        Manuals = new DevManuals(manualsDir);
        AddMessage("p-tom", MeId, "Fancy a shared flight on Saturday? EGLL to LSZH in the 320.", 3000);
        AddMessage(MeId, "p-tom", "Sounds good, I'll book the evening slot.", 2990);
        AddMessage("p-tom", MeId, "Booked. See you then.", 2985);
        AddMessage("p-sara", MeId, "Are you on VATSIM tonight?", 1500);
        AddMessage(MeId, "p-sara", "Yes, planning EGLL to LSZH around 1900Z.", 1490);
        AddMessage("p-sara", MeId, "Perfect, I'll be a few minutes behind you on the same SID.", 1480);
        AddMessage("p-sara", MeId, "Just pushed from 512. Taxiing to 27R now.", 32, read: false);
        AddMessage("p-sara", MeId, "Departure frequency is busy, heads up.", 31, read: false);
        AddMessage("p-ana", "broadcast", "Morning all! Anyone flying the new Dublin routes today?", 600, "Broadcast");
        AddMessage("p-ben", "broadcast", "EGLL is landing 09L/09R this morning, winds 080 at 15.", 400, "Broadcast");
        AddMessage(MeId, "broadcast", "Thanks Ben, heading there shortly.", 395, "Broadcast");
        AddMessage("p-ana", "broadcast", "Event tonight at 1900Z: EGKK to LEPA, all welcome.", 45, "Broadcast");
        Landings = SampleLandings();
    }

    public EfbMessageDto AddMessage(string from, string to, string text, int minutesAgo, string type = "Direct", bool read = true, bool synced = true)
    {
        var m = new EfbMessageDto
        {
            Id = Guid.NewGuid().ToString(), SenderId = from, SenderName = Names.GetValueOrDefault(from, "Pilot"), RecipientId = to,
            Content = text, SentAt = DateTime.UtcNow.AddMinutes(-minutesAgo), IsRead = read, Mine = from == MeId, Synced = synced, Type = type,
        };
        lock (Messages) { Messages.Add(m); Messages.Sort((x, y) => x.SentAt.CompareTo(y.SentAt)); }
        return m;
    }

    public EfbMessagingState Messaging() => new()
    {
        MyPilotId = MeId, MyPilotName = MyName, MyFriendCode = "K7QM-4XP2", HasKey = true,
        UnreadCount = Messages.Count(m => m.RecipientId == MeId && !m.IsRead),
        Friends = Friends.ToList(), Messages = Messages.ToList(),
    };

    public static List<EfbTraffic> Traffic(DevFlight f) =>
    [
        new() { PilotId = "p-sara", PilotName = "Sara Lindqvist", Callsign = "VAV412", Lat = 50.9, Lon = 0.4, Alt = 31000, Speed = 438, Heading = 118, Phase = "Climb", DepartureIcao = "EGLL", ArrivalIcao = "LSZH" },
        new() { PilotId = "p-ben", PilotName = "Ben Hartley", Callsign = "VAV88", Lat = 53.4, Lon = -2.2, Alt = 36000, Speed = 462, Heading = 140, Phase = "Cruise", DepartureIcao = "EGPH", ArrivalIcao = "LEMD" },
        new() { PilotId = "p-ana", PilotName = "Ana Costa", Callsign = "VAC7", Lat = 53.42, Lon = -6.27, Alt = 0, Speed = 0, Heading = 280, Phase = "Parked", DepartureIcao = "EIDW", ArrivalIcao = "EGCC" },
    ];

    // ── landing history: three landings, the latest with a full approach trace ──
    private static List<LandingResult> SampleLandings()
    {
        LandingResult L(int hoursAgo, string apt, string rwy, double vs, int score, double lat, double lon, double xw, int bounce = 0) => new()
        {
            Timestamp = DateTime.UtcNow.AddHours(-hoursAgo), AirportICAO = apt, RunwayIdentifier = rwy,
            VerticalSpeedFPM = vs, GroundSpeedKts = 131, IASKts = 136, TouchdownPitchDeg = 4.8, TouchdownBankDeg = 1.2,
            TouchdownLatitude = lat, TouchdownLongitude = lon, HeadwindComponent = 4, CrosswindComponent = xw,
            WindSpeedKts = 9, WindDirectionDeg = 170, ApproachWasStable = true, FlareDetected = true, BounceCount = bounce,
            RolloutDistanceFt = 4200, ThresholdDistanceFt = 1350, LandingScore = score, FuelOnTouchdownLbs = 5_908,
            // a believable split of the total across the six factors (the real analyser fills this)
            ScoreBreakdown = new() { VerticalSpeedScore = Math.Min(30, 30 * score / 100.0 + 1), PitchScore = Math.Min(20, 20 * score / 100.0 + 1.5),
                BankScore = Math.Min(15, 15 * score / 100.0 + 1), SpeedScore = 15 * score / 100.0 - 2, CrosswindScore = Math.Min(10, 10 * score / 100.0 + .5),
                StabilityScore = 10 * score / 100.0 - 1, TotalScore = score },
        };
        return
        [
            L(2, "LSZH", "14", -142, 92, 47.4815, 8.5333, 2),
            L(26, "EGLL", "27L", -238, 81, 51.4648, -0.4340, 7),
            L(74, "LEPA", "24L", -96, 96, 39.5600, 2.7550, 4),
        ];
    }

    /// <summary>A stabilised 3° approach onto the landing's runway: two minutes in, 20 s of rollout.</summary>
    public List<LandingTracePoint> Trace(LandingResult l)
    {
        const double tan3FtPerNm = 318.44, gsNmPerS = 140.0 / 3600;
        double rwyHdg = (double.TryParse(new string(l.RunwayIdentifier.TakeWhile(char.IsDigit).ToArray()), out var h) ? h : 14) * 10;
        double rad = rwyHdg * Math.PI / 180, cosLat = Math.Cos(l.TouchdownLatitude * Math.PI / 180);
        var pts = new List<LandingTracePoint>();
        for (int t = -120; t <= 20; t++)
        {
            double d = t <= 0 ? t * gsNmPerS : t * gsNmPerS * 0.4;
            double wobble = Math.Sin(t / 9.0) * 1.5;
            pts.Add(new LandingTracePoint
            {
                T = t, Lat = l.TouchdownLatitude + d * Math.Cos(rad) / 60, Lon = l.TouchdownLongitude + d * Math.Sin(rad) / 60 / cosLat,
                AglFt = t < 0 ? Math.Max(0, Math.Round(-d * tan3FtPerNm + (t > -6 ? -t * 2 : 0))) : 0,
                IasKts = t <= 0 ? 136 + wobble : Math.Max(30, 136 - t * 5), GsKts = t <= 0 ? 131 + wobble : Math.Max(25, 131 - t * 5),
                VsFpm = t < -6 ? -720 + wobble * 20 : t < 0 ? l.VerticalSpeedFPM * (1 + t / -6.0 * 3) : 0,
                PitchDeg = t < -6 ? 2.4 : t <= 0 ? 4.8 : 1.0, BankDeg = Math.Sin(t / 7.0) * 1.2,
                FlapsPct = 100, GearDown = true, OnGround = t >= 0,
            });
        }
        return pts;
    }
}

/// <summary>Manuals status (bookmarks, acknowledgements, recently opened) over the real library.</summary>
sealed class DevManuals
{
    public IReadOnlyList<EfbDocument> Documents { get; }
    private readonly HashSet<string> _bookmarks = [];
    private readonly Dictionary<string, (int Rev, DateTime At)> _acks = [];
    private readonly List<string> _recent = [];

    public DevManuals(string dir)
    {
        var docs = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.md").Select(f => EfbManualLibrary.Parse(File.ReadAllText(f))).Where(d => d != null).Select(d => d!).ToList()
            : [];
        var effective = docs.Select(d => d.Effective).DefaultIfEmpty("").Max() ?? "";
        Documents = EfbManualLibrary.Ordered(docs.Concat(EfbManualLibrary.ChecklistDocuments(effective))).ToList();
    }

    private bool Acked(EfbDocument d) => _acks.TryGetValue(d.Id, out var a) && a.Rev >= d.Revision;
    public int Pending => Documents.Count(d => d.RequiresAck && !Acked(d));
    public List<string> Recent() { lock (_recent) return _recent.ToList(); }
    public object List() => Documents.Select(ToSummary).ToList();
    public object? Summary(string id) => Documents.FirstOrDefault(d => d.Id == id) is { } d ? ToSummary(d) : null;
    public EfbDocument? Get(string id)
    {
        var d = Documents.FirstOrDefault(x => x.Id == id);
        if (d != null) lock (_recent) { _recent.Remove(id); _recent.Insert(0, id); if (_recent.Count > 8) _recent.RemoveAt(8); }
        return d;
    }
    public bool ToggleBookmark(string id) { if (Documents.All(d => d.Id != id)) return false; if (!_bookmarks.Remove(id)) _bookmarks.Add(id); return true; }
    public bool Acknowledge(string id)
    {
        var d = Documents.FirstOrDefault(x => x.Id == id);
        if (d is not { RequiresAck: true }) return false;
        _acks[id] = (d.Revision, DateTime.UtcNow); return true;
    }
    private object ToSummary(EfbDocument d) => new
    {
        d.Id, d.Manual, d.Chapter, d.Title, d.Summary, Tags = d.Tags.ToList(), Aircraft = d.Aircraft.ToList(), d.Revision, d.Effective,
        d.RequiresAck, AcknowledgedUtc = Acked(d) ? _acks[d.Id].At : (DateTime?)null, Bookmarked = _bookmarks.Contains(d.Id),
    };
}

/// <summary>The datalink, with "ATC" and "dispatch" answering a few seconds after a downlink.</summary>
sealed class DevAcars
{
    public event Action? Changed;
    public DateTime? LastPoll;
    private readonly List<EfbAcarsMessage> _msgs = [];
    private string _station = "", _state = "off";
    private int _min;

    public DevAcars()
    {
        Add(new() { Station = "VAOPS", Kind = "COMPANY", Text = "VAV123 GOOD MORNING. GATE 512 CONFIRMED. SLOT 1105Z. HAVE A GOOD FLIGHT.", Utc = DateTime.UtcNow.AddMinutes(-70), Read = false });
        Add(new() { Dir = "out", Station = "EGLL", Kind = "PDC REQ", Text = "REQUEST PREDEP CLEARANCE VAV123 A20N TO LSZH AT EGLL STAND 512 ATIS K", Utc = DateTime.UtcNow.AddMinutes(-22) });
        Add(new()
        {
            Station = "EGLL", Type = "cpdlc", Kind = "CLEARANCE", Utc = DateTime.UtcNow.AddMinutes(-20), Read = false, Min = 1, Ra = "WU",
            Text = "PDC 001\nVAV123 CLRD TO LSZH OFF 27R VIA CPT5J\nCLIMB 6000FT SQUAWK 4621\nNEXT FREQ 121.975 ATIS K",
            Responses = ["WILCO", "UNABLE", "STANDBY"], Open = true,
        });
    }

    public void Inject(EfbAcarsMessage m) { Add(m); Changed?.Invoke(); }
    private EfbAcarsMessage Add(EfbAcarsMessage m) { if (m.Utc == default) m.Utc = DateTime.UtcNow; lock (_msgs) _msgs.Add(m); return m; }
    private void Later(int ms, Action a) => _ = Task.Run(async () => { await Task.Delay(ms); a(); Changed?.Invoke(); });

    public EfbAcarsState State()
    {
        lock (_msgs) return new()
        {
            HasLogon = true, AutoOpsEnabled = true, Callsign = DevFlight.Callsign, Departure = "EGLL", Arrival = "LSZH", Alternate = "LSGG",
            AircraftType = DevFlight.Type, CpdlcStation = _station, CpdlcState = _state, LastPollUtc = LastPoll ?? DateTime.UtcNow, LastPollOk = true,
            Unread = _msgs.Count(m => m.Dir == "in" && !m.Read), Messages = _msgs.Select(m => m.Clone()).ToList(),
            Ops =
            [
                new() { Utc = DateTime.UtcNow.AddMinutes(-95), Category = "Loadsheet", Message = "LOADSHEET FINAL 1015\nVAV123 EGLL LSZH A20N\nZFW 58420 TOW 64210 LAW 61080\nMACTOW 27.8", Success = true },
                new() { Utc = DateTime.UtcNow.AddMinutes(-80), Category = "Pushback Reminder", Message = "VAV123 STD 1100Z. PUSHBACK IN 5 MIN.", Success = true },
            ],
        };
    }

    public void MarkRead(IEnumerable<string> ids) { var s = ids.ToHashSet(); lock (_msgs) foreach (var m in _msgs) if (s.Contains(m.Id)) m.Read = true; Changed?.Invoke(); }

    private static (bool, string?, EfbAcarsMessage?) Fail(string e) => (false, e, null);

    public (bool, string?, EfbAcarsMessage?) Telex(string? to, string? text, string kind = "TELEX")
    {
        to = EfbAcars.Clean(to).Replace(" ", ""); var t = EfbAcars.Clean(text);
        if (!EfbAcars.IsStation(to)) return Fail("Enter a valid station (e.g. EGLL, EDGG or VAOPS).");
        if (t.Length == 0) return Fail("Type a message first.");
        var m = Add(new() { Dir = "out", Station = to, Kind = to == "VAOPS" && kind == "TELEX" ? "COMPANY" : kind, Text = t });
        if (to == "VAOPS")
            Later(4000, () => Add(new() { Station = "VAOPS", Kind = "COMPANY", Read = false, Text = "AVIATES OPS: COPY YOUR MESSAGE. NO CHANGE TO YOUR SLOT. HAVE A GOOD FLIGHT." }));
        Changed?.Invoke();
        return (true, null, m);
    }

    public (bool, string?, EfbAcarsMessage?) Info(string? kind, string? icao, DevFlight f)
    {
        icao = EfbAcars.Clean(icao).Replace(" ", "");
        if (icao.Length != 4) return Fail("Enter a valid station (e.g. EGLL, EDGG or VAOPS).");
        var k = (kind ?? "METAR").ToUpperInvariant();
        var text = k switch
        {
            "TAF" => $"TAF {icao} 011100Z 0112/0218 23012KT 9999 FEW035\nTEMPO 0114/0118 4000 SHRA",
            "ATIS" => $"{icao} ATIS INFO K 1050Z. EXPECT ILS APPROACH. TL 70. QNH 1015.",
            _ => $"{icao} 011050Z 22012KT 9999 FEW035 16/08 Q1015 NOSIG",
        };
        var m = Add(new() { Station = icao, Type = "info", Kind = k, Text = text });
        Changed?.Invoke();
        return (true, null, m);
    }

    public (bool, string?, EfbAcarsMessage?) Logon(string? station)
    {
        station = EfbAcars.Clean(station).Replace(" ", "");
        if (!EfbAcars.IsStation(station)) return Fail("Enter a valid station (e.g. EGLL, EDGG or VAOPS).");
        var min = ++_min; _station = station; _state = "pending";
        var m = Add(new() { Dir = "out", Station = station, Type = "cpdlc", Kind = "LOGON", Text = "REQUEST LOGON", Min = min });
        Later(3000, () => { _state = "connected"; Add(new() { Station = station, Type = "cpdlc", Kind = "LOGON", Text = "LOGON ACCEPTED", Read = false, Min = 1, Mrn = min, Ra = "NE" }); });
        Changed?.Invoke();
        return (true, null, m);
    }

    public (bool, string?, EfbAcarsMessage?) Logoff()
    {
        if (_station.Length == 0) return Fail("Not logged on to a CPDLC station.");
        var m = Add(new() { Dir = "out", Station = _station, Type = "cpdlc", Kind = "LOGOFF", Text = "LOGOFF", Min = ++_min });
        _station = ""; _state = "off"; Changed?.Invoke();
        return (true, null, m);
    }

    public (bool, string?, EfbAcarsMessage?) Request(string? text)
    {
        if (_state != "connected") return Fail("Log on to a CPDLC station first.");
        var t = EfbAcars.Clean(text); if (t.Length == 0) return Fail("Build a request first.");
        var min = ++_min; var st = _station;
        var m = Add(new() { Dir = "out", Station = st, Type = "cpdlc", Kind = "REQUEST", Text = t, Min = min });
        Later(3500, () =>
        {
            var lvl = System.Text.RegularExpressions.Regex.Match(t, @"FL\d{3}").Value;
            var answer = lvl.Length > 0 ? $"CLIMB TO {lvl}\nREPORT LEVEL {lvl}" : t.Contains("DIRECT") ? t.Replace("REQUEST ", "PROCEED ") : "STANDBY";
            m.Response = answer;
            Add(new() { Station = st, Type = "cpdlc", Kind = "CPDLC", Text = answer, Read = false, Min = 7 + min, Mrn = min, Ra = "WU", Responses = ["WILCO", "UNABLE", "STANDBY"], Open = true });
        });
        Changed?.Invoke();
        return (true, null, m);
    }

    public (bool, string?, EfbAcarsMessage?) Respond(string? id, string? response)
    {
        var r = (response ?? "").ToUpperInvariant();
        EfbAcarsMessage? up; lock (_msgs) up = _msgs.FirstOrDefault(x => x.Id == id && x.Dir == "in" && x.Type == "cpdlc");
        if (up == null) return Fail("That message can't be answered.");
        if (!up.Responses.Contains(r)) return Fail($"{r} isn't a valid response to that message.");
        up.Response = r; up.Open = !EfbAcars.IsClosing(r); up.Read = true;
        var m = Add(new() { Dir = "out", Station = up.Station, Type = "cpdlc", Kind = "RESPONSE", Text = r, Min = ++_min, Mrn = up.Min });
        Changed?.Invoke();
        return (true, null, m);
    }
}

/// <summary>A connected tablet; sends are serialised per socket like the app's EfbSocket.</summary>
sealed class DevSocket(WebSocket ws, string ip, string name)
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime ConnectedUtc { get; } = DateTime.UtcNow;
    public string Ip { get; } = ip;
    public string Name { get; } = name;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<bool> SendAsync(string text)
    {
        if (ws.State != WebSocketState.Open) return false;
        await _lock.WaitAsync();
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None); return true; }
        catch { return false; }
        finally { _lock.Release(); }
    }

    public async Task CloseAsync()
    {
        try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None); } catch { }
    }

    public static string NameFrom(string ua) =>
        (ua.Contains("iPad") ? "iPad" : ua.Contains("iPhone") ? "iPhone" : ua.Contains("Android") ? (ua.Contains("Mobile") ? "Android phone" : "Android tablet")
            : ua.Contains("Windows") ? "Windows PC" : ua.Contains("Macintosh") ? "Mac" : "Device")
        + (ua.Contains("Edg/") ? " · Edge" : ua.Contains("Chrome/") ? " · Chrome" : ua.Contains("Safari/") ? " · Safari" : ua.Contains("Firefox/") ? " · Firefox" : "");
}
