using AviatesAirTracker.Services;
using Xunit;

namespace AviatesAirTracker.Tests;

public class PilotStatsLogicTests
{
    [Theory]
    [InlineData(0,    "Cadet",                "Junior First Officer")]
    [InlineData(25,   "Junior First Officer", "First Officer")]
    [InlineData(75,   "First Officer",        "Senior First Officer")]
    [InlineData(200,  "Senior First Officer", "Junior Captain")]
    [InlineData(450,  "Junior Captain",       "Captain")]
    [InlineData(800,  "Captain",              "Senior Captain")]
    [InlineData(1500, "Senior Captain",       "Top Rank")]
    public void ComputeRankProgress_EveryRealRank_ReturnsCorrectNextRank(double hours, string rank, string expectedNext)
    {
        var (nextRank, _) = PilotStatsLogic.ComputeRankProgress(hours, rank);
        Assert.Equal(expectedNext, nextRank);
    }

    [Fact]
    public void ComputeRankProgress_BrandNewCadet_IsNotTopRank()
    {
        // The bug this fixes: a 0-hour Cadet previously fell through to the default arm
        // and showed "Top Rank" at 100%, because "Cadet" didn't match any case label.
        var (nextRank, pct) = PilotStatsLogic.ComputeRankProgress(0, "Cadet");
        Assert.NotEqual("Top Rank", nextRank);
        Assert.Equal(0, pct, precision: 1);
    }

    [Fact]
    public void ComputeRankProgress_MidwayThroughARank_IsRoughlyHalfway()
    {
        // Cadet is 0-25h; 12.5h should be ~50%.
        var (_, pct) = PilotStatsLogic.ComputeRankProgress(12.5, "Cadet");
        Assert.Equal(50, pct, precision: 0);
    }

    [Fact]
    public void ComputeRankProgress_UnknownRankString_FallsBackToTopRank()
    {
        var (nextRank, pct) = PilotStatsLogic.ComputeRankProgress(999, "Something Made Up");
        Assert.Equal("Top Rank", nextRank);
        Assert.Equal(100, pct);
    }
}
