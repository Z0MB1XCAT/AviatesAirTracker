using AviatesAirTracker.Core.SimConnect;
using System.Text.Json.Serialization;
namespace AviatesAirTracker.Models;

// ============================================================
// FLIGHT RECORD
// Primary flight session data model
// This is what gets stored / later sent to backend API
// ============================================================

public class FlightRecord
{
    // =====================================================
    // IDENTITY
    // =====================================================
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FlightNumber { get; set; } = "";
    public string Callsign { get; set; } = "";
    public string PilotName { get; set; } = "";
    public string PilotId { get; set; } = "";

    // =====================================================
    // AIRPORTS
    // =====================================================
    public string DepartureICAO { get; set; } = "";
    public string ArrivalICAO { get; set; } = "";
    public string AlternateICAO { get; set; } = "";
    public string DepartureName { get; set; } = "";
    public string ArrivalName { get; set; } = "";

    // =====================================================
    // AIRCRAFT
    // =====================================================
    public string AircraftType { get; set; } = "";
    public string AircraftRegistration { get; set; } = "";
    public string AircraftTitle { get; set; } = "";  // MSFS aircraft name

    // =====================================================
    // TIMES
    // =====================================================
    public DateTime BlockOutTime { get; set; }
    public DateTime BlockInTime { get; set; }
    public DateTime TakeoffTime { get; set; }
    public DateTime LandingTime { get; set; }

    // Backfilled from BookingService.ActiveBooking at engine start (FlightSessionManager.
    // OnEnginesStarted), same as FlightNumber/Callsign -- null for a charter/free flight with no
    // booking behind it. Drives PilotStatsService's on-time percentage: BlockOutTime compared
    // against this, within OnTimeToleranceMinutes.
    public DateTime? ScheduledDepUtc { get; set; }
    // MAJOR-08: Was BlockInTime - BlockOutTime with no guard. DateTime.MinValue - UtcNow = deeply negative.
    // Return zero for in-progress flights where the end time hasn't been set yet.
    public TimeSpan BlockTime => BlockInTime > BlockOutTime ? BlockInTime - BlockOutTime : TimeSpan.Zero;
    public TimeSpan AirTime   => LandingTime > TakeoffTime  ? LandingTime - TakeoffTime  : TimeSpan.Zero;

    // =====================================================
    // PERFORMANCE SUMMARY
    // =====================================================
    public double MaxAltitudeFt { get; set; }
    public double CruiseAltitudeFt { get; set; }
    public double MaxIASKts { get; set; }
    public double MaxMach { get; set; }
    public double MaxVerticalSpeedFPM { get; set; }
    public double MinVerticalSpeedFPM { get; set; }

    // =====================================================
    // FUEL
    // =====================================================
    public double FuelDepartureLbs { get; set; }
    public double FuelArrivalLbs { get; set; }
    public double FuelUsedLbs { get; set; }
    public double FuelBurnAvgPPH { get; set; }
    public double FuelBurnPerNmLbs { get; set; }

    // =====================================================
    // ROUTE
    // =====================================================
    public string PlannedRoute { get; set; } = "";
    public double PlannedDistanceNm { get; set; }
    public double ActualDistanceNm { get; set; }

    // =====================================================
    // SIMBRIEF DATA
    // =====================================================
    public SimBriefFlightPlan? PlannedFlight { get; set; }

    // =====================================================
    // LANDING
    // =====================================================
    public LandingResult? PrimaryLanding { get; set; }
    public List<LandingResult> AllLandings { get; set; } = [];

    // =====================================================
    // TELEMETRY PATH (for map/replay)
    // =====================================================
    public List<PathPoint> FlightPath { get; set; } = [];

    // =====================================================
    // STATUS
    // =====================================================
    public FlightStatus Status { get; set; } = FlightStatus.InProgress;
    public bool IsDiverted { get; set; }
    public string Notes { get; set; } = "";

