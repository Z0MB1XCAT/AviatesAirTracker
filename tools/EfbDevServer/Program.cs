// ============================================================
// EFB DEV SERVER
//
// Serves the tablet frontend (AviatesAirTracker/EfbWeb) from disk with the same /efb/api and
// /efb/ws surface as Services/Efb/EfbServer.cs. Everything with logic runs the app's own code
// (linked in EfbDevServer.csproj); only the live inputs are simulated:
//   • a flight, VAV123 EGLL→LSZH in an A320neo, cruising along its route (loops every 40 min)
//   • company messages, friends and AviatesAir traffic
//   • ACARS / CPDLC answers from "ATC" a few seconds after you send
// Weather, airports, ATIS and VATSIM/IVAO come from the real feeds, through the app's services.
// No pairing: any token is accepted. State lives in memory and resets on restart.
// ============================================================
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Core.Efb;
using AviatesAirTracker.Models;
using AviatesAirTracker.Services.Efb;

var port = Arg("--port") ?? "8765";
var lan = args.Contains("--lan");                       // also listen on the LAN (for a real tablet)
var scenario = Arg("--scenario");                       // cruise (default) | full | none
var repo = FindRepo();
var webRoot = Path.Combine(repo, "AviatesAirTracker", "EfbWeb");
var manualsDir = Path.Combine(repo, "AviatesAirTracker", "Manuals");

var J = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
};

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls(lan ? $"http://0.0.0.0:{port}" : $"http://127.0.0.1:{port}");
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});
var app = builder.Build();
app.UseWebSockets();

// ── the app's services ──
var simBrief = new AviatesAirTracker.Services.SimBriefService();
var atis = new EfbAtisService(simBrief);
var mapWx = new EfbMapWeatherService();
var network = new EfbNetworkTrafficService(atis);
var checklists = new EfbChecklistService();
var flight = new DevFlight();
if (scenario != null) flight.SetScenario(scenario);
var dev = new DevState(manualsDir);
var sockets = new ConcurrentDictionary<Guid, DevSocket>();
var airportCache = new ConcurrentDictionary<string, EfbAirportInfo>(StringComparer.OrdinalIgnoreCase);
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("AviatesAir-EFB-DevServer/1.0");

string Env(string type, object data) => JsonSerializer.Serialize(new { type, data }, J);
async Task Broadcast(string payload) { foreach (var s in sockets.Values) if (!await s.SendAsync(payload)) sockets.TryRemove(s.Id, out _); }
Task PushMessages() => Broadcast(Env("messages", dev.Messaging()));
Task PushAcars() => Broadcast(Env("acars", dev.Acars.State()));
IResult Json(object o, int status = 200) => Results.Json(o, J, statusCode: status);
checklists.Changed += (_, _) => _ = Broadcast(Env("checklists", checklists.GetBundle(DevFlight.Type)));
dev.Acars.Changed += () => _ = PushAcars();
_ = flight.LoadWeatherAsync(atis);          // real METARs into the plan and briefing

async Task<EfbAirportInfo?> Airport(string id)
{
    if (airportCache.TryGetValue(id, out var hit)) return hit;
    try
    {
        var info = EfbAirportData.Parse(await http.GetStringAsync($"https://aviationweather.gov/api/data/airport?ids={id}&format=json"));
        if (info != null) airportCache[id] = info;
        return info;
    }
    catch { return null; }
}

// ── static files, straight from disk so an edit shows on refresh ──
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "/";
    if (path.StartsWith("/efb/")) { await next(); return; }
    var rel = path == "/" ? "index.html" : path.TrimStart('/');
    var file = Path.GetFullPath(Path.Combine(webRoot, rel));
    if (!file.StartsWith(webRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) { ctx.Response.StatusCode = 404; return; }
    ctx.Response.ContentType = Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8", ".css" => "text/css; charset=utf-8", ".js" => "text/javascript; charset=utf-8",
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".svg" => "image/svg+xml",
        ".json" => "application/json", ".ico" => "image/x-icon", ".woff2" => "font/woff2", _ => "application/octet-stream",
    };
    ctx.Response.Headers.CacheControl = "no-store";
    await ctx.Response.SendFileAsync(file);
});

