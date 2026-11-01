using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Models;
using AviatesAirTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace AviatesAirTracker.ViewModels;

/// <summary>
/// Ported from Routes.razor's @code block (verbatim behavior, not a redesign). The
/// 5-active-booking cap is NOT re-checked here — BookingService.CreateBookingAsync already
/// enforces it internally. The only client-side validation Routes.razor performs lives in
/// the booking dialog (Task 3): date/time presence, departure in the future, departure not
/// more than 90 days out.
/// </summary>
public partial class RoutesViewModel : ObservableObject
{
    private readonly RoutesService _routesService;
    private readonly BookingService _bookingService;
    private readonly FleetService _fleetService;
    private readonly SettingsService _settings;
    private readonly AviatesBackendClient _backendClient;

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private bool _isLoading = true;

    public ObservableCollection<AviatesRoute> Routes { get; } = [];

    public string CurrentAirportIata => _settings.Settings.CurrentAirportIata ?? "";

    public RoutesViewModel(
        RoutesService routesService, BookingService bookingService, FleetService fleetService,
        SettingsService settings, AviatesBackendClient backendClient)
    {
        _routesService = routesService;
        _bookingService = bookingService;
        _fleetService = fleetService;
        _settings = settings;
        _backendClient = backendClient;
    }

    public async Task SyncPositionAsync() => await _bookingService.SyncPositionAsync();

    public async Task LoadRoutesAsync(string tab, string? searchQuery)
    {
        IsLoading = true;
        try
        {
            var fleet = tab == "all" ? null : tab;
            var q = string.IsNullOrWhiteSpace(searchQuery) ? null : searchQuery.Trim();
            var origin = string.IsNullOrEmpty(CurrentAirportIata) ? null : CurrentAirportIata;

            var (routes, total) = await _routesService.GetRoutesAsync(fleet, q, origin);
            Routes.Clear();
            foreach (var r in routes) Routes.Add(r);
            TotalCount = total;
        }
        finally { IsLoading = false; }
    }

    // Matches Routes.razor's ClearPosition() sequencing exactly: SetPositionAsync, then a
    // separate reload. The reload is deliberately NOT baked in here -- the page (Task 2) owns
    // the current tab/search state and calls LoadRoutesAsync itself right after this returns.
    //
    // Rate-limited to once every 7 days (BookingService.CanManuallyReposition) -- returns
    // (false, remaining) without touching position/booking state if called again too soon, so
    // the page can show "try again in N days" instead of silently no-opping. On success, also
    // cancels the pilot's single active booking (MaxBookings = 1): a reposition means they're no
    // longer flying from the airport that booking assumed, so leaving it confirmed would let the
    // pilot's booking and position disagree.
    //
    // Instant locally, cloud catches up: local position/booking state (SetPositionAsync writes
    // CurrentAirportIata synchronously before its own network PATCH; SetActiveBooking(null) is
    // pure in-memory) is all committed before this method returns, so the page can reload routes
    // immediately. The actual booking cancel and position PATCH run in the background rather
    // than being awaited here -- this never skips server-side validation/abuse-prevention (both
    // calls still run for real, in full), it just doesn't block the pilot's own screen on them.
    public async Task<(bool Success, TimeSpan? RemainingCooldown)> ClearPositionAsync()
    {
        var (allowed, remaining) = _bookingService.CanManuallyReposition();
        if (!allowed) return (false, remaining);

        // GetCurrentBookingAsync still needs a real fetch (there's no reliable local-only source
        // for "does a booking exist server-side" -- ActiveBooking is only ever set for a booking
        // created or resumed THIS session), but the cancel call itself doesn't block on it.
        var current = await _bookingService.GetCurrentBookingAsync();
        if (current is { IsCancelled: false })
            _ = _bookingService.CancelBookingAsync(current.Id);
        if (_bookingService.ActiveBooking != null)
            _bookingService.SetActiveBooking(null);

        _ = _bookingService.SetPositionAsync("");

        _settings.Settings.LastManualRepositionUtc = DateTime.UtcNow;
        _settings.Save();

        return (true, null);
    }

    public async Task<List<AircraftRegistration>> LoadRegistrationsAsync(string typeCode)
    {
        if (string.IsNullOrEmpty(typeCode)) return [];
        return await _fleetService.GetRegistrationsAsync(typeCode);
    }

