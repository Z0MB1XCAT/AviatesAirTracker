using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Models;
using AviatesAirTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace AviatesAirTracker.ViewModels;

public partial class PilotHubViewModel : ObservableObject
{
    // Career stat cards
    [ObservableProperty] private string _totalFlights = "—";
    [ObservableProperty] private string _blockHours = "—";
    [ObservableProperty] private string _avgLandingScore = "—";
    [ObservableProperty] private string _totalDistance = "—";

    // Rank card
    [ObservableProperty] private string _pilotRank = "—";
    [ObservableProperty] private string _nextRank = "—";
    [ObservableProperty] private double _rankProgressPct;   // 0–100, used for CSS width%

    // Last landing analysis card
    [ObservableProperty] private LandingResult? _lastLanding;
    [ObservableProperty] private string _lastLandingGradeLabel = "—";
    [ObservableProperty] private string _lastLandingGradeClass = "grade-a";
    [ObservableProperty] private double _lastLandingRingOffset = 408.41; // full-ring = empty state

    // State
    [ObservableProperty] private bool _hasLandings;
    [ObservableProperty] private bool _isLoading;

    // Landing history table (most recent 10)
    public ObservableCollection<LandingResult> RecentLandings { get; } = [];

    // Blazor pages subscribe to this to trigger StateHasChanged after a background refresh
    public event Action? DataRefreshed;

    private readonly PilotStatsService _statsService;
    private readonly ILandingRepository _landingRepo;

    public PilotHubViewModel(PilotStatsService statsService, ILandingRepository landingRepo, FlightSessionManager sessionManager)
    {
        _statsService = statsService;
        _landingRepo  = landingRepo;
        sessionManager.FlightCompleted += (_, _) => _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var stats   = await _statsService.ComputeAsync();
            var landings = (await _landingRepo.GetAllAsync())
                .OrderByDescending(l => l.Timestamp)
                .Take(10)
                .ToList();

            TotalFlights     = stats.TotalFlights.ToString();
            BlockHours       = $"{stats.TotalHoursBlock:F1}h";
            AvgLandingScore  = stats.TotalLandings > 0 ? $"{stats.AverageLandingScore:F0}" : "—";
            TotalDistance    = stats.TotalDistanceNm >= 1000
                               ? $"{stats.TotalDistanceNm / 1000:F1}k nm"
                               : $"{stats.TotalDistanceNm:F0} nm";

            PilotRank = stats.Rank;
            (NextRank, RankProgressPct) = ComputeRankProgress(stats.TotalHoursBlock, stats.Rank);

            HasLandings = landings.Count > 0;

            RecentLandings.Clear();
            foreach (var l in landings) RecentLandings.Add(l);

            LastLanding = landings.FirstOrDefault();
            if (LastLanding != null)
            {
                const double circumference = 2 * Math.PI * 65; // r=65 matches SVG
                LastLandingRingOffset  = circumference * (1.0 - LastLanding.LandingScore / 100.0);
                LastLandingGradeLabel  = GradeLabel(LastLanding.LandingScore);
                LastLandingGradeClass  = GradeClass(LastLanding.LandingScore);
            }
        }
        finally
        {
            IsLoading = false;
            DataRefreshed?.Invoke();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    public static string GradeLabel(int score) => score switch
    {
        >= 90 => "Grade A",
        >= 75 => "Grade B",
        >= 60 => "Grade C",
        >= 40 => "Grade D",
        _     => "Grade F"
    };

    public static string GradeClass(int score) => score switch
    {
        >= 90 => "grade-a",
        >= 75 => "grade-b",
        >= 60 => "grade-c",
        >= 40 => "grade-d",
        _     => "grade-f"
    };

    public static string GradeChipClass(int score) => score switch
    {
        >= 90 => "chip-green",
        >= 75 => "chip-blue",
        >= 60 => "chip-yellow",
        >= 40 => "chip-orange",
        _     => "chip-red"
    };

    public static string GradeLetter(int score) => score switch
    {
        >= 90 => "A",
        >= 75 => "B",
        >= 60 => "C",
        >= 40 => "D",
        _     => "F"
    };

    public static string VsColor(double vsFpm) => vsFpm switch
    {
        > -250 => "var(--green)",
        > -400 => "var(--accent)",
        _      => "var(--red)"
    };

    private static (string nextRank, double pct) ComputeRankProgress(double hours, string rank) => rank switch
    {
        "Student Pilot"        => ("First Officer",         Math.Min(hours / 25.0, 1.0) * 100),
        "First Officer"        => ("Senior First Officer",  Math.Min((hours - 25) / 75.0, 1.0) * 100),
        "Senior First Officer" => ("Captain",               Math.Min((hours - 100) / 400.0, 1.0) * 100),
        "Captain"              => ("Senior Captain",        Math.Min((hours - 500) / 500.0, 1.0) * 100),
        _                      => ("Top Rank",              100.0)
    };
}
