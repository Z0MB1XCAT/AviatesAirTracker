using AviatesAirTracker.Models;
using AviatesAirTracker.Services;
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
using System.Reflection;
using System.Threading.Tasks;
using Windows.UI;

namespace AviatesAirTracker.Pages;

/// <summary>
/// Native reproduction of AviatesAirTracker.LegacyWeb\Pages\MapPage.razor. Hosts a
/// Mapsui.UI.WinUI.MapControl wrapping a NativeMapRenderer (the Tasks 1-3 rendering layer, the
/// direct analogue of wwwroot/js/interop.js's window.aviatesMap) plus the identity/progress/
/// map-controls HUD panels and the "no active flight" overlay. The plan HUD and pilot-detail
/// panel (top-right) are Task 5's job -- this page only wires the plumbing (the OtherPilotClicked-
/// equivalent hit-testing storage field, the SimBriefService.FlightPlanLoaded subscription) that
/// Task 5 builds its UI on top of, per the plan's explicit "don't make Task 5 touch this task's
/// Loaded handler" goal.
/// </summary>
public sealed partial class MapPage : Page
{
    private readonly MapViewModel _vm;
    private readonly SimBriefService _simBriefSvc;
    private readonly OtherPilotsService _otherPilots;
    private readonly SettingsService _settings;
    private readonly FlightSessionManager _flightSession;
    private readonly MessagingService _messaging;

    private readonly NativeMapRenderer _renderer;
    private readonly Mapsui.UI.WinUI.MapControl _mapControl;

    private HashSet<string> _renderedOtherPilotIds = new();

    // Set by OnMapInfo when an other-pilot marker is tapped (see the "IMPORTANT" hit-testing
    // wiring note in this task's brief). Exposed as a property (not a bare field) so this class's
    // own build doesn't trip CS0414 ("assigned but never read"); the pilot-detail panel below is
    // now the consumer.
    private string? _selectedPilotId;
    public string? SelectedOtherPilotId => _selectedPilotId;

    // ── Task 5 state: pilot-detail panel + plan HUD ─────────────────────────────────────────────
    // Mirrors MapPage.razor's own @code fields (_selectedPilot / _planLoading / _planError /
    // _currentPlan) exactly -- page-local UI state, not part of MapViewModel.
    private LivePilotPosition? _selectedPilot;
    private bool _planLoading;
    private string? _planError;
    private SimBriefFlightPlan? _currentPlan;

