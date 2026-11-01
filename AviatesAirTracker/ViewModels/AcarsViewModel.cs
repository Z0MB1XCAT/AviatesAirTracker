using System.Collections.ObjectModel;
using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Models;
using AviatesAirTracker.Platform;
using AviatesAirTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AviatesAirTracker.ViewModels;

/// <summary>
/// Full data/logic port of Acars.razor's @code block (~40 fields, ~35 methods), matching
/// MapViewModel's and MessagesViewModel's shape (CommunityToolkit.Mvvm ObservableObject,
/// constructor DI, IUiDispatcher for cross-thread marshaling instead of raw DispatcherQueue
/// calls). AcarsPage.xaml/.xaml.cs (Tasks 3-4 of the same plan) is mostly XAML + thin event
/// wiring on top of this.
///
/// Deliberate deviations from a literal 1:1 port, each called out where implemented:
///
/// 1. PilotHubViewModel is NOT a constructor dependency (excluded per the implementation plan's
///    own Task 2 step 1: "minus IJSRuntime/Nav/PilotHubViewModel (page-level concerns, not
///    ViewModel state)"). The legacy page's rank-up detection (_rankBefore = PilotHub.PilotRank,
///    reacting to PilotHub.DataRefreshed) therefore can't live here. Instead: RankBeforeSnapshot
///    is a plain settable property the page populates from its own PilotHubViewModel reference
///    right when it forwards Session.FlightCompleted, and NotifyRankRefreshed(currentRank) is
///    called by the page from its own PilotHub.DataRefreshed handler. This reproduces the exact
///    same before/after comparison, just fed from outside instead of owned internally.
///
/// 2. The legacy METAR 10-minute auto-refresh and the 30-second zulu clock were both
///    System.Threading.Timer instances owned by the Razor component. Every native page in this
///    migration keeps its DispatcherTimer instances in page code-behind (see RoutesPage.xaml.cs's
///    400ms search-debounce timer -- the only other timer precedent in this codebase, and the one
///    the design spec explicitly calls out); no ViewModel anywhere owns a DispatcherTimer, and
///    IUiDispatcher exists specifically so ViewModels never need a UI-thread timer type. This
///    ViewModel therefore exposes RefreshMetarsAsync() (the fetch logic + state) for the page's
///    own DispatcherTimer to call every 10 minutes, and does not implement the zulu clock at all
///    (it is pure UI chrome with no other consumer -- page-owned per the design spec's own
///    Threading section, which lists "the METAR-refresh timer, the zulu-clock timer" as page-
///    level DispatcherQueue.TryEnqueue sources, not ViewModel state).
///
/// 3. Colors are exposed as hex strings (e.g. VsColorHex, XtkColorHex), never WinUI Brush
///    objects -- mirrors MapViewModel.PhaseColor exactly, keeping this ViewModel free of any UI
///    framework dependency. The hex values reuse the literal fallbacks already hardcoded in the
///    legacy inline styles (var(--green, #22c55e) etc.); where the legacy had no literal fallback
///    (var(--text-primary), var(--text-muted)) the app's dark-theme app.css values are used, since
///    this app has no runtime light/dark toggle for native pages.
///
/// 4. _pirepCopied (a transient "just copied" flash) and the NOTAMs/OFP collapse booleans stay
///    page-local, matching the design spec's own carve-out for "collapsed-panel booleans, modal-
///    open booleans" -- _pirepCopied is the same kind of ephemeral UI-timing state, not app data.
///    BuildPirepText() supplies the string; the page owns the clipboard write + flash timing.
///
/// 5. BookingService is accepted as a constructor dependency per the plan's literal Task 2 step 1
///    signature, but the "Active booking banner" itself is a page-level display concern per the
///    design spec's own Task 3 item 3 (not listed anywhere in the design spec's itemized
///    AcarsViewModel checklist) -- exposed here only as a thin ActiveBooking passthrough so the
///    dependency is genuinely used rather than dead-stored.
/// </summary>
public partial class AcarsViewModel : ObservableObject
{
    // ── Brand hex constants (see deviation #3 above) ─────────────
    private const string GreenHex = "#22C55E";
    private const string AmberHex = "#F59E0B";
    private const string CyanHex = "#06B6D4";
    private const string RedHex = "#EF4444";
    private const string TextPrimaryHex = "#F1F5F9";
    private const string TextMutedHex = "#64748B";

    // ── Dependencies ──────────────────────────────────────────────
    private readonly FlightSessionManager _session;
    private readonly SimConnectManager _simConnect;
    private readonly SettingsService _settings;
    private readonly SimBriefService _simBriefSvc;
    private readonly RouteTracker _routeTracker;
    private readonly BookingService _booking;
    private readonly FlightPhaseDetector _phaseDetector;
    private readonly AcarsPositionService _posService;
    private readonly AviatesBackendClient _backend;
    private readonly HoppiesAcarsOrchestrator _hoppiesOrchestrator;
    private readonly FlightTrailService _flightTrail;
    private readonly IUiDispatcher _ui;

