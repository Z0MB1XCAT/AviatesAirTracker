using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Models;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;

namespace AviatesAirTracker.Core.Analytics;

// ============================================================
// FLIGHT PROFILE CHART BUILDER
//
// Generates OxyPlot models for:
//   1. Altitude profile across the flight
//   2. Speed profile (IAS over distance)
//   3. Vertical speed profile
//   4. Fuel burn profile
//   5. N1 power profile
//
// Used in StatisticsView, ReplayView, and the landing debrief
// ============================================================

public static class FlightProfileCharts
{
    private static readonly OxyColor BgTransparent  = OxyColors.Transparent;
    private static readonly OxyColor GridColor       = OxyColor.FromRgb(30, 38, 64);
    private static readonly OxyColor AxisTextColor   = OxyColor.FromRgb(136, 146, 170);
    private static readonly OxyColor AccentBlue      = OxyColor.FromRgb(61, 126, 238);
    private static readonly OxyColor AccentGreen     = OxyColor.FromRgb(34, 197, 94);
    private static readonly OxyColor AccentOrange    = OxyColor.FromRgb(249, 115, 22);
    private static readonly OxyColor AccentYellow    = OxyColor.FromRgb(234, 179, 8);
    private static readonly OxyColor AccentPurple    = OxyColor.FromRgb(139, 92, 246);
    private static readonly OxyColor AccentCyan      = OxyColor.FromRgb(6, 182, 212);
    private static readonly OxyColor AccentRed       = OxyColor.FromRgb(239, 68, 68);

    // =====================================================
    // ALTITUDE PROFILE
    // =====================================================

    public static PlotModel BuildAltitudeProfile(List<PathPoint> path)
    {
        var model = CreateBase("Altitude Profile", "Distance (nm)", "Altitude (ft)");

        if (path.Count < 2) return model;

        var altSeries = new AreaSeries
        {
            Color              = AccentBlue,
            Fill               = OxyColor.FromArgb(30, 61, 126, 238),
            StrokeThickness    = 2,
            Title              = "Altitude MSL"
        };

        var series = new LineSeries
        {
            Color           = OxyColor.FromRgb(61, 126, 238),
            StrokeThickness = 2,
            Title           = "Altitude MSL"
        };

        double cumulativeDist = 0;
        for (int i = 0; i < path.Count; i++)
        {
            if (i > 0)
                cumulativeDist += HaversineNm(path[i - 1], path[i]);

            series.Points.Add(new DataPoint(cumulativeDist, path[i].AltitudeMSL));
        }

        // Colour-code phase segments
        model.Series.Add(series);

        // TOC / TOD annotations
        AddPhaseAnnotations(model, path);

        return model;
    }

    // =====================================================
    // SPEED PROFILE (IAS)
    // =====================================================

    public static PlotModel BuildSpeedProfile(List<PathPoint> path)
    {
        var model = CreateBase("Speed Profile", "Distance (nm)", "Ground Speed (kt)");

        if (path.Count < 2) return model;

        var series = new LineSeries
        {
            Color = AccentCyan, StrokeThickness = 2, Title = "Ground Speed"
        };

        double dist = 0;
        for (int i = 0; i < path.Count; i++)
        {
            if (i > 0) dist += HaversineNm(path[i - 1], path[i]);
            series.Points.Add(new DataPoint(dist, path[i].GroundSpeed));
        }

        model.Series.Add(series);
        return model;
    }

    // =====================================================
    // VERTICAL SPEED PROFILE
    // =====================================================

    public static PlotModel BuildVerticalSpeedProfile(List<PathPoint> path)
    {
        var model = CreateBase("Vertical Speed", "Distance (nm)", "V/S (fpm)");

        if (path.Count < 2) return model;

        var posSeries = new AreaSeries
        {
            Color = AccentGreen, Fill = OxyColor.FromArgb(25, 34, 197, 94),
            StrokeThickness = 1.5, ConstantY2 = 0, Title = "Climb"
        };
        var negSeries = new AreaSeries
        {
            Color = AccentRed, Fill = OxyColor.FromArgb(25, 239, 68, 68),
            StrokeThickness = 1.5, ConstantY2 = 0, Title = "Descent"
        };

        double dist = 0;
        for (int i = 0; i < path.Count; i++)
        {
            if (i > 0) dist += HaversineNm(path[i - 1], path[i]);
            var vs = path[i].VerticalSpeed;
            if (vs >= 0) posSeries.Points.Add(new DataPoint(dist, vs));
            else         negSeries.Points.Add(new DataPoint(dist, vs));
        }

        model.Series.Add(posSeries);
        model.Series.Add(negSeries);
        return model;
    }

