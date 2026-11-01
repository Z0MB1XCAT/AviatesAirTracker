using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Core.Efb;
using AviatesAirTracker.Models;
using AviatesAirTracker.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AviatesAirTracker.Services.Efb;

// ============================================================
// EFB SERVER
//
// Embedded ASP.NET Core (Kestrel) web server hosted INSIDE the WPF
// process. It exposes the AviatesAir EFB to any device on the same
// LAN — tablet, phone, second monitor or another PC.
//
//   • Binds 0.0.0.0 on a random free port (or a configured one).
//   • Serves the tablet frontend from embedded resources.
//   • Streams live telemetry over a WebSocket at /efb/ws.
//   • Exposes a token-guarded REST API under /efb/api.
//   • Pairs devices with a stable device token + 6-digit code.
//
// Nothing here reaches into SimConnect directly — it consumes
// EfbStateService and friends, so it works with the live sim or the
// stub. Start/stop is controlled from Settings and App startup.
// ============================================================

public sealed class EfbServer : IAsyncDisposable, IDisposable
{
    private readonly EfbStateService _state;
    private readonly EfbChecklistService _checklists;
    private readonly EfbManualsService _manuals;
    private readonly FlightSessionManager _session;
    private readonly SimBriefService _simBrief;
    private readonly SettingsService _settings;
    private readonly MessagingService _messaging;
    private readonly OtherPilotsService _traffic;
    private readonly HoppiesAcarsOrchestrator _hoppies;
    private readonly EfbDatalinkService _datalink;
    private readonly EfbAtisService _atis;
    private readonly EfbNetworkTrafficService _network;
    private readonly EfbMapWeatherService _mapWx;
    private readonly ILandingRepository _landings;
    private readonly IFlightRepository _flights;

    private WebApplication? _app;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly ConcurrentDictionary<string, EfbAirportInfo> _airportCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, EfbSocket> _sockets = new();
    private readonly Dictionary<string, string> _assets;  // request path -> resource name

    // Custom EFB wallpaper, uploaded from a device and shared across all of them.
    private static readonly string WallpaperPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AviatesAirTracker", "efb_wallpaper.img");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    /// <summary>Raised when the server starts/stops so the Settings page can refresh.</summary>
    public event EventHandler? StatusChanged;

    public bool IsRunning { get; private set; }
    public int Port { get; private set; }
    public string DeviceToken { get; private set; } = "";
    public string PairingCode { get; private set; } = "";

    /// <summary>All reachable pairing URLs (one per LAN interface), token embedded in the hash.</summary>
    public IReadOnlyList<string> PairingUrls { get; private set; } = [];
    /// <summary>The best single URL to show the pilot (first LAN IPv4).</summary>
    public string PrimaryUrl => PairingUrls.FirstOrDefault() ?? "";

    public EfbServer(
        EfbStateService state,
        EfbChecklistService checklists,
        EfbManualsService manuals,
        FlightSessionManager session,
        SimBriefService simBrief,
        SettingsService settings,
        MessagingService messaging,
        OtherPilotsService traffic,
        HoppiesAcarsOrchestrator hoppies,
        EfbDatalinkService datalink,
        EfbAtisService atis,
        EfbNetworkTrafficService network,
        EfbMapWeatherService mapWx,
        ILandingRepository landings,
        IFlightRepository flights)
    {
        _hoppies = hoppies;
        _datalink = datalink;
        _atis = atis;
        _network = network;
        _mapWx = mapWx;
        _landings = landings;
        _flights = flights;
        _state = state;
        _checklists = checklists;
        _manuals = manuals;
        _session = session;
        _simBrief = simBrief;
        _settings = settings;
        _messaging = messaging;
        _traffic = traffic;
        _assets = MapEmbeddedAssets();
    }

    // ════════════════════════════════════════════════════════
    // LIFECYCLE
    // ════════════════════════════════════════════════════════