    /// <summary>Exposed so the page can read Settings.Settings.X directly (SimBriefUsername,
    /// AcarsKey, FriendCode, AutoPirepIntervalMinutes, ...), mirroring Acars.razor's own
    /// @inject SettingsService Settings ad hoc access pattern rather than duplicating every small
    /// settings-derived display value as its own ObservableProperty here.</summary>
    public SettingsService Settings => _settings;

    /// <summary>Thin passthrough -- see deviation #5 above.</summary>
    public FlightBooking? ActiveBooking => _booking.ActiveBooking;

    // ── Internal, non-bindable state ─────────────────────────────
    private TelemetrySnapshot? _snap;
    private AircraftIdentification _aircraft;
    private bool _autoImportDone;
    private int _tickCount = 4; // matches MapViewModel.UpdateTelemetry's own seed so the first tick passes
    private int _lastTrailPointCount = -1;

    // ── Connection / session state ───────────────────────────────
    [ObservableProperty] private SimConnectionStatus _simStatus = SimConnectionStatus.Disconnected;
    [ObservableProperty] private FlightSessionState _sessionState = FlightSessionState.Idle;
    [ObservableProperty] private bool _connectingInProgress;

    public string BannerTitle => SimStatus switch
    {
        SimConnectionStatus.Connected when SessionState != FlightSessionState.Idle => "ACARS Live: Flight In Progress",
        SimConnectionStatus.Connected  => "ACARS Online: Sim Connected",
        SimConnectionStatus.Connecting => "Connecting to MSFS...",
        SimConnectionStatus.Error      => "ACARS Error: Connection Failed",
        _                               => "ACARS Offline: MSFS Not Connected"
    };

    public string BannerDesc => SimStatus switch
    {
        SimConnectionStatus.Connected when SessionState != FlightSessionState.Idle =>
            $"Recording flight data in real-time. Session state: {SessionState}.",
        SimConnectionStatus.Connected =>
            "Microsoft Flight Simulator is connected. Start your engines to begin a tracked flight.",
        SimConnectionStatus.Connecting =>
            "Attempting to connect to Microsoft Flight Simulator. Make sure MSFS is running.",
        SimConnectionStatus.Error =>
            "Connection failed. The app will retry automatically every 5 seconds.",
        _ => "Launch Microsoft Flight Simulator to begin automatic ACARS tracking. All flight data will be recorded in real-time."
    };

    public string DotColorHex => SimStatus switch
    {
        SimConnectionStatus.Connected when SessionState != FlightSessionState.Idle => GreenHex,
        SimConnectionStatus.Connected  => CyanHex,
        SimConnectionStatus.Connecting => AmberHex,
        SimConnectionStatus.Error      => RedHex,
        _                               => TextMutedHex
    };

    partial void OnSimStatusChanged(SimConnectionStatus value)
    {
        OnPropertyChanged(nameof(BannerTitle));
        OnPropertyChanged(nameof(BannerDesc));
        OnPropertyChanged(nameof(DotColorHex));
    }

    partial void OnSessionStateChanged(FlightSessionState value)
    {
        OnPropertyChanged(nameof(BannerTitle));
        OnPropertyChanged(nameof(BannerDesc));
        OnPropertyChanged(nameof(DotColorHex));
    }

    // ── Aircraft identification strip ────────────────────────────
    [ObservableProperty] private bool _hasAircraftIdent;
    [ObservableProperty] private string _atcModel = "";
    [ObservableProperty] private string _atcId = "";
    [ObservableProperty] private string _atcFlightNumber = "";
    [ObservableProperty] private string _gpsActiveLeg = "";
    [ObservableProperty] private string _coordsText = "";

    // ── Instrument row ────────────────────────────────────────────
    [ObservableProperty] private string _altText = "—";
    [ObservableProperty] private string _iasText = "—";
    [ObservableProperty] private string _vsText = "—";
    [ObservableProperty] private string _vsColorHex = TextMutedHex;
    [ObservableProperty] private string _hdgText = "—";
    [ObservableProperty] private string _gsText = "—";
    [ObservableProperty] private string _machText = "—";
    [ObservableProperty] private string _fuelText = "—";

    // ── Flight progress / ETA strip ──────────────────────────────
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _enRouteText = "—:——";
    [ObservableProperty] private string _distFlownText = "0 nm";
    [ObservableProperty] private string _distRemainingText = "—";
    [ObservableProperty] private string _etaText = "—";
    [ObservableProperty] private bool _showXtk;
    [ObservableProperty] private string _xtkColorHex = TextMutedHex;
    [ObservableProperty] private string _xtkLabelText = "—";
    [ObservableProperty] private string _cruiseLabel = "—";
    [ObservableProperty] private string _cruiseColorHex = TextMutedHex;

