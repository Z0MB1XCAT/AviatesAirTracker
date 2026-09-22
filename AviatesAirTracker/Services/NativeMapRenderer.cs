using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BruTile;
using BruTile.Cache;
using BruTile.Predefined;
using BruTile.Web;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling.Layers;
using NetTopologySuite.Geometries;

namespace AviatesAirTracker.Services;

/// <summary>
/// Native Mapsui-backed rendering layer for the WinUI 3 Map page -- the direct analogue of
/// window.aviatesMap (wwwroot/js/interop.js). Owns one Mapsui.Map and exposes the subset of the
/// 17-function JS API this app actually calls; resetPath/setFlightPath/invalidateSize have zero
/// callers anywhere in the repo and are deliberately not ported.
///
/// Task 1: tile layer, own-aircraft marker + rotation, flown-path polyline capped at a
/// 2000-point ring buffer (drop-oldest). Task 2: altitude-colored path segments, planned route +
/// waypoint markers, airport markers. Task 3: other-pilot markers + their route lines, and the
/// pan/zoom/center wrappers -- this class now covers the full window.aviatesMap API surface this
/// app actually calls.
/// </summary>
public sealed class NativeMapRenderer : IDisposable
{
    // Originally ported exact from interop.js's CARTO Dark tile source (see git history), but
    // CARTO's basemaps.cartocdn.com is an unauthenticated, no-SLA free/demo endpoint -- it serves
    // a tiled "API KEY REQUIRED" watermark (still HTTP 200, fails silently) at its own discretion
    // once it decides to throttle, independent of request headers (re-verified directly via curl:
    // burst concurrency, empty User-Agent, and a real User-Agent all returned real tiles in
    // isolation, yet the watermark still appeared live in the running app -- so the earlier
    // "missing User-Agent" theory doesn't actually explain it). Swapped to Esri's World Dark Gray
    // Base tile service instead: no API key, no signup, no per-IP throttling observed, similar
    // dark/muted aesthetic. Confirmed via curl: real JPEG tiles at z0-16 (native), z17-18 return a
    // valid (if softly upsampled/over-zoomed) tile rather than an error, so MaxZoomLevel below is
    // left at 18 -- no crash or blank tiles at max native zoom.
    private const string TileUrlTemplate = "https://services.arcgisonline.com/arcgis/rest/services/Canvas/World_Dark_Gray_Base/MapServer/tile/{z}/{y}/{x}";
    private const string TileAttribution = "Esri, HERE, Garmin, FAO, NOAA, USGS, © OpenStreetMap contributors, GIS User Community";

    private const string TileUserAgent = "AviatesAirTracker-WinUI3/1.0";

    // Every tile fetch previously went over the network every time (including re-panning back to
    // an area already viewed this session), which is the "takes a while to load" / "white boxes
    // that get filled in" symptom -- the original Leaflet/WebView2 page never showed this because
    // the browser's own HTTP disk cache absorbed repeat tile requests for free; Mapsui's
    // HttpTileSource has no cache of its own beyond an in-memory one that resets every launch.
    // BruTile ships a ready-made on-disk IPersistentCache implementation (BruTile.Cache.FileCache)
    // for exactly this -- wiring it in means a tile fetched once is read from disk (near-instant)
    // on every later view, including across app restarts. 90 days is a conservative compromise
    // between "effectively permanent" (matching Esri's own observed Cache-Control: max-age=180
    // days) and not letting a cached tile go stale forever if the basemap imagery is ever updated.
    private static readonly TimeSpan TileCacheExpiry = TimeSpan.FromDays(90);
    private static readonly string TileCacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AviatesAirTracker", "map_tile_cache");

    private const int MinZoomLevel = 3;
    // Capped at the user's explicit request (14 was chosen directly, not derived) -- combined with
    // the OverrideZoomBounds fix in Init(), this is now a hard ceiling: the tile schema itself
    // never defines a level past 14, so there's no resolution the + button/scroll/pinch could
    // reach that would ask for one.
    private const int MaxZoomLevel = 14;
    private const int InitialZoomLevel = 7;

    // Matches the original's ring-buffer cap on the recorded flown path (RouteTracker /
    // interop.js's path-array trimming) so very long flights don't grow memory unbounded.
    private const int FlownPathCap = 2000;

    private const string AircraftColorHex = "#3B82F6";
    private const string FlownPathColorHex = "#3D7EEE";

    // Confirmed exact from interop.js's updateFlightPathAltitude/altToColor (lines ~254-267):
    //   if (altFt <= 100) return 'hsl(0,0%,90%)';
    //   var t = Math.min((altFt - 100) / 44900, 1.0);
    //   return 'hsl(' + Math.round(270 * (1 - t)) + ',55%,42%)';
    // Ported verbatim below in AltitudeToColor(). Segment opacity (0.85) and weight (3) are also
    // confirmed exact from the same function's L.polyline options.
    private const double AltitudeRampMinFt = 100.0;
    private const double AltitudeRampRangeFt = 44900.0; // 45000 - 100
    private const float AltitudeRampSaturation = 0.55f;
    private const float AltitudeRampLightness = 0.42f;
    private const float AltitudePathOpacity = 0.85f;
    private const double AltitudePathWeight = 3.0;

    // Confirmed exact from interop.js's init()/setPlannedRoute() (_plannedLine + waypoint dots).
    private const string PlannedRouteColorHex = "#93C5FD";
    private const double PlannedRouteWeight = 2.0;
    private const float PlannedRouteOpacity = 0.6f;
    private static readonly float[] PlannedRouteDashArray = { 8f, 5f };

    private const string WaypointFillColorHex = "#1E3A5F";
    private const string WaypointOutlineColorHex = "#60A5FA";
    private const double WaypointOutlineWidth = 1.5;
    // Leaflet's L.circleMarker radius is 3px (diameter 6px); Mapsui's SymbolStyle has no direct
    // pixel-radius knob, only a scale relative to SymbolStyle.DefaultWidth/Height (32px), so
    // 6/32 = 0.1875 is the closest equivalent -- an approximation, noted in the Task 2 report.
    private const double WaypointSymbolScale = 6.0 / 32.0;

    // Confirmed exact from interop.js's setPlannedRoute() thinning: step = max(1, floor(named.length / 80)).
    private const int MaxLabeledWaypoints = 80;

