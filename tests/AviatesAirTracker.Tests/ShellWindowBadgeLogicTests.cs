using AviatesAirTracker.Models;
using AviatesAirTracker.Services;
using AviatesAirTracker.Shell;
using Xunit;

namespace AviatesAirTracker.Tests;

public class ShellBadgeLogicTests
{
    [Theory]
    [InlineData(FlightSessionState.Idle, false)]
    [InlineData(FlightSessionState.Complete, false)]
    [InlineData(FlightSessionState.PreFlight, true)]
    [InlineData(FlightSessionState.Taxiing, true)]
    [InlineData(FlightSessionState.Airborne, true)]
    [InlineData(FlightSessionState.OnApproach, true)]
    [InlineData(FlightSessionState.Landed, true)]
    public void IsFlightLive_MatchesFlightSessionManagersOwnIdleOrCompleteConvention(
        FlightSessionState state, bool expectedLive)
    {
        Assert.Equal(expectedLive, ShellBadgeLogic.IsFlightLive(state));
    }

    [Fact]
    public void HasUnsyncedPireps_TrueWhenACompletedFlightIsNotSynced()
    {
        var flights = new[]
        {
            new FlightRecord { Status = FlightStatus.Completed, SyncedToBackend = false },
        };

        Assert.True(ShellBadgeLogic.HasUnsyncedPireps(flights));
    }

    [Fact]
    public void HasUnsyncedPireps_FalseWhenEverythingIsSynced()
    {
        var flights = new[]
        {
            new FlightRecord { Status = FlightStatus.Completed, SyncedToBackend = true },
        };

        Assert.False(ShellBadgeLogic.HasUnsyncedPireps(flights));
    }

    [Fact]
    public void HasUnsyncedPireps_IgnoresUnsyncedFlightsThatArentCompletedYet()
    {
        // An in-progress flight is trivially "not synced" — that must not count. Only a
        // completed-but-unsynced flight should show the dot.
        var flights = new[]
        {
            new FlightRecord { Status = FlightStatus.InProgress, SyncedToBackend = false },
        };

        Assert.False(ShellBadgeLogic.HasUnsyncedPireps(flights));
    }

    [Fact]
    public void HasUnsyncedPireps_FalseOnEmptyHistory()
    {
        Assert.False(ShellBadgeLogic.HasUnsyncedPireps(Array.Empty<FlightRecord>()));
    }
}
