using AviatesAirTracker.Services;
using AviatesAirTracker.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
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

public sealed partial class DashboardPage : Page
{
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

    public DashboardPage()
    {
        InitializeComponent();

        _viewModel = App.GetService<DashboardViewModel>();
        DataContext = _viewModel;
        FlightsList.ItemsSource = RecentFlightRows;
        ActivityChart.ItemsSource = MonthlyActivityRows;

        Loaded += async (_, _) =>
        {
            await _viewModel.RefreshAsync();
            RebuildRecentFlightRows();
            RebuildMonthlyActivityRows();
            RankProgressText.Text = $"{_viewModel.RankProgress:F0}%";
            RankRingArc.Data = BuildRingArc(centerX: 50, centerY: 50, radius: 45, percent: _viewModel.RankProgress);
        };
    }

    private void RebuildRecentFlightRows()
    {
        RecentFlightRows.Clear();
        foreach (var f in _viewModel.RecentFlights)
        {
            RecentFlightRows.Add(new FlightDisplayRow(
                $"{f.DepartureICAO} → {f.ArrivalICAO}",
                f.AircraftType,
                f.PilotName,
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
    /// </summary>
    private static PathGeometry? BuildRingArc(double centerX, double centerY, double radius, double percent)
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
}