    // Confirmed exact from interop.js's addAirportMarker(): colors { dep, arr, alt } and the
    // "ALT " (trailing space) prefix applied only to the alt marker's label.
    private const string DepAirportColorHex = "#10B981";
    private const string ArrAirportColorHex = "#F59E0B";
    private const string AltAirportColorHex = "#A855F7";
    private const string AltAirportLabelPrefix = "ALT ";

    // Confirmed exact from interop.js's fitRouteBounds(): L.map.fitBounds(bounds, { padding: [48, 48], maxZoom: 10 }).
    private const double FitBoundsPaddingPx = 48.0;
    private const int FitBoundsMaxZoomLevel = 10;

    // Confirmed exact from interop.js's upsertOtherPilot() marker (L.divIcon, 14x14px, white fill
    // @ 55% opacity, 2px blue-ish border @ 90% opacity) and its per-pilot route line
    // (color/weight/opacity/dashArray on the L.polyline).
    private const string OtherPilotFillColorHex = "#FFFFFF";
    private const float OtherPilotFillOpacity = 0.55f;
    private const string OtherPilotOutlineColorHex = "#3D7EEE";
    private const float OtherPilotOutlineOpacity = 0.9f;
    private const double OtherPilotOutlineWidth = 2.0;
    // Same approximation as WaypointSymbolScale above: Mapsui's SymbolStyle has no direct
    // pixel-radius knob, only a scale relative to SymbolStyle.DefaultWidth/Height (32px), so
    // 14/32 is the closest equivalent to the original's 14px-diameter divIcon.
    private const double OtherPilotSymbolScale = 14.0 / 32.0;

    private const string OtherPilotRouteColorHex = "#3D7EEE";
    private const double OtherPilotRouteWeight = 1.5;
    private const float OtherPilotRouteOpacity = 0.45f;
    private static readonly float[] OtherPilotRouteDashArray = { 4f, 5f };

    // Weather radar: RainViewer's public tile API (no key required), same source the EFB web map
    // uses. This renderer only ever receives an already-resolved tile URL template (the frame path
    // changes over time) -- it does no HTTP metadata fetching itself, matching every other method
    // on this class taking already-resolved primitives rather than owning network calls.
    private const float WeatherRadarOpacity = 0.55f;

    // VATSIM + IVAO online traffic/ATC. Same color convention as the EFB web map's own layer
    // toggles (efb.css/app.js) so a pilot switching between the desktop app and the EFB tablet
    // sees the same two colors mean the same two networks.
    private const string VatsimColorHex = "#3B82F6";
    private const string IvaoColorHex = "#A855F7";
    // Distinct from OtherPilotSymbolScale/AircraftColorHex above -- this app's OWN traffic
    // (OtherPilotsService) and the aircraft marker use their own established colors/shapes;
    // network traffic gets its own triangle (heading-rotated, like the aircraft marker) so it
    // reads as "another aircraft" rather than the plain dot OtherPilotsService's traffic uses.
    private const double NetworkTrafficSymbolScale = 0.55;
    private const double NetworkAtcSymbolScale = 10.0 / 32.0; // ~10px circle, see WaypointSymbolScale's own note on this approximation

    // Confirmed exact from interop.js's panTo(): this._map.panTo([lat, lon], { animate: true,
    // duration: 0.5 }) -- 0.5s == 500ms, which Mapsui's Navigator takes directly.
    private const long PanAnimationDurationMs = 500;
    // Leaflet animates zoomIn()/zoomOut() by default via a ~0.25s CSS transition baked into its
    // default map options; there is no single exported JS constant to port verbatim (it's a CSS
    // rule, not a JS value), so 250ms is used here as a close, standard approximation of the same
    // felt behavior rather than an invented arbitrary duration.
    private const long ZoomAnimationDurationMs = 250;

    public Mapsui.Map Map { get; }

    private MemoryLayer? _aircraftLayer;
    private MemoryLayer? _flownPathLayer;
    private MemoryLayer? _flownPathAltitudeLayer;
    private MemoryLayer? _plannedRouteLayer;
    private MemoryLayer? _waypointLayer;
    private MemoryLayer? _airportMarkerLayer;
    private MemoryLayer? _otherPilotLayer;
    private MemoryLayer? _otherPilotRouteLayer;

    private TileLayer? _weatherLayer;
    private MemoryLayer? _vatsimTrafficLayer;
    private MemoryLayer? _vatsimAtcLayer;
    private MemoryLayer? _ivaoTrafficLayer;
    private MemoryLayer? _ivaoAtcLayer;

    private PointFeature? _aircraftFeature;
    private readonly List<Coordinate> _flownPathPoints = new();

    // Used only by FitRouteBounds() to reproduce interop.js's fitRouteBounds() combining the
    // planned-route line points with the airport marker points.
    private readonly List<MPoint> _plannedRoutePoints = new();
    private readonly List<MPoint> _airportMarkerPoints = new();
    private readonly List<IFeature> _airportMarkerFeatures = new();

    // Keyed by pilotId, matching interop.js's _otherPilotMarkers/_otherPilotRoutes dictionaries --
    // create-or-move on UpsertOtherPilot, dropped on RemoveOtherPilot.
    private readonly Dictionary<string, PointFeature> _otherPilotFeatures = new();
    private readonly Dictionary<string, GeometryFeature> _otherPilotRouteFeatures = new();

    /// <summary>
    /// Wraps/owns a Mapsui.Map. Accepts an externally-constructed map (e.g. one already bound to
    /// a MapControl) or creates its own when none is supplied -- implementer's call per the plan,
    /// chosen so this class works both standalone (unit-testable) and pre-wired to a MapControl.
    /// </summary>
    public NativeMapRenderer(Mapsui.Map? map = null)
    {
        Map = map ?? new Mapsui.Map();
    }

