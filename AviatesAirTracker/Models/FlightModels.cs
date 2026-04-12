using AviatesAirTracker.Core.SimConnect;
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
        >= 75 => "#3D7EEE",
        >= 60 => "#EAB308",
        >= 40 => "#F97316",
        _ => "#EF4444"
    };
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
}
