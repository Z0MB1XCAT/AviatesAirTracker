using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Models;
using AviatesAirTracker.Platform;
using AviatesAirTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System.Collections.ObjectModel;

namespace AviatesAirTracker.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SimConnectManager _simConnect;
    private readonly PttInputService _pttInput;
    private readonly FlightSessionManager _session;
    private readonly AlertService _alertService;
    private readonly SettingsService _settings;
    private readonly TelemetryProcessor _telemetryProcessor;
    private readonly SimBriefService _simBriefSvc;
    private readonly ExportService _exportSvc;
    private readonly IUiDispatcher _ui;
    private readonly FilePickerService _filePicker;

    public DashboardViewModel Dashboard { get; }
    public MapViewModel Map { get; }
    public LandingAnalysisViewModel LandingAnalysis { get; }
    public SettingsViewModel SettingsVm { get; }
    public ReplayViewModel Replay { get; }

    [ObservableProperty] private SimConnectionStatus _connectionStatus = SimConnectionStatus.Disconnected;
    [ObservableProperty] private string _connectionStatusText = "MSFS Not Running";
    [ObservableProperty] private string _connectionStatusColor = "#4A5568";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _flightSessionText = "No Active Flight";
    [ObservableProperty] private FlightSessionState _sessionState = FlightSessionState.Idle;
    [ObservableProperty] private string _sessionStateColor = "#4A5568";
    [ObservableProperty] private bool _hasActiveFlight;
    [ObservableProperty] private string _headerCallsign = "AVIATES";
    [ObservableProperty] private string _headerDep = "----";
    [ObservableProperty] private string _headerArr = "----";
    [ObservableProperty] private string _headerAlt = "----";
    [ObservableProperty] private string _headerSpeed = "----";
    [ObservableProperty] private string _headerPhase = "PARKED";
    [ObservableProperty] private string _pilotName = "Pilot";
    [ObservableProperty] private string _pilotRank = "First Officer";
    [ObservableProperty] private bool _isLoadingSimBrief;

    public MainViewModel(
        SimConnectManager simConnect, FlightSessionManager session,
        AlertService alertService, SettingsService settings,
        TelemetryProcessor telemetryProcessor, SimBriefService simBriefSvc,
        ExportService exportSvc, PttInputService pttInput,
        DashboardViewModel dashboard,
        MapViewModel map, LandingAnalysisViewModel landingAnalysis,
        SettingsViewModel settingsVm,
        ReplayViewModel replay, IUiDispatcher ui, FilePickerService filePicker)
    {
        _simConnect = simConnect; _session = session; _pttInput = pttInput;
        _alertService = alertService; _settings = settings;
        _telemetryProcessor = telemetryProcessor;
        _simBriefSvc = simBriefSvc; _exportSvc = exportSvc;
        _ui = ui;
        _filePicker = filePicker;
        Dashboard = dashboard; Map = map;
        LandingAnalysis = landingAnalysis;
        SettingsVm = settingsVm; Replay = replay;

        _simConnect.TelemetryReceived += (_, snap) => _telemetryProcessor.Process(snap);
        _telemetryProcessor.SnapshotReady += (_, snap) => _session.OnTelemetryReceived(snap);
        _simConnect.ConnectionStatusChanged += OnConnectionStatusChanged;
        _session.SessionStateChanged += OnSessionStateChanged;
        _session.TelemetryUpdated += OnTelemetryUpdated;
        _session.FlightCompleted += OnFlightCompleted;

        PilotName = settings.Settings.PilotName.Length > 0 ? settings.Settings.PilotName : "Pilot";
    }

    [RelayCommand]
    public async Task LoadSimBriefAsync()
    {
        var user = _settings.Settings.SimBriefUsername;
        if (string.IsNullOrWhiteSpace(user)) return;
        IsLoadingSimBrief = true;
        try
        {
            var plan = await _simBriefSvc.FetchLatestOFPAsync(user);
            if (plan != null)
            {
                _session.AssignSimBriefPlan(plan);       // populate RouteTracker first
                _simBriefSvc.NotifyPlanLoaded(plan);     // then notify subscribers (including MapViewModel)
                HeaderDep = plan.DepartureICAO; HeaderArr = plan.ArrivalICAO;
                _alertService.ShowAlert($"SimBrief loaded: {plan.DepartureICAO}→{plan.ArrivalICAO}",
                    AlertLevel.Success, TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex) { Log.Warning(ex, "SimBrief load failed"); }
        finally { IsLoadingSimBrief = false; }
    }

    [RelayCommand]
    public async Task ImportSimBriefFileAsync()
    {
        var filePath = await _filePicker.PickSingleFileAsync(".xml", ".json");
        if (filePath == null) return;
        try
        {
            var plan = filePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                ? SimBriefService.ParseXmlOFP(filePath)
                : SimBriefService.ParseJsonOFPFile(filePath);
            if (plan != null)
            {
                _session.AssignSimBriefPlan(plan);
                _simBriefSvc.NotifyPlanLoaded(plan);
                HeaderDep = plan.DepartureICAO; HeaderArr = plan.ArrivalICAO;
                _alertService.ShowAlert($"OFP imported: {plan.DepartureICAO}→{plan.ArrivalICAO}",
                    AlertLevel.Success, TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex) { _alertService.ShowAlert("Import failed: " + ex.Message, AlertLevel.Warning); }
    }

    private void OnConnectionStatusChanged(object? _, SimConnectionStatus s) =>
        _ui.Invoke(() =>
        {
            ConnectionStatus = s; IsConnected = s == SimConnectionStatus.Connected;
            (ConnectionStatusText, ConnectionStatusColor) = s switch
            {
                SimConnectionStatus.Connected    => ("MSFS Connected",   "#22C55E"),
                SimConnectionStatus.Connecting   => ("Connecting...",    "#EAB308"),
                SimConnectionStatus.Error        => ("Connection Error", "#EF4444"),
                _                                => ("MSFS Not Running", "#4A5568")
            };
        });

    private void OnSessionStateChanged(object? _, FlightSessionState s) =>
        _ui.Invoke(() =>
        {
            SessionState = s;
            HasActiveFlight = s is not (FlightSessionState.Idle or FlightSessionState.Complete);
            (FlightSessionText, SessionStateColor) = s switch
            {
                FlightSessionState.PreFlight   => ("Pre-Flight",    "#3D7EEE"),
                FlightSessionState.Taxiing     => ("Taxiing",       "#EAB308"),
                FlightSessionState.Airborne    => ("Airborne",      "#22C55E"),
                FlightSessionState.OnApproach  => ("On Approach",   "#F97316"),
                FlightSessionState.Landed      => ("Landed",        "#22C55E"),
                FlightSessionState.Complete    => ("Flight Complete","#8B5CF6"),
                _                              => ("No Active Flight","#4A5568")
            };
            var f = _session.CurrentFlight;
            if (f != null)
            {
                if (f.Callsign.Length > 0)      HeaderCallsign = f.Callsign;
                if (f.DepartureICAO.Length > 0) HeaderDep = f.DepartureICAO;
                if (f.ArrivalICAO.Length > 0)   HeaderArr = f.ArrivalICAO;
            }
        });

    private void OnTelemetryUpdated(object? _, Core.SimConnect.TelemetrySnapshot snap) =>
        _ui.Invoke(() =>
        {
            HeaderAlt   = $"{snap.AltitudePressure:F0}";
            HeaderSpeed = $"{snap.IASKts:F0}";
            HeaderPhase = snap.Phase.ToString().ToUpper();
            Map.UpdateTelemetry(snap);
        });

    private void OnFlightCompleted(object? _, FlightRecord __) =>
        _ui.Invoke(() =>
        {
            _ = Dashboard.RefreshAsync();
            HeaderDep = HeaderArr = HeaderAlt = HeaderSpeed = "----";
            HeaderPhase = "PARKED";
        });

    public void SetWindowHandle(IntPtr hwnd)
    {
        _simConnect.AutoConnect = _settings.Settings.AutoConnectSimConnect;
        _settings.SettingsChanged += (_, _) => _simConnect.AutoConnect = _settings.Settings.AutoConnectSimConnect;
        _simConnect.SetWindowHandle(hwnd);
        _pttInput.SetWindowHandle(hwnd);
        _filePicker.SetWindowHandle(hwnd);
    }
    public void OnWindowMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l)
    {
        if (msg == 0x0402) _simConnect.HandleSimConnectMessage();
    }
}
