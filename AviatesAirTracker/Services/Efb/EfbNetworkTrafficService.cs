using AviatesAirTracker.Core.Efb;
using Serilog;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace AviatesAirTracker.Services.Efb;

// ============================================================
// EFB NETWORK TRAFFIC SERVICE
//
// Pulls live pilot/ATC positions from the two real online networks for the EFB map's
// VATSIM/IVAO layers (and the desktop MapPage) -- separate from OtherPilotsService, which is
// this app's own AviatesAir-backend traffic.
//
//   VATSIM: the main datafeed (shared with EfbAtisService's cache, see
//           GetSharedVatsimFeedAsync) has pilots with lat/lon directly; controllers and ATIS
//           stations carry no position of their own. Their position comes from the VATSpy/
//           SimAware sector data below (airport / TRACON / FIR, the way VATSIM Radar does it),
//           falling back to the transmitter lat/lon in transceivers-data.json.
//   IVAO:   the Whazzup v2 feed (https://api.ivao.aero/v2/tracker/whazzup) is public,
//           keyless, and gives both pilots and ATCs a lat/lon directly. IVAO's own docs
//           cap polling at once per 15s; this service never fetches more than once per
//           FeedTtl (30s), well inside that.
//
// Sector data (CC BY-SA 4.0 — credited in the EFB map attribution): VATSpy.dat +
// Boundaries.geojson from vatsimnetwork/vatspy-data-project and TRACONBoundaries.geojson from
// vatsimnetwork/simaware-tracon-project. Downloaded on first use, cached under
// %AppData%\AviatesAirTracker\efb_cache and refreshed weekly; a stale cache is used when
// GitHub is unreachable, and with no data at all ATC simply falls back to transmitter pins.
//
// All feeds are best-effort: a failed/slow network never blocks the others or throws into
// the caller, it just contributes nothing for that refresh.
// ============================================================

public sealed class EfbNetworkTrafficService : IDisposable
{
    private readonly EfbAtisService _atis;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly HttpClient _bulkHttp = new() { Timeout = TimeSpan.FromSeconds(60) };

    private const string VatsimTransceiversFeed = "https://data.vatsim.net/v3/transceivers-data.json";
    private const string IvaoWhazzupFeed = "https://api.ivao.aero/v2/tracker/whazzup";
    private const string VatSpyBase = "https://raw.githubusercontent.com/vatsimnetwork/vatspy-data-project/master/";
    private const string TraconUrl = "https://github.com/vatsimnetwork/simaware-tracon-project/releases/latest/download/TRACONBoundaries.geojson";
    private static readonly TimeSpan FeedTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GeoMaxAge = TimeSpan.FromDays(7);
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AviatesAirTracker", "efb_cache");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private EfbNetworkTraffic? _cached;
    private DateTime _cachedUtc = DateTime.MinValue;

    private Task<EfbAtcGeo?>? _geoTask;
    private readonly object _geoLock = new();

    public EfbNetworkTrafficService(EfbAtisService atis) => _atis = atis;

