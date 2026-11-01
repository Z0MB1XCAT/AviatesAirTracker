using AviatesAirTracker.Controls;
using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Models;
using AviatesAirTracker.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Serilog;
using System.Globalization;
using Windows.Foundation;
using Windows.UI;
using static AviatesAirTracker.Pages.UiKit;

namespace AviatesAirTracker.Pages;

/// <summary>
/// The full debrief of a landing: the assessment's rating and findings, the approach profile
/// against a 3° path with the 1,000 and 500 ft gates, every check against its limit, the speed,
/// sink, attitude and configuration traces, where on the runway it touched down, and how it
/// compares with the pilot's other landings. Goes further than the touchdown pop-up and the EFB
/// (which show the score, factors and findings); both read the same LandingReport.
/// </summary>
public sealed partial class LandingAnalysisPage : Page
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly LandingAnalysisViewModel _vm;
    private readonly Dictionary<Guid, Border> _listItems = [];
    private Guid? _requested;
    private Guid? _selected;

    public LandingAnalysisPage()
    {
        InitializeComponent();
        _vm = App.GetService<LandingAnalysisViewModel>();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string s && Guid.TryParse(s, out var id)) _requested = id;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.LoadAsync();
            BuildList();

            if (_vm.Landings.Count == 0)
            {
                EmptyState.Visibility = Visibility.Visible;
                LandingCountText.Text = "Every touchdown, debriefed.";
                return;
            }

            LandingCountText.Text = _vm.Landings.Count == 1 ? "1 landing recorded" : $"{_vm.Landings.Count} landings recorded";
            var first = _requested is { } r && _vm.Landings.Any(l => l.Id == r) ? r : _vm.Landings[0].Id;
            await SelectAsync(first);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[LandingAnalysis] Load failed");
        }
    }

    // ── Landing list ────────────────────────────────────────────

    private void BuildList()
    {
        LandingListHost.Children.Clear();
        _listItems.Clear();

        foreach (var l in _vm.Landings)
        {
            var color = Hex(l.LandingGradeColor);
            var row = Columns(12, Px(42), Star(), Auto);
            row.Children.Add(new Border
            {
                Width = 42, Height = 42, CornerRadius = new CornerRadius(12),
                Background = Brush(color, 0x1F),
                Child = new TextBlock
                {
                    Text = l.LandingScore.ToString(Inv), FontFamily = Display, FontSize = 16,
                    Foreground = new SolidColorBrush(color),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            });
            var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(l.RunwayIdentifier) ? Airport(l) : $"{Airport(l)}  {l.RunwayIdentifier}",
                FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Text,
            });
            text.Children.Add(new TextBlock
            {
                Text = $"{l.Timestamp.ToLocalTime().ToString("d MMM yyyy, HH:mm", Inv)}   {Math.Abs(l.VerticalSpeedFPM):F0} fpm",
                FontSize = 11.5, Foreground = Muted,
            });
            row.Children.Add(text.At(1));
            if (l.RequiresReview)
                row.Children.Add(Pill("Review", RedColor).At(2));

            var item = new Border
            {
                Padding = new Thickness(10, 8, 12, 8),
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                Background = new SolidColorBrush(Colors.Transparent),
                Child = row,
            };
            var id = l.Id;
            Activatable(item, () => _ = SelectAsync(id),
                $"{Airport(l)} {l.RunwayIdentifier}, {l.Timestamp.ToLocalTime():d MMMM yyyy}, score {l.LandingScore}", effects: false);
            item.PointerEntered += (_, _) => { if (_selected != id) item.Background = Brush(Colors.White, 0x08); };
            item.PointerExited += (_, _) => { if (_selected != id) item.Background = new SolidColorBrush(Colors.Transparent); };

            _listItems[id] = item;
            LandingListHost.Children.Add(item);
        }
    }

    private static string Airport(LandingResult l) => string.IsNullOrWhiteSpace(l.AirportICAO) ? "Unknown airport" : l.AirportICAO;

    private async Task SelectAsync(Guid id)
    {
        _selected = id;
        foreach (var (key, item) in _listItems)
        {
            var on = key == id;
            item.Background = on ? Brush(AccentColor, 0x1A) : new SolidColorBrush(Colors.Transparent);
            item.BorderBrush = on ? Brush(AccentColor, 0x40) : new SolidColorBrush(Colors.Transparent);
        }

        var report = await _vm.SelectAsync(id);
        if (report == null || _selected != id) return;
        BuildDetail(report, _vm.Selected!);
    }

    // ── Detail ──────────────────────────────────────────────────

    private void BuildDetail(LandingReport rep, LandingResult r)
    {
        DetailHost.Children.Clear();
        EmptyState.Visibility = Visibility.Collapsed;

        DetailHost.Children.Add(BuildHeader(rep, r));
        DetailHost.Children.Add(BuildRating(rep.Assessment));

        if (rep.Trace.Count > 1)
        {
            DetailHost.Children.Add(BuildProfile(rep));
        }
        else
        {
            DetailHost.Children.Add(Card(Body(
                "There's no approach recording for this landing, so the profile and traces can't be drawn. " +
                "Landings synced from another PC, or flown before recording was added, only keep the touchdown figures.", Muted)));
        }

        DetailHost.Children.Add(BuildChecks(rep.Assessment));
        if (rep.Trace.Count > 1) DetailHost.Children.Add(BuildTraceGrid(rep));
        if (rep.TouchdownDistanceFt is not null) DetailHost.Children.Add(BuildRunway(rep, r));
        DetailHost.Children.Add(BuildScoreAndComparison(rep));
    }

    private UIElement BuildHeader(LandingReport rep, LandingResult r)
    {
        var grid = Columns(24, Star(), Auto);

        var left = new StackPanel { Spacing = 6 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        title.Children.Add(Heading(rep.AirportIcao, 30));
        title.Children.Add(new TextBlock
        {
            Text = rep.Runway, FontFamily = Display, FontSize = 18, Foreground = Muted,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 4),
        });
        left.Children.Add(title);

        var facts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        void Fact(string? s) { if (!string.IsNullOrWhiteSpace(s)) facts.Children.Add(Body(s, Muted, 13)); }
        Fact(rep.TimestampUtc.ToString("d MMM yyyy, HH:mm'Z'", Inv));
        Fact(rep.FlightNumber);
        Fact(rep.AircraftType);
        Fact(rep.Route.Replace("→", "to"));
        left.Children.Add(facts);
        grid.Children.Add(left);

        var gradeColor = Hex(rep.GradeColor);
        var score = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Spacing = 0 };
        score.Children.Add(new TextBlock
        {
            Text = rep.Score.ToString(Inv), FontFamily = Display, FontSize = 46, CharacterSpacing = -40,
            Foreground = new SolidColorBrush(gradeColor), HorizontalAlignment = HorizontalAlignment.Right,
        });
        score.Children.Add(new TextBlock
        {
            Text = $"{Sentence(rep.Grade)}, {rep.ButterRating.ToLowerInvariant()}",
            FontSize = 12.5, Foreground = Secondary, HorizontalAlignment = HorizontalAlignment.Right,
        });
        AutomationProperties.SetName(score, $"Score {rep.Score}, {rep.Grade}");
        grid.Children.Add(score.At(1));
        return grid;
    }

    private static UIElement BuildRating(LandingAssessment a)
    {
        var color = Hex(a.RatingColor);
        var grid = Columns(20, Px(4), Star(), Auto);
        grid.Children.Add(new Border { Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(2) });

        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = a.Rating, FontFamily = Display, FontSize = 22, Foreground = new SolidColorBrush(color) });
        text.Children.Add(Body(a.Summary, Secondary, 13.5));
        grid.Children.Add(text.At(1));

        var passed = a.Checks.Count(c => c.Status == CheckStatus.Pass);
        var counts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 22, VerticalAlignment = VerticalAlignment.Center };
        counts.Children.Add(Stat(a.CriticalCount.ToString(Inv), "critical", new SolidColorBrush(a.CriticalCount > 0 ? RedColor : MutedColor), 22));
        counts.Children.Add(Stat(a.CautionCount.ToString(Inv), "caution", new SolidColorBrush(a.CautionCount > 0 ? AmberColor : MutedColor), 22));
        counts.Children.Add(Stat(passed.ToString(Inv), "passed", new SolidColorBrush(GreenColor), 22));
        grid.Children.Add(counts.At(2));

        var card = Card(grid);
        card.BorderBrush = Brush(color, 0x40);
        AutomationProperties.SetName(card, $"Rating {a.Rating}. {a.Summary}");
        return card;
    }

    // The hero: height above the runway against distance to touchdown, with the 3° path.
    private static UIElement BuildProfile(LandingReport rep)
    {
        var before = rep.Trace.Where(p => p.T <= 0).ToList();
        var hasPositions = before.Any(p => p.DistNm < -0.05);

        var chart = new TraceChart { Height = 270, YUnit = " ft" };
        var accent = AccentColor;
        if (hasPositions)
        {
            var minDist = before.Min(p => p.DistNm);
            chart.XRange = (minDist, 0.3);
            chart.XFormat = v => Math.Abs(v).ToString(Math.Abs(minDist) < 3 ? "0.0" : "0", Inv);
            chart.XUnit = " nm";
            chart.Series.Add(new ChartSeries("3° path", [new Point(minDist, -minDist * 6076.12 * Math.Tan(3 * Math.PI / 180)), new Point(0, 0)],
                MutedColor, 1.5, Dashed: true));
            chart.Series.Insert(0, new ChartSeries("Height", rep.Trace.Where(p => p.DistNm <= 0.3).Select(p => new Point(p.DistNm, p.AglFt)).ToList(), accent, 2.5));
            foreach (var g in rep.Gates.Where(g => g.Recorded))
                chart.Markers.Add(new ChartMarker(g.DistNm, $"{g.AglFt} ft", g.Stable ? GreenColor : g.AglFt == 500 ? RedColor : AmberColor));
            chart.Markers.Add(new ChartMarker(0, "Touchdown", TextColor));
        }
        else
        {
            chart.XRange = (before.Min(p => p.T), 2);
            chart.XUnit = " s";
            chart.Series.Add(new ChartSeries("Height", rep.Trace.Where(p => p.T <= 2).Select(p => new Point(p.T, p.AglFt)).ToList(), accent, 2.5));
            chart.Markers.Add(new ChartMarker(0, "Touchdown", TextColor));
        }
        chart.YRange = (0, Math.Max(500, Math.Ceiling(before.Max(p => p.AglFt) / 250) * 250));
        chart.SetDescription("Approach profile chart: height above the runway against distance to touchdown, with the 3 degree reference path and the 1,000 and 500 foot gates.");

        var a = rep.Assessment;
        var header = Columns(12, Star(), Auto);
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(Heading("Approach profile"));
        titles.Children.Add(Body(hasPositions
            ? "Height above the runway against distance to touchdown. The dashed line is a 3° path to where you touched down."
            : "Height above the runway in the seconds before touchdown (no positions were recorded for distance).", Muted, 12.5));
        header.Children.Add(titles);

        var figures = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
        if (a.GlidepathMaxDeviationDeg is { } dev)
            figures.Children.Add(Stat($"{Math.Abs(dev) / 0.35:F1} dot", dev > 0 ? "furthest above path" : "furthest below path", null, 18));
        if (a.GearDownAglFt is { } gear)
            figures.Children.Add(Stat($"{gear:N0} ft", "gear down by", null, 18));
        if (a.LandingFlapsAglFt is { } flaps)
            figures.Children.Add(Stat($"{flaps:N0} ft", "landing flap at", null, 18));
        header.Children.Add(figures.At(1));

        var stack = new StackPanel { Spacing = 14 };
        stack.Children.Add(header);
        stack.Children.Add(chart);
        return Card(stack);
    }

    private static UIElement BuildChecks(LandingAssessment a)
    {
        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(Heading("Checks"));
        stack.Children.Add(new Border { Height = 10 });

        var header = Columns(14, Px(22), Star(1.3), Star(1.2), Star(1.2));
        header.Padding = new Thickness(0, 0, 0, 8);
        header.Children.Add(Caption("Check").At(1));
        header.Children.Add(Caption("Measured").At(2));
        header.Children.Add(Caption("Target").At(3));
        stack.Children.Add(header);

        foreach (var group in a.Checks.GroupBy(c => c.Group))
        {
            stack.Children.Add(new TextBlock
            {
                Text = group.Key, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Accent,
                Margin = new Thickness(0, 14, 0, 4),
            });

            foreach (var c in group)
            {
                var (glyph, color, word) = c.Status switch
                {
                    CheckStatus.Critical => ("", RedColor, "Critical"),
                    CheckStatus.Caution => ("", AmberColor, "Caution"),
                    CheckStatus.Pass => ("", GreenColor, "Pass"),
                    _ => ("", MutedColor, "Not measured"),
                };

                var row = Columns(14, Px(22), Star(1.3), Star(1.2), Star(1.2));
                row.Padding = new Thickness(0, 9, 0, 9);
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                row.Children.Add(new Border
                {
                    BorderBrush = Divider, BorderThickness = new Thickness(0, 0, 0, 1),
                    Margin = new Thickness(0, 0, 0, -9),
                }.SpanAll(4, 2));
                row.Children.Add(Icon(glyph, color, 14).AtTop());
                row.Children.Add(new TextBlock { Text = c.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Text, TextWrapping = TextWrapping.Wrap }.At(1));
                row.Children.Add(new TextBlock { Text = c.Measured, FontSize = 13, Foreground = c.Status is CheckStatus.Pass or CheckStatus.NotMeasured ? Secondary : new SolidColorBrush(color), TextWrapping = TextWrapping.Wrap }.At(2));
                row.Children.Add(new TextBlock { Text = c.Target, FontSize = 12.5, Foreground = Muted, TextWrapping = TextWrapping.Wrap }.At(3));
                if (!string.IsNullOrEmpty(c.Advice))
                {
                    var advice = Body(c.Advice, Muted, 12.5);
                    advice.Margin = new Thickness(0, 4, 0, 0);
                    Grid.SetColumn(advice, 1);
                    Grid.SetRow(advice, 1);
                    Grid.SetColumnSpan(advice, 3);
                    row.Children.Add(advice);
                }
                AutomationProperties.SetName(row, $"{c.Name}: {word}. Measured {c.Measured}. Target {c.Target}. {c.Advice}");
                stack.Children.Add(row);
            }
        }
        return Card(stack);
    }

    private static UIElement BuildTraceGrid(LandingReport rep)
    {
        var pts = rep.Trace.Where(p => p.T <= 15).ToList();
        var minT = pts.Min(p => p.T);
        var markers = rep.Gates.Where(g => g.Recorded)
            .Select(g => (g, t: pts.Where(p => p.T < 0).FirstOrDefault(p => p.AglFt <= g.AglFt)?.T))
            .Where(x => x.t is not null)
            .Select(x => new ChartMarker(x.t!.Value, $"{x.g.AglFt} ft", MutedColor))
            .Append(new ChartMarker(0, "TD", TextColor))
            .ToList();

        TraceChart Chart(string description)
        {
            var c = new TraceChart { XRange = (minT, pts.Max(p => p.T)), XUnit = " s" };
            c.Markers.AddRange(markers);
            c.SetDescription(description);
            return c;
        }

        // Airspeed with the approach-speed window
        var speed = Chart("Airspeed in knots before and after touchdown, with the approach speed window shaded.");
        speed.YUnit = " kt";
        speed.Series.Add(new ChartSeries("IAS", pts.Select(p => new Point(p.T, p.IasKts)).ToList(), AccentColor));
        if (rep.TargetSpeedMinKts > 0)
            speed.Bands.Add(new ChartBand(rep.TargetSpeedMinKts, rep.TargetSpeedMaxKts, Color.FromArgb(0x26, GreenColor.R, GreenColor.G, GreenColor.B), "target"));

        // Vertical speed with the 1,000 fpm sink limit
        var sink = Chart("Vertical speed in feet per minute; the shaded area is a sink rate above 1,000 feet per minute.");
        sink.YUnit = " fpm";
        sink.Series.Add(new ChartSeries("V/S", pts.Select(p => new Point(p.T, p.VsFpm)).ToList(), AccentColor));
        var minVs = Math.Min(-1200, pts.Min(p => p.VsFpm) - 100);
        sink.YRange = (minVs, Math.Max(300, pts.Max(p => p.VsFpm) + 100));
        sink.Bands.Add(new ChartBand(minVs, -1000, Color.FromArgb(0x22, RedColor.R, RedColor.G, RedColor.B), "sink > 1,000 fpm"));

        // Attitude
        var attitude = Chart("Pitch and bank angle in degrees.");
        attitude.YUnit = "°";
        attitude.XFormat = v => v.ToString("0", Inv);
        attitude.Series.Add(new ChartSeries("Pitch", pts.Select(p => new Point(p.T, p.PitchDeg)).ToList(), AccentColor));
        attitude.Series.Add(new ChartSeries("Bank", pts.Select(p => new Point(p.T, p.BankDeg)).ToList(), AmberColor, 1.6));
        attitude.Bands.Add(new ChartBand(LandingReportBuilder.Limits.PitchHighCaution, 15, Color.FromArgb(0x1F, RedColor.R, RedColor.G, RedColor.B), "tail clearance"));
        attitude.YRange = (Math.Min(-12, pts.Min(p => Math.Min(p.PitchDeg, p.BankDeg)) - 2), Math.Max(12, pts.Max(p => Math.Max(p.PitchDeg, p.BankDeg)) + 2));

        // Configuration
        var config = Chart("Flap extension in percent, and landing gear (100 when down).");
        config.YUnit = "%";
        config.YRange = (-5, 110);
        config.Series.Add(new ChartSeries("Flaps", pts.Select(p => new Point(p.T, p.FlapsPct)).ToList(), AccentColor));
        config.Series.Add(new ChartSeries("Gear", pts.Select(p => new Point(p.T, p.GearDown ? 100 : 0)).ToList(), GreenColor, 1.6));
        if (rep.LandingFlapTargetPct > 0)
            config.Bands.Add(new ChartBand(rep.LandingFlapTargetPct, 105, Color.FromArgb(0x1F, GreenColor.R, GreenColor.G, GreenColor.B), "landing flap"));

        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(ChartCard("Airspeed", rep.TargetSpeedMinKts > 0 ? $"Shaded: {rep.TargetSpeedMinKts:F0}-{rep.TargetSpeedMaxKts:F0} kt approach window" : "Indicated airspeed", speed).At(0, 0));
        grid.Children.Add(ChartCard("Vertical speed", $"Touchdown {rep.VerticalSpeedFpm:F0} fpm, steepest below 1,000 ft {rep.MaxSinkBelow1000Fpm:F0} fpm", sink).At(1, 0));
        grid.Children.Add(ChartCard("Pitch and bank", $"Touchdown pitch {rep.PitchDeg:F1}°, bank {rep.BankDeg:F1}°", attitude).At(0, 1));
        grid.Children.Add(ChartCard("Configuration", "Flap position and landing gear", config).At(1, 1));
        return grid;
    }

    private static Border ChartCard(string title, string subtitle, TraceChart chart)
    {
        var stack = new StackPanel { Spacing = 10 };
        var t = new StackPanel { Spacing = 2 };
        t.Children.Add(Heading(title, 14.5));
        t.Children.Add(Body(subtitle, Muted, 12));
        stack.Children.Add(t);
        stack.Children.Add(chart);
        return Card(stack, 18);
    }

    // Plan view of the first part of the runway with the touchdown point on it.
    private static UIElement BuildRunway(LandingReport rep, LandingResult r)
    {
        var td = rep.TouchdownDistanceFt!.Value;
        var lengthFt = Math.Max(4000, Math.Ceiling((td + 600) / 1000) * 1000);
        var hasOffset = !string.IsNullOrWhiteSpace(r.RunwayIdentifier) && !r.RunwayIdentifier.Contains('?');
        const double halfWidthFt = 75;

        var canvas = new Canvas { Height = 150 };
        void Draw()
        {
            canvas.Children.Clear();
            double w = canvas.ActualWidth;
            if (w < 100) return;
            double left = 8, right = w - 8, top = 34, height = 70;
            double X(double ft) => left + ft / lengthFt * (right - left);
            double Y(double offsetFt) => top + height / 2 + offsetFt / halfWidthFt * (height / 2);

            canvas.Children.Add(Positioned(new Rectangle { Width = right - left, Height = height, RadiusX = 6, RadiusY = 6, Fill = new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x20, 0x2C)) }, left, top));

            // touchdown zone 500-3,000 ft
            canvas.Children.Add(Positioned(new Rectangle
            {
                Width = X(LandingReportBuilder.Limits.LongCautionFt) - X(LandingReportBuilder.Limits.ShortWarningFt),
                Height = height, Fill = Brush(GreenColor, 0x18),
            }, X(LandingReportBuilder.Limits.ShortWarningFt), top));

            // threshold bars and centreline
            for (int i = 0; i < 8; i++)
                canvas.Children.Add(Positioned(new Rectangle { Width = 12, Height = 4, Fill = Brush(Colors.White, 0x99) }, X(30), top + 8 + i * 7.5));
            for (double ft = 300; ft < lengthFt - 100; ft += 400)
                canvas.Children.Add(Positioned(new Rectangle { Width = Math.Max(4, X(ft + 200) - X(ft)), Height = 2, Fill = Brush(Colors.White, 0x66) }, X(ft), top + height / 2 - 1));
            // aiming point markers at 1,000 ft
            canvas.Children.Add(Positioned(new Rectangle { Width = X(1150) - X(1000), Height = 8, Fill = Brush(Colors.White, 0x80) }, X(1000), top + 12));
            canvas.Children.Add(Positioned(new Rectangle { Width = X(1150) - X(1000), Height = 8, Fill = Brush(Colors.White, 0x80) }, X(1000), top + height - 20));

            // distance scale
            for (double ft = 0; ft <= lengthFt; ft += 1000)
            {
                var label = new TextBlock { Text = ft == 0 ? "Threshold" : $"{ft:N0} ft", FontSize = 10.5, Foreground = Muted };
                canvas.Children.Add(Positioned(label, Math.Min(X(ft), right - 50), top + height + 8));
            }

            // touchdown point
            var color = Hex(rep.Assessment.Checks.FirstOrDefault(c => c.Name == "Touchdown point")?.Status switch
            {
                CheckStatus.Critical => "#EF4444",
                CheckStatus.Caution => "#F59E0B",
                _ => "#3EC7AC",
            });
            var offset = hasOffset ? Math.Clamp(r.CenterlineDeviationFt, -halfWidthFt + 6, halfWidthFt - 6) : 0;
            var cx = X(Math.Min(td, lengthFt));
            var cy = Y(offset);
            canvas.Children.Add(Positioned(new Ellipse { Width = 20, Height = 20, Fill = Brush(color, 0x40) }, cx - 10, cy - 10));
            canvas.Children.Add(Positioned(new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(color) }, cx - 5, cy - 5));
            var tag = new TextBlock { Text = $"{td:N0} ft{(rep.TouchdownDistanceEstimated ? " (est.)" : "")}", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(color) };
            canvas.Children.Add(Positioned(tag, Math.Min(cx - 20, right - 90), top - 22));
        }
        canvas.SizeChanged += (_, _) => Draw();
        AutomationProperties.SetName(canvas, $"Runway diagram: touchdown {td:N0} feet past the threshold" +
            (hasOffset ? $", {Math.Abs(r.CenterlineDeviationFt):F0} feet {(r.CenterlineDeviationFt > 0 ? "right" : "left")} of the centreline." : "."));

        var stack = new StackPanel { Spacing = 10 };
        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(Heading("Where you touched down"));
        header.Children.Add(Body(hasOffset
            ? $"Shaded: the touchdown zone, {LandingReportBuilder.Limits.ShortWarningFt:F0} to {LandingReportBuilder.Limits.LongCautionFt:N0} ft. Offset from the centreline is to scale."
            : $"Shaded: the touchdown zone, {LandingReportBuilder.Limits.ShortWarningFt:F0} to {LandingReportBuilder.Limits.LongCautionFt:N0} ft. Centreline offset isn't known for this runway, so the point is drawn on it.",
            Muted, 12.5));
        stack.Children.Add(header);
        stack.Children.Add(canvas);
        return Card(stack);
    }

    private static T Positioned<T>(T element, double left, double top) where T : UIElement
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        return element;
    }

    private UIElement BuildScoreAndComparison(LandingReport rep)
    {
        var grid = Columns(16, Star(1.2), Star());

        var factors = new StackPanel { Spacing = 12 };
        factors.Children.Add(Heading("Score breakdown"));
        foreach (var f in rep.Factors)
        {
            var row = Columns(12, Star(), Px(64));
            var left = new StackPanel { Spacing = 5 };
            left.Children.Add(new TextBlock { Text = f.Label, FontSize = 12.5, Foreground = Secondary });
            left.Children.Add(Bar(f.Pct / 100, Hex(f.Color)));
            row.Children.Add(left);
            row.Children.Add(new TextBlock
            {
                Text = $"{f.Score:F0} / {f.Max:F0}", FontSize = 12.5, Foreground = Text,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            }.At(1));
            factors.Children.Add(row);
        }
        grid.Children.Add(Card(factors));

        var compare = new StackPanel { Spacing = 16 };
        compare.Children.Add(Heading("Against your other landings"));
        if (rep.TotalLandings > 1)
        {
            var stats = new Grid { ColumnSpacing = 16, RowSpacing = 16 };
            stats.ColumnDefinitions.Add(new ColumnDefinition());
            stats.ColumnDefinitions.Add(new ColumnDefinition());
            stats.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            stats.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            stats.Children.Add(Stat($"{Ordinal(rep.RankAmongLandings)} of {rep.TotalLandings}", rep.IsPersonalBest ? "your best landing yet" : "ranked by score").At(0, 0));
            var diff = rep.Score - rep.AverageScore;
            stats.Children.Add(Stat(rep.AverageScore.ToString(Inv), diff == 0 ? "your average score" : $"your average ({(diff > 0 ? "+" : "")}{diff} here)").At(1, 0));
            stats.Children.Add(Stat($"{_vm.AverageVsFpm:F0} fpm", $"your average touchdown ({rep.VerticalSpeedFpm:F0} here)").At(0, 1));
            stats.Children.Add(Stat($"{rep.HeadwindKts:F0} / {rep.CrosswindKts:F0} kt", "head / crosswind").At(1, 1));
            compare.Children.Add(stats);
        }
        else
        {
            compare.Children.Add(Body("This is your first recorded landing. Fly a few more and this compares them.", Muted));
        }
        grid.Children.Add(Card(compare).At(1));
        return grid;
    }

    private static string Ordinal(int n) => (n % 100) switch
    {
        11 or 12 or 13 => $"{n}th",
        _ => (n % 10) switch { 1 => $"{n}st", 2 => $"{n}nd", 3 => $"{n}rd", _ => $"{n}th" },
    };

    private static string Sentence(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
}

internal static class LandingAnalysisLayout
{
    public static T SpanAll<T>(this T element, int columns, int rows) where T : FrameworkElement
    {
        Grid.SetColumnSpan(element, columns);
        Grid.SetRowSpan(element, rows);
        return element;
    }

    public static T AtTop<T>(this T element) where T : FrameworkElement
    {
        element.VerticalAlignment = VerticalAlignment.Top;
        element.Margin = new Thickness(0, 2, 0, 0);
        return element;
    }
}
