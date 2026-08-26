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

    public Mapsui.Map Map { get; }

    private MemoryLayer? _aircraftLayer;
    private MemoryLayer? _flownPathLayer;
    private MemoryLayer? _plannedRouteLayer;

    private PointFeature? _aircraftFeature;
    private readonly List<Coordinate> _flownPathPoints = new();

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

    public void Dispose()
    {
        Map.Layers.Clear();
        _aircraftFeature = null;
        _flownPathPoints.Clear();
        _aircraftLayer = null;
        _flownPathLayer = null;
        _plannedRouteLayer = null;
    }
}