    public void Init(double lat, double lon)
    {
        Map.Layers.Clear();

        var tileSchema = new GlobalSphericalMercator(MinZoomLevel, MaxZoomLevel);
        var tileCache = new FileCache(TileCacheDirectory, "jpg", TileCacheExpiry);
        var tileSource = new HttpTileSource(
            tileSchema,
            TileUrlTemplate,
            name: "Esri Dark Gray",
            persistentCache: tileCache,
            attribution: new Attribution(TileAttribution),
            userAgent: TileUserAgent);
        Map.Layers.Add(new TileLayer(new BoundsCheckedTileSource(tileSource)) { Name = "Base" });

        // Mapsui's own zoom-bounds calculation (Map.GetMinMaxResolution, confirmed by decompiling
        // Mapsui.dll) lets the deepest reachable resolution go to HALF of the schema's smallest
        // defined resolution -- i.e. it lets the + button/scroll zoom in "one extra level" beyond
        // MaxZoomLevel by default. BoundsCheckedTileSource above already guards against any tile
        // that ends up genuinely out of range, but the user still hit the placeholder in that
        // over-zoom band in live testing (reported: the 14th zoom-in click broke, the 13th didn't).
        // Overriding ZoomBounds to exactly the schema's own min/max resolution removes that extra
        // half-level of headroom entirely, so the + button/scroll/pinch physically cannot zoom
        // past MaxZoomLevel's native resolution -- confirmed with an isolated probe that simulated
        // 20 consecutive ZoomIn() calls and never left the schema's defined range.
        var schemaResolutions = tileSchema.Resolutions.Values.Select(r => r.UnitsPerPixel).ToList();
        Map.Navigator.OverrideZoomBounds = new MMinMax(schemaResolutions.Min(), schemaResolutions.Max());

        _aircraftFeature = null;
        _aircraftLayer = new MemoryLayer
        {
            Name = "Aircraft",
            Features = Array.Empty<IFeature>(),
            Style = null,
        };
        Map.Layers.Add(_aircraftLayer);

        _flownPathPoints.Clear();
        _flownPathLayer = new MemoryLayer
        {
            Name = "FlownPath",
            Features = Array.Empty<IFeature>(),
            Style = null,
        };
        Map.Layers.Add(_flownPathLayer);

        _plannedRouteLayer = new MemoryLayer
        {
            Name = "PlannedRoute",
            Features = Array.Empty<IFeature>(),
            Style = null,
        };
        Map.Layers.Add(_plannedRouteLayer);

        _flownPathAltitudeLayer = new MemoryLayer
        {
            Name = "FlownPathAltitude",
            Features = Array.Empty<IFeature>(),
            Style = null,
        };
        Map.Layers.Add(_flownPathAltitudeLayer);

        _waypointLayer = new MemoryLayer
        {
            Name = "Waypoints",
            Features = Array.Empty<IFeature>(),
            Style = null,
            // MapControl.GetMapInfo/Info hit-testing (Mapsui.Rendering.Skia.MapRenderer.GetMapInfo)
            // filters candidate layers with `where l.IsMapInfoLayer` before doing any pixel-hit
            // work -- it defaults to false, so without this, hovering/tapping a waypoint dot can
            // never resolve a feature no matter how precise the hit is. Confirmed by decompiling
            // Mapsui.Rendering.Skia.dll's MapRenderer.GetMapInfo.
            IsMapInfoLayer = true,
        };
        Map.Layers.Add(_waypointLayer);

        _airportMarkerLayer = new MemoryLayer
        {
            Name = "AirportMarkers",
            Features = Array.Empty<IFeature>(),
            Style = null,
        };
        Map.Layers.Add(_airportMarkerLayer);

        _otherPilotRouteLayer = new MemoryLayer
        {
            Name = "OtherPilotRoutes",
            Features = Array.Empty<IFeature>(),
            Style = null,
        };
        Map.Layers.Add(_otherPilotRouteLayer);

        _otherPilotLayer = new MemoryLayer
        {
            Name = "OtherPilots",
            Features = Array.Empty<IFeature>(),
            Style = null,
            // Same reason as _waypointLayer above -- required for tap hit-testing to resolve a
            // pilotId via MapControl.Info.
            IsMapInfoLayer = true,
        };
        Map.Layers.Add(_otherPilotLayer);

        // Weather radar has no layer yet -- SetWeatherRadarTile() creates it lazily (and inserts
        // it directly above "Base", index 1, so vector overlays still render above it) the first
        // time the page has a tile URL to give it; there's nothing to add here at Init() time.
        _weatherLayer = null;

        _vatsimTrafficLayer = new MemoryLayer
        {
            Name = "VatsimTraffic",
            Features = Array.Empty<IFeature>(),
            Style = null,
            IsMapInfoLayer = true, // same reason as _waypointLayer above -- needed for hover labels
        };
        Map.Layers.Add(_vatsimTrafficLayer);

        _vatsimAtcLayer = new MemoryLayer
        {
            Name = "VatsimAtc",
            Features = Array.Empty<IFeature>(),
            Style = null,
            IsMapInfoLayer = true,
        };
        Map.Layers.Add(_vatsimAtcLayer);

        _ivaoTrafficLayer = new MemoryLayer
        {
            Name = "IvaoTraffic",
            Features = Array.Empty<IFeature>(),
            Style = null,
            IsMapInfoLayer = true,
        };
        Map.Layers.Add(_ivaoTrafficLayer);

        _ivaoAtcLayer = new MemoryLayer
        {
            Name = "IvaoAtc",
            Features = Array.Empty<IFeature>(),
            Style = null,
            IsMapInfoLayer = true,
        };
        Map.Layers.Add(_ivaoAtcLayer);

        _plannedRoutePoints.Clear();
        _airportMarkerPoints.Clear();
        _airportMarkerFeatures.Clear();
        _otherPilotFeatures.Clear();
        _otherPilotRouteFeatures.Clear();

        var (x, y) = SphericalMercator.FromLonLat(lon, lat);
        Map.Navigator.CenterOnAndZoomTo(new MPoint(x, y), GetResolutionForLevel(InitialZoomLevel));
    }

    /// <summary>
    /// <c>Map.Navigator.Resolutions</c> is built by merging every layer's own <c>Resolutions</c>
    /// list (<see cref="Mapsui.Map.LayersChanged"/> -&gt; <c>DetermineResolutions</c>, confirmed by
    /// decompiling Mapsui.dll) -- it is NOT a fixed 0-based "web mercator zoom level" table.
    /// <c>TileLayer.Resolutions</c> only contains the levels the schema actually defines
    /// (<see cref="MinZoomLevel"/>..<see cref="MaxZoomLevel"/>), so list index 0 corresponds to
    /// level <see cref="MinZoomLevel"/>, not level 0. Indexing <c>Navigator.Resolutions</c>
    /// directly by a real zoom level number (as the previous code did for
    /// <see cref="InitialZoomLevel"/>/<see cref="FitBoundsMaxZoomLevel"/>) silently zoomed
    /// <see cref="MinZoomLevel"/> levels deeper than intended -- confirmed with an isolated probe
    /// that replicated this exact Map/TileLayer setup: <c>Navigator.Resolutions[7]</c> resolved to
    /// the schema's level-10 resolution, not level 7, given <see cref="MinZoomLevel"/> = 3. This
    /// helper converts an intended absolute level into the correct list index.
    /// </summary>
    private double GetResolutionForLevel(int level)
    {
        var index = level - MinZoomLevel;
        var resolutions = Map.Navigator.Resolutions;
        if (index < 0)
        {
            index = 0;
        }
        else if (index >= resolutions.Count)
        {
            index = resolutions.Count - 1;
        }

        return resolutions[index];
    }