    public async Task<EfbNetworkTraffic> GetAsync()
    {
        if (_cached != null && DateTime.UtcNow - _cachedUtc < FeedTtl) return _cached;

        await _gate.WaitAsync();
        try
        {
            if (_cached != null && DateTime.UtcNow - _cachedUtc < FeedTtl) return _cached;

            var geo = await GetGeoAsync();
            var result = new EfbNetworkTraffic { HasSectorData = geo != null };
            await Task.WhenAll(
                AddVatsimAsync(result, geo),
                AddIvaoAsync(result, geo));
            BuildSectors(result, geo);

            _cached = result;
            _cachedUtc = DateTime.UtcNow;
            return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Outlines for the given sector keys (FIR:… / TRACON:…). Static data — the
    /// tablet caches them, so each shape crosses the LAN once per session.</summary>
    public async Task<Dictionary<string, List<double[][]>>> GetGeometryAsync(IEnumerable<string> keys)
    {
        var geo = await GetGeoAsync();
        var outp = new Dictionary<string, List<double[][]>>();
        if (geo == null) return outp;
        foreach (var k in keys.Distinct().Take(200))
            if (geo.GetShape(k) is { } s) outp[k] = s.Rings;
        return outp;
    }

    // ── sector data (VATSpy + SimAware) ─────────────────────

    private Task<EfbAtcGeo?> GetGeoAsync()
    {
        lock (_geoLock)
        {
            // retry a failed load on a later poll rather than caching the failure forever
            if (_geoTask == null || (_geoTask.IsCompleted && _geoTask.Result == null && _geoFailedUtc < DateTime.UtcNow.AddMinutes(-10)))
                _geoTask = LoadGeoAsync();
            return _geoTask;
        }
    }
    private DateTime _geoFailedUtc = DateTime.MinValue;

    private async Task<EfbAtcGeo?> LoadGeoAsync()
    {
        try
        {
            var dat = await CachedDownloadAsync("VATSpy.dat", VatSpyBase + "VATSpy.dat");
            var firs = await CachedDownloadAsync("Boundaries.geojson", VatSpyBase + "Boundaries.geojson");
            if (dat == null || firs == null) { _geoFailedUtc = DateTime.UtcNow; return null; }
            var tracons = await CachedDownloadAsync("TRACONBoundaries.geojson", TraconUrl);

            var geo = new EfbAtcGeo();
            geo.LoadVatSpyDat(dat);
            geo.LoadFirBoundaries(firs);
            if (tracons != null)
            {
                try { geo.LoadTraconBoundaries(tracons); }
                catch (Exception ex) { Log.Debug(ex, "[EFB/Network] TRACON data unreadable — APP falls back to airport rings"); }
            }
            Log.Information("[EFB/Network] Sector data loaded: {Airports} airports, {Shapes} shapes", geo.AirportCount, geo.ShapeCount);
            return geo;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[EFB/Network] Sector data load failed — ATC falls back to transmitter positions");
            _geoFailedUtc = DateTime.UtcNow;
            return null;
        }
    }

    private async Task<string?> CachedDownloadAsync(string file, string url)
    {
        var path = Path.Combine(CacheDir, file);
        bool fresh = File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < GeoMaxAge;
        if (fresh) return await File.ReadAllTextAsync(path);
        try
        {
            var text = await _bulkHttp.GetStringAsync(url);
            Directory.CreateDirectory(CacheDir);
            await File.WriteAllTextAsync(path, text);
            return text;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[EFB/Network] Download failed for {File}", file);
            return File.Exists(path) ? await File.ReadAllTextAsync(path) : null;   // stale beats nothing
        }
    }

    private static void BuildSectors(EfbNetworkTraffic result, EfbAtcGeo? geo)
    {
        if (geo == null) return;
        var byKey = new Dictionary<string, EfbNetworkSector>();
        foreach (var a in result.Atcs)
            foreach (var key in a.SectorKeys)
            {
                var k = a.Network + "|" + key;
                if (!byKey.TryGetValue(k, out var sec))
                {
                    var shape = geo.GetShape(key);
                    if (shape == null) continue;
                    byKey[k] = sec = new EfbNetworkSector
                    {
                        Key = key, Network = a.Network, Kind = shape.Kind,
                        Name = a.SectorName.Length > 0 ? a.SectorName : shape.Name,
                        LabelLat = shape.LabelLat, LabelLon = shape.LabelLon,
                    };
                }
                sec.Callsigns.Add(a.Callsign);
            }
        result.Sectors = byKey.Values.ToList();
    }

    private static void ApplyResolution(EfbNetworkAtc atc, EfbAtcGeo? geo, string networkFacility)
    {
        atc.Facility = EfbAtcGeo.FacilityOf(atc.Callsign, networkFacility);
        if (geo == null || atc.Facility.Length == 0) return;
        var r = geo.Resolve(atc.Callsign, atc.Facility);
        atc.AirportIcao = r.AirportIcao ?? "";
        atc.SectorKeys = r.SectorKeys.ToList();
        atc.SectorName = r.SectorName ?? "";
        if (r.Lat is double la && r.Lon is double lo) { atc.Lat = la; atc.Lon = lo; }
    }

    // ── VATSIM ──────────────────────────────────────────────

    // VATSIM facility ids (vatsim-data.json "facilities"): 0 OBS, 1 FSS, 2 DEL, 3 GND, 4 TWR, 5 APP, 6 CTR
    private static readonly string[] VatsimFacility = ["", "FSS", "DEL", "GND", "TWR", "APP", "CTR"];

    private async Task AddVatsimAsync(EfbNetworkTraffic result, EfbAtcGeo? geo)
    {
        try
        {
            var feed = await _atis.GetSharedVatsimFeedAsync();
            if (feed == null) return;
            var root = feed.RootElement;

            if (root.TryGetProperty("pilots", out var pilots) && pilots.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pilots.EnumerateArray())
                {
                    var callsign = GetString(p, "callsign");
                    if (string.IsNullOrEmpty(callsign)) continue;
                    if (!p.TryGetProperty("latitude", out var latEl) || !p.TryGetProperty("longitude", out var lonEl))
                        continue;

                    string dep = "", arr = "", type = "";
                    if (p.TryGetProperty("flight_plan", out var fp) && fp.ValueKind == JsonValueKind.Object)
                    {
                        dep = GetString(fp, "departure");
                        arr = GetString(fp, "arrival");
                        type = GetString(fp, "aircraft_short");
                        if (string.IsNullOrEmpty(type)) type = GetString(fp, "aircraft_faa");
                    }

                    result.Pilots.Add(new EfbNetworkPilot
                    {
                        Network = "VATSIM",
                        Callsign = callsign,
                        Lat = latEl.GetDouble(),
                        Lon = lonEl.GetDouble(),
                        AltitudeFt = GetInt(p, "altitude"),
                        GroundSpeedKts = GetInt(p, "groundspeed"),
                        HeadingDeg = GetInt(p, "heading"),
                        AircraftType = type,
                        DepartureIcao = dep,
                        ArrivalIcao = arr,
                    });
                }
            }

            // Transmitter positions are only needed for stations the sector data can't place.
            Dictionary<string, (double lat, double lon)>? positions = null;
            async Task<Dictionary<string, (double, double)>> Positions() => positions ??= await GetTransceiverPositionsAsync();

            if (root.TryGetProperty("controllers", out var controllers) && controllers.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in controllers.EnumerateArray())
                {
                    var callsign = GetString(c, "callsign");
                    if (string.IsNullOrEmpty(callsign)) continue;
                    int facility = GetInt(c, "facility");
                    var freqStr = GetString(c, "frequency");
                    // observers (facility 0) and the 199.998 "not controlling" frequency aren't ATC
                    if (facility <= 0 || freqStr.StartsWith("199.998")) continue;
                    double.TryParse(freqStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var freq);

                    var atc = new EfbNetworkAtc
                    {
                        Network = "VATSIM",
                        Callsign = callsign,
                        Position = callsign.Contains('_') ? callsign[(callsign.LastIndexOf('_') + 1)..] : callsign,
                        Frequency = freq,
                        Text = JoinLines(c, "text_atis"),
                    };
                    ApplyResolution(atc, geo, facility < VatsimFacility.Length ? VatsimFacility[facility] : "");
                    if (atc.Lat == null && (await Positions()).TryGetValue(callsign, out var pos)) (atc.Lat, atc.Lon) = pos;
                    result.Atcs.Add(atc);
                }
            }

            // ATIS stations live in their own array in the v3 feed
            if (root.TryGetProperty("atis", out var atisArr) && atisArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in atisArr.EnumerateArray())
                {
                    var callsign = GetString(a, "callsign");
                    if (string.IsNullOrEmpty(callsign)) continue;
                    double.TryParse(GetString(a, "frequency"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var freq);
                    var atc = new EfbNetworkAtc
                    {
                        Network = "VATSIM", Callsign = callsign, Position = "ATIS", Frequency = freq,
                        AtisCode = GetString(a, "atis_code"), Text = JoinLines(a, "text_atis"),
                    };
                    ApplyResolution(atc, geo, "ATIS");
                    atc.Facility = "ATIS";
                    if (atc.Lat == null && (await Positions()).TryGetValue(callsign, out var pos)) (atc.Lat, atc.Lon) = pos;
                    result.Atcs.Add(atc);
                }
            }
        }
        catch (Exception ex) { Log.Debug(ex, "[EFB/Network] VATSIM traffic fetch failed"); }
    }