    // =====================================================
    // LANDING DEBRIEF CHART
    // Shows the last 2 minutes of approach + touchdown
    // =====================================================

    public static PlotModel BuildApproachDebrief(List<PathPoint> path, LandingResult landing)
    {
        var model = CreateBase("Approach & Landing", "Time (sec before touchdown)", "Altitude AGL (ft)");

        if (path.Count < 2 || path.All(p => p.AltitudeMSL == 0)) return model;

        // Find the touchdown point in path
        var tdIdx = path
            .Select((p, i) => (p, i))
            .Where(x => x.p.Timestamp <= landing.Timestamp)
            .OrderByDescending(x => x.p.Timestamp)
            .Select(x => x.i)
            .FirstOrDefault();

        if (tdIdx < 2) return model;

        // Take 120 seconds before touchdown
        var tdTime   = path[tdIdx].Timestamp;
        var cutoff   = tdTime.AddSeconds(-120);
        var segment  = path.Where(p => p.Timestamp >= cutoff && p.Timestamp <= tdTime).ToList();

        var altSeries = new LineSeries
        {
            Color = AccentBlue, StrokeThickness = 2, Title = "Altitude"
        };
        var vsSeries = new LineSeries
        {
            Color = AccentOrange, StrokeThickness = 1.5, Title = "V/S",
            YAxisKey = "vs"
        };

        foreach (var pt in segment)
        {
            double secBefore = (tdTime - pt.Timestamp).TotalSeconds;
            altSeries.Points.Add(new DataPoint(-secBefore, pt.AltitudeMSL));
            vsSeries.Points.Add(new DataPoint(-secBefore, pt.VerticalSpeed));
        }

        model.Axes.Add(new LinearAxis
        {
            Position          = AxisPosition.Right,
            Key               = "vs",
            Title             = "V/S (fpm)",
            TitleFontSize     = 10,
            FontSize          = 9,
            TextColor         = AxisTextColor,
            TicklineColor     = OxyColors.Transparent,
            MajorGridlineStyle = LineStyle.None
        });

        model.Series.Add(altSeries);
        model.Series.Add(vsSeries);

        // Touchdown annotation
        model.Annotations.Add(new OxyPlot.Annotations.LineAnnotation
        {
            Type      = OxyPlot.Annotations.LineAnnotationType.Vertical,
            X         = 0,
            Color     = AccentRed,
            StrokeThickness = 2,
            LineStyle = LineStyle.Dash,
            Text      = $"TD  {landing.VerticalSpeedFPM:F0}fpm",
            TextColor = AccentRed
        });

        return model;
    }

    // =====================================================
    // LANDING SCORE HISTORY CHART
    // =====================================================

    public static PlotModel BuildLandingScoreHistory(List<LandingResult> landings)
    {
        var model = CreateBase("Landing Score History", "Landing #", "Score");

        if (!landings.Any()) return model;

        var bars = new BarSeries
        {
            FillColor = AccentBlue,
            StrokeColor = OxyColors.Transparent,
            StrokeThickness = 0
        };

        // Category axis labels
        var cats = new CategoryAxis
        {
            Position  = AxisPosition.Bottom,
            TextColor = AxisTextColor,
            FontSize  = 9,
            TicklineColor = OxyColors.Transparent
        };

        var sorted = landings.OrderBy(l => l.Timestamp).TakeLast(20).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            var l = sorted[i];
            bars.Items.Add(new BarItem
            {
                Value     = l.LandingScore,
                Color     = l.LandingScore switch
                {
                    >= 90 => AccentGreen,
                    >= 75 => AccentBlue,
                    >= 60 => AccentYellow,
                    >= 40 => AccentOrange,
                    _      => AccentRed
                }
            });
            cats.Labels.Add(l.AirportICAO + "\n" + l.RunwayIdentifier);
        }

