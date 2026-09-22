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
            Subsidiary = raw.Subsidiary,
        };
        Log.Information("[BackendClient] Auth OK: {Name}", result.Name);
        return result;
    }

    // =====================================================
    // SUBSIDIARY
    // =====================================================

    /// <summary>
    /// Live read of GET /api/portal/subsidiary -- used instead of the locally-cached
    /// SettingsService.Settings.Subsidiary (only ever refreshed by SyncProfileAsync's manual
    /// "Verify &amp; Sync") right before a booking is confirmed, so a pilot who chose/changed
    /// subsidiary on the website without ever re-syncing in the app still gets an accurate
    /// answer. Success=false means the call itself failed (network/parse) -- distinct from a
    /// successful call that returned null (no subsidiary chosen yet) -- so the caller can fail
    /// open on connectivity issues while still enforcing an explicit "not chosen" result.
    /// </summary>
    public async Task<(string? Subsidiary, bool Success)> GetSubsidiaryAsync(string acarsKey)
    {
        try
        {
            var request = new RestRequest("/api/portal/subsidiary", Method.Get)
                .AddHeader("Authorization", $"Bearer {acarsKey}");
            var response = await _client.ExecuteAsync(request);
            if (!response.IsSuccessful || string.IsNullOrEmpty(response.Content))
                return (null, false);

            var raw = JsonConvert.DeserializeObject<SubsidiaryInfoResponse>(response.Content);
            return (raw?.Subsidiary, true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[BackendClient] Failed to fetch subsidiary info");
            return (null, false);
        }
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
                registration    = flight.AircraftRegistration,
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
                // Full per-touchdown detail (every landing, not just the primary one -- a
                // flight can bounce/go-around) so Pilot Hub's landing history and score
                // breakdown survive an account switch, not just the coarse VS/score above.
                landings        = flight.AllLandings.Select(l => new
                {
                    touchdown_at           = l.Timestamp.ToString("o"),
                    airport_icao           = l.AirportICAO,
                    runway_identifier      = l.RunwayIdentifier,
                    touchdown_lat          = l.TouchdownLatitude,
                    touchdown_lon          = l.TouchdownLongitude,
                    vs_fpm                 = l.VerticalSpeedFPM,
                    ias_kts                = l.IASKts,
                    gs_kts                 = l.GroundSpeedKts,
                    pitch_deg              = l.TouchdownPitchDeg,
                    bank_deg               = l.TouchdownBankDeg,
                    headwind_kt            = l.HeadwindComponent,
                    crosswind_kt           = l.CrosswindComponent,
                    wind_speed_kt          = l.WindSpeedKts,
                    wind_dir_deg           = l.WindDirectionDeg,
                    approach_stable        = l.ApproachWasStable,
                    flare_detected         = l.FlareDetected,
                    bounce_count           = l.BounceCount,
                    rollout_distance_ft    = l.RolloutDistanceFt,
                    avg_deceleration       = l.AverageDecelerationKtPerSec,
                    threshold_distance_ft  = l.ThresholdDistanceFt,
                    fuel_on_touchdown_lbs  = l.FuelOnTouchdownLbs,
                    score                  = l.LandingScore,
                    score_vs               = l.ScoreBreakdown?.VerticalSpeedScore ?? 0,
                    score_pitch            = l.ScoreBreakdown?.PitchScore ?? 0,
                    score_bank             = l.ScoreBreakdown?.BankScore ?? 0,
                    score_speed            = l.ScoreBreakdown?.SpeedScore ?? 0,
                    score_crosswind        = l.ScoreBreakdown?.CrosswindScore ?? 0,
                    score_stability        = l.ScoreBreakdown?.StabilityScore ?? 0,
                }).ToList(),
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
    // PIREP HISTORY
    // =====================================================

    internal async Task<List<RemotePirepRecord>> FetchMyPirepsAsync(string acarsKey)
    {
        try
        {
            var request = new RestRequest("/api/v1/pireps")
                .AddQueryParameter("limit", "100")
                .AddHeader("Authorization", $"Bearer {acarsKey}");

            var response = await _client.ExecuteAsync(request);

            if (!response.IsSuccessful || string.IsNullOrEmpty(response.Content))
            {
                Log.Debug("[BackendClient] FetchMyPireps non-success: {Status}", response.StatusCode);
                return [];
            }

            var wrapper = JsonConvert.DeserializeObject<PirepListResponse>(response.Content);
            return wrapper?.Pireps ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[BackendClient] FetchMyPireps error");
            return [];
        }
    }

    /// <summary>
    /// Fetches the caller's full per-touchdown landing history (every landing, not just each
    /// flight's primary one), for reconstructing Pilot Hub's landing detail on a new device.
    /// </summary>
    internal async Task<List<RemoteLandingRecord>> FetchMyLandingsAsync(string acarsKey)
    {
        try
        {
            var request = new RestRequest("/api/v1/landings")
                .AddQueryParameter("limit", "200")
                .AddHeader("Authorization", $"Bearer {acarsKey}");

            var response = await _client.ExecuteAsync(request);

            if (!response.IsSuccessful || string.IsNullOrEmpty(response.Content))
            {
                Log.Debug("[BackendClient] FetchMyLandings non-success: {Status}", response.StatusCode);
                return [];
            }

            var wrapper = JsonConvert.DeserializeObject<LandingListResponse>(response.Content);
            return wrapper?.Landings ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[BackendClient] FetchMyLandings error");
            return [];
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

    /// <summary>
    /// Persists a newly-added friend server-side (upsert, so re-adding is harmless), so the
    /// friend list survives an account switch instead of living only in the local JSON cache.
    /// </summary>
    public async Task AddFriendAsync(FriendEntry entry, string acarsKey)
    {
        try
        {
            var request = new RestRequest("/api/v1/friends", Method.Post)
                .AddHeader("Authorization", $"Bearer {acarsKey}")
                .AddJsonBody(new
                {
                    pilot_id    = entry.PilotId,
                    friend_name = entry.PilotName,
                    friend_code = entry.FriendCode,
                });

            var response = await _client.ExecuteAsync(request);
            if (!response.IsSuccessful)
                Log.Warning("[BackendClient] AddFriend failed: {Status}", response.StatusCode);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[BackendClient] AddFriend error (non-critical)");
        }
    }

    /// <summary>
    /// Logs a flight the app's own compliance checks flagged (hard landing, unstabilized
    /// approach, off-centerline/touchdown-zone, excessive VS, dangerous cruise speed, or a late
    /// departure) for manual review, along with the pilot's own explanation. Returns true only
    /// on a confirmed 2xx from the backend -- the caller uses this to decide whether to show the
    /// dialog's "Submitted" state or let the pilot retry.
    /// </summary>
    public async Task<bool> SubmitFlightReviewAsync(
        string acarsKey, string? flightId, string? landingId, List<string> violations, string? pilotReason)
    {
        try
        {
            var request = new RestRequest("/api/v1/flight-review", Method.Post)
                .AddHeader("Authorization", $"Bearer {acarsKey}")
                .AddJsonBody(new
                {
                    flight_id    = flightId,
                    landing_id   = landingId,
                    violations,
                    pilot_reason = pilotReason,
                });

            var response = await _client.ExecuteAsync(request);
            if (!response.IsSuccessful)
                Log.Warning("[BackendClient] SubmitFlightReview failed: {Status}", response.StatusCode);
            return response.IsSuccessful;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[BackendClient] SubmitFlightReview error");
            return false;
        }
    }

    /// <summary>
    /// Checks whether the signed-in pilot's account email matches an entry in admin_users --
    /// used to decide whether Flights' Pending Review (deletion-request) tab should even be
    /// shown. Fails closed: any error, timeout, or non-success response returns false, since
    /// showing an admin-only panel to a non-admin is the wrong failure direction here.
    /// </summary>
    public async Task<bool> CheckIsAdminAsync(string acarsKey)
    {
        try
        {
            var request = new RestRequest("/api/v1/is-admin")
                .AddHeader("Authorization", $"Bearer {acarsKey}");

            var response = await _client.ExecuteAsync(request);
            if (!response.IsSuccessful || string.IsNullOrEmpty(response.Content))
                return false;

            var result = JsonConvert.DeserializeObject<IsAdminResponse>(response.Content);
            return result?.IsAdmin ?? false;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[BackendClient] CheckIsAdmin error (non-critical, fails closed)");
            return false;
        }
    }

    /// <summary>
    /// Fetches the News page's articles from the same news_articles table the AviatesAir
    /// website itself reads (public endpoint, no ACARS key needed -- matches News.razor's
    /// original "no service dependency" simplicity, just backed by real data instead of 6
    /// hardcoded cards now).
    /// </summary>
    public async Task<List<RemoteNewsArticle>> FetchNewsAsync()
    {
        try
        {
            var request = new RestRequest("/api/news");
            var response = await _client.ExecuteAsync(request);
            if (!response.IsSuccessful || string.IsNullOrEmpty(response.Content))
                return [];

            var wrapper = JsonConvert.DeserializeObject<NewsApiResponse>(response.Content);
            return wrapper?.Success == true ? wrapper.Data : [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[BackendClient] FetchNews error");
            return [];
        }
    }

    /// <summary>
    /// Fetches the caller's friends' most recent flights (newest first), for the Dashboard's
    /// activity feed. The server resolves each friend's ACARS key internally to look up their
    /// PIREPs -- it is never returned to this client.
    /// </summary>
    public async Task<List<RemoteFriendActivity>> FetchFriendsActivityAsync(string acarsKey, int limit = 3)
    {
        try
        {
            var request = new RestRequest("/api/v1/friends/activity")
                .AddQueryParameter("limit", limit.ToString())
                .AddHeader("Authorization", $"Bearer {acarsKey}");

            var response = await _client.ExecuteAsync(request);
            if (!response.IsSuccessful || string.IsNullOrEmpty(response.Content))
                return [];

            var wrapper = JsonConvert.DeserializeObject<FriendActivityResponse>(response.Content);
            return wrapper?.Activity ?? [];
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[BackendClient] FetchFriendsActivity error (non-critical)");
            return [];
        }
    }

    /// <summary>
    /// Fetches the caller's saved friend list, for reconstructing it on a new device.
    /// </summary>
    public async Task<List<FriendEntry>> FetchMyFriendsAsync(string acarsKey)
    {
        try
        {
            var request = new RestRequest("/api/v1/friends")
                .AddHeader("Authorization", $"Bearer {acarsKey}");

            var response = await _client.ExecuteAsync(request);
            if (!response.IsSuccessful || string.IsNullOrEmpty(response.Content))
                return [];

            var wrapper = JsonConvert.DeserializeObject<FriendListResponse>(response.Content);
            return (wrapper?.Friends ?? []).Select(r => new FriendEntry
            {
                PilotId    = r.FriendPilotId,
                PilotName  = r.FriendName,
                FriendCode = r.FriendCode,
                AddedAt    = r.CreatedAt,
            }).ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[BackendClient] FetchMyFriends error");
            return [];
        }
    }

    private DateTime _lastPositionReport = DateTime.MinValue;

    /// <summary>
    /// Sends a live ACARS position report to the backend (every 30 seconds, for the live map).
    /// </summary>
    public async Task SendPositionReportAsync(
        double lat, double lon, double altFt, int speedKts, int headingDeg,
        string phase, string acarsKey)
    {
        if ((DateTime.UtcNow - _lastPositionReport).TotalSeconds < 30) return;

        _lastPositionReport = DateTime.UtcNow;

        try
        {
            var request = new RestRequest("/api/v1/acars/position", Method.Post)
                .AddHeader("Authorization", $"Bearer {acarsKey}")
                .AddJsonBody(new
                {
                    latitude  = lat,
                    longitude = lon,
                    altitude  = (int)altFt,
                    speed     = speedKts,
                    heading   = headingDeg,
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

    /// <summary>
    /// Registers the start of a flight (or an updated flight plan) with the live map.
    /// Safe to call multiple times for the same flight — the backend upserts.
    /// </summary>
    public async Task SendFlightStartAsync(
        string? departureIcao, string? arrivalIcao, string? callsign,
        List<Waypoint>? waypoints, string acarsKey)
    {
        try
        {
            var request = new RestRequest("/api/v1/acars/flight-start", Method.Post)
                .AddHeader("Authorization", $"Bearer {acarsKey}")
                .AddJsonBody(new
                {
                    departure_icao = departureIcao ?? "",
                    arrival_icao   = arrivalIcao   ?? "",
                    callsign       = callsign      ?? "",
                    waypoints      = waypoints?.Select(w => new { lat = w.Latitude, lon = w.Longitude, id = w.Identifier }).ToList()
                });

            await _client.ExecuteAsync(request);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[BackendClient] flight-start report failed (non-critical)");
        }
    }

    /// <summary>Removes the pilot from the live map (call on landing/session end).</summary>
    public async Task SendFlightEndAsync(string acarsKey)
    {
        try
        {
            var request = new RestRequest("/api/v1/acars/flight-end", Method.Post)
                .AddHeader("Authorization", $"Bearer {acarsKey}");

            await _client.ExecuteAsync(request);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[BackendClient] flight-end report failed (non-critical)");
        }
    }

    /// <summary>Toggles whether this pilot's position is shared on the live map. Enforced server-side.</summary>
    public async Task<bool> SetLiveSharingAsync(bool enabled, string acarsKey)
    {
        try
        {
            var request = new RestRequest("/api/v1/acars/live-sharing", Method.Patch)
                .AddHeader("Authorization", $"Bearer {acarsKey}")
                .AddJsonBody(new { enabled });

            var response = await _client.ExecuteAsync(request);
            return response.IsSuccessful;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[BackendClient] live-sharing toggle failed (non-critical)");
            return false;
        }
    }

    /// <summary>Fetches all currently-flying pilots for the live map. Public endpoint, no auth.</summary>
    public async Task<List<LivePilotPosition>> FetchLivePositionsAsync()
    {
        try
        {
            var request = new RestRequest("/api/acars/live-positions", Method.Get);
            var response = await _client.ExecuteAsync(request);

            if (!response.IsSuccessful || string.IsNullOrEmpty(response.Content))
                return new List<LivePilotPosition>();

            var wrapper = JsonConvert.DeserializeObject<LivePositionsResponse>(response.Content);
            return wrapper?.Positions ?? new List<LivePilotPosition>();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[BackendClient] FetchLivePositions failed (non-critical)");
            return new List<LivePilotPosition>();
        }
    }

    // =====================================================
    // SUPPORT LIVE CHAT
    // Public endpoints, no ACARS key required (a chat can start before a pilot signs in) — the
    // same /api/support/chat* endpoints the website's /support live chat widget calls. Auth is
    // just the visitorToken returned once at chat creation, persisted in
    // SettingsService.Settings.SupportChatVisitorToken so the conversation survives an app restart.
    // =====================================================

    /// <summary>Starts a new support chat with its first message. Returns null (with the reason
    /// logged) on any failure — the caller shows a generic retry message either way.</summary>
    public async Task<SupportChatStartResult> StartSupportChatAsync(string name, string message)
    {
        var request = new RestRequest("/api/support/chat", Method.Post)
            .AddJsonBody(new { name, message });
        var response = await _client.ExecuteAsync(request);

        if (string.IsNullOrEmpty(response.Content))
            return new SupportChatStartResult { Success = false, Error = "Could not reach the support server." };

        var raw = JsonConvert.DeserializeObject<SupportChatStartResponse>(response.Content);
        if (raw?.Success != true || raw.Data is null)
            return new SupportChatStartResult { Success = false, Error = raw?.Error ?? "Could not start chat. Please try again." };

        return new SupportChatStartResult { Success = true, ChatId = raw.Data.ChatId, VisitorToken = raw.Data.VisitorToken };
    }

    /// <summary>Sends a follow-up message on an existing chat.</summary>
    public async Task<SupportChatSendResult> SendSupportChatMessageAsync(int chatId, string visitorToken, string content)
    {
        var request = new RestRequest($"/api/support/chat/{chatId}/messages", Method.Post)
            .AddJsonBody(new { visitorToken, content });
        var response = await _client.ExecuteAsync(request);

        if (string.IsNullOrEmpty(response.Content))
            return new SupportChatSendResult { Success = false, Error = "Could not reach the support server." };

        var raw = JsonConvert.DeserializeObject<SupportChatGenericResponse>(response.Content);
        return raw?.Success == true
            ? new SupportChatSendResult { Success = true }
            : new SupportChatSendResult { Success = false, Error = raw?.Error ?? "Failed to send message." };
    }

    /// <summary>Polls for messages after <paramref name="afterId"/> (or the full history if null).
    /// Returns null on a network/parse failure — the caller just skips that poll tick.</summary>
    public async Task<SupportChatPollData?> PollSupportChatMessagesAsync(int chatId, string visitorToken, int? afterId)
    {
        try
        {
            var request = new RestRequest($"/api/support/chat/{chatId}/messages", Method.Get)
                .AddQueryParameter("visitorToken", visitorToken);
            if (afterId.HasValue) request.AddQueryParameter("after", afterId.Value.ToString());

            var response = await _client.ExecuteAsync(request);
            if (!response.IsSuccessful || string.IsNullOrEmpty(response.Content)) return null;

            var raw = JsonConvert.DeserializeObject<SupportChatPollResponse>(response.Content);
            return raw?.Success == true ? raw.Data : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[BackendClient] Support chat poll failed (non-critical)");
            return null;
        }
    }
}

internal class LivePositionsResponse
{
    public List<LivePilotPosition> Positions { get; set; } = new();
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
    [JsonProperty("subsidiary")] public string? Subsidiary { get; set; }
}

// Raw shape returned by GET /api/portal/subsidiary
internal class SubsidiaryInfoResponse
{
    [JsonProperty("subsidiary")] public string? Subsidiary { get; set; }
    [JsonProperty("moves_used_this_month")] public int MovesUsedThisMonth { get; set; }
    [JsonProperty("moves_remaining_this_month")] public int MovesRemainingThisMonth { get; set; }
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
    [JsonProperty("subsidiary")]  public string? Subsidiary { get; set; }
}

public class RouteValidationResult
{
    [JsonProperty("approved")]    public bool   IsApproved   { get; set; }
    [JsonProperty("message")]     public string Message      { get; set; } = "";
    [JsonProperty("route_id")]    public string? RouteId     { get; set; }
    [JsonProperty("aircraft_ok")] public bool   AircraftOk  { get; set; }
}

internal class RemotePirepRecord
{
    [JsonProperty("id")]             public int       Id            { get; set; }
    [JsonProperty("flight_number")]  public string    FlightNumber  { get; set; } = "";
    [JsonProperty("callsign")]       public string    Callsign      { get; set; } = "";
    [JsonProperty("departure_icao")] public string    DepartureICAO { get; set; } = "";
    [JsonProperty("arrival_icao")]   public string    ArrivalICAO   { get; set; } = "";
    [JsonProperty("aircraft_type")]  public string    AircraftType  { get; set; } = "";
    [JsonProperty("block_out_time")] public DateTime? BlockOutTime  { get; set; }
    [JsonProperty("block_in_time")]  public DateTime? BlockInTime   { get; set; }
    [JsonProperty("block_minutes")]  public int       BlockMinutes  { get; set; }
    [JsonProperty("air_minutes")]    public int       AirMinutes    { get; set; }
    [JsonProperty("distance_nm")]    public int       DistanceNm    { get; set; }
    [JsonProperty("fuel_used_lbs")]  public int       FuelUsedLbs   { get; set; }
    [JsonProperty("landing_vs_fpm")] public int       LandingVsFpm  { get; set; }
    [JsonProperty("landing_score")]  public double    LandingScore  { get; set; }
    [JsonProperty("submitted_at")]   public DateTime  SubmittedAt   { get; set; }
    [JsonProperty("status")]         public string    Status        { get; set; } = "";
}

internal class PirepListResponse
{
    [JsonProperty("pireps")] public List<RemotePirepRecord> Pireps { get; set; } = [];
}

public class RemoteNewsArticle
{
    [JsonProperty("id")]           public string  Id           { get; set; } = "";
    [JsonProperty("title")]        public string  Title        { get; set; } = "";
    [JsonProperty("category")]     public string? Category     { get; set; }
    [JsonProperty("date")]         public string  Date         { get; set; } = "";
    [JsonProperty("summary")]      public string  Summary      { get; set; } = "";
    [JsonProperty("content")]      public string  Content      { get; set; } = "";
    [JsonProperty("thumbnail")]    public string? Thumbnail    { get; set; }
    [JsonProperty("thumbnailAlt")] public string? ThumbnailAlt { get; set; }
    [JsonProperty("heroImage")]    public string? HeroImage    { get; set; }
}

internal class NewsApiResponse
{
    [JsonProperty("success")] public bool Success { get; set; }
    [JsonProperty("data")]    public List<RemoteNewsArticle> Data { get; set; } = [];
}

internal class IsAdminResponse
{
    [JsonProperty("is_admin")] public bool IsAdmin { get; set; }
}

internal class RemoteLandingRecord
{
    [JsonProperty("id")]                     public long      Id                   { get; set; }
    [JsonProperty("pirep_id")]               public long?     PirepId              { get; set; }
    [JsonProperty("touchdown_at")]           public DateTime  TouchdownAt          { get; set; }
    [JsonProperty("airport_icao")]           public string    AirportIcao          { get; set; } = "";
    [JsonProperty("runway_identifier")]      public string    RunwayIdentifier     { get; set; } = "";
    [JsonProperty("touchdown_lat")]          public double?   TouchdownLat         { get; set; }
    [JsonProperty("touchdown_lon")]          public double?   TouchdownLon         { get; set; }
    [JsonProperty("vs_fpm")]                 public double    VsFpm                { get; set; }
    [JsonProperty("ias_kts")]                public double    IasKts               { get; set; }
    [JsonProperty("gs_kts")]                 public double    GsKts                { get; set; }
    [JsonProperty("pitch_deg")]              public double    PitchDeg             { get; set; }
    [JsonProperty("bank_deg")]               public double    BankDeg              { get; set; }
    [JsonProperty("headwind_kt")]            public double    HeadwindKt           { get; set; }
    [JsonProperty("crosswind_kt")]           public double    CrosswindKt          { get; set; }
    [JsonProperty("wind_speed_kt")]          public double    WindSpeedKt          { get; set; }
    [JsonProperty("wind_dir_deg")]           public double    WindDirDeg           { get; set; }
    [JsonProperty("approach_stable")]        public bool      ApproachStable       { get; set; }
    [JsonProperty("flare_detected")]         public bool      FlareDetected        { get; set; }
    [JsonProperty("bounce_count")]           public int       BounceCount          { get; set; }
    [JsonProperty("rollout_distance_ft")]    public double    RolloutDistanceFt    { get; set; }
    [JsonProperty("avg_deceleration")]       public double    AvgDeceleration      { get; set; }
    [JsonProperty("threshold_distance_ft")]  public double    ThresholdDistanceFt  { get; set; }
    [JsonProperty("fuel_on_touchdown_lbs")]  public double    FuelOnTouchdownLbs   { get; set; }
    [JsonProperty("score")]                  public int       Score                { get; set; }
    [JsonProperty("score_vs")]               public int       ScoreVs              { get; set; }
    [JsonProperty("score_pitch")]            public int       ScorePitch           { get; set; }
    [JsonProperty("score_bank")]             public int       ScoreBank            { get; set; }
    [JsonProperty("score_speed")]            public int       ScoreSpeed           { get; set; }
    [JsonProperty("score_crosswind")]        public int       ScoreCrosswind       { get; set; }
    [JsonProperty("score_stability")]        public int       ScoreStability       { get; set; }
}

internal class LandingListResponse
{
    [JsonProperty("landings")] public List<RemoteLandingRecord> Landings { get; set; } = [];
}

internal class RemoteFriendRecord
{
    [JsonProperty("friend_pilot_id")] public string   FriendPilotId { get; set; } = "";
    [JsonProperty("friend_name")]     public string   FriendName    { get; set; } = "";
    [JsonProperty("friend_code")]     public string   FriendCode    { get; set; } = "";
    [JsonProperty("created_at")]      public DateTime CreatedAt     { get; set; }
}

internal class FriendListResponse
{
    [JsonProperty("friends")] public List<RemoteFriendRecord> Friends { get; set; } = [];
}

public class RemoteFriendActivity
{
    [JsonProperty("friend_name")]     public string   FriendName    { get; set; } = "";
    [JsonProperty("flight_number")]   public string   FlightNumber  { get; set; } = "";
    [JsonProperty("departure_icao")]  public string   DepartureICAO { get; set; } = "";
    [JsonProperty("arrival_icao")]    public string   ArrivalICAO   { get; set; } = "";
    [JsonProperty("aircraft_type")]   public string   AircraftType  { get; set; } = "";
    [JsonProperty("distance_nm")]     public int      DistanceNm    { get; set; }
    [JsonProperty("landing_score")]   public double   LandingScore  { get; set; }
    [JsonProperty("submitted_at")]    public DateTime SubmittedAt   { get; set; }
}

internal class FriendActivityResponse
{
    [JsonProperty("activity")] public List<RemoteFriendActivity> Activity { get; set; } = [];
}

// ── Support live chat ──────────────────────────────────────────────────────

public class SupportChatStartResult
{
    public bool Success { get; set; }
    public int ChatId { get; set; }
    public string VisitorToken { get; set; } = "";
    public string? Error { get; set; }
}

public class SupportChatSendResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public class SupportChatMessage
{
    [JsonProperty("id")]         public int Id { get; set; }
    [JsonProperty("senderType")] public string SenderType { get; set; } = "";
    [JsonProperty("senderName")] public string SenderName { get; set; } = "";
    [JsonProperty("content")]    public string Content { get; set; } = "";
    [JsonProperty("createdAt")]  public DateTime CreatedAt { get; set; }
}

public class SupportChatPollData
{
    [JsonProperty("status")]   public string Status { get; set; } = "open";
    [JsonProperty("messages")] public List<SupportChatMessage> Messages { get; set; } = [];
}

internal class SupportChatStartData
{
    [JsonProperty("chatId")]       public int ChatId { get; set; }
    [JsonProperty("visitorToken")] public string VisitorToken { get; set; } = "";
}

internal class SupportChatStartResponse
{
    [JsonProperty("success")] public bool Success { get; set; }
    [JsonProperty("error")]   public string? Error { get; set; }
    [JsonProperty("data")]    public SupportChatStartData? Data { get; set; }
}

internal class SupportChatGenericResponse
{
    [JsonProperty("success")] public bool Success { get; set; }
    [JsonProperty("error")]   public string? Error { get; set; }
}

internal class SupportChatPollResponse
{
    [JsonProperty("success")] public bool Success { get; set; }
    [JsonProperty("error")]   public string? Error { get; set; }
    [JsonProperty("data")]    public SupportChatPollData? Data { get; set; }
}
