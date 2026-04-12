using AviatesAirTracker.Models;
using AviatesAirTracker.Services;
using Newtonsoft.Json;
using RestSharp;
using Serilog;

namespace AviatesAirTracker.Core.Backend;

// ============================================================
// AVIATES AIR BACKEND CLIENT
//
// *** BACKEND INTEGRATION LAYER ***
//
// This class is the single integration point between the
// flight tracker client and the Aviates Air backend API.
//
// Currently: all methods are stubbed / logged only.
//
// When the Aviates Air backend is ready:
//   1. Set the real API base URL in AppSettings.BackendApiUrl
//   2. Implement the TODO sections in each method below
//   3. Register AviatesBackendClient in App.xaml.cs DI
//   4. Replace InMemoryFlightRepository.SaveAsync() calls
//      with AviatesBackendClient.SubmitPirepAsync()
//
// Authentication: ACARS key from pilot settings
// Format: JSON REST API (standard Anthropic-style endpoints)
// ============================================================

public class AviatesBackendClient
{
    private const string ApiBaseUrl = "https://acars.flyaviatesair.uk";

    private readonly SettingsService _settings;
    private readonly RestClient _client;

    public AviatesBackendClient(SettingsService settings)
    {
        _settings = settings;
        _client   = new RestClient(ApiBaseUrl);
        Log.Information("[BackendClient] Initialized with base URL: {URL}", ApiBaseUrl);
    }

    // =====================================================
    // PILOT AUTHENTICATION
    // =====================================================

    /// <summary>
    /// Validates the pilot's ACARS key against the backend.
    /// Returns pilot profile on success, null on failure.
    /// </summary>
    public async Task<PilotAuthResult?> ValidateAcarsKeyAsync(string acarsKey)
    {
        var trimmedKey = acarsKey.Trim();
        Log.Information("[BackendClient] Validating ACARS key...");

        // POST /api/acars/auth  { acarsKey: "..." }
        // Worker requires User-Agent to identify the official ACARS client.
        var request = new RestRequest("/api/acars/auth", Method.Post)
            .AddHeader("User-Agent", "AviatesAir-ACARS-Client/1.0")
            .AddJsonBody(new { acarsKey = trimmedKey });

        var response = await _client.ExecuteAsync(request);

        // ResponseStatus != Completed means DNS failure, timeout, connection refused, etc.
        // Throw so the caller can distinguish "can't reach server" from "key invalid".
        if (response.ResponseStatus != RestSharp.ResponseStatus.Completed)
        {
            var inner = response.ErrorException;
            var msg   = inner?.Message ?? response.ResponseStatus.ToString();
            Log.Warning(inner, "[BackendClient] Network error during ACARS auth — status={Status} msg={Msg}",
                response.ResponseStatus, msg);
            throw new Exception($"{response.ResponseStatus}: {msg}", inner);
        }

        if (!response.IsSuccessful || string.IsNullOrEmpty(response.Content))
        {
            Log.Warning("[BackendClient] Auth failed: HTTP {Status}", (int)response.StatusCode);
            return null;
        }

        var raw = JsonConvert.DeserializeObject<AcarsAuthResponse>(response.Content);
        if (raw?.Success != true)
        {
            Log.Warning("[BackendClient] Auth rejected by server: {Content}", response.Content);
            return null;
        }

        var result = new PilotAuthResult
        {
            PilotId  = raw.PilotId,
            Name     = $"{raw.FirstName} {raw.LastName}".Trim(),
            SimBrief = raw.SimBrief,
            Email    = raw.Email,
            IsValid  = true,
        };
        Log.Information("[BackendClient] Auth OK: {Name}", result.Name);
        return result;
    }

    // =====================================================
    // PIREP SUBMISSION
    // =====================================================

    /// <summary>
    /// Submits a completed flight record (PIREP) to the Aviates Air backend.
    /// </summary>
    public async Task<bool> SubmitPirepAsync(FlightRecord flight, string acarsKey)
    {
        Log.Information("[BackendClient] Submitting PIREP: {Dep}→{Arr}",
            flight.DepartureICAO, flight.ArrivalICAO);

        try
        {
            // TODO: POST /api/v1/pireps  { flight data as JSON }
            // Headers: Authorization: Bearer {acars_key}

            var payload = new
            {
                flight_number   = flight.FlightNumber,
                callsign        = flight.Callsign,
                departure_icao  = flight.DepartureICAO,
                arrival_icao    = flight.ArrivalICAO,
                aircraft_type   = flight.AircraftType,
                block_out_time  = flight.BlockOutTime.ToString("o"),
                block_in_time   = flight.BlockInTime.ToString("o"),
                block_minutes   = (int)flight.BlockTime.TotalMinutes,
                air_minutes     = (int)flight.AirTime.TotalMinutes,
                distance_nm     = (int)flight.ActualDistanceNm,
                fuel_used_lbs   = (int)flight.FuelUsedLbs,
                max_altitude_ft = (int)flight.MaxAltitudeFt,
                landing_vs_fpm  = flight.PrimaryLanding?.VerticalSpeedFPM ?? 0,
                landing_score   = flight.PrimaryLanding?.LandingScore ?? 0,
                route           = flight.PlannedRoute,
                planned_ofp     = flight.PlannedFlight?.OFPNumber ?? "",
                acars_key       = acarsKey
            };

            var request = new RestRequest("/api/v1/pireps", Method.Post)
                .AddHeader("Authorization", $"Bearer {acarsKey}")
                .AddJsonBody(payload);

            var response = await _client.ExecuteAsync(request);

            if (response.IsSuccessful)
            {
                Log.Information("[BackendClient] PIREP submitted OK");
                return true;
            }

            Log.Warning("[BackendClient] PIREP submission failed: {Status} {Content}",
                response.StatusCode, response.Content);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[BackendClient] PIREP submission error");
            return false;
        }
    }

