using System.Globalization;
using System.Text.Json;

namespace AviatesAirTracker.Core.Efb;

// ============================================================
// EFB ATC GEOGRAPHY
//
// Turns an online-network ATC callsign into something drawable, the way VATSIM Radar /
// Navigraph / VATSpy do it:
//   DEL / GND / TWR / ATIS  → the airport's reference point (EGLL_TWR → EGLL)
//   APP / DEP               → the TRACON polygon whose prefix matches (SimAware data),
//                             else a ring around the airport
//   CTR / FSS               → the FIR boundary polygon (VATSpy FIR + UIR tables)
//
// Data (both CC BY-SA 4.0, downloaded at runtime by EfbNetworkTrafficService, credited on
// the map): the VATSpy data project (VATSpy.dat + Boundaries.geojson) and the SimAware
// TRACON project (TRACONBoundaries.geojson). Everything here is pure parsing/lookup so it
// can be unit-tested without network access.
// ============================================================

public sealed record AtcAirport(string Icao, string Name, double Lat, double Lon);

/// <summary>One drawable sector: a FIR/UIR ("FIR:EGTT") or a TRACON ("TRACON:A80:ATL").</summary>
public sealed class AtcSectorShape
{
    public string Key { get; init; } = "";
    public string Kind { get; init; } = "";          // FIR | TRACON
    public string Name { get; init; } = "";
    public double LabelLat { get; set; }
    public double LabelLon { get; set; }
    /// <summary>Outer rings only, each as [lat, lon] pairs, simplified for the wire.</summary>
    public List<double[][]> Rings { get; init; } = [];
}

public sealed record AtcResolution(string Facility, string? AirportIcao, double? Lat, double? Lon, IReadOnlyList<string> SectorKeys, string? SectorName);

public sealed class EfbAtcGeo
{
    private readonly Dictionary<string, AtcAirport> _airports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _iataToIcao = new(StringComparer.OrdinalIgnoreCase);
    // callsign prefix → (boundary id, display name)
    private readonly Dictionary<string, (string boundary, string name)> _firPrefixes = new(StringComparer.OrdinalIgnoreCase);
    // UIR prefix → member FIR ICAOs
    private readonly Dictionary<string, (string name, string[] firs)> _uirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AtcSectorShape> _shapes = new(StringComparer.OrdinalIgnoreCase);
    // TRACON prefix → shape key
    private readonly Dictionary<string, string> _traconPrefixes = new(StringComparer.OrdinalIgnoreCase);

    public int AirportCount => _airports.Count;
    public int ShapeCount => _shapes.Count;
    public bool IsLoaded => _airports.Count > 0;

    // ── loading ─────────────────────────────────────────────

