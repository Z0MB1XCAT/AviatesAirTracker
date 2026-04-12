using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System.Collections.ObjectModel;

namespace AviatesAirTracker.ViewModels;

public partial class LiveFlightViewModel : ObservableObject
{
    // Primary flight instruments
    [ObservableProperty] private string _altMSL = "----";
    [ObservableProperty] private string _altAGL = "----";
    [ObservableProperty] private string _ias = "----";
    [ObservableProperty] private string _tas = "----";
    [ObservableProperty] private string _gs = "----";
    [ObservableProperty] private string _vs = "----";
    [ObservableProperty] private string _mach = "----";
    [ObservableProperty] private string _heading = "----";
    [ObservableProperty] private string _track = "----";
    [ObservableProperty] private string _pitch = "----";
    [ObservableProperty] private string _bank = "----";

    // Engine
    [ObservableProperty] private string _n1_1 = "--.-";
    [ObservableProperty] private string _n1_2 = "--.-";
    [ObservableProperty] private string _n1_3 = "--.-";
    [ObservableProperty] private string _n1_4 = "--.-";
    [ObservableProperty] private double _n1_1_pct;
    [ObservableProperty] private double _n1_2_pct;

    // Fuel
    [ObservableProperty] private string _fuelTotal = "----";
    [ObservableProperty] private string _fuelBurnRate = "----";
    [ObservableProperty] private double _fuelPct;

    // Config
    [ObservableProperty] private string _flaps = "----";
    [ObservableProperty] private string _gear = "UP";
    [ObservableProperty] private string _gearColor = "#22C55E";
    [ObservableProperty] private string _spoilers = "----";

    // Autopilot
    [ObservableProperty] private bool _apEngaged;
    [ObservableProperty] private string _apMode = "—";
    [ObservableProperty] private string _apAlt = "----";
    [ObservableProperty] private string _apSpd = "----";
    [ObservableProperty] private string _apHdg = "----";
    [ObservableProperty] private string _apVs = "----";

    // Weather
    [ObservableProperty] private string _windInfo = "--- / --kt";
    [ObservableProperty] private string _headwind = "+0kt";
    [ObservableProperty] private string _crosswind = "+0kt";
    [ObservableProperty] private string _oat = "--°C";
    [ObservableProperty] private string _qnh = "----";

    // Flight info
    [ObservableProperty] private string _phase = "Unknown";
    [ObservableProperty] private string _phaseColor = "#4A5568";
    [ObservableProperty] private bool _approachStable;
    [ObservableProperty] private string _approachStableText = "—";
    [ObservableProperty] private string _approachStableColor = "#4A5568";

    // ILS
    [ObservableProperty] private bool _ilsCapturing;
    [ObservableProperty] private double _locDev;
    [ObservableProperty] private double _gsDev;

    // Navigation
    [ObservableProperty] private string _nav1Freq = "---.--";
    [ObservableProperty] private string _com1Freq = "---.---";
    [ObservableProperty] private string _squawk = "2000";

    // Weight
    [ObservableProperty] private string _grossWeight = "----";
    [ObservableProperty] private string _cgPct = "--.-";

    // VS color coding
    [ObservableProperty] private string _vsColor = "#F0F4FF";

    // Alerts panel
    public ObservableCollection<string> ApproachAlerts { get; } = [];

    // OxyPlot models for live mini-charts
    public PlotModel AltitudePlot { get; } = CreateMiniPlot("Alt (ft)");
    public PlotModel SpeedPlot { get; } = CreateMiniPlot("IAS (kt)");
    public PlotModel VSPlot { get; } = CreateMiniPlot("VS (fpm)");
    public PlotModel N1Plot { get; } = CreateMiniPlot("N1 (%)");

    private readonly LineSeries _altSeries = new() { Color = OxyColor.FromRgb(61, 126, 238), LineStyle = LineStyle.Solid, StrokeThickness = 1.5 };
    private readonly LineSeries _speedSeries = new() { Color = OxyColor.FromRgb(34, 197, 94), LineStyle = LineStyle.Solid, StrokeThickness = 1.5 };
    private readonly LineSeries _vsSeries = new() { Color = OxyColor.FromRgb(249, 115, 22), LineStyle = LineStyle.Solid, StrokeThickness = 1.5 };
    private readonly LineSeries _n1Series = new() { Color = OxyColor.FromRgb(139, 92, 246), LineStyle = LineStyle.Solid, StrokeThickness = 1.5 };

    private double _plotX = 0;
    private const int MAX_PLOT_POINTS = 200;

    public LiveFlightViewModel()
    {
        AltitudePlot.Series.Add(_altSeries);
        SpeedPlot.Series.Add(_speedSeries);
        VSPlot.Series.Add(_vsSeries);
        N1Plot.Series.Add(_n1Series);
    }

