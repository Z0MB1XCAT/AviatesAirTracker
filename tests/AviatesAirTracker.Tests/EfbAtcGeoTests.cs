using AviatesAirTracker.Core.Efb;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace AviatesAirTracker.Tests;

public class EfbAtcGeoTests
{
    private const string Dat = """
        ;ICAO|Airport Name|Latitude Decimal|Longitude Decimal|IATA/LID|FIR|IsPseudo
        [Airports]
        EGLL|Heathrow|51.4775|-0.461389|LHR|EGTT|0
        KLAX|Los Angeles Intl|33.9425|-118.408056|LAX|KZLA|0
        PHNL|Honolulu Intl|21.318681|-157.922428||PHZH|0
        EGTT|London FIR label|51.5|-1.0||EGTT|1
        [FIRs]
        ;ICAO|NAME|CALLSIGN PREFIX|FIR BOUNDARY
        EGTT|London Control|LON|EGTT
        EGTT|London Control (South)|LON_S|EGTT-S
        LFFF|Paris Control||LFFF
        [UIRs]
        EURM|Euro Control Middle|LFFF,EGTT
        """;

    private static string Square(string id, double lat, double lon)
    {
        string P(double x, double y) => FormattableString.Invariant($"[{x},{y}]");
        var ring = string.Join(",", P(lon - 1, lat - 1), P(lon + 1, lat - 1), P(lon + 1, lat + 1), P(lon - 1, lat + 1), P(lon - 1, lat - 1));
        return "{\"type\":\"Feature\",\"properties\":{\"id\":\"" + id + "\",\"label_lat\":\"" + FormattableString.Invariant($"{lat}")
             + "\",\"label_lon\":\"" + FormattableString.Invariant($"{lon}") + "\"},\"geometry\":{\"type\":\"Polygon\",\"coordinates\":[[" + ring + "]]}}";
    }

    private static EfbAtcGeo Geo()
    {
        var g = new EfbAtcGeo();
        g.LoadVatSpyDat(Dat);
        g.LoadFirBoundaries("{\"type\":\"FeatureCollection\",\"features\":[" + Square("EGTT", 53, -2) + "," + Square("EGTT-S", 51, -1) + "," + Square("LFFF", 48, 2) + "]}");
        g.LoadTraconBoundaries("""
            {"type":"FeatureCollection","features":[{"type":"Feature","properties":{"id":"A80","prefix":["ATL","AHN"],"name":"Atlanta Approach"},
             "geometry":{"type":"MultiPolygon","coordinates":[[[[-85,33],[-84,33],[-84,34],[-85,34],[-85,33]]]]}}]}
            """);
        return g;
    }

    [Fact]
    public void Tower_ResolvesToTheAirportReferencePoint()
    {
        var r = Geo().Resolve("EGLL_TWR", "TWR");
        Assert.Equal("EGLL", r.AirportIcao);
        Assert.Equal(51.4775, r.Lat);
        Assert.Empty(r.SectorKeys);
    }

    [Fact]
    public void ThreeLetterPrefix_FallsBackToTheIataCode()
    {
        Assert.Equal("KLAX", Geo().Resolve("LAX_GND", "GND").AirportIcao);
    }

    [Fact]
    public void FaaId_WithNoAlias_TriesTheKAndPPrefixes()
    {
        // live VATSIM case: HNL_N_TWR, where VATSpy lists no LID for PHNL
        Assert.Equal("PHNL", Geo().Resolve("HNL_N_TWR", "TWR").AirportIcao);
    }

    [Fact]
    public void PseudoAirports_AreNotAerodromes()
    {
        Assert.Null(Geo().FindAirport("EGTT"));
    }

    [Theory]
    [InlineData("LON_S_CTR", "FIR:EGTT-S", "London Control (South)")]
    [InlineData("LON_CTR", "FIR:EGTT", "London Control")]
    [InlineData("LFFF_CTR", "FIR:LFFF", "Paris Control")]    // empty prefix → ICAO
    public void Centre_ResolvesTheLongestMatchingFirPrefix(string callsign, string key, string name)
    {
        var r = Geo().Resolve(callsign, "CTR");
        Assert.Equal(new[] { key }, r.SectorKeys);
        Assert.Equal(name, r.SectorName);
    }

    [Fact]
    public void Uir_CoversAllItsMemberFirs()
    {
        var r = Geo().Resolve("EURM_CTR", "CTR");
        Assert.Equal(new[] { "FIR:LFFF", "FIR:EGTT" }, r.SectorKeys);
    }

    [Fact]
    public void Approach_UsesTheTraconByAnyOfItsPrefixes_ElseTheAirport()
    {
        var geo = Geo();
        var atl = geo.Resolve("AHN_APP", "APP");
        Assert.Single(atl.SectorKeys);
        Assert.StartsWith("TRACON:A80", atl.SectorKeys[0]);
        Assert.Equal("Atlanta Approach", atl.SectorName);

        var lhr = geo.Resolve("EGLL_APP", "APP");
        Assert.Empty(lhr.SectorKeys);
        Assert.Equal("EGLL", lhr.AirportIcao);
    }

    [Theory]
    [InlineData("EGLL_ATIS", null, "ATIS")]
    [InlineData("EGLL_DEL", null, "DEL")]
    [InlineData("KJFK_RMP", null, "GND")]
    [InlineData("EDDF_N_APP", null, "APP")]
    [InlineData("EGLL_1", "DEP", "APP")]     // odd suffix → the network's own facility
    [InlineData("EGLL_1", null, "")]
    public void FacilityOf(string callsign, string? network, string expected)
    {
        Assert.Equal(expected, EfbAtcGeo.FacilityOf(callsign, network));
    }

    [Fact]
    public void Simplify_DropsVerticesCloserThanTheTolerance()
    {
        var pts = Enumerable.Range(0, 1000).Select(i => new[] { i * 0.001, 0.0 }).ToList();
        var s = EfbAtcGeo.Simplify(pts, 0.1);
        Assert.InRange(s.Count, 10, 12);
        Assert.Equal(pts[^1], s[^1]);    // endpoints kept
    }
}