    public static string GenerateCallsign(string? aircraftType) => BookingService.GenerateCallsign(aircraftType);

    // Also makes the new booking the active one: before this, CreateBookingAsync returned the
    // created booking but nothing ever called BookingService.SetActiveBooking with it -- the
    // ONLY call sites anywhere in the app passed null (clearing it), so a freshly booked flight
    // never appeared on the ACARS page's active-booking banner and there was no way to "start"
    // it at all. Booking a flight is the natural moment to make it the one you're about to fly.
    // Live-checked reject before the network round-trip -- POST /api/bookings enforces this same
    // rule server-side regardless (worker.js's own comment on that check), so this is purely for
    // a fast, friendly local error message, not a security boundary. Deliberately fetches
    // Settings.Subsidiary fresh from the backend here rather than trusting the locally-cached
    // copy: that cache is only ever refreshed by SyncProfileAsync's manual "Verify & Sync" in
    // Settings, so a pilot who chose/changed subsidiary on the website (the only place the
    // picker exists) without ever revisiting Settings would otherwise see a silently stale
    // (usually null/unrestricted) local value and could book across subsidiaries the server
    // would then reject anyway -- confusing, and on a flaky connection worse than just asking
    // the server directly. A pilot with no subsidiary chosen at all (GetSubsidiaryAsync
    // succeeds but returns null) is blocked from a normal booking, matching the server's own
    // tightened check -- only Charter (CreateCharterBookingAsync below) has no such gate.
    // GetSubsidiaryAsync failing outright (network/parse -- Success=false) falls through to the
    // real network call instead of blocking on a connectivity issue this check can't resolve.
    public async Task<(FlightBooking? Booking, string? Error)> CreateBookingAsync(
        AviatesRoute route, string aircraftType, string? registration, DateTime scheduledDepUtc, string callsign,
        bool isCharter = false)
    {
        var acarsKey = _settings.Settings.AcarsKey;
        if (!string.IsNullOrEmpty(acarsKey))
        {
            var (subsidiary, success) = await _backendClient.GetSubsidiaryAsync(acarsKey);
            if (success)
            {
                _settings.Settings.Subsidiary = subsidiary;

                if (string.IsNullOrEmpty(subsidiary))
                {
                    return (null, "Choose a subsidiary (Mainline, Cityhopper, or Sky Cargo) on the AviatesAir " +
                        "website before booking a scheduled route -- or use Charter, which has no subsidiary restriction.");
                }

                // A route can be flown by several subsidiaries; what matters is who flies the
                // aircraft being booked.
                var fleet = route.AircraftFor(aircraftType)?.FleetGroup is { Length: > 0 } f ? f : route.FleetGroup;
                if (!string.Equals(fleet, subsidiary, StringComparison.OrdinalIgnoreCase))
                {
                    return (null, $"The {aircraftType} on this route is flown by a different subsidiary ({fleet}). " +
                        $"You're currently assigned to {subsidiary} -- pick an aircraft your subsidiary flies, switch subsidiary on the website, or use Charter.");
                }
            }
        }

        var (booking, error) = await _bookingService.CreateBookingAsync(route, aircraftType, registration, scheduledDepUtc, callsign);
        if (booking != null)
            _bookingService.SetActiveBooking(booking, isCharter);
        return (booking, error);
    }

    // CharterBookingDialog's own entry point -- a charter has no AviatesRoute at all (see
    // BookingService.CreateCharterBookingAsync's doc comment), so this can't reuse
    // CreateBookingAsync above, which requires one.
    public async Task<(FlightBooking? Booking, string? Error)> CreateCharterBookingAsync(
        string originIcao, string destIcao, string aircraftType, string? registration, DateTime scheduledDepUtc, string callsign)
    {
        var (booking, error) = await _bookingService.CreateCharterBookingAsync(originIcao, destIcao, aircraftType, registration, scheduledDepUtc, callsign);
        if (booking != null)
            _bookingService.SetActiveBooking(booking, isCharter: true);
        return (booking, error);
    }

    // Exposed so BookingDialog (Task 3) only ever needs a RoutesViewModel reference, never its
    // own separate BookingService reference.
    public async Task SetPositionAsync(string iata) => await _bookingService.SetPositionAsync(iata);
}