    public void UpdateTelemetry(TelemetrySnapshot snap)
    {
        // Instruments
        AltMSL = $"{snap.AltitudePressure:F0}";
        AltAGL = $"{snap.AltitudeAGL:F0}";
        Ias = $"{snap.IASKts:F0}";
        Tas = $"{snap.TASKts:F0}";
        Gs = $"{snap.GroundSpeedKts:F0}";
        Vs = $"{snap.VerticalSpeedFPM:+#;-#;0}";
        Mach = $"{snap.Raw.Mach:F3}";
        Heading = $"{snap.Raw.HeadingMagnetic:F0}°";
        Track = $"{snap.Raw.TrackTrue:F0}°";
        Pitch = $"{snap.Raw.Pitch:+#.#;-#.#;0}°";
        Bank = $"{snap.Raw.Bank:+#.#;-#.#;0}°";

        // VS color
        VsColor = snap.VerticalSpeedFPM switch
        {
            < -1500 => "#EF4444",
            < -800 => "#F97316",
            > 3000 => "#EAB308",
            _ => "#F0F4FF"
        };

        // Engines
        N1_1 = $"{snap.Raw.EngineN1_1:F1}";
        N1_2 = $"{snap.Raw.EngineN1_2:F1}";
        N1_3 = $"{snap.Raw.EngineN1_3:F1}";
        N1_4 = $"{snap.Raw.EngineN1_4:F1}";
        N1_1_pct = snap.Raw.EngineN1_1 / 100.0;
        N1_2_pct = snap.Raw.EngineN1_2 / 100.0;

        // Fuel
        FuelTotal = $"{snap.Raw.FuelTotalLbs:F0}";
        FuelBurnRate = $"{snap.FuelBurnRatePPH:F0}";
        FuelPct = snap.Raw.MaxGrossWeight > 0 ? snap.Raw.FuelTotalLbs / (snap.Raw.MaxGrossWeight * 0.4) : 0;

        // Config
        Flaps = $"{snap.Raw.FlapsPercent:F0}%";
        Gear = snap.GearDown ? "DN" : "UP";
        GearColor = snap.GearDown ? "#EAB308" : "#22C55E";
        Spoilers = snap.Raw.SpoilerPercent > 10 ? $"{snap.Raw.SpoilerPercent:F0}%" : "RET";

        // AP
        ApEngaged = snap.AutopilotEngaged;
        ApAlt = $"{snap.Raw.AutopilotAltValue:F0}";
        ApSpd = $"{snap.Raw.AutopilotSpeedValue:F0}";
        ApHdg = $"{snap.Raw.AutopilotHeadingValue:F0}°";
        ApVs = $"{snap.Raw.AutopilotVSValue:+#;-#;0}";

        var apModes = new List<string>();
        if (snap.Raw.AutopilotAltitudeLock > 0.5) apModes.Add("ALT");
        if (snap.Raw.AutopilotHeadingLock > 0.5) apModes.Add("HDG");
        if (snap.Raw.AutopilotNAV1 > 0.5) apModes.Add("NAV");
        if (snap.Raw.AutopilotAPPR > 0.5) apModes.Add("APP");
        if (snap.Raw.AutopilotFLC > 0.5) apModes.Add("FLC");
        ApMode = apModes.Count > 0 ? string.Join(" | ", apModes) : "OFF";

        // Weather
        WindInfo = $"{snap.Raw.WindDirection:F0}° / {snap.Raw.WindSpeed:F0}kt";
        Headwind = $"{snap.HeadwindComponent:+#;-#;0}kt";
        Crosswind = $"{snap.CrosswindComponent:+#;-#;0}kt";
        Oat = $"{snap.Raw.OAT:F0}°C";
        Qnh = $"{snap.Raw.SeaLevelPressure:F0}mb";

        // ILS
        IlsCapturing = snap.ILSCapturing;
        LocDev = snap.LocalizerDevDots;
        GsDev = snap.GlideSlopeDevDots;

        // Nav
        Nav1Freq = $"{snap.Raw.NAV1Frequency:F2}";
        Com1Freq = $"{snap.Raw.COM1Frequency:F3}";
        Squawk = ((int)snap.Raw.TransponderCode).ToString("D4");

        // Weight
        GrossWeight = $"{snap.Raw.GrossWeight:F0}";
        CgPct = $"{snap.Raw.CGPercent:F1}";

        // Phase
        Phase = snap.Phase.ToString();
        PhaseColor = snap.Phase switch
        {
            FlightPhase.Cruise => "#22C55E",
            FlightPhase.Approach or FlightPhase.FinalApproach => "#F97316",
            FlightPhase.Landing => "#EF4444",
            FlightPhase.Climb or FlightPhase.InitialClimb => "#3D7EEE",
            FlightPhase.Descent or FlightPhase.TopOfDescent => "#EAB308",
            FlightPhase.Takeoff => "#8B5CF6",
            _ => "#4A5568"
        };

        // Approach stability
        ApproachStable = snap.ApproachStable;
        ApproachStableText = snap.ApproachStable ? "STABLE" : (snap.ApproachAlerts.Count > 0 ? "UNSTABLE" : "—");
        ApproachStableColor = snap.ApproachStable ? "#22C55E" : (snap.ApproachAlerts.Count > 0 ? "#EF4444" : "#4A5568");

        ApproachAlerts.Clear();
        foreach (var a in snap.ApproachAlerts) ApproachAlerts.Add(a);

        // Update plots
        UpdatePlots(snap);
    }

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

    private static PlotModel CreateMiniPlot(string title)
    {
        return new PlotModel
        {
            Background = OxyColors.Transparent,
            PlotAreaBackground = OxyColors.Transparent,
            PlotAreaBorderColor = OxyColor.FromRgb(30, 38, 64),
            PlotAreaBorderThickness = new OxyThickness(0, 0, 0, 1),
            TextColor = OxyColor.FromRgb(136, 146, 170),
            TitleColor = OxyColor.FromRgb(136, 146, 170),
            TitleFontSize = 10,
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
                    IsAxisVisible = false
                }
            }
        };
    }
}
