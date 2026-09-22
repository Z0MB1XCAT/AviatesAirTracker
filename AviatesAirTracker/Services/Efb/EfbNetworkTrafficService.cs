using AviatesAirTracker.Core.Efb;
using Serilog;
using System.Net.Http;
using System.Text.Json;

namespace AviatesAirTracker.Services.Efb;

// ============================================================
// EFB NETWORK TRAFFIC SERVICE
//
// Pulls live pilot/ATC positions from the two real online networks for the EFB map's
// VATSIM/IVAO layers -- separate from OtherPilotsService, which is this app's own
// AviatesAir-backend traffic.
//
//   VATSIM: the main datafeed (shared with EfbAtisService's cache, see
//           GetSharedVatsimFeedAsync) has pilots with lat/lon directly, but controllers
//           carry no position of their own -- their transmitter lat/lon comes from a
//           second feed (transceivers-data.json) joined here by callsign, the same
//           technique VATSIM's own radar tools use.
//   IVAO:   the Whazzup v2 feed (https://api.ivao.aero/v2/tracker/whazzup) is public,
//           keyless, and gives both pilots and ATCs a lat/lon directly. IVAO's own docs
//           cap polling at once per 15s; this service never fetches more than once per
//           FeedTtl (30s), well inside that.
//
// All three feeds are best-effort: a failed/slow network never blocks the others or
// throws into the caller, it just contributes nothing for that refresh.
// ============================================================

public sealed class EfbNetworkTrafficService : IDisposable
{
    private readonly EfbAtisService _atis;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private const string VatsimTransceiversFeed = "https://data.vatsim.net/v3/transceivers-data.json";
    private const string IvaoWhazzupFeed = "https://api.ivao.aero/v2/tracker/whazzup";
    private static readonly TimeSpan FeedTtl = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private EfbNetworkTraffic? _cached;
    private DateTime _cachedUtc = DateTime.MinValue;

    public EfbNetworkTrafficService(EfbAtisService atis) => _atis = atis;

    public async Task<EfbNetworkTraffic> GetAsync()
    {
        if (_cached != null && DateTime.UtcNow - _cachedUtc < FeedTtl) return _cached;

        await _gate.WaitAsync();
        try
        {
            if (_cached != null && DateTime.UtcNow - _cachedUtc < FeedTtl) return _cached;

            var result = new EfbNetworkTraffic();
            await Task.WhenAll(
                AddVatsimAsync(result),
                AddIvaoAsync(result));

            _cached = result;
            _cachedUtc = DateTime.UtcNow;
            return result;
        }
        finally { _gate.Release(); }
    }

    // ── VATSIM ──────────────────────────────────────────────

    private async Task AddVatsimAsync(EfbNetworkTraffic result)
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

            if (!root.TryGetProperty("controllers", out var controllers) || controllers.ValueKind != JsonValueKind.Array)
                return;

            // Controllers have no lat/lon in the main feed -- resolve each callsign's transmitter
            // position from the separate transceivers feed (first transceiver is good enough for
            // a map pin; a station can have several covering different frequencies/antennas).
            var positions = await GetTransceiverPositionsAsync();

            foreach (var c in controllers.EnumerateArray())
            {
                var callsign = GetString(c, "callsign");
                if (string.IsNullOrEmpty(callsign)) continue;

                double? lat = null, lon = null;
                if (positions.TryGetValue(callsign, out var pos)) (lat, lon) = pos;

                var freqStr = GetString(c, "frequency");
                double.TryParse(freqStr, System.Globalization.CultureInfo.InvariantCulture, out var freq);

                var lastSeg = callsign.Contains('_') ? callsign[(callsign.LastIndexOf('_') + 1)..] : callsign;

                result.Atcs.Add(new EfbNetworkAtc
                {
                    Network = "VATSIM",
                    Callsign = callsign,
                    Position = lastSeg,
                    Frequency = freq,
                    Lat = lat,
                    Lon = lon,
                });
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

    private async Task AddIvaoAsync(EfbNetworkTraffic result)
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

                    result.Atcs.Add(new EfbNetworkAtc
                    {
                        Network = "IVAO",
                        Callsign = callsign,
                        Position = position,
                        Frequency = freq,
                        Lat = latEl.GetDouble(),
                        Lon = lonEl.GetDouble(),
                    });
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

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
