using Newtonsoft.Json;

namespace AviatesAirTracker.Models;

// ============================================================
// LIVE MAP — DTOs for GET /api/acars/live-positions
// Mirrors the LiveMapRoom Durable Object's response shape.
// ============================================================

// Every other Newtonsoft-deserialized model talking to this exact backend
// (AviatesBackendClient.cs -- PilotAuthResult, RemotePilotStats, RemotePirepRecord, etc.) maps
// snake_case JSON keys via [JsonProperty], including the identical "pilot_id"/"departure_icao"/
// "arrival_icao" fields this class also needs. This class had no [JsonProperty] mappings at all,
// so every field silently deserialized to its C# default (PilotId="", Lat/Lon=0, ...) against a
// snake_case response -- other pilots were fetched successfully (no exception, no log entry) but
// rendered as blank markers at 0,0 (off the visible map) instead of not appearing to be broken at
// all. Fixed by adding the same mappings the rest of this backend's models already use.
public class LivePilotPosition
{
    [JsonProperty("pilot_id")]        public string PilotId       { get; set; } = "";
    [JsonProperty("pilot_name")]      public string PilotName     { get; set; } = "";
    [JsonProperty("lat")]             public double Lat           { get; set; }
    [JsonProperty("lon")]             public double Lon           { get; set; }
    [JsonProperty("alt")]             public int    Alt           { get; set; }
    [JsonProperty("speed")]           public int    Speed         { get; set; }
    [JsonProperty("heading")]         public int    Heading       { get; set; }
    [JsonProperty("phase")]           public string Phase         { get; set; } = "";
    [JsonProperty("departure_icao")]  public string? DepartureIcao { get; set; }
    [JsonProperty("arrival_icao")]    public string? ArrivalIcao   { get; set; }
    [JsonProperty("callsign")]        public string? Callsign      { get; set; }
    [JsonProperty("waypoints")]       public List<LiveWaypoint>? Waypoints { get; set; }
    [JsonProperty("updated_at")]      public DateTime UpdatedAt   { get; set; }
}

public class LiveWaypoint
{
    [JsonProperty("lat")] public double Lat { get; set; }
    [JsonProperty("lon")] public double Lon { get; set; }
    [JsonProperty("id")]  public string Id  { get; set; } = "";
}
