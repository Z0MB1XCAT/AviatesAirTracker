using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace AviatesAirTracker.Services;

// ============================================================
// EVENTS SERVICE
// Handles fetching, creating, and registering for community
// events hosted on the Aviates Air backend.
// ============================================================

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

    [JsonIgnore] public bool      IsFeatured   => IsFeaturedRaw != 0;
    [JsonIgnore] public bool      IsRegistered => IsRegisteredRaw != 0;
    [JsonIgnore] public DateTime? ParsedDate   => DateTime.TryParse(EventDate, out var d) ? d : null;
    [JsonIgnore] public string    DisplayDay   => ParsedDate?.Day.ToString("D2") ?? "--";
    [JsonIgnore] public string    DisplayMonth => ParsedDate?.ToString("MMM") ?? "---";
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

public class EventsService
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
            var response = await _http.GetAsync($"/api/events/my?acarsKey={Uri.EscapeDataString(acarsKey)}");
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
            var body = new { acarsKey };
            var response = await _http.PostAsJsonAsync($"/api/events/{eventId}/register", body);
            var result = await response.Content.ReadFromJsonAsync<EventsApiResult>();
            return response.IsSuccessStatusCode
                ? (true, "")
                : (false, result?.Error ?? "Registration failed");
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
            var body = new { acarsKey };
            var response = await _http.PostAsJsonAsync($"/api/events/{eventId}/unregister", body);
            var result = await response.Content.ReadFromJsonAsync<EventsApiResult>();
            return response.IsSuccessStatusCode
                ? (true, "")
                : (false, result?.Error ?? "Failed to unregister");
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
            var body = new
            {
                acarsKey,
                title               = req.Title,
                description         = req.Description,
                eventDate           = req.EventDate,
                timeUtc             = req.TimeUtc,
                route               = req.Route,
                aircraftRestriction = req.AircraftRestriction,
                rankRestriction     = req.RankRestriction,
                maxParticipants     = req.MaxParticipants,
            };
            var response = await _http.PostAsJsonAsync("/api/events", body);
            var result = await response.Content.ReadFromJsonAsync<EventsApiResult>();
            return response.IsSuccessStatusCode
                ? (true, "")
                : (false, result?.Error ?? "Failed to create event");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[EventsService] Create event failed");
            return (false, "Network error — please try again");
        }
    }
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