    // =====================================================
    // BACKEND SYNC
    // =====================================================
    // TODO: Connect to Aviates Air backend SQL database
    // When backend is available, set SyncedToBackend = true after upload
    public bool SyncedToBackend { get; set; } = false;
    public string? BackendRecordId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // =====================================================
    // COMPLIANCE (general, flight-wide checks -- see LandingResult below for the
    // landing-specific ones). Populated once at flight completion by
    // FlightComplianceMonitor + FlightSessionManager.OnFlightComplete's late-departure check.
    // Never blocks or rejects PIREP submission -- purely a "flag for review" surface.
    // =====================================================
    public List<string> FlightComplianceViolations { get; set; } = [];
    public bool RequiresReview => FlightComplianceViolations.Count > 0 || AllLandings.Any(l => l.RequiresReview);

    // =====================================================
    // EVENT FLIGHT (see EventReminderService)
    // Set when the pilot confirms attendance at the 10-minutes-before popup for an event they're
    // registered for; null for an ordinary flight. HoursMultiplier is applied only in local stat
    // calculations (PilotStatsService.ComputeAsync, DashboardViewModel) -- it never touches the
    // raw BlockTime/AirTime themselves or anything submitted in the PIREP.
    // =====================================================
    public int? EventId { get; set; }
    public bool IsOfficialEvent { get; set; }

    // Charter flight (RoutesPage's Charter button/CharterBookingDialog -- pilot picks both route
    // and aircraft themselves) earns 0.5x hours. Mutually exclusive with EventId in practice
    // (FlightSessionManager.OnEnginesStarted checks the event flag first and only falls back to
    // charter if no event was armed), but both read HoursMultiplier the same way.
    public bool IsCharterFlight { get; set; }
    public double HoursMultiplier { get; set; } = 1.0;
}

public enum FlightStatus
{
    InProgress,
    Completed,
    Diverted,
    Aborted,
    Crashed
}

// ============================================================
// LANDING RESULT
// ============================================================

public class LandingResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; }
    public string FlightId { get; set; } = "";

    // Core parameters
    public double VerticalSpeedFPM { get; set; }
    public double GroundSpeedKts { get; set; }
    public double IASKts { get; set; }
    public double TouchdownPitchDeg { get; set; }
    public double TouchdownBankDeg { get; set; }
    public double TouchdownLatitude { get; set; }
    public double TouchdownLongitude { get; set; }

    // Wind
    public double HeadwindComponent { get; set; }
    public double CrosswindComponent { get; set; }
    public double WindSpeedKts { get; set; }
    public double WindDirectionDeg { get; set; }

    // Approach quality
    public bool ApproachWasStable { get; set; }
    public bool FlareDetected { get; set; }
    public int BounceCount { get; set; }

    // Rollout
    public double RolloutDistanceFt { get; set; }
    public double AverageDecelerationKtPerSec { get; set; }

    // Runway
    public string RunwayIdentifier { get; set; } = "";
    public string AirportICAO { get; set; } = "";
    public double ThresholdDistanceFt { get; set; }

    // Score
    public int LandingScore { get; set; }
    public LandingScoreBreakdown ScoreBreakdown { get; set; } = new();

    // Fuel
    public double FuelOnTouchdownLbs { get; set; }

    // =====================================================
    // COMPLIANCE — populated by LandingComplianceChecker at rollout completion, right before
    // LandingAnalyzer fires LandingDetected. Never blocks the landing/PIREP itself -- these
    // just decide whether ComplianceViolations ends up non-empty (which flags the flight for
    // review via FlightRecord.RequiresReview).
    // =====================================================

    // Signed lateral distance from the extended runway centerline at touchdown, in feet.
    // Positive = right of centerline, negative = left (same sign convention as
    // CrosswindComponent's own "positive = from the right"). Only meaningful when a runway was
    // actually detected -- stays 0 otherwise.
    public double CenterlineDeviationFt { get; set; }

    // Whether flaps/gear/speed were in the landing configuration by 1000ft AGL. Default true
    // (not false) when there's no approach-history sample at/below 1000ft to check against
    // (e.g. a very short/steep approach) -- missing data should not read as a violation.
    public bool FlapsConfiguredBy1000Ft { get; set; } = true;
    public bool GearDownBy1000Ft { get; set; } = true;
    public bool SpeedStabilizedBy1000Ft { get; set; } = true;

    // Human-readable reasons a pilot/reviewer can read directly -- e.g. "Hard landing: touchdown
    // vertical speed was 912 fpm (limit 800 fpm)." Empty when the landing violated nothing.
    public List<string> ComplianceViolations { get; set; } = [];
    public bool RequiresReview => ComplianceViolations.Count > 0;

    // Heading vs ground track at touchdown -- their difference is the crab (de-crab) angle.
    public double TouchdownHeadingTrueDeg { get; set; }
    public double TouchdownTrackTrueDeg { get; set; }

    // Approach + rollout trace for the landing report's profile plots (LandingAnalyzer:
    // ~1 Hz on the approach, 4 Hz around touchdown, ~1 Hz on the rollout; T is seconds
    // relative to the primary touchdown). NOT serialized with the landing/flight records --
    // JsonLandingRepository keeps it in its own per-landing file so landings.json and
    // flights.json (which embeds PrimaryLanding/AllLandings) stay small. Empty for landings
    // recorded before the trace existed, or synced from the backend.
    [JsonIgnore]
    public List<LandingTracePoint> Trace { get; set; } = [];

    // ---- Derived ----
    public string LandingGrade => LandingScore switch
    {
        >= 90 => "EXCELLENT",
        >= 75 => "GOOD",
        >= 60 => "FAIR",
        >= 40 => "HARD",
        _ => "VERY HARD"
    };

    public string LandingGradeColor => LandingScore switch
    {
        >= 90 => "#22C55E",
        >= 75 => "#4D9BEC",
        >= 60 => "#EAB308",
        >= 40 => "#F97316",
        _ => "#EF4444"
    };
}

