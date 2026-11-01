using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Globalization;
using Windows.Foundation;
using Windows.UI;

namespace AviatesAirTracker.Controls;

public sealed record ChartSeries(string Name, IReadOnlyList<Point> Points, Color Color, double Thickness = 2, bool Dashed = false);
public sealed record ChartBand(double YFrom, double YTo, Color Color, string? Label = null);
public sealed record ChartMarker(double X, string Label, Color Color);

/// <summary>
/// A small line chart drawn straight onto a Canvas: series, horizontal limit bands, vertical
/// event markers, axis labels and a hover readout. The app has no charting library (OxyPlot
/// went with the WinUI migration) and these traces are a few hundred points, so a Polyline per
/// series is plenty. Redraws itself on resize.
/// </summary>
public sealed class TraceChart : Grid
{
    private static readonly SolidColorBrush GridBrush = new(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush AxisText = new(Color.FromArgb(0xFF, 0x64, 0x74, 0x8B));
    private static readonly SolidColorBrush ReadoutBrush = new(Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1));

    private const double LeftGutter = 44, BottomGutter = 22, TopGutter = 8, RightGutter = 8;

    private readonly Canvas _canvas = new();
    private readonly Line _hoverLine = new() { Stroke = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1, Visibility = Visibility.Collapsed };
    private readonly TextBlock _readout = new() { FontSize = 11, Foreground = ReadoutBrush, Visibility = Visibility.Collapsed };

    public List<ChartSeries> Series { get; } = [];
    public List<ChartBand> Bands { get; } = [];
    public List<ChartMarker> Markers { get; } = [];
    public (double Min, double Max)? XRange { get; set; }
    public (double Min, double Max)? YRange { get; set; }
    public Func<double, string> XFormat { get; set; } = v => v.ToString("0", CultureInfo.InvariantCulture);
    public Func<double, string> YFormat { get; set; } = v => v.ToString("0", CultureInfo.InvariantCulture);
    public string XUnit { get; set; } = "";
    public string YUnit { get; set; } = "";

    public TraceChart()
    {
        Height = 190;
        Children.Add(_canvas);
        SizeChanged += (_, _) => Render();
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerExited += (_, _) => { _hoverLine.Visibility = Visibility.Collapsed; _readout.Visibility = Visibility.Collapsed; };
        _canvas.Background = new SolidColorBrush(Colors.Transparent); // hit-testable for the readout
    }

    private (double minX, double maxX, double minY, double maxY) Bounds()
    {
        var pts = Series.SelectMany(s => s.Points).ToList();
        double minX = XRange?.Min ?? (pts.Count > 0 ? pts.Min(p => p.X) : 0);
        double maxX = XRange?.Max ?? (pts.Count > 0 ? pts.Max(p => p.X) : 1);
        double minY, maxY;
        if (YRange is { } yr) (minY, maxY) = yr;
        else
        {
            var ys = pts.Select(p => p.Y).Concat(Bands.SelectMany(b => new[] { b.YFrom, b.YTo })).ToList();
            minY = ys.Count > 0 ? ys.Min() : 0;
            maxY = ys.Count > 0 ? ys.Max() : 1;
            var pad = Math.Max((maxY - minY) * 0.08, 1);
            minY -= pad; maxY += pad;
        }
        if (maxX - minX < 1e-6) maxX = minX + 1;
        if (maxY - minY < 1e-6) maxY = minY + 1;
        return (minX, maxX, minY, maxY);
    }

