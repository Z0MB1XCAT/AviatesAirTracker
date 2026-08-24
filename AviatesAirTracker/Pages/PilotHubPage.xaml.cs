using AviatesAirTracker.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Globalization;
using Windows.UI;

namespace AviatesAirTracker.Pages;

public sealed partial class PilotHubPage : Page
{
    // Must match RankProgressTrack's fixed Width in PilotHubPage.xaml -- there is no
    // {Binding}/x:Bind path from a XAML Width attribute back into this const, so the two have
    // to be kept in sync by hand (same idiom as DashboardPage.xaml.cs's ring-radius constants
    // passed into BuildRingArc).
    private const double RankProgressTrackWidth = 600.0;

    // app.css's ".score-ring" svg: viewBox 160x160, r=65, stroke-width=14 -- a different circle
    // from Dashboard's own rank ring (r=45, stroke-width=8). The Grid hosting the ring in
    // PilotHubPage.xaml is sized 160x160 so these center coordinates line up with the svg's own
    // cx/cy=80 with no extra offset math.
    private const double LandingRingCenter = 80.0;
    private const double LandingRingRadius = 65.0;

    private static readonly Brush GradeGreen = new SolidColorBrush(Color.FromArgb(0xFF, 0x3E, 0xC7, 0xAC));
    private static readonly Brush GradeBlue = new SolidColorBrush(Color.FromArgb(0xFF, 0x4D, 0x9B, 0xEC));
    private static readonly Brush GradeAmber = new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B));
    private static readonly Brush GradeRed = new SolidColorBrush(Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));

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

            RebuildLastLandingCard();
        };
    }

    private void RebuildLastLandingCard()
    {
        var landing = _viewModel.LastLanding;
        var hasLanding = _viewModel.HasLandings && landing != null;

        LastLandingEmptyText.Visibility = hasLanding ? Visibility.Collapsed : Visibility.Visible;
        LastLandingContent.Visibility = hasLanding ? Visibility.Visible : Visibility.Collapsed;

        if (!hasLanding || landing == null) return;

        var gradeClass = PilotHubViewModel.GradeClass(landing.LandingScore);
        var ringColor = GradeColor(gradeClass);

        LandingRingArc.Stroke = ringColor;
        LandingRingArc.Data = DashboardPage.BuildRingArc(
            centerX: LandingRingCenter, centerY: LandingRingCenter, radius: LandingRingRadius,
            percent: landing.LandingScore);

        LandingScoreText.Text = landing.LandingScore.ToString(CultureInfo.InvariantCulture);
        LandingGradeText.Text = PilotHubViewModel.GradeLabel(landing.LandingScore);

        LandingVsText.Text = $"{landing.VerticalSpeedFPM.ToString("F0", CultureInfo.InvariantCulture)} fpm";
        LandingVsText.Foreground = VsColor(landing.VerticalSpeedFPM);

        LandingPitchText.Text = $"{landing.TouchdownPitchDeg.ToString("F1", CultureInfo.InvariantCulture)}°";

        LandingCrosswindText.Text =
            $"{Math.Abs(landing.CrosswindComponent).ToString("F0", CultureInfo.InvariantCulture)} kts";

        LandingRunwayText.Text = $"{landing.AirportICAO} {landing.RunwayIdentifier}";
    }

    // Written directly per the plan rather than calling PilotHubViewModel.GradeClass and
    // string-matching a CSS class name: GradeClass() gives us the grade string, this switches
    // on it into an actual native Brush. Grade F is the deliberate bug fix here -- app.css has
    // no ".score-ring-fill.grade-f" rule at all, so a real F-grade landing renders with an
    // invisible/unset stroke in the live Blazor app today (only the gray track shows). This
    // page maps grade-f to the same red used for grade-d instead of reproducing that gap.
    private static Brush GradeColor(string gradeClass) => gradeClass switch
    {
        "grade-a" => GradeGreen,
        "grade-b" => GradeBlue,
        "grade-c" => GradeAmber,
        "grade-d" => GradeRed,
        "grade-f" => GradeRed, // bug fix: CSS has no .grade-f rule, ring is invisible today
        _ => GradeRed,
    };

    // Reproduces PilotHubViewModel.VsColor's exact thresholds (> -250 green, > -400 accent
    // blue, else red) as a native Brush instead of a "var(--x)" CSS string.
    private static Brush VsColor(double vsFpm) => vsFpm switch
    {
        > -250 => GradeGreen,
        > -400 => GradeBlue,
        _ => GradeRed,
    };
}