    public void UpdatePosition(double lat, double lon, double heading)
    {
        var (x, y) = SphericalMercator.FromLonLat(lon, lat);

        if (_aircraftFeature == null)
        {
            _aircraftFeature = new PointFeature(new MPoint(x, y));
            _aircraftFeature.Styles.Add(new SymbolStyle
            {
                SymbolScale = 0.8,
                SymbolType = SymbolType.Triangle,
                RotateWithMap = true,
                SymbolRotation = heading,
                Fill = new Brush(Color.FromString(AircraftColorHex)),
                Outline = new Pen(Color.White, 1),
            });
        }
        else
        {
            _aircraftFeature.Point.X = x;
            _aircraftFeature.Point.Y = y;
            var style = _aircraftFeature.Styles.OfType<SymbolStyle>().FirstOrDefault();
            if (style != null)
            {
                style.SymbolRotation = heading;
            }
        }

        if (_aircraftLayer != null)
        {
            _aircraftLayer.Features = new IFeature[] { _aircraftFeature };
            _aircraftLayer.DataHasChanged();
        }

        _flownPathPoints.Add(new Coordinate(x, y));
        if (_flownPathPoints.Count > FlownPathCap)
        {
            _flownPathPoints.RemoveAt(0);
        }

        RebuildFlownPathFeature();
    }

    private void RebuildFlownPathFeature()
    {
        if (_flownPathLayer == null)
        {
            return;
        }

        if (_flownPathPoints.Count < 2)
        {
            _flownPathLayer.Features = Array.Empty<IFeature>();
            _flownPathLayer.DataHasChanged();
            return;
        }

        var geometry = new LineString(_flownPathPoints.ToArray());
        var feature = new GeometryFeature { Geometry = geometry };
        feature.Styles.Add(new VectorStyle
        {
            Line = new Pen(Color.FromString(FlownPathColorHex), 3),
        });

        _flownPathLayer.Features = new IFeature[] { feature };
        _flownPathLayer.DataHasChanged();
    }

    /// <summary>
    /// Altitude-colored flown path (Task 2). The original (interop.js's updateFlightPathAltitude)
    /// renders this as ~2000 individual 1-segment polylines, each colored via the shared
    /// altToColor() ramp evaluated at the segment's average altitude, on a shared canvas renderer,
    /// rebuilt from scratch every call. Mapsui has no per-vertex-colored line style, so this ports
    /// the same "one feature per segment" approach. Callers should only invoke this when the path
    /// has actually changed (the page gates this via <c>MapViewModel.GetPathIfChanged()</c>
    /// returning non-null) -- this method itself has no change-detection and always rebuilds.
    /// </summary>
    public void UpdateFlightPathAltitude(IReadOnlyList<(double Lat, double Lon, double AltFt)> points)
    {
        if (_flownPathAltitudeLayer == null)
        {
            return;
        }

        if (points == null || points.Count < 2)
        {
            _flownPathAltitudeLayer.Features = Array.Empty<IFeature>();
            _flownPathAltitudeLayer.DataHasChanged();
            return;
        }

        var projected = new (double X, double Y, double AltFt)[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            var (x, y) = SphericalMercator.FromLonLat(points[i].Lon, points[i].Lat);
            projected[i] = (x, y, points[i].AltFt);
        }

        var segments = new IFeature[projected.Length - 1];
        for (var i = 0; i < projected.Length - 1; i++)
        {
            var p1 = projected[i];
            var p2 = projected[i + 1];
            var geometry = new LineString(new[] { new Coordinate(p1.X, p1.Y), new Coordinate(p2.X, p2.Y) });
            var feature = new GeometryFeature { Geometry = geometry };
            var avgAltFt = (p1.AltFt + p2.AltFt) / 2.0;
            feature.Styles.Add(new VectorStyle
            {
                Line = new Pen(AltitudeToColor(avgAltFt), AltitudePathWeight),
            });
            segments[i] = feature;
        }

        _flownPathAltitudeLayer.Features = segments;
        _flownPathAltitudeLayer.DataHasChanged();
    }

    /// <summary>
    /// Exact port of interop.js's altToColor(): a fixed light-gray for near-ground altitudes,
    /// else a 270 (purple) -&gt; 0 (red) hue ramp normalized between 100 ft and 45,000 ft, at fixed
    /// 55% saturation / 42% lightness, with the opacity (0.85, matching the original's L.polyline
    /// `opacity` option) applied via alpha rather than a separate style property so it survives
    /// regardless of whether the renderer honors per-style Opacity for line strokes.
    /// <c>Mapsui.Styles.Color.FromHsl</c> was verified empirically (hue as a 0-1 fraction, not
    /// degrees; saturation/lightness as 0-1 fractions, not percentages) to reproduce the exact same
    /// RGB values as the browser's CSS hsl() used by the original -- so it is used directly here
    /// instead of hand-rolling a separate HSL-&gt;RGB conversion.
    /// </summary>
    private static Color AltitudeToColor(double altFt)
    {
        Color baseColor;
        if (altFt <= AltitudeRampMinFt)
        {
            // hsl(0,0%,90%)
            baseColor = Color.FromHsl(0f, 0f, 0.9f, 255);
        }
        else
        {
            var t = Math.Min((altFt - AltitudeRampMinFt) / AltitudeRampRangeFt, 1.0);
            // JS Math.round() rounds half-up (toward +Infinity); all values here are
            // non-negative, so MidpointRounding.AwayFromZero reproduces that exactly.
            var hueDegrees = Math.Round(270.0 * (1.0 - t), MidpointRounding.AwayFromZero);
            baseColor = Color.FromHsl((float)(hueDegrees / 360.0), AltitudeRampSaturation, AltitudeRampLightness, 255);
        }

        return Color.Opacity(baseColor, AltitudePathOpacity);
    }