// ── pairing / identity ──
app.MapGet("/efb/api/hello", () => Json(new EfbServerInfo { Version = "dev", PilotName = DevState.MyName, TokenValid = true }));
app.MapPost("/efb/api/pair", () => Json(new { token = "dev" }));
app.MapGet("/efb/api/devices", () => Json(new
{
    pairingCode = "000000", url = $"http://localhost:{port}/",
    devices = sockets.Values.OrderBy(s => s.ConnectedUtc).Select(s => new { id = s.Id, name = s.Name, ip = s.Ip, connectedUtc = s.ConnectedUtc }),
}));
app.MapPost("/efb/api/pairing/reset", () => Json(new { ok = true }));
app.MapGet("/efb/api/prefs", () => Json(dev.Prefs));
app.MapPost("/efb/api/prefs", (PrefsReq r) =>
{
    if (r.WeightUnit is "KG" or "LB") { dev.Prefs = new { weightUnit = r.WeightUnit }; _ = Broadcast(Env("prefs", dev.Prefs)); }
    return Json(dev.Prefs);
});
app.MapGet("/efb/api/theme", () => Json(new { theme = dev.Theme }));
app.MapPost("/efb/api/theme", (ThemeReq r) => { dev.Theme = string.IsNullOrWhiteSpace(r.Theme) ? "aviates" : r.Theme; return Json(new { ok = true, theme = dev.Theme }); });
app.MapGet("/efb/api/wallpaper", () => dev.Wallpaper == null ? Results.NotFound() : Results.Bytes(dev.Wallpaper, "image/jpeg"));
app.MapPost("/efb/api/wallpaper", async (HttpContext ctx) =>
{
    using var ms = new MemoryStream(); await ctx.Request.Body.CopyToAsync(ms);
    dev.Wallpaper = ms.Length > 0 ? ms.ToArray() : null; return Json(new { ok = true });
});
app.MapDelete("/efb/api/wallpaper", () => { dev.Wallpaper = null; return Json(new { ok = true }); });

// ── flight ──
app.MapGet("/efb/api/state", () => Json(flight.Snapshot()));
app.MapGet("/efb/api/flightplan", () => Json(flight.Plan));
app.MapPost("/efb/api/flightplan/import", () =>
{
    flight.Plan.FromEfbImport = true;
    _ = Broadcast(Env("flightplan", flight.Plan)); _ = PushAcars();
    return Json(flight.Plan);
});
app.MapGet("/efb/api/ofp", () => Json(flight.Briefing()));
app.MapPost("/efb/api/flight/end", () => Json(new { ok = true }));

// ── performance (the real calculator and data) ──
app.MapGet("/efb/api/aircraft", () => Json(AircraftPerformanceProfiles.All.Select(p => new
{
    p.Code, p.Name, p.Category, p.MtowLbs, p.MlwLbs, p.OewLbs, p.Aliases, p.Fleet,
    takeoffFlaps = EfbCompanyPolicy.TakeoffFlaps(p).Select(f => f.Label),
    landingFlaps = EfbCompanyPolicy.LandingFlaps(p).Select(f => f.Label),
    standardLandingFlap = EfbCompanyPolicy.StandardLandingFlap(p),
    companyNotices = EfbCompanyPolicy.Notices(p.Code),
})));
app.MapPost("/efb/api/perf/takeoff", (TakeoffPerfRequest r) => Json(EfbPerformanceCalculator.CalculateTakeoff(r)));
app.MapPost("/efb/api/perf/landing", (LandingPerfRequest r) => Json(EfbPerformanceCalculator.CalculateLanding(r)));
app.MapGet("/efb/api/perf/landing/failures", (string? type) => Json(new
{
    family = EfbLandingFailures.FamilyOf(type),
    categories = string.IsNullOrWhiteSpace(type) ? EfbLandingFailures.Categories : EfbLandingFailures.CategoriesFor(type),
    failures = string.IsNullOrWhiteSpace(type) ? EfbLandingFailures.All : EfbLandingFailures.ForAircraft(type),
}));

