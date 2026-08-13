# 20Hz Batching Reference

Source: `AviatesAirTracker/ViewModels/LiveFlightViewModel.cs` (deleted in this commit
by Task 4 of the WinUI 3 migration plan — "Delete OxyPlot and the three orphan
ViewModels"). Last commit touching that file before deletion:
`e8eb167a48d7696191f93f4b50fd632b0ae69c35` (2026-04-12).

Kept because `CLAUDE.md` names this file as the worked example for throttling UI
work on the 20Hz SimConnect telemetry path ("batch every 3–5 samples"). Deleting
the file destroyed the only concrete example of the pattern, so the excerpt below
is preserved here for later phases that reimplement this cadence in whichever
ViewModel backs the live ACARS surface.

## The pattern

A running sample counter is incremented on every telemetry tick; expensive UI
refresh work (here, `PlotModel.InvalidatePlot(true)`) is gated behind a modulo
check so it only runs once every 5 samples instead of on every 20Hz tick.

```csharp
private double _plotX = 0;
private const int MAX_PLOT_POINTS = 200;

private void UpdatePlots(TelemetrySnapshot snap)
{
    _plotX++;
    double x = _plotX;

    void AddPoint(LineSeries series, double y)
    {
        series.Points.Add(new DataPoint(x, y));
        if (series.Points.Count > MAX_PLOT_POINTS)
            series.Points.RemoveAt(0);
    }

    AddPoint(_altSeries, snap.AltitudePressure);
    AddPoint(_speedSeries, snap.IASKts);
    AddPoint(_vsSeries, snap.VerticalSpeedFPM);
    AddPoint(_n1Series, snap.Raw.EngineN1_1);

    // Refresh plots every 5 samples to reduce CPU
    if (_plotX % 5 == 0)
    {
        AltitudePlot.InvalidatePlot(true);
        SpeedPlot.InvalidatePlot(true);
        VSPlot.InvalidatePlot(true);
        N1Plot.InvalidatePlot(true);
    }
}
```

The counter increments unconditionally (data is still appended every sample);
only the visible-refresh call is throttled. The equivalent for a Razor page is
the same shape: increment a counter on every `UpdateTelemetry`/tick call, and
only call `StateHasChanged()` when `counter % 5 == 0`.