    /// <summary>
    /// Dashed planned-route line + labeled-waypoint dot markers (Task 2). Exact port of
    /// interop.js's setPlannedRoute(): drops stray (0,0) points as a safety net, thins named
    /// waypoints to a max of ~80 labels via the original's `step = max(1, floor(named.length /
    /// 80))` sampling (NOT a hard cap -- for named.length in [80,159] step stays 1 so every point
    /// is kept; the cap only really engages once named.length &gt;= 160), and stores each labeled
    /// waypoint's name as a feature attribute ("Name") rather than a live hover tooltip.
    ///
    /// Hover/tap wiring split: this class only owns a Mapsui.Map, not a MapControl, so it cannot
    /// itself subscribe to MapControl.Info pointer-hit-testing or drive a ToolTipService popup --
    /// that requires the actual control instance, which only the page (Task 4/5) will have. This
    /// method instead exposes enough feature metadata (via <see cref="GetWaypointName"/>) for the
    /// page to resolve a name from whatever feature its own MapControl.Info-based hit-testing
    /// returns, on hover or tap as it sees fit.
    /// </summary>
    public void SetPlannedRoute(IReadOnlyList<(double Lat, double Lon, string? Name)> points)
    {
        if (_plannedRouteLayer == null || _waypointLayer == null)
        {
            return;
        }

        _plannedRoutePoints.Clear();

        var valid = (points ?? Array.Empty<(double Lat, double Lon, string? Name)>())
            .Where(p => p.Lat != 0 || p.Lon != 0)
            .ToList();

        if (valid.Count == 0)
        {
            _plannedRouteLayer.Features = Array.Empty<IFeature>();
            _plannedRouteLayer.DataHasChanged();
            _waypointLayer.Features = Array.Empty<IFeature>();
            _waypointLayer.DataHasChanged();
            return;
        }

        var lineCoordinates = new Coordinate[valid.Count];
        for (var i = 0; i < valid.Count; i++)
        {
            var (x, y) = SphericalMercator.FromLonLat(valid[i].Lon, valid[i].Lat);
            lineCoordinates[i] = new Coordinate(x, y);
            _plannedRoutePoints.Add(new MPoint(x, y));
        }

        var lineFeature = new GeometryFeature { Geometry = new LineString(lineCoordinates) };
        lineFeature.Styles.Add(new VectorStyle
        {
            Line = new Pen(Color.Opacity(Color.FromString(PlannedRouteColorHex), PlannedRouteOpacity), PlannedRouteWeight)
            {
                PenStyle = PenStyle.UserDefined,
                DashArray = PlannedRouteDashArray,
            },
        });
        _plannedRouteLayer.Features = new IFeature[] { lineFeature };
        _plannedRouteLayer.DataHasChanged();

        // interop.js: `named = valid.filter(p => p.type !== 'ltlg' && p.id)`. This C# tuple has
        // no separate "type" field -- callers pass a non-null/non-empty Name only for fixes that
        // should be labeled (Name plays the role of interop.js's `id`), so filtering on Name
        // reproduces the same set.
        var named = valid.Where(p => !string.IsNullOrEmpty(p.Name)).ToList();

        var step = Math.Max(1, named.Count / MaxLabeledWaypoints);
        var waypointFeatures = new List<IFeature>();
        for (var i = 0; i < named.Count; i += step)
        {
            var p = named[i];
            var (x, y) = SphericalMercator.FromLonLat(p.Lon, p.Lat);
            var feature = new PointFeature(new MPoint(x, y));
            feature["Name"] = p.Name;
            feature.Styles.Add(new SymbolStyle
            {
                SymbolType = SymbolType.Ellipse,
                SymbolScale = WaypointSymbolScale,
                Fill = new Brush(Color.FromString(WaypointFillColorHex)),
                Outline = new Pen(Color.FromString(WaypointOutlineColorHex), WaypointOutlineWidth),
            });
            waypointFeatures.Add(feature);
        }

        _waypointLayer.Features = waypointFeatures;
        _waypointLayer.DataHasChanged();
    }

    /// <summary>
    /// Resolves the waypoint name stored by <see cref="SetPlannedRoute"/> on a feature the page
    /// identified via its own MapControl.Info hit-testing (hover or tap) -- see the hover/tap
    /// split documented on <see cref="SetPlannedRoute"/>.
    /// </summary>
    public static string? GetWaypointName(IFeature feature) => feature["Name"] as string;

    /// <summary>
    /// Labeled airport pin (Task 2). Exact port of interop.js's addAirportMarker(): dep=green,
    /// arr=amber, alt=purple, with "ALT " (trailing space) prefixed only onto the alt marker's
    /// label. The original renders this as an HTML divIcon (a colored, rounded, padded text
    /// badge) -- there is no HTML/CSS layer in the native renderer, so this uses Mapsui's
    /// <see cref="LabelStyle"/> (background color + rounded corners + border + text) directly on
    /// a PointFeature, which is the closest native equivalent to the original's colored badge
    /// rather than a separate pin glyph.
    /// </summary>
    public void AddAirportMarker(double lat, double lon, string icao, string type)
    {
        if (_airportMarkerLayer == null)
        {
            return;
        }

        var colorHex = type switch
        {
            "dep" => DepAirportColorHex,
            "arr" => ArrAirportColorHex,
            "alt" => AltAirportColorHex,
            _ => ArrAirportColorHex, // interop.js falls back to the amber/arr color for an unrecognized type
        };
        var prefix = type == "alt" ? AltAirportLabelPrefix : string.Empty;

        var (x, y) = SphericalMercator.FromLonLat(lon, lat);
        var point = new MPoint(x, y);
        var feature = new PointFeature(point);
        feature["Icao"] = icao;
        feature["Type"] = type;
        feature.Styles.Add(new LabelStyle
        {
            Text = prefix + icao,
            Font = new Font { FontFamily = "Consolas", Size = 10, Bold = true },
            ForeColor = Color.White,
            BackColor = new Brush(Color.FromString(colorHex)),
            BorderColor = new Color(255, 255, 255, 51), // rgba(255,255,255,0.2)
            BorderThickness = 1,
            CornerRounding = 5,
        });

        _airportMarkerFeatures.Add(feature);
        _airportMarkerPoints.Add(point);
        _airportMarkerLayer.Features = _airportMarkerFeatures.ToArray();
        _airportMarkerLayer.DataHasChanged();
    }

    public void ClearAirportMarkers()
    {
        _airportMarkerFeatures.Clear();
        _airportMarkerPoints.Clear();

        if (_airportMarkerLayer == null)
        {
            return;
        }

        _airportMarkerLayer.Features = Array.Empty<IFeature>();
        _airportMarkerLayer.DataHasChanged();
    }

