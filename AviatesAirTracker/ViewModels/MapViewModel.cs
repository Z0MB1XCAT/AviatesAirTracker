using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Models;
using AviatesAirTracker.Platform;
using AviatesAirTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AviatesAirTracker.ViewModels;

public partial class MapViewModel : ObservableObject
{
    // ── Aircraft position (pushed to JS) ─────────────────────
    [ObservableProperty] private double _lat = 51.477;
    [ObservableProperty] private double _lon = -0.461;
    [ObservableProperty] private double _hdg;

    // ── Telemetry HUD ─────────────────────────────────────────
    [ObservableProperty] private string _altText  = "----";
    [ObservableProperty] private string _vsText   = "----";
    [ObservableProperty] private string _iasText  = "----";
    [ObservableProperty] private string _gsText   = "----";
    [ObservableProperty] private string _hdgText  = "----";
    [ObservableProperty] private string _machText = "----";

    // ── Progress HUD ──────────────────────────────────────────
    [ObservableProperty] private string _routeLabel   = "---- → ----";
    [ObservableProperty] private string _nextWaypoint = "----";
    [ObservableProperty] private string _etaZ         = "----";
    [ObservableProperty] private string _distRemText  = "----";
    [ObservableProperty] private string _fuelRemText  = "----";

    // ── Phase badge ───────────────────────────────────────────
    [ObservableProperty] private string _phaseText  = "PARKED";
    [ObservableProperty] private string _phaseColor = "#4A5568";

    // ── Status overlay ────────────────────────────────────────
    [ObservableProperty] private string _noFlightText    = "Waiting for SimConnect…";
    [ObservableProperty] private bool   _hasActiveFlight;

    // ── Page lifecycle gate ───────────────────────────────────
    public bool MapReady { get; set; }

    // ── Internal state ────────────────────────────────────────
    private int  _tickCount = 4;
    private int  _lastPathCount = -1;
    private volatile bool _planNeedsSync;
    private SimBriefFlightPlan? _latestPlan;

    // ── Dependencies ──────────────────────────────────────────
    private readonly RouteTracker       _routeTracker;
    private readonly SimBriefService    _simBriefSvc;
    private readonly SimConnectManager  _simConnect;
    private readonly IUiDispatcher      _ui;
    private readonly FlightTrailService _flightTrail;

    // ── Page callback ─────────────────────────────────────────
    public event Action? MapRenderRequested;

    public MapViewModel(
        RouteTracker routeTracker,
        SimBriefService simBriefSvc,
        SimConnectManager simConnect,
        IUiDispatcher ui,
        FlightTrailService flightTrail)
    {
        _routeTracker = routeTracker;
        _simBriefSvc  = simBriefSvc;
        _simConnect   = simConnect;
        _ui           = ui;
        _flightTrail  = flightTrail;

        _simBriefSvc.FlightPlanLoaded       += OnFlightPlanLoaded;
        _simConnect.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    public void UpdateTelemetry(TelemetrySnapshot snap)
    {
        if (++_tickCount % 5 != 0) return;

        Lat = snap.Latitude;
        Lon = snap.Longitude;
        Hdg = snap.Raw.HeadingTrue;

        AltText = snap.AltitudePressure >= 18000
            ? $"FL{snap.AltitudePressure / 100:F0}"
            : $"{snap.AltitudePressure:F0} FT";

        VsText   = snap.Phase is FlightPhase.Parked or FlightPhase.Pushback
            ? "----"
            : $"{(snap.VerticalSpeedFPM >= 0 ? "+" : "")}{snap.VerticalSpeedFPM:F0}";
        IasText  = $"{snap.IASKts:F0}";
        GsText   = $"{snap.GroundSpeedKts:F0}";
        HdgText  = $"{snap.Raw.HeadingTrue:F0}°";
        MachText = snap.Raw.Mach >= 0.10 ? $"M{snap.Raw.Mach:F2}" : "----";

        HasActiveFlight = snap.Phase is not (
            FlightPhase.Parked or FlightPhase.Pushback or FlightPhase.Unknown);

        PhaseText  = snap.Phase.ToString().ToUpper();
        PhaseColor = snap.Phase switch
        {
            FlightPhase.Taxi or FlightPhase.Vacating                                      => "#EAB308",
            FlightPhase.Takeoff or FlightPhase.InitialClimb or FlightPhase.Climb          => "#3D7EEE",
            FlightPhase.Cruise                                                             => "#22C55E",
            FlightPhase.TopOfDescent or FlightPhase.Descent
                or FlightPhase.Approach or FlightPhase.FinalApproach                      => "#F97316",
            FlightPhase.Landing or FlightPhase.Rollout                                    => "#22C55E",
            _                                                                              => "#4A5568"
        };

        var next = _routeTracker.GetNextWaypoint();
        NextWaypoint = next?.Identifier ?? "----";

        double distRem = _routeTracker.GetRemainingDistanceNm(snap.Latitude, snap.Longitude);
        DistRemText = distRem > 0 ? $"{distRem:F0} NM" : "----";

        if (distRem > 0 && snap.GroundSpeedKts > 50)
        {
            var etaUtc = DateTime.UtcNow.AddHours(distRem / snap.GroundSpeedKts);
            EtaZ = etaUtc.ToString("HHmm") + "Z";
        }
        else EtaZ = "----";

        double fuelTonnes = snap.FuelRemainingLbs / 2204.62;
        FuelRemText = $"{fuelTonnes:F1} T";

        MapRenderRequested?.Invoke();
    }

    public IReadOnlyList<object>? GetPathIfChanged()
    {
        // Reads the shared, permanent FlightTrailService instead of RouteTracker's own
        // 500-point rolling window, so the colored flown path never silently drops the start
        // of a long flight. Both MapPage and AcarsPage's mini-map read from this same trail.
        var totalCount = _flightTrail.RecordedPointCount;
        if (totalCount == _lastPathCount) return null;
        _lastPathCount = totalCount;
        var path = _flightTrail.GetTrailForRendering(2000);
        return path
            .Select(p => (object)new { lat = p.Lat, lon = p.Lon, alt = p.AltFt })
            .ToList();
    }

    public (SimBriefFlightPlan? Plan, bool Dirty) ConsumePlanSync()
    {
        var dirty = _planNeedsSync; // volatile read — acquire fence must come first
        var plan  = _latestPlan;
        _planNeedsSync = false;
        return (plan, dirty);
    }

    private void OnFlightPlanLoaded(object? sender, SimBriefFlightPlan plan)
    {
        _ui.Invoke(() =>
        {
            _latestPlan    = plan;
            _planNeedsSync = true;
            RouteLabel     = $"{plan.DepartureICAO} → {plan.ArrivalICAO}";
            MapRenderRequested?.Invoke();
        });
    }

    private void OnConnectionStatusChanged(object? sender, SimConnectionStatus status)
    {
        _ui.Invoke(() =>
        {
            NoFlightText = status == SimConnectionStatus.Connected
                ? "No Active Flight"
                : "Waiting for SimConnect…";
        });
    }
}
