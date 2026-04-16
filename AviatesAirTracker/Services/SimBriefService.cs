using AviatesAirTracker.Models;
using Newtonsoft.Json.Linq;
using RestSharp;
using Serilog;
using System.IO;
using System.Xml.Linq;

namespace AviatesAirTracker.Services;

// ============================================================
// SIMBRIEF SERVICE
// Integrates with SimBrief flight planning service
// 
// Supported methods:
//   1. Fetch latest OFP by username via SimBrief API
//   2. Parse exported SimBrief XML OFP file
//   3. Parse exported SimBrief JSON OFP
// ============================================================

public class SimBriefService
{
    private const string SIMBRIEF_API_BASE = "https://www.simbrief.com/api/xml.fetcher.php";
    private const string SIMBRIEF_JSON_API = "https://www.simbrief.com/api/json.fetcher.php";

    private readonly RestClient _client = new();

    /// <summary>The most recently loaded OFP, or null if none has been fetched yet.</summary>
    public SimBriefFlightPlan? CurrentPlan { get; private set; }

    /// <summary>Raised after a flight plan is successfully parsed and stored in CurrentPlan.</summary>
    public event EventHandler<SimBriefFlightPlan>? FlightPlanLoaded;

    // Maps internal fleet type codes → SimBrief ICAO designators.
    // SimBrief rejects unknown codes, so every type in aircraft_types must have an entry here.
    private static readonly Dictionary<string, string> _simBriefTypeMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["A320neo"]    = "A20N",
            ["A321XLR"]    = "A21X",
            ["A350F"]      = "A35F",
            ["ATR 72"]     = "AT76",
            ["B737-800"]   = "B738",
            ["B777-300ER"] = "B77W",
            ["B787-9"]     = "B789",
        };

    /// <summary>Converts an internal fleet type code to the SimBrief ICAO designator.</summary>
    private static string ToSimBriefType(string internalCode) =>
        _simBriefTypeMap.TryGetValue(internalCode.Trim(), out var icao) ? icao : internalCode.Trim();

    // =====================================================
    // FETCH LATEST OFP BY SIMBRIEF USERNAME
    // =====================================================

    public async Task<SimBriefFlightPlan?> FetchLatestOFPAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("SimBrief username is required");

        Log.Information("[SimBrief] Fetching latest OFP for user: {User}", username);

        try
        {
            var request = new RestRequest(SIMBRIEF_API_BASE)
                .AddParameter("username", username)
                .AddParameter("json", "1");

            var response = await _client.GetAsync(request);

            if (!response.IsSuccessful || string.IsNullOrEmpty(response.Content))
            {
                Log.Warning("[SimBrief] API request failed: {Status}", response.StatusCode);
                return null;
            }

            var plan = ParseSimBriefJson(response.Content);
            Log.Information("[SimBrief] OFP fetched: {Dep}→{Arr} via {Route}",
                plan?.DepartureICAO, plan?.ArrivalICAO, plan?.Route);

            if (plan != null)
            {
                CurrentPlan = plan;
                FlightPlanLoaded?.Invoke(this, plan);
            }

            return plan;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SimBrief] Fetch failed");
            throw;
        }
    }

    // =====================================================
    // PARSE SIMBRIEF JSON RESPONSE
    // =====================================================

    private static SimBriefFlightPlan? ParseSimBriefJson(string json)
    {
        try
        {
            var root = JObject.Parse(json);

            var plan = new SimBriefFlightPlan
            {
                GeneratedAt = DateTime.UtcNow,
                GeneratedBy = root["params"]?["request_system"]?.ToString() ?? "SimBrief",
                OFPNumber = root["params"]?["ofp_layout"]?.ToString() ?? "",

                DepartureICAO = root["origin"]?["icao_code"]?.ToString() ?? "",
                ArrivalICAO = root["destination"]?["icao_code"]?.ToString() ?? "",
                AlternateICAO = root["alternate"]?["icao_code"]?.ToString() ?? "",

                DepartureLat = ParseDouble(root["origin"]?["pos_lat"]?.ToString()),
                DepartureLon = ParseDouble(root["origin"]?["pos_long"]?.ToString()),
                ArrivalLat   = ParseDouble(root["destination"]?["pos_lat"]?.ToString()),
                ArrivalLon   = ParseDouble(root["destination"]?["pos_long"]?.ToString()),
                AlternateLat = ParseDouble(root["alternate"]?["pos_lat"]?.ToString()),
                AlternateLon = ParseDouble(root["alternate"]?["pos_long"]?.ToString()),

                AircraftType = root["aircraft"]?["icaocode"]?.ToString() ?? "",
                AircraftRegistration = root["aircraft"]?["reg"]?.ToString() ?? "",

                Route = root["general"]?["route"]?.ToString() ?? "",
                // plan_rwy is the runway identifier — SID/STAR procedure names live in separate fields
                SID = root["origin"]?["sid"]?.ToString()
                    ?? root["origin"]?["sid_trans"]?.ToString()
                    ?? "",
                STAR = root["destination"]?["star"]?.ToString()
                     ?? root["destination"]?["star_trans"]?.ToString()
                     ?? "",
                DepartureRunway = root["origin"]?["plan_rwy"]?.ToString() ?? "",
                ArrivalRunway   = root["destination"]?["plan_rwy"]?.ToString() ?? "",
            };

            // Cruise altitude
            if (int.TryParse(root["general"]?["initial_altitude"]?.ToString(), out int cruiseAlt))
                plan.CruiseAltitudeFt = cruiseAlt;

            // Mach
            if (double.TryParse(root["general"]?["cruise_mach"]?.ToString(), out double mach))
                plan.CruiseMach = mach;

            // Distance
            if (double.TryParse(root["general"]?["route_distance"]?.ToString(), out double dist))
                plan.PlannedDistanceNm = dist;

            // Flight time
            if (int.TryParse(root["times"]?["est_time_enroute"]?.ToString(), out int ete))
                plan.PlannedFlightTimeMin = ete / 60;

            // Fuel
            // MAJOR-06: Was always multiplying by 2.20462 (kg→lbs) regardless of OFP unit setting.
            // SimBrief returns params.units = "kgs" or "lbs" — only convert when unit is kilograms.
            bool fuelInKg = string.Equals(
                root["params"]?["units"]?.ToString(), "kgs", StringComparison.OrdinalIgnoreCase);
            double ToLbs(double value) => fuelInKg ? value * 2.20462 : value;

            if (double.TryParse(root["fuel"]?["plan_ramp"]?.ToString(), out double ramp))
                plan.TakeoffFuelLbs = ToLbs(ramp);
            if (double.TryParse(root["fuel"]?["plan_landing"]?.ToString(), out double land))
                plan.LandingFuelLbs = ToLbs(land);
            if (double.TryParse(root["fuel"]?["alternate_burn"]?.ToString(), out double alt))
                plan.AlternateFuelLbs = ToLbs(alt);

            // METAR
            plan.DepartureMetar = root["weather"]?["orig_metar"]?.ToString() ?? "";
            plan.ArrivalMetar = root["weather"]?["dest_metar"]?.ToString() ?? "";

            // Scheduled departure time (Unix timestamp → UTC DateTime)
            if (long.TryParse(root["times"]?["sched_dep"]?.ToString(), out long schedDep) && schedDep > 0)
                plan.ScheduledDepartureUtc = DateTimeOffset.FromUnixTimeSeconds(schedDep).UtcDateTime;

            // Takeoff weight — same kg/lbs unit as fuel
            double ToKg(double v) => fuelInKg ? v : v / 2.20462;
            if (double.TryParse(root["weights"]?["est_tow"]?.ToString(), out double tow))
                plan.TakeoffWeightKg = ToKg(tow);

            // Departure elevation (ft)
            if (int.TryParse(root["origin"]?["elevation"]?.ToString(), out int depElev))
                plan.DepartureElevationFt = depElev;

            // Planned departure runway length (ft → m) from runway data array
            var rwyData = root["origin"]?["rwy_data"] as JArray ?? root["origin"]?["runway"] as JArray;
            if (rwyData != null && !string.IsNullOrEmpty(plan.DepartureRunway))
            {
                foreach (var rwy in rwyData)
                {
                    var rwyId = rwy["ident"]?.ToString() ?? rwy["rwy_id"]?.ToString() ?? "";
                    if (string.Equals(rwyId, plan.DepartureRunway, StringComparison.OrdinalIgnoreCase))
                    {
                        if (double.TryParse(rwy["length_ft"]?.ToString() ?? rwy["length"]?.ToString(), out double lenFt))
                            plan.DepartureRunwayLengthM = (int)(lenFt * 0.3048);
                        break;
                    }
                }
            }

            // Waypoints
            plan.Waypoints = ParseWaypointsFromJson(root["navlog"]?["fix"]);

            return plan;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SimBrief] Failed to parse JSON OFP");
            return null;
        }
    }

    private static double ParseDouble(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        return double.TryParse(s,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double v) ? v : 0;
    }

    private static List<Waypoint> ParseWaypointsFromJson(JToken? fixArray)
    {
        var waypoints = new List<Waypoint>();
        if (fixArray == null) return waypoints;

        int seq = 0;
        foreach (var fix in fixArray)
        {
            var wp = new Waypoint
            {
                SequenceNumber = seq++,
                Identifier = fix["ident"]?.ToString() ?? "",
                Type = fix["type"]?.ToString() ?? "FIX"
            };

            if (double.TryParse(fix["pos_lat"]?.ToString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double lat)) wp.Latitude = lat;
            if (double.TryParse(fix["pos_long"]?.ToString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double lon)) wp.Longitude = lon;
            if (int.TryParse(fix["altitude_feet"]?.ToString(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int alt))
                wp.AltitudeConstraintFt = alt;

            waypoints.Add(wp);
        }

        return waypoints;
    }

    // =====================================================
    // PARSE XML OFP FILE (exported from SimBrief website)
    // =====================================================

    public static SimBriefFlightPlan? ParseXmlOFP(string filePath)
    {
        Log.Information("[SimBrief] Parsing XML OFP: {File}", filePath);

        try
        {
            var doc = XDocument.Load(filePath);
            var root = doc.Root!;

            var plan = new SimBriefFlightPlan
            {
                GeneratedAt = DateTime.UtcNow,
                DepartureICAO = root.Element("origin")?.Element("icao_code")?.Value ?? "",
                ArrivalICAO = root.Element("destination")?.Element("icao_code")?.Value ?? "",
                AlternateICAO = root.Element("alternate")?.Element("icao_code")?.Value ?? "",
                DepartureLat = ParseDouble(root.Element("origin")?.Element("pos_lat")?.Value),
                DepartureLon = ParseDouble(root.Element("origin")?.Element("pos_long")?.Value),
                ArrivalLat   = ParseDouble(root.Element("destination")?.Element("pos_lat")?.Value),
                ArrivalLon   = ParseDouble(root.Element("destination")?.Element("pos_long")?.Value),
                AlternateLat = ParseDouble(root.Element("alternate")?.Element("pos_lat")?.Value),
                AlternateLon = ParseDouble(root.Element("alternate")?.Element("pos_long")?.Value),
                AircraftType = root.Element("aircraft")?.Element("icaocode")?.Value ?? "",
                AircraftRegistration = root.Element("aircraft")?.Element("reg")?.Value ?? "",
                Route = root.Element("general")?.Element("route")?.Value ?? "",
                SID = root.Element("origin")?.Element("sid")?.Value ?? "",
                STAR = root.Element("destination")?.Element("star")?.Value ?? "",
                DepartureRunway = root.Element("origin")?.Element("plan_rwy")?.Value ?? "",
                ArrivalRunway   = root.Element("destination")?.Element("plan_rwy")?.Value ?? "",
                DepartureMetar = root.Element("weather")?.Element("orig_metar")?.Value ?? "",
                ArrivalMetar = root.Element("weather")?.Element("dest_metar")?.Value ?? ""
            };

            if (int.TryParse(root.Element("general")?.Element("initial_altitude")?.Value, out int alt))
                plan.CruiseAltitudeFt = alt;
            if (double.TryParse(root.Element("general")?.Element("route_distance")?.Value, out double dist))
                plan.PlannedDistanceNm = dist;

            // Parse waypoints from navlog
            var navlog = root.Element("navlog");
            if (navlog != null)
            {
                int seq = 0;
                foreach (var fix in navlog.Elements("fix"))
                {
                    var wp = new Waypoint
                    {
                        SequenceNumber = seq++,
                        Identifier = fix.Element("ident")?.Value ?? "",
                        Type = fix.Element("type")?.Value ?? "FIX"
                    };

                    if (double.TryParse(fix.Element("pos_lat")?.Value,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double lat2)) wp.Latitude = lat2;
                    if (double.TryParse(fix.Element("pos_long")?.Value,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double lon)) wp.Longitude = lon;

                    plan.Waypoints.Add(wp);
                }
            }

            Log.Information("[SimBrief] XML OFP parsed: {Dep}→{Arr}, {WP} waypoints",
                plan.DepartureICAO, plan.ArrivalICAO, plan.Waypoints.Count);

            return plan;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SimBrief] XML parse failed");
            throw;
        }
    }

    // =====================================================
    // PARSE JSON OFP FILE (exported from SimBrief)
    // =====================================================

    public static SimBriefFlightPlan? ParseJsonOFPFile(string filePath)
    {
        Log.Information("[SimBrief] Parsing JSON OFP file: {File}", filePath);
        var json = File.ReadAllText(filePath);
        return ParseSimBriefJson(json);
    }

    // =====================================================
    // OPEN SIMBRIEF DISPATCH IN BROWSER (pre-filled from booking)
    // Uses the dispatch.simbrief.com/options/custom URL accepted by
    // all SimBrief-integrated desktop tools (Navigraph Charts, vPilot, etc.)
    // =====================================================

    public void OpenDispatch(FlightBooking booking)
    {
        // ATC callsign = the pilot's generated booking callsign, e.g. "VAV64E"
        var callsign = booking.Callsign ?? "";
        var airline  = "VAV";

        // Flight number = the route's scheduled callsign number, e.g. "VAV103" → "103"
        // Falls back to stripping the prefix off the generated callsign if no route callsign is set.
        var routeCs = booking.RouteCallsign;
        var fltnum  = !string.IsNullOrEmpty(routeCs) && routeCs.StartsWith("VAV", StringComparison.OrdinalIgnoreCase) && routeCs.Length > 3
            ? routeCs[3..]
            : (callsign.StartsWith("VAV", StringComparison.OrdinalIgnoreCase) && callsign.Length > 3 ? callsign[3..] : callsign);

        // Deterministic static_id — allows fetching this specific plan later by ID
        // rather than "latest by username", avoiding ambiguity if multiple plans exist.
        var staticId = $"AVT_{booking.OriginIata}_{booking.DestIata}_{booking.ScheduledDepUtc:yyyyMMdd}";

        var dep = booking.ScheduledDepUtc;
        // SimBrief date format: ddMMMyy in uppercase, e.g. "29MAR26"
        var dateStr = dep.ToString("ddMMMyy", System.Globalization.CultureInfo.InvariantCulture)
                         .ToUpperInvariant();

        const string remarks = "TCAS CS=AVIATESAIR=FLYAVIATESAIR=FLYAVIATESAIR.UK";

        var url = "https://dispatch.simbrief.com/options/custom" +
                  // ── Route ────────────────────────────────────────────
                  $"?orig={Uri.EscapeDataString(booking.OriginIata)}" +
                  $"&dest={Uri.EscapeDataString(booking.DestIata)}" +
                  // ── Flight identity ──────────────────────────────────
                  $"&airline={Uri.EscapeDataString(airline)}" +
                  $"&fltnum={Uri.EscapeDataString(fltnum)}" +
                  $"&callsign={Uri.EscapeDataString(callsign)}" +
                  // ── Schedule ─────────────────────────────────────────
                  $"&date={dateStr}" +
                  $"&deph={dep.Hour:D2}" +
                  $"&depm={dep.Minute:D2}" +
                  // ── Aircraft ─────────────────────────────────────────
                  $"&type={Uri.EscapeDataString(ToSimBriefType(booking.AircraftType))}" +
                  (!string.IsNullOrWhiteSpace(booking.Registration)
                      ? $"&reg={Uri.EscapeDataString(booking.Registration)}"
                      : "") +
                  // ── Plan options ─────────────────────────────────────
                  "&flightrules=i" +        // IFR
                  "&flighttype=s" +         // Scheduled service
                  "&navlog=1" +             // Detailed navlog (needed for ACARS waypoint sync)
                  "&maps=detail" +          // Detailed flight maps
                  "&notams=1" +             // Include NOTAMs
                  "&stepclimbs=1" +         // Step climbs for long-haul efficiency
                  // ── Remarks ──────────────────────────────────────────
                  $"&manualrmk={Uri.EscapeDataString(remarks)}" +
                  // ── Reference ────────────────────────────────────────
                  $"&static_id={Uri.EscapeDataString(staticId)}";

        Log.Information("[SimBrief] Opening dispatch for {Orig}→{Dest} ({Type}) static_id={Id}",
            booking.OriginIata, booking.DestIata, booking.AircraftType, staticId);

        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    // =====================================================
    // FETCH LIVE METAR FROM AVIATIONWEATHER.GOV (free, no key)
    // =====================================================

    public async Task<string?> FetchMetarAsync(string icao)
    {
        if (string.IsNullOrWhiteSpace(icao)) return null;
        try
        {
            var request = new RestRequest("https://aviationweather.gov/api/data/metar")
                .AddParameter("ids", icao.Trim().ToUpperInvariant())
                .AddParameter("format", "raw")
                .AddParameter("hours", "2");

            var response = await _client.GetAsync(request);
            if (!response.IsSuccessful || string.IsNullOrWhiteSpace(response.Content))
                return null;

            // Response is plain-text METAR(s); take the first line
            var line = response.Content
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(l => l.TrimStart().StartsWith(icao.Trim().ToUpperInvariant(),
                    StringComparison.OrdinalIgnoreCase));

            return string.IsNullOrWhiteSpace(line) ? response.Content.Trim() : line.Trim();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[SimBrief] METAR fetch failed for {ICAO}", icao);
            return null;
        }
    }
}
