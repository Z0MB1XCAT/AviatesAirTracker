using AviatesAirTracker.Core.Backend;
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
    private readonly PilotStatsService _statsService;
    private readonly AviatesBackendClient _backend;

    [ObservableProperty] private string _welcomeText = "Welcome back, Pilot";
    [ObservableProperty] private string _totalHoursText = "0.0";
    [ObservableProperty] private string _totalFlightsText = "0";
    [ObservableProperty] private string _avgLandingScoreText = "—";
    [ObservableProperty] private string _totalDistanceText = "0";
    // "—" (not a fabricated percentage) until at least one scheduled/booked flight has been
    // completed -- see PilotStatsService.ComputeAsync's own OnTimePercent doc comment.
    [ObservableProperty] private string _onTimePercentText = "—";
    [ObservableProperty] private string _pilotRank = "First Officer";
    [ObservableProperty] private double _rankProgress = 0.15;
    // Raw next-rank name (e.g. "Captain"), not a pre-formatted sentence -- matches
    // PilotHubViewModel.NextRank's shape so the two ViewModels don't diverge in what they
    // consider "next rank" data. Any "Xhrs to <rank>" wording is a binding-site concern.
    [ObservableProperty] private string _nextRankText = "Captain";
    [ObservableProperty] private bool _isFlightActive;
    [ObservableProperty] private string _activeFlightRoute = "No Active Flight";
    [ObservableProperty] private string _activeFlightPhase = "";
    [ObservableProperty] private bool _isLoading;

    public ObservableCollection<FlightRecord> RecentFlights { get; } = [];
    public ObservableCollection<LandingResult> RecentLandings { get; } = [];
    public ObservableCollection<PilotStatsLogic.MonthlyActivityPoint> MonthlyActivity { get; } = [];
    public ObservableCollection<RemoteFriendActivity> FriendActivity { get; } = [];

    public DashboardViewModel(IFlightRepository flightRepo, ILandingRepository landingRepo,
        SettingsService settings, FlightSessionManager session, PilotStatsService statsService,
        AviatesBackendClient backend)
    {
        _flightRepo = flightRepo;
        _landingRepo = landingRepo;
        _settings = settings;
        _session = session;
        _statsService = statsService;
        _backend = backend;

        WelcomeText = $"Welcome back, {(settings.Settings.PilotName.Length > 0 ? settings.Settings.PilotName : "Pilot")}";
        session.SessionStateChanged += (_, s) =>
        {
            IsFlightActive = s != FlightSessionState.Idle;
            if (session.CurrentFlight is { } f)
                ActiveFlightRoute = $"{f.DepartureICAO} → {f.ArrivalICAO}";
        };
    }

    // CRIT-11: Changed from async void (silently crashed on repository exceptions) to async Task.
    // MAJOR-07: Was GetRecentAsync(5) for everything — TotalFlightsText always showed "5", career
    //           stats based on 5 flights only. Now fetches all flights for stats (unchanged below),
    //           and a separate GetRecentAsync(5) call feeds RecentFlights only (filtered+sorted,
    //           unlike raw allFlights.Take(5)). Do not collapse these back into one call.
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var allFlights = await _flightRepo.GetAllAsync();
            var landings = await _landingRepo.GetAllAsync();
            var recent = await _flightRepo.GetRecentAsync(5);

            RecentFlights.Clear();
            foreach (var f in recent) RecentFlights.Add(f);

            RecentLandings.Clear();
            foreach (var l in landings.Take(5)) RecentLandings.Add(l);

            // Held-for-review flights/landings still show in RecentFlights/RecentLandings below
            // (with a review badge) but don't count toward these summary stats until reviewed --
            // same exclusion PilotStatsService.ComputeAsync applies.
            var countedFlights = allFlights.Where(f => !f.RequiresReview).ToList();
            var countedLandings = landings.Where(l => !l.RequiresReview).ToList();

            TotalFlightsText = countedFlights.Count.ToString();
            // HoursMultiplier applies event-flight bonus hours -- see FlightRecord.HoursMultiplier's
            // own doc comment, same exclusion PilotStatsService.ComputeAsync applies.
            TotalHoursText = $"{countedFlights.Sum(f => f.BlockTime.TotalHours * f.HoursMultiplier):F1}";
            AvgLandingScoreText = countedLandings.Any() ? $"{countedLandings.Average(l => l.LandingScore):F0}" : "—";
            TotalDistanceText = $"{countedFlights.Sum(f => f.ActualDistanceNm):F0}";

            var stats = await _statsService.ComputeAsync();
            OnTimePercentText = stats.OnTimePercent.HasValue ? $"{stats.OnTimePercent.Value:F0}%" : "—";
            PilotRank = stats.Rank;
            (NextRankText, RankProgress) = PilotStatsLogic.ComputeRankProgress(stats.TotalHoursBlock, stats.Rank);

            MonthlyActivity.Clear();
            foreach (var point in await _statsService.GetMonthlyActivityAsync()) MonthlyActivity.Add(point);

            WelcomeText = $"Welcome back, {(_settings.Settings.PilotName.Length > 0 ? _settings.Settings.PilotName : "Pilot")}";

            // Replaces the old hardcoded "Operations Team" placeholder rows: real friends'
            // most recent flights, newest first, capped at 3 to match that section's original
            // fixed-3-row layout. Soft-fails to an empty list (no ACARS key, offline, no
            // friends yet) rather than blocking the rest of the dashboard refresh.
            var acarsKey = _settings.Settings.AcarsKey.Trim();
            FriendActivity.Clear();
            if (!string.IsNullOrEmpty(acarsKey))
            {
                foreach (var a in await _backend.FetchFriendsActivityAsync(acarsKey, 3))
                    FriendActivity.Add(a);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }
}