/// <summary>One sample of the landing trace. Short JSON names: a trace is a few hundred of these.</summary>
public sealed class LandingTracePoint
{
    [JsonPropertyName("t")]   public double T { get; set; }          // s relative to touchdown (negative = before)
    [JsonPropertyName("agl")] public double AglFt { get; set; }
    [JsonPropertyName("ias")] public double IasKts { get; set; }
    [JsonPropertyName("gs")]  public double GsKts { get; set; }
    [JsonPropertyName("vs")]  public double VsFpm { get; set; }
    [JsonPropertyName("pit")] public double PitchDeg { get; set; }
    [JsonPropertyName("bnk")] public double BankDeg { get; set; }
    [JsonPropertyName("lat")] public double Lat { get; set; }
    [JsonPropertyName("lon")] public double Lon { get; set; }
    [JsonPropertyName("flp")] public double FlapsPct { get; set; }
    [JsonPropertyName("gear")] public bool GearDown { get; set; }
    [JsonPropertyName("gnd")] public bool OnGround { get; set; }
}

public class LandingScoreBreakdown
{
    public double VerticalSpeedScore { get; set; }
    public double PitchScore { get; set; }
    public double BankScore { get; set; }
    public double SpeedScore { get; set; }
    public double CrosswindScore { get; set; }
    public double StabilityScore { get; set; }
    public int TotalScore { get; set; }
}

// ============================================================
// RUNWAY INFO
// ============================================================

public class RunwayInfo
{
    public string AirportICAO { get; set; } = "";
    public string Identifier { get; set; } = "";
    public double HeadingTrue { get; set; }
    public double ThresholdLatitude { get; set; }
    public double ThresholdLongitude { get; set; }
    public double EndLatitude { get; set; }
    public double EndLongitude { get; set; }
    public double LengthFt { get; set; }
    public double WidthFt { get; set; }
    public string SurfaceType { get; set; } = "";
}

// ============================================================
// SIMBRIEF FLIGHT PLAN
// ============================================================

public class SimBriefFlightPlan
{
    public string GeneratedBy { get; set; } = "";
    public DateTime GeneratedAt { get; set; }
    public string OFPNumber { get; set; } = "";
    // Concatenation of general/icao_airline + general/flight_number (e.g. "BAW123"). Deliberately
    // NOT atc/callsign, which can be virtual-airline/user-customized. Empty if either source field
    // is missing on the OFP.
    public string FlightNumber { get; set; } = "";

