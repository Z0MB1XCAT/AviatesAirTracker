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

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private bool _isLoading = true;

    public ObservableCollection<AviatesRoute> Routes { get; } = [];

    public string CurrentAirportIata => _settings.Settings.CurrentAirportIata ?? "";

    public RoutesViewModel(RoutesService routesService, BookingService bookingService, FleetService fleetService, SettingsService settings)
    {
        _routesService = routesService;
        _bookingService = bookingService;
        _fleetService = fleetService;
        _settings = settings;
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
    public async Task ClearPositionAsync() => await _bookingService.SetPositionAsync("");

    public async Task<List<AircraftRegistration>> LoadRegistrationsAsync(string typeCode)
    {
        if (string.IsNullOrEmpty(typeCode)) return [];
        return await _fleetService.GetRegistrationsAsync(typeCode);
    }

    public static string GenerateCallsign(string? aircraftType) => BookingService.GenerateCallsign(aircraftType);

    public async Task<(FlightBooking? Booking, string? Error)> CreateBookingAsync(
        AviatesRoute route, string aircraftType, string? registration, DateTime scheduledDepUtc, string callsign)
        => await _bookingService.CreateBookingAsync(route, aircraftType, registration, scheduledDepUtc, callsign);

    // Exposed so BookingDialog (Task 3) only ever needs a RoutesViewModel reference, never its
    // own separate BookingService reference.
    public async Task SetPositionAsync(string iata) => await _bookingService.SetPositionAsync(iata);
}
