using System.Runtime.InteropServices;

namespace AviatesAirTracker.Core.SimConnect;

// ============================================================
// SIMCONNECT DATA STRUCTURE DEFINITIONS
// Maps directly to MSFS SimVar variables
// Sampling target: 10-20Hz
// ============================================================

/// <summary>
/// Primary aircraft state - sampled every 50ms (20Hz)
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct AircraftState
{
    // ---- POSITION ----
    public double Latitude;                 // PLANE LATITUDE (degrees)
    public double Longitude;               // PLANE LONGITUDE (degrees)
    public double AltitudeMSL;             // PLANE ALTITUDE (feet MSL)
    public double AltitudeAGL;             // PLANE ALT ABOVE GROUND (feet AGL)
    public double AltitudePressure;        // PRESSURE ALTITUDE (feet)

    // ---- ORIENTATION ----
    public double HeadingTrue;             // PLANE HEADING DEGREES TRUE (degrees)
    public double HeadingMagnetic;         // PLANE HEADING DEGREES MAGNETIC (degrees)
    public double TrackTrue;               // GPS GROUND TRUE TRACK (degrees)
    public double Pitch;                   // PLANE PITCH DEGREES (degrees, nose-up positive)
    public double Bank;                    // PLANE BANK DEGREES (degrees, right-positive)

    // ---- SPEEDS ----
    public double GroundSpeed;             // GPS GROUND SPEED (knots)
    public double TrueAirspeed;            // AIRSPEED TRUE (knots)
    public double IndicatedAirspeed;       // AIRSPEED INDICATED (knots)
    public double Mach;                    // AIRSPEED MACH
    public double VerticalSpeed;           // VERTICAL SPEED (feet/min)
    public double WindSpeed;               // AMBIENT WIND VELOCITY (knots)
    public double WindDirection;           // AMBIENT WIND DIRECTION (degrees)

    // ---- AIRCRAFT SYSTEMS ----
    public double ThrottlePct_1;           // GENERAL ENG THROTTLE LEVER POSITION:1 (percent 0-100)
    public double ThrottlePct_2;           // GENERAL ENG THROTTLE LEVER POSITION:2
    public double ThrottlePct_3;           // GENERAL ENG THROTTLE LEVER POSITION:3
    public double ThrottlePct_4;           // GENERAL ENG THROTTLE LEVER POSITION:4

    // ---- ENGINES ----
    public double EngineN1_1;              // ENG N1 RPM PCT:1
    public double EngineN1_2;
    public double EngineN1_3;
    public double EngineN1_4;
    public double EngineN2_1;              // ENG N2 RPM PCT:1
    public double EngineN2_2;
    public double EngineFuelFlow_1;        // ENG FUEL FLOW PPH:1 (lbs/hr)
    public double EngineFuelFlow_2;
    public double EngineFuelFlow_3;
    public double EngineFuelFlow_4;
    public double EngineRunning_1;         // ENG COMBUSTION:1 (bool)
    public double EngineRunning_2;
    public double EngineRunning_3;
    public double EngineRunning_4;

    // ---- FUEL ----
    public double FuelTotalLbs;            // FUEL TOTAL QUANTITY WEIGHT (lbs)
    public double FuelTotalGallons;        // FUEL TOTAL QUANTITY (gallons)
    public double FuelLeftMainLbs;         // FUEL LEFT MAIN QUANTITY
    public double FuelRightMainLbs;        // FUEL RIGHT MAIN QUANTITY
    public double FuelUsedLbs;             // ENG FUEL USED SINCE START (lbs)

    // ---- FLIGHT CONTROLS ----
    public double FlapsDegrees;            // FLAPS HANDLE INDEX (position index)
    public double FlapsPercent;            // TRAILING EDGE FLAPS LEFT PERCENT
    public double SpoilerPercent;          // SPOILERS HANDLE POSITION (percent)
    public double GearPosition;            // GEAR HANDLE POSITION (0=up, 1=down)
    public double GearNoseTouchdown;       // GEAR CENTER POSITION
    public double GearLeftTouchdown;       // GEAR LEFT POSITION
    public double GearRightTouchdown;      // GEAR RIGHT POSITION
    public double BrakesLeft;             // BRAKE LEFT POSITION EX1
    public double BrakesRight;            // BRAKE RIGHT POSITION EX1

    // ---- AUTOPILOT ----
    public double AutopilotMaster;         // AUTOPILOT MASTER (bool)
    public double AutopilotAltitudeLock;   // AUTOPILOT ALTITUDE LOCK (bool)
    public double AutopilotAltValue;       // AUTOPILOT ALTITUDE LOCK VAR
    public double AutopilotHeadingLock;    // AUTOPILOT HEADING LOCK (bool)
    public double AutopilotHeadingValue;   // AUTOPILOT HEADING LOCK DIR
    public double AutopilotSpeedLock;      // AUTOPILOT AIRSPEED HOLD (bool)
    public double AutopilotSpeedValue;     // AUTOPILOT AIRSPEED HOLD VAR
    public double AutopilotVSLock;         // AUTOPILOT VERTICAL HOLD
    public double AutopilotVSValue;        // AUTOPILOT VERTICAL HOLD VAR
    public double AutopilotNAV1;           // AUTOPILOT NAV1 LOCK
    public double AutopilotAPPR;           // AUTOPILOT APPROACH HOLD
    public double AutopilotLNAV;           // AUTOPILOT APPROACH CAPTURED (using for LNAV)
    public double AutopilotFLC;            // AUTOPILOT FLIGHT LEVEL CHANGE
    public double AutothrottleActive;      // AUTOPILOT THROTTLE ARM

    // ---- NAVIGATION ----
    public double NAV1Frequency;           // NAV ACTIVE FREQUENCY:1 (MHz)
    public double NAV2Frequency;           // NAV ACTIVE FREQUENCY:2
    public double COM1Frequency;           // COM ACTIVE FREQUENCY:1
    public double ILSLocalizerDeviation;   // NAV CDI:1 (deviation dots)
    public double ILSGlideSlopeDeviation;  // NAV GSI:1
    public double ILSLocalizerFlag;        // NAV LOCALIZER:1 (bool)
    public double ILSGlideSlopeFlag;       // NAV GS FLAG:1 (bool)
    public double DME1Distance;            // NAV DME:1 (nm)

    // ---- AMBIENT ----
    public double AmbientTemperature;      // AMBIENT TEMPERATURE (celsius)
    public double AmbientPressure;         // AMBIENT PRESSURE (millibars)
    public double SeaLevelPressure;        // SEA LEVEL PRESSURE (millibars)
    public double OAT;                     // TOTAL AIR TEMPERATURE (celsius)
    public double Visibility;              // AMBIENT VISIBILITY (meters)

    // ---- GROUND STATE ----
    public double SimOnGround;             // SIM ON GROUND (bool)
    public double OnGroundAnimation;       // ON ANY RUNWAY (bool)
    public double ParkingBrake;            // PARKING BRAKE INDICATOR (bool)
    public double Stall;                   // STALL WARNING (bool)
    public double Overspeed;               // OVERSPEED WARNING (bool)

    // ---- WEIGHT & BALANCE ----
    public double GrossWeight;             // TOTAL WEIGHT (lbs)
    public double EmptyWeight;             // EMPTY WEIGHT (lbs)
    public double MaxGrossWeight;          // MAX GROSS WEIGHT (lbs)
    public double CGPercent;               // CG PERCENT MAC

    // ---- TRANSPONDER / FMS ----
    public double TransponderCode;         // TRANSPONDER CODE:1
    public double GPSFlightPlanActive;     // GPS IS ACTIVE FLIGHT PLAN
    public double GPSNextWPDistance;       // GPS WP NEXT DISTANCE (nm)
    public double GPSNextWPBearing;        // GPS WP BEARING

    // ---- MISC ----
    public double SimulationRate;          // SIMULATION RATE
    public double LocalTime;              // LOCAL TIME (seconds since midnight)
    public double ZuluTime;               // ZULU TIME (seconds since midnight)
    public double PlaneInParkingState;     // PLANE IN PARKING STATE
}

