using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace AviatesAirTracker.Services;

// ============================================================
// BOOKING MODELS
// ============================================================

public class FlightBooking
{
    [JsonPropertyName("id")]            public int    Id           { get; set; }
    [JsonPropertyName("acars_key")]     public string AcarsKey     { get; set; } = "";
    [JsonPropertyName("route_id")]      public int?   RouteId      { get; set; }
    [JsonPropertyName("callsign")]      public string Callsign     { get; set; } = "";
    [JsonPropertyName("aircraft_type")] public string AircraftType { get; set; } = "";
    [JsonPropertyName("registration")]  public string? Registration { get; set; }
    [JsonPropertyName("scheduled_dep")] public string ScheduledDep { get; set; } = "";
    [JsonPropertyName("status")]        public string Status       { get; set; } = "confirmed";
    [JsonPropertyName("created_at")]    public string CreatedAt    { get; set; } = "";

    // Set server-side by POST /api/bookings/:id/activate — null means not yet activated. One-way:
    // there is no un-activate, either here or server-side (see that endpoint's own comment).
    [JsonPropertyName("activated_at")]  public string? ActivatedAt { get; set; }

    // Enriched from joined route data (set client-side after fetch)
    [JsonPropertyName("origin_iata")]   public string OriginIata   { get; set; } = "";
    [JsonPropertyName("dest_iata")]     public string DestIata     { get; set; } = "";
    [JsonPropertyName("origin_name")]   public string OriginName   { get; set; } = "";
    [JsonPropertyName("dest_name")]     public string DestName     { get; set; } = "";

    // The route's scheduled callsign (e.g. "VAV103") — enriched client-side, not persisted in booking row.
    // Used to populate SimBrief fltnum with the proper route number rather than the random booking callsign.
    [JsonIgnore] public string RouteCallsign { get; set; } = "";

    // The route's scheduled flight number in display form (e.g. "AV700") — enriched
    // client-side, not persisted in the booking row. Used for the Flights column,
    // booking UI, and (numeric part) the SimBrief fltnum.
    [JsonIgnore] public string RouteFlightNumber { get; set; } = "";

    [JsonIgnore]
    public DateTime ScheduledDepUtc
    {
        get => DateTime.TryParse(ScheduledDep, out var dt) ? dt.ToUniversalTime() : DateTime.MinValue;
    }

    [JsonIgnore]
    public bool IsCancelled => Status == "cancelled";

    [JsonIgnore]
    public bool IsPast => ScheduledDepUtc < DateTime.UtcNow;

    [JsonIgnore]
    public bool IsActivated => !string.IsNullOrEmpty(ActivatedAt);
}

public class CreateBookingRequest
{
    [JsonPropertyName("acars_key")]           public string  AcarsKey          { get; set; } = "";
    [JsonPropertyName("route_id")]            public int?    RouteId           { get; set; }
    [JsonPropertyName("callsign")]            public string  Callsign          { get; set; } = "";
    [JsonPropertyName("aircraft_type")]       public string  AircraftType      { get; set; } = "";
    [JsonPropertyName("registration")]        public string? Registration      { get; set; }
    [JsonPropertyName("scheduled_dep")]       public string  ScheduledDep      { get; set; } = "";

    // Charter-only fields (see CharterBookingDialog): a charter can depart/arrive anywhere, not
    // just between two AviatesAir-serviced airports, so it has no RouteId at all — it carries its
    // own free-typed ICAO pair instead. RouteId stays null for these; both are null/false for a
    // normal route-based booking.
    [JsonPropertyName("is_charter")]          public bool    IsCharter         { get; set; }
    [JsonPropertyName("charter_origin_icao")] public string? CharterOriginIcao { get; set; }
    [JsonPropertyName("charter_dest_icao")]   public string? CharterDestIcao   { get; set; }
}

public class BookingsResponse
{
    [JsonPropertyName("bookings")] public List<FlightBooking> Bookings { get; set; } = [];
}

public class PositionResponse
{
    [JsonPropertyName("current_airport_iata")] public string? CurrentAirportIata { get; set; }
}

// ============================================================
// BOOKING SERVICE
// Manages flight bookings tied to the pilot's ACARS key.
// Bookings are stored in Cloudflare D1 via the backend API.
// Max 5 active bookings per pilot.
// ============================================================