    private async Task<Dictionary<string, (double lat, double lon)>> GetTransceiverPositionsAsync()
    {
        var map = new Dictionary<string, (double, double)>();
        try
        {
            var json = await _http.GetStringAsync(VatsimTransceiversFeed);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return map;

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var callsign = GetString(entry, "callsign");
                if (string.IsNullOrEmpty(callsign)) continue;
                if (!entry.TryGetProperty("transceivers", out var xcvrs) || xcvrs.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var x in xcvrs.EnumerateArray())
                {
                    if (x.TryGetProperty("latDeg", out var latEl) && x.TryGetProperty("lonDeg", out var lonEl))
                    {
                        map[callsign] = (latEl.GetDouble(), lonEl.GetDouble());
                        break;
                    }
                }
            }
        }
        catch (Exception ex) { Log.Debug(ex, "[EFB/Network] VATSIM transceivers fetch failed"); }
        return map;
    }

    // ── IVAO ────────────────────────────────────────────────

    private async Task AddIvaoAsync(EfbNetworkTraffic result, EfbAtcGeo? geo)
    {
        try
        {
            var json = await _http.GetStringAsync(IvaoWhazzupFeed);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("clients", out var clients) && clients.ValueKind == JsonValueKind.Object)
                root = clients; // Whazzup v2 nests pilots/atcs/observers under "clients"

            if (root.TryGetProperty("pilots", out var pilots) && pilots.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pilots.EnumerateArray())
                {
                    var callsign = GetString(p, "callsign");
                    if (string.IsNullOrEmpty(callsign)) continue;
                    if (!p.TryGetProperty("lastTrack", out var track) || track.ValueKind != JsonValueKind.Object)
                        continue;
                    if (!track.TryGetProperty("latitude", out var latEl) || !track.TryGetProperty("longitude", out var lonEl))
                        continue;

                    string dep = "", arr = "", type = "";
                    if (p.TryGetProperty("flightPlan", out var fp) && fp.ValueKind == JsonValueKind.Object)
                    {
                        dep = GetString(fp, "departureId");
                        arr = GetString(fp, "arrivalId");
                        if (fp.TryGetProperty("aircraft", out var ac) && ac.ValueKind == JsonValueKind.Object)
                            type = GetString(ac, "icaoCode");
                    }

                    result.Pilots.Add(new EfbNetworkPilot
                    {
                        Network = "IVAO",
                        Callsign = callsign,
                        Lat = latEl.GetDouble(),
                        Lon = lonEl.GetDouble(),
                        AltitudeFt = GetInt(track, "altitude"),
                        GroundSpeedKts = GetInt(track, "groundSpeed"),
                        HeadingDeg = GetInt(track, "heading"),
                        AircraftType = type,
                        DepartureIcao = dep,
                        ArrivalIcao = arr,
                    });
                }
            }

            if (root.TryGetProperty("atcs", out var atcs) && atcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in atcs.EnumerateArray())
                {
                    var callsign = GetString(a, "callsign");
                    if (string.IsNullOrEmpty(callsign)) continue;
                    if (!a.TryGetProperty("lastTrack", out var track) || track.ValueKind != JsonValueKind.Object)
                        continue;
                    if (!track.TryGetProperty("latitude", out var latEl) || !track.TryGetProperty("longitude", out var lonEl))
                        continue;

                    string position = "";
                    double freq = 0;
                    if (a.TryGetProperty("atcSession", out var sess) && sess.ValueKind == JsonValueKind.Object)
                    {
                        position = GetString(sess, "position");
                        if (sess.TryGetProperty("frequency", out var freqEl) && freqEl.ValueKind == JsonValueKind.Number)
                            freq = freqEl.GetDouble();
                    }

                    string atisCode = "", text = "";
                    if (a.TryGetProperty("atis", out var atis) && atis.ValueKind == JsonValueKind.Object)
                    {
                        atisCode = GetString(atis, "revision");
                        text = JoinLines(atis, "lines");
                    }

                    var atc = new EfbNetworkAtc
                    {
                        Network = "IVAO",
                        Callsign = callsign,
                        Position = position,
                        Frequency = freq,
                        Lat = latEl.GetDouble(),
                        Lon = lonEl.GetDouble(),
                        AtisCode = atisCode,
                        Text = text,
                    };
                    ApplyResolution(atc, geo, position);
                    result.Atcs.Add(atc);
                }
            }
        }
        catch (Exception ex) { Log.Debug(ex, "[EFB/Network] IVAO whazzup fetch failed"); }
    }

    // ── helpers ─────────────────────────────────────────────

    private static string GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int GetInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static string JoinLines(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array
            ? string.Join("\n", v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()))
            : "";

    public void Dispose()
    {
        _http.Dispose();
        _bulkHttp.Dispose();
        _gate.Dispose();
    }
}
