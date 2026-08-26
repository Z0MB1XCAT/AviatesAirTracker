using System;
using System.Collections.Generic;
using System.Linq;
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
/// Task 1 scope: tile layer, own-aircraft marker + rotation, flown-path polyline capped at a
/// 2000-point ring buffer (drop-oldest). Altitude-colored path segments, planned route,
/// waypoint/airport markers, other-pilot markers, and pan/zoom/center wrappers are Tasks 2-3.
/// </summary>
public sealed class NativeMapRenderer : IDisposable
{
    // Confirmed exact from wwwroot/js/interop.js's window.aviatesMap.init (lines ~34-49):
    // L.map({ zoom: 7, minZoom: 3, maxZoom: 18 }) + L.tileLayer('https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png', { subdomains: 'abcd' }).
    // The {r} retina-suffix token has no BruTile/Mapsui equivalent (no built-in DPI-aware tile
    // source), so it is dropped here -- the native renderer always fetches @1x tiles. Simplification,
    // noted in the Task 1 report.
    private const string TileUrlTemplate = "https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png";
    private static readonly string[] TileSubdomains = { "a", "b", "c", "d" };

    private const int MinZoomLevel = 3;
    private const int MaxZoomLevel = 18;
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

    public Mapsui.Map Map { get; }

    private MemoryLayer? _aircraftLayer;
    private MemoryLayer? _flownPathLayer;
    private MemoryLayer? _flownPathAltitudeLayer;
    private MemoryLayer? _plannedRouteLayer;
    private MemoryLayer? _waypointLayer;
    private MemoryLayer? _airportMarkerLayer;

    private PointFeature? _aircraftFeature;
    private readonly List<Coordinate> _flownPathPoints = new();

    // Used only by FitRouteBounds() to reproduce interop.js's fitRouteBounds() combining the
    // planned-route line points with the airport marker points.
    private readonly List<MPoint> _plannedRoutePoints = new();
    private readonly List<MPoint> _airportMarkerPoints = new();
    private readonly List<IFeature> _airportMarkerFeatures = new();

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
        var tileSource = new HttpTileSource(tileSchema, TileUrlTemplate, TileSubdomains, name: "CARTO Dark");
        Map.Layers.Add(new TileLayer(tileSource) { Name = "Base" });

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
        };
        Map.Layers.Add(_waypointLayer);

        _airportMarkerLayer = new MemoryLayer
        {
            Name = "AirportMarkers",
            Features = Array.Empty<IFeature>(),
            Style = null,
        };
        Map.Layers.Add(_airportMarkerLayer);

        _plannedRoutePoints.Clear();
        _airportMarkerPoints.Clear();
        _airportMarkerFeatures.Clear();

        var (x, y) = SphericalMercator.FromLonLat(lon, lat);
        var resolution = Map.Navigator.Resolutions.Count > InitialZoomLevel
            ? Map.Navigator.Resolutions[InitialZoomLevel]
            : Map.Navigator.Resolutions.Last();
        Map.Navigator.CenterOnAndZoomTo(new MPoint(x, y), resolution);
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

        if (Map.Navigator.Resolutions.Count > FitBoundsMaxZoomLevel)
        {
            var minResolutionAllowed = Map.Navigator.Resolutions[FitBoundsMaxZoomLevel];
            if (resolution < minResolutionAllowed)
            {
                resolution = minResolutionAllowed;
            }
        }

        Map.Navigator.CenterOnAndZoomTo(box.Centroid, resolution);
    }

    public void Dispose()
    {
        Map.Layers.Clear();
        _aircraftFeature = null;
        _flownPathPoints.Clear();
        _plannedRoutePoints.Clear();
        _airportMarkerPoints.Clear();
        _airportMarkerFeatures.Clear();
        _aircraftLayer = null;
        _flownPathLayer = null;
        _flownPathAltitudeLayer = null;
        _plannedRouteLayer = null;
        _waypointLayer = null;
        _airportMarkerLayer = null;
    }
}