    public async Task StartAsync()
    {
        if (IsRunning) return;

        EnsurePairingSecret();
        int desiredPort = _settings.Settings.EfbPort > 0 ? _settings.Settings.EfbPort : 0;

        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();                       // keep Kestrel quiet — we log via Serilog
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.UseUrls($"http://0.0.0.0:{desiredPort}");
            builder.Services.ConfigureHttpJsonOptions(o =>
            {
                o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                o.SerializerOptions.PropertyNameCaseInsensitive = true;
            });

            _app = builder.Build();
            ConfigurePipeline(_app);

            await _app.StartAsync();

            Port = ResolveBoundPort(_app);
            PairingUrls = BuildPairingUrls(Port, DeviceToken);
            IsRunning = true;

            Log.Information("[EFB] Server started on port {Port} — pair at {Url} (code {Code})",
                Port, PrimaryUrl, PairingCode);
            _session.SessionStateChanged += OnSessionResetChecklists;
            _checklists.Changed += OnChecklistsChanged;
            _state.SnapshotUpdated += OnSnapshotUpdated;
            _messaging.NewMessageReceived += OnNewMessage;
            _datalink.Changed += OnDatalinkChanged;
            _datalink.Start(() => !_sockets.IsEmpty);   // peek the pilot's inbox only while a device is watching
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[EFB] Failed to start server");
            IsRunning = false;
            await SafeStopAsync();
        }
    }

    public async Task StopAsync()
    {
        if (!IsRunning && _app == null) return;
        _session.SessionStateChanged -= OnSessionResetChecklists;
        _checklists.Changed -= OnChecklistsChanged;
        _state.SnapshotUpdated -= OnSnapshotUpdated;
        _messaging.NewMessageReceived -= OnNewMessage;
        _datalink.Changed -= OnDatalinkChanged;
        await SafeStopAsync();
        IsRunning = false;
        Log.Information("[EFB] Server stopped");
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task SafeStopAsync()
    {
        foreach (var s in _sockets.Values) await s.CloseAsync();
        _sockets.Clear();
        if (_app != null)
        {
            try { await _app.StopAsync(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
            try { await _app.DisposeAsync(); } catch { }
            _app = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _http.Dispose();
    }

    /// <summary>
    /// Synchronous dispose so the DI container (which calls Dispose(), not DisposeAsync,
    /// during a WPF shutdown) can tear the server down without throwing on an
    /// async-only disposable. Blocks briefly on graceful stop.
    /// </summary>
    /// <remarks>
    /// The stop MUST run through <see cref="Task.Run(Func{Task})"/>, not be awaited inline.
    /// This is called from <c>App.OnExit</c> on the WPF dispatcher thread, where
    /// <c>SynchronizationContext.Current</c> is the <c>DispatcherSynchronizationContext</c>.
    /// Awaiting <see cref="StopAsync"/> directly captures that context, so its continuations
    /// are posted back to the dispatcher thread — the very thread this call has just blocked.
    /// The continuation never runs and the process hangs in <c>OnExit</c> forever.
    ///
    /// That hang looked like a clean stop from the outside: Kestrel unbinds the listen socket
    /// on a thread-pool thread before the first continuation is due, so the EFB port is
    /// released and only the deadlocked dispatcher thread is left behind.
    ///
    /// Task.Run hands the whole state machine to a thread-pool thread with no captured
    /// context, so nothing needs the blocked thread to make progress. The timeout is a
    /// backstop for a socket close that refuses to finish; abandoning the stop is safe
    /// because the process is on its way out.
    /// </remarks>
    public void Dispose()
    {
        try
        {
            if (!Task.Run(StopAsync).Wait(TimeSpan.FromSeconds(5)))
                Log.Warning("[EFB] Server stop did not finish within 5s — abandoning it");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[EFB] Server stop failed during Dispose (non-critical)");
        }
        _http.Dispose();
    }

    // ════════════════════════════════════════════════════════
    // PIPELINE
    // ════════════════════════════════════════════════════════

    private void ConfigurePipeline(WebApplication app)
    {
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        // Auth guard for the API + WS (everything else — static assets — is open so
        // the browser can load the app shell; the token is required to pull data).
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "/";
            bool guarded = path.StartsWith("/efb/ws") ||
                           (path.StartsWith("/efb/api") &&
                            !path.StartsWith("/efb/api/hello") &&
                            !path.StartsWith("/efb/api/pair"));
            if (guarded && !IsAuthorized(ctx))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsync("EFB token required");
                return;
            }
            await next();
        });

        MapApi(app);
        MapWebSocket(app);

        // Static app shell from embedded resources — registered as the lowest-priority
        // fallback ENDPOINT, NOT a terminal app.Run(). A terminal delegate here would
        // short-circuit before the endpoint middleware (which runs at the very end of the
        // WebApplication pipeline), shadowing every /efb/api and /efb/ws route and serving
        // index.html for API calls. The explicit "{*path}" pattern is required — the
        // parameterless MapFallback uses a :nonfile constraint that would refuse
        // efb.css / app.js / favicon.svg.
        app.MapFallback("{*path}", (RequestDelegate)ServeStaticAsync);
    }

    // Live rebroadcast handlers (subscribed on Start, unsubscribed on Stop).
    private void OnChecklistsChanged(object? s, EventArgs e) => _ = BroadcastChecklistsAsync();
    private string? _lastChecklistSetId;
    private void OnSnapshotUpdated(object? s, EfbSnapshot snap)
    {
        _ = BroadcastSnapshotAsync(snap);
        // Aircraft type changed (booking / OFP loaded) → devices need the other checklist set.
        var set = _checklists.ActiveSet(string.IsNullOrWhiteSpace(snap.AircraftType) ? _state.GetFlightPlan().AircraftType : snap.AircraftType).Id;
        if (_lastChecklistSetId != null && set != _lastChecklistSetId) _ = BroadcastChecklistsAsync();
        _lastChecklistSetId = set;
    }
    private void OnNewMessage(object? s, PilotMessage m) => _ = BroadcastMessagingAsync();
    private void OnDatalinkChanged(object? s, EventArgs e) => _ = BroadcastAsync(Envelope("acars", _datalink.GetState()));
    private object Prefs() => new { weightUnit = _settings.Settings.EfbWeightUnit == "LB" ? "LB" : "KG" };

    // ── REST API ────────────────────────────────────────────

    private void MapApi(WebApplication app)
    {
        app.MapGet("/efb/api/hello", (HttpContext ctx) => Results.Json(new EfbServerInfo
        {
            Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "",
            PilotName = _settings.Settings.PilotName,
            TokenValid = IsAuthorized(ctx),
        }, Json));

        app.MapPost("/efb/api/pair", (PairRequest req) =>
        {
            if (!string.IsNullOrEmpty(req.Code) &&
                string.Equals(req.Code.Trim(), PairingCode, StringComparison.Ordinal))
                return Results.Json(new { token = DeviceToken }, Json);
            return Results.Json(new { error = "Invalid pairing code" }, Json, statusCode: 401);
        });

        app.MapGet("/efb/api/state", () => Results.Json(_state.GetLiveSnapshot(), Json));
        app.MapGet("/efb/api/flightplan", () => Results.Json(_state.GetFlightPlan(), Json));

        app.MapGet("/efb/api/aircraft", () => Results.Json(
            AircraftPerformanceProfiles.All.Select(p => new
            {
                p.Code, p.Name, p.Category, p.MtowLbs, p.MlwLbs, p.OewLbs, p.Aliases, p.Fleet,
                // only the detents company policy permits (CN-002 / CN-003)
                takeoffFlaps = EfbCompanyPolicy.TakeoffFlaps(p).Select(f => f.Label),
                landingFlaps = EfbCompanyPolicy.LandingFlaps(p).Select(f => f.Label),
                standardLandingFlap = EfbCompanyPolicy.StandardLandingFlap(p),
                companyNotices = EfbCompanyPolicy.Notices(p.Code),
            }), Json));

        app.MapPost("/efb/api/perf/takeoff",
            (TakeoffPerfRequest req) => Results.Json(EfbPerformanceCalculator.CalculateTakeoff(req), Json));
        app.MapPost("/efb/api/perf/landing",
            (LandingPerfRequest req) => Results.Json(EfbPerformanceCalculator.CalculateLanding(req), Json));
        // ?type= narrows it to that aircraft family's QRH failures (Airbus G/Y/B, 737 A/B, 777 L/C/R…)
        app.MapGet("/efb/api/perf/landing/failures", (string? type) => Results.Json(new
        {
            family = EfbLandingFailures.FamilyOf(type),
            categories = string.IsNullOrWhiteSpace(type) ? EfbLandingFailures.Categories : EfbLandingFailures.CategoriesFor(type),
            failures = string.IsNullOrWhiteSpace(type) ? EfbLandingFailures.All : EfbLandingFailures.ForAircraft(type),
        }, Json));

        // Checklists: the active per-aircraft set (auto from the flight's type, or the pilot's pick).
        app.MapGet("/efb/api/checklists", (string? type) =>
            Results.Json(_checklists.GetBundle(string.IsNullOrWhiteSpace(type) ? ChecklistAircraftType() : type), Json));
        app.MapPost("/efb/api/checklists/select", (ChecklistSelectRequest req) =>
            _checklists.SelectSet(req.SetId)
                ? Results.Json(_checklists.GetBundle(ChecklistAircraftType()), Json)
                : Results.Json(new { error = "Unknown checklist set" }, Json, statusCode: 400));
        app.MapPost("/efb/api/checklists/item", (ChecklistItemRequest req) =>
            _checklists.SetItem(req.SetId ?? ActiveChecklistSetId(), req.ChecklistId, req.ItemId, req.Checked)
                ? Results.Json(new { ok = true }, Json)
                : Results.Json(new { error = "Unknown checklist item" }, Json, statusCode: 400));
        app.MapPost("/efb/api/checklists/complete", (ChecklistResetRequest req) =>
            !string.IsNullOrEmpty(req.ChecklistId) && _checklists.CompleteChecklist(req.SetId ?? ActiveChecklistSetId(), req.ChecklistId)
                ? Results.Json(new { ok = true }, Json)
                : Results.Json(new { error = "Unknown checklist" }, Json, statusCode: 400));
        app.MapPost("/efb/api/checklists/reset", (ChecklistResetRequest req) =>
        {
            var set = req.SetId ?? ActiveChecklistSetId();
            if (string.IsNullOrEmpty(req.ChecklistId)) _checklists.ResetAll(set);
            else _checklists.ResetChecklist(set, req.ChecklistId);
            return Results.Json(new { ok = true }, Json);
        });

        // Document library (Manuals app): the manual index + every document's status, the
        // current aircraft's checklist-set id (so type-specific OM-B / QRH pages can be pinned),
        // and the count of Company NOTAMs still awaiting acknowledgement (home-screen badge).
        app.MapGet("/efb/api/manuals", () => Results.Json(new
        {
            manuals = _manuals.Manuals,
            documents = _manuals.List(),
            recent = _manuals.Recent(),
            pending = _manuals.PendingAcknowledgements,
            aircraftType = ChecklistAircraftType(),
            aircraftSetId = EfbChecklistLibrary.Resolve(ChecklistAircraftType()).Id,
        }, Json));
        app.MapGet("/efb/api/manuals/search", (string? q) => Results.Json(_manuals.Search(q ?? ""), Json));
        app.MapGet("/efb/api/manuals/{id}", (string id) =>
        {
            var doc = _manuals.Get(id);
            return doc == null ? Results.NotFound() : Results.Json(new { document = doc, status = _manuals.Summary(id) }, Json);
        });
        app.MapPost("/efb/api/manuals/{id}/bookmark", (string id) =>
            _manuals.ToggleBookmark(id) ? Results.Json(_manuals.Summary(id), Json) : Results.NotFound());
        app.MapPost("/efb/api/manuals/{id}/ack", (string id) =>
            _manuals.Acknowledge(id)
                ? Results.Json(new { status = _manuals.Summary(id), pending = _manuals.PendingAcknowledgements }, Json)
                : Results.Json(new { error = "Document does not take acknowledgement" }, Json, statusCode: 400));

        app.MapGet("/efb/api/weather", async (string? icao) =>
        {
            if (string.IsNullOrWhiteSpace(icao)) return Results.Json(new { error = "icao required" }, Json, statusCode: 400);
            var (metar, taf) = await FetchWeatherAsync(icao.Trim().ToUpperInvariant());
            return Results.Json(new { icao = icao.Trim().ToUpperInvariant(), metar, taf }, Json);
        });

        // Runway picker for Departure Perf (aviationweather.gov airport data, cached per ICAO
        // for the session — runways don't move; a failed lookup isn't cached so it can retry).
        app.MapGet("/efb/api/airport", async (string? icao) =>
        {
            var id = (icao ?? "").Trim().ToUpperInvariant();
            if (id.Length != 4 || !id.All(char.IsLetterOrDigit))
                return Results.Json(new { error = "4-letter ICAO required" }, Json, statusCode: 400);
            var info = await GetAirportAsync(id);
            return info == null
                ? Results.Json(new { error = $"No runway data for {id}" }, Json, statusCode: 404)
                : Results.Json(info, Json);
        });

        // Weather app: everything about one station in a single round trip: METAR + TAF, the
        // airport (runways for the wind table), every online ATIS on VATSIM and IVAO (separate
        // arrival / departure ATIS included) with letter and frequency, the airport's online
        // controllers, and a METAR-generated ATIS when no ATIS is online.
        app.MapGet("/efb/api/wx/station", async (string? icao) =>
        {
            var id = (icao ?? "").Trim().ToUpperInvariant();
            if (id.Length != 4 || !id.All(char.IsLetterOrDigit))
                return Results.Json(new { error = "4-letter ICAO required" }, Json, statusCode: 400);
            var atisTask = _atis.GetAsync(id);
            var aptTask = GetAirportAsync(id);
            var netTask = SafeNetworkAsync();
            await Task.WhenAll(atisTask, aptTask, netTask);
            var atis = atisTask.Result;
            var here = (netTask.Result?.Atcs ?? [])
                .Where(a => string.Equals(a.AirportIcao, id, StringComparison.OrdinalIgnoreCase)
                         || a.Callsign.StartsWith(id + "_", StringComparison.OrdinalIgnoreCase))
                .ToList();
            bool IsAtis(EfbNetworkAtc a) => a.Facility == "ATIS" || a.Callsign.Contains("ATIS", StringComparison.OrdinalIgnoreCase);
            var stations = here.Where(IsAtis).Select(a => new
            {
                network = a.Network, callsign = a.Callsign, letter = a.AtisCode, frequency = a.Frequency, text = a.Text,
            }).ToList();
            if (stations.Count == 0 && atis.Source == "VATSIM")           // datafeed seen by the ATIS service only
                stations.Add(new { network = "VATSIM", callsign = id + "_ATIS", letter = atis.Letter, frequency = 0.0, text = atis.Text });
            string[] order = ["DEL", "GND", "TWR", "DEP", "APP", "CTR", "FSS"];
            var controllers = here.Where(a => !IsAtis(a))
                .OrderBy(a => { var i = Array.IndexOf(order, a.Facility); return i < 0 ? 99 : i; })
                .Select(a => new { network = a.Network, callsign = a.Callsign, facility = a.Facility, frequency = a.Frequency, name = a.SectorName })
                .ToList();
            return Results.Json(new
            {
                icao = id,
                metar = atis.Metar,
                taf = atis.Taf,
                airport = aptTask.Result,
                atis = stations,
                generatedAtis = stations.Count == 0 && atis.Source == "METAR" ? atis.Text : "",
                atc = controllers,
                fetchedUtc = DateTime.UtcNow,
            }, Json);
        });

        // ── Messaging (the SAME system as the desktop app) ──────────────
        app.MapGet("/efb/api/messages", async () => Results.Json(await BuildMessagingStateAsync(), Json));
        app.MapPost("/efb/api/messages/send", async (SendDmRequest req) =>
        {
            bool ok = req.Broadcast
                ? await _messaging.SendBroadcastAsync(req.Content)
                : await _messaging.SendDirectAsync(req.RecipientId ?? "", req.Content);
            if (ok)
            {
                _ = BroadcastMessagingAsync();
                // the backend upload runs in the background; push again once it has had time to
                // land so the tablet's "queued" clock turns into a sent tick without a reload
                _ = Task.Run(async () => { await Task.Delay(3000); await BroadcastMessagingAsync(); });
            }
            return Results.Json(new { ok, error = ok ? null : (_messaging.LastSendError ?? "Message not sent.") }, Json,
                statusCode: ok ? 200 : 400);
        });
        // {id} for one message, or {ids:[...]} to clear a whole conversation in one round trip
        app.MapPost("/efb/api/messages/read", async (MessageIdRequest req) =>
        {
            var ids = (req.Ids ?? []).Append(req.Id ?? "").Distinct();
            foreach (var s in ids)
                if (Guid.TryParse(s, out var id)) await _messaging.MarkReadAsync(id);
            _ = BroadcastMessagingAsync();
            return Results.Json(new { ok = true }, Json);
        });
        app.MapPost("/efb/api/friends/add", async (FriendCodeRequest req) =>
        {
            var f = await _messaging.AddFriendByCodeAsync(req.Code ?? "");
            if (f != null) _ = BroadcastMessagingAsync();
            return f == null
                ? Results.Json(new { error = "Could not add that friend code." }, Json, statusCode: 400)
                : Results.Json(new { ok = true, pilotId = f.PilotId, name = f.PilotName }, Json);
        });
        // Start a conversation with an AviatesAir pilot seen in live traffic (no friend code needed,
        // same as the desktop live map's "Message" action)
        app.MapPost("/efb/api/friends/add-pilot", async (AddPilotRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.PilotId) || req.PilotId == _messaging.MyPilotId)
                return Results.Json(new { error = "That pilot can't be added." }, Json, statusCode: 400);
            var f = await _messaging.AddOrGetFriendFromLiveMapAsync(req.PilotId, req.PilotName ?? "Pilot");
            _ = BroadcastMessagingAsync();
            return Results.Json(new { ok = true, pilotId = f.PilotId, name = f.PilotName }, Json);
        });
        app.MapPost("/efb/api/friends/remove", async (AddPilotRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.PilotId))
                return Results.Json(new { error = "No pilot given." }, Json, statusCode: 400);
            await _messaging.RemoveFriendAsync(req.PilotId);
            _ = BroadcastMessagingAsync();
            return Results.Json(new { ok = true }, Json);
        });

        // ── Landing reports (durable — read from the persisted landing history,
        //     so a landing is viewable long after the live modal has gone) ──
        app.MapGet("/efb/api/landings", async () =>
        {
            var all = await _landings.GetAllAsync();
            if (all.Count == 0) return Results.Json(new { hasLanding = false }, Json);

            var flights = await _flights.GetAllAsync();
            FlightRecord? FlightFor(LandingResult l) =>
                flights.FirstOrDefault(f => f.Id.ToString() == l.FlightId);

            // The history list is the picker (no traces); `latest` is the full flight-data report.
            var ordered = all.OrderByDescending(l => l.Timestamp).ToList();
            return Results.Json(new
            {
                hasLanding = true,
                latest = LandingReportBuilder.Build(ordered[0], FlightFor(ordered[0]), all, await _landings.GetTraceAsync(ordered[0].Id)),
                history = ordered.Take(25).Select(l => LandingReportBuilder.Build(l, FlightFor(l), all, [])),
            }, Json);
        });
        app.MapGet("/efb/api/landings/{id}", async (string id) =>
        {
            var all = await _landings.GetAllAsync();
            var l = all.FirstOrDefault(x => x.Id.ToString() == id);
            if (l == null) return Results.Json(new { error = "Unknown landing" }, Json, statusCode: 404);
            var flights = await _flights.GetAllAsync();
            return Results.Json(LandingReportBuilder.Build(l, flights.FirstOrDefault(f => f.Id.ToString() == l.FlightId),
                all, await _landings.GetTraceAsync(l.Id)), Json);
        });

        // ── Hoppies ACARS log (session history from the orchestrator) ──
        app.MapGet("/efb/api/hoppies", () => Results.Json(new
        {
            enabled = _settings.Settings.HoppiesEnabled,
            pilotCode = _settings.Settings.HoppiesPilotCode,
            messages = _hoppies.MessageLog
                .OrderByDescending(m => m.SentAtUtc)
                .Select(m => new { m.SentAtUtc, m.Category, m.Message, m.Success }),
        }, Json));

        // ── Datalink: the ACARS app's terminal (pilot's own Hoppie logon) ──
        app.MapGet("/efb/api/acars", () => Results.Json(_datalink.GetState(), Json));
        app.MapPost("/efb/api/acars/poll", async () =>
        {
            await _datalink.PollAsync();
            return Results.Json(_datalink.GetState(), Json);
        });
        app.MapPost("/efb/api/acars/telex", async (AcarsTelexRequest r) =>
            Datalink(await _datalink.TelexAsync(r.To, r.Text,
                string.Equals(r.To?.Trim(), "VAOPS", StringComparison.OrdinalIgnoreCase) ? "COMPANY" : "TELEX")));
        app.MapPost("/efb/api/acars/info", async (AcarsInfoRequest r) => Datalink(await _datalink.InfoAsync(r.Kind, r.Icao)));
        app.MapPost("/efb/api/acars/pdc", async (AcarsPdcRequest r) =>
            Datalink(await _datalink.PdcAsync(r.Station, r.AircraftType, r.Destination, r.Departure, r.Stand, r.Atis)));
        app.MapPost("/efb/api/acars/cpdlc/logon", async (AcarsStationRequest r) => Datalink(await _datalink.CpdlcLogonAsync(r.Station)));
        app.MapPost("/efb/api/acars/cpdlc/logoff", async () => Datalink(await _datalink.CpdlcLogoffAsync()));
        app.MapPost("/efb/api/acars/cpdlc/request", async (AcarsTextRequest r) => Datalink(await _datalink.CpdlcRequestAsync(r.Text)));
        app.MapPost("/efb/api/acars/cpdlc/respond", async (AcarsRespondRequest r) => Datalink(await _datalink.CpdlcRespondAsync(r.Id, r.Response)));
        app.MapPost("/efb/api/acars/read", (AcarsReadRequest r) =>
        {
            _datalink.MarkRead(r.Ids ?? []);
            return Results.Json(new { ok = true }, Json);
        });

        // ── ATIS (VATSIM when online, else generated from METAR) ───────
        app.MapGet("/efb/api/atis", async (string? icao) =>
        {
            if (string.IsNullOrWhiteSpace(icao))
                return Results.Json(new { error = "icao required" }, Json, statusCode: 400);
            return Results.Json(await _atis.GetAsync(icao), Json);
        });

        // ── Live traffic (mirrors the app's live map) ───────────────────
        app.MapGet("/efb/api/traffic", () => Results.Json(
            _traffic.LatestPositions.Select(p => new EfbTraffic
            {
                PilotId = p.PilotId, PilotName = p.PilotName, Callsign = p.Callsign ?? "",
                Lat = p.Lat, Lon = p.Lon, Alt = p.Alt, Speed = p.Speed, Heading = p.Heading,
                Phase = p.Phase, DepartureIcao = p.DepartureIcao ?? "", ArrivalIcao = p.ArrivalIcao ?? "",
            }), Json));

        // ── Online network traffic/ATC (VATSIM + IVAO map layers) ──────
        app.MapGet("/efb/api/network", async () => Results.Json(await _network.GetAsync(), Json));
        // Sector outlines for staffed FIRs/TRACONs (static; the tablet caches them)
        app.MapGet("/efb/api/network/geometry", async (string? keys) => Results.Json(
            await _network.GetGeometryAsync((keys ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)), Json));

        // ── Map weather overlays (aviationweather.gov, cached 5 min) ──
        app.MapGet("/efb/api/wx/sigmets", async () => Results.Json(await _mapWx.GetSigmetsAsync(), Json));
        app.MapGet("/efb/api/wx/metars", async (double minLat, double minLon, double maxLat, double maxLon) =>
            Results.Json(await _mapWx.GetMetarsAsync(minLat, minLon, maxLat, maxLon), Json));

        // ── SimBrief OFP: import inside the EFB (does NOT touch ACARS) + PDF link ──
        app.MapPost("/efb/api/flightplan/import", async (SimbriefImportRequest req) =>
        {
            var user = string.IsNullOrWhiteSpace(req.Username)
                ? _settings.Settings.SimBriefUsername : req.Username.Trim();
            if (string.IsNullOrWhiteSpace(user))
                return Results.Json(new { error = "SimBrief username or pilot ID required." }, Json, statusCode: 400);
            try
            {
                var plan = await _simBrief.FetchLatestOFPAsync(user);
                if (plan == null)
                    return Results.Json(new { error = "No OFP found for that SimBrief account." }, Json, statusCode: 404);
                _state.SetEfbLocalPlan(plan);   // EFB-only — never pushed to ACARS
                // every paired device picks up the new plan, and the datalink its callsign
                _ = BroadcastAsync(Envelope("flightplan", _state.GetFlightPlan()));
                _ = BroadcastAsync(Envelope("acars", _datalink.GetState()));
                return Results.Json(_state.GetFlightPlan(), Json);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[EFB] SimBrief import failed");
                return Results.Json(new { error = "SimBrief import failed." }, Json, statusCode: 502);
            }
        });
        // Full briefing package for the Pilot Brief app (still carries hasPdf/pdfUrl).
        app.MapGet("/efb/api/ofp", () => Results.Json(_state.GetBriefing(), Json));

        // ── Wallpaper (uploaded from a device, shared across all) ───────
        app.MapGet("/efb/api/wallpaper", () =>
        {
            if (!File.Exists(WallpaperPath)) return Results.NoContent();
            var bytes = File.ReadAllBytes(WallpaperPath);
            return Results.File(bytes, DetectImageType(bytes));
        });
        app.MapPost("/efb/api/wallpaper", async (HttpContext ctx) =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            var bytes = ms.ToArray();
            if (bytes.Length == 0 || bytes.Length > 12_000_000)
                return Results.Json(new { error = "Image missing or too large (max 12 MB)." }, Json, statusCode: 400);
            Directory.CreateDirectory(Path.GetDirectoryName(WallpaperPath)!);
            await File.WriteAllBytesAsync(WallpaperPath, bytes);
            return Results.Json(new { ok = true }, Json);
        });
        app.MapDelete("/efb/api/wallpaper", () =>
        {
            if (File.Exists(WallpaperPath)) File.Delete(WallpaperPath);
            return Results.Json(new { ok = true }, Json);
        });

        app.MapGet("/efb/api/theme", () => Results.Json(new { theme = _settings.Settings.EfbTheme }, Json));

        // Shared EFB preferences (one weight unit for every app and device)
        app.MapGet("/efb/api/prefs", () => Results.Json(Prefs(), Json));
        app.MapPost("/efb/api/prefs", (PrefsRequest req) =>
        {
            if (req.WeightUnit is "KG" or "LB")
            {
                _settings.Settings.EfbWeightUnit = req.WeightUnit;
                _settings.Save();
                _ = BroadcastAsync(Envelope("prefs", Prefs()));
            }
            return Results.Json(Prefs(), Json);
        });

        // Paired devices: whoever is connected now. There's one shared pairing token, so pairing
        // can only be undone for everyone at once, by rotating it.
        app.MapGet("/efb/api/devices", () => Results.Json(new
        {
            pairingCode = PairingCode,
            url = PrimaryUrl.Split('#')[0],
            devices = _sockets.Values.OrderBy(d => d.ConnectedUtc).Select(d => new
            {
                id = d.Id, name = d.DeviceName, ip = d.RemoteIp, connectedUtc = d.ConnectedUtc,
            }),
        }, Json));
        app.MapPost("/efb/api/pairing/reset", async () =>
        {
            _settings.Settings.EfbPairingSecret = "";
            EnsurePairingSecret();                      // new token + new 6-digit code
            PairingUrls = BuildPairingUrls(Port, DeviceToken);
            Log.Information("[EFB] Pairing reset from a device; every device must pair again (new code {Code})", PairingCode);
            var sockets = _sockets.Values.ToList();
            _sockets.Clear();
            foreach (var d in sockets) await d.CloseAsync();
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return Results.Json(new { ok = true }, Json);
        });
        app.MapPost("/efb/api/theme", (ThemeRequest req) =>
        {
            _settings.Settings.EfbTheme = string.IsNullOrWhiteSpace(req.Theme) ? "aviates" : req.Theme.Trim();
            _settings.Save();
            return Results.Json(new { ok = true, theme = _settings.Settings.EfbTheme }, Json);
        });

        // Control action — end the flight from the tablet (mirrors the desktop button).
        app.MapPost("/efb/api/flight/end", () =>
        {
            _session.EndFlightManually();
            return Results.Json(new { ok = true }, Json);
        });
    }

    // ── WebSocket ───────────────────────────────────────────

    private void MapWebSocket(WebApplication app)
    {
        app.Map("/efb/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var socket = new EfbSocket(ws)
            {
                RemoteIp = ctx.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "",
                DeviceName = EfbSocket.NameFromUserAgent(ctx.Request.Headers.UserAgent.ToString()),
            };
            _sockets[socket.Id] = socket;
            Log.Debug("[EFB] Device connected ({Count} total)", _sockets.Count);

            // Prime the new device with the current picture.
            await socket.SendAsync(Envelope("snapshot", _state.GetLiveSnapshot()));
            await socket.SendAsync(Envelope("flightplan", _state.GetFlightPlan()));
            await socket.SendAsync(Envelope("checklists", _checklists.GetBundle(ChecklistAircraftType())));
            await socket.SendAsync(Envelope("messages", await BuildMessagingStateAsync()));
            await socket.SendAsync(Envelope("welcome", new { id = socket.Id }));
            await socket.SendAsync(Envelope("prefs", Prefs()));
            await socket.SendAsync(Envelope("acars", _datalink.GetState()));

            // Read loop — we don't expect inbound frames (control is via REST),
            // but reading keeps the socket alive and detects disconnects.
            var buffer = new byte[1024];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(buffer, ctx.RequestAborted);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch { /* client vanished */ }
            finally
            {
                _sockets.TryRemove(socket.Id, out _);
                await socket.CloseAsync();
                Log.Debug("[EFB] Device disconnected ({Count} total)", _sockets.Count);
            }
        });
    }

    private Task BroadcastSnapshotAsync(EfbSnapshot snap) => BroadcastAsync(Envelope("snapshot", snap));
    private Task BroadcastChecklistsAsync() => BroadcastAsync(Envelope("checklists", _checklists.GetBundle(ChecklistAircraftType())));

    // The aircraft type the checklist set follows: booking → SimBrief → sim (as folded into the
    // snapshot), falling back to the effective flight plan before a session exists.
    private string ChecklistAircraftType()
    {
        var t = _state.GetLiveSnapshot().AircraftType;
        return string.IsNullOrWhiteSpace(t) ? _state.GetFlightPlan().AircraftType : t;
    }
    private string ActiveChecklistSetId() => _checklists.ActiveSet(ChecklistAircraftType()).Id;
    private async Task BroadcastMessagingAsync() => await BroadcastAsync(Envelope("messages", await BuildMessagingStateAsync()));

    // ── Messaging state projection (reuses the desktop MessagingService) ──
    private async Task<EfbMessagingState> BuildMessagingStateAsync()
    {
        var myId = _messaging.MyPilotId;
        var friends = await _messaging.GetFriendsAsync();
        var inbox = await _messaging.GetInboxAsync();
        var broadcasts = await _messaging.GetBroadcastsAsync();

        var msgs = inbox.Concat(broadcasts)
            .OrderBy(m => m.SentAt)
            .Select(m => new EfbMessageDto
            {
                Id = m.Id.ToString(),
                SenderId = m.SenderId,
                SenderName = m.SenderName,
                RecipientId = m.RecipientId,
                Content = m.Content,
                SentAt = m.SentAt,
                IsRead = m.ReadAt != null,
                Mine = m.SenderId == myId,
                Synced = m.SyncedToBackend,
                Type = m.Type.ToString(),
            }).ToList();

        return new EfbMessagingState
        {
            MyPilotId = myId,
            MyPilotName = _settings.Settings.PilotName,
            MyFriendCode = _settings.Settings.FriendCode,
            HasKey = !string.IsNullOrEmpty(_settings.Settings.AcarsKey),
            UnreadCount = _messaging.UnreadCount,
            Friends = friends.Select(f => new EfbFriend
            {
                PilotId = f.PilotId, PilotName = f.PilotName, FriendCode = f.FriendCode,
                Rank = f.Rank, IsOnline = f.IsOnline,
            }).ToList(),
            Messages = msgs,
        };
    }

    private static string DetectImageType(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8) return "image/jpeg";
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50) return "image/png";
        if (b.Length >= 12 && b[8] == 0x57 && b[9] == 0x45) return "image/webp"; // "WE"BP
        if (b.Length >= 6 && b[0] == 0x47 && b[1] == 0x49) return "image/gif";
        return "application/octet-stream";
    }

    private async Task BroadcastAsync(string payload)
    {
        if (_sockets.IsEmpty) return;
        foreach (var socket in _sockets.Values)
        {
            if (!await socket.SendAsync(payload))
                _sockets.TryRemove(socket.Id, out _);
        }
    }

    private static string Envelope(string type, object data) =>
        JsonSerializer.Serialize(new { type, data }, Json);

    // ── Static assets (embedded) ────────────────────────────

    private async Task ServeStaticAsync(HttpContext ctx)
    {
        var path = (ctx.Request.Path.Value ?? "/").TrimStart('/');
        if (string.IsNullOrEmpty(path)) path = "index.html";

        if (!_assets.TryGetValue(path, out var resource))
        {
            // SPA fallback — unknown non-file path serves the shell.
            if (!path.Contains('.') && _assets.TryGetValue("index.html", out var shell))
                resource = shell;
            else
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }

        var asm = Assembly.GetExecutingAssembly();
        await using var stream = asm.GetManifestResourceStream(resource);
        if (stream == null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        ctx.Response.ContentType = ContentType(path);
        ctx.Response.Headers.CacheControl = "no-cache";
        await stream.CopyToAsync(ctx.Response.Body);
    }

    private static Dictionary<string, string> MapEmbeddedAssets()
    {
        const string prefix = "AviatesAirTracker.EfbWeb.";
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                map[name[prefix.Length..]] = name;   // flat files → "efb.css", "app.js", …
        return map;
    }

    private static string ContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".json" or ".webmanifest" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".ico" => "image/x-icon",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream",
        };
    }

    // ── Weather proxy ───────────────────────────────────────

    /// <summary>aviationweather.gov airport + runways, cached per ICAO for the session (runways don't
    /// move; a failed lookup isn't cached so it can retry).</summary>
    private async Task<EfbAirportInfo?> GetAirportAsync(string id)
    {
        if (_airportCache.TryGetValue(id, out var info)) return info;
        try
        {
            info = EfbAirportData.Parse(await _http.GetStringAsync(
                $"https://aviationweather.gov/api/data/airport?ids={id}&format=json"));
        }
        catch (Exception ex) { Log.Debug(ex, "[EFB] Airport lookup failed for {Icao}", id); }
        if (info != null) _airportCache[id] = info;
        return info;
    }

    private async Task<EfbNetworkTraffic?> SafeNetworkAsync()
    {
        try { return await _network.GetAsync(); }
        catch (Exception ex) { Log.Debug(ex, "[EFB] Network feed unavailable for the weather app"); return null; }
    }

    private async Task<(string metar, string taf)> FetchWeatherAsync(string icao)
    {
        string metar = await _simBrief.FetchMetarAsync(icao) ?? "";
        string taf = "";
        try
        {
            taf = (await _http.GetStringAsync(
                $"https://aviationweather.gov/api/data/taf?ids={icao}&format=raw")).Trim();
        }
        catch (Exception ex) { Log.Debug(ex, "[EFB] TAF fetch failed for {Icao}", icao); }
        return (metar, taf);
    }

    // ── Auth / pairing ──────────────────────────────────────

    private bool IsAuthorized(HttpContext ctx)
    {
        var token = ctx.Request.Query["t"].FirstOrDefault()
                    ?? ctx.Request.Headers["X-EFB-Token"].FirstOrDefault();
        if (string.IsNullOrEmpty(token))
        {
            var auth = ctx.Request.Headers.Authorization.FirstOrDefault();
            if (auth?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
                token = auth["Bearer ".Length..];
        }
        return !string.IsNullOrEmpty(token) &&
               CryptographicOperations.FixedTimeEquals(
                   Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(DeviceToken));
    }

    private void EnsurePairingSecret()
    {
        var secret = _settings.Settings.EfbPairingSecret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            _settings.Settings.EfbPairingSecret = secret;
            _settings.Save();
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("AVIATES_EFB_" + secret));
        DeviceToken = Convert.ToHexString(hash)[..24].ToLowerInvariant();
        // Stable 6-digit code derived from the same hash.
        int code = (BitConverter.ToInt32(hash, 0) & 0x7FFFFFFF) % 1_000_000;
        PairingCode = code.ToString("D6");
    }

    // ── Networking ──────────────────────────────────────────

    private static int ResolveBoundPort(WebApplication app)
    {
        foreach (var addr in app.Urls)
            if (Uri.TryCreate(addr, UriKind.Absolute, out var uri) && uri.Port > 0)
                return uri.Port;
        return 0;
    }

    private static IReadOnlyList<string> BuildPairingUrls(int port, string token)
    {
        var urls = new List<string>();
        foreach (var ip in LanIPv4Addresses())
            urls.Add($"http://{ip}:{port}/#t={token}");
        if (urls.Count == 0) urls.Add($"http://localhost:{port}/#t={token}");
        return urls;
    }

    private static IEnumerable<string> LanIPv4Addresses()
    {
        var results = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var s = ua.Address.ToString();
                    if (s.StartsWith("169.254")) continue;  // APIPA / link-local
                    results.Add(s);
                }
            }
        }
        catch (Exception ex) { Log.Debug(ex, "[EFB] LAN address enumeration failed"); }
        // Prefer common private ranges first (most likely the Wi-Fi the tablet is on).
        return results.OrderBy(ip => ip.StartsWith("192.168.") ? 0 : ip.StartsWith("10.") ? 1 : 2);
    }

    private void OnSessionResetChecklists(object? sender, FlightSessionState state)
    {
        // Fresh checklists at the start of each new flight.
        if (state == FlightSessionState.PreFlight) _checklists.ResetAll();
    }

    // ── Request DTOs ────────────────────────────────────────
    private sealed record PairRequest(string Code);
    private sealed record ChecklistItemRequest(string ChecklistId, string ItemId, bool Checked, string? SetId = null);
    private sealed record ChecklistResetRequest(string? ChecklistId, string? SetId = null);
    private sealed record ChecklistSelectRequest(string? SetId);
    private sealed record ThemeRequest(string Theme);
    private sealed record SendDmRequest(string? RecipientId, string Content, bool Broadcast);
    private sealed record MessageIdRequest(string? Id, List<string>? Ids);
    private sealed record AddPilotRequest(string? PilotId, string? PilotName);
    private sealed record FriendCodeRequest(string? Code);
    private sealed record SimbriefImportRequest(string? Username);
    private sealed record PrefsRequest(string? WeightUnit);
    private sealed record AcarsTelexRequest(string? To, string? Text);
    private sealed record AcarsInfoRequest(string? Kind, string? Icao);
    private sealed record AcarsPdcRequest(string? Station, string? AircraftType, string? Destination, string? Departure, string? Stand, string? Atis);
    private sealed record AcarsStationRequest(string? Station);
    private sealed record AcarsTextRequest(string? Text);
    private sealed record AcarsRespondRequest(string? Id, string? Response);
    private sealed record AcarsReadRequest(List<string>? Ids);

    private IResult Datalink(EfbDatalinkResult r) =>
        Results.Json(new { ok = r.Ok, error = r.Error, message = r.Message, state = _datalink.GetState() }, Json,
            statusCode: r.Ok ? 200 : 400);
}

