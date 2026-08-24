using AviatesAirTracker.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace AviatesAirTracker.Pages;

public sealed partial class PilotHubPage : Page
{
    // Must match RankProgressTrack's fixed Width in PilotHubPage.xaml -- there is no
    // {Binding}/x:Bind path from a XAML Width attribute back into this const, so the two have
    // to be kept in sync by hand (same idiom as DashboardPage.xaml.cs's ring-radius constants
    // passed into BuildRingArc).
    private const double RankProgressTrackWidth = 600.0;

    private readonly PilotHubViewModel _viewModel;

    public PilotHubPage()
    {
        InitializeComponent();

        _viewModel = App.GetService<PilotHubViewModel>();
        DataContext = _viewModel;

        Loaded += async (_, _) =>
        {
            await _viewModel.RefreshAsync();

            RankSubText.Text = $"{_viewModel.RankProgressPct:F0}% progress toward {_viewModel.NextRank}";
            RankProgressFill.Width = RankProgressTrackWidth * (_viewModel.RankProgressPct / 100.0);
        };
    }
}