    // Route
    public string DepartureICAO { get; set; } = "";
    public string ArrivalICAO { get; set; } = "";
    public string AlternateICAO { get; set; } = "";
    public double DepartureLat { get; set; }
    public double DepartureLon { get; set; }
    public double ArrivalLat { get; set; }
    public double ArrivalLon { get; set; }
    public double AlternateLat { get; set; }
    public double AlternateLon { get; set; }
    public string Route { get; set; } = "";
    public string SID { get; set; } = "";
    public string STAR { get; set; } = "";
    public string DepartureRunway { get; set; } = "";
    public string ArrivalRunway { get; set; } = "";
    public List<Waypoint> Waypoints { get; set; } = [];

    // Aircraft
    public string AircraftType { get; set; } = "";
    public string AircraftRegistration { get; set; } = "";

    // Performance
    public int CruiseAltitudeFt { get; set; }
    public double CruiseMach { get; set; }
    public int? CostIndex { get; set; }
    public int PlannedFlightTimeMin { get; set; }
    public double PlannedDistanceNm { get; set; }

    // Fuel
    public double TakeoffFuelLbs { get; set; }
    public double LandingFuelLbs { get; set; }
    public double ContingencyFuelLbs { get; set; }
    public double AlternateFuelLbs { get; set; }
    public double ReserveFuelLbs { get; set; }

    // Weather
    public string DepartureMetar { get; set; } = "";
    public string ArrivalMetar { get; set; } = "";
    public int AverageWindAtCruiseKts { get; set; }
    public int AverageWindDirectionAtCruise { get; set; }

    // Timing — parsed from SimBrief /times/sched_out (scheduled off-block time, Unix timestamp)
    public DateTime? ScheduledDepartureUtc { get; set; }

    // Takeoff performance inputs — extracted from OFP weights/origin
    public double TakeoffWeightKg { get; set; }
    public int    DepartureRunwayLengthM { get; set; }
    public int    DepartureElevationFt { get; set; }
    public int    DepartureTransitionAltFt { get; set; }   // origin/trans_alt; 0 when the OFP has none

    // Landing performance inputs — same, for the destination
    public int    ArrivalRunwayLengthM { get; set; }
    public int    ArrivalElevationFt { get; set; }

    // Weights — extracted from OFP weights section, used by the Hoppies loadsheet
    public double ZeroFuelWeightKg { get; set; }
    public int    PaxCount { get; set; }
    public double CargoKg { get; set; }

    // OFP content
    public string OFPText { get; set; } = "";
    public List<string> NOTAMs { get; set; } = [];

    // Direct link to the generated OFP PDF (files.directory + files.pdf.link).
    // Used by the EFB "OFP" app to display the briefing package.
    public string PdfUrl { get; set; } = "";

    // ── Briefing package (EFB Pilot Brief) — JSON OFP only; 0 / "" when absent ──
    public bool   OfpUnitsKg { get; set; }                 // params.units == "kgs"
    public string Callsign { get; set; } = "";              // atc/callsign
    public string AircraftName { get; set; } = "";          // aircraft/name
    public string DepartureName { get; set; } = "";
    public string ArrivalName { get; set; } = "";
    public string AlternateName { get; set; } = "";
    public double TaxiFuelLbs { get; set; }                 // fuel/taxi
    public double TripFuelLbs { get; set; }                 // fuel/enroute_burn
    public double ExtraFuelLbs { get; set; }                // fuel/extra
    public double MinTakeoffFuelLbs { get; set; }           // fuel/min_takeoff
    public double PlanTakeoffFuelLbs { get; set; }          // fuel/plan_takeoff
    public double MaxZeroFuelWeightKg { get; set; }         // weights/max_zfw
    public double MaxTakeoffWeightKg { get; set; }          // weights/max_tow
    public double LandingWeightKg { get; set; }             // weights/est_ldw
    public double MaxLandingWeightKg { get; set; }          // weights/max_ldw
    public double PayloadKg { get; set; }                   // weights/payload
    public DateTime? ScheduledArrivalUtc { get; set; }      // times/sched_in
    public int    BlockTimeMin { get; set; }                // times/est_block
    public int    AvgWindComponentKts { get; set; }         // general/avg_wind_comp (+ tail)
    public string DepartureTaf { get; set; } = "";
    public string ArrivalTaf { get; set; } = "";
    public string AlternateMetar { get; set; } = "";
    public string AlternateTaf { get; set; } = "";
}