    /// <summary>VATSpy.dat: pipe-separated [Airports] / [FIRs] / [UIRs] sections, ';' comments.</summary>
    public void LoadVatSpyDat(string text)
    {
        string section = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            if (line[0] == '[') { section = line.Trim('[', ']'); continue; }
            var f = line.Split('|');
            switch (section)
            {
                case "Airports" when f.Length >= 7:
                    // pseudo airports are FIR/UIR label points, not aerodromes
                    if (f[6].Trim() == "1") break;
                    if (!double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                        !double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) break;
                    var icao = f[0].Trim();
                    _airports.TryAdd(icao, new AtcAirport(icao, f[1].Trim(), lat, lon));
                    var lid = f[4].Trim();
                    if (lid.Length > 0) _iataToIcao.TryAdd(lid, icao);
                    break;
                case "FIRs" when f.Length >= 4:
                    var prefix = f[2].Trim().Length > 0 ? f[2].Trim() : f[0].Trim();
                    var boundary = f[3].Trim().Length > 0 ? f[3].Trim() : f[0].Trim();
                    _firPrefixes.TryAdd(prefix, (boundary, f[1].Trim()));
                    break;
                case "UIRs" when f.Length >= 3:
                    _uirs.TryAdd(f[0].Trim(), (f[1].Trim(), f[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
                    break;
            }
        }
    }

    /// <summary>VATSpy Boundaries.geojson — FIR polygons keyed by properties.id.</summary>
    public void LoadFirBoundaries(string geojson)
    {
        using var doc = JsonDocument.Parse(geojson);
        foreach (var feat in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var props = feat.GetProperty("properties");
            var id = Str(props, "id");
            if (id.Length == 0) continue;
            var key = "FIR:" + id;
            var shape = GetOrAddShape(key, "FIR", id);
            AddRings(shape, feat.GetProperty("geometry"));
            if (double.TryParse(Str(props, "label_lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out var la) &&
                double.TryParse(Str(props, "label_lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lo))
            { shape.LabelLat = la; shape.LabelLon = lo; }
        }
    }

    /// <summary>SimAware TRACONBoundaries.geojson — properties.id / prefix[] / name.</summary>
    public void LoadTraconBoundaries(string geojson)
    {
        using var doc = JsonDocument.Parse(geojson);
        foreach (var feat in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var props = feat.GetProperty("properties");
            var id = Str(props, "id");
            var prefixes = props.TryGetProperty("prefix", out var p) && p.ValueKind == JsonValueKind.Array
                ? p.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                : [];
            if (id.Length == 0 || prefixes.Length == 0) continue;
            var key = $"TRACON:{id}:{prefixes[0]}";
            var shape = GetOrAddShape(key, "TRACON", Str(props, "name"));
            AddRings(shape, feat.GetProperty("geometry"));
            foreach (var pre in prefixes) _traconPrefixes.TryAdd(pre, key);
        }
        // label = centroid of the first ring's points
        foreach (var s in _shapes.Values.Where(s => s.Kind == "TRACON" && s.Rings.Count > 0 && s.LabelLat == 0))
        { s.LabelLat = s.Rings[0].Average(pt => pt[0]); s.LabelLon = s.Rings[0].Average(pt => pt[1]); }
    }

    // ── resolution ──────────────────────────────────────────

    /// <summary>Facility from the callsign suffix, falling back to the network's own facility code.</summary>
    public static string FacilityOf(string callsign, string? networkFacility = null)
    {
        var suffix = callsign.Contains('_') ? callsign[(callsign.LastIndexOf('_') + 1)..].ToUpperInvariant() : "";
        return suffix switch
        {
            "DEL" or "CLD" => "DEL",
            "GND" or "RMP" or "APN" => "GND",
            "TWR" => "TWR",
            "APP" or "DEP" or "TMA" => "APP",
            "CTR" => "CTR",
            "FSS" => "FSS",
            "ATIS" => "ATIS",
            _ => (networkFacility ?? "").ToUpperInvariant() switch
            {
                "DEP" => "APP",
                var f when f is "DEL" or "GND" or "TWR" or "APP" or "CTR" or "FSS" => f,
                _ => "",
            },
        };
    }

    public AtcResolution Resolve(string callsign, string facility)
    {
        var parts = callsign.ToUpperInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries);
        var head = parts.Length > 0 ? parts[0] : "";
        var airport = FindAirport(head);

        switch (facility)
        {
            case "DEL" or "GND" or "TWR" or "ATIS":
                return new(facility, airport?.Icao, airport?.Lat, airport?.Lon, [], airport?.Name);

            case "APP":
                // try "NY_CAM" style multi-part prefixes before the bare head
                for (int n = parts.Length - 1; n >= 1; n--)
                {
                    var pre = string.Join('_', parts.Take(n));
                    if (_traconPrefixes.TryGetValue(pre, out var key) && _shapes.TryGetValue(key, out var shape))
                        return new(facility, airport?.Icao, shape.LabelLat, shape.LabelLon, [key], shape.Name);
                }
                return new(facility, airport?.Icao, airport?.Lat, airport?.Lon, [], airport?.Name);

            case "CTR" or "FSS":
                for (int n = parts.Length - 1; n >= 1; n--)
                {
                    var pre = string.Join('_', parts.Take(n));
                    if (_firPrefixes.TryGetValue(pre, out var fir) && _shapes.TryGetValue("FIR:" + fir.boundary, out var shape))
                        return new(facility, null, shape.LabelLat, shape.LabelLon, ["FIR:" + fir.boundary], fir.name);
                    if (_uirs.TryGetValue(pre, out var uir))
                    {
                        var keys = uir.firs
                            .Select(icao => _firPrefixes.TryGetValue(icao, out var f) ? "FIR:" + f.boundary : "FIR:" + icao)
                            .Where(_shapes.ContainsKey).Distinct().ToList();
                        if (keys.Count > 0)
                        {
                            var first = _shapes[keys[0]];
                            return new(facility, null, first.LabelLat, first.LabelLon, keys, uir.name);
                        }
                    }
                }
                return new(facility, null, null, null, [], null);

            default:
                return new(facility, airport?.Icao, airport?.Lat, airport?.Lon, [], null);
        }
    }

    public AtcAirport? FindAirport(string code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        if (_airports.TryGetValue(code, out var a)) return a;
        if (_iataToIcao.TryGetValue(code, out var icao) && _airports.TryGetValue(icao, out a)) return a;
        // US controllers log on with the 3-letter FAA id: K + id (contiguous US), P + id (HI/AK)
        if (code.Length == 3)
            foreach (var pre in new[] { "K", "P" })
                if (_airports.TryGetValue(pre + code, out a)) return a;
        return null;
    }

    public AtcSectorShape? GetShape(string key) => _shapes.TryGetValue(key, out var s) ? s : null;

    // ── geometry helpers ────────────────────────────────────

    private AtcSectorShape GetOrAddShape(string key, string kind, string name)
    {
        if (!_shapes.TryGetValue(key, out var s)) _shapes[key] = s = new AtcSectorShape { Key = key, Kind = kind, Name = name };
        return s;
    }

    private static void AddRings(AtcSectorShape shape, JsonElement geom)
    {
        var type = Str(geom, "type");
        var coords = geom.GetProperty("coordinates");
        IEnumerable<JsonElement> polygons = type switch
        {
            "Polygon" => [coords],
            "MultiPolygon" => coords.EnumerateArray(),
            _ => [],
        };
        foreach (var poly in polygons)
        {
            var outer = poly.EnumerateArray().FirstOrDefault();
            if (outer.ValueKind != JsonValueKind.Array) continue;
            // GeoJSON is [lon, lat]; the map wants [lat, lon]
            var pts = outer.EnumerateArray().Select(c => new[] { c[1].GetDouble(), c[0].GetDouble() }).ToList();
            var simple = Simplify(pts, 0.02);
            if (simple.Count >= 3) shape.Rings.Add(simple.Select(p => new[] { Math.Round(p[0], 3), Math.Round(p[1], 3) }).ToArray());
        }
    }

    /// <summary>Radial-distance thinning (drops vertices closer than tol° to the last kept one).
    /// FIR outlines carry thousands of survey points that are invisible at map scale.</summary>
    internal static List<double[]> Simplify(List<double[]> pts, double tol)
    {
        if (pts.Count <= 4) return pts;
        var outp = new List<double[]> { pts[0] };
        double t2 = tol * tol;
        for (int i = 1; i < pts.Count - 1; i++)
        {
            var last = outp[^1];
            double dy = pts[i][0] - last[0], dx = pts[i][1] - last[1];
            if (dx * dx + dy * dy >= t2) outp.Add(pts[i]);
        }
        outp.Add(pts[^1]);
        return outp;
    }

    private static string Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        } : "";
}
