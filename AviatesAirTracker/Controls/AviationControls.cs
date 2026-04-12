using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AviatesAirTracker.Controls;

// ============================================================
// AVIATES AIR CUSTOM WPF CONTROLS
//
// Aviation-grade instrument controls drawn with pure WPF
// vector graphics for a high-end EFB aesthetic.
// ============================================================

// ============================================================
// COMPASS ROSE CONTROL
// Draws a circular compass with heading indicator
// ============================================================

public class CompassRose : Control
{
    public static readonly DependencyProperty HeadingProperty =
        DependencyProperty.Register(nameof(Heading), typeof(double), typeof(CompassRose),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty =
        DependencyProperty.Register(nameof(Track), typeof(double), typeof(CompassRose),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Heading { get => (double)GetValue(HeadingProperty); set => SetValue(HeadingProperty, value); }
    public double Track   { get => (double)GetValue(TrackProperty);   set => SetValue(TrackProperty, value);   }

    private static readonly Typeface _font = new(
        new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        double r = Math.Min(w, h) / 2 - 4;
        var center = new Point(w / 2, h / 2);

        // Outer ring
        dc.DrawEllipse(
            new SolidColorBrush(Color.FromRgb(14, 18, 32)),
            new Pen(new SolidColorBrush(Color.FromRgb(30, 38, 64)), 1.5),
            center, r, r);

        // Rotate the rose by -heading so "up" always points to current heading
        dc.PushTransform(new RotateTransform(-Heading, center.X, center.Y));

        // Cardinal directions and tick marks
        var cardinals = new[] { ("N", 0), ("E", 90), ("S", 180), ("W", 270) };
        var majorTick = new Pen(new SolidColorBrush(Color.FromRgb(136, 146, 170)), 1.5);
        var minorTick = new Pen(new SolidColorBrush(Color.FromRgb(74, 85, 104)), 1);

        for (int deg = 0; deg < 360; deg += 10)
        {
            double rad = deg * Math.PI / 180;
            double sinR = Math.Sin(rad), cosR = Math.Cos(rad);
            bool isCardinal  = deg % 90 == 0;
            bool isMajor     = deg % 30 == 0;
            double tickLen   = isCardinal ? 14 : isMajor ? 10 : 6;
            var pen          = isMajor ? majorTick : minorTick;

            var outer = new Point(center.X + sinR * r,         center.Y - cosR * r);
            var inner = new Point(center.X + sinR * (r - tickLen), center.Y - cosR * (r - tickLen));
            dc.DrawLine(pen, outer, inner);

            if (isCardinal)
            {
                string label = cardinals.First(c => c.Item2 == deg).Item1;
                var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, _font, 11,
                    new SolidColorBrush(Color.FromRgb(240, 244, 255)),
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);

                var tp = new Point(center.X + sinR * (r - 24) - text.Width / 2,
                                   center.Y - cosR * (r - 24) - text.Height / 2);
                dc.DrawText(text, tp);
            }
        }

        dc.Pop(); // End rose rotation

        // Heading indicator (fixed triangle at top)
        var hdgBrush = new SolidColorBrush(Color.FromRgb(61, 126, 238));
        var triangle = new PathGeometry(new[]
        {
            new PathFigure(new Point(center.X, center.Y - r + 2), new PathSegment[]
            {
                new LineSegment(new Point(center.X - 6, center.Y - r + 14), true),
                new LineSegment(new Point(center.X + 6, center.Y - r + 14), true)
            }, true)
        });
        dc.DrawGeometry(hdgBrush, null, triangle);

        // Center hub
        dc.DrawEllipse(
            new SolidColorBrush(Color.FromRgb(30, 38, 64)),
            new Pen(new SolidColorBrush(Color.FromRgb(61, 126, 238)), 1),
            center, 4, 4);

        // Heading readout
        var hdgText = new FormattedText(
            $"{Heading:000}°",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _font, 14,
            new SolidColorBrush(Color.FromRgb(61, 126, 238)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(hdgText, new Point(center.X - hdgText.Width / 2, center.Y + r - 26));
    }
}

// ============================================================
// VERTICAL SPEED INDICATOR — analog-style arc
// ============================================================

public class VerticalSpeedIndicator : Control
{
    public static readonly DependencyProperty VerticalSpeedProperty =
        DependencyProperty.Register(nameof(VerticalSpeed), typeof(double), typeof(VerticalSpeedIndicator),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double VerticalSpeed
    {
        get => (double)GetValue(VerticalSpeedProperty);
        set => SetValue(VerticalSpeedProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        double r = Math.Min(w, h) / 2 - 6;
        var center = new Point(w / 2, h / 2);

        // Background
        dc.DrawEllipse(
            new SolidColorBrush(Color.FromRgb(14, 18, 32)),
            new Pen(new SolidColorBrush(Color.FromRgb(30, 38, 64)), 1.5),
            center, r, r);

        // Scale: -2000 to +2000 fpm maps to -135° to +135°
        double clampedVS = Math.Clamp(VerticalSpeed, -2000, 2000);
        double angleDeg  = clampedVS / 2000.0 * 135.0;  // degrees from 12-o'clock
        double angleRad  = angleDeg * Math.PI / 180;

        // Needle
        var needleColor = VerticalSpeed switch
        {
            < -1200 => Color.FromRgb(239, 68, 68),
            > 3000  => Color.FromRgb(234, 179, 8),
            _       => Color.FromRgb(240, 244, 255)
        };

        double nx = center.X + Math.Sin(angleRad) * (r - 10);
        double ny = center.Y - Math.Cos(angleRad) * (r - 10);

        dc.DrawLine(
            new Pen(new SolidColorBrush(needleColor), 2.5),
            center, new Point(nx, ny));

        // Center cap
        dc.DrawEllipse(
            new SolidColorBrush(Color.FromRgb(61, 126, 238)), null,
            center, 5, 5);

        // VS readout
        var text = new FormattedText(
            $"{VerticalSpeed:+#;-#;0}",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            12,
            new SolidColorBrush(needleColor),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y + r - 22));
    }
}

// ============================================================
// AIRCRAFT ICON SYMBOL (for map overlay)
// ============================================================

public class AircraftSymbol : Shape
{
    public static readonly DependencyProperty HeadingProperty =
        DependencyProperty.Register(nameof(Heading), typeof(double), typeof(AircraftSymbol),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Heading
    {
        get => (double)GetValue(HeadingProperty);
        set => SetValue(HeadingProperty, value);
    }

    protected override Geometry DefiningGeometry
    {
        get
        {
            // Simple aircraft silhouette pointing "up" (0° = north)
            var geo = new PathGeometry();
            var fig = new PathFigure { StartPoint = new Point(0, -12), IsClosed = true };
            fig.Segments.Add(new LineSegment(new Point(3, 0), true));
            fig.Segments.Add(new LineSegment(new Point(8, 3), true));
            fig.Segments.Add(new LineSegment(new Point(8, 5), true));
            fig.Segments.Add(new LineSegment(new Point(3, 3), true));
            fig.Segments.Add(new LineSegment(new Point(2, 8), true));
            fig.Segments.Add(new LineSegment(new Point(4, 9), true));
            fig.Segments.Add(new LineSegment(new Point(4, 11), true));
            fig.Segments.Add(new LineSegment(new Point(0, 10), true));
            fig.Segments.Add(new LineSegment(new Point(-4, 11), true));
            fig.Segments.Add(new LineSegment(new Point(-4, 9), true));
            fig.Segments.Add(new LineSegment(new Point(-2, 8), true));
            fig.Segments.Add(new LineSegment(new Point(-3, 3), true));
            fig.Segments.Add(new LineSegment(new Point(-8, 5), true));
            fig.Segments.Add(new LineSegment(new Point(-8, 3), true));
            fig.Segments.Add(new LineSegment(new Point(-3, 0), true));
            geo.Figures.Add(fig);

            // Apply heading rotation
            geo.Transform = new RotateTransform(Heading);
            return geo;
        }
    }
}

// ============================================================
// FUEL GAUGE — circular arc style
// ============================================================

public class FuelGauge : Control
{
    public static readonly DependencyProperty FuelPercentProperty =
        DependencyProperty.Register(nameof(FuelPercent), typeof(double), typeof(FuelGauge),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double FuelPercent
    {
        get => (double)GetValue(FuelPercentProperty);
        set => SetValue(FuelPercentProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        double r = Math.Min(w, h) / 2 - 4;
        var center = new Point(w / 2, h / 2);

        // Background ring
        dc.DrawEllipse(null,
            new Pen(new SolidColorBrush(Color.FromRgb(30, 38, 64)), 8),
            center, r, r);

        // Fuel arc
        double pct = Math.Clamp(FuelPercent, 0, 1);
        var fuelColor = pct switch
        {
            < 0.1  => Color.FromRgb(239, 68, 68),
            < 0.25 => Color.FromRgb(234, 179, 8),
            _      => Color.FromRgb(249, 115, 22)
        };

        if (pct > 0.01)
        {
            double sweepAngle = pct * 270; // 270° sweep total
            double startAngle = -225;      // Start at bottom-left

            var geo = new StreamGeometry();
            using var ctx = geo.Open();

            double startRad = startAngle * Math.PI / 180;
            ctx.BeginFigure(new Point(
                center.X + r * Math.Cos(startRad),
                center.Y + r * Math.Sin(startRad)), false, false);

            double endRad = (startAngle + sweepAngle) * Math.PI / 180;
            bool isLarge  = sweepAngle > 180;
            ctx.ArcTo(new Point(
                center.X + r * Math.Cos(endRad),
                center.Y + r * Math.Sin(endRad)),
                new Size(r, r), 0, isLarge, SweepDirection.Clockwise, true, false);

            dc.DrawGeometry(null,
                new Pen(new SolidColorBrush(fuelColor), 8),
                geo);
        }

        // Percentage text
        var text = new FormattedText(
            $"{pct * 100:F0}%",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            13,
            new SolidColorBrush(Color.FromRgb(240, 244, 255)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }
}