    // ── SimBrief flight-plan card ────────────────────────────────
    [ObservableProperty] private SimBriefFlightPlan? _flightPlan;
    [ObservableProperty] private bool _loadingSimBrief;
    [ObservableProperty] private string? _simBriefError;

    // ── METAR cards ───────────────────────────────────────────────
    [ObservableProperty] private string? _depMetar;
    [ObservableProperty] private string? _arrMetar;
    [ObservableProperty] private string? _altMetar;
    [ObservableProperty] private DateTime? _metarRefreshedAt;
    [ObservableProperty] private bool _metarRefreshing;

    // ── PIREP / position report panel ────────────────────────────
    [ObservableProperty] private string _pirepRemarks = "";
    [ObservableProperty] private bool _pirepSubmitting;
    [ObservableProperty] private string? _pirepSubmitResult;
    public ObservableCollection<DateTime> PosReportHistory { get; } = [];

    // ── Flight event log ──────────────────────────────────────────
    public ObservableCollection<AcarsEventLogEntry> EventLog { get; } = [];

    // ── Hoppies ACARS log (seeded from orchestrator history, then live) ──
    public ObservableCollection<HoppiesLogEntry> HoppiesLog { get; } = [];

    // ── Flight completion overlay ────────────────────────────────
    [ObservableProperty] private bool _showCompletionPanel;
    [ObservableProperty] private FlightRecord? _completedRecord;
    [ObservableProperty] private string? _rankUp;

    /// <summary>Page-fed -- see deviation #1 above. Set by the page right when it snapshots its
    /// own PilotHubViewModel.PilotRank in response to Session.FlightCompleted.</summary>
    public string RankBeforeSnapshot { get; set; } = "";

    public AcarsViewModel(
        FlightSessionManager session,
        SimConnectManager simConnect,
        SettingsService settings,
        SimBriefService simBriefSvc,
        RouteTracker routeTracker,
        BookingService booking,
        FlightPhaseDetector phaseDetector,
        AcarsPositionService posService,
        AviatesBackendClient backend,
        HoppiesAcarsOrchestrator hoppiesOrchestrator,
        FlightTrailService flightTrail,
        IUiDispatcher ui)
    {
        _session = session;
        _simConnect = simConnect;
        _settings = settings;
        _simBriefSvc = simBriefSvc;
        _routeTracker = routeTracker;
        _booking = booking;
        _phaseDetector = phaseDetector;
        _posService = posService;
        _backend = backend;
        _hoppiesOrchestrator = hoppiesOrchestrator;
        _flightTrail = flightTrail;
        _ui = ui;

        // Initial synchronous snapshot -- mirrors Acars.razor's OnInitialized().
        SimStatus = _simConnect.ConnectionStatus;
        SessionState = _session.State;
        if (_session.LatestTelemetry is { } initialSnap) ApplyTelemetry(initialSnap);

        // Fix #1 from the design spec: seed the Hoppies log from the orchestrator's existing
        // 200-entry ring buffer (oldest-first) on construction, not only from future MessageSent
        // events -- previously a page reload during an active flight lost prior-session entries
        // even though the orchestrator still had them in memory. MessageLog is oldest->newest;
        // take the most recent up to 50 (legacy's own in-app display cap) and reverse so the
        // result is newest-first, matching the live handler's own Insert(0, entry) semantics.
        foreach (var entry in _hoppiesOrchestrator.MessageLog.TakeLast(50).Reverse())
            HoppiesLog.Add(entry);

        _simConnect.ConnectionStatusChanged += HandleConnectionStatusChanged;
        _simConnect.AircraftIdentReceived   += HandleAircraftIdentReceived;
        _session.SessionStateChanged        += HandleSessionStateChanged;
        _session.FlightCompleted            += HandleFlightCompleted;
        _phaseDetector.PhaseChanged         += HandlePhaseChanged;
        _posService.GateFired               += HandlePosGateFired;
        _hoppiesOrchestrator.MessageSent    += HandleHoppiesMessageSent;
    }

    // ── One-time async init (page calls from Loaded) ─────────────
    // Idempotent by design (null-check + _autoImportDone guard) -- safe to call more than once,
    // matching how MessagesViewModel.RefreshAllAsync() is safe to call repeatedly. Kept separate
    // from the constructor because ObservableObject constructors can't be async and this needs to
    // await disk I/O (Settings.LoadSimBriefPlanAsync) -- mirrors Acars.razor's OnInitializedAsync.
    public async Task InitializeAsync()
    {
        if (FlightPlan == null)
            FlightPlan = await _settings.LoadSimBriefPlanAsync();

        if (SimStatus == SimConnectionStatus.Connected
            && FlightPlan == null
            && !_autoImportDone
            && !string.IsNullOrWhiteSpace(_settings.Settings.SimBriefUsername))
        {
            _autoImportDone = true;
            await AutoImportAsync();
        }
    }