    /// <summary>
    /// Per-other-pilot marker + optional dashed route line (Task 3). Exact port of interop.js's
    /// upsertOtherPilot(): create-or-move a marker keyed by <paramref name="pilotId"/>, plus a
    /// dashed route polyline when non-empty <paramref name="waypoints"/> are supplied, dropped
    /// again once they become null/empty (matching the original's <c>route.remove()</c> branch).
    /// Waypoints are filtered the same way as the original (drop stray (0,0) points and anything
    /// outside valid lat/lon range) before being turned into a line.
    ///
    /// Marker taps: this class only owns a <c>Mapsui.Map</c>, not a <c>MapControl</c> -- the same
    /// boundary already established for waypoint names on <see cref="SetPlannedRoute"/> (see that
    /// method's doc comment). Rather than have this class grow a <c>MapControl</c> reference just
    /// to raise one C# event -- which would just be the JS interop's DotNetObjectReference/
    /// [JSInvokable] round-trip reintroduced in the opposite direction -- the pilotId is stored on
    /// the feature (see <see cref="GetOtherPilotId"/>) so the page's own MapControl.Info-based tap
    /// handler (which it needs anyway for waypoint names) can resolve it the same way. One
    /// hit-testing boundary, one lookup pattern, no duplicate plumbing.
    /// </summary>
    public void UpsertOtherPilot(string pilotId, double lat, double lon, IReadOnlyList<(double Lat, double Lon)>? waypoints)
    {
        if (_otherPilotLayer == null || _otherPilotRouteLayer == null)
        {
            return;
        }

        var (x, y) = SphericalMercator.FromLonLat(lon, lat);

        if (!_otherPilotFeatures.TryGetValue(pilotId, out var feature))
        {
            feature = new PointFeature(new MPoint(x, y));
            feature["PilotId"] = pilotId;
            feature.Styles.Add(new SymbolStyle
            {
                SymbolType = SymbolType.Ellipse,
                SymbolScale = OtherPilotSymbolScale,
                Fill = new Brush(Color.Opacity(Color.FromString(OtherPilotFillColorHex), OtherPilotFillOpacity)),
                Outline = new Pen(Color.Opacity(Color.FromString(OtherPilotOutlineColorHex), OtherPilotOutlineOpacity), OtherPilotOutlineWidth),
            });
            _otherPilotFeatures[pilotId] = feature;
        }
        else
        {
            feature.Point.X = x;
            feature.Point.Y = y;
        }

        _otherPilotLayer.Features = _otherPilotFeatures.Values.ToArray();
        _otherPilotLayer.DataHasChanged();

        // interop.js: `waypoints.filter(w => (w.lat !== 0 || w.lon !== 0) && Math.abs(w.lat) <= 90
        // && Math.abs(w.lon) <= 180)`.
        var validCoordinates = (waypoints ?? Array.Empty<(double Lat, double Lon)>())
            .Where(w => (w.Lat != 0 || w.Lon != 0) && Math.Abs(w.Lat) <= 90 && Math.Abs(w.Lon) <= 180)
            .Select(w =>
            {
                var (wx, wy) = SphericalMercator.FromLonLat(w.Lon, w.Lat);
                return new Coordinate(wx, wy);
            })
            .ToArray();

        if (validCoordinates.Length >= 2)
        {
            if (!_otherPilotRouteFeatures.TryGetValue(pilotId, out var routeFeature))
            {
                routeFeature = new GeometryFeature { Geometry = new LineString(validCoordinates) };
                routeFeature.Styles.Add(new VectorStyle
                {
                    Line = new Pen(Color.Opacity(Color.FromString(OtherPilotRouteColorHex), OtherPilotRouteOpacity), OtherPilotRouteWeight)
                    {
                        PenStyle = PenStyle.UserDefined,
                        DashArray = OtherPilotRouteDashArray,
                    },
                });
                _otherPilotRouteFeatures[pilotId] = routeFeature;
            }
            else
            {
                routeFeature.Geometry = new LineString(validCoordinates);
            }
        }
        else
        {
            // Fewer than 2 valid points can't form a line. interop.js's route.setLatLngs() would
            // happily accept a degenerate 0/1-point polyline; dropping the feature here is
            // cleaner and avoids handing Mapsui's LineString fewer points than it requires.
            _otherPilotRouteFeatures.Remove(pilotId);
        }

        _otherPilotRouteLayer.Features = _otherPilotRouteFeatures.Values.ToArray();
        _otherPilotRouteLayer.DataHasChanged();
    }

    public void RemoveOtherPilot(string pilotId)
    {
        if (_otherPilotFeatures.Remove(pilotId) && _otherPilotLayer != null)
        {
            _otherPilotLayer.Features = _otherPilotFeatures.Values.ToArray();
            _otherPilotLayer.DataHasChanged();
        }

        if (_otherPilotRouteFeatures.Remove(pilotId) && _otherPilotRouteLayer != null)
        {
            _otherPilotRouteLayer.Features = _otherPilotRouteFeatures.Values.ToArray();
            _otherPilotRouteLayer.DataHasChanged();
        }
    }

    /// <summary>
    /// Resolves the pilotId stored by <see cref="UpsertOtherPilot"/> on a feature the page
    /// identified via its own MapControl.Info hit-testing -- see the tap-handling boundary
    /// documented on <see cref="UpsertOtherPilot"/>.
    /// </summary>
    public static string? GetOtherPilotId(IFeature feature) => feature["PilotId"] as string;

