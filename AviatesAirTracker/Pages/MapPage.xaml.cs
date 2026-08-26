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

    private readonly NativeMapRenderer _renderer;
    private readonly Mapsui.UI.WinUI.MapControl _mapControl;

    private HashSet<string> _renderedOtherPilotIds = new();

    // Set by OnMapInfo when an other-pilot marker is tapped (see the "IMPORTANT" hit-testing
    // wiring note in this task's brief). Task 5's pilot-detail panel reads this via the public
    // property below to know which pilot to show -- exposed as a property (not a bare field) so
    // this class's own build doesn't trip CS0414 ("assigned but never read") while nothing
    // consumes it yet.
    private string? _selectedPilotId;
    public string? SelectedOtherPilotId => _selectedPilotId;

    public MapPage()
    {
        InitializeComponent();

        _vm = App.GetService<MapViewModel>();
        _simBriefSvc = App.GetService<SimBriefService>();
        _otherPilots = App.GetService<OtherPilotsService>();

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

        // Reusable drag helper (ManipulationDelta + TranslateTransform) applied to the two
        // draggable panels this task builds. Task 5 reuses this exact same method for the plan
        // HUD's own drag handle -- deliberately a single shared method, not copy-pasted per panel.
        AttachDragHandle(IdentityDragHandle, IdentityPanel);
        AttachDragHandle(ProgressDragHandle, ProgressPanel);
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

        UpdateIdentityStrip();
        UpdateProgressStrip();
        UpdateNoFlightOverlay();
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
    }

    // ── SimBriefService.FlightPlanLoaded ────────────────────────────────────────────────────────

    // Plumbing only for now: MapViewModel.OnFlightPlanLoaded already sets its own dirty flag and
    // raises MapRenderRequested (which OnMapRenderRequested above already handles via
    // ConsumePlanSync -- the map layers update regardless of this subscription). This handler
    // exists so Task 5's Plan HUD (loading/loaded/error/no-plan states) has a live hook to build
    // its own state tracking on without having to touch this task's Loaded/Unloaded wiring.
    private void OnFlightPlanLoaded(object? sender, SimBriefFlightPlan plan)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Intentionally empty -- Task 5 fills this in alongside the Plan HUD panel it drives.
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
            // Task 5's pilot-detail panel reacts to this (resolves the pilot from
            // OtherPilotsService.LatestPositions and shows itself) -- out of scope here.
            _selectedPilotId = pilotId;
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
