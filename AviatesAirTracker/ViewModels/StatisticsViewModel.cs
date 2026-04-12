using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Models;
using AviatesAirTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using OxyPlot;
using OxyPlot.Series;
using System.Collections.ObjectModel;

namespace AviatesAirTracker.ViewModels;

public partial class StatisticsViewModel : ObservableObject
{
    [ObservableProperty] private string _totalFlights = "0";
    [ObservableProperty] private string _totalHours = "0.0h";
    [ObservableProperty] private string _totalDistance = "0nm";
    [ObservableProperty] private string _avgLandingScore = "—";
    [ObservableProperty] private string _bestLandingVs = "—";
    [ObservableProperty] private string _pilotRank = "First Officer";
    [ObservableProperty] private double _rankProgress;
    [ObservableProperty] private string _rankProgressText = "—";
    [ObservableProperty] private bool _isLoading;

    public ObservableCollection<FlightRecord> FlightLog { get; } = [];
    public PlotModel LandingScoreHistory { get; } = BuildScoreHistoryPlot();
    public PlotModel AltitudeProfile { get; } = BuildAltProfile();

    private readonly PilotStatsService _statsService;
    private readonly IFlightRepository _flightRepo;

    public StatisticsViewModel(PilotStatsService statsService, IFlightRepository flightRepo)
    {
        _statsService = statsService;
        _flightRepo = flightRepo;
    }

    // CRIT-11: Changed from async void to async Task.
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var stats = await _statsService.ComputeAsync();
            var flights = await _flightRepo.GetRecentAsync(50);

            TotalFlights = stats.TotalFlights.ToString();
            TotalHours = $"{stats.TotalHoursBlock:F1}h";
            TotalDistance = $"{stats.TotalDistanceNm:F0}nm";
            AvgLandingScore = stats.TotalLandings > 0 ? $"{stats.AverageLandingScore:F0}/100" : "—";
            BestLandingVs = stats.TotalLandings > 0 ? $"{stats.BestLandingVSFPM:F0}fpm" : "—";
            PilotRank = stats.Rank;
            RankProgress = stats.RankProgress;

            FlightLog.Clear();
            foreach (var f in flights) FlightLog.Add(f);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static PlotModel BuildScoreHistoryPlot()
    {
        return new PlotModel
        {
            Background = OxyColors.Transparent,
            PlotAreaBackground = OxyColors.Transparent,
            TextColor = OxyColor.FromRgb(136, 146, 170),
        };
    }

    private static PlotModel BuildAltProfile()
    {
        return new PlotModel
        {
            Background = OxyColors.Transparent,
            PlotAreaBackground = OxyColors.Transparent,
            TextColor = OxyColor.FromRgb(136, 146, 170),
        };
    }
}
