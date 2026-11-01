using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace AviatesAirTracker.Services;

/// <summary>One aircraft that flies a route. A route can be flown by several, each with its own
/// subsidiary and optionally its own flight number, frequency and block time.</summary>
public class RouteAircraft
{
    [JsonPropertyName("type")]                   public string  Type             { get; set; } = "";
    [JsonPropertyName("fleet_group")]            public string  FleetGroup       { get; set; } = "";
    [JsonPropertyName("flight_number")]          public string? FlightNumber     { get; set; }
    [JsonPropertyName("frequency")]              public string? Frequency        { get; set; }
    [JsonPropertyName("est_block_time_minutes")] public int?    BlockTimeMinutes { get; set; }

    /// <summary>"ATR 72" and "ATR72", "b738" and "B738" are the same aircraft.</summary>
    public bool Is(string? type) =>
        Normalized(Type).Length > 0 && Normalized(Type) == Normalized(type);

    private static string Normalized(string? s) =>
        new string((s ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
}

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
    // Every aircraft flying the route, first = the one in AircraftType/FleetGroup. Absent from
    // servers that predate multi-aircraft routes; use AircraftOptions, which falls back.
    [JsonPropertyName("aircraft")]               public List<RouteAircraft>? Aircraft { get; set; }

    /// <summary>Every aircraft flying the route (at least the single AircraftType one).</summary>
    [JsonIgnore] public IReadOnlyList<RouteAircraft> AircraftOptions =>
        Aircraft is { Count: > 0 } list
            ? list
            : [new RouteAircraft
              {
                  Type = AircraftType, FleetGroup = FleetGroup,
                  FlightNumber = string.IsNullOrEmpty(FlightNumber) ? null : FlightNumber,
                  Frequency = string.IsNullOrEmpty(Frequency) ? null : Frequency,
                  BlockTimeMinutes = BlockTimeMinutes > 0 ? BlockTimeMinutes : null,
              }];

    /// <summary>"B738, B739, ATR72"</summary>
    [JsonIgnore] public string AircraftLabel => string.Join(", ", AircraftOptions.Select(a => a.Type).Where(t => t.Length > 0));

    /// <summary>The route's aircraft entry for <paramref name="type"/>, or null if it isn't flown with it.</summary>
    public RouteAircraft? AircraftFor(string? type) => AircraftOptions.FirstOrDefault(a => a.Is(type));

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
    // Normalized (uppercase, single-spaced) so "ATR72", "atr 72", and "ATR  72"
    // all match the same entry — a raw string mismatch here used to silently
    // fall through to VAV for CityHopper/Cargo aircraft.
    private static readonly HashSet<string> CityHopperTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        Normalize("A319"), Normalize("B736"), Normalize("ATR72"), Normalize("ATR 42")
    };

    private static readonly HashSet<string> CargoTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        Normalize("B77F")
    };

    // Letters and digits only: routes store both "ATR 42" and "ATR42", and the space-only
    // normalisation this replaced sent "ATR42" to the Mainline prefix.
    private static string Normalize(string type) =>
        new string((type ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Resolves the ICAO designator (VAV/VAC/VAH) for a given aircraft type.</summary>
    public static string IcaoPrefix(string? aircraftType)
    {
        var type = Normalize(aircraftType ?? "");
        if (CargoTypes.Contains(type))      return "VAC";
        if (CityHopperTypes.Contains(type)) return "VAH";
        return "VAV";
    }

    /// <summary>Resolves the human-readable subsidiary name for a given aircraft type.</summary>
    public static string Name(string? aircraftType)
    {
        var type = Normalize(aircraftType ?? "");
        if (CargoTypes.Contains(type))      return "Skycargo";
        if (CityHopperTypes.Contains(type)) return "CityHopper";
        return "Mainline";
    }

    /// <summary>Strips a VAV/VAC/VAH ICAO prefix off a callsign, e.g. "VAH103" → "103".
    /// Returns the input unchanged if it doesn't start with one of the three prefixes.</summary>
    public static string StripIcaoPrefix(string callsign)
    {
        var cs = callsign ?? "";
        return cs.Length > 3 && StartsWithIcaoPrefix(cs) ? cs[3..] : cs;
    }

    private static bool StartsWithIcaoPrefix(string s) =>
        s.StartsWith("VAV", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("VAC", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("VAH", StringComparison.OrdinalIgnoreCase);
}