public class BookingService
{
    private readonly HttpClient   _http;
    private readonly SettingsService _settings;
    private const string BaseUrl    = "https://acars.flyaviatesair.uk";
    // One booking at a time -- Pilot Hub now shows it inline (no more separate My Bookings
    // list page to manage several), and a single booking keeps "reposition cancels the active
    // booking" (see ClearPositionAsync/RoutesViewModel) and "missed booking expires" unambiguous
    // about which booking they refer to.
    public  const int    MaxBookings = 1;

    // The booking currently being operated — set when pilot clicks "Start Flight",
    // read by the ACARS page to show context. Session-level state on the singleton.
    // volatile: CompleteActiveBookingAsync writes on a thread-pool thread; OnEnginesStarted
    // reads on the SimConnect pump thread — the keyword prevents stale cached reads.
    private volatile FlightBooking? _activeBooking;
    public FlightBooking? ActiveBooking
    {
        get => _activeBooking;
        private set => _activeBooking = value;
    }

    // Charter flights (RoutesPage's Charter button/CharterBookingDialog -- the pilot picks both
    // route and aircraft themselves, unlike a normal booking's route-then-fixed-aircraft flow)
    // earn 0.5x hours. Reset whenever a new active booking is set (default isCharter: false) so
    // a later normal booking never inherits a stale charter flag from a previous one.
    public bool IsActiveBookingCharter { get; private set; }

    public void SetActiveBooking(FlightBooking? booking, bool isCharter = false)
    {
        ActiveBooking = booking;
        IsActiveBookingCharter = booking != null && isCharter;
    }

    public BookingService(SettingsService settings)
    {
        _settings = settings;
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
    }

    // =====================================================
    // CALLSIGN GENERATION
    //   Format: <ICAO prefix> + [1-4 digits] + [0-2 letters]
    //   Prefix varies by subsidiary: VAV mainline, VAH CityHopper, VAC Skycargo.
    //   Examples: VAV9, VAH64E, VAC023F, VAV34NE, VAH2D
    // =====================================================

    public static string GenerateCallsign(string? aircraftType)
    {
        var prefix = Subsidiary.IcaoPrefix(aircraftType);
        var rng = Random.Shared;
        // Total suffix length: 1–4 characters
        int total      = rng.Next(1, 5);
        // Letters: 0–2, but must leave at least 1 slot for a digit
        int maxLetters = Math.Min(2, total - 1);
        int numLetters = rng.Next(0, maxLetters + 1);
        int numDigits  = total - numLetters;

        var sb = new System.Text.StringBuilder(prefix);
        for (int i = 0; i < numDigits; i++)
        {
            // First (and only) digit must not be 0 when it would be the sole suffix character
            // — prevents generating "VAV0" which reads as an invalid flight number.
            bool soloDigit = (i == 0 && numDigits == 1 && numLetters == 0);
            sb.Append((char)('0' + rng.Next(soloDigit ? 1 : 0, 10)));
        }
        for (int i = 0; i < numLetters; i++)
            sb.Append((char)('A' + rng.Next(0, 26)));

        return sb.ToString();
    }

    // =====================================================
    // FETCH BOOKINGS
    // =====================================================

    public async Task<List<FlightBooking>> GetBookingsAsync()
    {
        var acarsKey = _settings.Settings.AcarsKey;
        if (string.IsNullOrEmpty(acarsKey)) return [];

        try
        {
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", acarsKey);

            var response = await _http.GetAsync("/api/bookings");
            if (!response.IsSuccessStatusCode) return [];

            var result = await response.Content.ReadFromJsonAsync<BookingsResponse>();
            return result?.Bookings ?? [];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[BookingService] Failed to fetch bookings");
            return [];
        }
    }

    // =====================================================
    // CREATE BOOKING
    // =====================================================

