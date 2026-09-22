using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Core.Reference;
using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Dialogs;
using AviatesAirTracker.Models;
using AviatesAirTracker.Platform;
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
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
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

/// <summary>Pre-formatted display row for one Hoppies ACARS Log entry (Acars.razor:540-546) --
/// same CS8852 rationale as AcarsEventLogDisplayRow above. StatusBrush is a genuinely-typed Brush
/// property (not a hex string + converter), so {Binding StatusBrush} on a TextBlock.Foreground
/// binds directly with no StringFormat/IValueConverter involved -- unlike the timestamp text
/// formatting, a same-type property-to-property binding needs no Page.Resources converter entry.</summary>
public sealed class HoppiesLogDisplayRow
{
    public string CategoryText { get; set; } = "";
    public string StatusText { get; set; } = "";
    public Brush StatusBrush { get; set; } = new SolidColorBrush(Color.FromArgb(0xFF, 0x64, 0x74, 0x8B));
    public string TimeText { get; set; } = "";
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

    // Task 4 additions: accent/red for the Hoppies log SENT/FAILED text, and the four
    // .completion-stat-value.vs-* colors for the completion overlay's landing-VS stat.
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x4D, 0x9B, 0xEC));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));
    private static readonly Brush VsGreenBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x22, 0xC5, 0x5E));
    private static readonly Brush VsAmberBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B));
    private static readonly Brush VsRedBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));
    private static readonly Brush VsNeutralBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x94, 0xA3, 0xB8));
    private static readonly Brush TextMutedBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x64, 0x74, 0x8B));
    private static readonly Brush TextSecondaryBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1));
    private static readonly Brush BorderBrush_ = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    // Waypoint route strip's DEP/ARR fix-tag chips -- var(--green)/var(--green-light) and
    // var(--amber)/var(--amber-light) from app.css (dark theme leaves --green at the root's
    // #3EC7AC; --amber is #F59E0B in both themes), matching the same ~0.12/~0.10 alpha-hex
    // convention this page's own LiveGroupPanel/CompleteChipBorder Background values already use.
    private static readonly Brush GreenChipBg = new SolidColorBrush(Color.FromArgb(0x1F, 0x3E, 0xC7, 0xAC));
    private static readonly Brush GreenChipBorder = new SolidColorBrush(Color.FromArgb(0xFF, 0x3E, 0xC7, 0xAC));
    private static readonly Brush GreenChipText = new SolidColorBrush(Color.FromArgb(0xFF, 0x3E, 0xC7, 0xAC));
    private static readonly Brush AmberChipBg = new SolidColorBrush(Color.FromArgb(0x1A, 0xF5, 0x9E, 0x0B));
    private static readonly Brush AmberChipBorder = new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B));
    private static readonly Brush AmberChipText = new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B));

    private readonly AcarsViewModel _vm;
    private readonly FlightSessionManager _session;
    private readonly SimConnectManager _simConnect;
    private readonly FlightPhaseDetector _phaseDetector;
    private readonly BookingService _booking;
    // Task 4: PilotHubViewModel is needed only for the completion overlay's rank-before/-after
    // snapshot (AcarsViewModel deviation #1 -- the VM has no PilotHubViewModel dependency of its
    // own, so the page supplies RankBeforeSnapshot/NotifyRankRefreshed from here). FlightTrailService
    // is needed only for the OFP-import wipe-warning's Clear() call -- the VM only ever *reads* the
    // trail (WouldWipeTrail/GetTrailIfChanged), never clears it (see AcarsViewModel.ApplyImportedPlanAsync's
    // own doc comment: "the page calls FlightTrailService.Clear() itself, separately, only on confirm").
    private readonly PilotHubViewModel _pilotHub;
    private readonly FlightTrailService _flightTrail;

    private readonly NativeMapRenderer _renderer;
    private readonly Mapsui.UI.WinUI.MapControl _mapControl;

    // Page-local mirror of AcarsViewModel.EventLog, newest-first (the VM appends oldest-first via
    // .Add(), matching Acars.razor's own @code field -- the legacy page reverses at render time via
    // Enumerable.Reverse(_eventLog); this page reverses once per change instead, via
    // OnEventLogCollectionChanged inserting each new entry at index 0).
    private readonly ObservableCollection<AcarsEventLogDisplayRow> _eventLogRows = [];

    // Page-local mirror of AcarsViewModel.HoppiesLog. Unlike EventLog, HoppiesLog is already
    // newest-first at the VM (constructor seeds via TakeLast(50).Reverse().Add(), live entries via
    // Insert(0, entry)), so no reversal is needed here -- this mirror exists only to precompute
    // TimeText/StatusText/StatusBrush the same way AcarsEventLogDisplayRow precomputes TimeText (see
    // that class's own doc comment). A full rebuild on every CollectionChanged (rather than
    // delta-patching Add/Remove/Reset individually) is simplest and cheap at HoppiesLog's 50-entry cap.
    private readonly ObservableCollection<HoppiesLogDisplayRow> _hoppiesLogRows = [];

    // CLAUDE.md: "Do not call StateHasChanged() on every 20Hz tick -- batch every 3-5 samples."
    // AcarsViewModel.UpdateTelemetry already does its own internal /5 decimation for its own
    // properties, but this page's own TextBlock/MapControl writes are separately decimated here to
    // the same ~4Hz cadence (this page reads snap.Latitude/Longitude/Raw.HeadingTrue directly for
    // the mini-map, which AcarsViewModel deliberately does not expose -- see class doc deviation
    // #3, "free of any UI framework dependency" -- so this page needs its own tick gate).
    private int _uiTickCount = 4; // matches AcarsViewModel/MapViewModel's own seed so the first tick passes

    private DispatcherTimer? _utcClockTimer;
    // Legacy 10-minute METAR auto-refresh (Acars.razor's own System.Threading.Timer) -- kept as a
    // page-owned DispatcherTimer per AcarsViewModel's own class-doc deviation #2 ("no ViewModel
    // anywhere owns a DispatcherTimer... this ViewModel therefore exposes RefreshMetarsAsync() for
    // the page's own DispatcherTimer to call every 10 minutes").
    private DispatcherTimer? _metarTimer;

    // Page-local UI-only state (collapsed-panel booleans) -- matches the design spec's own
    // carve-out and AcarsViewModel's class-doc deviation #4.
    private bool _showNotams;
    private bool _showOfp;

    public AcarsPage()
    {
        InitializeComponent();

        // CompletionPanelBorder is excluded -- it's a whole slide-in overlay panel whose Tapped
        // handler only stops the dismiss-on-backdrop-tap bubble, not a clickable button.
        InteractiveEffects.Attach(ConnectButtonBorder, EndFlightButtonBorder, BookingDismissBorder,
            FullMapLinkBorder, ClearPlanButtonBorder, RefreshOfpButtonBorder, ImportOfpButtonBorder);

        _vm = App.GetService<AcarsViewModel>();
        _session = App.GetService<FlightSessionManager>();
        _simConnect = App.GetService<SimConnectManager>();
        _phaseDetector = App.GetService<FlightPhaseDetector>();
        _booking = App.GetService<BookingService>();
        _pilotHub = App.GetService<PilotHubViewModel>();
        _flightTrail = App.GetService<FlightTrailService>();

        EventLogItemsControl.ItemsSource = _eventLogRows;
        HoppiesLogItemsControl.ItemsSource = _hoppiesLogRows;

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
        _session.FlightCompleted += OnFlightCompleted;
        _phaseDetector.PhaseChanged += OnPhaseChanged;
        _pilotHub.DataRefreshed += OnPilotHubDataRefreshed;
        _vm.EventLog.CollectionChanged += OnEventLogCollectionChanged;
        _vm.HoppiesLog.CollectionChanged += OnHoppiesLogCollectionChanged;

        // Booking activation gate -- checked fresh against the server (not the possibly-stale
        // in-memory BookingService.ActiveBooking) every time this page loads, since activating
        // happens on Pilot Hub, a different page in this single-Frame shell, so the pilot always
        // returns here by navigating back rather than this page staying open across activation.
        // A one-time network call on page load is cheap; this is not the 20Hz hot path.
        // FlightSessionManager.OnEnginesStarted enforces the real "no tracking" rule -- this only
        // decides what the page shows, so the two are allowed to use different booking sources.
        var currentBooking = await _booking.GetCurrentBookingAsync();
        var activated = currentBooking is { IsActivated: true };
        BookingGateGrid.Visibility = activated ? Visibility.Collapsed : Visibility.Visible;
        if (!activated)
        {
            BookingGateSubtitleText.Text = currentBooking == null
                ? "Book a flight from Routes, then activate it from Pilot Hub before you fly."
                : "You have a booking waiting. Activate it from Pilot Hub before you start tracking.";
            return;
        }

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

        // 10-minute METAR auto-refresh -- see AcarsViewModel's class-doc deviation #2 for why this
        // timer lives here rather than in the ViewModel.
        _metarTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _metarTimer.Tick += async (_, _) => await RefreshMetarsAsync();
        _metarTimer.Start();

        // Mirrors Acars.razor's OnAfterRenderAsync(firstRender): render whatever state already
        // exists the moment this page opens, not just future updates.
        SeedEventLogMirror();
        SeedHoppiesLogMirror();
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
        _session.FlightCompleted -= OnFlightCompleted;
        _phaseDetector.PhaseChanged -= OnPhaseChanged;
        _pilotHub.DataRefreshed -= OnPilotHubDataRefreshed;
        _vm.EventLog.CollectionChanged -= OnEventLogCollectionChanged;
        _vm.HoppiesLog.CollectionChanged -= OnHoppiesLogCollectionChanged;

        _utcClockTimer?.Stop();
        _utcClockTimer = null;
        _metarTimer?.Stop();
        _metarTimer = null;
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
        UpdateOfpDetailsStrip();
        UpdateCompletionOverlay();
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

    // Task 4: real confirm dialog, gating AcarsViewModel.EndFlight() (-> Session.EndFlightManually())
    // behind an explicit Confirm tap. ConfirmDialog.ShowAsync only returns true for the primary
    // (confirm) button -- Cancel/close/light-dismiss all return false, so EndFlight() is never
    // called on anything but an explicit confirm.
    private async void OnEndFlightTapped(object sender, TappedRoutedEventArgs e)
    {
        var confirmed = await ConfirmDialog.ShowAsync(
            XamlRoot,
            "End Flight?",
            "This will end your current flight and finalize the flight record. This cannot be undone.",
            "End Flight",
            "Cancel");

        if (confirmed)
            _vm.EndFlight();
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

    // Task 4: the OFP-import wipe-warning gate. FetchSimBriefPlanAsync always runs first (matching
    // Task 3's own sequencing); WouldWipeTrail(...) then decides (ViewModel-owned decision, per its
    // own doc comment: "the ViewModel decides, the page prompts") whether the fetched plan would
    // require clearing the persistent flight trail. If true, ConfirmDialog gates BOTH the trail
    // clear AND the plan application behind an explicit Confirm tap -- declining leaves the
    // previously-loaded plan and the recorded trail completely untouched (the whole import is
    // cancelled, not half-applied). If WouldWipeTrail is false (same dep+arr, or same flight
    // number), the plan is applied immediately with no dialog, identical to Task 3's own behavior.
    private async void OnImportOfpTapped(object sender, TappedRoutedEventArgs e) => await ImportOrRefreshOfpAsync();

    private async void OnRefreshOfpTapped(object sender, TappedRoutedEventArgs e) => await ImportOrRefreshOfpAsync();

    private async Task ImportOrRefreshOfpAsync()
    {
        RefreshAllSections(); // reflect LoadingSimBrief=true immediately
        var (plan, _) = await _vm.FetchSimBriefPlanAsync();
        if (plan is null)
        {
            RefreshAllSections(); // reflect LoadingSimBrief=false / SimBriefError, if any
            return;
        }

        if (_vm.WouldWipeTrail(plan))
        {
            var confirmed = await ConfirmDialog.ShowAsync(
                XamlRoot,
                "Clear Flight Trail?",
                "Importing this OFP will clear your recorded flight trail. Continue?",
                "Continue",
                "Cancel");

            if (!confirmed)
            {
                RefreshAllSections(); // LoadingSimBrief already false (FetchSimBriefPlanAsync's own finally)
                return; // cancel the whole import -- old plan and trail both stay exactly as they were
            }

            _flightTrail.Clear();
        }

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

    // ── SimBrief OFP details strip (Acars.razor:395-664) — Task 4 ──────────────────────────────
    // Entirely gated on FlightPlan != null, matching Acars.razor's own single @if wrapping the
    // whole strip (waypoint route, METAR cards, NOTAMs, Hoppies log, OFP text, PIREP panel).

    private void UpdateOfpDetailsStrip()
    {
        var hasPlan = _vm.FlightPlan is not null;
        OfpDetailsStripPanel.Visibility = hasPlan ? Visibility.Visible : Visibility.Collapsed;
        if (!hasPlan) return;

        UpdateWaypointRouteStrip();
        UpdateMetarCards();
        UpdateNotamsPanel();
        UpdateOfpTextPanel();
    }

    // ── Waypoint route strip (Acars.razor:401-437) ──────────────────────────────────────────────

    private void UpdateWaypointRouteStrip()
    {
        var plan = _vm.FlightPlan;
        var hasWaypoints = plan is not null && plan.Waypoints.Count > 0;
        WaypointStripBorder.Visibility = hasWaypoints ? Visibility.Visible : Visibility.Collapsed;
        if (!hasWaypoints || plan is null) return;

        WaypointStripTitleText.Text = $"Route: {plan.DepartureICAO} → {plan.ArrivalICAO}";

        // SID/STAR label line -- falls back to the runway when no SID/STAR name is present,
        // matching Acars.razor:407-408 exactly. The original also color-differentiates the " · "
        // separators (var(--border)) from the label text (var(--text-muted)); this uses a single
        // uniform muted color instead -- a minor, purely decorative simplification (no Style/
        // converter needed either way), noted here rather than silently dropped.
        var sidText = !string.IsNullOrWhiteSpace(plan.SID) ? plan.SID : plan.DepartureRunway;
        var starText = !string.IsNullOrWhiteSpace(plan.STAR) ? plan.STAR : plan.ArrivalRunway;
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(sidText))
        {
            sb.Append("SID: ").Append(sidText);
            if (!string.IsNullOrWhiteSpace(plan.DepartureRunway) && !string.IsNullOrWhiteSpace(plan.SID))
                sb.Append(" · Rwy ").Append(plan.DepartureRunway);
        }
        if (!string.IsNullOrWhiteSpace(sidText) && !string.IsNullOrWhiteSpace(starText)) sb.Append("  ·  ");
        if (!string.IsNullOrWhiteSpace(starText))
        {
            sb.Append("STAR: ").Append(starText);
            if (!string.IsNullOrWhiteSpace(plan.ArrivalRunway) && !string.IsNullOrWhiteSpace(plan.STAR))
                sb.Append(" · Rwy ").Append(plan.ArrivalRunway);
        }
        WaypointSidStarText.Text = sb.ToString();
        WaypointSidStarText.Visibility = sb.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Scrollable fix-tag chips: DEP (green) -- intermediate fixes (type != "ltlg", surface-2) --
        // ARR (amber). Built in code (count varies per OFP), matching how the instrument row/phase
        // ladder above build their own fixed-count rows directly rather than via an ItemsControl.
        WaypointChipsPanel.Children.Clear();
        WaypointChipsPanel.Children.Add(BuildFixChip(plan.DepartureICAO, GreenChipBg, GreenChipBorder, GreenChipText, bold: true));
        foreach (var wp in plan.Waypoints.Where(w => w.Type != "ltlg"))
            WaypointChipsPanel.Children.Add(BuildFixChip(wp.Identifier, Surface2Brush, BorderBrush_, TextSecondaryBrush, bold: false));
        WaypointChipsPanel.Children.Add(BuildFixChip(plan.ArrivalICAO, AmberChipBg, AmberChipBorder, AmberChipText, bold: true));

        var hasRoute = !string.IsNullOrWhiteSpace(plan.Route);
        WaypointRawRouteText.Visibility = hasRoute ? Visibility.Visible : Visibility.Collapsed;
        WaypointRawRouteText.Text = hasRoute ? plan.Route : "";
    }

    private static Border BuildFixChip(string text, Brush background, Brush border, Brush foreground, bool bold) => new()
    {
        Background = background,
        BorderBrush = border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(bold ? 8 : 7, 3, bold ? 8 : 7, 3),
        Child = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            Foreground = foreground,
        },
    };

    // ── METAR cards — DEP / ARR / ALT (Acars.razor:439-506) ─────────────────────────────────────

    private void UpdateMetarCards()
    {
        var plan = _vm.FlightPlan;
        if (plan is null) { MetarCardsGrid.Visibility = Visibility.Collapsed; return; }

        var depText = _vm.DepMetar ?? plan.DepartureMetar;
        var arrText = _vm.ArrMetar ?? plan.ArrivalMetar;
        var altText = _vm.AltMetar;

        var showDep = !string.IsNullOrWhiteSpace(depText);
        var showArr = !string.IsNullOrWhiteSpace(arrText);
        var showAlt = !string.IsNullOrWhiteSpace(altText) && !string.IsNullOrWhiteSpace(plan.AlternateICAO);
        MetarCardsGrid.Visibility = showDep || showArr || showAlt ? Visibility.Visible : Visibility.Collapsed;

        var timeText = _vm.MetarRefreshedAt is { } t ? t.ToString("HH:mm") : "";

        DepMetarCardBorder.Visibility = showDep ? Visibility.Visible : Visibility.Collapsed;
        if (showDep)
        {
            DepMetarTitleText.Text = $"DEP METAR: {plan.DepartureICAO}";
            DepMetarTimeText.Text = timeText;
            DepMetarBodyText.Text = depText;
            DepMetarRefreshIconText.Text = _vm.MetarRefreshing ? "…" : "↻";
        }

        ArrMetarCardBorder.Visibility = showArr ? Visibility.Visible : Visibility.Collapsed;
        if (showArr)
        {
            ArrMetarTitleText.Text = $"ARR METAR: {plan.ArrivalICAO}";
            ArrMetarTimeText.Text = timeText;
            ArrMetarBodyText.Text = arrText;
            ArrMetarRefreshIconText.Text = _vm.MetarRefreshing ? "…" : "↻";
        }

        AltMetarCardBorder.Visibility = showAlt ? Visibility.Visible : Visibility.Collapsed;
        if (showAlt)
        {
            AltMetarTitleText.Text = $"ALT METAR: {plan.AlternateICAO}";
            AltMetarTimeText.Text = timeText;
            AltMetarBodyText.Text = altText;
            AltMetarRefreshIconText.Text = _vm.MetarRefreshing ? "…" : "↻";
        }
    }

    private async void OnRefreshMetarsTapped(object sender, TappedRoutedEventArgs e)
    {
        await RefreshMetarsAsync();
    }

    private async Task RefreshMetarsAsync()
    {
        await _vm.RefreshMetarsAsync();
        UpdateMetarCards();
    }

    // ── NOTAMs panel — collapsible (Acars.razor:508-526) ────────────────────────────────────────

    private void UpdateNotamsPanel()
    {
        var plan = _vm.FlightPlan;
        var hasNotams = plan is not null && plan.NOTAMs.Count > 0;
        NotamsBorder.Visibility = hasNotams ? Visibility.Visible : Visibility.Collapsed;
        if (!hasNotams || plan is null) return;

        NotamsCountText.Text = $"({plan.NOTAMs.Count})";
        NotamsChevronText.Text = _showNotams ? "▲" : "▼";
        NotamsListPanel.Visibility = _showNotams ? Visibility.Visible : Visibility.Collapsed;

        if (!_showNotams) return; // no need to rebuild the (collapsed) list every refresh

        NotamsListPanel.Children.Clear();
        foreach (var notam in plan.NOTAMs)
        {
            NotamsListPanel.Children.Add(new Border
            {
                Background = Surface2Brush,
                BorderBrush = BorderBrush_,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 10, 12, 10),
                Child = new TextBlock
                {
                    Text = notam,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Foreground = TextSecondaryBrush,
                    TextWrapping = TextWrapping.Wrap,
                },
            });
        }
    }

    private void OnToggleNotamsTapped(object sender, TappedRoutedEventArgs e)
    {
        _showNotams = !_showNotams;
        UpdateNotamsPanel();
    }

    // ── Hoppies ACARS Log panel — read-only (Acars.razor:528-550) ──────────────────────────────
    // Already seeded from HoppiesAcarsOrchestrator.MessageLog by AcarsViewModel's own constructor
    // (the design spec's named fix) -- SeedHoppiesLogMirror below just mirrors whatever VM.HoppiesLog
    // already holds the moment this page loads, then OnHoppiesLogCollectionChanged keeps it current.

    private void SeedHoppiesLogMirror()
    {
        _hoppiesLogRows.Clear();
        foreach (var entry in _vm.HoppiesLog) // already newest-first at the VM
            _hoppiesLogRows.Add(ToHoppiesDisplayRow(entry));

        UpdateHoppiesLogEmptyState();
    }

    private void OnHoppiesLogCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => SeedHoppiesLogMirror();

    private void UpdateHoppiesLogEmptyState()
    {
        var hasEntries = _hoppiesLogRows.Count > 0;
        HoppiesLogEmptyText.Visibility = hasEntries ? Visibility.Collapsed : Visibility.Visible;
        HoppiesLogScrollViewer.Visibility = hasEntries ? Visibility.Visible : Visibility.Collapsed;
    }

    private static HoppiesLogDisplayRow ToHoppiesDisplayRow(HoppiesLogEntry entry) => new()
    {
        CategoryText = entry.Category,
        StatusText = entry.Success ? "SENT" : "FAILED",
        StatusBrush = entry.Success ? AccentBrush : RedBrush,
        TimeText = entry.SentAtUtc.ToString("HH:mm:ss") + "Z",
    };

    // ── OFP text viewer — collapsible (Acars.razor:552-567) ─────────────────────────────────────

    private void UpdateOfpTextPanel()
    {
        var plan = _vm.FlightPlan;
        var hasText = plan is not null && !string.IsNullOrWhiteSpace(plan.OFPText);
        OfpTextBorder.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        if (!hasText || plan is null) return;

        OfpTextChevronText.Text = _showOfp ? "▲" : "▼";
        OfpTextScrollViewer.Visibility = _showOfp ? Visibility.Visible : Visibility.Collapsed;
        if (_showOfp)
            OfpTextBodyText.Text = plan.OFPText;
    }

    private void OnToggleOfpTextTapped(object sender, TappedRoutedEventArgs e)
    {
        _showOfp = !_showOfp;
        UpdateOfpTextPanel();
    }

    // ── Flight completion overlay (Acars.razor:1414-1479) ───────────────────────────────────────
    // AcarsViewModel's own Session.FlightCompleted subscription (registered at DI-singleton
    // construction, well before this page's Loaded ever runs) always finishes updating
    // CompletedRecord/ShowCompletionPanel before this page's own handler (subscribed later) runs --
    // see this class's own doc comment for the full ordering argument. This handler's only real job
    // is therefore the piece AcarsViewModel deliberately cannot do itself (deviation #1): snapshot
    // PilotHubViewModel.PilotRank into RankBeforeSnapshot right now, before any later
    // PilotHub.DataRefreshed fires and moves PilotRank on.

    private void OnFlightCompleted(object? sender, FlightRecord record)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                _vm.RankBeforeSnapshot = _pilotHub.PilotRank;
                UpdateCompletionOverlay();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Acars] Flight-completion overlay refresh failed");
            }
        });
    }

    private void OnPilotHubDataRefreshed()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                _vm.NotifyRankRefreshed(_pilotHub.PilotRank);
                UpdateCompletionOverlay();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Acars] Rank-refresh overlay update failed");
            }
        });
    }

    private void UpdateCompletionOverlay()
    {
        var show = _vm.ShowCompletionPanel && _vm.CompletedRecord is not null;
        CompletionOverlayGrid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        var record = _vm.CompletedRecord!;
        CompletionDepText.Text = record.DepartureICAO;
        CompletionArrText.Text = record.ArrivalICAO;
        CompletionSubText.Text = string.IsNullOrEmpty(record.Callsign)
            ? record.AircraftType
            : $"{record.AircraftType} · {record.Callsign}";

        CompletionVsText.Text = AcarsViewModel.FormatVS(record);
        CompletionVsText.Foreground = AcarsViewModel.LandingVsClass(record) switch
        {
            "green" => VsGreenBrush,
            "amber" => VsAmberBrush,
            "red" => VsRedBrush,
            _ => VsNeutralBrush,
        };
        CompletionScoreText.Text = record.PrimaryLanding?.LandingScore.ToString() ?? "—";
        CompletionAirTimeText.Text = AcarsViewModel.FormatAirTime(record.AirTime);
        CompletionDistanceText.Text = $"{record.ActualDistanceNm:F0} nm";
        CompletionFuelText.Text = $"{record.FuelUsedLbs:F0} lbs";

        var rankUp = _vm.RankUp;
        CompletionRankupBorder.Visibility = rankUp is not null ? Visibility.Visible : Visibility.Collapsed;
        if (rankUp is not null)
            CompletionRankupValueText.Text = rankUp;
    }

    private void OnCompletionOverlayTapped(object sender, TappedRoutedEventArgs e) => DismissCompletionOverlay();

    // Stops the tap from bubbling to CompletionOverlayGrid's own Tapped handler (which would
    // otherwise dismiss the panel on every tap inside it) -- mirrors Acars.razor's
    // @onclick:stopPropagation="true" on .completion-panel.
    private void OnCompletionPanelTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void OnCompletionDismissTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        DismissCompletionOverlay();
    }

    private void DismissCompletionOverlay()
    {
        _vm.DismissCompletionPanel();
        UpdateCompletionOverlay();
    }

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
