namespace AviatesAirTracker.Core.Efb;

// ============================================================
// EFB DATA-TRANSFER OBJECTS
//
// Plain serializable snapshots pushed to the EFB tablet over the
// WebSocket / REST API. Property names are PascalCase here and
// emitted as camelCase by the server's JSON options.
//
// These types are intentionally decoupled from the internal
// TelemetrySnapshot / SimBriefFlightPlan so the wire format can
// evolve independently of the sim pipeline.
// ============================================================

/// <summary>The complete live picture the EFB renders — one object, pushed at ~4 Hz.</summary>
public sealed class EfbSnapshot
{
    // ── Server / session ────────────────────────────────────
    public DateTime ServerTimeUtc { get; set; } = DateTime.UtcNow;
    /// <summary>True when fresh telemetry is arriving from MSFS.</summary>
    public bool SimConnected { get; set; }
    /// <summary>True when a flight session is in progress (not Idle/Complete).</summary>
    public bool HasActiveFlight { get; set; }
    /// <summary>FlightSessionManager state name (Idle, PreFlight, Taxiing, Airborne…).</summary>
    public string SessionState { get; set; } = "Idle";

    // ── Flight identity ─────────────────────────────────────
    public string Callsign { get; set; } = "";
    public string FlightNumber { get; set; } = "";
    public string Registration { get; set; } = "";
    public string AircraftType { get; set; } = "";
    public string AircraftName { get; set; } = "";
    public string DepartureIcao { get; set; } = "";
    public string DepartureName { get; set; } = "";
    public string ArrivalIcao { get; set; } = "";
    public string ArrivalName { get; set; } = "";
    public string AlternateIcao { get; set; } = "";

    // ── Live flight data ────────────────────────────────────
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double AltitudeFt { get; set; }         // pressure altitude
    public double AltitudeAglFt { get; set; }
    public double IasKts { get; set; }
    public double TasKts { get; set; }
    public double GroundKts { get; set; }
    public double Mach { get; set; }
    public double HeadingDeg { get; set; }         // magnetic
    public double TrackDeg { get; set; }
    public double VerticalSpeedFpm { get; set; }
    public double PitchDeg { get; set; }
    public double BankDeg { get; set; }
    public string Phase { get; set; } = "Unknown";
    public string PhaseColor { get; set; } = "#4A5568";

    // ── Fuel ────────────────────────────────────────────────
    public double FuelLbs { get; set; }
    public double FuelPct { get; set; }
    public double FuelBurnPph { get; set; }

    // ── Configuration ───────────────────────────────────────
    public string Gear { get; set; } = "UP";
    public double FlapsPct { get; set; }
    public double SpoilersPct { get; set; }
    public bool ApMaster { get; set; }
    public string ApModes { get; set; } = "OFF";
    public string Squawk { get; set; } = "2000";

    // ── Weather (live from sim) ─────────────────────────────
    public double WindDirDeg { get; set; }
    public double WindKts { get; set; }
    public double HeadwindKts { get; set; }
    public double CrosswindKts { get; set; }
    public double OatC { get; set; }
    public double QnhMb { get; set; }

    // ── Progress (computed from plan + position) ────────────
    public double PlannedDistanceNm { get; set; }
    public double DistanceRemainingNm { get; set; }
    public double ProgressPct { get; set; }
    public int EteMinutes { get; set; }            // remaining, from current GS
    public DateTime? EtaUtc { get; set; }
    public int CruiseAltitudeFt { get; set; }
    public double BearingToDestDeg { get; set; }

    // ── Descent & fuel advisor (EFB-exclusive, computed live) ──
    /// <summary>Distance from the current position to the computed top-of-descent, nm. 0 when past/NA.</summary>
    public double TodDistanceNm { get; set; }
    /// <summary>Minutes until top-of-descent at the current ground speed.</summary>
    public int TodMinutes { get; set; }
    /// <summary>True once the aircraft is at or past the computed descent point.</summary>
    public bool DescendNow { get; set; }
    /// <summary>Required descent gradient to make the destination from here, ft/nm.</summary>
    public double RequiredDescentFtPerNm { get; set; }
    /// <summary>Predicted fuel remaining at touchdown, lb, from current burn + distance.</summary>
    public double PredictedLandingFuelLbs { get; set; }
    /// <summary>Planned landing fuel from the OFP, lb (0 if no plan).</summary>
    public double PlannedLandingFuelLbs { get; set; }
    /// <summary>"OK" | "CAUTION" | "LOW" — predicted arrival fuel vs plan/reserve.</summary>
    public string FuelStatus { get; set; } = "OK";
}

/// <summary>Static-ish pairing / server info returned by /efb/api/hello.</summary>
public sealed class EfbServerInfo
{
    public string AppName { get; set; } = "AviatesAir EFB";
    public string Version { get; set; } = "";
    public string PilotName { get; set; } = "";
    public string Airline { get; set; } = "AviatesAir";
    public bool TokenValid { get; set; }
}

