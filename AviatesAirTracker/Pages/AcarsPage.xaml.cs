using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Core.Reference;
using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Services;
using AviatesAirTracker.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI;

namespace AviatesAirTracker.Pages;

/// <summary>
/// Pre-formatted display row for one Flight Events entry -- built from
/// <see cref="AcarsEventLogEntry"/> (a plain mutable class, not init-only, per its own doc comment
/// avoiding the CS8852 XamlTypeInfo.g.cs trap every ItemsControl row type in this codebase already
/// works around). A separate page-local type is needed anyway here, not because of CS8852, but to
/// pre-format the timestamp ("HH:mm:ssZ", matching Acars.razor's own @evtTime.ToString("HH:mm:ss")Z)
/// the same way DashboardPage.xaml.cs's own FlightDisplayRow/MonthlyActivityBarRow precompute
/// display strings rather than relying on classic {Binding} StringFormat (unsupported without a
/// Page.Resources converter entry, which risks the same XamlCompiler crash class this migration
/// avoids throughout).
/// </summary>
public sealed class AcarsEventLogDisplayRow
{
    public string TimeText { get; set; } = "";
    public string Description { get; set; } = "";
}

/// <summary>
/// Native reproduction of AviatesAirTracker.LegacyWeb\Pages\Acars.razor -- Task 3 of the ACARS
/// native-page plan ("skeleton"): banners, instrument row, progress strip, mini-map + sidebar,
/// event log. See AcarsPage.xaml's own header comment for the Task 3/4 scope boundary and the
/// AcarsFmc drop.
///
/// Threading: AcarsViewModel already owns its own SimConnect/FlightSessionManager/
/// FlightPhaseDetector subscriptions internally (marshaled onto the UI thread via its own
/// IUiDispatcher, see AcarsViewModel's class doc) and keeps its own properties current. This page
/// subscribes to the *same underlying service events* a second time -- not to re-derive VM state,
/// but because the page needs to know *when* to re-render its own TextBlocks/MapControl, which the
/// ViewModel (deliberately UI-framework-free, see AcarsViewModel deviation #2/#3) has no mechanism
/// to signal beyond its own property setters. Because AcarsViewModel subscribed to each of these
/// events first (at DI-singleton construction time, well before this page's Loaded ever runs), its
/// handler always finishes updating VM state before this page's own handler (subscribed later, so
/// invoked later per C#'s in-order multicast-delegate semantics) reads that state back out --
/// confirmed against AviatesAirTracker/Platform/DispatcherQueueUiDispatcher.cs: Invoke blocks
/// synchronously via ManualResetEventSlim, and BeginInvoke's DispatcherQueue.TryEnqueue is FIFO, so
/// either way VM's own queued/blocking update is guaranteed to run before this page's
/// DispatcherQueue.TryEnqueue callback (queued strictly after, since this page's subscription is
/// added strictly after) reads the refreshed properties.
/// </summary>
public sealed partial class AcarsPage : Page
{
    // Dark-theme hex values read directly from wwwroot/css/app.css's html[data-theme="dark"]
    // block -- same values every prior native page in this migration hardcodes (surface #0C111D,
    // surface-2 #121A2A, border rgba(255,255,255,0.08) = #14FFFFFF, text-primary #F1F5F9,
    // text-muted #64748B, accent #4D9BEC).
    private static readonly Brush TextPrimaryBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9));
    private static readonly Brush Surface2Brush = new SolidColorBrush(Color.FromArgb(0xFF, 0x12, 0x1A, 0x2A));
    // Active flight-phase row highlight -- Acars.razor's own inline style uses a literal
    // rgba(61,126,238,0.08) (the app's *previous* accent RGB, from before the accent hex was
    // updated app-wide); this uses the current accent (#4D9BEC) at the same 8% opacity instead of
    // carrying the stale magic number forward, matching every other native page's accent usage.
    private static readonly Brush ActivePhaseRowBrush = new SolidColorBrush(Color.FromArgb(0x14, 0x4D, 0x9B, 0xEC));

    private readonly AcarsViewModel _vm;
    private readonly FlightSessionManager _session;
    private readonly SimConnectManager _simConnect;
    private readonly FlightPhaseDetector _phaseDetector;
    private readonly BookingService _booking;

    private readonly NativeMapRenderer _renderer;
    private readonly Mapsui.UI.WinUI.MapControl _mapControl;

    // Page-local mirror of AcarsViewModel.EventLog, newest-first (the VM appends oldest-first via
    // .Add(), matching Acars.razor's own @code field -- the legacy page reverses at render time via
    // Enumerable.Reverse(_eventLog); this page reverses once per change instead, via
    // OnEventLogCollectionChanged inserting each new entry at index 0).
    private readonly ObservableCollection<AcarsEventLogDisplayRow> _eventLogRows = [];

    // CLAUDE.md: "Do not call StateHasChanged() on every 20Hz tick -- batch every 3-5 samples."
    // AcarsViewModel.UpdateTelemetry already does its own internal /5 decimation for its own
    // properties, but this page's own TextBlock/MapControl writes are separately decimated here to
    // the same ~4Hz cadence (this page reads snap.Latitude/Longitude/Raw.HeadingTrue directly for
    // the mini-map, which AcarsViewModel deliberately does not expose -- see class doc deviation
    // #3, "free of any UI framework dependency" -- so this page needs its own tick gate).
    private int _uiTickCount = 4; // matches AcarsViewModel/MapViewModel's own seed so the first tick passes

    private DispatcherTimer? _utcClockTimer;

    public AcarsPage()
    {
        InitializeComponent();

        _vm = App.GetService<AcarsViewModel>();
        _session = App.GetService<FlightSessionManager>();
        _simConnect = App.GetService<SimConnectManager>();
        _phaseDetector = App.GetService<FlightPhaseDetector>();
        _booking = App.GetService<BookingService>();

        EventLogItemsControl.ItemsSource = _eventLogRows;

        // Same construction pattern as MapPage.xaml.cs: NativeMapRenderer(map: null) creates and
        // owns its own Mapsui.Map, handed to the MapControl below to "pre-wire" it. This mini-map
        // only ever calls Init/UpdatePosition/UpdateFlightPathAltitude -- no SetPlannedRoute/
        // AddAirportMarker/UpsertOtherPilot, no PointerMoved/Info hit-testing (design spec: "this
        // mini-map does not need the MapControl.PointerMoved/Info wiring MapPage.xaml.cs has").
        _renderer = new NativeMapRenderer();
        _mapControl = new Mapsui.UI.WinUI.MapControl { Map = _renderer.Map };
        MiniMapHost.Children.Add(_mapControl);

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────────────────────

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _simConnect.ConnectionStatusChanged += OnSimConnectionStatusChanged;
        _session.SessionStateChanged += OnSessionStateChanged;
        _session.TelemetryUpdated += OnTelemetryUpdated;
        _phaseDetector.PhaseChanged += OnPhaseChanged;
        _vm.EventLog.CollectionChanged += OnEventLogCollectionChanged;

        var snap = _session.LatestTelemetry;
        _renderer.Init(snap?.Latitude ?? 0, snap?.Longitude ?? 0);
        if (snap is not null)
        {
            _renderer.UpdatePosition(snap.Latitude, snap.Longitude, snap.Raw.HeadingTrue);
            var trail = _vm.GetTrailIfChanged();
            if (trail is not null)
                _renderer.UpdateFlightPathAltitude(trail);
        }

        _utcClockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _utcClockTimer.Tick += (_, _) => UpdateUtcClock();
        _utcClockTimer.Start();
        UpdateUtcClock();

        // Mirrors Acars.razor's OnAfterRenderAsync(firstRender): render whatever state already
        // exists the moment this page opens, not just future updates.
        SeedEventLogMirror();
        RefreshAllSections();

        try
        {
            await _vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Acars] InitializeAsync failed");
        }

        // InitializeAsync may have loaded a persisted plan or completed an auto-import above --
        // re-render the sections whose content depends on FlightPlan now that it may have changed.
        RefreshAllSections();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _simConnect.ConnectionStatusChanged -= OnSimConnectionStatusChanged;
        _session.SessionStateChanged -= OnSessionStateChanged;
        _session.TelemetryUpdated -= OnTelemetryUpdated;
        _phaseDetector.PhaseChanged -= OnPhaseChanged;
        _vm.EventLog.CollectionChanged -= OnEventLogCollectionChanged;

        _utcClockTimer?.Stop();
        _utcClockTimer = null;
    }

    private void RefreshAllSections()
    {
        UpdateConnectionBanner();
        UpdateBookingBanner();
        UpdateAircraftIdStrip();
        UpdateInstrumentRow();
        UpdateProgressStrip();
        UpdatePhaseLadder();
        UpdateSimBriefCard();
        UpdateAcarsKeyCard();
    }

    // ── SimConnectManager.ConnectionStatusChanged / FlightSessionManager.SessionStateChanged /
    //    FlightSessionManager.TelemetryUpdated / FlightPhaseDetector.PhaseChanged ──────────────
    // Every handler wraps its body in DispatcherQueue.TryEnqueue with its own try/catch inside the
    // callback -- an unhandled exception inside an async-void-shaped TryEnqueue callback would
    // otherwise crash the process, same defensive posture MessagesPage.xaml.cs's own
    // OnNewMessageReceived already established for this exact shape.

    private void OnSimConnectionStatusChanged(object? sender, SimConnectionStatus status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                UpdateConnectionBanner();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Acars] Connection banner refresh failed");
            }
        });
    }

    private void OnSessionStateChanged(object? sender, FlightSessionState state)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                UpdateConnectionBanner();
                UpdatePhaseLadder();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Acars] Session-state refresh failed");
            }
        });
    }

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEvent e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                UpdatePhaseLadder();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Acars] Phase ladder refresh failed");
            }
        });
    }

    private void OnTelemetryUpdated(object? sender, TelemetrySnapshot snap)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                // Always feed the ViewModel (it does its own internal /5 decimation for its own
                // properties) -- only this page's own UI writes below are gated on _uiTickCount.
                _vm.UpdateTelemetry(snap);

                if (++_uiTickCount % 5 != 0) return;

                UpdateInstrumentRow();
                UpdateAircraftIdStrip();
                UpdateProgressStrip();
                UpdateBookingBanner();
                UpdatePhaseLadder();

                _renderer.UpdatePosition(snap.Latitude, snap.Longitude, snap.Raw.HeadingTrue);
                var trail = _vm.GetTrailIfChanged();
                if (trail is not null)
                    _renderer.UpdateFlightPathAltitude(trail);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Acars] Telemetry update failed");
            }
        });
    }

    // ── Connection status banner ────────────────────────────────────────────────────────────

    private void UpdateConnectionBanner()
    {
        BannerTitleText.Text = _vm.BannerTitle;
        BannerDescText.Text = _vm.BannerDesc;
        ConnectionDot.Fill = BrushFromHex(_vm.DotColorHex);

        var connected = _vm.SimStatus == SimConnectionStatus.Connected;
        var sessionActive = _vm.SessionState != FlightSessionState.Idle && _vm.SessionState != FlightSessionState.Complete;
        var sessionComplete = _vm.SessionState == FlightSessionState.Complete;

        // Exactly one of these three is shown, matching Acars.razor's own if/else-if/else-if chain
        // exactly (connected+idle shows none of the three, same as the original).
        ConnectButtonBorder.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        ConnectButtonText.Text = _vm.ConnectingInProgress ? "Connecting…" : "Connect";
        LiveGroupPanel.Visibility = connected && sessionActive ? Visibility.Visible : Visibility.Collapsed;
        CompleteChipBorder.Visibility = connected && sessionComplete ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnConnectTapped(object sender, TappedRoutedEventArgs e)
    {
        _vm.TryConnect();
        UpdateConnectionBanner();
    }

    // Task 4's job to replace this with a real ContentDialog (title/message/Confirm/Cancel) --
    // deliberately a no-op stub for Task 3, per the implementation plan's own explicit allowance:
    // "wire the button here, the dialog itself is Task 4's job -- a stub/no-op handler is fine for
    // this task if sequencing is awkward, note if so." Calling Session.EndFlightManually()
    // directly here (skipping confirmation) would defeat the point of the dialog the design spec
    // asks for, so this intentionally does nothing yet rather than a lossy partial implementation.
    private void OnEndFlightTapped(object sender, TappedRoutedEventArgs e)
    {
        // Intentionally empty -- see method doc comment above.
    }

    // ── Active booking banner ───────────────────────────────────────────────────────────────

    private void UpdateBookingBanner()
    {
        var booking = _vm.ActiveBooking;
        if (booking is null)
        {
            BookingBannerBorder.Visibility = Visibility.Collapsed;
            return;
        }

        BookingBannerBorder.Visibility = Visibility.Visible;
        BookingCallsignText.Text = booking.Callsign;

        var hasFltNum = !string.IsNullOrEmpty(booking.RouteFlightNumber);
        BookingFltNumText.Visibility = hasFltNum ? Visibility.Visible : Visibility.Collapsed;
        BookingFltNumText.Text = hasFltNum ? $"Flight {booking.RouteFlightNumber}" : "";

        BookingOriginText.Text = booking.OriginIata;
        BookingDestText.Text = booking.DestIata;
        BookingAircraftTypeText.Text = booking.AircraftType;

        // OriginIata/DestIata actually hold ICAO codes despite the field name (a known,
        // already-documented quirk elsewhere in this codebase) -- IcaoCountry.IsoFromIcao is the
        // correct call here, matching Acars.razor's own IcaoCountry.IsoFromIcao(_bkg.OriginIata).
        var depFlag = LoadFlagImage(booking.OriginIata);
        BookingFlagDepImage.Source = depFlag;
        BookingFlagDepImage.Visibility = depFlag is not null ? Visibility.Visible : Visibility.Collapsed;
        var arrFlag = LoadFlagImage(booking.DestIata);
        BookingFlagArrImage.Source = arrFlag;
        BookingFlagArrImage.Visibility = arrFlag is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBookingDismissTapped(object sender, TappedRoutedEventArgs e)
    {
        _booking.SetActiveBooking(null);
        UpdateBookingBanner();
    }

    // Same technique FlightsPage.xaml.cs's own LoadFlagImage already established: wwwroot ships as
    // loose Content next to the exe, not a packaged ms-appx:// asset, so this is an absolute file
    // path; never throws, just omits the flag when the country can't be resolved or the file isn't
    // shipped.
    private static ImageSource? LoadFlagImage(string? icao)
    {
        var iso = IcaoCountry.IsoFromIcao(icao);
        if (string.IsNullOrEmpty(iso)) return null;

        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "wwwroot", "images", "flags", $"{iso}.svg");
        if (!File.Exists(path)) return null;

        return new SvgImageSource(new Uri(path));
    }

    // ── Aircraft identification strip ───────────────────────────────────────────────────────

    private void UpdateAircraftIdStrip()
    {
        AircraftIdStripBorder.Visibility = _vm.HasAircraftIdent ? Visibility.Visible : Visibility.Collapsed;
        if (!_vm.HasAircraftIdent) return;

        AtcModelText.Text = _vm.AtcModel;

        var hasCallsign = !string.IsNullOrWhiteSpace(_vm.AtcId);
        CallsignGroup.Visibility = hasCallsign ? Visibility.Visible : Visibility.Collapsed;
        AtcIdText.Text = _vm.AtcId;

        var hasFlightNum = !string.IsNullOrWhiteSpace(_vm.AtcFlightNumber);
        FlightNumGroup.Visibility = hasFlightNum ? Visibility.Visible : Visibility.Collapsed;
        AtcFlightNumberText.Text = _vm.AtcFlightNumber;

        var hasActiveLeg = !string.IsNullOrWhiteSpace(_vm.GpsActiveLeg);
        ActiveLegGroup.Visibility = hasActiveLeg ? Visibility.Visible : Visibility.Collapsed;
        GpsActiveLegText.Text = _vm.GpsActiveLeg;

        AircraftCoordsText.Text = _vm.CoordsText;
    }

    // ── Instrument row ───────────────────────────────────────────────────────────────────────

    private void UpdateInstrumentRow()
    {
        AltValueText.Text = _vm.AltText;
        AltValueText.Foreground = TextPrimaryBrush;
        IasValueText.Text = _vm.IasText;
        IasValueText.Foreground = TextPrimaryBrush;
        VsValueText.Text = _vm.VsText;
        VsValueText.Foreground = BrushFromHex(_vm.VsColorHex);
        HdgValueText.Text = _vm.HdgText;
        HdgValueText.Foreground = TextPrimaryBrush;
        GsValueText.Text = _vm.GsText;
        GsValueText.Foreground = TextPrimaryBrush;
        MachValueText.Text = _vm.MachText;
        MachValueText.Foreground = TextPrimaryBrush;
    }

    // ── Flight progress + ETA strip ─────────────────────────────────────────────────────────

    private void UpdateProgressStrip()
    {
        var plan = _vm.FlightPlan;
        ProgressStripBorder.Visibility = plan is not null ? Visibility.Visible : Visibility.Collapsed;
        if (plan is null) return;

        ProgressDepText.Text = plan.DepartureICAO;
        ProgressArrText.Text = plan.ArrivalICAO;

        var pct = Math.Clamp(_vm.ProgressPercent, 0, 100);
        ProgressFillColumn.Width = new GridLength(pct, GridUnitType.Star);
        ProgressRemainderColumn.Width = new GridLength(100 - pct, GridUnitType.Star);

        EnRouteText.Text = _vm.EnRouteText;
        DistFlownText.Text = _vm.DistFlownText;
        DistRemainingText.Text = _vm.DistRemainingText;
        EtaText.Text = _vm.EtaText;

        XtkGroup.Visibility = _vm.ShowXtk ? Visibility.Visible : Visibility.Collapsed;
        if (_vm.ShowXtk)
        {
            XtkLabelValueText.Text = _vm.XtkLabelText;
            XtkLabelValueText.Foreground = BrushFromHex(_vm.XtkColorHex);
        }
    }

    // ── Mini-map + sidebar: SimBrief / Flight Plan card ─────────────────────────────────────

    private void UpdateSimBriefCard()
    {
        var plan = _vm.FlightPlan;
        ClearPlanButtonBorder.Visibility = plan is not null ? Visibility.Visible : Visibility.Collapsed;
        SimBriefPlanPanel.Visibility = plan is not null ? Visibility.Visible : Visibility.Collapsed;
        SimBriefLoadingPanel.Visibility = plan is null && _vm.LoadingSimBrief ? Visibility.Visible : Visibility.Collapsed;
        SimBriefEmptyPanel.Visibility = plan is null && !_vm.LoadingSimBrief ? Visibility.Visible : Visibility.Collapsed;

        if (plan is not null)
        {
            SimBriefRouteDepText.Text = plan.DepartureICAO;
            SimBriefRouteArrText.Text = plan.ArrivalICAO;

            var hasAircraft = !string.IsNullOrWhiteSpace(plan.AircraftType);
            SimBriefAircraftText.Visibility = hasAircraft ? Visibility.Visible : Visibility.Collapsed;
            SimBriefAircraftText.Text = hasAircraft
                ? plan.AircraftType + (string.IsNullOrWhiteSpace(plan.AircraftRegistration) ? "" : " · " + plan.AircraftRegistration)
                : "";

            SimBriefCruiseRow.Visibility = plan.CruiseAltitudeFt > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (plan.CruiseAltitudeFt > 0)
                SimBriefCruiseValueText.Text = $"FL{plan.CruiseAltitudeFt / 100:D3} · M{plan.CruiseMach:F2}";

            SimBriefDistanceRow.Visibility = plan.PlannedDistanceNm > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (plan.PlannedDistanceNm > 0)
                SimBriefDistanceValueText.Text = $"{plan.PlannedDistanceNm:F0} nm";

            SimBriefEteRow.Visibility = plan.PlannedFlightTimeMin > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (plan.PlannedFlightTimeMin > 0)
                SimBriefEteValueText.Text = AcarsViewModel.FormatETE(plan.PlannedFlightTimeMin);

            SimBriefFuelRow.Visibility = plan.TakeoffFuelLbs > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (plan.TakeoffFuelLbs > 0)
                SimBriefFuelValueText.Text = $"{plan.TakeoffFuelLbs:N0} lbs";

            SimBriefFixesRow.Visibility = plan.Waypoints.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (plan.Waypoints.Count > 0)
                SimBriefFixesValueText.Text = $"{plan.Waypoints.Count} waypoints";
        }
        else
        {
            var hasError = !string.IsNullOrWhiteSpace(_vm.SimBriefError);
            SimBriefErrorBorder.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
            SimBriefErrorText.Text = _vm.SimBriefError ?? "";

            var hasUsername = !string.IsNullOrWhiteSpace(_vm.Settings.Settings.SimBriefUsername);
            SimBriefNoUsernamePanel.Visibility = hasUsername ? Visibility.Collapsed : Visibility.Visible;
            SimBriefHasUsernamePanel.Visibility = hasUsername ? Visibility.Visible : Visibility.Collapsed;
            if (hasUsername)
                SimBriefUsernameText.Text = _vm.Settings.Settings.SimBriefUsername;
        }
    }

    private async void OnClearPlanTapped(object sender, TappedRoutedEventArgs e)
    {
        await _vm.ClearPlanAsync();
        RefreshAllSections();
    }

    // Task 4 adds the WouldWipeTrail(...) confirmation ContentDialog gate here (see this class's
    // doc comment and AcarsPage.xaml's header comment) -- for now this always fetches and applies
    // the plan directly, matching the plan's own explicit Task 3/4 split (the OFP-import
    // wipe-warning dialog is listed under Task 4, not Task 3).
    private async void OnImportOfpTapped(object sender, TappedRoutedEventArgs e) => await ImportOrRefreshOfpAsync();

    private async void OnRefreshOfpTapped(object sender, TappedRoutedEventArgs e) => await ImportOrRefreshOfpAsync();

    private async Task ImportOrRefreshOfpAsync()
    {
        RefreshAllSections(); // reflect LoadingSimBrief=true immediately
        var (plan, _) = await _vm.FetchSimBriefPlanAsync();
        if (plan is not null)
            await _vm.ApplyImportedPlanAsync(plan);

        RefreshAllSections();
    }

    private void OnConfigureSimBriefTapped(object sender, TappedRoutedEventArgs e) => Frame.Navigate(typeof(SettingsPage));

    // ── Mini-map + sidebar: Flight Phase card ───────────────────────────────────────────────

    private void UpdatePhaseLadder()
    {
        UpdatePhaseRow("Parked", PhaseParkedRow, PhaseParkedDot, PhaseParkedText);
        UpdatePhaseRow("Taxi", PhaseTaxiRow, PhaseTaxiDot, PhaseTaxiText);
        UpdatePhaseRow("Climb", PhaseClimbRow, PhaseClimbDot, PhaseClimbText);
        UpdatePhaseRow("Cruise", PhaseCruiseRow, PhaseCruiseDot, PhaseCruiseText);
        UpdatePhaseRow("Descent", PhaseDescentRow, PhaseDescentDot, PhaseDescentText);
        UpdatePhaseRow("Approach", PhaseApproachRow, PhaseApproachDot, PhaseApproachText);
        UpdatePhaseRow("Landing", PhaseLandingRow, PhaseLandingDot, PhaseLandingText);
    }

    private void UpdatePhaseRow(string phaseName, Border row, Ellipse dot, TextBlock text)
    {
        var (chipClass, textColorHex) = _vm.PhaseStatus(phaseName);
        var brush = BrushFromHex(textColorHex);
        dot.Fill = brush;
        text.Foreground = brush;
        text.FontWeight = chipClass == "active" ? FontWeights.Bold : FontWeights.Normal;
        row.Background = chipClass == "active" ? ActivePhaseRowBrush : Surface2Brush;
    }

    // ── Mini-map + sidebar: ACARS Key card ──────────────────────────────────────────────────

    private void UpdateAcarsKeyCard()
    {
        var key = _vm.Settings.Settings.AcarsKey;
        var hasKey = !string.IsNullOrWhiteSpace(key);
        AcarsKeyValueBorder.Visibility = hasKey ? Visibility.Visible : Visibility.Collapsed;
        AcarsKeyEmptyBorder.Visibility = hasKey ? Visibility.Collapsed : Visibility.Visible;
        if (hasKey)
            AcarsKeyMaskedText.Text = AcarsViewModel.MaskAcarsKey(key);

        var friendCode = _vm.Settings.Settings.FriendCode;
        var showFriendCode = hasKey && !string.IsNullOrWhiteSpace(friendCode);
        FriendCodeRow.Visibility = showFriendCode ? Visibility.Visible : Visibility.Collapsed;
        if (showFriendCode)
            FriendCodeText.Text = friendCode;
    }

    private void OnConfigureAcarsTapped(object sender, TappedRoutedEventArgs e) => Frame.Navigate(typeof(SettingsPage));

    // ── Mini-map: "Full Map ↗" ──────────────────────────────────────────────────────────────

    private void OnFullMapTapped(object sender, TappedRoutedEventArgs e) => Frame.Navigate(typeof(MapPage));

    // ── Flight event log ─────────────────────────────────────────────────────────────────────

    private void SeedEventLogMirror()
    {
        _eventLogRows.Clear();
        // VM.EventLog is oldest-first (.Add()); reverse once here so the newest phase change shows
        // first, matching Acars.razor's own Enumerable.Reverse(_eventLog) render-time reversal.
        foreach (var entry in _vm.EventLog.Reverse())
            _eventLogRows.Add(ToDisplayRow(entry));

        EventLogSectionBorder.Visibility = _eventLogRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static AcarsEventLogDisplayRow ToDisplayRow(AcarsEventLogEntry entry) => new()
    {
        TimeText = entry.Time.ToString("HH:mm:ss") + "Z",
        Description = entry.Description,
    };

    // AcarsViewModel.EventLog only ever Adds (PhaseChanged) or is Cleared (ClearEventLog) -- mirror
    // both, inserting each new entry at the front. This handler is invoked synchronously on
    // whatever thread mutates VM.EventLog; every mutation of that collection already happens inside
    // AcarsViewModel's own _ui.Invoke callback (see HandlePhaseChanged), i.e. already on the UI
    // thread, so no further DispatcherQueue marshaling is needed here.
    private void OnEventLogCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (AcarsEventLogEntry entry in e.NewItems)
                _eventLogRows.Insert(0, ToDisplayRow(entry));
        }
        else if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _eventLogRows.Clear();
        }

        EventLogSectionBorder.Visibility = _eventLogRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClearEventLogTapped(object sender, TappedRoutedEventArgs e) => _vm.ClearEventLog();

    // ── UTC clock ─────────────────────────────────────────────────────────────────────────────

    private void UpdateUtcClock() => UtcClockText.Text = DateTime.UtcNow.ToString("HH:mm:ss");

    // ── Hex-string -> Brush (AcarsViewModel exposes colors as hex strings, never WinUI Brush
    //    objects, mirroring MapViewModel.PhaseColor -- see AcarsViewModel's class doc deviation #3)

    private static Brush BrushFromHex(string hex)
    {
        var h = hex.TrimStart('#');
        byte a = 0xFF, r, g, b;
        if (h.Length == 8)
        {
            a = Convert.ToByte(h.Substring(0, 2), 16);
            r = Convert.ToByte(h.Substring(2, 2), 16);
            g = Convert.ToByte(h.Substring(4, 2), 16);
            b = Convert.ToByte(h.Substring(6, 2), 16);
        }
        else
        {
            r = Convert.ToByte(h.Substring(0, 2), 16);
            g = Convert.ToByte(h.Substring(2, 2), 16);
            b = Convert.ToByte(h.Substring(4, 2), 16);
        }

        return new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }
}