// ── checklists (the real service) ──
string ActiveSet() => checklists.ActiveSet(DevFlight.Type).Id;
app.MapGet("/efb/api/checklists", (string? type) => Json(checklists.GetBundle(string.IsNullOrWhiteSpace(type) ? DevFlight.Type : type)));
app.MapPost("/efb/api/checklists/select", (ChecklistSelectReq r) =>
    checklists.SelectSet(r.SetId) ? Json(checklists.GetBundle(DevFlight.Type)) : Json(new { error = "Unknown checklist set" }, 400));
app.MapPost("/efb/api/checklists/item", (ChecklistItemReq r) =>
    checklists.SetItem(r.SetId ?? ActiveSet(), r.ChecklistId, r.ItemId, r.Checked) ? Json(new { ok = true }) : Json(new { error = "Unknown checklist item" }, 400));
app.MapPost("/efb/api/checklists/complete", (ChecklistResetReq r) =>
    !string.IsNullOrEmpty(r.ChecklistId) && checklists.CompleteChecklist(r.SetId ?? ActiveSet(), r.ChecklistId)
        ? Json(new { ok = true }) : Json(new { error = "Unknown checklist" }, 400));
app.MapPost("/efb/api/checklists/reset", (ChecklistResetReq r) =>
{
    var set = r.SetId ?? ActiveSet();
    if (string.IsNullOrEmpty(r.ChecklistId)) checklists.ResetAll(set); else checklists.ResetChecklist(set, r.ChecklistId);
    return Json(new { ok = true });
});

// ── manuals (the real library, read from AviatesAirTracker/Manuals) ──
app.MapGet("/efb/api/manuals", () => Json(new
{
    manuals = EfbManualLibrary.Manuals, documents = dev.Manuals.List(), recent = dev.Manuals.Recent(),
    pending = dev.Manuals.Pending, aircraftType = DevFlight.Type, aircraftSetId = ActiveSet(),
}));
app.MapGet("/efb/api/manuals/search", (string? q) => Json(EfbManualLibrary.Search(dev.Manuals.Documents, q ?? "")));
app.MapGet("/efb/api/manuals/{id}", (string id) =>
    dev.Manuals.Get(id) is { } d ? Json(new { document = d, status = dev.Manuals.Summary(id) }) : Results.NotFound());
app.MapPost("/efb/api/manuals/{id}/bookmark", (string id) => dev.Manuals.ToggleBookmark(id) ? Json(dev.Manuals.Summary(id)!) : Results.NotFound());
app.MapPost("/efb/api/manuals/{id}/ack", (string id) =>
    dev.Manuals.Acknowledge(id) ? Json(new { status = dev.Manuals.Summary(id), pending = dev.Manuals.Pending }) : Results.NotFound());

// ── landing reports (the real builder over a sample landing history) ──
app.MapGet("/efb/api/landings", () =>
{
    var all = dev.Landings;
    return Json(new
    {
        hasLanding = true,
        latest = LandingReportBuilder.Build(all[0], null, all, dev.Trace(all[0])),
        history = all.Select(l => LandingReportBuilder.Build(l, null, all, [])),
    });
});
app.MapGet("/efb/api/landings/{id}", (string id) =>
    dev.Landings.FirstOrDefault(l => l.Id.ToString() == id) is { } l
        ? Json(LandingReportBuilder.Build(l, null, dev.Landings, dev.Trace(l))) : Json(new { error = "Unknown landing" }, 404));

