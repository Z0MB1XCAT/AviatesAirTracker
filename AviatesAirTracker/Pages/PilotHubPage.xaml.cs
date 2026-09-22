using AviatesAirTracker.Platform;
using AviatesAirTracker.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Serilog;
using System.Collections.ObjectModel;
using System.Globalization;
using Windows.UI;

namespace AviatesAirTracker.Pages;

/// <summary>
/// Pre-formatted display row for the Landing History table. Same reasoning as
/// DashboardPage.xaml.cs's FlightDisplayRow: a plain mutable class with a parameterless
/// constructor and { get; set; } properties, not an init-only record, since XamlTypeInfo.g.cs
/// assigns each property via a plain setter for anything bound through an ItemsControl's
/// ItemsSource (an init accessor fails that with CS8852).
/// </summary>
public sealed class LandingDisplayRow
{
    public string AirportLine { get; set; } = "";
    public string DateText { get; set; } = "";
    public string RunwayText { get; set; } = "";
    public string VsText { get; set; } = "";
    public Brush VsColor { get; set; } = PilotHubPage.GradeRed;
    public string PitchText { get; set; } = "";
    public string CrosswindText { get; set; } = "";
    public string ScoreText { get; set; } = "";
    public Brush ScoreColor { get; set; } = PilotHubPage.GradeRed;
    public string GradeLetter { get; set; } = "";
    public Brush GradeChipColor { get; set; } = PilotHubPage.GradeRed;
    public Brush GradeChipBackground { get; set; } = PilotHubPage.GradeRedBg;

