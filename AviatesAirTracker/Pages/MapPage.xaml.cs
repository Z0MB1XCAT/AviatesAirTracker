using AviatesAirTracker.Core.Efb;
using AviatesAirTracker.Models;
using AviatesAirTracker.Platform;
using AviatesAirTracker.Services;
using AviatesAirTracker.Services.Efb;
using AviatesAirTracker.ViewModels;
using Mapsui;
using Mapsui.Layers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;

namespace AviatesAirTracker.Pages;

/// <summary>
/// Native reproduction of AviatesAirTracker.LegacyWeb\Pages\MapPage.razor. Hosts a
/// Mapsui.UI.WinUI.MapControl wrapping a NativeMapRenderer (the Tasks 1-3 rendering layer, the
/// direct analogue of wwwroot/js/interop.js's window.aviatesMap) plus the map-controls HUD panel,
/// the pilot-detail panel, and the "no active flight" overlay.
///
/// The identity strip, progress strip, and SimBrief plan HUD (all originally ported faithfully
/// from MapPage.razor, all draggable) were removed after the fact at the user's explicit request,
/// once they were actually visible on screen -- not part of the original Phase 4 plan. Their
/// underlying data plumbing (MapViewModel's own telemetry/route-tracking, the map's own planned-
/// route rendering) is untouched; only the floating text panels that displayed a slice of it are
/// gone. SimBriefService/FlightSessionManager injections were removed along with them since
/// nothing else in this page used those two services.
/// </summary>
public sealed partial class MapPage : Page
{
    // Enter/Space on a focused Border-as-button runs its Tapped handler (see KeyboardActivation).
    private void OnActivateKey(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e) =>
        AviatesAirTracker.Platform.KeyboardActivation.Handle(this, sender, e);

    private readonly MapViewModel _vm;
    private readonly OtherPilotsService _otherPilots;
    private readonly MessagingService _messaging;
    private readonly EfbNetworkTrafficService _network;

    private readonly NativeMapRenderer _renderer;
    private readonly Mapsui.UI.WinUI.MapControl _mapControl;

    private HashSet<string> _renderedOtherPilotIds = new();