    public void Render()
    {
        _canvas.Children.Clear();
        double w = ActualWidth, h = ActualHeight;
        if (w < 60 || h < 60) return;

        var (minX, maxX, minY, maxY) = Bounds();
        double plotW = w - LeftGutter - RightGutter, plotH = h - TopGutter - BottomGutter;
        double X(double x) => LeftGutter + (x - minX) / (maxX - minX) * plotW;
        double Y(double y) => TopGutter + (1 - (y - minY) / (maxY - minY)) * plotH;

        // Bands (limits / target windows) under everything else.
        foreach (var b in Bands)
        {
            double y1 = Y(Math.Min(Math.Max(b.YTo, minY), maxY)), y2 = Y(Math.Max(Math.Min(b.YFrom, maxY), minY));
            var rect = new Rectangle { Width = plotW, Height = Math.Max(1, y2 - y1), Fill = new SolidColorBrush(b.Color) };
            Canvas.SetLeft(rect, LeftGutter); Canvas.SetTop(rect, y1);
            _canvas.Children.Add(rect);
            if (b.Label != null)
                AddText(b.Label, LeftGutter + 6, y1 + 2, new SolidColorBrush(Color.FromArgb(0xCC, b.Color.R, b.Color.G, b.Color.B)), 10);
        }

        // Horizontal grid + Y labels.
        foreach (var v in Ticks(minY, maxY, 4))
        {
            var y = Y(v);
            _canvas.Children.Add(new Line { X1 = LeftGutter, X2 = LeftGutter + plotW, Y1 = y, Y2 = y, Stroke = GridBrush, StrokeThickness = 1 });
            AddText(YFormat(v), 0, y - 7, AxisText, 10.5, LeftGutter - 6, TextAlignment.Right);
        }
        // X labels.
        foreach (var v in Ticks(minX, maxX, 6))
            AddText(XFormat(v) + XUnit, X(v) - 30, TopGutter + plotH + 5, AxisText, 10.5, 60, TextAlignment.Center);

        // Markers.
        foreach (var m in Markers.Where(m => m.X >= minX && m.X <= maxX))
        {
            var x = X(m.X);
            _canvas.Children.Add(new Line
            {
                X1 = x, X2 = x, Y1 = TopGutter, Y2 = TopGutter + plotH,
                Stroke = new SolidColorBrush(Color.FromArgb(0x99, m.Color.R, m.Color.G, m.Color.B)),
                StrokeThickness = 1, StrokeDashArray = [3, 3],
            });
            AddText(m.Label, x + 4, TopGutter, new SolidColorBrush(m.Color), 10);
        }

        // Series.
        foreach (var s in Series)
        {
            var line = new Polyline
            {
                Stroke = new SolidColorBrush(s.Color),
                StrokeThickness = s.Thickness,
                StrokeLineJoin = PenLineJoin.Round,
            };
            if (s.Dashed) line.StrokeDashArray = [5, 4];
            foreach (var p in s.Points.Where(p => p.X >= minX && p.X <= maxX))
                line.Points.Add(new Point(X(p.X), Y(Math.Clamp(p.Y, minY, maxY))));
            _canvas.Children.Add(line);
        }

        _canvas.Children.Add(_hoverLine);
        _canvas.Children.Add(_readout);
    }

    private void OnPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var primary = Series.FirstOrDefault(s => s.Points.Count > 0);
        if (primary == null) return;
        var (minX, maxX, _, _) = Bounds();
        double plotW = ActualWidth - LeftGutter - RightGutter;
        var pos = e.GetCurrentPoint(_canvas).Position;
        if (pos.X < LeftGutter || pos.X > LeftGutter + plotW) return;

        var x = minX + (pos.X - LeftGutter) / plotW * (maxX - minX);
        var parts = Series.Where(s => s.Points.Count > 0 && !s.Dashed)
            .Select(s => $"{s.Name} {YFormat(s.Points.MinBy(p => Math.Abs(p.X - x)).Y)}{YUnit}");
        _readout.Text = $"{XFormat(x)}{XUnit}   {string.Join("   ", parts)}";
        _hoverLine.X1 = _hoverLine.X2 = pos.X;
        _hoverLine.Y1 = TopGutter;
        _hoverLine.Y2 = ActualHeight - BottomGutter;
        Canvas.SetLeft(_readout, Math.Min(pos.X + 8, Math.Max(LeftGutter, ActualWidth - 260)));
        Canvas.SetTop(_readout, TopGutter + 14);
        _hoverLine.Visibility = Visibility.Visible;
        _readout.Visibility = Visibility.Visible;
    }

    private void AddText(string text, double left, double top, Brush brush, double size,
        double? width = null, TextAlignment align = TextAlignment.Left)
    {
        var tb = new TextBlock { Text = text, FontSize = size, Foreground = brush, TextAlignment = align };
        if (width is { } wd) tb.Width = wd;
        Canvas.SetLeft(tb, left); Canvas.SetTop(tb, top);
        _canvas.Children.Add(tb);
    }

    /// <summary>"Nice" round tick values spanning [min, max].</summary>
    internal static IEnumerable<double> Ticks(double min, double max, int target)
    {
        var raw = (max - min) / Math.Max(1, target);
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * mag).First(s => s >= raw);
        for (var v = Math.Ceiling(min / step) * step; v <= max + step * 1e-6; v += step)
            yield return Math.Round(v, 6);
    }

    /// <summary>Screen readers get a one-line summary instead of a picture.</summary>
    public void SetDescription(string description) => AutomationProperties.SetName(this, description);
}