    /// <summary>
    /// Fits the viewport to the planned-route points + airport markers combined, matching
    /// interop.js's fitRouteBounds() (`L.map.fitBounds(bounds, { padding: [48, 48], maxZoom: 10
    /// })`). Mapsui's <c>Navigator.ZoomToBox</c> has no pixel-padding parameter, so the padding is
    /// applied manually here: the target resolution is computed from the current viewport's pixel
    /// size minus 2x the padding on each axis, then clamped so it never zooms in tighter than zoom
    /// level 10's resolution (the "maxZoom" cap).
    /// </summary>
    public void FitRouteBounds()
    {
        var allPoints = new List<MPoint>(_plannedRoutePoints.Count + _airportMarkerPoints.Count);
        allPoints.AddRange(_plannedRoutePoints);
        allPoints.AddRange(_airportMarkerPoints);

        if (allPoints.Count <= 1)
        {
            return;
        }

        var minX = allPoints.Min(p => p.X);
        var maxX = allPoints.Max(p => p.X);
        var minY = allPoints.Min(p => p.Y);
        var maxY = allPoints.Max(p => p.Y);
        var box = new MRect(minX, minY, maxX, maxY);

        var viewport = Map.Navigator.Viewport;
        // Viewport pixel size is only known once the MapControl has laid out at least once; fall
        // back to a reasonable default so this is still well-defined if called earlier.
        var viewWidth = viewport.Width > 0 ? viewport.Width : 1024.0;
        var viewHeight = viewport.Height > 0 ? viewport.Height : 768.0;

        var availableWidth = Math.Max(viewWidth - (2 * FitBoundsPaddingPx), 1.0);
        var availableHeight = Math.Max(viewHeight - (2 * FitBoundsPaddingPx), 1.0);

        var resolution = Math.Max(box.Width / availableWidth, box.Height / availableHeight);

        // See GetResolutionForLevel's doc comment -- FitBoundsMaxZoomLevel is a real zoom level
        // number (matching Leaflet's maxZoom: 10), not a Navigator.Resolutions list index.
        var minResolutionAllowed = GetResolutionForLevel(FitBoundsMaxZoomLevel);
        if (resolution < minResolutionAllowed)
        {
            resolution = minResolutionAllowed;
        }

        Map.Navigator.CenterOnAndZoomTo(box.Centroid, resolution);
    }

    /// <summary>
    /// Recenters the viewport on the current own-aircraft position (Task 3). No original JS
    /// equivalent -- interop.js has no dedicated "center on me" function; MapPage.razor's map
    /// controls call panTo(vm.Lat, vm.Lon) directly for this button. Exposing it as its own
    /// method here instead lets the renderer reuse the position it already tracks from
    /// <see cref="UpdatePosition"/> rather than the page re-supplying coordinates it already
    /// handed over. No-ops if <see cref="UpdatePosition"/> hasn't been called yet.
    /// </summary>
    public void CenterOnAircraft()
    {
        if (_aircraftFeature == null)
        {
            return;
        }

        Map.Navigator.CenterOn(_aircraftFeature.Point.X, _aircraftFeature.Point.Y, PanAnimationDurationMs);
    }

    /// <summary>
    /// Exact behavioral port of interop.js's zoomIn() (<c>this._map.zoomIn()</c>). Leaflet
    /// animates zoom-level changes by default via a ~0.25s CSS transition baked into its default
    /// map options; Mapsui's <c>Navigator.ZoomIn</c> defaults to an instant jump (duration &lt;=
    /// 0 skips animation entirely -- confirmed by decompiling Navigator.SetViewport), so an
    /// explicit 250ms duration is passed to reproduce the same felt behavior. Leaflet's default
    /// is a CSS rule, not an exported JS constant, so this is a close standard approximation
    /// rather than a value ported verbatim.
    /// </summary>
    public void ZoomIn() => Map.Navigator.ZoomIn(ZoomAnimationDurationMs);

    /// <summary>Exact behavioral port of interop.js's zoomOut() -- see <see cref="ZoomIn"/>.</summary>
    public void ZoomOut() => Map.Navigator.ZoomOut(ZoomAnimationDurationMs);

    /// <summary>
    /// Exact port of interop.js's panTo(): <c>this._map.panTo([lat, lon], { animate: true,
    /// duration: 0.5 })</c>. Mapsui's <c>Navigator.CenterOn</c> takes a duration in milliseconds
    /// and animates whenever it's &gt; 0 (confirmed by decompiling Navigator.SetViewport), so 500
    /// reproduces the original's 0.5s animated pan exactly -- no simplification needed.
    /// </summary>
    public void PanTo(double lat, double lon)
    {
        var (x, y) = SphericalMercator.FromLonLat(lon, lat);
        Map.Navigator.CenterOn(x, y, PanAnimationDurationMs);
    }

    /// <summary>
    /// Sets (or replaces) the weather radar tile layer. <paramref name="tileUrlTemplate"/> is an
    /// already-resolved XYZ tile URL template (RainViewer's frame path changes over time, so the
    /// caller re-resolves and passes a fresh one periodically) -- pass null/empty to remove the
    /// layer entirely (used on page teardown; toggling visibility on/off while keeping the current
    /// frame loaded is <see cref="SetWeatherRadarVisible"/> instead, since re-creating the tile
    /// source on every toggle would needlessly re-fetch every tile already on screen).
    /// </summary>
    public void SetWeatherRadarTile(string? tileUrlTemplate)
    {
        var wasEnabled = _weatherLayer?.Enabled ?? true;
        if (_weatherLayer != null)
        {
            Map.Layers.Remove(_weatherLayer);
            _weatherLayer = null;
        }

        if (string.IsNullOrEmpty(tileUrlTemplate))
        {
            return;
        }

        var tileSchema = new GlobalSphericalMercator(MinZoomLevel, MaxZoomLevel);
        var tileSource = new HttpTileSource(
            tileSchema,
            tileUrlTemplate,
            name: "Weather Radar",
            attribution: new Attribution("RainViewer"),
            userAgent: TileUserAgent);
        _weatherLayer = new TileLayer(new BoundsCheckedTileSource(tileSource))
        {
            Name = "WeatherRadar",
            Opacity = WeatherRadarOpacity,
            Enabled = wasEnabled,
        };

        // "Base" is always the first layer Init() adds, so index 1 reliably sits directly above
        // it -- below every vector overlay (route, aircraft, traffic), matching the EFB web map's
        // own z-order (radar under markers, over the basemap).
        Map.Layers.Insert(1, _weatherLayer);
    }

    public void SetWeatherRadarVisible(bool visible)
    {
        if (_weatherLayer != null)
        {
            _weatherLayer.Enabled = visible;
        }
    }

    /// <summary>
    /// Rebuilds one network's traffic markers from scratch (Task: online network layers). Shared
    /// by <see cref="UpdateVatsimTraffic"/>/<see cref="UpdateIvaoTraffic"/> -- both networks render
    /// identically (heading-rotated triangle, same shape as the own-aircraft marker) and differ
    /// only by color, so there's one implementation rather than two near-duplicates.
    /// <paramref name="label"/> per pilot is stored under the same "Name" feature key
    /// <see cref="SetPlannedRoute"/>'s waypoint dots use, so the page's existing hover-popup
    /// hit-testing (<c>GetWaypointName</c>) picks these up for free with no page-side changes.
    /// </summary>
    private static void UpdateNetworkTrafficLayer(
        MemoryLayer? layer,
        IReadOnlyList<(double Lat, double Lon, double HeadingDeg, string Label)> pilots,
        string colorHex)
    {
        if (layer == null)
        {
            return;
        }

        var features = new List<IFeature>(pilots.Count);
        foreach (var p in pilots)
        {
            var (x, y) = SphericalMercator.FromLonLat(p.Lon, p.Lat);
            var feature = new PointFeature(new MPoint(x, y));
            feature["Name"] = p.Label;
            feature.Styles.Add(new SymbolStyle
            {
                SymbolScale = NetworkTrafficSymbolScale,
                SymbolType = SymbolType.Triangle,
                RotateWithMap = true,
                SymbolRotation = p.HeadingDeg,
                Fill = new Brush(Color.FromString(colorHex)),
                Outline = new Pen(Color.White, 1),
            });
            features.Add(feature);
        }

        layer.Features = features;
        layer.DataHasChanged();
    }