    // ── Weather radar / VATSIM / IVAO layer toggles ─────────────────────────────────────────
    // Page-owned, not MapViewModel-owned — matches OtherPilotsService's own direct-injection
    // pattern above (this.constructor comment) and AcarsPage.xaml.cs's documented rule that no
    // ViewModel here owns a DispatcherTimer. EfbNetworkTrafficService is the exact same singleton
    // already built for the EFB web map's VATSIM/IVAO layers (Services/Efb/EfbNetworkTrafficService.cs)
    // — reused here rather than duplicating the VATSIM/IVAO/transceivers fetch+cache logic a
    // second time for the native map.
    private bool _weatherRadarOn;
    private bool _vatsimOn;
    private bool _ivaoOn;
    private DispatcherTimer? _weatherTimer;
    private DispatcherTimer? _networkTimer;
    // RainViewer's metadata endpoint is unrelated to this app's own backend/EFB server, so this
    // gets its own small HttpClient rather than reusing any service's client that carries
    // ACARS-key or EFB-token auth headers meant for a different host.
    private readonly HttpClient _weatherHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Set by OnMapInfo when an other-pilot marker is tapped. Exposed as a property (not a bare
    // field) so this class's own build doesn't trip CS0414 ("assigned but never read"); the
    // pilot-detail panel below is the consumer.
    private string? _selectedPilotId;
    public string? SelectedOtherPilotId => _selectedPilotId;

    // ── Pilot-detail panel state ─────────────────────────────────────────────────────────────
    // Mirrors MapPage.razor's own @code field (_selectedPilot) -- page-local UI state, not part
    // of MapViewModel.
    private LivePilotPosition? _selectedPilot;

    public MapPage()
    {
        InitializeComponent();

        InteractiveEffects.Attach(CenterButtonBorder, ZoomInButtonBorder, ZoomOutButtonBorder, PilotDetailCloseButton,
            WeatherRadarButtonBorder, VatsimButtonBorder, IvaoButtonBorder);

        _vm = App.GetService<MapViewModel>();
        _otherPilots = App.GetService<OtherPilotsService>();
        _messaging = App.GetService<MessagingService>();
        _network = App.GetService<EfbNetworkTrafficService>();

        // NativeMapRenderer(map: null) creates and owns its own Mapsui.Map (see its own doc
        // comment: "works both standalone ... or pre-wired to a MapControl") -- handing that same
        // Map instance to the MapControl below is what "pre-wires" it. NativeMapRenderer.Init()
        // (called in Loaded) mutates that Map's Layers collection in place, so it doesn't matter
        // that Init() hasn't run yet at the point the MapControl takes the reference here.
        _renderer = new NativeMapRenderer();
        _mapControl = new Mapsui.UI.WinUI.MapControl { Map = _renderer.Map };
        MapHost.Children.Add(_mapControl);

        // NativeMapRenderer only owns a Mapsui.Map (not a MapControl), so it cannot itself
        // subscribe to pointer hit-testing -- that requires the actual MapControl instance, which
        // only this page has. Two separate handlers now, deliberately not one shared one: Info
        // (a tap/click gesture) drives the other-pilot marker -> pilot-detail-panel flow;
        // PointerMoved (continuous hover) drives the waypoint-name popup. These used to share one
        // handler wired to Info alone (waypoint names showed on tap, not hover) -- split apart at
        // the user's explicit request for hover-based waypoint names.
        _mapControl.Info += OnMapInfo;
        _mapControl.PointerMoved += OnMapPointerMoved;

        UpdateNoFlightOverlay();
        UpdatePilotDetailPanel();

        // Reusable drag helper (ManipulationDelta + TranslateTransform). The identity/progress/
        // plan-HUD panels this used to also apply to are gone (removed at the user's request);
        // the pilot-detail panel is the only draggable panel left.
        // The original's markup has a drag handle on the pilot-detail panel but never actually
        // calls initDrag on it (a confirmed pre-existing bug -- it looks draggable but isn't).
        // This port wires real dragging here since the panel is structurally identical to the
        // ones that already drag correctly -- a small, defensible fix, not a silent behavior
        // change (called out in the Task 5 commit message/report).
        AttachDragHandle(PilotDetailDragHandle, PilotDetailPanel);
        // MapControlsPanel intentionally has no drag handle and no call to AttachDragHandle here,
        // matching the original exactly (bottom-right map controls do not drag) -- and per the
        // user's explicit choice to keep this panel as-is.

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────────────────────

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _renderer.Init(_vm.Lat, _vm.Lon);

        // MapViewModel.MapRenderRequested fires from the telemetry pump (confirmed off the UI
        // thread during this phase's research); OtherPilotsService.PositionsUpdated fires from its
        // own background System.Threading.Timer poll. Both are marshaled via
        // DispatcherQueue.TryEnqueue in their handlers below, matching the Loaded/Unloaded
        // subscribe-unsubscribe shape MessagesPage.xaml.cs/SettingsPage.xaml.cs already
        // established. (SimBriefService.FlightPlanLoaded was only subscribed here to feed the now-
        // removed Plan HUD -- MapViewModel's own ConsumePlanSync()/MapRenderRequested already keeps
        // the map's planned-route layer in sync independently of that subscription, so removing it
        // doesn't affect the actual map rendering.)
        _vm.MapRenderRequested += OnMapRenderRequested;
        _otherPilots.PositionsUpdated += OnOtherPilotsUpdated;

        // Mirrors MapPage.razor's OnAfterRenderAsync(firstRender): render whatever other-pilot
        // positions and planned route already exist the moment this page opens, not just future
        // updates.
        RenderOtherPilots(_otherPilots.LatestPositions);

        var (plan, _) = _vm.ConsumePlanSync();
        if (plan is not null)
            PushPlannedRoute(plan);

        UpdateNoFlightOverlay();

        // Always running (matching AcarsPage.xaml.cs's _metarTimer convention), gated inside each
        // tick by the relevant _weatherRadarOn/_vatsimOn/_ivaoOn flag -- so nothing fetches over
        // the network while every layer toggle is off, and re-enabling a toggle later doesn't
        // need to wait for the next tick to pick up (each toggle handler also fires one immediate
        // refresh of its own).
        _weatherTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _weatherTimer.Tick += async (_, _) => await RefreshWeatherRadarAsync();
        _weatherTimer.Start();

        _networkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _networkTimer.Tick += async (_, _) => await RefreshNetworkTrafficAsync();
        _networkTimer.Start();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _vm.MapRenderRequested -= OnMapRenderRequested;
        _otherPilots.PositionsUpdated -= OnOtherPilotsUpdated;

        _weatherTimer?.Stop();
        _weatherTimer = null;
        _networkTimer?.Stop();
        _networkTimer = null;
    }

    // ── MapViewModel.MapRenderRequested -- the ~4 Hz decimated telemetry tick ───────────────────

    // Matches MapPage.razor's PushMapToJSAsync exactly: always push position/heading, but only
    // rebuild the altitude-colored path when GetPathIfChanged() says it actually grew, and only
    // rebuild the planned route/airport markers when ConsumePlanSync() says the plan is dirty --
    // never unconditionally rebuild everything on every tick.
    private void OnMapRenderRequested()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                _renderer.UpdatePosition(_vm.Lat, _vm.Lon, _vm.Hdg);

                var path = _vm.GetPathIfChanged();
                if (path is not null)
                    _renderer.UpdateFlightPathAltitude(ProjectPathPoints(path));

                var (plan, dirty) = _vm.ConsumePlanSync();
                if (dirty && plan is not null)
                    PushPlannedRoute(plan);

                UpdateNoFlightOverlay();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Map] Render update failed");
            }
        });
    }

    // MapViewModel.GetPathIfChanged() returns IReadOnlyList<object> where each element is an
    // anonymous type (new { lat, lon, alt }) private to MapViewModel.cs -- there is no named type
    // this page can reference directly. Plain System.Reflection (resolving the 3 PropertyInfos
    // once, then reusing them for every element) reads the values without needing a `dynamic`/
    // Microsoft.CSharp.RuntimeBinder dependency this project doesn't otherwise have.
    private static List<(double Lat, double Lon, double AltFt)> ProjectPathPoints(IReadOnlyList<object> path)
    {
        var result = new List<(double Lat, double Lon, double AltFt)>(path.Count);
        if (path.Count == 0)
            return result;

        var type = path[0].GetType();
        var latProp = type.GetProperty("lat")!;
        var lonProp = type.GetProperty("lon")!;
        var altProp = type.GetProperty("alt")!;

        foreach (var raw in path)
        {
            result.Add((
                (double)latProp.GetValue(raw)!,
                (double)lonProp.GetValue(raw)!,
                (double)altProp.GetValue(raw)!));
        }

        return result;
    }

    // Matches MapPage.razor's PushPlannedRouteAsync exactly: filters stray/out-of-range waypoints,
    // clears+resets the airport markers, sets the dashed route + waypoint dots, adds up to 3
    // airport pins (dep/arr/alt), then fits the viewport to the combined bounds.
    private void PushPlannedRoute(SimBriefFlightPlan plan)
    {
        var waypoints = plan.Waypoints
            .Where(w => (w.Latitude != 0 || w.Longitude != 0)
                     && Math.Abs(w.Latitude) <= 90
                     && Math.Abs(w.Longitude) <= 180)
            .Select(w => (
                w.Latitude,
                w.Longitude,
                // interop.js: named = valid.filter(p => p.type !== 'ltlg' && p.id) -- ported here
                // as the caller-side filtering NativeMapRenderer.SetPlannedRoute's doc comment
                // documents (Name plays the role of interop.js's "id"; null means "unlabeled").
                Name: (string?)(w.Type != "ltlg" && !string.IsNullOrEmpty(w.Identifier) ? w.Identifier : null)))
            .ToList();

        _renderer.ClearAirportMarkers();
        _renderer.SetPlannedRoute(waypoints);

        if (plan.DepartureLat != 0 || plan.DepartureLon != 0)
            _renderer.AddAirportMarker(plan.DepartureLat, plan.DepartureLon, plan.DepartureICAO, "dep");

        if (plan.ArrivalLat != 0 || plan.ArrivalLon != 0)
            _renderer.AddAirportMarker(plan.ArrivalLat, plan.ArrivalLon, plan.ArrivalICAO, "arr");

        if (!string.IsNullOrEmpty(plan.AlternateICAO) && (plan.AlternateLat != 0 || plan.AlternateLon != 0))
            _renderer.AddAirportMarker(plan.AlternateLat, plan.AlternateLon, plan.AlternateICAO, "alt");

        _renderer.FitRouteBounds();
    }

    // ── OtherPilotsService.PositionsUpdated ─────────────────────────────────────────────────────

    private void OnOtherPilotsUpdated(object? sender, List<LivePilotPosition> positions)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                RenderOtherPilots(positions);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Map] Failed to render other-pilot positions");
            }
        });
    }

    // Matches MapPage.razor's RenderOtherPilotsAsync exactly: diff current ids against the
    // previously-rendered set, remove stale markers, upsert everything current.
    private void RenderOtherPilots(List<LivePilotPosition> positions)
    {
        var currentIds = positions.Select(p => p.PilotId).ToHashSet();

        foreach (var stalePilotId in _renderedOtherPilotIds.Except(currentIds).ToList())
            _renderer.RemoveOtherPilot(stalePilotId);

        foreach (var pos in positions)
        {
            var waypoints = pos.Waypoints?.Select(w => (w.Lat, w.Lon)).ToList();
            _renderer.UpsertOtherPilot(pos.PilotId, pos.Lat, pos.Lon, waypoints);
        }

        _renderedOtherPilotIds = currentIds;

        // Matches MapPage.razor's RenderOtherPilotsAsync: keep the open pilot-detail panel's
        // telemetry fresh on every poll, and implicitly hide it (UpdatePilotDetailPanel treats a
        // null _selectedPilot as "hide") if the selected pilot stopped broadcasting.
        if (_selectedPilot is not null)
        {
            _selectedPilot = positions.FirstOrDefault(p => p.PilotId == _selectedPilot.PilotId);
            UpdatePilotDetailPanel();
        }
    }

    // ── Hit-testing: tap (other-pilot markers) vs. hover (waypoint names) ──────────────────────
    // Two separate MapControl handlers, not one shared one: Info is a tap/click gesture, used only
    // to select an other-pilot marker and open the pilot-detail panel. PointerMoved is continuous
    // hover, used only to show/hide the waypoint-name popup as the pointer crosses a waypoint dot
    // -- per the user's explicit request for hover-based waypoint names (previously this was
    // tap-only, sharing the Info handler above with the pilot-marker logic).

    private void OnMapInfo(object? sender, Mapsui.MapInfoEventArgs e)
    {
        var pilotId = e.MapInfo?.Feature is { } feature ? NativeMapRenderer.GetOtherPilotId(feature) : null;
        if (pilotId is null) return;

        _selectedPilotId = pilotId;
        // Matches MapPage.razor's [JSInvokable] OnOtherPilotClicked: resolve the tapped pilot
        // from OtherPilotsService.LatestPositions and show the pilot-detail panel.
        _selectedPilot = _otherPilots.LatestPositions.FirstOrDefault(p => p.PilotId == pilotId);
        UpdatePilotDetailPanel();
    }

    // Pixel hit-test tolerance around the pointer for MapControl.GetMapInfo -- a small forgiving
    // radius so hovering doesn't require pixel-perfect precision over a small waypoint dot,
    // matching the original Leaflet page's own forgiving circle-marker hit target.
    private const int HoverHitTestMargin = 8;

    private void OnMapPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(_mapControl).Position;
        var screenPosition = new MPoint(position.X, position.Y);
        var mapInfo = _mapControl.GetMapInfo(screenPosition, HoverHitTestMargin);
        var waypointName = mapInfo?.Feature is { } feature ? NativeMapRenderer.GetWaypointName(feature) : null;

        if (waypointName is not null)
            ShowWaypointPopup(waypointName, screenPosition);
        else
            HideWaypointPopup();
    }

    private void ShowWaypointPopup(string name, MPoint screenPosition)
    {
        WaypointPopupText.Text = name;
        Canvas.SetLeft(WaypointPopupBorder, screenPosition.X + 8);
        Canvas.SetTop(WaypointPopupBorder, screenPosition.Y - 22);
        WaypointPopupBorder.Visibility = Visibility.Visible;
    }

    private void HideWaypointPopup()
    {
        WaypointPopupBorder.Visibility = Visibility.Collapsed;
    }

    // ── Map controls (bottom-right, not draggable) ──────────────────────────────────────────────

    private void OnCenterOnAircraftTapped(object sender, TappedRoutedEventArgs e) => _renderer.CenterOnAircraft();

    private void OnZoomInTapped(object sender, TappedRoutedEventArgs e) => _renderer.ZoomIn();

    private void OnZoomOutTapped(object sender, TappedRoutedEventArgs e) => _renderer.ZoomOut();

    // ── Weather radar / VATSIM / IVAO layer toggles ─────────────────────────────────────────────
    // Same "toggle + immediate refresh + periodic background refresh" shape the EFB web map's
    // rail buttons use (EfbWeb/app.js's toggleWeather/drawNetwork) -- kept deliberately parallel
    // so both surfaces behave the same way, not because any code is shared between them (WinUI3
    // vs. vanilla JS can't share code here).

    private async void OnWeatherRadarTapped(object sender, TappedRoutedEventArgs e)
    {
        _weatherRadarOn = !_weatherRadarOn;
        UpdateLayerToggleVisuals();
        _renderer.SetWeatherRadarVisible(_weatherRadarOn);
        if (_weatherRadarOn)
            await RefreshWeatherRadarAsync();
    }

    private async void OnVatsimTapped(object sender, TappedRoutedEventArgs e)
    {
        _vatsimOn = !_vatsimOn;
        UpdateLayerToggleVisuals();
        _renderer.SetVatsimVisible(_vatsimOn);
        if (_vatsimOn)
            await RefreshNetworkTrafficAsync();
    }

    private async void OnIvaoTapped(object sender, TappedRoutedEventArgs e)
    {
        _ivaoOn = !_ivaoOn;
        UpdateLayerToggleVisuals();
        _renderer.SetIvaoVisible(_ivaoOn);
        if (_ivaoOn)
            await RefreshNetworkTrafficAsync();
    }

    private void UpdateLayerToggleVisuals()
    {
        SetToggleButtonVisual(WeatherRadarButtonBorder, _weatherRadarOn);
        SetToggleButtonVisual(VatsimButtonBorder, _vatsimOn);
        SetToggleButtonVisual(IvaoButtonBorder, _ivaoOn);
    }

    // #4D9BEC is this app's current brand accent (CLAUDE.md: #3D7EEE is the retired previous
    // value) -- used only for this toggle-active highlight, not touching the panel's existing
    // #3D7EEE-based resting chrome shared with the center/zoom buttons above.
    private static void SetToggleButtonVisual(Border border, bool active)
    {
        border.Background = new SolidColorBrush(active
            ? Windows.UI.Color.FromArgb(0x66, 0x4D, 0x9B, 0xEC)
            : Windows.UI.Color.FromArgb(0x14, 0x3D, 0x7E, 0xEE));
        border.BorderBrush = new SolidColorBrush(active
            ? Windows.UI.Color.FromArgb(0xFF, 0x4D, 0x9B, 0xEC)
            : Windows.UI.Color.FromArgb(0x40, 0x3D, 0x7E, 0xEE));
    }

    // RainViewer's public metadata + tile API -- no key required, same source the EFB web map's
    // Map app uses (EfbWeb/app.js's drawWeather). This page does the HTTP fetch itself (unlike
    // VATSIM/IVAO below, RainViewer has no existing backend service already fetching it) and
    // hands NativeMapRenderer only the resolved tile URL template, matching every other method
    // on that class taking already-resolved primitives rather than owning network calls itself.
    private async Task RefreshWeatherRadarAsync()
    {
        if (!_weatherRadarOn) return;
        try
        {
            var json = await _weatherHttp.GetStringAsync("https://api.rainviewer.com/public/weather-maps.json");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("host", out var hostEl)) return;
            if (!root.TryGetProperty("radar", out var radarEl) || !radarEl.TryGetProperty("past", out var pastEl))
                return;
            var frameCount = pastEl.GetArrayLength();
            if (frameCount == 0) return;
            var last = pastEl[frameCount - 1];
            if (!last.TryGetProperty("path", out var pathEl)) return;

            var tileUrl = $"{hostEl.GetString()}{pathEl.GetString()}/256/{{z}}/{{x}}/{{y}}/2/1_1.png";
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_weatherRadarOn) return; // toggled off while the fetch was in flight
                _renderer.SetWeatherRadarTile(tileUrl);
                _renderer.SetWeatherRadarVisible(true);
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Map] Weather radar refresh failed");
        }
    }

    // Reuses EfbNetworkTrafficService (Services/Efb/EfbNetworkTrafficService.cs) -- the same
    // VATSIM+IVAO fetch/cache the EFB web map's Map app already uses, so this page's polling
    // never doubles the request rate against either network beyond that service's own ~30s cache.
    private async Task RefreshNetworkTrafficAsync()
    {
        if (!_vatsimOn && !_ivaoOn) return;
        try
        {
            var net = await _network.GetAsync();

            var vatsimPilots = net.Pilots.Where(p => p.Network == "VATSIM")
                .Select(p => (p.Lat, p.Lon, (double)p.HeadingDeg, FormatPilotLabel(p))).ToList();
            var ivaoPilots = net.Pilots.Where(p => p.Network == "IVAO")
                .Select(p => (p.Lat, p.Lon, (double)p.HeadingDeg, FormatPilotLabel(p))).ToList();
            // VATSIM controllers carry no lat/lon of their own -- EfbNetworkTrafficService already
            // dropped any it couldn't resolve via the transceivers feed, leaving Lat/Lon null.
            var vatsimAtcs = net.Atcs.Where(a => a.Network == "VATSIM" && a.Lat.HasValue && a.Lon.HasValue)
                .Select(a => (a.Lat!.Value, a.Lon!.Value, FormatAtcLabel(a))).ToList();
            var ivaoAtcs = net.Atcs.Where(a => a.Network == "IVAO" && a.Lat.HasValue && a.Lon.HasValue)
                .Select(a => (a.Lat!.Value, a.Lon!.Value, FormatAtcLabel(a))).ToList();

            DispatcherQueue.TryEnqueue(() =>
            {
                _renderer.UpdateVatsimTraffic(vatsimPilots);
                _renderer.UpdateVatsimAtc(vatsimAtcs);
                _renderer.UpdateIvaoTraffic(ivaoPilots);
                _renderer.UpdateIvaoAtc(ivaoAtcs);
            });
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Map] Network traffic refresh failed");
        }
    }

    private static string FormatPilotLabel(EfbNetworkPilot p) =>
        $"{p.Callsign} · {p.Network} · {(string.IsNullOrEmpty(p.AircraftType) ? "?" : p.AircraftType)} · " +
        $"{(string.IsNullOrEmpty(p.DepartureIcao) ? "?" : p.DepartureIcao)}→{(string.IsNullOrEmpty(p.ArrivalIcao) ? "?" : p.ArrivalIcao)}";

    private static string FormatAtcLabel(EfbNetworkAtc a) =>
        $"{a.Callsign} · {(string.IsNullOrEmpty(a.Position) ? a.Network : a.Position)}" +
        (a.Frequency > 0 ? $" · {a.Frequency:F3}" : "");

    // ── Pilot-detail panel (top-right, offset from the plan HUD) ───────────────────────────────

    private void OnPilotDetailCloseTapped(object sender, TappedRoutedEventArgs e)
    {
        _selectedPilotId = null;
        _selectedPilot = null;
        UpdatePilotDetailPanel();
    }

    // Matches MapPage.razor's MessageSelectedPilotAsync: add-or-get the friend, then navigate to
    // Messages. The original's Nav.NavigateTo($"/messages?open={pilotId}") query-parameter
    // approach has no equivalent on the native Frame-hosted MessagesPage, so this uses
    // Frame.Navigate's own parameter-passing mechanism instead (see MessagesPage.xaml.cs's new
    // OnNavigatedTo override for the receiving side).
    private async void OnMessagePilotTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_selectedPilot is null) return;

        await _messaging.AddOrGetFriendFromLiveMapAsync(_selectedPilot.PilotId, _selectedPilot.PilotName);
        // Bare type name, not "Pages.MessagesPage" -- MapPage and MessagesPage are both already in
        // the AviatesAirTracker.Pages namespace, matching DashboardPage.xaml.cs's own
        // Frame.Navigate(typeof(FlightsPage)) convention.
        this.Frame.Navigate(typeof(MessagesPage), _selectedPilot.PilotId);
    }

    // Matches MapPage.razor's declarative @if (_selectedPilot is not null) block exactly --
    // called from every place _selectedPilot changes (OnMapInfo's tap handler, the close button,
    // and RenderOtherPilots' per-poll refresh), not cached/bound once.
    private void UpdatePilotDetailPanel()
    {
        if (_selectedPilot is null)
        {
            PilotDetailPanel.Visibility = Visibility.Collapsed;
            return;
        }

        PilotDetailPanel.Visibility = Visibility.Visible;
        PilotNameText.Text = _selectedPilot.PilotName;
        PilotPhaseBadgeText.Text = _selectedPilot.Phase.ToUpperInvariant();
        PilotAltValueText.Text = $"{_selectedPilot.Alt} ft";
        PilotGsValueText.Text = $"{_selectedPilot.Speed} kts";
        PilotHdgValueText.Text = $"{_selectedPilot.Heading}°";

        var hasRoute = !string.IsNullOrEmpty(_selectedPilot.DepartureIcao) || !string.IsNullOrEmpty(_selectedPilot.ArrivalIcao);
        PilotRouteDivider.Visibility = hasRoute ? Visibility.Visible : Visibility.Collapsed;
        PilotRouteText.Visibility = hasRoute ? Visibility.Visible : Visibility.Collapsed;
        if (hasRoute)
            PilotRouteText.Text = $"{_selectedPilot.DepartureIcao ?? "----"} → {_selectedPilot.ArrivalIcao ?? "----"}";
    }

    // ── HUD text refresh ─────────────────────────────────────────────────────────────────────────

    // VM.HasActiveFlight/VM.NoFlightText re-read live on every call (from OnPageLoaded and every
    // OnMapRenderRequested tick), not cached once at construction -- matching the original's
    // declarative @if (!VM.HasActiveFlight) binding, which re-evaluates on every Blazor re-render.
    private void UpdateNoFlightOverlay()
    {
        NoFlightOverlayText.Visibility = _vm.HasActiveFlight ? Visibility.Collapsed : Visibility.Visible;
        NoFlightOverlayText.Text = _vm.NoFlightText.ToUpperInvariant();
    }

    // ── Reusable drag helper ─────────────────────────────────────────────────────────────────────

    // Attaches a ManipulationDelta-driven drag to `handle` that moves `panel` via its own
    // TranslateTransform (already declared as `panel`'s RenderTransform in XAML for every
    // draggable panel). Only `handle` (the thin drag-strip child, matching the original's
    // data-drag-handle div) gets ManipulationMode enabled -- the rest of the panel's content
    // (buttons, text) is untouched, so tapping e.g. "REFRESH OFP" inside a panel never starts a
    // drag. Genuinely shared, not copy-pasted: Task 5 calls this exact method again for the plan
    // HUD's own drag handle.
    private static void AttachDragHandle(UIElement handle, FrameworkElement panel)
    {
        if (panel.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            panel.RenderTransform = transform;
        }

        handle.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        handle.ManipulationDelta += (_, e) =>
        {
            transform.X += e.Delta.Translation.X;
            transform.Y += e.Delta.Translation.Y;
        };
    }
}