// ============================================================
// WAYPOINT
// ============================================================

public class Waypoint
{
    public string Identifier { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string Type { get; set; } = "";  // FIX, NAVAID, AIRPORT, etc.
    public int? AltitudeConstraintFt { get; set; }
    public double? SpeedConstraintKts { get; set; }
    public int SequenceNumber { get; set; }
    public bool IsPassed { get; set; }
}

// ============================================================
// PATH POINT (for map rendering / replay)
// ============================================================

public class PathPoint
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double AltitudeMSL { get; set; }
    public double GroundSpeed { get; set; }
    public double VerticalSpeed { get; set; }
    public FlightPhase Phase { get; set; }
    public DateTime Timestamp { get; set; }
    public float Heading { get; set; }
}

// ============================================================
// MESSAGING
// ============================================================

public class PilotMessage
{
    public Guid     Id          { get; set; } = Guid.NewGuid();
    public string   SenderId    { get; set; } = "";
    public string   SenderName  { get; set; } = "";
    public string   RecipientId { get; set; } = ""; // PilotId or "broadcast"
    public string   Content     { get; set; } = "";
    public DateTime SentAt      { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt     { get; set; }
    public bool     IsModerated { get; set; }        // set server-side
    public MessageType Type     { get; set; }
    public bool     SyncedToBackend { get; set; }    // false until confirmed by backend
    public DateTime? LastSyncAttempt { get; set; }   // for retry logic
}

public enum MessageType { Direct, Broadcast, System }

// ============================================================
// FRIENDS
// ============================================================

public class FriendEntry
{
    public string   PilotId    { get; set; } = "";
    public string   PilotName  { get; set; } = "";
    public string   FriendCode { get; set; } = "";
    public string   Rank       { get; set; } = "";
    public bool     IsOnline   { get; set; }
    public DateTime AddedAt    { get; set; } = DateTime.UtcNow;
}

// ============================================================
// FLIGHT DELETION REQUEST
// Pilots submit a reason; the request must be approved before
// the flight record is actually removed.
// ============================================================

public class FlightDeletionRequest
{
    public Guid Id          { get; set; } = Guid.NewGuid();
    public Guid FlightId    { get; set; }
    public string FlightNumber { get; set; } = "";
    public string Route     { get; set; } = "";   // e.g. "EGLL → KJFK"
    public string Reason    { get; set; } = "";
    public DeletionRequestStatus Status { get; set; } = DeletionRequestStatus.Pending;
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAt { get; set; }
}

public enum DeletionRequestStatus { Pending, Approved, Rejected }

// ============================================================
// PILOT STATISTICS
// ============================================================

public class PilotStatistics
{
    public string PilotId { get; set; } = "";
    public string PilotName { get; set; } = "";

    // Totals
    public int TotalFlights { get; set; }
    public double TotalHoursBlock { get; set; }
    public double TotalHoursAir { get; set; }
    public double TotalDistanceNm { get; set; }
    public double TotalFuelUsedLbs { get; set; }

    // Landing stats
    public int TotalLandings { get; set; }
    public double AverageLandingScore { get; set; }
    public double BestLandingScore { get; set; }
    public double WorstLandingVSFPM { get; set; }
    public double AverageLandingVSFPM { get; set; }
    public double BestLandingVSFPM { get; set; }

    // Routes
    public List<string> FrequentRoutes { get; set; } = [];
    public List<string> FlightsByAircraft { get; set; } = [];

    // Rank
    public string Rank { get; set; } = "First Officer";
    public double RankProgress { get; set; }  // 0-1 progress to next rank

    // Last updated
    public DateTime LastFlight { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    // On-time performance -- only meaningful for flights that actually had a scheduled
    // departure (a real booking), so ScheduledFlightsCount can be less than TotalFlights (a
    // charter/free flight has nothing to be "on time" against). Null when ScheduledFlightsCount
    // is 0, so the UI can show "--" instead of a fabricated percentage with no flights behind it.
    public int ScheduledFlightsCount { get; set; }
    public double? OnTimePercent { get; set; }
}
