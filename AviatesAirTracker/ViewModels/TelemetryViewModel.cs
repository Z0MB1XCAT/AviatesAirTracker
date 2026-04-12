using AviatesAirTracker.Core.SimConnect;
using CommunityToolkit.Mvvm.ComponentModel;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace AviatesAirTracker.ViewModels;

public partial class TelemetryViewModel : ObservableObject
{
    private readonly Queue<TelemetrySnapshot> _samples = new();
    private const int MAX_SAMPLES = 1000;

    public PlotModel AltitudePlot { get; } = BuildPlot("Altitude (ft MSL)", OxyColor.FromRgb(61, 126, 238));
    public PlotModel SpeedPlot { get; } = BuildPlot("Airspeed (kts)", OxyColor.FromRgb(34, 197, 94));
    public PlotModel VSPlot { get; } = BuildPlot("Vertical Speed (fpm)", OxyColor.FromRgb(249, 115, 22));
    public PlotModel PitchPlot { get; } = BuildPlot("Pitch (°)", OxyColor.FromRgb(139, 92, 246));
    public PlotModel BankPlot { get; } = BuildPlot("Bank (°)", OxyColor.FromRgb(234, 179, 8));
    public PlotModel N1Plot { get; } = BuildPlot("N1 (%)", OxyColor.FromRgb(6, 182, 212));
    public PlotModel FuelPlot { get; } = BuildPlot("Fuel (lbs)", OxyColor.FromRgb(239, 68, 68));

    private int _chartRefreshCounter;

    public void AddSample(TelemetrySnapshot snap)
    {
        _samples.Enqueue(snap);
        if (_samples.Count > MAX_SAMPLES) _samples.Dequeue();

        _chartRefreshCounter++;
        if (_chartRefreshCounter < 20) return;  // Refresh every 20 samples (~1Hz)
        _chartRefreshCounter = 0;

        RebuildPlots();
    }

    private void RebuildPlots()
    {
        var snapshots = _samples.ToList();
        if (snapshots.Count < 2) return;

        var t0 = snapshots[0].Timestamp;

        void Fill(PlotModel model, Func<TelemetrySnapshot, double> selector)
        {
            var series = (LineSeries)model.Series[0];
            series.Points.Clear();
            foreach (var s in snapshots)
                series.Points.Add(new DataPoint((s.Timestamp - t0).TotalSeconds, selector(s)));
            model.InvalidatePlot(true);
        }

        Fill(AltitudePlot, s => s.AltitudePressure);
        Fill(SpeedPlot, s => s.IASKts);
        Fill(VSPlot, s => s.VerticalSpeedFPM);
        Fill(PitchPlot, s => s.Raw.Pitch);
        Fill(BankPlot, s => s.Raw.Bank);
        Fill(N1Plot, s => s.Raw.EngineN1_1);
        Fill(FuelPlot, s => s.Raw.FuelTotalLbs);
    }

    private static PlotModel BuildPlot(string title, OxyColor color)
    {
        var model = new PlotModel
        {
            Background = OxyColors.Transparent,
            PlotAreaBackground = OxyColor.FromArgb(30, 10, 13, 23),
            PlotAreaBorderColor = OxyColor.FromRgb(30, 38, 64),
            TextColor = OxyColor.FromRgb(136, 146, 170),
            TitleColor = OxyColor.FromRgb(136, 146, 170),
            TitleFontSize = 11,
            Title = title,
            Axes =
            {
                new LinearAxis
                {
                    Position = AxisPosition.Left,
                    TextColor = OxyColor.FromRgb(136, 146, 170),
                    TicklineColor = OxyColors.Transparent,
                    MajorGridlineStyle = LineStyle.Dot,
                    MajorGridlineColor = OxyColor.FromRgb(30, 38, 64),
                    FontSize = 9
                },
                new LinearAxis
                {
                    Position = AxisPosition.Bottom,
                    Title = "Time (s)",
                    TitleFontSize = 9,
                    TextColor = OxyColor.FromRgb(136, 146, 170),
                    TicklineColor = OxyColors.Transparent,
                    FontSize = 9
                }
            },
            Series =
            {
                new LineSeries
                {
                    Color = color,
                    LineStyle = LineStyle.Solid,
                    StrokeThickness = 1.5
                }
            }
        };
        return model;
    }
}