// ── weather, airports, ATIS, network (the real services and feeds) ──
app.MapGet("/efb/api/weather", async (string? icao) =>
{
    var r = await atis.GetAsync((icao ?? "").ToUpperInvariant());
    return Json(new { metar = r.Metar, taf = r.Taf });
});
app.MapGet("/efb/api/atis", async (string? icao) => Json(await atis.GetAsync((icao ?? "").ToUpperInvariant())));
app.MapGet("/efb/api/airport", async (string? icao) =>
{
    var id = (icao ?? "").Trim().ToUpperInvariant();
    if (id.Length != 4) return Json(new { error = "4-letter ICAO required" }, 400);
    return await Airport(id) is { } info ? Json(info) : Json(new { error = $"No runway data for {id}" }, 404);
});
app.MapGet("/efb/api/wx/station", async (string? icao) =>
{
    var id = (icao ?? "").Trim().ToUpperInvariant();
    if (id.Length != 4) return Json(new { error = "4-letter ICAO required" }, 400);
    var atisTask = atis.GetAsync(id); var aptTask = Airport(id);
    EfbNetworkTraffic? net = null; try { net = await network.GetAsync(); } catch { }
    await Task.WhenAll(atisTask, aptTask);
    var a = atisTask.Result;
    var here = (net?.Atcs ?? []).Where(x => string.Equals(x.AirportIcao, id, StringComparison.OrdinalIgnoreCase)
                                            || x.Callsign.StartsWith(id + "_", StringComparison.OrdinalIgnoreCase)).ToList();
    bool IsAtis(EfbNetworkAtc x) => x.Facility == "ATIS" || x.Callsign.Contains("ATIS", StringComparison.OrdinalIgnoreCase);
    var stations = here.Where(IsAtis).Select(x => new { network = x.Network, callsign = x.Callsign, letter = x.AtisCode, frequency = x.Frequency, text = x.Text }).ToList();
    if (stations.Count == 0 && a.Source == "VATSIM") stations.Add(new { network = "VATSIM", callsign = id + "_ATIS", letter = a.Letter, frequency = 0.0, text = a.Text });
    string[] order = ["DEL", "GND", "TWR", "DEP", "APP", "CTR", "FSS"];
    var atc = here.Where(x => !IsAtis(x)).OrderBy(x => { var i = Array.IndexOf(order, x.Facility); return i < 0 ? 99 : i; })
        .Select(x => new { network = x.Network, callsign = x.Callsign, facility = x.Facility, frequency = x.Frequency, name = x.SectorName }).ToList();
    return Json(new
    {
        icao = id, metar = a.Metar, taf = a.Taf, airport = aptTask.Result, atis = stations,
        generatedAtis = stations.Count == 0 && a.Source == "METAR" ? a.Text : "", atc, fetchedUtc = DateTime.UtcNow,
    });
});
app.MapGet("/efb/api/wx/sigmets", async () => Json(await mapWx.GetSigmetsAsync()));
app.MapGet("/efb/api/wx/metars", async (double minLat, double minLon, double maxLat, double maxLon) =>
    Json(await mapWx.GetMetarsAsync(minLat, minLon, maxLat, maxLon)));
app.MapGet("/efb/api/network", async () => Json(await network.GetAsync()));
app.MapGet("/efb/api/network/geometry", async (string? keys) =>
    Json(await network.GetGeometryAsync((keys ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))));
app.MapGet("/efb/api/traffic", () => Json(DevState.Traffic(flight)));

