namespace AviatesAirTracker.Models;

// ============================================================
// LIVE MAP — DTOs for GET /api/acars/live-positions
// Mirrors the LiveMapRoom Durable Object's response shape.
// ============================================================

public class LivePilotPosition
{
    public string PilotId       { get; set; } = "";
    public string PilotName     { get; set; } = "";
    public double Lat           { get; set; }
    public double Lon           { get; set; }
    public int    Alt           { get; set; }
    public int    Speed         { get; set; }
    public int    Heading       { get; set; }
    public string Phase         { get; set; } = "";
    public string? DepartureIcao { get; set; }
    public string? ArrivalIcao   { get; set; }
    public string? Callsign      { get; set; }
    public List<LiveWaypoint>? Waypoints { get; set; }
    public DateTime UpdatedAt   { get; set; }
}

public class LiveWaypoint
{
    public double Lat { get; set; }
    public double Lon { get; set; }
    public string Id  { get; set; } = "";
}
