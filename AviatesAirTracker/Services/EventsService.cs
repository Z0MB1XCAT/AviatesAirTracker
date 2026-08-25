using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace AviatesAirTracker.Services;

public class AviatesEvent
{
    [JsonPropertyName("id")]                   public int    Id                  { get; set; }
    [JsonPropertyName("title")]                public string Title               { get; set; } = "";
    [JsonPropertyName("description")]          public string Description         { get; set; } = "";
    [JsonPropertyName("event_date")]           public string EventDate           { get; set; } = "";
    [JsonPropertyName("time_utc")]             public string TimeUtc             { get; set; } = "";
    [JsonPropertyName("route")]                public string Route               { get; set; } = "";
    [JsonPropertyName("aircraft_restriction")] public string AircraftRestriction { get; set; } = "";
    [JsonPropertyName("rank_restriction")]     public string RankRestriction     { get; set; } = "";
    [JsonPropertyName("created_by")]           public string CreatedBy           { get; set; } = "";
    [JsonPropertyName("created_by_name")]      public string CreatedByName       { get; set; } = "";
    [JsonPropertyName("created_at")]           public string CreatedAt           { get; set; } = "";
    [JsonPropertyName("is_featured")]          public int    IsFeaturedRaw       { get; set; }
    [JsonPropertyName("max_participants")]     public int    MaxParticipants     { get; set; }
    [JsonPropertyName("status")]               public string Status              { get; set; } = "upcoming";
    [JsonPropertyName("registration_count")]   public int    RegistrationCount   { get; set; }
    [JsonPropertyName("is_registered")]        public int    IsRegisteredRaw     { get; set; }

    [JsonIgnore] public bool IsFeatured   => IsFeaturedRaw != 0;
    [JsonIgnore] public bool IsRegistered => IsRegisteredRaw != 0;

    // Parsed once and cached — EventDate is a date-only string (YYYY-MM-DD), always UTC.
    private DateTime? _parsedDate;
    [JsonIgnore]
    public DateTime? ParsedDate
    {
        get
        {
            if (_parsedDate.HasValue) return _parsedDate;
            if (DateTime.TryParseExact(EventDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
                _parsedDate = d;
            return _parsedDate;
        }
    }

    [JsonIgnore] public string DisplayDay   => ParsedDate?.Day.ToString("D2") ?? "--";
    [JsonIgnore] public string DisplayMonth => ParsedDate?.ToString("MMM", CultureInfo.InvariantCulture) ?? "---";
}

public class CreateEventRequest
{
    public string Title               { get; set; } = "";
    public string Description         { get; set; } = "";
    public string EventDate           { get; set; } = "";
    public string TimeUtc             { get; set; } = "";
    public string Route               { get; set; } = "";
    public string AircraftRestriction { get; set; } = "";
    public string RankRestriction     { get; set; } = "";
    public int    MaxParticipants     { get; set; }
}

public class EventsService : IDisposable
{
    private readonly HttpClient _http;
    private const string BaseUrl = "https://acars.flyaviatesair.uk";

    public EventsService()
    {
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<List<AviatesEvent>> GetEventsAsync(string filter = "upcoming")
    {
        try
        {
            var response = await _http.GetAsync($"/api/events?filter={Uri.EscapeDataString(filter)}");
            if (!response.IsSuccessStatusCode) return [];
            var result = await response.Content.ReadFromJsonAsync<EventsListResponse>();
            return result?.Data ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[EventsService] Failed to fetch events (filter={Filter})", filter);
            return [];
        }
    }

    public async Task<List<AviatesEvent>> GetMyEventsAsync(string acarsKey)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/events/my");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", acarsKey);
            var response = await _http.SendAsync(req);
            if (!response.IsSuccessStatusCode) return [];
            var result = await response.Content.ReadFromJsonAsync<EventsListResponse>();
            return result?.Data ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[EventsService] Failed to fetch my events");
            return [];
        }
    }

    public async Task<(bool Success, string Error)> RegisterForEventAsync(int eventId, string acarsKey)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/events/{eventId}/register");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", acarsKey);
            req.Content = JsonContent.Create(new { });
            var response = await _http.SendAsync(req);
            var result = await response.Content.ReadFromJsonAsync<EventsApiResult>();
            return response.IsSuccessStatusCode
                ? (true, "")
                : (false, ResolveErrorMessage(result, "Registration failed"));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[EventsService] Register for event {Id} failed", eventId);
            return (false, "Network error — please try again");
        }
    }

    public async Task<(bool Success, string Error)> UnregisterFromEventAsync(int eventId, string acarsKey)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/events/{eventId}/unregister");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", acarsKey);
            req.Content = JsonContent.Create(new { });
            var response = await _http.SendAsync(req);
            var result = await response.Content.ReadFromJsonAsync<EventsApiResult>();
            return response.IsSuccessStatusCode
                ? (true, "")
                : (false, ResolveErrorMessage(result, "Failed to unregister"));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[EventsService] Unregister from event {Id} failed", eventId);
            return (false, "Network error — please try again");
        }
    }

    public async Task<(bool Success, string Error)> CreateEventAsync(CreateEventRequest req, string acarsKey)
    {
        try
        {
            // Field names must match the backend's actual contract, which is snake_case
            // throughout (every response DTO in this file maps snake_case JSON keys via
            // [JsonPropertyName] -- event_date, time_utc, aircraft_restriction, etc.). This was
            // previously camelCase, so most fields (including event_date) landed as null/undefined
            // server-side: the row got created but never matched the date-based "upcoming"/"past"
            // filters, making every created event invisible.
            var body = new
            {
                title                = req.Title,
                description          = req.Description,
                event_date           = req.EventDate,
                time_utc             = req.TimeUtc,
                route                = req.Route,
                aircraft_restriction = req.AircraftRestriction,
                rank_restriction     = req.RankRestriction,
                max_participants     = req.MaxParticipants,
            };
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, "/api/events");
            httpReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", acarsKey);
            httpReq.Content = JsonContent.Create(body);
            var response = await _http.SendAsync(httpReq);
            var result = await response.Content.ReadFromJsonAsync<EventsApiResult>();
            return response.IsSuccessStatusCode
                ? (true, "")
                : (false, ResolveErrorMessage(result, "Failed to create event"));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[EventsService] Create event failed");
            return (false, "Network error — please try again");
        }
    }

    // result?.Error ?? fallback only catches a literal null -- a response like
    // {"success":false,"error":""} (empty but non-null) or one that puts the real reason in
    // "message" instead of "error" would silently produce a blank error bar in the UI instead of
    // any visible text. Checks both fields and treats whitespace-only as absent too.
    private static string ResolveErrorMessage(EventsApiResult? result, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(result?.Error)) return result.Error;
        if (!string.IsNullOrWhiteSpace(result?.Message)) return result.Message!;
        return fallback;
    }

    public void Dispose() => _http.Dispose();
}

public class EventsListResponse
{
    [JsonPropertyName("data")] public List<AviatesEvent> Data { get; set; } = [];
}

public class EventsApiResult
{
    [JsonPropertyName("success")] public bool    Success { get; set; }
    [JsonPropertyName("error")]   public string? Error   { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("id")]      public int?    Id      { get; set; }
}