// ── messages ──
app.MapGet("/efb/api/messages", () => Json(dev.Messaging()));
app.MapPost("/efb/api/messages/send", (SendReq r) =>
{
    var text = (r.Content ?? "").Trim();
    if (text.Length == 0) return Json(new { ok = false, error = "Message not sent." }, 400);
    if (text.Contains("darn", StringComparison.OrdinalIgnoreCase))
        return Json(new { ok = false, error = "Your message contains inappropriate language and was not sent." }, 400);
    var m = dev.AddMessage(DevState.MeId, r.Broadcast ? "broadcast" : r.RecipientId ?? "", text, 0, r.Broadcast ? "Broadcast" : "Direct", synced: false);
    _ = PushMessages();
    _ = Task.Run(async () => { await Task.Delay(3000); m.Synced = true; await PushMessages(); });
    return Json(new { ok = true, error = (string?)null });
});
app.MapPost("/efb/api/messages/read", (ReadReq r) =>
{
    var ids = (r.Ids ?? []).Append(r.Id ?? "").ToHashSet();
    foreach (var m in dev.Messages) if (ids.Contains(m.Id)) m.IsRead = true;
    _ = PushMessages(); return Json(new { ok = true });
});
app.MapPost("/efb/api/friends/add", (CodeReq r) =>
{
    var code = (r.Code ?? "").Trim().ToUpperInvariant();
    if (code is not ("ABCD-EFGH" or "W3LL-D0NE")) return Json(new { error = "Could not add that friend code." }, 400);
    if (dev.Friends.All(f => f.PilotId != "p-nina"))
        dev.Friends.Add(new EfbFriend { PilotId = "p-nina", PilotName = "Nina Petrova", FriendCode = code, Rank = "Cadet" });
    _ = PushMessages(); return Json(new { ok = true, pilotId = "p-nina", name = "Nina Petrova" });
});
app.MapPost("/efb/api/friends/add-pilot", (PilotReq r) =>
{
    if (string.IsNullOrWhiteSpace(r.PilotId)) return Json(new { error = "That pilot can't be added." }, 400);
    if (dev.Friends.All(f => f.PilotId != r.PilotId))
        dev.Friends.Add(new EfbFriend { PilotId = r.PilotId, PilotName = r.PilotName ?? "Pilot", IsOnline = true });
    _ = PushMessages(); return Json(new { ok = true, pilotId = r.PilotId });
});
app.MapPost("/efb/api/friends/remove", (PilotReq r) => { dev.Friends.RemoveAll(f => f.PilotId == r.PilotId); _ = PushMessages(); return Json(new { ok = true }); });

// ── ACARS / CPDLC (the real wire formats; "ATC" and "dispatch" answer after a few seconds) ──
var ac = dev.Acars;
IResult Dl((bool ok, string? error, EfbAcarsMessage? m) r) =>
    Json(new { ok = r.ok, error = r.error, message = r.m, state = ac.State() }, r.ok ? 200 : 400);
app.MapGet("/efb/api/acars", () => Json(ac.State()));
app.MapPost("/efb/api/acars/poll", () => { ac.LastPoll = DateTime.UtcNow; return Json(ac.State()); });
app.MapPost("/efb/api/acars/read", (ReadReq r) => { ac.MarkRead(r.Ids ?? []); return Json(new { ok = true }); });
app.MapPost("/efb/api/acars/telex", (TelexReq r) => Dl(ac.Telex(r.To, r.Text)));
app.MapPost("/efb/api/acars/info", (InfoReq r) => Dl(ac.Info(r.Kind, r.Icao, flight)));
app.MapPost("/efb/api/acars/pdc", (PdcReq r) => Dl(ac.Telex(r.Station ?? r.Departure,
    EfbAcars.PdcRequest(DevFlight.Callsign, r.AircraftType ?? "ZZZZ", r.Destination ?? "", r.Departure ?? "", r.Stand, r.Atis), "PDC REQ")));
app.MapPost("/efb/api/acars/cpdlc/logon", (StationReq r) => Dl(ac.Logon(r.Station)));
app.MapPost("/efb/api/acars/cpdlc/logoff", () => Dl(ac.Logoff()));
app.MapPost("/efb/api/acars/cpdlc/request", (TextReq r) => Dl(ac.Request(r.Text)));
app.MapPost("/efb/api/acars/cpdlc/respond", (RespondReq r) => Dl(ac.Respond(r.Id, r.Response)));
app.MapGet("/efb/api/hoppies", () => Json(new { enabled = true, pilotCode = "", messages = Array.Empty<object>() }));