    // =====================================================
    // PILOT STATISTICS SYNC
    // =====================================================

    /// <summary>
    /// Fetches pilot statistics from the backend.
    /// </summary>
    public async Task<RemotePilotStats?> FetchStatsAsync(string pilotId, string acarsKey)
    {
        try
        {
            // TODO: GET /api/v1/pilots/{pilot_id}/stats
            var request = new RestRequest($"/api/v1/pilots/{pilotId}/stats")
                .AddHeader("Authorization", $"Bearer {acarsKey}");

            var response = await _client.ExecuteAsync(request);

            if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
                return JsonConvert.DeserializeObject<RemotePilotStats>(response.Content);

            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[BackendClient] Stats fetch error");
            return null;
        }
    }

    // =====================================================
    // FLIGHT PLAN VALIDATION
    // =====================================================

    /// <summary>
    /// Validates a route against the Aviates Air approved route network.
    /// </summary>
    public async Task<RouteValidationResult?> ValidateRouteAsync(
        string dep, string arr, string acarsKey)
    {
        try
        {
            // TODO: GET /api/v1/routes/validate?dep={dep}&arr={arr}
            var request = new RestRequest("/api/v1/routes/validate")
                .AddQueryParameter("dep", dep)
                .AddQueryParameter("arr", arr)
                .AddHeader("Authorization", $"Bearer {acarsKey}");

            var response = await _client.ExecuteAsync(request);

            if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
                return JsonConvert.DeserializeObject<RouteValidationResult>(response.Content);

            return new RouteValidationResult { IsApproved = false, Message = "Validation unavailable" };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[BackendClient] Route validation error");
            return new RouteValidationResult { IsApproved = true, Message = "Offline mode" };
        }
    }

    // =====================================================
    // LIVE FLIGHT POSITION UPDATE (ACARS position report)
    // =====================================================

    // =====================================================
    // MESSAGING
    // =====================================================

