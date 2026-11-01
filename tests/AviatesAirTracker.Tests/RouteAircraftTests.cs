using System.Text.Json;
using AviatesAirTracker.Services;

namespace AviatesAirTracker.Tests;

public class RouteAircraftTests
{
    private const string MultiAircraftRoute = """
        {
          "id": 24, "flight_number": "AX110", "origin_iata": "EGBB", "dest_iata": "LEPA",
          "aircraft_type": "B738", "fleet_group": "mainline", "est_block_time_minutes": 120,
          "distance_km": 1475, "frequency": "Daily",
          "aircraft": [
            { "type": "B738",  "fleet_group": "mainline",   "flight_number": "AX110", "frequency": "Daily",     "est_block_time_minutes": 120 },
            { "type": "B739",  "fleet_group": "mainline",   "flight_number": "AX164", "frequency": "Daily",     "est_block_time_minutes": 105 },
            { "type": "ATR72", "fleet_group": "cityhopper", "flight_number": "AX608", "frequency": "4x Weekly" }
          ],
          "fleet_groups": ["cityhopper", "mainline"]
        }
        """;

    // What the server sent before multi-aircraft routes (and still sends until the migration runs).
    private const string SingleAircraftRoute = """
        { "id": 58, "flight_number": "AX610", "origin_iata": "EGKK", "dest_iata": "LFPG",
          "aircraft_type": "ATR42", "fleet_group": "cityhopper", "est_block_time_minutes": 75,
          "distance_km": 308, "frequency": "Daily" }
        """;

    [Fact]
    public void Reads_every_aircraft_on_a_route()
    {
        var route = JsonSerializer.Deserialize<AviatesRoute>(MultiAircraftRoute)!;
        Assert.Equal(["B738", "B739", "ATR72"], route.AircraftOptions.Select(a => a.Type));
        Assert.Equal("B738, B739, ATR72", route.AircraftLabel);
        Assert.Equal("cityhopper", route.AircraftFor("atr 72")!.FleetGroup);
        Assert.Equal("AX164", route.AircraftFor("B739")!.FlightNumber);
        Assert.Null(route.AircraftFor("A320"));
    }

    [Fact]
    public void A_route_without_the_list_still_has_its_one_aircraft()
    {
        var route = JsonSerializer.Deserialize<AviatesRoute>(SingleAircraftRoute)!;
        var only = Assert.Single(route.AircraftOptions);
        Assert.Equal(("ATR42", "cityhopper", "AX610", 75), (only.Type, only.FleetGroup, only.FlightNumber, only.BlockTimeMinutes));
    }

    [Theory]
    [InlineData("ATR42", "VAH")]
    [InlineData("ATR 42", "VAH")]
    [InlineData("ATR 72", "VAH")]
    [InlineData("B77F", "VAC")]
    [InlineData("B738", "VAV")]
    public void Callsign_prefix_ignores_spacing_in_the_type(string type, string prefix) =>
        Assert.Equal(prefix, Subsidiary.IcaoPrefix(type));
}
