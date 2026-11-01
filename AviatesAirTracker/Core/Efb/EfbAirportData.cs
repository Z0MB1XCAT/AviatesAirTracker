using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AviatesAirTracker.Core.Efb;

// ============================================================
// EFB AIRPORT / RUNWAY DATA
//
// Runway picker data for the EFB's Departure Perf app, parsed from the
// aviationweather.gov Data API airport endpoint (free, keyless — the same
// provider the host already uses for METAR/TAF):
//   GET https://aviationweather.gov/api/data/airport?ids=EGLL&format=json
// Each runway comes back as one physical strip ("09L/27R", "12802x164" ft,
// true alignment of the FIRST end); this splits it into its two takeoff
// directions, which is what a performance tool actually selects.
// Field elevation is published in metres.
// ============================================================

public sealed class EfbAirportInfo
{
    public string Icao { get; set; } = "";
    public string Name { get; set; } = "";
    public int ElevationFt { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double MagVarDeg { get; set; }            // + east
    public List<EfbRunwayEnd> Runways { get; set; } = [];
}

public sealed class EfbRunwayEnd
{
    public string Ident { get; set; } = "";          // "27R"
    public string Strip { get; set; } = "";          // "09L/27R"
    public int LengthM { get; set; }                 // full strip length (TORA, no displaced data)
    public int WidthM { get; set; }
    public int HeadingTrueDeg { get; set; }
    public string Surface { get; set; } = "";        // ASPH | CONC | HARD | GRASS | …
}

public static class EfbAirportData
{
    private const double FtToM = 0.3048;

    /// <summary>Parses the aviationweather.gov airport JSON array; null when the ICAO wasn't found.</summary>
    public static EfbAirportInfo? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JArray arr;
        try { arr = JArray.Parse(json); }
        catch (Newtonsoft.Json.JsonException) { return null; }
        if (arr.Count == 0 || arr[0] is not JObject a) return null;

        var info = new EfbAirportInfo
        {
            Icao = a.Value<string>("icaoId") ?? "",
            Name = a.Value<string>("name") ?? "",
            ElevationFt = (int)Math.Round((a.Value<double?>("elev") ?? 0) / FtToM),
            Latitude = a.Value<double?>("lat") ?? 0,
            Longitude = a.Value<double?>("lon") ?? 0,
            MagVarDeg = ParseMagVar(a.Value<string>("magdec")),
        };

        foreach (var rw in a["runways"] as JArray ?? new JArray())
        {
            var id = rw.Value<string>("id") ?? "";
            var ends = id.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (ends.Length == 0) continue;
            // helipads / water lanes aren't takeoff runways for this fleet
            if (!Regex.IsMatch(ends[0], @"^\d{1,2}[LCR]?$")) continue;

            var (lenFt, widFt) = ParseDimension(rw.Value<string>("dimension"));
            int? align = rw.Value<int?>("alignment");
            string surface = SurfaceName(rw.Value<string>("surface"));

            for (int i = 0; i < ends.Length && i < 2; i++)
            {
                // alignment is the FIRST end's true heading; the other end is its reciprocal.
                // Without it, fall back to the runway number (magnetic, ±5°) corrected by var.
                int hdg = align is int al
                    ? (i == 0 ? al : (al + 180) % 360)
                    : (int)Math.Round(Norm(RunwayNumber(ends[i]) * 10 + info.MagVarDeg));
                info.Runways.Add(new EfbRunwayEnd
                {
                    Ident = ends[i],
                    Strip = id,
                    LengthM = (int)Math.Round(lenFt * FtToM),
                    WidthM = (int)Math.Round(widFt * FtToM),
                    HeadingTrueDeg = hdg == 0 ? 360 : hdg,
                    Surface = surface,
                });
            }
        }
        info.Runways = info.Runways.OrderByDescending(r => r.LengthM).ThenBy(r => r.Ident).ToList();
        return info;
    }

    private static (double lenFt, double widFt) ParseDimension(string? dim)
    {
        var m = Regex.Match(dim ?? "", @"^\s*(\d+)\s*x\s*(\d+)");
        return m.Success
            ? (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
            : (0, 0);
    }

    /// <summary>"08E" → +8, "2W" → −2, "0" → 0.</summary>
    internal static double ParseMagVar(string? s)
    {
        var m = Regex.Match(s ?? "", @"^\s*(\d+(?:\.\d+)?)\s*([EW])?", RegexOptions.IgnoreCase);
        if (!m.Success) return 0;
        double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value.Equals("W", StringComparison.OrdinalIgnoreCase) ? -v : v;
    }

    private static int RunwayNumber(string ident) =>
        int.TryParse(Regex.Match(ident, @"^\d+").Value, out var n) ? n : 0;

    private static double Norm(double deg) => ((deg % 360) + 360) % 360;

    private static string SurfaceName(string? code) => (code ?? "").ToUpperInvariant() switch
    {
        "A" => "ASPH",
        "C" => "CONC",
        "H" => "HARD",
        "G" => "GRASS",
        "T" => "TURF",
        "D" => "DIRT",
        "W" => "WATER",
        "" => "",
        var other => other,
    };
}