    // ── Connection ────────────────────────────────────────────────

    public void TryConnect()
    {
        var before = _simConnect.ConnectionStatus;
        ConnectingInProgress = true;
        _simConnect.TryConnect();
        // If TryConnect bailed out silently (guard hit), status didn't change and
        // HandleConnectionStatusChanged never ran -- clear the flag now.
        if (_simConnect.ConnectionStatus == before)
            ConnectingInProgress = false;
    }

    private void HandleConnectionStatusChanged(object? sender, SimConnectionStatus status)
    {
        // IUiDispatcher.Invoke/BeginInvoke only accept a synchronous Action -- passing an async
        // lambda directly makes it an async-void delegate whose exceptions can't be observed by
        // Invoke's own try/catch (that only wraps the synchronous prefix up to the first await;
        // see DispatcherQueueUiDispatcher.Invoke). BeginInvoke is used here (not Invoke) since
        // this is genuinely fire-and-forget -- nothing awaits HandleConnectionStatusChanged's
        // completion -- and the async continuation is wrapped in its own try/catch, matching the
        // established, documented-safe pattern for exactly this shape elsewhere in the codebase
        // (MessagesPage.xaml.cs's OnNewMessageReceived: "an unhandled exception inside an
        // async-void TryEnqueue callback would otherwise crash the process -- log and swallow").
        _ui.BeginInvoke(() =>
        {
            SimStatus = status;
            ConnectingInProgress = false;

            // Auto-import SimBrief OFP once per MSFS connection, if no plan is loaded yet.
            if (status == SimConnectionStatus.Connected
                && !_autoImportDone
                && FlightPlan == null
                && !string.IsNullOrWhiteSpace(_settings.Settings.SimBriefUsername))
            {
                _autoImportDone = true;
                _ = AutoImportSafeAsync();
            }
        });
    }

    private async Task AutoImportSafeAsync()
    {
        try
        {
            await AutoImportAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[Acars] Auto-import of SimBrief OFP failed");
        }
    }

    private void HandleAircraftIdentReceived(object? sender, AircraftIdentification ident)
    {
        _ui.Invoke(() =>
        {
            _aircraft = ident;
            HasAircraftIdent = true;
            AtcModel = ident.ATCModel;
            AtcId = ident.ATCId;
            AtcFlightNumber = ident.ATCFlightNumber;
            GpsActiveLeg = ident.GPSActiveLeg;
        });
    }

    private void HandleSessionStateChanged(object? sender, FlightSessionState state)
    {
        _ui.Invoke(() => SessionState = state);
    }

    // ── Flight event log ──────────────────────────────────────────

    private void HandlePhaseChanged(object? sender, FlightPhaseChangedEvent e)
    {
        _ui.Invoke(() => EventLog.Add(new AcarsEventLogEntry(DateTime.UtcNow, $"Phase: {e.Previous} → {e.Current}")));
    }

    public void ClearEventLog() => EventLog.Clear();

    // ── Flight phase ladder ───────────────────────────────────────

    /// <summary>Mirrors Acars.razor's PhaseStatus(phaseName) exactly. ChipClass is one of
    /// "offline"/"active"/"done"; TextColorHex is the matching text color.</summary>
    public (string ChipClass, string TextColorHex) PhaseStatus(string phaseName)
    {
        var phaseOrder = new[] { "Parked", "Taxi", "Climb", "Cruise", "Descent", "Approach", "Landing" };
        var currentIdx = SessionState switch
        {
            FlightSessionState.Idle or FlightSessionState.PreFlight => 0,
            FlightSessionState.Taxiing => 1,
            FlightSessionState.Airborne when _snap?.Phase is FlightPhase.TopOfDescent or FlightPhase.Descent => 4,
            FlightSessionState.Airborne when _snap?.Phase is FlightPhase.Cruise => 3,
            FlightSessionState.Airborne => 2,
            FlightSessionState.OnApproach => 5,
            FlightSessionState.Landed or FlightSessionState.Complete => 6,
            _ => -1
        };

        var idx = Array.IndexOf(phaseOrder, phaseName);
        if (currentIdx == -1 || idx > currentIdx) return ("offline", TextMutedHex);
        if (idx == currentIdx) return ("active", TextPrimaryHex);
        return ("done", TextMutedHex);
    }

    // ── Position reports / PIREP ─────────────────────────────────

    private void HandlePosGateFired()
    {
        _ui.Invoke(() =>
        {
            if (PosReportHistory.Count >= 10) PosReportHistory.RemoveAt(0);
            PosReportHistory.Add(DateTime.UtcNow);
        });
    }

