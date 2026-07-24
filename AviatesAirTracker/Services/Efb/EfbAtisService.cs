using AviatesAirTracker.Services;
using Serilog;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AviatesAirTracker.Services.Efb;

// ============================================================
// EFB ATIS SERVICE
//
// Two sources, in priority order:
//   1. VATSIM datafeed — the real controller-issued ATIS when an
//      ATIS station is online for that airport (includes the
//      information letter).
//   2. A METAR-derived ATIS, generated locally, so the EFB always
//      has something useful even offline-network / uncontrolled.
//
// The VATSIM datafeed is a single large JSON document, so it is
// fetched at most once every 90 s and shared across airports.
// ============================================================

public sealed class EfbAtisResult
{
    public string Icao { get; set; } = "";
    public bool Found { get; set; }
    public string Source { get; set; } = "";      // "VATSIM" | "METAR" | "NONE"
    public string Letter { get; set; } = "";      // information letter, if known
    public string Text { get; set; } = "";
    public string Metar { get; set; } = "";
    public string Taf { get; set; } = "";
}

public sealed class EfbAtisService : IDisposable
{
    private readonly SimBriefService _simBrief;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };

    private const string VatsimFeed = "https://data.vatsim.net/v3/vatsim-data.json";
    private static readonly TimeSpan FeedTtl = TimeSpan.FromSeconds(90);

    private readonly SemaphoreSlim _feedGate = new(1, 1);
    private JsonDocument? _feed;
    private DateTime _feedFetchedUtc = DateTime.MinValue;

    public EfbAtisService(SimBriefService simBrief) => _simBrief = simBrief;

    public async Task<EfbAtisResult> GetAsync(string icao)
    {
        icao = (icao ?? "").Trim().ToUpperInvariant();
        var result = new EfbAtisResult { Icao = icao, Source = "NONE" };
        if (icao.Length is < 3 or > 4) return result;

        // Weather first — used by both the VATSIM display and the fallback.
        result.Metar = await SafeMetarAsync(icao);
        result.Taf = await SafeTafAsync(icao);

        var vatsim = await TryVatsimAsync(icao);
        if (vatsim != null)
        {
            result.Found = true;
            result.Source = "VATSIM";
            result.Letter = vatsim.Value.letter;
            result.Text = vatsim.Value.text;
            return result;
        }

        if (!string.IsNullOrWhiteSpace(result.Metar))
        {
            result.Found = true;
            result.Source = "METAR";
            result.Text = BuildMetarAtis(icao, result.Metar);
        }
        return result;
    }

    // ── VATSIM ──────────────────────────────────────────────

    private async Task<(string letter, string text)?> TryVatsimAsync(string icao)
    {
        try
        {
            var feed = await GetFeedAsync();
            if (feed == null) return null;
            if (!feed.RootElement.TryGetProperty("atis", out var atisArr) ||
                atisArr.ValueKind != JsonValueKind.Array) return null;

            foreach (var a in atisArr.EnumerateArray())
            {
                var callsign = a.TryGetProperty("callsign", out var cs) ? cs.GetString() ?? "" : "";
                // Matches EGLL_ATIS, KJFK_D_ATIS, LFPG_A_ATIS …
                if (!callsign.StartsWith(icao + "_", StringComparison.OrdinalIgnoreCase)) continue;
                if (!callsign.Contains("ATIS", StringComparison.OrdinalIgnoreCase)) continue;

                string letter = a.TryGetProperty("atis_code", out var lc) ? lc.GetString() ?? "" : "";
                var sb = new StringBuilder();
                if (a.TryGetProperty("text_atis", out var ta) && ta.ValueKind == JsonValueKind.Array)
                    foreach (var line in ta.EnumerateArray())
                        sb.AppendLine(line.GetString());

                var text = sb.ToString().Trim();
                if (text.Length > 0) return (letter, text);
            }
        }
        catch (Exception ex) { Log.Debug(ex, "[EFB/ATIS] VATSIM lookup failed for {Icao}", icao); }
        return null;
    }

    private async Task<JsonDocument?> GetFeedAsync()
    {
        if (_feed != null && DateTime.UtcNow - _feedFetchedUtc < FeedTtl) return _feed;
        await _feedGate.WaitAsync();
        try
        {
            if (_feed != null && DateTime.UtcNow - _feedFetchedUtc < FeedTtl) return _feed;
            var json = await _http.GetStringAsync(VatsimFeed);
            var doc = JsonDocument.Parse(json);
            _feed?.Dispose();
            _feed = doc;
            _feedFetchedUtc = DateTime.UtcNow;
            return _feed;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[EFB/ATIS] VATSIM datafeed fetch failed");
            return _feed;   // serve stale rather than nothing
        }
        finally { _feedGate.Release(); }
    }

    // ── METAR-derived fallback ──────────────────────────────

    /// <summary>Turns a raw METAR into a readable, ATIS-style briefing block.</summary>
    private static string BuildMetarAtis(string icao, string metar)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{icao} AUTOMATED INFORMATION (generated from METAR)");
        sb.AppendLine();

        var parts = metar.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Time  ddhhmmZ
        var time = parts.FirstOrDefault(p => p.Length == 7 && p.EndsWith("Z") && p[..6].All(char.IsDigit));
        if (time != null) sb.AppendLine($"TIME .............. {time[2..4]}:{time[4..6]}Z");

        // Wind  dddffKT / dddffGggKT / VRBffKT
        var wind = parts.FirstOrDefault(p => p.EndsWith("KT") && p.Length >= 7);
        if (wind != null)
        {
            var w = wind[..^2];
            string dir = w[..3], spd = w.Length >= 5 ? w[3..5] : "00";
            string gust = w.Contains('G') ? $" GUSTING {w[(w.IndexOf('G') + 1)..]}" : "";
            sb.AppendLine(dir == "VRB"
                ? $"WIND .............. VARIABLE {spd.TrimStart('0')} KT{gust}"
                : $"WIND .............. {dir}° / {spd.TrimStart('0')} KT{gust}");
        }

        // Visibility
        if (metar.Contains("CAVOK")) sb.AppendLine("VISIBILITY ........ CAVOK");
        else
        {
            var vis = parts.FirstOrDefault(p => p.Length == 4 && p.All(char.IsDigit));
            if (vis != null) sb.AppendLine($"VISIBILITY ........ {(vis == "9999" ? "10 KM OR MORE" : vis + " M")}");
        }

        // Cloud
        var clouds = parts.Where(p => p.StartsWith("FEW") || p.StartsWith("SCT") ||
                                      p.StartsWith("BKN") || p.StartsWith("OVC")).ToList();
        if (clouds.Count > 0)
            sb.AppendLine("CLOUD ............. " + string.Join(", ", clouds.Select(DecodeCloud)));

        // Temp / dew  TT/DD (M-prefixed = negative)
        var td = parts.FirstOrDefault(p => p.Contains('/') && p.Length is >= 5 and <= 7 &&
                                           p.Replace("M", "").Replace("/", "").All(char.IsDigit));
        if (td != null)
        {
            var bits = td.Split('/');
            sb.AppendLine($"TEMPERATURE ....... {bits[0].Replace("M", "-")}°C   DEW POINT {bits[1].Replace("M", "-")}°C");
        }

        // QNH
        var q = parts.FirstOrDefault(p => p.StartsWith("Q") && p.Length == 5 && p[1..].All(char.IsDigit));
        if (q != null) sb.AppendLine($"QNH ............... {q[1..]} hPa");
        var alt = parts.FirstOrDefault(p => p.StartsWith("A") && p.Length == 5 && p[1..].All(char.IsDigit));
        if (alt != null) sb.AppendLine($"ALTIMETER ......... {alt[1..3]}.{alt[3..]} inHg");

        sb.AppendLine();
        sb.AppendLine("RAW METAR");
        sb.AppendLine(metar);
        sb.AppendLine();
        sb.AppendLine("No online ATIS station — generated locally. Advise you have information on initial contact.");
        return sb.ToString().Trim();
    }

    private static string DecodeCloud(string g)
    {
        string kind = g[..3] switch
        {
            "FEW" => "FEW", "SCT" => "SCATTERED", "BKN" => "BROKEN", "OVC" => "OVERCAST", _ => g[..3]
        };
        var digits = new string(g.Skip(3).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var h) ? $"{kind} {h * 100} FT" : g;
    }

    // ── weather helpers ─────────────────────────────────────

    private async Task<string> SafeMetarAsync(string icao)
    {
        try { return await _simBrief.FetchMetarAsync(icao) ?? ""; } catch { return ""; }
    }

    private async Task<string> SafeTafAsync(string icao)
    {
        try
        {
            return (await _http.GetStringAsync(
                $"https://aviationweather.gov/api/data/taf?ids={icao}&format=raw")).Trim();
        }
        catch { return ""; }
    }

    public void Dispose()
    {
        _feed?.Dispose();
        _http.Dispose();
        _feedGate.Dispose();
    }
}
