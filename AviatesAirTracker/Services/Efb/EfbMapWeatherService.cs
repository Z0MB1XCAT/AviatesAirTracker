using Serilog;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace AviatesAirTracker.Services.Efb;

// ============================================================
// EFB MAP WEATHER SERVICE
//
// Aviation weather overlays for the EFB map, proxied (and cached 5 min) through the host so
// the tablet needs no CORS/keys — the same free aviationweather.gov Data API the host already
// uses for METAR/TAF:
//   SIGMETs  — /api/data/isigmet (international) + /api/data/airsigmet (US SIGMET/AIRMET),
//              GeoJSON polygons with hazard, levels, validity and the raw bulletin
//   METARs   — /api/data/metar?bbox=…&format=geojson, one dot per station coloured by the
//              flight category AWC computes (VFR / MVFR / IFR / LIFR)
// Radar and IR satellite tiles come straight from RainViewer on the tablet.
// ============================================================

public sealed class EfbMapWeatherService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (DateTime at, object data)> _cache = new();

    public sealed class Sigmet
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";         // SIGMET | AIRMET | CONVECTIVE
        public string Hazard { get; set; } = "";       // TS | TURB | ICE | VA | MTW | …
        public string Fir { get; set; } = "";
        public int? BaseFt { get; set; }
        public int? TopFt { get; set; }
        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }
        public string Raw { get; set; } = "";
        public List<double[][]> Rings { get; set; } = [];
    }

    public sealed class MetarDot
    {
        public string Id { get; set; } = "";
        public double Lat { get; set; }
        public double Lon { get; set; }
        public string Cat { get; set; } = "";          // VFR | MVFR | IFR | LIFR
        public string Raw { get; set; } = "";
    }

    public async Task<List<Sigmet>> GetSigmetsAsync()
    {
        if (_cache.TryGetValue("sigmets", out var c) && DateTime.UtcNow - c.at < Ttl) return (List<Sigmet>)c.data;
        var list = new List<Sigmet>();
        await Task.WhenAll(
            AddSigmetsAsync(list, "https://aviationweather.gov/api/data/isigmet?format=geojson", intl: true),
            AddSigmetsAsync(list, "https://aviationweather.gov/api/data/airsigmet?format=geojson", intl: false));
        _cache["sigmets"] = (DateTime.UtcNow, list);
        return list;
    }

    private async Task AddSigmetsAsync(List<Sigmet> list, string url, bool intl)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            var found = new List<Sigmet>();
            foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
            {
                var p = f.GetProperty("properties");
                var s = new Sigmet
                {
                    Hazard = Str(p, "hazard"),
                    Fir = intl ? Str(p, "firId") : Str(p, "icaoId"),
                    Kind = intl ? "SIGMET" : (Str(p, "hazard") == "CONVECTIVE" ? "CONVECTIVE" : Str(p, "airSigmetType")),
                    BaseFt = Int(p, intl ? "base" : "altitudeLow1"),
                    TopFt = Int(p, intl ? "top" : "altitudeHi1"),
                    ValidFrom = Date(p, "validTimeFrom"),
                    ValidTo = Date(p, "validTimeTo"),
                    Raw = intl ? Str(p, "rawSigmet") : Str(p, "rawAirSigmet"),
                };
                s.Id = $"{s.Fir}-{Str(p, "seriesId")}-{s.Hazard}";
                if (f.TryGetProperty("geometry", out var g) && g.ValueKind == JsonValueKind.Object) AddRings(s, g);
                if (s.Rings.Count > 0) found.Add(s);
            }
            lock (list) list.AddRange(found);
        }
        catch (Exception ex) { Log.Debug(ex, "[EFB/Wx] SIGMET fetch failed ({Url})", url); }
    }

    /// <summary>bbox is minLat,minLon,maxLat,maxLon; snapped to a 2° grid so panning reuses the cache.</summary>
    public async Task<List<MetarDot>> GetMetarsAsync(double minLat, double minLon, double maxLat, double maxLon)
    {
        static double Dn(double v) => Math.Floor(v / 2) * 2;
        static double Up(double v) => Math.Ceiling(v / 2) * 2;
        minLat = Math.Max(-90, Dn(minLat)); maxLat = Math.Min(90, Up(maxLat));
        minLon = Math.Max(-180, Dn(minLon)); maxLon = Math.Min(180, Up(maxLon));
        // a whole-continent view would be thousands of stations — cap the box
        if (maxLat - minLat > 30 || maxLon - minLon > 40) return [];
        var key = FormattableString.Invariant($"metar:{minLat},{minLon},{maxLat},{maxLon}");
        if (_cache.TryGetValue(key, out var c) && DateTime.UtcNow - c.at < Ttl) return (List<MetarDot>)c.data;

        var list = new List<MetarDot>();
        try
        {
            var url = FormattableString.Invariant($"https://aviationweather.gov/api/data/metar?bbox={minLat},{minLon},{maxLat},{maxLon}&format=geojson");
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
            {
                var p = f.GetProperty("properties");
                var coords = f.GetProperty("geometry").GetProperty("coordinates");
                list.Add(new MetarDot
                {
                    Id = Str(p, "id"),
                    Lon = coords[0].GetDouble(), Lat = coords[1].GetDouble(),
                    Cat = Str(p, "fltcat"),
                    Raw = Str(p, "rawOb"),
                });
            }
        }
        catch (Exception ex) { Log.Debug(ex, "[EFB/Wx] METAR bbox fetch failed"); }
        _cache[key] = (DateTime.UtcNow, list);
        return list;
    }

    private static void AddRings(Sigmet s, JsonElement g)
    {
        var type = Str(g, "type");
        if (!g.TryGetProperty("coordinates", out var coords)) return;
        IEnumerable<JsonElement> polys = type switch { "Polygon" => [coords], "MultiPolygon" => coords.EnumerateArray(), _ => [] };
        foreach (var poly in polys)
        {
            var outer = poly.EnumerateArray().FirstOrDefault();
            if (outer.ValueKind != JsonValueKind.Array) continue;
            var ring = outer.EnumerateArray().Select(c => new[] { Math.Round(c[1].GetDouble(), 3), Math.Round(c[0].GetDouble(), 3) }).ToArray();
            if (ring.Length >= 3) s.Rings.Add(ring);
        }
    }

    private static string Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static int? Int(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    private static DateTime? Date(JsonElement el, string prop) =>
        DateTime.TryParse(Str(el, prop), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public void Dispose() => _http.Dispose();
}