    public async Task SubmitPositionReportAsync()
    {
        if (_snap == null || string.IsNullOrWhiteSpace(_settings.Settings.AcarsKey)) return;

        PirepSubmitting = true;
        PirepSubmitResult = null;
        try
        {
            await _backend.SendPositionReportAsync(
                _snap.Latitude, _snap.Longitude, _snap.AltitudeMSL,
                (int)_snap.GroundSpeedKts, (int)_snap.Raw.HeadingTrue, SessionState.ToString(),
                _settings.Settings.AcarsKey);
            PirepSubmitResult = "✓ Sent";
            if (PosReportHistory.Count >= 10) PosReportHistory.RemoveAt(0);
            PosReportHistory.Add(DateTime.UtcNow);
        }
        catch
        {
            PirepSubmitResult = "Failed";
        }
        finally
        {
            PirepSubmitting = false;
        }

        await Task.Delay(2500);
        PirepSubmitResult = null;
    }

    /// <summary>Builds the PIREP clipboard text. Clipboard write + the transient "copied" flash
    /// are page-level (see deviation #4 above).</summary>
    public string BuildPirepText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"PIREP {DateTime.UtcNow:HH:mm}Z");

        if (FlightPlan is { } plan)
        {
            sb.AppendLine($"Route: {plan.DepartureICAO} → {plan.ArrivalICAO}");
            var acft = plan.AircraftType;
            var reg = plan.AircraftRegistration;
            if (!string.IsNullOrWhiteSpace(acft))
                sb.AppendLine($"Aircraft: {acft}{(string.IsNullOrWhiteSpace(reg) ? "" : " " + reg)}");
        }

        if (_snap is { } snap)
        {
            sb.AppendLine($"Position: {CoordsText}");
            sb.AppendLine($"Altitude: {snap.AltitudeMSL:F0} ft MSL");
            sb.AppendLine($"IAS: {snap.IASKts:F0} kts  GS: {snap.GroundSpeedKts:F0} kts");
            sb.AppendLine($"Fuel: {snap.Raw.FuelTotalLbs:F0} lbs remaining");
        }

        if (EtaText != "—") sb.AppendLine($"ETA: {EtaText}");

        if (!string.IsNullOrWhiteSpace(PirepRemarks))
            sb.AppendLine($"Remarks: {PirepRemarks}");

        return sb.ToString();
    }

    // ── Hoppies ACARS log ─────────────────────────────────────────

    private void HandleHoppiesMessageSent(object? sender, HoppiesLogEntry entry)
    {
        _ui.Invoke(() =>
        {
            HoppiesLog.Insert(0, entry);
            if (HoppiesLog.Count > 50) HoppiesLog.RemoveAt(HoppiesLog.Count - 1);
        });
    }

    // ── SimBrief import / clear ───────────────────────────────────

    /// <summary>Fetches (but does not apply/persist) the latest OFP. The page calls this, then
    /// WouldWipeTrail(...), then ApplyImportedPlanAsync(...) -- see the design spec's explicit
    /// separation of "the ViewModel decides [whether to wipe], the page prompts [the dialog]".</summary>
    public async Task<(SimBriefFlightPlan? Plan, string? Error)> FetchSimBriefPlanAsync()
    {
        var username = _settings.Settings.SimBriefUsername;
        if (string.IsNullOrWhiteSpace(username)) return (null, null);

        LoadingSimBrief = true;
        SimBriefError = null;
        try
        {
            var plan = await _simBriefSvc.FetchLatestOFPAsync(username);
            if (plan == null)
            {
                SimBriefError = "No OFP found for this SimBrief username.";
                return (null, SimBriefError);
            }
            return (plan, null);
        }
        catch
        {
            SimBriefError = "Failed to fetch SimBrief OFP. Check your username in Settings and try again.";
            return (null, SimBriefError);
        }
        finally
        {
            LoadingSimBrief = false;
        }
    }

    /// <summary>
    /// Decides whether importing <paramref name="newPlan"/> would require wiping the persistent
    /// flight trail. True only when BOTH the departure+arrival airport pair differs from the
    /// currently-loaded plan AND the flight number differs (matched independently -- either
    /// matching alone is enough to import silently with no wipe/warning). If no plan was active
    /// before this import (SimBriefService.CurrentPlan is null), there is nothing to compare
    /// against, so this returns false -- nothing to wipe.
    ///
    /// Edge case: if both plans' FlightNumber are empty (OFP lacked the icao_airline/flight_number
    /// fields), they compare as equal, so "same flight number" is satisfied and the trail is never
    /// wiped via that leg of the comparison alone -- a direct, literal reading of "matched
    /// independently"; there is no signal here to conclude two flight-number-less plans are
    /// different flights.
    /// </summary>
    public bool WouldWipeTrail(SimBriefFlightPlan newPlan)
    {
        var current = _simBriefSvc.CurrentPlan;
        if (current == null) return false;

        bool samePair = string.Equals(current.DepartureICAO, newPlan.DepartureICAO, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(current.ArrivalICAO, newPlan.ArrivalICAO, StringComparison.OrdinalIgnoreCase);
        bool sameFlightNumber = string.Equals(current.FlightNumber, newPlan.FlightNumber, StringComparison.OrdinalIgnoreCase);

        return !samePair && !sameFlightNumber;
    }

    /// <summary>The "import-continuation" the page calls after fetching + (if needed) confirming
    /// the wipe-warning dialog. Does NOT touch FlightTrailService -- the ViewModel only reads the
    /// trail (see class doc, deviation-free design decision); the page calls
    /// FlightTrailService.Clear() itself, separately, only on confirm.</summary>
    public async Task ApplyImportedPlanAsync(SimBriefFlightPlan plan)
    {
        FlightPlan = plan;
        await _settings.SaveSimBriefPlanAsync(plan);

        // Wire into the active flight session so Discord Rich Presence picks up the correct
        // DEP/ARR on its next update cycle.
        _session.AssignSimBriefPlan(plan);
        _simBriefSvc.NotifyPlanLoaded(plan);

        // Seed METAR display from the OFP; live refresh is driven by the page's own
        // DispatcherTimer calling RefreshMetarsAsync() (see deviation #2 above).
        DepMetar = plan.DepartureMetar;
        ArrMetar = plan.ArrivalMetar;
        AltMetar = null;
        MetarRefreshedAt = null;

        RecomputeFlightProgress();
    }

    private async Task AutoImportAsync()
    {
        var (plan, _) = await FetchSimBriefPlanAsync();
        if (plan != null)
        {
            // Auto-import only ever fires when FlightPlan was null, so SimBriefService.CurrentPlan
            // is null too -- WouldWipeTrail is guaranteed false here, no dialog needed.
            await ApplyImportedPlanAsync(plan);
        }
    }

    public async Task ClearPlanAsync()
    {
        FlightPlan = null;
        SimBriefError = null;
        _simBriefSvc.ClearPlan();
        await _settings.SaveSimBriefPlanAsync(null);
    }

    // ── METAR refresh ─────────────────────────────────────────────

    public async Task RefreshMetarsAsync()
    {
        if (FlightPlan == null) return;

        MetarRefreshing = true;
        var dep = await _simBriefSvc.FetchMetarAsync(FlightPlan.DepartureICAO);
        var arr = await _simBriefSvc.FetchMetarAsync(FlightPlan.ArrivalICAO);
        var alt = !string.IsNullOrWhiteSpace(FlightPlan.AlternateICAO)
            ? await _simBriefSvc.FetchMetarAsync(FlightPlan.AlternateICAO)
            : null;

        DepMetar = dep ?? FlightPlan.DepartureMetar;
        ArrMetar = arr ?? FlightPlan.ArrivalMetar;
        AltMetar = alt;
        MetarRefreshedAt = DateTime.Now;
        MetarRefreshing = false;
    }

    // ── End flight ────────────────────────────────────────────────

    public void EndFlight() => _session.EndFlightManually();

    // ── Flight completion overlay ────────────────────────────────

    private void HandleFlightCompleted(object? sender, FlightRecord record)
    {
        _ui.Invoke(() =>
        {
            CompletedRecord = record;
            RankUp = null; // rank badge updates once the page's PilotHub.DataRefreshed fires
            FlightPlan = null; // clear SimBrief display immediately
            _ = _settings.SaveSimBriefPlanAsync(null); // one-shot disk clear, fire-and-forget
            ShowCompletionPanel = true;
        });
    }

    /// <summary>Called by the page from its own PilotHubViewModel.DataRefreshed handler -- see
    /// deviation #1 above.</summary>
    public void NotifyRankRefreshed(string currentRank)
    {
        if (CompletedRecord == null) return;
        RankUp = currentRank != RankBeforeSnapshot ? currentRank : null;
    }

    public void DismissCompletionPanel()
    {
        ShowCompletionPanel = false;
        CompletedRecord = null;
        RankUp = null;
    }

    public static string FormatVS(FlightRecord r)
    {
        if (r.PrimaryLanding == null) return "—";
        var vs = (int)r.PrimaryLanding.VerticalSpeedFPM;
        return vs >= 0 ? $"+{vs} fpm" : $"{vs} fpm";
    }

    public static string LandingVsClass(FlightRecord r)
    {
        if (r.PrimaryLanding == null) return "neutral";
        var abs = Math.Abs(r.PrimaryLanding.VerticalSpeedFPM);
        return abs <= 150 ? "green" : abs <= 300 ? "amber" : "red";
    }

    public static string FormatAirTime(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m";

    // ── Small formatting helpers ──────────────────────────────────

    public static string FormatETE(int minutes)
    {
        if (minutes <= 0) return "—";
        var h = minutes / 60;
        var m = minutes % 60;
        return h > 0 ? $"{h}h {m:D2}m" : $"{m}m";
    }

    public static string MaskAcarsKey(string key)
    {
        if (key.Length <= 8) return new string('•', key.Length);
        return new string('•', key.Length - 4) + key[^4..];
    }

    private static string FormatCoords(TelemetrySnapshot? snap)
    {
        if (snap == null) return "";
        var lat = snap.Latitude;
        var lon = snap.Longitude;
        var latDir = lat >= 0 ? "N" : "S";
        var lonDir = lon >= 0 ? "E" : "W";
        return $"{latDir}{Math.Abs(lat):F4}°  {lonDir}{Math.Abs(lon):F4}°";
    }

    // ── Flight trail (read-only from here; see class doc) ────────

    /// <summary>Mirrors MapViewModel.GetPathIfChanged()'s exact shape -- returns null when the
    /// trail's point count hasn't changed since the last call, so the page's mini-map only rebuilds
    /// its colored path when there's genuinely new data.</summary>
    public IReadOnlyList<(double Lat, double Lon, double AltFt)>? GetTrailIfChanged()
    {
        var count = _flightTrail.RecordedPointCount;
        if (count == _lastTrailPointCount) return null;
        _lastTrailPointCount = count;
        return _flightTrail.GetTrailForRendering(2000);
    }

    // ── Telemetry (20Hz -> 4Hz decimation, page calls after its own dispatcher marshaling) ──

    /// <summary>The pre-flight readiness checklist from the current sim, booking and plan state.</summary>
    public IReadOnlyList<ReadinessItem> GetReadiness()
    {
        var booking = _booking.ActiveBooking;
        var plan = FlightPlan;
        var snap = _snap ?? _session.LatestTelemetry;
        bool hasOrigin = plan is { DepartureLat: not 0 } || plan is { DepartureLon: not 0 };
        return PreflightReadiness.Evaluate(new ReadinessInputs(
            SimConnected: SimStatus == SimConnectionStatus.Connected,
            BookingActivated: booking is { IsActivated: true },
            BookingOrigin: booking?.OriginIata,
            BookingDestination: booking?.DestIata,
            BookingAircraftType: booking?.AircraftType,
            PlanOrigin: plan?.DepartureICAO,
            PlanDestination: plan?.ArrivalICAO,
            PlanOriginLat: hasOrigin ? plan!.DepartureLat : null,
            PlanOriginLon: hasOrigin ? plan!.DepartureLon : null,
            AircraftLat: snap?.Latitude,
            AircraftLon: snap?.Longitude,
            OnGround: snap?.IsOnGround,
            EnginesRunning: snap?.EnginesRunning,
            SimAircraftTitle: _aircraft.Title,
            SimAircraftModel: _aircraft.ATCModel));
    }

    public void UpdateTelemetry(TelemetrySnapshot snap)
    {
        if (++_tickCount % 5 != 0) return;
        ApplyTelemetry(snap);
    }

    private void ApplyTelemetry(TelemetrySnapshot snap)
    {
        _snap = snap;

        AltText = $"{snap.AltitudeMSL:F0}";
        IasText = $"{snap.IASKts:F0}";
        VsText = $"{(snap.VerticalSpeedFPM >= 0 ? "+" : "")}{snap.VerticalSpeedFPM:F0}";
        VsColorHex = snap.VerticalSpeedFPM switch
        {
            > 200   => GreenHex,
            < -1500 => AmberHex,
            < -200  => CyanHex,
            _       => TextPrimaryHex
        };
        HdgText = $"{snap.Raw.HeadingMagnetic:F0}";
        GsText = $"{snap.GroundSpeedKts:F0}";
        MachText = $"{snap.Raw.Mach:F2}";
        FuelText = $"{snap.Raw.FuelTotalLbs:F0}";
        CoordsText = FormatCoords(snap);

        RecomputeFlightProgress();
    }

    // ── Progress / ETA / cross-track / cruise-compliance recompute ──

    private void RecomputeFlightProgress()
    {
        var plan = FlightPlan;
        var snap = _snap;

        if (plan == null)
        {
            ProgressPercent = 0;
            EnRouteText = "—:——";
            DistFlownText = $"{_routeTracker.TotalDistanceNm:F0} nm";
            DistRemainingText = "—";
            EtaText = "—";
            ShowXtk = false;
            XtkColorHex = TextMutedHex;
            XtkLabelText = "—";
            CruiseLabel = "—";
            CruiseColorHex = TextMutedHex;
            return;
        }

        ProgressPercent = plan.PlannedDistanceNm > 0
            ? Math.Clamp(_routeTracker.TotalDistanceNm / plan.PlannedDistanceNm * 100, 0, 100)
            : 0;

        var takeoff = _session.CurrentFlight?.TakeoffTime;
        if (takeoff == null || takeoff == DateTime.MinValue)
        {
            EnRouteText = "—:——";
        }
        else
        {
            var elapsed = DateTime.UtcNow - takeoff.Value.ToUniversalTime();
            EnRouteText = elapsed.TotalSeconds < 0 ? "—:——" : $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}";
        }

        DistFlownText = $"{_routeTracker.TotalDistanceNm:F0} nm";

        double distRemaining = plan.PlannedDistanceNm > 0
            ? Math.Max(0, plan.PlannedDistanceNm - _routeTracker.TotalDistanceNm)
            : 0;
        DistRemainingText = distRemaining > 0.5 ? $"{distRemaining:F0} nm" : "—";

        if (snap == null || snap.GroundSpeedKts < 50 || distRemaining < 0.5)
        {
            EtaText = "—";
        }
        else
        {
            var etaMin = distRemaining / snap.GroundSpeedKts * 60;
            EtaText = DateTime.UtcNow.AddMinutes(etaMin).ToString("HH:mm") + "Z";
        }

        ShowXtk = plan.Waypoints.Count >= 2 && snap != null;
        if (ShowXtk)
        {
            var xtk = ComputeCrossTrackNm(plan, snap!);
            var abs = Math.Abs(xtk);
            XtkColorHex = abs > 8 ? RedHex : abs > 3 ? AmberHex : GreenHex;
            XtkLabelText = abs < 0.1 ? "On Track" : $"{abs:F1} nm {(xtk > 0 ? "R" : "L")}";
        }
        else
        {
            XtkColorHex = TextMutedHex;
            XtkLabelText = "—";
        }

        if (plan.CruiseAltitudeFt <= 0 || snap == null || snap.Phase != FlightPhase.Cruise)
        {
            CruiseLabel = "—";
            CruiseColorHex = TextMutedHex;
        }
        else
        {
            var deviation = snap.AltitudeMSL - plan.CruiseAltitudeFt;
            var abs = Math.Abs(deviation);
            if (abs < 300)
            {
                CruiseLabel = "On FL";
                CruiseColorHex = GreenHex;
            }
            else
            {
                var sign = deviation > 0 ? "+" : "";
                CruiseLabel = $"{sign}{(int)deviation} ft";
                CruiseColorHex = abs > 1000 ? RedHex : AmberHex;
            }
        }
    }

    // ── Cross-track deviation (great-circle geometry, no existing service home) ──
    // XTK = asin( sin(d_AP/R) · sin(θ_AP − θ_AB) ) · R   (signed nm), ported verbatim from
    // Acars.razor:1339-1369.

    private static double ComputeCrossTrackNm(SimBriefFlightPlan plan, TelemetrySnapshot snap)
    {
        var wps = plan.Waypoints
            .Where(w => w.Latitude != 0 || w.Longitude != 0)
            .OrderBy(w => w.SequenceNumber)
            .ToList();
        if (wps.Count < 2) return 0;
        int nextIdx = wps.FindIndex(w => !w.IsPassed);
        if (nextIdx < 1) nextIdx = 1;
        var from = wps[nextIdx - 1];
        var to = wps[nextIdx];
        return XtkCalc(from.Latitude, from.Longitude, to.Latitude, to.Longitude, snap.Latitude, snap.Longitude);
    }

    private static double XtkCalc(double lat1, double lon1,
                                   double lat2, double lon2,
                                   double latP, double lonP)
    {
        const double R = 3440.065;
        double d13 = Haversine01(lat1, lon1, latP, lonP); // radians
        double θ13 = BearingRad(lat1, lon1, latP, lonP);
        double θ12 = BearingRad(lat1, lon1, lat2, lon2);
        return Math.Asin(Math.Sin(d13) * Math.Sin(θ13 - θ12)) * R;
    }

    private static double Haversine01(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = (lat2 - lat1) * Math.PI / 180;
        double dLon = (lon2 - lon1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a)); // radians
    }

    private static double BearingRad(double lat1, double lon1, double lat2, double lon2)
    {
        double dLon  = (lon2 - lon1) * Math.PI / 180;
        double lat1R = lat1 * Math.PI / 180;
        double lat2R = lat2 * Math.PI / 180;
        double y = Math.Sin(dLon) * Math.Cos(lat2R);
        double x = Math.Cos(lat1R) * Math.Sin(lat2R) -
                   Math.Sin(lat1R) * Math.Cos(lat2R) * Math.Cos(dLon);
        return Math.Atan2(y, x);
    }
}

/// <summary>
/// One entry in the ACARS page's flight event log (phase-change history). A plain mutable class,
/// not a positional record -- this SDK throws CS8852 when an init-only record's properties are
/// bound in any XAML ItemsControl (a known, repeated gotcha in this migration), so every display-
/// row type in this codebase uses `{ get; set; }` instead.
/// </summary>
public class AcarsEventLogEntry
{
    public DateTime Time { get; set; }
    public string Description { get; set; } = "";

    public AcarsEventLogEntry(DateTime time, string description)
    {
        Time = time;
        Description = description;
    }
}
