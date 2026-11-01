using AviatesAirTracker.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.Globalization;

namespace AviatesAirTracker.Pages;

public sealed partial class LogbookPage : Page
{
    // Enter/Space on a focused Border-as-button runs its Tapped handler (see KeyboardActivation).
    private void OnActivateKey(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e) =>
        AviatesAirTracker.Platform.KeyboardActivation.Handle(this, sender, e);

    private readonly LogbookViewModel _viewModel;

    // Reuses PilotHubPage's own LandingDisplayRow (same namespace) rather than a near-duplicate
    // type -- both pages format the exact same LandingResult shape into the exact same table row.
    public ObservableCollection<LandingDisplayRow> LandingRows { get; } = [];

    public LogbookPage()
    {
        InitializeComponent();

        _viewModel = App.GetService<LogbookViewModel>();
        DataContext = _viewModel;
        LandingList.ItemsSource = LandingRows;

        Loaded += async (_, _) =>
        {
            await _viewModel.RefreshAsync();

            TotalLandingsText.Text = _viewModel.TotalLandingsText;
            AvgScoreText.Text = _viewModel.AvgScoreText;
            BestScoreText.Text = _viewModel.BestScoreText;

            RebuildLandingRows();
        };
    }

    private void OnLandingRowTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LandingDisplayRow row })
            Frame?.Navigate(typeof(LandingAnalysisPage), row.LandingId);
    }

    private void RebuildLandingRows()
    {
        LandingRows.Clear();

        foreach (var l in _viewModel.Landings)
        {
            LandingRows.Add(new LandingDisplayRow
            {
                AirportLine = l.AirportICAO,
                DateText = l.Timestamp.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
                RunwayText = l.RunwayIdentifier,
                VsText = $"{l.VerticalSpeedFPM.ToString("F0", CultureInfo.InvariantCulture)} fpm",
                VsColor = PilotHubPage.VsColor(l.VerticalSpeedFPM),
                PitchText = $"{l.TouchdownPitchDeg.ToString("F1", CultureInfo.InvariantCulture)}°",
                CrosswindText = $"{Math.Abs(l.CrosswindComponent).ToString("F0", CultureInfo.InvariantCulture)} kts",
                ScoreText = l.LandingScore.ToString(CultureInfo.InvariantCulture),
                ScoreColor = PilotHubPage.ScoreColor(l.LandingScore),
                GradeLetter = PilotHubViewModel.GradeLetter(l.LandingScore),
                GradeChipColor = PilotHubPage.ScoreColor(l.LandingScore),
                GradeChipBackground = PilotHubPage.ScoreColorBg(l.LandingScore),
                ShowReviewBadge = l.RequiresReview ? Visibility.Visible : Visibility.Collapsed,
                LandingId = l.Id.ToString(),
            });
        }

        var isEmpty = LandingRows.Count == 0;
        EmptyText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        TableContent.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }
}