    // See LandingComplianceChecker -- set whenever LandingResult.RequiresReview is true.
    public Visibility ShowReviewBadge { get; set; } = Visibility.Collapsed;
}

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

    // internal (not private): LandingDisplayRow's default property values reference these
    // below, and the Landing History table (Task 4) reuses the exact same four brushes for its
    // score-text and grade-chip colors so every widget on this page agrees on one grade→color
    // mapping (see GradeColorFor's comment for why the table deliberately does not preserve
    // PilotHubViewModel.GradeChipClass's separate D-orange/F-red split).
    internal static readonly Brush GradeGreen = new SolidColorBrush(Color.FromArgb(0xFF, 0x3E, 0xC7, 0xAC));
    internal static readonly Brush GradeBlue = new SolidColorBrush(Color.FromArgb(0xFF, 0x4D, 0x9B, 0xEC));
    internal static readonly Brush GradeAmber = new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B));
    internal static readonly Brush GradeRed = new SolidColorBrush(Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));

    // Translucent tints for the Landing History grade chip's Border.Background, same ~12% alpha
    // convention this page already uses for its stat-icon swatches (e.g. "#1F4D9BEC" above).
    internal static readonly Brush GradeGreenBg = new SolidColorBrush(Color.FromArgb(0x1F, 0x3E, 0xC7, 0xAC));
    internal static readonly Brush GradeBlueBg = new SolidColorBrush(Color.FromArgb(0x1F, 0x4D, 0x9B, 0xEC));
    internal static readonly Brush GradeAmberBg = new SolidColorBrush(Color.FromArgb(0x1F, 0xF5, 0x9E, 0x0B));
    internal static readonly Brush GradeRedBg = new SolidColorBrush(Color.FromArgb(0x1F, 0xEF, 0x44, 0x44));

    private readonly PilotHubViewModel _viewModel;

    public ObservableCollection<LandingDisplayRow> LandingRows { get; } = [];

    public PilotHubPage()
    {
        InitializeComponent();

        InteractiveEffects.Attach(BookingActivateBorder, BookingCancelBorder, BookingActivatedRow);

        _viewModel = App.GetService<PilotHubViewModel>();
        DataContext = _viewModel;
        LandingHistoryList.ItemsSource = LandingRows;

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        // Subscribed before the first RefreshAsync so that call's own DataRefreshed firing
        // renders the initial state -- and every later refresh (e.g. PilotHubViewModel's
        // FlightCompleted subscription firing while this page is already open) re-renders the
        // rank card and landing lists too, instead of only ever reflecting whatever was current
        // the moment this page first loaded.
        _viewModel.DataRefreshed += OnViewModelDataRefreshed;
        await _viewModel.RefreshAsync();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel.DataRefreshed -= OnViewModelDataRefreshed;
    }

    private void OnViewModelDataRefreshed()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            RankSubText.Text = $"{_viewModel.RankProgressPct:F0}% progress toward {_viewModel.NextRank}";
            RankProgressFill.Width = RankProgressTrackWidth * (_viewModel.RankProgressPct / 100.0);

            RebuildLastLandingCard();
            RebuildLandingRows();
            RebuildBookingCard();
        });
    }

    // See BookingService.cs's MaxBookings = 1 and PilotHubViewModel.RefreshBookingAsync's own
    // doc comment. Four states: no booking (card collapsed entirely), an unactivated confirmed
    // booking (route/callsign/aircraft/scheduled time + Activate/Cancel), an activated one (same
    // details, but Activate/Cancel are replaced by a plain "Reopen SimBrief" row -- activating is
    // one-way, see PilotHubViewModel.ActivateCurrentBookingAsync's own comment), or one that just
    // expired (BookingService.ExpireIfStaleAsync already cancelled it server-side -- this only
    // renders the after-the-fact notice, there's nothing left to activate or cancel).
    private void RebuildBookingCard()
    {
        var booking = _viewModel.CurrentBooking;
        if (booking == null)
        {
            BookingCardBorder.Visibility = Visibility.Collapsed;
            return;
        }

        BookingCardBorder.Visibility = Visibility.Visible;
        BookingRouteText.Text = $"{booking.OriginIata} → {booking.DestIata}";
        BookingActivateErrorText.Visibility = Visibility.Collapsed; // reset; OnActivateBookingTapped re-shows it on failure

        if (_viewModel.BookingExpired)
        {
            BookingExpiredChipBorder.Visibility = Visibility.Visible;
            BookingActivatedChipBorder.Visibility = Visibility.Collapsed;
            BookingDetailText.Text = $"{booking.Callsign} · {booking.AircraftType} — you didn't fly before the scheduled departure, so this booking was cancelled. You remain at your last location.";
            BookingActivateBorder.Visibility = Visibility.Collapsed;
            BookingCancelBorder.Visibility = Visibility.Collapsed;
            BookingActivatedRow.Visibility = Visibility.Collapsed;
        }
        else
        {
            BookingExpiredChipBorder.Visibility = Visibility.Collapsed;
            BookingDetailText.Text =
                $"{booking.Callsign} · {booking.AircraftType} — scheduled {booking.ScheduledDepUtc.ToString("MMM d, yyyy 'at' HH:mm 'UTC'", CultureInfo.InvariantCulture)}";

            var activated = booking.IsActivated;
            BookingActivatedChipBorder.Visibility = activated ? Visibility.Visible : Visibility.Collapsed;
            BookingActivateBorder.Visibility = activated ? Visibility.Collapsed : Visibility.Visible;
            BookingCancelBorder.Visibility = activated ? Visibility.Collapsed : Visibility.Visible;
            BookingActivatedRow.Visibility = activated ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async void OnActivateBookingTapped(object sender, TappedRoutedEventArgs e)
    {
        // Disabled immediately so a fast double-tap can't fire SimBrief's browser launch twice
        // or race two activate calls -- re-enabled by the next RebuildBookingCard either way.
        BookingActivateBorder.IsHitTestVisible = false;
        var error = await _viewModel.ActivateCurrentBookingAsync();
        BookingActivateBorder.IsHitTestVisible = true;
        RebuildBookingCard(); // resets BookingActivateErrorText.Visibility -- set it after, not before

        if (error != null)
        {
            Log.Warning("[PilotHubPage] Activate booking failed: {Error}", error);
            BookingActivateErrorText.Text = $"Activation failed: {error} SimBrief opened anyway; tap Activate again once this is fixed.";
            BookingActivateErrorText.Visibility = Visibility.Visible;
        }
    }

    private void OnReopenDispatchTapped(object sender, TappedRoutedEventArgs e) => _viewModel.ReopenDispatch();

    private void OnCancelBookingTapped(object sender, TappedRoutedEventArgs e)
    {
        _viewModel.CancelCurrentBooking();
        RebuildBookingCard();
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

    // Mirrors DashboardPage.xaml.cs's RebuildRecentFlightRows()/RebuildMonthlyActivityRows()
    // naming and shape: precompute one display row per landing, then toggle empty-state vs
    // content visibility, same pattern as RebuildLastLandingCard() above.
    private void RebuildLandingRows()
    {
        LandingRows.Clear();

        foreach (var l in _viewModel.RecentLandings)
        {
            LandingRows.Add(new LandingDisplayRow
            {
                AirportLine = l.AirportICAO,
                DateText = l.Timestamp.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
                RunwayText = l.RunwayIdentifier,
                VsText = $"{l.VerticalSpeedFPM.ToString("F0", CultureInfo.InvariantCulture)} fpm",
                VsColor = VsColor(l.VerticalSpeedFPM),
                PitchText = $"{l.TouchdownPitchDeg.ToString("F1", CultureInfo.InvariantCulture)}°",
                CrosswindText = $"{Math.Abs(l.CrosswindComponent).ToString("F0", CultureInfo.InvariantCulture)} kts",
                ScoreText = l.LandingScore.ToString(CultureInfo.InvariantCulture),
                ScoreColor = ScoreColor(l.LandingScore),
                GradeLetter = PilotHubViewModel.GradeLetter(l.LandingScore),
                GradeChipColor = ScoreColor(l.LandingScore),
                GradeChipBackground = ScoreColorBg(l.LandingScore),
                ShowReviewBadge = l.RequiresReview ? Visibility.Visible : Visibility.Collapsed,
            });
        }

        var isEmpty = LandingRows.Count == 0;
        LandingHistoryEmptyText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        LandingHistoryContent.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    // Written directly per the plan rather than calling PilotHubViewModel.ScoreColor()/
    // GradeChipClass(): both emit a "var(--yellow)"/"chip-yellow" for the 60-74 (grade C) band
    // that app.css never defines, so a grade-C landing's score text and chip both render with a
    // broken/unset color in the live Blazor app today -- even though the LAST-LANDING RING
    // (Task 3, GradeColor() above) already correctly uses amber for grade C. This is the bug fix:
    // amber for >= 60 instead of the undefined yellow.
    //
    // Deliberately reuses the exact same 4-tier green/blue/amber/red mapping as GradeColor()
    // above (Task 3's ring) for BOTH the score text and the grade chip, rather than preserving
    // GradeChipClass's separate chip-orange(D)/chip-red(F) split -- GradeChipClass has no bug of
    // its own to fix, but collapsing D into red here keeps the ring, the score text, and the
    // chip all agreeing on one color for a given grade (e.g. a Grade D landing is red
    // everywhere), which is the "should agree" goal the amber fix is meant to achieve.
    // internal (not private): LogbookPage.xaml.cs's full landing-history table reuses this
    // exact 4-tier mapping so the two pages' grade colors never drift apart from each other.
    internal static Brush ScoreColor(int score) => score switch
    {
        >= 90 => GradeGreen,
        >= 75 => GradeBlue,
        >= 60 => GradeAmber, // bug fix: PilotHubViewModel.ScoreColor() emits undefined var(--yellow) here
        _ => GradeRed,
    };

    // Translucent-background counterpart to ScoreColor(), used for the grade chip's Border
    // background so the tint always matches the chip's own foreground color.
    internal static Brush ScoreColorBg(int score) => score switch
    {
        >= 90 => GradeGreenBg,
        >= 75 => GradeBlueBg,
        >= 60 => GradeAmberBg,
        _ => GradeRedBg,
    };

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
    internal static Brush VsColor(double vsFpm) => vsFpm switch
    {
        > -250 => GradeGreen,
        > -400 => GradeBlue,
        _ => GradeRed,
    };

    // Shared by both "View all" (Last Landing Analysis card) and "View logbook" (Landing
    // History table) -- same this.Frame.Navigate pattern DashboardPage.xaml.cs's own
    // "View Flights" CTA uses.
    private void OnViewLogbookTapped(object sender, TappedRoutedEventArgs e)
    {
        if (this.Frame == null)
        {
            Log.Warning("[PilotHub] View logbook tapped but this.Frame is null; cannot navigate to LogbookPage");
            return;
        }

        this.Frame.Navigate(typeof(LogbookPage));
    }
}
