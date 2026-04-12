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
    [JsonPropertyName("route_id")]      public int    RouteId      { get; set; }
    [JsonPropertyName("callsign")]      public string Callsign     { get; set; } = "";
    [JsonPropertyName("aircraft_type")] public string AircraftType { get; set; } = "";
    [JsonPropertyName("registration")]  public string? Registration { get; set; }
    [JsonPropertyName("scheduled_dep")] public string ScheduledDep { get; set; } = "";
    [JsonPropertyName("status")]        public string Status       { get; set; } = "confirmed";
    [JsonPropertyName("created_at")]    public string CreatedAt    { get; set; } = "";

    // Enriched from joined route data (set client-side after fetch)
    [JsonPropertyName("origin_iata")]   public string OriginIata   { get; set; } = "";
    [JsonPropertyName("dest_iata")]     public string DestIata     { get; set; } = "";
    [JsonPropertyName("origin_name")]   public string OriginName   { get; set; } = "";
    [JsonPropertyName("dest_name")]     public string DestName     { get; set; } = "";

    // The route's scheduled callsign (e.g. "VAV103") — enriched client-side, not persisted in booking row.
    // Used to populate SimBrief fltnum with the proper route number rather than the random booking callsign.
    [JsonIgnore] public string RouteCallsign { get; set; } = "";

    [JsonIgnore]
    public DateTime ScheduledDepUtc
    {
        get => DateTime.TryParse(ScheduledDep, out var dt) ? dt.ToUniversalTime() : DateTime.MinValue;
    }

    [JsonIgnore]
    public bool IsCancelled => Status == "cancelled";

    [JsonIgnore]
    public bool IsPast => ScheduledDepUtc < DateTime.UtcNow;
}

public class CreateBookingRequest
{
    [JsonPropertyName("acars_key")]     public string  AcarsKey     { get; set; } = "";
    [JsonPropertyName("route_id")]      public int     RouteId      { get; set; }
    [JsonPropertyName("callsign")]      public string  Callsign     { get; set; } = "";
    [JsonPropertyName("aircraft_type")] public string  AircraftType { get; set; } = "";
    [JsonPropertyName("registration")]  public string? Registration { get; set; }
    [JsonPropertyName("scheduled_dep")] public string  ScheduledDep { get; set; } = "";
}

public class BookingsResponse
{
    [JsonPropertyName("bookings")] public List<FlightBooking> Bookings { get; set; } = [];
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
    public  const int    MaxBookings = 5;

    // The booking currently being operated — set when pilot clicks "Start Flight",
    // read by the ACARS page to show context. Session-level state on the singleton.
    public FlightBooking? ActiveBooking { get; private set; }
    public void SetActiveBooking(FlightBooking? booking) => ActiveBooking = booking;

    public BookingService(SettingsService settings)
    {
        _settings = settings;
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(15) };
    }

    // =====================================================
    // CALLSIGN GENERATION
    //   Format: VAV + [1-4 digits] + [0-2 letters]
    //   Examples: VAV9, VAV64E, VAV023F, VAV34NE, VAV2D
    // =====================================================

    public static string GenerateCallsign()
    {
        var rng = Random.Shared;
        // Total suffix length: 1–4 characters
        int total      = rng.Next(1, 5);
        // Letters: 0–2, but must leave at least 1 slot for a digit
        int maxLetters = Math.Min(2, total - 1);
        int numLetters = rng.Next(0, maxLetters + 1);
        int numDigits  = total - numLetters;

        var sb = new System.Text.StringBuilder("VAV");
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
        AviatesRoute route, string aircraftType, string? registration, DateTime scheduledDep)
    {
        var acarsKey = _settings.Settings.AcarsKey;
        if (string.IsNullOrEmpty(acarsKey))
            return (null, "No ACARS key configured. Please set your ACARS key in Settings.");

        // Client-side guard: fetch current bookings and check limit
        var existing = await GetBookingsAsync();
        var active   = existing.Where(b => !b.IsCancelled && !b.IsPast).ToList();
        if (active.Count >= MaxBookings)
            return (null, $"You already have {MaxBookings} active bookings. Cancel one to add another.");

        var callsign = GenerateCallsign();

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
}