    /// <summary>
    /// Creates a booking for the given route. Returns the created booking,
    /// or null if the request fails or the pilot already has 5 active bookings.
    /// </summary>
    public async Task<(FlightBooking? Booking, string? Error)> CreateBookingAsync(
        AviatesRoute route, string aircraftType, string? registration, DateTime scheduledDep,
        string? callsign = null)
    {
        var acarsKey = _settings.Settings.AcarsKey;
        if (string.IsNullOrEmpty(acarsKey))
            return (null, "No ACARS key configured. Please set your ACARS key in Settings.");

        // Client-side guard: fetch current bookings and check limit
        var existing = await GetBookingsAsync();
        var active   = existing.Where(b => !b.IsCancelled && !b.IsPast).ToList();
        if (active.Count >= MaxBookings)
            return (null, $"You already have {MaxBookings} active bookings. Cancel one to add another.");

        // Use the callsign the pilot saw/regenerated in the booking modal; only
        // generate a fresh one as a safety net if the caller passed nothing.
        callsign = string.IsNullOrWhiteSpace(callsign) ? GenerateCallsign(aircraftType) : callsign;

        var payload = new CreateBookingRequest
        {
            AcarsKey     = acarsKey,
            RouteId      = route.Id,
            Callsign     = callsign,
            AircraftType = aircraftType,
            Registration = string.IsNullOrWhiteSpace(registration) ? null : registration,
            ScheduledDep = scheduledDep.ToUniversalTime().ToString("o")
        };

        try
        {
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", acarsKey);

            var response = await _http.PostAsJsonAsync("/api/bookings", payload);

            if (response.IsSuccessStatusCode)
            {
                var created = await response.Content.ReadFromJsonAsync<FlightBooking>();
                if (created != null)
                {
                    // Enrich with route info (returned booking may omit these)
                    created.OriginIata    = route.OriginIata;
                    created.DestIata      = route.DestIata;
                    created.OriginName    = route.OriginName;
                    created.DestName      = route.DestName;
                    created.RouteCallsign = route.Callsign;
                    created.RouteFlightNumber = route.FlightNumber;
                }
                Log.Information("[BookingService] Booking created: {Callsign} {Orig}→{Dest}",
                    callsign, route.OriginIata, route.DestIata);
                return (created, null);
            }

            var body = await response.Content.ReadAsStringAsync();
            Log.Warning("[BookingService] Create failed: {Status} {Body}", response.StatusCode, body);
            return (null, $"Booking failed ({(int)response.StatusCode}). Please try again.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[BookingService] Create booking error");
            return (null, "Network error. Please check your connection.");
        }
    }

    // =====================================================
    // CREATE CHARTER BOOKING
    // Same flow as CreateBookingAsync, except there's no AviatesRoute at all -- a charter can fly
    // between any two airports, so the pilot types both ICAOs directly rather than picking a
    // route the network already schedules. The aircraft is still constrained to the real fleet
    // (CharterBookingDialog's ComboBox only ever lists real /api/fleet/types entries, and the
    // server independently re-checks aircraftType against aircraft_types -- see POST
    // /api/bookings' isCharter branch).
    // =====================================================

    public async Task<(FlightBooking? Booking, string? Error)> CreateCharterBookingAsync(
        string originIcao, string destIcao, string aircraftType, string? registration, DateTime scheduledDep,
        string? callsign = null)
    {
        var acarsKey = _settings.Settings.AcarsKey;
        if (string.IsNullOrEmpty(acarsKey))
            return (null, "No ACARS key configured. Please set your ACARS key in Settings.");

        var existing = await GetBookingsAsync();
        var active   = existing.Where(b => !b.IsCancelled && !b.IsPast).ToList();
        if (active.Count >= MaxBookings)
            return (null, $"You already have {MaxBookings} active bookings. Cancel one to add another.");

        callsign = string.IsNullOrWhiteSpace(callsign) ? GenerateCallsign(aircraftType) : callsign;

        var payload = new CreateBookingRequest
        {
            AcarsKey          = acarsKey,
            IsCharter         = true,
            CharterOriginIcao = originIcao.Trim().ToUpperInvariant(),
            CharterDestIcao   = destIcao.Trim().ToUpperInvariant(),
            Callsign          = callsign,
            AircraftType      = aircraftType,
            Registration      = string.IsNullOrWhiteSpace(registration) ? null : registration,
            ScheduledDep      = scheduledDep.ToUniversalTime().ToString("o"),
        };

        try
        {
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", acarsKey);

            var response = await _http.PostAsJsonAsync("/api/bookings", payload);

            if (response.IsSuccessStatusCode)
            {
                var created = await response.Content.ReadFromJsonAsync<FlightBooking>();
                if (created != null)
                {
                    // The server's get_booking_by_id already COALESCEs these from the charter
                    // columns, but setting them here too costs nothing and keeps this path
                    // resilient if that response shape ever changes.
                    created.OriginIata = payload.CharterOriginIcao!;
                    created.DestIata   = payload.CharterDestIcao!;
                }
                Log.Information("[BookingService] Charter booking created: {Callsign} {Orig}→{Dest}",
                    callsign, payload.CharterOriginIcao, payload.CharterDestIcao);
                return (created, null);
            }

            var body = await response.Content.ReadAsStringAsync();
            Log.Warning("[BookingService] Create charter failed: {Status} {Body}", response.StatusCode, body);
            return (null, $"Booking failed ({(int)response.StatusCode}). Please try again.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[BookingService] Create charter booking error");
            return (null, "Network error. Please check your connection.");
        }
    }