    /// <summary>
    /// Sends a direct or broadcast message. The ACARS key is the auth token the backend
    /// records per-message for moderation — it is never returned to other clients.
    /// </summary>
    public async Task<bool> SendMessageAsync(PilotMessage message, string acarsKey)
    {

        try
        {
            // TODO: POST /api/v1/messages
            // Headers: Authorization: Bearer {acars_key}
            // Body: { recipient_id, content, type, sent_at }
            var request = new RestRequest("/api/v1/messages", Method.Post)
                .AddHeader("Authorization", $"Bearer {acarsKey}")
                .AddJsonBody(new
                {
                    message_id   = message.Id.ToString(),
                    sender_id    = message.SenderId,
                    sender_name  = message.SenderName,
                    recipient_id = message.RecipientId,
                    content      = message.Content,
                    type         = message.Type.ToString().ToLowerInvariant(),
                    sent_at      = message.SentAt.ToString("o")
                });

            var response = await _client.ExecuteAsync(request);
            if (response.IsSuccessful) return true;

            Log.Warning("[BackendClient] SendMessage failed: {Status}", response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[BackendClient] SendMessage error");
            return false;
        }
    }

    /// <summary>
    /// Fetches new direct messages for the given pilot since last sync.
    /// </summary>
    public async Task<List<PilotMessage>> FetchInboxAsync(string pilotId, string acarsKey)
    {

        try
        {
            // TODO: GET /api/v1/messages/inbox
            var request = new RestRequest("/api/v1/messages/inbox")
                .AddHeader("Authorization", $"Bearer {acarsKey}");

            var response = await _client.ExecuteAsync(request);
            if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
                return JsonConvert.DeserializeObject<List<PilotMessage>>(response.Content) ?? [];

            return [];
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[BackendClient] FetchInbox error (non-critical)");
            return [];
        }
    }

    /// <summary>
    /// Fetches the latest broadcast messages.
    /// </summary>
    public async Task<List<PilotMessage>> FetchBroadcastsAsync(string acarsKey)
    {

        try
        {
            var request = new RestRequest("/api/v1/messages/broadcast")
                .AddHeader("Authorization", $"Bearer {acarsKey}");

            var response = await _client.ExecuteAsync(request);
            if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
                return JsonConvert.DeserializeObject<List<PilotMessage>>(response.Content) ?? [];

            return [];
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[BackendClient] FetchBroadcasts error (non-critical)");
            return [];
        }
    }

    // =====================================================
    // FRIENDS
    // =====================================================

    /// <summary>
    /// Resolves a friend code to a pilot profile. Returns null if the code is invalid.
    /// The server maps the friend code back to the pilot's ACARS key without exposing it.
    /// </summary>
    public async Task<FriendEntry?> ResolveFriendCodeAsync(string friendCode, string acarsKey)
    {
        try
        {
            var request = new RestRequest("/api/friends/resolve")
                .AddQueryParameter("code", friendCode)
                .AddHeader("Authorization", $"Bearer {acarsKey}");

            var response = await _client.ExecuteAsync(request);
            if (response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
                return JsonConvert.DeserializeObject<FriendEntry>(response.Content);

            Log.Warning("[BackendClient] ResolveFriendCode failed: {Status}", response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[BackendClient] ResolveFriendCode error");
            return null;
        }
    }

    /// <summary>
    /// Notifies the backend that a friend relationship has been removed.
    /// </summary>
    public async Task RemoveFriendAsync(string pilotId, string acarsKey)
    {

        try
        {
            // TODO: DELETE /api/v1/friends/{pilotId}
            var request = new RestRequest($"/api/v1/friends/{pilotId}", Method.Delete)
                .AddHeader("Authorization", $"Bearer {acarsKey}");

            await _client.ExecuteAsync(request);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[BackendClient] RemoveFriend error (non-critical)");
        }
    }

    private DateTime _lastPositionReport = DateTime.MinValue;

    /// <summary>
    /// Sends a live ACARS position report to the backend (every 5 minutes).
    /// </summary>
    public async Task SendPositionReportAsync(
        double lat, double lon, double altFt, int speedKts,
        string phase, string acarsKey)
    {
        if ((DateTime.UtcNow - _lastPositionReport).TotalMinutes < 5) return;

        _lastPositionReport = DateTime.UtcNow;

        try
        {
            // TODO: POST /api/v1/acars/position
            var request = new RestRequest("/api/v1/acars/position", Method.Post)
                .AddHeader("Authorization", $"Bearer {acarsKey}")
                .AddJsonBody(new
                {
                    latitude  = lat,
                    longitude = lon,
                    altitude  = (int)altFt,
                    speed     = speedKts,
                    phase     = phase,
                    timestamp = DateTime.UtcNow.ToString("o")
                });

            await _client.ExecuteAsync(request);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[BackendClient] Position report failed (non-critical)");
        }
    }
}

// =====================================================
// API RESPONSE MODELS
// =====================================================

// Raw shape returned by POST /api/acars/auth
internal class AcarsAuthResponse
{
    [JsonProperty("success")]   public bool   Success   { get; set; }
    [JsonProperty("email")]     public string Email     { get; set; } = "";
    [JsonProperty("firstName")] public string FirstName { get; set; } = "";
    [JsonProperty("lastName")]  public string LastName  { get; set; } = "";
    [JsonProperty("simbrief")]  public string SimBrief  { get; set; } = "";
    // Backend may return the pilot's stable ID under either casing
    [JsonProperty("pilotId")]   public string PilotId   { get; set; } = "";
}

public class PilotAuthResult
{
    [JsonProperty("pilot_id")]    public string PilotId   { get; set; } = "";
    [JsonProperty("name")]        public string Name      { get; set; } = "";
    [JsonProperty("email")]       public string Email     { get; set; } = "";
    [JsonProperty("simbrief")]    public string SimBrief  { get; set; } = "";
    [JsonProperty("rank")]        public string Rank      { get; set; } = "";
    [JsonProperty("total_hours")] public double TotalHours { get; set; }
    [JsonProperty("valid")]       public bool   IsValid   { get; set; }
}

public class RemotePilotStats
{
    [JsonProperty("total_flights")] public int    TotalFlights  { get; set; }
    [JsonProperty("total_hours")]   public double TotalHours    { get; set; }
    [JsonProperty("total_nm")]      public double TotalNm       { get; set; }
    [JsonProperty("avg_score")]     public double AvgScore      { get; set; }
    [JsonProperty("rank")]          public string Rank          { get; set; } = "";
    [JsonProperty("rank_pct")]      public double RankProgress  { get; set; }
}

public class RouteValidationResult
{
    [JsonProperty("approved")]    public bool   IsApproved   { get; set; }
    [JsonProperty("message")]     public string Message      { get; set; } = "";
    [JsonProperty("route_id")]    public string? RouteId     { get; set; }
    [JsonProperty("aircraft_ok")] public bool   AircraftOk  { get; set; }
}