// ── WebSocket wrapper — serialises sends per socket ──────────

internal sealed class EfbSocket(WebSocket ws)
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime ConnectedUtc { get; } = DateTime.UtcNow;
    public string RemoteIp { get; init; } = "";
    public string DeviceName { get; init; } = "Device";

    /// <summary>"iPad · Safari", "Android phone · Chrome"… from a User-Agent string.</summary>
    public static string NameFromUserAgent(string? ua)
    {
        ua ??= "";
        string dev = ua.Contains("iPad") ? "iPad"
            : ua.Contains("iPhone") ? "iPhone"
            : ua.Contains("Android") ? (ua.Contains("Mobile") ? "Android phone" : "Android tablet")
            : ua.Contains("Macintosh") ? (ua.Contains("Mobile") ? "iPad" : "Mac")     // iPadOS asks for desktop sites
            : ua.Contains("Windows") ? "Windows PC"
            : ua.Contains("CrOS") ? "Chromebook"
            : ua.Contains("Linux") ? "Linux PC" : "Device";
        string br = ua.Contains("Edg/") ? "Edge"
            : ua.Contains("Firefox/") || ua.Contains("FxiOS") ? "Firefox"
            : ua.Contains("Chrome/") || ua.Contains("CriOS") ? "Chrome"
            : ua.Contains("Safari/") ? "Safari" : "";
        return br.Length > 0 ? $"{dev} · {br}" : dev;
    }
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<bool> SendAsync(string text)
    {
        if (ws.State != WebSocketState.Open) return false;
        await _lock.WaitAsync();
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            return true;
        }
        catch { return false; }
        finally { _lock.Release(); }
    }

    public async Task CloseAsync()
    {
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
        }
        catch { /* ignore */ }
    }
}
