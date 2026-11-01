using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Services;
using AviatesAirTracker.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Windows.UI;

namespace AviatesAirTracker.Pages;

/// <summary>
/// Pre-formatted display row for the Recent Flights list. Built in code-behind rather than
/// bound directly to <see cref="Models.FlightRecord"/> so the DataTemplate needs no
/// StringFormat/converter (classic WinUI {Binding} supports neither without a Page.Resources
/// entry, which risks the same XamlCompiler crash documented in DashboardPage.xaml's header
/// comment). Plain mutable properties, not an init-only positional record: XamlTypeInfo.g.cs
/// (generated because this type flows through an ItemsControl's ItemsSource) assigns each
/// property via a plain setter, which an init accessor does not satisfy (CS8852).
/// </summary>
public sealed class FlightDisplayRow
{
    public string Route { get; set; } = "";
    public string AircraftType { get; set; } = "";
    public string PilotName { get; set; } = "";
    public string DateText { get; set; } = "";

    public FlightDisplayRow(string route, string aircraftType, string pilotName, string dateText)
    {
        Route = route;
        AircraftType = aircraftType;
        PilotName = pilotName;
        DateText = dateText;
    }
}

/// <summary>
/// Pre-formatted display row for one monthly-activity bar. Same reasoning as
/// <see cref="FlightDisplayRow"/>: <see cref="PilotStatsLogic.MonthlyActivityPoint"/> is a
/// <c>readonly record struct</c> with init-only positional properties, which fails CS8852 in
/// XamlTypeInfo.g.cs the same way an init-only <c>record</c> class would if bound directly to
/// an ItemsControl's ItemsSource. Height/color are precomputed here too, since classic
/// {Binding} has no converter without a Page.Resources entry (the same XamlCompiler-crash risk
/// documented in DashboardPage.xaml's header comment).
/// </summary>
public sealed class MonthlyActivityBarRow
{
    public string Label { get; set; } = "";
    public int FlightCount { get; set; }
    public double BarHeight { get; set; }
    public Brush BarColor { get; set; } = new SolidColorBrush(Color.FromArgb(0x00, 0x00, 0x00, 0x00));

    public MonthlyActivityBarRow(string label, int flightCount, double barHeight, Brush barColor)
    {
        Label = label;
        FlightCount = flightCount;
        BarHeight = barHeight;
        BarColor = barColor;
    }
}

/// <summary>
/// Pre-formatted display row for one friend's recent-flight card in the Dashboard's activity
/// feed. Same reasoning as <see cref="FlightDisplayRow"/>: a plain mutable class, not bound
/// directly to <see cref="AviatesAirTracker.Core.Backend.RemoteFriendActivity"/>, so the relative
/// time / initials / route text are computed once here instead of needing a converter.
/// </summary>
public sealed class FriendActivityRow
{
    public string Initials { get; set; } = "";
    public string PilotName { get; set; } = "";
    public string RouteText { get; set; } = "";
    public string ScoreText { get; set; } = "";
    public string TimeAgoText { get; set; } = "";

    public FriendActivityRow(string initials, string pilotName, string routeText, string scoreText, string timeAgoText)
    {
        Initials = initials;
        PilotName = pilotName;
        RouteText = routeText;
        ScoreText = scoreText;
        TimeAgoText = timeAgoText;
    }
}

/// <summary>
/// Pre-formatted display row for one entry in the Dashboard's "Recent Landings" table (replaces
/// the old static "Your Assignments" rows). Reuses PilotHubPage's grade→color mapping
/// (ScoreColor/ScoreColorBg, made internal for exactly this kind of cross-page reuse) so a given
/// grade never renders a different color on Dashboard than it does on Pilot Hub/Logbook.
/// </summary>
public sealed class RecentLandingRow
{
    public string Airport { get; set; } = "";
    public string DateText { get; set; } = "";
    public string GradeText { get; set; } = "";
    public string DetailsText { get; set; } = "";
    public string ScoreText { get; set; } = "";
    public Brush IconFg { get; set; } = PilotHubPage.GradeRed;
    public Brush IconBg { get; set; } = PilotHubPage.GradeRedBg;
}