    public MapPage()
    {
        InitializeComponent();

        _vm = App.GetService<MapViewModel>();
        _simBriefSvc = App.GetService<SimBriefService>();
        _otherPilots = App.GetService<OtherPilotsService>();
        _settings = App.GetService<SettingsService>();
        _flightSession = App.GetService<FlightSessionManager>();
        _messaging = App.GetService<MessagingService>();

        // NativeMapRenderer(map: null) creates and owns its own Mapsui.Map (see its own doc
        // comment: "works both standalone ... or pre-wired to a MapControl") -- handing that same
        // Map instance to the MapControl below is what "pre-wires" it. NativeMapRenderer.Init()
        // (called in Loaded) mutates that Map's Layers collection in place, so it doesn't matter
        // that Init() hasn't run yet at the point the MapControl takes the reference here.
        _renderer = new NativeMapRenderer();
        _mapControl = new Mapsui.UI.WinUI.MapControl { Map = _renderer.Map };
        MapHost.Children.Add(_mapControl);

        // Single hit-testing handler for both waypoint-name and other-pilot-marker taps, per the
        // "IMPORTANT" note: NativeMapRenderer only owns a Mapsui.Map (not a MapControl), so it
        // cannot itself subscribe to pointer hit-testing -- that requires the actual MapControl
        // instance, which only this page has. GetOtherPilotId is checked first, GetWaypointName
        // second, exactly as directed -- one hit-testing path, not two redundant ones.
        _mapControl.Info += OnMapInfo;

        UpdateIdentityStrip();
        UpdateProgressStrip();
        UpdateNoFlightOverlay();
        UpdatePlanHud();
        UpdatePilotDetailPanel();

        // Reusable drag helper (ManipulationDelta + TranslateTransform), shared across every
        // draggable panel rather than reimplemented per panel.
        AttachDragHandle(IdentityDragHandle, IdentityPanel);
        AttachDragHandle(ProgressDragHandle, ProgressPanel);
        AttachDragHandle(PlanDragHandle, PlanHudPanel);
        // Pilot-detail panel: the original's markup has a drag handle but never actually calls
        // initDrag on it (a confirmed pre-existing bug -- it looks draggable but isn't). This port
        // wires real dragging here since the panel is structurally identical to the ones that
        // already drag correctly -- a small, defensible fix, not a silent behavior change (called
        // out in the Task 5 commit message/report).
        AttachDragHandle(PilotDetailDragHandle, PilotDetailPanel);
        // MapControlsPanel intentionally has no drag handle and no call to AttachDragHandle here --
        // matching the original's hud--br panel exactly (bottom-right map controls do not drag).

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────────────────────

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _renderer.Init(_vm.Lat, _vm.Lon);

        // MapViewModel.MapRenderRequested fires from the telemetry pump (confirmed off the UI
        // thread during this phase's research); OtherPilotsService.PositionsUpdated fires from its
        // own background System.Threading.Timer poll; SimBriefService.FlightPlanLoaded fires from
        // an async fetch continuation. All three are marshaled via DispatcherQueue.TryEnqueue in
        // their handlers below, matching the Loaded/Unloaded subscribe-unsubscribe shape
        // MessagesPage.xaml.cs/SettingsPage.xaml.cs already established.
        _vm.MapRenderRequested += OnMapRenderRequested;
        _otherPilots.PositionsUpdated += OnOtherPilotsUpdated;
        _simBriefSvc.FlightPlanLoaded += OnFlightPlanLoaded;

        // Mirrors MapPage.razor's OnAfterRenderAsync(firstRender): render whatever other-pilot
        // positions and planned route already exist the moment this page opens, not just future
        // updates.
        RenderOtherPilots(_otherPilots.LatestPositions);

        var (plan, _) = _vm.ConsumePlanSync();
        if (plan is not null)
            PushPlannedRoute(plan);

        // Mirrors MapPage.razor's OnAfterRenderAsync(firstRender): pick up whatever SimBrief plan
        // was already loaded (e.g. via ACARS) before this page was opened, so the Plan HUD doesn't
        // show "NO FLIGHT PLAN" for a plan that already exists.
        _currentPlan = _simBriefSvc.CurrentPlan;

        UpdateIdentityStrip();
        UpdateProgressStrip();
        UpdateNoFlightOverlay();
        UpdatePlanHud();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _vm.MapRenderRequested -= OnMapRenderRequested;
        _otherPilots.PositionsUpdated -= OnOtherPilotsUpdated;
        _simBriefSvc.FlightPlanLoaded -= OnFlightPlanLoaded;
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

                UpdateIdentityStrip();
                UpdateProgressStrip();
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

    // ── SimBriefService.FlightPlanLoaded ────────────────────────────────────────────────────────

    // MapViewModel.OnFlightPlanLoaded already sets its own dirty flag and raises
    // MapRenderRequested (which OnMapRenderRequested above already handles via ConsumePlanSync --
    // the map layers update regardless of this subscription). This handler drives the Plan HUD's
    // own state, matching MapPage.razor's OnFlightPlanLoaded exactly (`_currentPlan = plan;
    // _planError = null; StateHasChanged();`).
    private void OnFlightPlanLoaded(object? sender, SimBriefFlightPlan plan)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _currentPlan = plan;
            _planError = null;
            UpdatePlanHud();
        });
    }

    // ── Hit-testing: MapControl.Info (single handler for waypoint names + other-pilot taps) ────

    private void OnMapInfo(object? sender, Mapsui.MapInfoEventArgs e)
    {
        var feature = e.MapInfo?.Feature;
        if (feature is null)
        {
            HideWaypointPopup();
            return;
        }

        var pilotId = NativeMapRenderer.GetOtherPilotId(feature);
        if (pilotId is not null)
        {
            HideWaypointPopup();
            _selectedPilotId = pilotId;
            // Matches MapPage.razor's [JSInvokable] OnOtherPilotClicked: resolve the tapped pilot
            // from OtherPilotsService.LatestPositions and show the pilot-detail panel.
            _selectedPilot = _otherPilots.LatestPositions.FirstOrDefault(p => p.PilotId == pilotId);
            UpdatePilotDetailPanel();
            return;
        }

        var waypointName = NativeMapRenderer.GetWaypointName(feature);
        if (waypointName is not null && e.MapInfo?.ScreenPosition is { } screenPosition)
        {
            ShowWaypointPopup(waypointName, screenPosition);
            return;
        }

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

    // ── Plan HUD (top-right, draggable) — loading / loaded / error-and-no-username / no-plan ───

    private async void OnImportOfpTapped(object sender, TappedRoutedEventArgs e) => await ImportOfpAsync();

    private async void OnRefreshOfpTapped(object sender, TappedRoutedEventArgs e) => await ImportOfpAsync();

    private void OnConfigureTapped(object sender, TappedRoutedEventArgs e) =>
        this.Frame.Navigate(typeof(SettingsPage));

    // Matches MapPage.razor's ImportSimBriefAsync exactly, including the confirmed pre-existing
    // inconsistency the design spec flagged: unlike ACARS's own OFP import, this does NOT call
    // Settings.SaveSimBriefPlanAsync -- the fetched plan is handed to FlightSessionManager and
    // broadcast via SimBriefService.NotifyPlanLoaded, but never persisted to disk from here. Left
    // exactly as-is per the plan's explicit "don't fix this unilaterally" instruction.
    private async Task ImportOfpAsync()
    {
        var username = _settings.Settings.SimBriefUsername;
        if (string.IsNullOrWhiteSpace(username)) return;

        _planLoading = true;
        _planError = null;
        UpdatePlanHud();

        try
        {
            var plan = await _simBriefSvc.FetchLatestOFPAsync(username);
            if (plan is not null)
            {
                _currentPlan = plan;
                _flightSession.AssignSimBriefPlan(plan);
                _simBriefSvc.NotifyPlanLoaded(plan);
            }
            else
            {
                _planError = "NO OFP FOUND";
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Map] SimBrief OFP fetch failed");
            _planError = "FETCH FAILED";
        }
        finally
        {
            _planLoading = false;
            UpdatePlanHud();
        }
    }

    // Matches MapPage.razor's declarative plan-HUD @if/@else chain: loading takes priority, then
    // loaded (a plan exists), then the empty branch which itself splits on whether a SimBrief
    // username is configured (no-username shows "Configure", otherwise "Import OFP") -- with an
    // optional error line shown above either empty sub-state when the last fetch failed.
    private void UpdatePlanHud()
    {
        PlanLoadingPanel.Visibility = _planLoading ? Visibility.Visible : Visibility.Collapsed;

        var showLoaded = !_planLoading && _currentPlan is not null;
        PlanLoadedPanel.Visibility = showLoaded ? Visibility.Visible : Visibility.Collapsed;
        if (showLoaded)
        {
            PlanRouteText.Text = $"{_currentPlan!.DepartureICAO} → {_currentPlan.ArrivalICAO}";
            PlanDetailText.Text = $"{_currentPlan.AircraftType}  ·  {_currentPlan.Waypoints.Count} WPT";
        }

        var showEmpty = !_planLoading && _currentPlan is null;
        PlanEmptyPanel.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
        if (showEmpty)
        {
            PlanErrorText.Visibility = _planError is not null ? Visibility.Visible : Visibility.Collapsed;
            PlanErrorDivider.Visibility = _planError is not null ? Visibility.Visible : Visibility.Collapsed;
            PlanErrorText.Text = _planError ?? "";

            var noUsername = string.IsNullOrWhiteSpace(_settings.Settings.SimBriefUsername);
            PlanNoUsernamePanel.Visibility = noUsername ? Visibility.Visible : Visibility.Collapsed;
            PlanNoPlanPanel.Visibility = noUsername ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    // ── HUD text refresh ─────────────────────────────────────────────────────────────────────────

    private void UpdateIdentityStrip()
    {
        RouteLabelText.Text = _vm.RouteLabel;
        UpdatePhaseBadge();
        AltValueText.Text = _vm.AltText;
        VsValueText.Text = _vm.VsText;
        IasValueText.Text = _vm.IasText;
        GsValueText.Text = _vm.GsText;
        HdgValueText.Text = _vm.HdgText;
        MachValueText.Text = _vm.MachText;
    }

    // Ported from MapPage.razor's inline style="background:@(VM.PhaseColor)22;border-color:@(VM.PhaseColor)66;color:@VM.PhaseColor;"
    // -- CSS hex-with-alpha-suffix is RRGGBBAA (alpha appended after the base color), so 0x22/0x66
    // become the alpha channel of a Color built from PhaseColor's own R/G/B.
    private void UpdatePhaseBadge()
    {
        var baseColor = ParseHexColor(_vm.PhaseColor);
        PhaseBadgeBorder.Background = new SolidColorBrush(Color.FromArgb(0x22, baseColor.R, baseColor.G, baseColor.B));
        PhaseBadgeBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, baseColor.R, baseColor.G, baseColor.B));
        PhaseBadgeText.Foreground = new SolidColorBrush(baseColor);
        PhaseBadgeText.Text = _vm.PhaseText;
    }

    private static Color ParseHexColor(string hex)
    {
        hex = hex.TrimStart('#');
        var r = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
        var g = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
        var b = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
        return Color.FromArgb(0xFF, r, g, b);
    }

    private void UpdateProgressStrip()
    {
        NextWaypointText.Text = _vm.NextWaypoint;
        EtaText.Text = _vm.EtaZ;
        DistRemValueText.Text = _vm.DistRemText;
        FuelRemValueText.Text = _vm.FuelRemText;
    }

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
