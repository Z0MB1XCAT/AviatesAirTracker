using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace AviatesAirTracker.Services;

public class AviatesRoute
{
    [JsonPropertyName("id")]                     public int    Id               { get; set; }
    [JsonPropertyName("callsign")]               public string Callsign         { get; set; } = "";
    [JsonPropertyName("flight_number")]          public string FlightNumber     { get; set; } = "";
    [JsonPropertyName("origin_iata")]            public string OriginIata       { get; set; } = "";
    [JsonPropertyName("dest_iata")]              public string DestIata         { get; set; } = "";
    [JsonPropertyName("origin_name")]            public string OriginName       { get; set; } = "";
    [JsonPropertyName("dest_name")]              public string DestName         { get; set; } = "";
    [JsonPropertyName("aircraft_type")]          public string AircraftType     { get; set; } = "";
    [JsonPropertyName("fleet_group")]            public string FleetGroup       { get; set; } = "";
    [JsonPropertyName("est_block_time_minutes")] public int    BlockTimeMinutes { get; set; }
    [JsonPropertyName("distance_km")]            public int    DistanceKm       { get; set; }
    [JsonPropertyName("frequency")]              public string Frequency        { get; set; } = "";
    [JsonPropertyName("notes")]                  public string Notes            { get; set; } = "";

    [JsonIgnore] public int    DistanceNm      => (int)(DistanceKm / 1.852);
    [JsonIgnore] public string BlockTimeDisplay => BlockTimeMinutes >= 60
        ? $"{BlockTimeMinutes / 60}h {BlockTimeMinutes % 60:D2}m"
        : $"{BlockTimeMinutes}m";
}

public class RoutesService
{
    private readonly HttpClient _http;
    private const string BaseUrl = "https://acars.flyaviatesair.uk";

    public RoutesService()
    {
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<(List<AviatesRoute> Routes, int Total)> GetRoutesAsync(
        string? fleet = null, string? q = null, string? origin = null, int limit = 200, int offset = 0)
    {
        try
        {
            var url = $"/api/routes?limit={limit}&offset={offset}";
            if (!string.IsNullOrEmpty(fleet))  url += $"&fleet={Uri.EscapeDataString(fleet)}";
            if (!string.IsNullOrEmpty(q))      url += $"&q={Uri.EscapeDataString(q)}";
            if (!string.IsNullOrEmpty(origin)) url += $"&origin={Uri.EscapeDataString(origin)}";

            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return ([], 0);

            var result = await response.Content.ReadFromJsonAsync<RoutesListResponse>();
            return (result?.Data ?? [], result?.Meta.Count ?? 0);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RoutesService] Failed to fetch routes (fleet={Fleet}, q={Q})", fleet, q);
            return ([], 0);
        }
    }
}

public class RoutesMeta
{
    [JsonPropertyName("count")]  public int Count  { get; set; }
    [JsonPropertyName("limit")]  public int Limit  { get; set; }
    [JsonPropertyName("offset")] public int Offset { get; set; }
}

public class RoutesListResponse
{
    [JsonPropertyName("meta")] public RoutesMeta         Meta { get; set; } = new();
    [JsonPropertyName("data")] public List<AviatesRoute> Data { get; set; } = [];
}

// ============================================================
// SUBSIDIARY / ICAO LOOKUP
// Single source of truth for which aircraft types fly for which
// subsidiary, and the ICAO designator each subsidiary uses for
// ATC callsigns and SimBrief dispatch. Route flight numbers
// (e.g. "AV150") are unaffected — they always use "AV".
// ============================================================

public static class Subsidiary
{
    private static readonly HashSet<string> CityHopperTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "A319", "ATR 72", "ATR 42"
    };

    private static readonly HashSet<string> CargoTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "A350F"
    };

    /// <summary>Resolves the ICAO designator (VAV/VAC/VAH) for a given aircraft type.</summary>
    public static string IcaoPrefix(string? aircraftType)
    {
        var type = (aircraftType ?? "").Trim();
        if (CargoTypes.Contains(type))      return "VAC";
        if (CityHopperTypes.Contains(type)) return "VAH";
        return "VAV";
    }

    /// <summary>Resolves the human-readable subsidiary name for a given aircraft type.</summary>
    public static string Name(string? aircraftType)
    {
        var type = (aircraftType ?? "").Trim();
        if (CargoTypes.Contains(type))      return "Skycargo";
        if (CityHopperTypes.Contains(type)) return "CityHopper";
        return "Mainline";
    }
}
