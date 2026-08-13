using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Models;
using AviatesAirTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Serilog;
using System.Collections.ObjectModel;
using System.Windows;

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

    public DashboardViewModel Dashboard { get; }
    public MapViewModel Map { get; }
    public LandingAnalysisViewModel LandingAnalysis { get; }
    public SettingsViewModel SettingsVm { get; }
    public ReplayViewModel Replay { get; }

    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private string _currentPageName = "Dashboard";
    [ObservableProperty] private bool _isDashboardActive = true;
    [ObservableProperty] private bool _isMapActive;
    [ObservableProperty] private bool _isLandingActive;
    [ObservableProperty] private bool _isSettingsActive;
    [ObservableProperty] private bool _isReplayActive;
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

    public ObservableCollection<AlertNotification> ActiveAlerts { get; } = [];

    public MainViewModel(
        SimConnectManager simConnect, FlightSessionManager session,
        AlertService alertService, SettingsService settings,
        TelemetryProcessor telemetryProcessor, SimBriefService simBriefSvc,
        ExportService exportSvc, PttInputService pttInput,
        DashboardViewModel dashboard,
        MapViewModel map, LandingAnalysisViewModel landingAnalysis,
        SettingsViewModel settingsVm,
        ReplayViewModel replay)
    {
        _simConnect = simConnect; _session = session; _pttInput = pttInput;
        _alertService = alertService; _settings = settings;
        _telemetryProcessor = telemetryProcessor;
        _simBriefSvc = simBriefSvc; _exportSvc = exportSvc;
        Dashboard = dashboard; Map = map;
        LandingAnalysis = landingAnalysis;
        SettingsVm = settingsVm; Replay = replay;

        _simConnect.TelemetryReceived += (_, snap) => _telemetryProcessor.Process(snap);
        _telemetryProcessor.SnapshotReady += (_, snap) => _session.OnTelemetryReceived(snap);
        _simConnect.ConnectionStatusChanged += OnConnectionStatusChanged;
        _session.SessionStateChanged += OnSessionStateChanged;
        _session.TelemetryUpdated += OnTelemetryUpdated;
        _session.FlightCompleted += OnFlightCompleted;
        _alertService.AlertRaised += OnAlertRaised;
        _alertService.LandingResultReady += OnLandingResultReady;

        PilotName = settings.Settings.PilotName.Length > 0 ? settings.Settings.PilotName : "Pilot";
        NavigateToDashboard();
    }

    [RelayCommand] public void NavigateToDashboard()  => Navigate(Dashboard,       "Dashboard",         ref _isDashboardActive);
    [RelayCommand] public void NavigateToMap()        => Navigate(Map,             "World Map",          ref _isMapActive);
    [RelayCommand] public void NavigateToLanding()    => Navigate(LandingAnalysis, "Landing Analysis",   ref _isLandingActive);
    [RelayCommand] public void NavigateToSettings()   => Navigate(SettingsVm,      "Settings",           ref _isSettingsActive);
    [RelayCommand] public void NavigateToReplay()     => Navigate(Replay,          "Flight Replay",      ref _isReplayActive);

    private void Navigate(object page, string name, ref bool flag)
    {
        // #region agent log
        AgentDebug.Log("H1", "MainViewModel.cs:Navigate", "Navigate requested", new
        {
            pageType = page?.GetType().FullName,
            name,
            currentPageType = CurrentPage?.GetType().FullName,
            currentPageName = CurrentPageName
        });
        // #endregion

        ClearNav(); flag = true; CurrentPage = page; CurrentPageName = name;

        // #region agent log
        AgentDebug.Log("H1", "MainViewModel.cs:Navigate", "Navigate applied", new
        {
            newPageType = CurrentPage?.GetType().FullName,
            newPageName = CurrentPageName
        });
        // #endregion
    }

    private void ClearNav()
    {
        IsDashboardActive = IsMapActive = IsLandingActive =
        IsSettingsActive = IsReplayActive = false;
    }

    [RelayCommand]
    public async Task LoadSimBriefAsync()
    {
        var user = _settings.Settings.SimBriefUsername;
        if (string.IsNullOrWhiteSpace(user)) { NavigateToSettings(); return; }
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
    public void ImportSimBriefFile()
    {
        var dlg = new OpenFileDialog { Filter = "SimBrief OFP|*.xml;*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var plan = dlg.FileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                ? SimBriefService.ParseXmlOFP(dlg.FileName)
                : SimBriefService.ParseJsonOFPFile(dlg.FileName);
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
        Application.Current.Dispatcher.Invoke(() =>
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
        Application.Current.Dispatcher.Invoke(() =>
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
        Application.Current.Dispatcher.Invoke(() =>
        {
            HeaderAlt   = $"{snap.AltitudePressure:F0}";
            HeaderSpeed = $"{snap.IASKts:F0}";
            HeaderPhase = snap.Phase.ToString().ToUpper();
            Map.UpdateTelemetry(snap);
        });

    private void OnFlightCompleted(object? _, FlightRecord __) =>
        Application.Current.Dispatcher.Invoke(() =>
        {
            _ = Dashboard.RefreshAsync();
            HeaderDep = HeaderArr = HeaderAlt = HeaderSpeed = "----";
            HeaderPhase = "PARKED";
        });

    private void OnAlertRaised(object? _, AlertNotification alert) =>
        Application.Current.Dispatcher.Invoke(() =>
        {
            ActiveAlerts.Insert(0, alert);
            if (alert.ExpiresAt.HasValue)
            {
                var ms = (int)Math.Max(0, (alert.ExpiresAt.Value - DateTime.UtcNow).TotalMilliseconds);
                Task.Delay(ms).ContinueWith(_ =>
                    Application.Current.Dispatcher.BeginInvoke(() => ActiveAlerts.Remove(alert)));
            }
        });

    private void OnLandingResultReady(object? _, LandingResult result) =>
        Application.Current.Dispatcher.Invoke(() =>
        {
            LandingAnalysis.AddLanding(result);
            NavigateToLanding();
        });

    public void SetWindowHandle(IntPtr hwnd)
    {
        _simConnect.SetWindowHandle(hwnd);
        _pttInput.SetWindowHandle(hwnd);
    }
    public void OnWindowMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l)
    {
        if (msg == 0x0402) _simConnect.HandleSimConnectMessage();
    }
}
