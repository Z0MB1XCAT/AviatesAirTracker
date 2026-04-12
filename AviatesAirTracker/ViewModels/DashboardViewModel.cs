using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Models;
using AviatesAirTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace AviatesAirTracker.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly IFlightRepository _flightRepo;
    private readonly ILandingRepository _landingRepo;
    private readonly SettingsService _settings;
    private readonly FlightSessionManager _session;

    [ObservableProperty] private string _welcomeText = "Welcome back, Pilot";
    [ObservableProperty] private string _totalHoursText = "0.0";
    [ObservableProperty] private string _totalFlightsText = "0";
    [ObservableProperty] private string _avgLandingScoreText = "—";
    [ObservableProperty] private string _totalDistanceText = "0";
    [ObservableProperty] private string _pilotRank = "First Officer";
    [ObservableProperty] private double _rankProgress = 0.15;
    [ObservableProperty] private string _nextRankText = "25hrs to Captain";
    [ObservableProperty] private bool _isFlightActive;
    [ObservableProperty] private string _activeFlightRoute = "No Active Flight";
    [ObservableProperty] private string _activeFlightPhase = "";
    [ObservableProperty] private bool _isLoading;

    public ObservableCollection<FlightRecord> RecentFlights { get; } = [];
    public ObservableCollection<LandingResult> RecentLandings { get; } = [];

    public DashboardViewModel(IFlightRepository flightRepo, ILandingRepository landingRepo,
        SettingsService settings, FlightSessionManager session)
    {
        _flightRepo = flightRepo;
        _landingRepo = landingRepo;
        _settings = settings;
        _session = session;

        WelcomeText = $"Welcome back, {(settings.Settings.PilotName.Length > 0 ? settings.Settings.PilotName : "Pilot")}";
        session.SessionStateChanged += (_, s) =>
        {
            IsFlightActive = s != FlightSessionState.Idle;
            if (session.CurrentFlight is { } f)
                ActiveFlightRoute = $"{f.DepartureICAO} → {f.ArrivalICAO}";
        };
    }

    // CRIT-11: Changed from async void (silently crashed on repository exceptions) to async Task.
    // MAJOR-07: Was GetRecentAsync(5) — TotalFlightsText always showed "5", career stats based on 5 flights only.
    //           Now fetches all flights for stats, recent 5 only for the display list.
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var allFlights = await _flightRepo.GetAllAsync();
            var landings = await _landingRepo.GetAllAsync();

            RecentFlights.Clear();
            foreach (var f in allFlights.Take(5)) RecentFlights.Add(f);

            RecentLandings.Clear();
            foreach (var l in landings.Take(5)) RecentLandings.Add(l);

            TotalFlightsText = allFlights.Count.ToString();
            TotalHoursText = $"{allFlights.Sum(f => f.BlockTime.TotalHours):F1}";
            AvgLandingScoreText = landings.Any() ? $"{landings.Average(l => l.LandingScore):F0}" : "—";
            TotalDistanceText = $"{allFlights.Sum(f => f.ActualDistanceNm):F0}";

            WelcomeText = $"Welcome back, {(_settings.Settings.PilotName.Length > 0 ? _settings.Settings.PilotName : "Pilot")}";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