        // Remove default bottom axis, add category axis
        model.Axes.Clear();
        model.Axes.Add(cats);
        model.Axes.Add(new LinearAxis
        {
            Position          = AxisPosition.Left,
            Minimum           = 0,
            Maximum           = 100,
            MajorGridlineStyle = LineStyle.Dot,
            MajorGridlineColor = GridColor,
            TextColor         = AxisTextColor,
            FontSize          = 9,
            TicklineColor     = OxyColors.Transparent
        });

        // Reference lines
        model.Annotations.Add(new OxyPlot.Annotations.LineAnnotation
        {
            Type  = OxyPlot.Annotations.LineAnnotationType.Horizontal,
            Y     = 75, Color = AccentGreen, LineStyle = LineStyle.Dot,
            Text  = "GOOD", TextColor = AccentGreen, StrokeThickness = 1
        });

        model.Series.Add(bars);
        return model;
    }

    // =====================================================
    // HELPERS
    // =====================================================

    private static PlotModel CreateBase(string title, string xTitle, string yTitle)
    {
        var model = new PlotModel
        {
            Background              = BgTransparent,
            PlotAreaBackground      = OxyColor.FromArgb(20, 10, 13, 23),
            PlotAreaBorderColor     = GridColor,
            PlotAreaBorderThickness = new OxyThickness(0, 0, 0, 1),
            TextColor               = AxisTextColor,
            TitleColor              = AxisTextColor,
            TitleFontSize           = 12,
            Title                   = title,
            Axes =
            {
                new LinearAxis
                {
                    Position           = AxisPosition.Bottom,
                    Title              = xTitle,
                    TitleFontSize      = 10,
                    FontSize           = 9,
                    TextColor          = AxisTextColor,
                    TicklineColor      = OxyColors.Transparent,
                    MajorGridlineStyle = LineStyle.Dot,
                    MajorGridlineColor = GridColor
                },
                new LinearAxis
                {
                    Position           = AxisPosition.Left,
                    Title              = yTitle,
                    TitleFontSize      = 10,
                    FontSize           = 9,
                    TextColor          = AxisTextColor,
                    TicklineColor      = OxyColors.Transparent,
                    MajorGridlineStyle = LineStyle.Dot,
                    MajorGridlineColor = GridColor
                }
            }
        };

        // OxyPlot 2.x: legend properties moved from PlotModel to a Legend object
        model.Legends.Add(new Legend
        {
            LegendBackground = OxyColors.Transparent,
            LegendBorder     = OxyColors.Transparent,
            LegendTextColor  = AxisTextColor,
            LegendFontSize   = 9
        });

        return model;
    }

    private static void AddPhaseAnnotations(PlotModel model, List<PathPoint> path)
    {
        // Mark TOC and TOD with vertical dashed lines
        double dist = 0;
        Core.SimConnect.FlightPhase? lastPhase = null;

        for (int i = 0; i < path.Count; i++)
        {
            if (i > 0) dist += HaversineNm(path[i - 1], path[i]);

            var p = path[i].Phase;
            if (lastPhase.HasValue && lastPhase != p)
            {
                string? label = (lastPhase, p) switch
                {
                    (Core.SimConnect.FlightPhase.Climb, Core.SimConnect.FlightPhase.Cruise) => "TOC",
                    (Core.SimConnect.FlightPhase.Cruise, Core.SimConnect.FlightPhase.TopOfDescent) => "TOD",
                    _ => null
                };

                if (label != null)
                {
                    model.Annotations.Add(new OxyPlot.Annotations.LineAnnotation
                    {
                        Type = OxyPlot.Annotations.LineAnnotationType.Vertical,
                        X    = dist,
                        Color = OxyColor.FromRgb(136, 146, 170),
                        LineStyle = LineStyle.Dot,
                        StrokeThickness = 1,
                        Text = label,
                        TextColor = OxyColor.FromRgb(136, 146, 170)
                    });
                }
            }
            lastPhase = p;
        }
    }

    private static double HaversineNm(PathPoint a, PathPoint b)
    {
        const double R = 3440.065;
        double dLat = (b.Latitude - a.Latitude) * Math.PI / 180;
        double dLon = (b.Longitude - a.Longitude) * Math.PI / 180;
        double x = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(a.Latitude * Math.PI / 180) * Math.Cos(b.Latitude * Math.PI / 180) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(x), Math.Sqrt(1 - x));
    }
}