    // =====================================================
    // ACTIVATE BOOKING
    // One-way: opens SimBrief dispatch pre-filled from the booking (SimBriefService.OpenDispatch,
    // called by the caller before/alongside this — this method only persists the server-side
    // lock) and marks the booking so it can never be cancelled again. There is no
    // DeactivateBookingAsync; the server itself refuses a second activate call and refuses DELETE
    // once activated_at is set, so this isn't just a client-side restriction.
    // =====================================================

    public async Task<(FlightBooking? Booking, string? Error)> ActivateBookingAsync(int bookingId)
    {
        var acarsKey = _settings.Settings.AcarsKey;
        if (string.IsNullOrEmpty(acarsKey))
            return (null, "No ACARS key configured.");

        try
        {
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", acarsKey);

            var response = await _http.PostAsync($"/api/bookings/{bookingId}/activate", null);
            if (response.IsSuccessStatusCode)
            {
                var updated = await response.Content.ReadFromJsonAsync<FlightBooking>();
                Log.Information("[BookingService] Booking {Id} activated", bookingId);
                return (updated, null);
            }

            var body = await response.Content.ReadAsStringAsync();
            Log.Warning("[BookingService] Activate failed: {Status} {Body}", response.StatusCode, body);
            return (null, $"Activation failed ({(int)response.StatusCode}). Please try again.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[BookingService] Activate booking error");
            return (null, "Network error. Please check your connection.");
        }
    }

    // =====================================================
    // CANCEL BOOKING
    // =====================================================

    public async Task<(bool Success, string? Error)> CancelBookingAsync(int bookingId)
    {
        var acarsKey = _settings.Settings.AcarsKey;
        if (string.IsNullOrEmpty(acarsKey))
            return (false, "No ACARS key configured.");

        try
        {
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", acarsKey);

            var response = await _http.DeleteAsync($"/api/bookings/{bookingId}");
            if (response.IsSuccessStatusCode)
            {
                Log.Information("[BookingService] Booking {Id} cancelled", bookingId);
                return (true, null);
            }

            return (false, $"Cancel failed ({(int)response.StatusCode}). Please try again.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[BookingService] Cancel booking error");
            return (false, "Network error. Please check your connection.");
        }
    }

    // =====================================================
    // COMPLETE ACTIVE BOOKING
    // Marks the active booking as completed on the backend,
    // advances the pilot's home base to the arrival airport,
    // and clears ActiveBooking. Fire-and-forget safe.
    // =====================================================

    public async Task CompleteActiveBookingAsync()
    {
        if (ActiveBooking == null) return;

        var booking  = ActiveBooking;
        var acarsKey = _settings.Settings.AcarsKey;

        // Clear immediately — UI should not show a stale active booking
        ActiveBooking = null;

        if (string.IsNullOrEmpty(acarsKey)) return;

        try
        {
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", acarsKey);

            var response = await _http.PatchAsync(
                $"/api/bookings/{booking.Id}",
                new StringContent("""{"status":"completed"}""", System.Text.Encoding.UTF8, "application/json"));

            if (response.IsSuccessStatusCode)
                Log.Information("[BookingService] Booking {Id} completed", booking.Id);
            else
                Log.Warning("[BookingService] Complete booking {Id} failed: {Status}",
                    booking.Id, response.StatusCode);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[BookingService] CompleteActiveBookingAsync error");
        }

        // Update home base regardless of API success
        if (!string.IsNullOrEmpty(booking.DestIata))
            await SetPositionAsync(booking.DestIata);
    }