/// <summary>The SimBrief-derived flight plan the EFB shows (subset of the internal plan).</summary>
public sealed class EfbFlightPlan
{
    public bool HasPlan { get; set; }
    public string DepartureIcao { get; set; } = "";
    public string ArrivalIcao { get; set; } = "";
    public string AlternateIcao { get; set; } = "";
    public string Route { get; set; } = "";
    public string AircraftType { get; set; } = "";
    public string Registration { get; set; } = "";
    public int CruiseAltitudeFt { get; set; }
    public double CruiseMach { get; set; }
    public int? CostIndex { get; set; }
    public double PlannedDistanceNm { get; set; }
    public int PlannedFlightTimeMin { get; set; }
    public double RampFuelLbs { get; set; }
    public double LandingFuelLbs { get; set; }
    public double TakeoffWeightKg { get; set; }
    public double ZeroFuelWeightKg { get; set; }
    public int PaxCount { get; set; }
    public double CargoKg { get; set; }
    public string DepartureRunway { get; set; } = "";
    public string ArrivalRunway { get; set; } = "";
    // Field data used to autofill the performance apps
    public int DepartureElevationFt { get; set; }
    public int DepartureRunwayLengthM { get; set; }
    public int ArrivalElevationFt { get; set; }
    public int ArrivalRunwayLengthM { get; set; }
    public string DepartureMetar { get; set; } = "";
    public string ArrivalMetar { get; set; } = "";
    public DateTime? ScheduledDepartureUtc { get; set; }
    public string PdfUrl { get; set; } = "";
    /// <summary>True when the plan was imported inside the EFB (not from the ACARS app).</summary>
    public bool FromEfbImport { get; set; }
    public List<EfbWaypoint> Waypoints { get; set; } = [];
}

// ============================================================
// MESSAGING (mirrors the desktop MessagingService / friend system)
// ============================================================

public sealed class EfbMessagingState
{
    public string MyPilotId { get; set; } = "";
    public string MyPilotName { get; set; } = "";
    public string MyFriendCode { get; set; } = "";
    public int UnreadCount { get; set; }
    public List<EfbFriend> Friends { get; set; } = [];
    public List<EfbMessageDto> Messages { get; set; } = [];   // DMs + broadcasts, oldest first
}

public sealed class EfbFriend
{
    public string PilotId { get; set; } = "";
    public string PilotName { get; set; } = "";
    public string FriendCode { get; set; } = "";
    public string Rank { get; set; } = "";
    public bool IsOnline { get; set; }
}

public sealed class EfbMessageDto
{
    public string Id { get; set; } = "";
    public string SenderId { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string RecipientId { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime SentAt { get; set; }
    public bool IsRead { get; set; }
    public bool Mine { get; set; }
    public string Type { get; set; } = "Direct";   // Direct | Broadcast | System
}

// ============================================================
// LIVE TRAFFIC (for the EFB map — mirrors the app's live map)
// ============================================================

public sealed class EfbTraffic
{
    public string PilotId { get; set; } = "";
    public string PilotName { get; set; } = "";
    public string Callsign { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public int Alt { get; set; }
    public int Speed { get; set; }
    public int Heading { get; set; }
    public string Phase { get; set; } = "";
    public string DepartureIcao { get; set; } = "";
    public string ArrivalIcao { get; set; } = "";
}

// ============================================================
// ONLINE NETWORK TRAFFIC/ATC (VATSIM + IVAO, for the EFB map's network layers)
// Separate from EfbTraffic above, which is this app's OWN pilots (OtherPilotsService,
// AviatesAir's backend) -- these are the two real online ATC networks instead.
// ============================================================

public sealed class EfbNetworkPilot
{
    public string Network { get; set; } = "";   // "VATSIM" | "IVAO"
    public string Callsign { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public int AltitudeFt { get; set; }
    public int GroundSpeedKts { get; set; }
    public int HeadingDeg { get; set; }
    public string AircraftType { get; set; } = "";
    public string DepartureIcao { get; set; } = "";
    public string ArrivalIcao { get; set; } = "";
}

public sealed class EfbNetworkAtc
{
    public string Network { get; set; } = "";   // "VATSIM" | "IVAO"
    public string Callsign { get; set; } = "";
    public string Position { get; set; } = "";  // DEL/GND/TWR/APP/DEP/CTR/FSS, best-effort
    public double Frequency { get; set; }
    // Null when the ATC's position could not be resolved (VATSIM controllers carry no lat/lon of
    // their own -- see EfbNetworkTrafficService for how this is joined from the transceivers feed).
    public double? Lat { get; set; }
    public double? Lon { get; set; }
}

public sealed class EfbNetworkTraffic
{
    public List<EfbNetworkPilot> Pilots { get; set; } = [];
    public List<EfbNetworkAtc> Atcs { get; set; } = [];
}

public sealed class EfbWaypoint
{
    public int Seq { get; set; }
    public string Ident { get; set; } = "";
    public string Type { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public int AltitudeFt { get; set; }
}

// ============================================================
// GREAT-CIRCLE HELPERS
// ============================================================

public static class GeoMath
{
    private const double EarthRadiusNm = 3440.065;

    public static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = Deg2Rad(lat2 - lat1);
        double dLon = Deg2Rad(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(Deg2Rad(lat1)) * Math.Cos(Deg2Rad(lat2)) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return EarthRadiusNm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    public static double InitialBearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        double φ1 = Deg2Rad(lat1), φ2 = Deg2Rad(lat2);
        double Δλ = Deg2Rad(lon2 - lon1);
        double y = Math.Sin(Δλ) * Math.Cos(φ2);
        double x = Math.Cos(φ1) * Math.Sin(φ2) - Math.Sin(φ1) * Math.Cos(φ2) * Math.Cos(Δλ);
        double θ = Math.Atan2(y, x);
        return (Rad2Deg(θ) + 360) % 360;
    }

    private static double Deg2Rad(double d) => d * Math.PI / 180.0;
    private static double Rad2Deg(double r) => r * 180.0 / Math.PI;
}
