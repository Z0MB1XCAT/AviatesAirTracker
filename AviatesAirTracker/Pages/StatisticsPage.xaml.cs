using AviatesAirTracker.Controls;
using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Models;
using AviatesAirTracker.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Serilog;
using System.Globalization;
using Windows.Foundation;
using static AviatesAirTracker.Pages.UiKit;

namespace AviatesAirTracker.Pages;

/// <summary>
/// Career statistics from the local logbook (recorded flights plus history synced from the
/// server): totals and rank progress, hours by month, the landing-score trend, a map of every
/// recorded track, aircraft and route breakdowns, landing grades and personal records.
/// </summary>
public sealed partial class StatisticsPage : Page
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private NativeMapRenderer? _renderer;

    public StatisticsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => { _renderer?.Dispose(); _renderer = null; };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var flights = await App.GetService<IFlightRepository>().GetAllAsync();
            var landings = await App.GetService<ILandingRepository>().GetAllAsync();
            var stats = CareerStatsLogic.Compute(flights, landings, DateTime.UtcNow);
            var pilot = await App.GetService<PilotStatsService>().ComputeAsync();

            ContentHost.Children.Clear();
            if (stats.Flights == 0 && stats.Landings == 0)
            {
                EmptyState.Visibility = Visibility.Visible;
                return;
            }

            ContentHost.Children.Add(BuildTotals(stats, pilot));
            ContentHost.Children.Add(TwoUp(BuildMonths(stats), BuildScoreTrend(stats), 1.15, 1));
            ContentHost.Children.Add(BuildMap(flights, stats));
            ContentHost.Children.Add(TwoUp(BuildAircraft(stats), BuildRoutes(stats)));
            ContentHost.Children.Add(TwoUp(BuildGrades(stats), BuildRecords(stats)));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Statistics] Load failed");
        }
    }

    private static Grid TwoUp(UIElement left, UIElement right, double leftWeight = 1, double rightWeight = 1)
    {
        var g = Columns(20, Star(leftWeight), Star(rightWeight));
        g.Children.Add(left);
        Grid.SetColumn((FrameworkElement)right, 1);
        g.Children.Add(right);
        return g;
    }

    private static Border Section(string title, string? subtitle, UIElement content)
    {
        var stack = new StackPanel { Spacing = 14 };
        var head = new StackPanel { Spacing = 2 };
        head.Children.Add(Heading(title));
        if (subtitle != null) head.Children.Add(Body(subtitle, Muted, 12.5));
        stack.Children.Add(head);
        stack.Children.Add(content);
        return Card(stack);
    }

    // ── Totals and rank ─────────────────────────────────────────

    private static UIElement BuildTotals(CareerStats s, PilotStatistics pilot)
    {
        var grid = Columns(20, Star(), Star(), Star(), Star(), Star(1.6));
        grid.Children.Add(Stat(Hours(s.CreditedHours), s.CreditedHours > s.BlockHours + 0.05 || s.CreditedHours < s.BlockHours - 0.05
            ? $"hours credited ({Hours(s.BlockHours)} block)" : "block hours", null, 30));
        grid.Children.Add(Stat(s.Flights.ToString("N0", Inv), s.Flights == 1 ? "flight" : "flights", null, 30).At(1));
        grid.Children.Add(Stat(s.DistanceNm.ToString("N0", Inv), "nautical miles", null, 30).At(2));
        grid.Children.Add(Stat(s.Landings.ToString("N0", Inv), s.Landings > 0 ? $"landings, avg score {s.AverageScore:F0}" : "landings", null, 30).At(3));

        var (next, pct) = PilotStatsLogic.ComputeRankProgress(pilot.TotalHoursBlock, pilot.Rank);
        var rank = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        rank.Children.Add(new TextBlock { Text = pilot.Rank, FontFamily = Display, FontSize = 20, Foreground = Accent });
        rank.Children.Add(Bar(pct / 100, AccentColor, 7));
        rank.Children.Add(Body(next == "Top Rank"
            ? "Top rank reached."
            : $"{pct:F0}% of the way to {next}", Muted, 12.5));
        grid.Children.Add(rank.At(4));

        var card = Card(grid, 24);
        AutomationProperties.SetName(card, $"{Hours(s.CreditedHours)} hours, {s.Flights} flights, {s.DistanceNm:N0} nautical miles, {s.Landings} landings. Rank {pilot.Rank}.");
        return card;
    }

    private static string Hours(double h) => h >= 100 ? h.ToString("N0", Inv) : h.ToString("0.0", Inv);

    // ── Hours by month ──────────────────────────────────────────

    private static UIElement BuildMonths(CareerStats s)
    {
        var max = Math.Max(1, s.Months.Max(m => m.Hours));
        var bars = new Grid { Height = 190, ColumnSpacing = 8 };
        for (int i = 0; i < s.Months.Count; i++)
        {
            var m = s.Months[i];
            bars.ColumnDefinitions.Add(new ColumnDefinition());
            var col = new Grid { RowSpacing = 6 };
            col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var isCurrent = i == s.Months.Count - 1;
            var barHeight = m.Hours <= 0 ? 3 : Math.Max(4, m.Hours / max * 140);
            col.Children.Add(new TextBlock
            {
                Text = m.Hours > 0 ? Hours(m.Hours) : "",
                FontSize = 11, Foreground = Secondary, HorizontalAlignment = HorizontalAlignment.Center,
            });
            col.Children.Add(new Border
            {
                Height = barHeight, VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(5, 5, 2, 2),
                Background = m.Hours > 0 ? (isCurrent ? Accent : Brush(AccentColor, 0x80)) : Brush(Microsoft.UI.Colors.White, 0x10),
            }.At(0, 1));
            col.Children.Add(new TextBlock
            {
                Text = m.Label, FontSize = 11, Foreground = isCurrent ? Text : Muted, HorizontalAlignment = HorizontalAlignment.Center,
            }.At(0, 2));
            AutomationProperties.SetName(col, $"{m.Month:MMMM yyyy}: {Hours(m.Hours)} hours, {m.Flights} flights");
            bars.Children.Add(col.At(i));
        }

        var thisMonth = s.Months[^1];
        return Section("Hours by month",
            $"Block hours over the last year. This month: {Hours(thisMonth.Hours)} h from {thisMonth.Flights} flight{(thisMonth.Flights == 1 ? "" : "s")}.",
            bars);
    }

    // ── Landing score trend ─────────────────────────────────────

    private static UIElement BuildScoreTrend(CareerStats s)
    {
        if (s.ScoreTrend.Count < 2)
            return Section("Landing scores", null, Body("Two or more landings are needed to show a trend.", Muted));

        var chart = new TraceChart { Height = 190, YRange = (0, 100), XRange = (1, s.ScoreTrend.Count) };
        chart.XFormat = v => v.ToString("0", Inv);
        chart.Series.Add(new ChartSeries("Average", s.ScoreTrend.Select((p, i) => new Point(i + 1, p.RollingAverage)).ToList(), AccentColor, 2.5));
        chart.Series.Add(new ChartSeries("Score", s.ScoreTrend.Select((p, i) => new Point(i + 1, p.Score)).ToList(), MutedColor, 1.2));
        chart.Bands.Add(new ChartBand(90, 100, Brush(GreenColor, 0x1A).Color, "excellent"));
        chart.SetDescription($"Landing scores for your last {s.ScoreTrend.Count} landings with a five-landing rolling average.");

        var first = s.ScoreTrend.Take(5).Average(p => p.Score);
        var last = s.ScoreTrend.TakeLast(5).Average(p => p.Score);
        var delta = last - first;
        var trend = Math.Abs(delta) < 2 ? "Holding steady" : delta > 0 ? $"Up {delta:F0} points" : $"Down {-delta:F0} points";
        return Section("Landing scores", $"Your last {s.ScoreTrend.Count} landings, with a five-landing average. {trend} since the first five.", chart);
    }

    // ── Map ─────────────────────────────────────────────────────

    private UIElement BuildMap(IReadOnlyList<FlightRecord> flights, CareerStats s)
    {
        var tracks = flights
            .Where(f => f.Status == FlightStatus.Completed && f.FlightPath.Count > 1)
            .Select(f => Decimate(f.FlightPath.Select(p => (p.Latitude, p.Longitude)).ToList(), 160))
            .ToList();

        // An airport's position is where recorded tracks start or end.
        var airports = new Dictionary<string, (double Lat, double Lon)>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in flights.Where(f => f.FlightPath.Count > 1))
        {
            if (f.DepartureICAO.Length == 4 && !airports.ContainsKey(f.DepartureICAO))
                airports[f.DepartureICAO] = (f.FlightPath[0].Latitude, f.FlightPath[0].Longitude);
            if (f.ArrivalICAO.Length == 4 && !airports.ContainsKey(f.ArrivalICAO))
                airports[f.ArrivalICAO] = (f.FlightPath[^1].Latitude, f.FlightPath[^1].Longitude);
        }

        var summary = Columns(28, Auto, Auto, Auto, Star());
        summary.Children.Add(Stat(s.Airports.ToString(Inv), "airports", null, 22));
        summary.Children.Add(Stat(s.Countries.ToString(Inv), s.Countries == 1 ? "country" : "countries", null, 22).At(1));
        summary.Children.Add(Stat(tracks.Count.ToString(Inv), "tracks recorded", null, 22).At(2));

        var stack = new StackPanel { Spacing = 14 };
        var head = new StackPanel { Spacing = 2 };
        head.Children.Add(Heading("Where you've flown"));
        head.Children.Add(Body("Every track recorded on this PC. History synced from your account has the airports but not the track itself.", Muted, 12.5));
        stack.Children.Add(head);
        stack.Children.Add(summary);

        if (tracks.Count == 0)
        {
            stack.Children.Add(Body("No recorded tracks yet. Fly a tracked flight and it will be drawn here.", Muted));
            return Card(stack);
        }

        _renderer = new NativeMapRenderer();
        var map = new Mapsui.UI.WinUI.MapControl { Map = _renderer.Map, Height = 380 };
        var host = new Border { CornerRadius = new CornerRadius(14), Child = map };
        AutomationProperties.SetName(host, $"Map of {tracks.Count} recorded flight tracks across {s.Airports} airports.");
        var first = tracks[0][0];
        _renderer.Init(first.Lat, first.Lon);
        var points = airports.Select(a => (a.Value.Lat, a.Value.Lon, a.Key.ToUpperInvariant())).ToList();
        var shown = false;
        map.SizeChanged += (_, e) =>
        {
            if (shown || e.NewSize.Width < 50 || _renderer == null) return;
            shown = true;
            _renderer.ShowRouteNetwork(tracks, points);
        };
        stack.Children.Add(host);
        return Card(stack);
    }

    private static IReadOnlyList<(double Lat, double Lon)> Decimate(List<(double Lat, double Lon)> pts, int max)
    {
        if (pts.Count <= max) return pts;
        var step = (double)(pts.Count - 1) / (max - 1);
        return Enumerable.Range(0, max).Select(i => pts[(int)Math.Round(i * step)]).ToList();
    }

    // ── Aircraft and routes ─────────────────────────────────────

    private static UIElement BuildAircraft(CareerStats s)
    {
        var stack = new StackPanel { Spacing = 12 };
        var max = Math.Max(0.1, s.Aircraft.Count > 0 ? s.Aircraft.Max(a => a.Hours) : 0);
        foreach (var a in s.Aircraft.Take(8))
        {
            var row = Columns(12, Px(70), Star(), Px(96));
            row.Children.Add(new TextBlock { Text = a.Type, FontFamily = Display, FontSize = 13, Foreground = Text, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(Bar(a.Hours / max, AccentColor, 8).At(1));
            row.Children.Add(new TextBlock
            {
                Text = $"{Hours(a.Hours)} h, {a.Flights}", FontSize = 12, Foreground = Secondary,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            }.At(2));
            AutomationProperties.SetName(row, $"{a.Type}: {Hours(a.Hours)} hours over {a.Flights} flights");
            stack.Children.Add(row);
        }
        if (s.Aircraft.Count == 0) stack.Children.Add(Body("No aircraft flown yet.", Muted));
        return Section("Aircraft", "Block hours and flights by type.", stack);
    }

    private static UIElement BuildRoutes(CareerStats s)
    {
        var stack = new StackPanel { Spacing = 0 };
        var header = Columns(12, Star(1.4), Px(70), Star());
        header.Padding = new Thickness(0, 0, 0, 8);
        header.Children.Add(Caption("Route"));
        header.Children.Add(Caption("Flights").At(1));
        header.Children.Add(Caption("Last flown").At(2));
        stack.Children.Add(header);

        foreach (var r in s.Routes)
        {
            var row = Columns(12, Star(1.4), Px(70), Star());
            row.Padding = new Thickness(0, 9, 0, 9);
            row.BorderBrush = Divider;
            row.BorderThickness = new Thickness(0, 1, 0, 0);
            row.Children.Add(new TextBlock { Text = $"{r.From}  to  {r.To}", FontFamily = Display, FontSize = 13, Foreground = Text });
            row.Children.Add(new TextBlock { Text = r.Flights.ToString(Inv), FontSize = 13, Foreground = Secondary }.At(1));
            row.Children.Add(new TextBlock { Text = r.LastFlown == default ? "" : r.LastFlown.ToLocalTime().ToString("d MMM yyyy", Inv), FontSize = 12.5, Foreground = Muted }.At(2));
            AutomationProperties.SetName(row, $"{r.From} to {r.To}, {r.Flights} flights");
            stack.Children.Add(row);
        }
        if (s.Routes.Count == 0) stack.Children.Add(Body("Routes appear once a flight has both airports recorded.", Muted));
        return Section("Most-flown routes", null, stack);
    }

    // ── Grades and records ──────────────────────────────────────

    private static UIElement BuildGrades(CareerStats s)
    {
        var stack = new StackPanel { Spacing = 12 };
        var total = Math.Max(1, s.Grades.Values.Sum());
        foreach (var (grade, color) in new[] { ("A", GreenColor), ("B", AccentColor), ("C", AmberColor), ("D", RedColor), ("F", RedColor) })
        {
            s.Grades.TryGetValue(grade, out var n);
            var row = Columns(12, Px(24), Star(), Px(70));
            row.Children.Add(new TextBlock { Text = grade, FontFamily = Display, FontSize = 14, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(Bar((double)n / total, color, 8).At(1));
            row.Children.Add(new TextBlock
            {
                Text = $"{n} ({100.0 * n / total:F0}%)", FontSize = 12, Foreground = Secondary,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            }.At(2));
            AutomationProperties.SetName(row, $"Grade {grade}: {n} landings");
            stack.Children.Add(row);
        }
        return Section("Landing grades",
            s.Landings > 0 ? $"Average touchdown {s.AverageTouchdownFpm:F0} fpm; smoothest {s.SmoothestTouchdownFpm:F0} fpm." : null,
            stack);
    }

    private static UIElement BuildRecords(CareerStats s)
    {
        var grid = new Grid { ColumnSpacing = 20, RowSpacing = 18 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var items = new List<StackPanel>();

        if (s.Longest is { } longest)
            items.Add(Stat($"{longest.BlockTime.TotalHours:0.0} h", $"longest flight, {Route(longest)}", null, 20));
        if (s.Farthest is { } far)
            items.Add(Stat($"{far.ActualDistanceNm:N0} nm", $"farthest flight, {Route(far)}", null, 20));
        if (s.BestLanding is { } best)
            items.Add(Stat(best.LandingScore.ToString(Inv), $"best landing, {best.AirportICAO} {best.RunwayIdentifier}".Trim(), new SolidColorBrush(GreenColor), 20));
        if (s.OnTimePercent is { } onTime)
            items.Add(Stat($"{onTime:F0}%", "on-time departures (within 15 min)", null, 20));
        items.Add(Stat(s.EventFlights.ToString(Inv), "event flights", null, 20));
        items.Add(Stat(s.CharterFlights.ToString(Inv), "charter flights", null, 20));
        if (s.FuelLbs > 0)
            items.Add(Stat($"{s.FuelLbs / 2204.62:N1} t", "fuel burned", null, 20));

        for (int i = 0; i < items.Count; i++)
        {
            if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(items[i].At(i % 2, i / 2));
        }
        return Section("Records", null, grid);
    }

    private static string Route(FlightRecord f) =>
        string.IsNullOrEmpty(f.DepartureICAO) ? (string.IsNullOrEmpty(f.FlightNumber) ? "unknown route" : f.FlightNumber)
        : $"{f.DepartureICAO} to {f.ArrivalICAO}";
}
