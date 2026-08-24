using AviatesAirTracker.Models;
using AviatesAirTracker.Services;
using System.Linq;
using Xunit;

namespace AviatesAirTracker.Tests;

public class FlightsFilterLogicTests
{
    private static FlightRecord[] MakeFlights() =>
    [
        new FlightRecord { FlightNumber = "AAT101", Status = FlightStatus.InProgress },
        new FlightRecord { FlightNumber = "AAT202", Status = FlightStatus.Completed },
        new FlightRecord { FlightNumber = "AAT303", Status = FlightStatus.Diverted },
        new FlightRecord { FlightNumber = "AAT404", Status = FlightStatus.Aborted },
        new FlightRecord { FlightNumber = "AAT505", Status = FlightStatus.Crashed },
    ];

    [Fact]
    public void FilterByTab_Active_ReturnsOnlyInProgress()
    {
        var result = FlightsFilterLogic.FilterByTab(MakeFlights(), "active").ToList();

        Assert.Single(result);
        Assert.All(result, f => Assert.Equal(FlightStatus.InProgress, f.Status));
    }

    [Fact]
    public void FilterByTab_Completed_ReturnsOnlyCompleted()
    {
        var result = FlightsFilterLogic.FilterByTab(MakeFlights(), "completed").ToList();

        Assert.Single(result);
        Assert.All(result, f => Assert.Equal(FlightStatus.Completed, f.Status));
    }

    [Fact]
    public void FilterByTab_All_ReturnsEverythingUnfiltered()
    {
        var flights = MakeFlights();
        var result = FlightsFilterLogic.FilterByTab(flights, "all").ToList();

        Assert.Equal(flights.Length, result.Count);
    }

    [Fact]
    public void FilterByTab_UnknownTab_FallsBackToEverythingUnfiltered()
    {
        var flights = MakeFlights();
        var result = FlightsFilterLogic.FilterByTab(flights, "something-made-up").ToList();

        Assert.Equal(flights.Length, result.Count);
    }
}