/// <summary>
/// Secondary/slow-update data (refreshed every 1s)
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct AircraftIdentification
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string Title;                   // TITLE (aircraft title string)

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string ATCModel;               // ATC MODEL

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string ATCId;                  // ATC ID (callsign)

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string ATCAirline;             // ATC AIRLINE

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string ATCFlightNumber;        // ATC FLIGHT NUMBER

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string GPSActiveLeg;           // GPS WP NEXT ID
}

// ============================================================
// PROCESSED TELEMETRY SNAPSHOT
// Enriched version of raw AircraftState with computed values
// ============================================================

public class TelemetrySnapshot
{
    public DateTime Timestamp { get; set; }
    public AircraftState Raw { get; set; }

    // Derived position
    public double Latitude => Raw.Latitude;
    public double Longitude => Raw.Longitude;
    // PLANE ALTITUDE returns geometric/true altitude which diverges from pressure altitude
    // in non-ISA atmospheres (e.g. cold weather at FL370 can read ~2200ft low).
    // AltitudePressure uses PRESSURE ALTITUDE (1013.25 hPa reference) — always matches FL.
    public double AltitudeMSL => Raw.AltitudeMSL;
    public double AltitudePressure => Raw.AltitudePressure;
    public double AltitudeAGL => Raw.AltitudeAGL;

