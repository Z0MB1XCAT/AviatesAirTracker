using AviatesAirTracker.Models;
using AviatesAirTracker.ViewModels;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Nts.Extensions;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;
using NetTopologySuite.Geometries;
using System.Windows;
using System.Windows.Controls;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AviatesAirTracker.Views
{
    public partial class DashboardView : UserControl
    {
        public DashboardView()
        {
            InitializeComponent();
            // CRIT-11: RefreshAsync is now async Task; use async event handler so exceptions propagate properly.
            Loaded += async (_, _) =>
            {
                if (DataContext is DashboardViewModel vm)
                    await vm.RefreshAsync();
            };
        }
    }

    public partial class LiveFlightView : UserControl
    {
        public LiveFlightView() => InitializeComponent();
    }

    public partial class LandingAnalysisView : UserControl
    {
        public LandingAnalysisView() => InitializeComponent();
    }

    public partial class SettingsView : UserControl
    {
        public SettingsView() => InitializeComponent();
    }

    public partial class StatisticsView : UserControl
    {
        public StatisticsView()
        {
            InitializeComponent();
            // CRIT-11: RefreshAsync is now async Task; use async event handler so exceptions propagate properly.
            Loaded += async (_, _) =>
            {
                if (DataContext is StatisticsViewModel vm)
                    await vm.RefreshAsync();
            };
        }
    }

    public partial class TelemetryView : UserControl
    {
        public TelemetryView() => InitializeComponent();
    }

    public partial class ReplayView : UserControl
    {
        public ReplayView()
        {
            InitializeComponent();
            Loaded += async (_, _) =>
            {
                if (DataContext is ReplayViewModel vm)
                    await vm.RefreshAvailableFlightsAsync();
            };
        }

        private async void FlightList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // MINOR-17: Removed AgentDebug calls that wrote to disk on every page navigation in production.
            if (DataContext is ReplayViewModel vm &&
                e.AddedItems.Count > 0 &&
                e.AddedItems[0] is FlightRecord flight)
            {
                await vm.LoadFlightAsync(flight);
            }
        }
    }

    // ============================================================
    // MAP VIEW — full Mapsui integration with path rendering
    // ============================================================
    public partial class MapView : UserControl
    {
        private MapViewModel? _vm;
        private WritableLayer? _aircraftLayer;
        private WritableLayer? _pathLayer;
        private WritableLayer? _plannedLayer;
        private System.Threading.CancellationTokenSource? _cts;
        private bool _mapReady;

        public MapView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _vm = DataContext as MapViewModel;
            InitMap();
            StartRefreshLoop();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }

        private void InitMap()
        {
            var map = new Mapsui.Map();
            map.Layers.Add(OpenStreetMap.CreateTileLayer());

            _pathLayer = new WritableLayer
            {
                Name = "ActualPath",
                Style = new VectorStyle
                {
                    Line = new Pen(new Mapsui.Styles.Color(61, 126, 238, 220), 2.5f)
                }
            };
            map.Layers.Add(_pathLayer);

            _plannedLayer = new WritableLayer
            {
                Name = "PlannedRoute",
                Style = new VectorStyle
                {
                    Line = new Pen(new Mapsui.Styles.Color(34, 197, 94, 160), 1.5f)
                    {
                        PenStyle = PenStyle.Dash
                    }
                }
            };
            map.Layers.Add(_plannedLayer);

            _aircraftLayer = new WritableLayer { Name = "Aircraft", Style = null };
            map.Layers.Add(_aircraftLayer);

            MapControl.Map = map;

            // Default center: mid-Atlantic
            var (cx, cy) = SphericalMercator.FromLonLat(-30, 40);
            map.Navigator.CenterOnAndZoomTo(new MPoint(cx, cy), 20_000_000);

            _mapReady = true;
        }

        private void StartRefreshLoop()
        {
            _cts = new System.Threading.CancellationTokenSource();
            var token = _cts.Token;

            System.Threading.Tasks.Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    await System.Threading.Tasks.Task.Delay(500, token);
                    if (!token.IsCancellationRequested)
                        Dispatcher.BeginInvoke(RenderFrame);
                }
            }, token);
        }

        private void RenderFrame()
        {
            if (!_mapReady || _vm == null) return;

            try
            {
                // Aircraft marker
                _aircraftLayer!.Clear();
                if (_vm.AircraftLat != 0 || _vm.AircraftLon != 0)
                {
                    var (ax, ay) = SphericalMercator.FromLonLat(_vm.AircraftLon, _vm.AircraftLat);
                    var aircraftFeature = new PointFeature(ax, ay);
                    aircraftFeature.Styles.Add(new SymbolStyle
                    {
                        Fill = new Mapsui.Styles.Brush(new Mapsui.Styles.Color(61, 126, 238)),
                        Outline = new Pen(Mapsui.Styles.Color.White, 2),
                        SymbolScale = 1.2,
                        SymbolType = SymbolType.Ellipse
                    });
                    _aircraftLayer.Add(aircraftFeature);
                }

                // Actual path
                if (_vm.ShowActualPath)
                {
                    var pts = _vm.GetFlightPath();
                    if (pts.Count >= 2)
                    {
                        _pathLayer!.Clear();
                        var coords = pts.Select(p => { var (x, y) = SphericalMercator.FromLonLat(p.Longitude, p.Latitude); return new Coordinate(x, y); }).ToArray();
                        _pathLayer.Add(new GeometryFeature
                        {
                            Geometry = new LineString(coords)
                        });
                    }
                }

                // Planned route
                if (_vm.ShowPlannedRoute)
                {
                    var wps = _vm.GetPlannedRoute()
                        .Where(w => w.Latitude != 0 || w.Longitude != 0).ToList();
                    if (wps.Count >= 2)
                    {
                        _plannedLayer!.Clear();
                        var coords = wps.Select(w =>
                        {
                            var (x, y) = SphericalMercator.FromLonLat(w.Longitude, w.Latitude);
                            return new Coordinate(x, y);
                        }).ToArray();
                        _plannedLayer.Add(new GeometryFeature
                        {
                            Geometry = new LineString(coords)
                        });
                    }
                }

                MapControl.Refresh();
            }
            catch (Exception ex)
            {
                // CRIT-10: Removed throw; — Mapsui tile/projection errors were crashing the whole app
                // via DispatcherUnhandledException. Log and continue instead.
                Serilog.Log.Warning(ex, "[MapView] RenderFrame error — swallowed to prevent crash");
            }
        }
    }
}
