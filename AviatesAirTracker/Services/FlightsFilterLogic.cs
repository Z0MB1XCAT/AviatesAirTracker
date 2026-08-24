using System.Collections.Generic;
using System.Linq;
using AviatesAirTracker.Models;

namespace AviatesAirTracker.Services;

/// <summary>
/// Pure tab-filter logic, ported verbatim from Flights.razor's FilteredFlights() switch.
/// </summary>
public static class FlightsFilterLogic
{
    public static IEnumerable<FlightRecord> FilterByTab(IEnumerable<FlightRecord> flights, string tab) => tab switch
    {
        "active"    => flights.Where(f => f.Status == FlightStatus.InProgress),
        "completed" => flights.Where(f => f.Status == FlightStatus.Completed),
        _           => flights
    };
}