    // Derived speeds
    public double GroundSpeedKts => Raw.GroundSpeed;
    public double IASKts => Raw.IndicatedAirspeed;
    public double TASKts => Raw.TrueAirspeed;
    public double VerticalSpeedFPM => Raw.VerticalSpeed;

    // Aircraft state flags
    public bool IsOnGround => Raw.SimOnGround > 0.5;
    public bool IsParked => Raw.PlaneInParkingState > 0.5;
    public bool AutopilotEngaged => Raw.AutopilotMaster > 0.5;
    public bool GearDown => Raw.GearPosition > 0.5;
    public bool ParkingBrakeSet => Raw.ParkingBrake > 0.5;
    public bool EnginesRunning => Raw.EngineRunning_1 > 0.5 || Raw.EngineRunning_2 > 0.5;

    // Computed fuel
    public double FuelUsedLbs => Raw.FuelUsedLbs;
    public double FuelRemainingLbs => Raw.FuelTotalLbs;
    public double FuelBurnRatePPH => Raw.EngineFuelFlow_1 + Raw.EngineFuelFlow_2 + 
                                      Raw.EngineFuelFlow_3 + Raw.EngineFuelFlow_4;

    // ILS status
    public bool ILSCapturing => Raw.ILSLocalizerFlag > 0.5 && Raw.ILSGlideSlopeFlag > 0.5;
    public double LocalizerDevDots => Raw.ILSLocalizerDeviation;
    public double GlideSlopeDevDots => Raw.ILSGlideSlopeDeviation;

    // Engine state
    public double[] EngineN1 => [Raw.EngineN1_1, Raw.EngineN1_2, Raw.EngineN1_3, Raw.EngineN1_4];
    public double[] ThrottlePositions => [Raw.ThrottlePct_1, Raw.ThrottlePct_2, Raw.ThrottlePct_3, Raw.ThrottlePct_4];

    // Wind
    public double WindSpeedKts => Raw.WindSpeed;
    public double WindDirectionDeg => Raw.WindDirection;

    // Computed crosswind and headwind components
    public double HeadwindComponent { get; set; }  // Positive = headwind
    public double CrosswindComponent { get; set; } // Positive = right crosswind

    // Flight phase (set by FlightPhaseDetector)
    public FlightPhase Phase { get; set; }

    // Approach stability (set by ApproachMonitor)
    public bool ApproachStable { get; set; }
    public List<string> ApproachAlerts { get; set; } = [];
}

// ============================================================
// SIMCONNECT ENUM IDS
// ============================================================

public enum DataDefinitionId : uint
{
    AircraftState = 1,
    AircraftIdent = 2
}

public enum DataRequestId : uint
{
    AircraftState = 1,
    AircraftIdent = 2
}

public enum SystemEventId : uint
{
    SimStart = 10,
    SimStop = 11,
    Pause = 12,
    Unpaused = 13,
    CrashDetected = 14,
    AircraftLoaded = 15
}

// ============================================================
// SIMCONNECT CONNECTION STATUS
// ============================================================

public enum SimConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

// ============================================================
// FLIGHT PHASE ENUM
// ============================================================

public enum FlightPhase
{
    Parked,
    Pushback,
    Taxi,
    Takeoff,
    InitialClimb,
    Climb,
    Cruise,
    TopOfDescent,
    Descent,
    Approach,
    FinalApproach,
    Landing,
    Rollout,
    Vacating,
    Unknown
}

// ============================================================
// LANDING DETECTION STATE MACHINE
// ============================================================

public enum LandingState
{
    Airborne,
    FlareDetected,
    Touchdown,
    Rolling,
    Deceleration,
    Vacated
}