    /// <summary>Shared ATC-dot rebuild -- see <see cref="UpdateNetworkTrafficLayer"/>'s doc comment; same reasoning, ATC instead of pilots.</summary>
    private static void UpdateNetworkAtcLayer(
        MemoryLayer? layer,
        IReadOnlyList<(double Lat, double Lon, string Label)> atcs,
        string colorHex)
    {
        if (layer == null)
        {
            return;
        }

        var features = new List<IFeature>(atcs.Count);
        foreach (var a in atcs)
        {
            var (x, y) = SphericalMercator.FromLonLat(a.Lon, a.Lat);
            var feature = new PointFeature(new MPoint(x, y));
            feature["Name"] = a.Label;
            feature.Styles.Add(new SymbolStyle
            {
                SymbolType = SymbolType.Ellipse,
                SymbolScale = NetworkAtcSymbolScale,
                Fill = new Brush(Color.FromString(colorHex)),
                Outline = new Pen(Color.White, 1.5),
            });
            features.Add(feature);
        }

        layer.Features = features;
        layer.DataHasChanged();
    }

    public void UpdateVatsimTraffic(IReadOnlyList<(double Lat, double Lon, double HeadingDeg, string Label)> pilots) =>
        UpdateNetworkTrafficLayer(_vatsimTrafficLayer, pilots, VatsimColorHex);

    public void UpdateIvaoTraffic(IReadOnlyList<(double Lat, double Lon, double HeadingDeg, string Label)> pilots) =>
        UpdateNetworkTrafficLayer(_ivaoTrafficLayer, pilots, IvaoColorHex);

    // ATC entries with no resolvable position (VATSIM controllers carry none of their own -- see
    // EfbNetworkTrafficService's own doc comment on the transceivers-feed join) are filtered out by
    // the caller before reaching here, same boundary as EfbNetworkTrafficService/app.js already use.
    public void UpdateVatsimAtc(IReadOnlyList<(double Lat, double Lon, string Label)> atcs) =>
        UpdateNetworkAtcLayer(_vatsimAtcLayer, atcs, VatsimColorHex);

    public void UpdateIvaoAtc(IReadOnlyList<(double Lat, double Lon, string Label)> atcs) =>
        UpdateNetworkAtcLayer(_ivaoAtcLayer, atcs, IvaoColorHex);

    /// <summary>Toggles both of VATSIM's layers (traffic + ATC) together -- one on-screen toggle per network, matching the EFB web map's own single VATSIM rail button.</summary>
    public void SetVatsimVisible(bool visible)
    {
        if (_vatsimTrafficLayer != null) _vatsimTrafficLayer.Enabled = visible;
        if (_vatsimAtcLayer != null) _vatsimAtcLayer.Enabled = visible;
    }

    /// <summary>Toggles both of IVAO's layers (traffic + ATC) together -- see <see cref="SetVatsimVisible"/>.</summary>
    public void SetIvaoVisible(bool visible)
    {
        if (_ivaoTrafficLayer != null) _ivaoTrafficLayer.Enabled = visible;
        if (_ivaoAtcLayer != null) _ivaoAtcLayer.Enabled = visible;
    }

    public void Dispose()
    {
        Map.Layers.Clear();
        _aircraftFeature = null;
        _flownPathPoints.Clear();
        _plannedRoutePoints.Clear();
        _airportMarkerPoints.Clear();
        _airportMarkerFeatures.Clear();
        _otherPilotFeatures.Clear();
        _otherPilotRouteFeatures.Clear();
        _aircraftLayer = null;
        _flownPathLayer = null;
        _flownPathAltitudeLayer = null;
        _plannedRouteLayer = null;
        _waypointLayer = null;
        _airportMarkerLayer = null;
        _otherPilotLayer = null;
        _otherPilotRouteLayer = null;
        _weatherLayer = null;
        _vatsimTrafficLayer = null;
        _vatsimAtcLayer = null;
        _ivaoTrafficLayer = null;
        _ivaoAtcLayer = null;
    }
}

/// <summary>
/// Wraps an <see cref="ITileSource"/> and refuses (no network call, resolves to null so the tile
/// just renders blank) any <see cref="TileInfo"/> whose Col/Row falls outside the valid
/// 0..2^Level-1 range for that level. A well-formed viewport extent fed through
/// <c>GlobalSphericalMercator.GetTileInfos</c> shouldn't mathematically produce an out-of-range
/// index, but Esri's World Dark Gray Base tile server was directly observed (via curl, requesting
/// a deliberately out-of-range index) to respond with an actual literal placeholder JPEG reading
/// "Map data not yet available" -- still HTTP 200, so BruTile has no way to distinguish it from a
/// real tile -- rather than a 404/error for out-of-range requests. This guard means that whatever
/// upstream cause produces one (a stray pan/fit past the world edge, a corrupted lat/lon on some
/// feature, etc.) renders as an empty gap instead of that placeholder text tiling the screen.
/// </summary>
internal sealed class BoundsCheckedTileSource : ITileSource
{
    private readonly ITileSource _inner;

    public BoundsCheckedTileSource(ITileSource inner)
    {
        _inner = inner;
    }

    public ITileSchema Schema => _inner.Schema;
    public string Name => _inner.Name;
    public Attribution Attribution => _inner.Attribution;

    public Task<byte[]?> GetTileAsync(TileInfo tileInfo)
    {
        var maxIndex = (1L << tileInfo.Index.Level) - 1;
        if (tileInfo.Index.Col < 0 || tileInfo.Index.Col > maxIndex ||
            tileInfo.Index.Row < 0 || tileInfo.Index.Row > maxIndex)
        {
            return Task.FromResult<byte[]?>(null);
        }

        return _inner.GetTileAsync(tileInfo);
    }
}