    // =====================================================
    // MANUAL REPOSITION RATE LIMIT
    // Routes' "Reposition" action (RoutesViewModel.ClearPositionAsync) is capped at once every 7
    // days. The automatic expiry path (ExpireStaleBookingAsync below, a missed booking) does NOT
    // go through this check and does NOT update LastManualRepositionUtc -- it's a separate,
    // unlimited system action, not something the weekly cap is meant to throttle.
    // =====================================================

    private static readonly TimeSpan ManualRepositionCooldown = TimeSpan.FromDays(7);

    public (bool Allowed, TimeSpan? RemainingCooldown) CanManuallyReposition()
    {
        var last = _settings.Settings.LastManualRepositionUtc;
        if (last == DateTime.MinValue) return (true, null);

        var elapsed = DateTime.UtcNow - last;
        return elapsed >= ManualRepositionCooldown
            ? (true, null)
            : (false, ManualRepositionCooldown - elapsed);
    }

    // =====================================================
    // CURRENT BOOKING / EXPIRY
    // Pilot Hub shows the pilot's single booking (MaxBookings = 1) inline rather than linking to
    // a separate list page. Server-side /api/bookings is the source of truth -- ActiveBooking is
    // only the in-memory "flight in progress right now" pointer, and is null across app restarts
    // even though the booking itself still exists server-side.
    // =====================================================

    public async Task<FlightBooking?> GetCurrentBookingAsync()
    {
        var bookings = await GetBookingsAsync();
        return bookings.FirstOrDefault(b => !b.IsCancelled);
    }

    /// <summary>If <paramref name="booking"/>'s scheduled departure has passed and it was never
    /// flown (still "confirmed"), cancels it server-side and clears ActiveBooking if it was the
    /// one active. Returns the now-expired booking on success, so the caller can show "Booking
    /// Expired" messaging, or null if nothing needed expiring. CurrentAirportIata is untouched --
    /// a booking that's merely created never moves it (only completing the flight does, in
    /// App.xaml.cs's FlightCompleted handler), so "repositioned to their last location" is
    /// already true by construction; there's nothing to actively move.</summary>
    public async Task<FlightBooking?> ExpireIfStaleAsync(FlightBooking booking)
    {
        if (booking.IsCancelled || !booking.IsPast || booking.Status != "confirmed") return null;

        var (success, _) = await CancelBookingAsync(booking.Id);
        if (success && ActiveBooking?.Id == booking.Id)
            SetActiveBooking(null);

        return success ? booking : null;
    }

    // =====================================================
    // SYNC POSITION FROM SUPABASE
    // Overwrites the local CurrentAirportIata with the
    // value stored in Supabase. Call on app startup / Routes init.
    // Silently no-ops on failure — local cache stays valid.
    // =====================================================

    public async Task SyncPositionAsync()
    {
        var acarsKey = _settings.Settings.AcarsKey;
        if (string.IsNullOrEmpty(acarsKey)) return;

        try
        {
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", acarsKey);

            var response = await _http.GetAsync("/api/pilot/position");
            if (!response.IsSuccessStatusCode) return;

            var body = await response.Content.ReadFromJsonAsync<PositionResponse>();
            if (body == null) return;

            _settings.Settings.CurrentAirportIata = body.CurrentAirportIata ?? "";
            _settings.Save();
            Log.Information("[Position] Synced from server: {IATA}", _settings.Settings.CurrentAirportIata);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Position] SyncPositionAsync failed — using local cache");
        }
    }

    // =====================================================
    // SET POSITION LOCALLY + SYNC TO SUPABASE
    // Updates the local setting immediately (so the UI is
    // instant) then fires a background PATCH to Supabase.
    // =====================================================

    public async Task SetPositionAsync(string iata)
    {
        _settings.Settings.CurrentAirportIata = iata;
        _settings.Save();

        var acarsKey = _settings.Settings.AcarsKey;
        if (string.IsNullOrEmpty(acarsKey)) return;

        try
        {
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", acarsKey);

            await _http.PatchAsJsonAsync("/api/pilot/position",
                new { current_airport_iata = iata });

            Log.Information("[Position] Synced to server: {IATA}", iata);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Position] SetPositionAsync PATCH failed — local setting saved");
        }
    }
}