// ── dev only: switch the simulated flight and inject incoming traffic, to test flight mode and alerts ──
//   POST /efb/dev/scenario {"name":"full","speed":10}   (cruise | full | none; "full" restarts the 15-minute gate-to-gate flight, sped up 1–60×)
//   POST /efb/dev/incoming {"kind":"dm"}     (dm | crew | company | cpdlc)
app.MapPost("/efb/dev/scenario", (DevScenarioReq r) => { flight.SetScenario(r.Name ?? "", r.Speed ?? 1); return Json(new { ok = true, scenario = flight.Scenario }); });
app.MapPost("/efb/dev/incoming", (DevIncomingReq r) =>
{
    switch (r.Kind)
    {
        case "crew": dev.AddMessage("p-ben", "broadcast", "Anyone else seeing the long queue for 27R?", 0, "Broadcast", read: false); _ = PushMessages(); break;
        case "company": ac.Inject(new() { Station = "VAOPS", Kind = "COMPANY", Read = false, Text = "AVIATES OPS: ARRIVAL GATE E23. GROUND POWER AVAILABLE." }); break;
        case "cpdlc": ac.Inject(new() { Station = "LSAS", Type = "cpdlc", Kind = "CPDLC", Read = false, Min = 9, Ra = "WU", Open = true,
                          Text = "DESCEND TO REACH FL240 BY SUL", Responses = ["WILCO", "UNABLE", "STANDBY"] }); break;
        default: dev.AddMessage("p-sara", DevState.MeId, "Just landed in Zurich, how was your flight?", 0, read: false); _ = PushMessages(); break;
    }
    return Json(new { ok = true });
});

// ── WebSocket: the same envelopes as the app, plus the live flight once a second ──
app.Map("/efb/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var s = new DevSocket(ws, ctx.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "", DevSocket.NameFrom(ctx.Request.Headers.UserAgent.ToString()));
    sockets[s.Id] = s;
    await s.SendAsync(Env("snapshot", flight.Snapshot()));
    await s.SendAsync(Env("flightplan", flight.Plan));
    await s.SendAsync(Env("checklists", checklists.GetBundle(DevFlight.Type)));
    await s.SendAsync(Env("messages", dev.Messaging()));
    await s.SendAsync(Env("welcome", new { id = s.Id }));
    await s.SendAsync(Env("prefs", dev.Prefs));
    await s.SendAsync(Env("acars", ac.State()));
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
    var ticker = Task.Run(async () =>
    {
        while (!cts.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            await Task.Delay(1000, cts.Token).ContinueWith(_ => { });
            if (!await s.SendAsync(Env("snapshot", flight.Snapshot()))) break;
        }
    });
    var buf = new byte[1024];
    try { while (ws.State == WebSocketState.Open) { var r = await ws.ReceiveAsync(buf, ctx.RequestAborted); if (r.MessageType == WebSocketMessageType.Close) break; } }
    catch { }
    finally { cts.Cancel(); sockets.TryRemove(s.Id, out _); await s.CloseAsync(); }
});

Console.WriteLine($"AviatesAir EFB dev server on http://localhost:{port}/  (web: {webRoot})");
app.Run();

string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
static string FindRepo()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "AviatesAirTracker", "EfbWeb"))) return d.FullName;
    throw new DirectoryNotFoundException("Run the dev server from inside the AviatesAirTracker repository.");
}

// ── request bodies ──
record PrefsReq(string? WeightUnit);
record ThemeReq(string? Theme);
record ChecklistItemReq(string ChecklistId, string ItemId, bool Checked, string? SetId = null);
record ChecklistResetReq(string? ChecklistId, string? SetId = null);
record ChecklistSelectReq(string? SetId);
record SendReq(string? RecipientId, string? Content, bool Broadcast);
record ReadReq(string? Id, List<string>? Ids);
record CodeReq(string? Code);
record PilotReq(string? PilotId, string? PilotName);
record TelexReq(string? To, string? Text);
record InfoReq(string? Kind, string? Icao);
record PdcReq(string? Station, string? AircraftType, string? Destination, string? Departure, string? Stand, string? Atis);
record StationReq(string? Station);
record TextReq(string? Text);
record RespondReq(string? Id, string? Response);
record DevScenarioReq(string? Name, double? Speed);
record DevIncomingReq(string? Kind);