public sealed partial class DashboardPage : Page
{
    // Enter/Space on a focused Border-as-button runs its Tapped handler (see KeyboardActivation).
    private void OnActivateKey(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e) =>
        AviatesAirTracker.Platform.KeyboardActivation.Handle(this, sender, e);

    // Matches the fixed Height="48" bar-area Grid cell in DashboardPage.xaml's activity-chart
    // ItemTemplate; app.css's tallest static chart-bar values top out around here too.
    private const double MaxBarHeight = 48.0;

    // app.css's ".chart-bar { min-height: 4px; }": a zero-flight month must still render a
    // visible sliver, not a literal 0px bar that looks like a rendering bug.
    private const double MinBarHeight = 4.0;

    private static readonly Brush ActiveBarBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x4D, 0x9B, 0xEC));

    // Dark-theme-appropriate stand-in for app.css's ".chart-bar.inactive { background: #E2E8F0; }";
    // that light gray was tuned for a light surface and would look like a rendering glitch on
    // this native page's dark cards, so this uses the same muted slate already used elsewhere on
    // this page (e.g. the team-avatar gradient's dark stop) instead of porting the literal hex.
    private static readonly Brush InactiveBarBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x29, 0x3B));

    private readonly DashboardViewModel _viewModel;

    public ObservableCollection<FlightDisplayRow> RecentFlightRows { get; } = [];
    public ObservableCollection<MonthlyActivityBarRow> MonthlyActivityRows { get; } = [];
    public ObservableCollection<FriendActivityRow> FriendActivityRows { get; } = [];
    public ObservableCollection<RecentLandingRow> RecentLandingRows { get; } = [];

    public DashboardPage()
    {
        InitializeComponent();

        _viewModel = App.GetService<DashboardViewModel>();
        DataContext = _viewModel;
        FlightsList.ItemsSource = RecentFlightRows;
        ActivityChart.ItemsSource = MonthlyActivityRows;
        FriendActivityList.ItemsSource = FriendActivityRows;
        RecentLandingsList.ItemsSource = RecentLandingRows;

        // wwwroot ships as loose Content next to the exe (BlazorHostServer.cs resolves it the
        // same way), not as a packaged ms-appx:// asset, so the hero photo is loaded from an
        // absolute file path rather than a XAML-resolvable URI.
        var heroPhotoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "wwwroot", "images", "hero.webp");
        if (System.IO.File.Exists(heroPhotoPath))
        {
            HeroPhotoBrush.ImageSource = new BitmapImage(new Uri(heroPhotoPath));
        }

        Loaded += async (_, _) =>
        {
            await _viewModel.RefreshAsync();
            RebuildRecentFlightRows();
            RebuildMonthlyActivityRows();
            RebuildFriendActivityRows();
            RebuildRecentLandingRows();
            RankProgressText.Text = $"{_viewModel.RankProgress:F0}%";
            RankRingArc.Data = BuildRingArc(centerX: 50, centerY: 50, radius: 45, percent: _viewModel.RankProgress);
        };
    }

    private void RebuildFriendActivityRows()
    {
        FriendActivityRows.Clear();
        foreach (var a in _viewModel.FriendActivity)
        {
            var initials = GetInitials(a.FriendName);
            var route = string.IsNullOrWhiteSpace(a.DepartureICAO) || string.IsNullOrWhiteSpace(a.ArrivalICAO)
                ? (string.IsNullOrWhiteSpace(a.AircraftType) ? "Flight logged" : a.AircraftType)
                : $"{a.DepartureICAO} → {a.ArrivalICAO}" + (string.IsNullOrWhiteSpace(a.AircraftType) ? "" : $" · {a.AircraftType}");

            FriendActivityRows.Add(new FriendActivityRow(
                initials,
                string.IsNullOrWhiteSpace(a.FriendName) ? "Pilot" : a.FriendName,
                route,
                a.LandingScore > 0 ? $"Score {a.LandingScore:F0}" : "",
                TimeAgo(a.SubmittedAt)));
        }

        // Friends' activity is a genuinely reachable empty state (no friends added yet, or
        // friends with no flights logged), unlike Recent Flights' own no-longer-applicable
        // "always present" comment used to claim for this section before it was real data.
        var isEmpty = FriendActivityRows.Count == 0;
        EmptyFriendActivityText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        FriendActivityList.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RebuildRecentLandingRows()
    {
        RecentLandingRows.Clear();
        foreach (var l in _viewModel.RecentLandings)
        {
            var grade = PilotHubViewModel.GradeClass(l.LandingScore);
            RecentLandingRows.Add(new RecentLandingRow
            {
                Airport = string.IsNullOrWhiteSpace(l.AirportICAO) ? "—" : l.AirportICAO,
                DateText = l.Timestamp.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
                GradeText = PilotHubViewModel.GradeLabel(l.LandingScore).ToUpperInvariant(),
                DetailsText = $"V/S {l.VerticalSpeedFPM:F0} fpm · Pitch {l.TouchdownPitchDeg:F1}° · Crosswind {Math.Abs(l.CrosswindComponent):F0} kts",
                ScoreText = l.LandingScore.ToString(CultureInfo.InvariantCulture),
                IconFg = PilotHubPage.ScoreColor(l.LandingScore),
                IconBg = PilotHubPage.ScoreColorBg(l.LandingScore),
            });
        }

        var isEmpty = RecentLandingRows.Count == 0;
        EmptyLandingsText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        RecentLandingsBorder.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string GetInitials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        return $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant();
    }

    private static string TimeAgo(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1) return "Just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        return utc.ToString("MMM d", CultureInfo.InvariantCulture);
    }

    private void RebuildRecentFlightRows()
    {
        RecentFlightRows.Clear();
        foreach (var f in _viewModel.RecentFlights)
        {
            // A flight recorded while MSFS wasn't running (SimConnectStub's fallback mode) has
            // no real ICAO/aircraft/pilot data to report -- rendering "→" with nothing around it
            // reads as a broken card, not an honestly-sparse one, so blank fields get named
            // explicitly instead.
            var departure = string.IsNullOrWhiteSpace(f.DepartureICAO) ? "—" : f.DepartureICAO;
            var arrival = string.IsNullOrWhiteSpace(f.ArrivalICAO) ? "—" : f.ArrivalICAO;
            var aircraft = string.IsNullOrWhiteSpace(f.AircraftType) ? "Aircraft unknown" : f.AircraftType;
            var pilot = string.IsNullOrWhiteSpace(f.PilotName) ? "Unknown pilot" : f.PilotName;

            RecentFlightRows.Add(new FlightDisplayRow(
                $"{departure} → {arrival}",
                aircraft,
                pilot,
                f.CreatedAt.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)));
        }

        // Recent Flights is a genuinely reachable empty state for a new pilot, unlike
        // Assignments/Operations Team, which are always-present static content.
        var isEmpty = RecentFlightRows.Count == 0;
        EmptyFlightsText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        FlightsList.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RebuildMonthlyActivityRows()
    {
        MonthlyActivityRows.Clear();

        var points = _viewModel.MonthlyActivity;
        var maxCount = points.Count > 0 ? points.Max(p => p.FlightCount) : 0;

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var height = maxCount > 0
                ? Math.Max(MinBarHeight, point.FlightCount / (double)maxCount * MaxBarHeight)
                : MinBarHeight;

            // Dashboard.razor's static markup highlights exactly one bar (the rightmost) in the
            // accent color and leaves the rest muted; with real data the rightmost bar is always
            // the current month, so that's the one carried forward as "active" here.
            var isCurrentMonth = i == points.Count - 1;
            MonthlyActivityRows.Add(new MonthlyActivityBarRow(
                point.Label, point.FlightCount, height, isCurrentMonth ? ActiveBarBrush : InactiveBarBrush));
        }
    }

    /// <summary>
    /// Builds a Path.Data arc sweeping clockwise from 12 o'clock, covering
    /// <paramref name="percent"/>/100 of the circle; the WinUI equivalent of app.css's
    /// ".pilot-ring svg { transform: rotate(-90deg); }" + stroke-dashoffset trick, which rotates
    /// an SVG circle's default 3-o'clock start point to 12 o'clock and reveals it clockwise.
    /// Verified sample points: 0% -&gt; degenerate arc at 12 o'clock (nothing drawn, correct: no
    /// progress to show); 25% -&gt; endpoint at 3 o'clock; 50% -&gt; endpoint at 6 o'clock; 75% -&gt;
    /// endpoint at 9 o'clock (IsLargeArc true, since the swept angle exceeds 180deg). Percent is
    /// clamped just short of 100 because at exactly 100% (360deg) the start and end points both
    /// land on 12 o'clock and coincide, which makes a single ArcSegment degenerate to an
    /// invisible zero-length arc instead of a visually-complete ring; the sub-degree gap this
    /// leaves is not visible at this stroke width.
    ///
    /// internal (not private) because PilotHubPage.xaml.cs's Last Landing Analysis score ring
    /// (Phase 3 Task 3) reuses this exact method with its own radius/stroke-width rather than
    /// duplicating the arc math -- same assembly, same Pages namespace, no other blocker.
    /// </summary>
    internal static PathGeometry? BuildRingArc(double centerX, double centerY, double radius, double percent)
    {
        var clampedPercent = Math.Clamp(percent, 0.0, 99.9);
        if (clampedPercent <= 0.0) return null;

        var angle = clampedPercent / 100.0 * 360.0;
        var startPoint = new Windows.Foundation.Point(centerX, centerY - radius);
        var radians = (angle - 90) * Math.PI / 180.0;
        var endPoint = new Windows.Foundation.Point(
            centerX + radius * Math.Cos(radians),
            centerY + radius * Math.Sin(radians));

        var figure = new PathFigure
        {
            StartPoint = startPoint,
            Segments =
            {
                new ArcSegment
                {
                    Point = endPoint,
                    Size = new Windows.Foundation.Size(radius, radius),
                    IsLargeArc = angle > 180,
                    SweepDirection = SweepDirection.Clockwise,
                },
            },
        };

        return new PathGeometry { Figures = { figure } };
    }

    /// <summary>
    /// The hero card's "View Flights" CTA -- the first native-to-native navigation in this
    /// migration (Phase 3 Task 5). <c>this.Frame</c> is populated by the standard WinUI/UWP
    /// Frame navigation infrastructure: when <c>ShellWindow</c>'s <c>NativeHost</c> (a
    /// <c>Frame</c>) navigates to a page type, the Frame sets that page instance's own
    /// <c>Frame</c> property to itself before/as it becomes the Frame's Content, exactly the
    /// same way it would for any UWP/WinUI Frame-hosted Page -- this is documented framework
    /// behavior, not something ShellWindow implements itself. Navigating via
    /// <c>this.Frame.Navigate</c> therefore sends the SAME NativeHost Frame that is currently
    /// showing this page on to FlightsPage, which is exactly what's wanted here (as opposed to,
    /// say, a static back-reference to ShellWindow, which would also work but is a strictly
    /// worse pattern than the standard Frame-navigation idiom every other Frame-hosted Page in
    /// the ecosystem already uses).
    /// </summary>
    private void OnViewFlightsTapped(object sender, TappedRoutedEventArgs e)
    {
        if (this.Frame == null)
        {
            // Would mean the standard Frame-hosting behavior described above didn't hold for
            // this page instance -- logged rather than silently no-op'd so a real regression
            // here is observable in Serilog instead of just "the button does nothing."
            Log.Warning("[Dashboard] View Flights tapped but this.Frame is null; cannot navigate to FlightsPage");
            return;
        }

        this.Frame.Navigate(typeof(FlightsPage));
    }

    /// <summary>
    /// "See All Team" under Friends' Recent Activity -- was completely inert (no Tapped handler
    /// at all). Same this.Frame.Navigate pattern as OnViewFlightsTapped above, but also passes
    /// MessagesPage.FriendsTabParameter so it lands directly on the Friends tab instead of the
    /// default Inbox.
    /// </summary>
    private void OnSeeAllTeamTapped(object sender, TappedRoutedEventArgs e)
    {
        if (this.Frame == null)
        {
            Log.Warning("[Dashboard] See All Team tapped but this.Frame is null; cannot navigate to MessagesPage");
            return;
        }

        this.Frame.Navigate(typeof(MessagesPage), MessagesPage.FriendsTabParameter);
    }
}
