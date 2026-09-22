using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace AviatesAirTracker.ViewModels;

/// <summary>
/// Full, uncapped landing history for the Logbook page -- the destination "View logbook" (Pilot
/// Hub's Landing History table) and "View all" (Pilot Hub's Last Landing Analysis card) both
/// navigate to now that a real page exists for them. PilotHubViewModel.RecentLandings caps at
/// 10 for its own dashboard-card use; this intentionally does not cap.
/// </summary>
public partial class LogbookViewModel : ObservableObject
{
    private readonly ILandingRepository _landingRepo;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasLandings;
    [ObservableProperty] private string _totalLandingsText = "0";
    [ObservableProperty] private string _avgScoreText = "—";
    [ObservableProperty] private string _bestScoreText = "—";

    public ObservableCollection<LandingResult> Landings { get; } = [];

    public LogbookViewModel(ILandingRepository landingRepo)
    {
        _landingRepo = landingRepo;
    }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var all = (await _landingRepo.GetAllAsync())
                .OrderByDescending(l => l.Timestamp)
                .ToList();

            Landings.Clear();
            foreach (var l in all) Landings.Add(l);

            // Held-for-review landings still show in the table above (with a review badge --
            // see LogbookPage.xaml) but don't count toward these stats until reviewed, same
            // exclusion PilotStatsService.ComputeAsync applies.
            var counted = all.Where(l => !l.RequiresReview).ToList();

            HasLandings = all.Count > 0;
            TotalLandingsText = counted.Count.ToString();
            AvgScoreText = counted.Count > 0 ? $"{counted.Average(l => l.LandingScore):F0}" : "—";
            BestScoreText = counted.Count > 0 ? counted.Max(l => l.LandingScore).ToString() : "—";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
