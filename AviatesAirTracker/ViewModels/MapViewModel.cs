using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AviatesAirTracker.ViewModels;

public partial class MapViewModel : ObservableObject
{
    [ObservableProperty] private double _aircraftLat;
    [ObservableProperty] private double _aircraftLon;
    [ObservableProperty] private double _aircraftHeading;
    [ObservableProperty] private bool _showPlannedRoute = true;
    [ObservableProperty] private bool _showActualPath = true;
    [ObservableProperty] private bool _showWaypoints = true;

    private readonly RouteTracker _routeTracker;

    public MapViewModel(RouteTracker routeTracker)
    {
        _routeTracker = routeTracker;
    }

    public void UpdatePosition(TelemetrySnapshot snap)
    {
        AircraftLat = snap.Latitude;
        AircraftLon = snap.Longitude;
        AircraftHeading = snap.Raw.HeadingTrue;
    }

    public List<Models.PathPoint> GetFlightPath() => _routeTracker.GetRecordedPathSnapshot();
    public List<Models.Waypoint> GetPlannedRoute() => _routeTracker.GetPlannedRoute();
}
