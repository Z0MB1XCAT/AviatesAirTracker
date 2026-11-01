using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Core.Efb;
using AviatesAirTracker.Models;
using Serilog;

namespace AviatesAirTracker.Services.Efb;

// ============================================================
// EFB DATALINK SERVICE — the ACARS app's terminal on the real Hoppie network
//
// Everything runs under the PILOT's own Hoppie logon code and callsign, never the company
// code: it reads the pilot's own inbox, so there's no shared queue to fight over (the AI
// dispatcher, which reads the shared VAOPS inbox, stays server-side for that reason).
//
// The inbox is read with "peek", which leaves messages undelivered, so an in-sim ACARS unit
// (Fenix, PMDG, FSLabs…) polling the same callsign still gets every message. Peek returns all
// stored messages, so they're de-duplicated by Hoppie's message id; the first peek for a
// callsign is treated as history (read). Peeks run every 45–75 s (Hoppie's etiquette) and only
// while an EFB device is connected.
//
// Outbound: telex (incl. company/dispatch on VAOPS and PDC requests), inforeq (METAR, TAF,
// VATSIM ATIS) and CPDLC (logon/logoff, requests, WILCO/UNABLE/… responses).
// ============================================================

public sealed class EfbDatalinkService : IDisposable
{
    private const int MaxMessages = 300;

    private readonly SettingsService _settings;
    private readonly HoppiesAcarsClient _client;
    private readonly BookingService _booking;
    private readonly SimBriefService _simBrief;
    private readonly EfbStateService _state;
    private readonly HoppiesAcarsOrchestrator _ops;

    private readonly object _lock = new();
    private readonly List<EfbAcarsMessage> _msgs = [];
    private readonly HashSet<string> _seen = [];
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly Random _rng = new();

    private int _min;                       // CPDLC message number for our downlinks
    private string _cpdlcStation = "";
    private string _cpdlcState = "off";     // off | pending | connected
    private string _nextStation = "";
    private string _peekCallsign = "";      // the callsign the de-dupe set belongs to
    private DateTime? _lastPollUtc;
    private bool _lastPollOk;
    private string? _lastError;
    private System.Threading.Timer? _timer;
    private Func<bool>? _active;
    private bool _disposed;

    public event EventHandler? Changed;

    public EfbDatalinkService(SettingsService settings, HoppiesAcarsClient client, BookingService booking,
                              SimBriefService simBrief, EfbStateService state, HoppiesAcarsOrchestrator ops)
    {
        _settings = settings;
        _client = client;
        _booking = booking;
        _simBrief = simBrief;
        _state = state;
        _ops = ops;
        _ops.MessageSent += OnOpsMessage;
    }

    private string Code => (_settings.Settings.HoppiesPilotCode ?? "").Trim();

    /// <summary>The ATC callsign the datalink speaks as: booking, then the plan (desktop or EFB import),
    /// then the live session.</summary>
    public string Callsign
    {
        get
        {
            var c = _booking.ActiveBooking?.Callsign;
            // the plan in use, including one imported inside the EFB (which never reaches SimBriefService)
            if (string.IsNullOrWhiteSpace(c)) c = _state.PlanCallsign();
            if (string.IsNullOrWhiteSpace(c)) c = _state.GetLiveSnapshot().Callsign;
            return EfbAcars.Clean(c).Replace(" ", "");
        }
    }

    // ── polling ──────────────────────────────────────────────

    /// <summary>Starts the inbox timer; <paramref name="active"/> says whether anyone is watching.</summary>
    public void Start(Func<bool> active)
    {
        _active = active;
        _timer ??= new System.Threading.Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
    }

    private async Task TickAsync()
    {
        try { if (_active?.Invoke() == true) await PollAsync(); }
        catch (Exception ex) { Log.Debug(ex, "[Datalink] poll tick failed"); }
        finally
        {
            if (!_disposed) _timer?.Change(TimeSpan.FromSeconds(_rng.Next(45, 76)), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Reads the pilot's inbox now (peek). Returns false when it can't run or Hoppie refused.</summary>
    public async Task<bool> PollAsync()
    {
        var cs = Callsign;
        if (Code.Length == 0 || cs.Length == 0) return false;
        if (!await _pollGate.WaitAsync(0)) return false;   // one at a time
        try
        {
            var (ok, body) = await _client.ConnectAsync(Code, cs, "SERVER", "peek", null);
            bool changed;
            lock (_lock)
            {
                bool history = false;
                if (!cs.Equals(_peekCallsign, StringComparison.Ordinal))
                {
                    // a new callsign (new flight): its stored messages are history, and any CPDLC
                    // connection belonged to the old one
                    _peekCallsign = cs;
                    _seen.Clear();
                    history = true;
                    _cpdlcState = "off"; _cpdlcStation = ""; _nextStation = ""; _min = 0;
                }
                _lastPollUtc = DateTime.UtcNow;
                _lastPollOk = ok;
                _lastError = ok ? null : EfbAcars.ErrorOf(body);
                changed = true;
                if (ok)
                    foreach (var m in EfbAcars.Parse(body))
                    {
                        var key = m.Id ?? $"{m.From}|{m.Type}|{m.Packet}";
                        if (!_seen.Add(key)) continue;
                        IngestLocked(m, history);
                    }
            }
            if (changed) Changed?.Invoke(this, EventArgs.Empty);
            return ok;
        }
        finally { _pollGate.Release(); }
    }

    private void IngestLocked(HoppieMessage m, bool history)
    {
        var msg = new EfbAcarsMessage
        {
            HoppieId = m.Id, Dir = "in", Station = m.From.ToUpperInvariant(), Type = m.Type,
            Utc = DateTime.UtcNow, Read = history, Text = m.Packet.Trim(),
            Kind = m.Type.ToUpperInvariant(),
        };
        if (m.Type == "telex")
        {
            msg.Kind = msg.Station == "VAOPS" ? "COMPANY"
                     : LooksLikeClearance(msg.Text) ? "CLEARANCE" : "TELEX";
        }
        else if (m.Type == "cpdlc" && EfbAcars.TryParseCpdlc(m.Packet, out var p))
        {
            msg.Min = p.Min; msg.Mrn = p.Mrn; msg.Ra = p.Ra; msg.Text = p.Display;
            msg.Responses = p.Responses.ToList();
            msg.Open = !history && msg.Responses.Count > 0;
            msg.Kind = Classify(p.Display);
            ApplyCpdlcStateLocked(msg);
            // ATC's answer to one of our downlinks closes it
            if (p.Mrn is int mrn)
            {
                var mine = _msgs.LastOrDefault(x => x.Dir == "out" && x.Type == "cpdlc" && x.Min == mrn && x.Station == msg.Station);
                if (mine != null) mine.Response = p.Display;
            }
        }
        AddLocked(msg);
    }

    private static bool LooksLikeClearance(string t) =>
        t.Contains("CLRD", StringComparison.OrdinalIgnoreCase) || t.Contains("PDC", StringComparison.OrdinalIgnoreCase)
        || t.Contains("CLEARED", StringComparison.OrdinalIgnoreCase);

    private static string Classify(string t)
    {
        var u = t.ToUpperInvariant();
        if (u.Contains("LOGON")) return "LOGON";
        if (u.Contains("HANDOVER") || u.Contains("CURRENT ATC UNIT") || u.Contains("NEXT DATA AUTHORITY")) return "ATC UNIT";
        if (u.Contains("LOGOFF") || u.Contains("SERVICE TERMINATED")) return "LOGOFF";
        if (u.Contains("CLRD") || u.Contains("CLEARED") || u.Contains("PDC")) return "CLEARANCE";
        if (u.Contains("CONTACT") || u.Contains("MONITOR")) return "CONTACT";
        return "CPDLC";
    }

    private void ApplyCpdlcStateLocked(EfbAcarsMessage m)
    {
        var u = m.Text.ToUpperInvariant();
        if (u.Contains("LOGON ACCEPTED")) { _cpdlcState = "connected"; _cpdlcStation = m.Station; }
        else if ((u.Contains("LOGON REJECTED") || (u.StartsWith("UNABLE") && _cpdlcState == "pending")) && m.Station == _cpdlcStation)
            { _cpdlcState = "off"; _cpdlcStation = ""; }
        else if ((u.Contains("LOGOFF") || u.Contains("SERVICE TERMINATED")) && m.Station == _cpdlcStation)
            { _cpdlcState = "off"; _cpdlcStation = ""; }
        var h = System.Text.RegularExpressions.Regex.Match(u, @"HANDOVER\s+@?([A-Z0-9_]{3,10})");
        if (h.Success) _nextStation = h.Groups[1].Value;
    }

    private void AddLocked(EfbAcarsMessage m)
    {
        _msgs.Add(m);
        if (_msgs.Count > MaxMessages) _msgs.RemoveRange(0, _msgs.Count - MaxMessages);
    }

    // ── sending ──────────────────────────────────────────────

    private string? Guard(string? station = null)
    {
        if (Code.Length == 0) return "Add your Hoppie logon code in the desktop app (Settings › Hoppies ACARS) to use datalink.";
        if (Callsign.Length == 0) return "No callsign yet. Book a flight or load a SimBrief plan first.";
        if (station != null && !EfbAcars.IsStation(station)) return "Enter a valid station (e.g. EGLL, EDGG or VAOPS).";
        return null;
    }

    private EfbAcarsMessage Outbound(string station, string type, string kind, string text, bool ok, string body, int? min = null, int? mrn = null)
    {
        var m = new EfbAcarsMessage
        {
            Dir = "out", Station = station, Type = type, Kind = kind, Text = text, Utc = DateTime.UtcNow,
            Read = true, Status = ok ? "sent" : "failed", Error = ok ? null : EfbAcars.ErrorOf(body), Min = min, Mrn = mrn,
        };
        lock (_lock) AddLocked(m);
        Changed?.Invoke(this, EventArgs.Empty);
        return m;
    }

    public async Task<EfbDatalinkResult> TelexAsync(string? to, string? text, string kind = "TELEX")
    {
        to = EfbAcars.Clean(to).Replace(" ", "");
        var err = Guard(to); if (err != null) return EfbDatalinkResult.Fail(err);
        var clean = EfbAcars.Clean(text);
        if (clean.Length == 0) return EfbDatalinkResult.Fail("Type a message first.");
        if (clean.Length > 500) return EfbDatalinkResult.Fail("Telex is limited to 500 characters.");
        var (ok, body) = await _client.ConnectAsync(Code, Callsign, to, "telex", clean);
        var m = Outbound(to, "telex", kind, clean, ok, body);
        return ok ? EfbDatalinkResult.Done(m) : EfbDatalinkResult.Fail(m.Error, m);
    }

    /// <summary>METAR / TAF / ATIS (VATSIM) for an airport; the answer comes back in the same request.</summary>
    public async Task<EfbDatalinkResult> InfoAsync(string? kind, string? icao)
    {
        icao = EfbAcars.Clean(icao).Replace(" ", "");
        var err = Guard(icao); if (err != null) return EfbDatalinkResult.Fail(err);
        var k = (kind ?? "").ToUpperInvariant() switch { "TAF" => "TAF", "ATIS" => "VATATIS", "SHORTTAF" => "SHORTTAF", _ => "METAR" };
        var label = k == "VATATIS" ? "ATIS" : k;
        var (ok, body) = await _client.ConnectAsync(Code, Callsign, "SERVER", "inforeq", $"{k} {icao}");
        if (!ok) return EfbDatalinkResult.Fail(Outbound(icao, "info", label + " REQ", $"{label} {icao}", false, body).Error);
        var text = string.Join("\n", EfbAcars.Parse(body).Select(x => x.Packet)).Trim();
        var m = new EfbAcarsMessage
        {
            Dir = "in", Station = icao, Type = "info", Kind = label, Utc = DateTime.UtcNow, Read = true,
            Text = text.Length > 0 ? text : $"NO {label} AVAILABLE FOR {icao}",
        };
        lock (_lock) AddLocked(m);
        Changed?.Invoke(this, EventArgs.Empty);
        return EfbDatalinkResult.Done(m);
    }

    public Task<EfbDatalinkResult> PdcAsync(string? station, string? aircraftType, string? destination, string? departure,
                                             string? stand, string? atis)
    {
        var err = Guard();
        if (err != null) return Task.FromResult(EfbDatalinkResult.Fail(err));
        if (string.IsNullOrWhiteSpace(destination) || string.IsNullOrWhiteSpace(departure))
            return Task.FromResult(EfbDatalinkResult.Fail("Departure and destination are needed for a clearance request."));
        var text = EfbAcars.PdcRequest(Callsign, string.IsNullOrWhiteSpace(aircraftType) ? "ZZZZ" : aircraftType,
                                       destination, departure, stand, atis);
        return TelexAsync(string.IsNullOrWhiteSpace(station) ? departure : station, text, "PDC REQ");
    }

    public async Task<EfbDatalinkResult> CpdlcLogonAsync(string? station)
    {
        station = EfbAcars.Clean(station).Replace(" ", "");
        var err = Guard(station); if (err != null) return EfbDatalinkResult.Fail(err);
        int min; lock (_lock) min = ++_min;
        var (ok, body) = await _client.ConnectAsync(Code, Callsign, station, "cpdlc", EfbAcars.FormatCpdlc(min, null, "Y", "REQUEST LOGON"));
        if (ok) lock (_lock) { _cpdlcState = "pending"; _cpdlcStation = station; if (_nextStation == station) _nextStation = ""; }
        var m = Outbound(station, "cpdlc", "LOGON", "REQUEST LOGON", ok, body, min);
        return ok ? EfbDatalinkResult.Done(m) : EfbDatalinkResult.Fail(m.Error, m);
    }

    public async Task<EfbDatalinkResult> CpdlcLogoffAsync()
    {
        string station; int min;
        lock (_lock) { station = _cpdlcStation; min = ++_min; }
        if (station.Length == 0) return EfbDatalinkResult.Fail("Not logged on to a CPDLC station.");
        var (ok, body) = await _client.ConnectAsync(Code, Callsign, station, "cpdlc", EfbAcars.FormatCpdlc(min, null, "N", "LOGOFF"));
        lock (_lock) { _cpdlcState = "off"; _cpdlcStation = ""; }   // off locally either way
        var m = Outbound(station, "cpdlc", "LOGOFF", "LOGOFF", ok, body, min);
        return ok ? EfbDatalinkResult.Done(m) : EfbDatalinkResult.Fail(m.Error, m);
    }

    public async Task<EfbDatalinkResult> CpdlcRequestAsync(string? text)
    {
        var err = Guard(); if (err != null) return EfbDatalinkResult.Fail(err);
        string station;
        lock (_lock) station = _cpdlcState == "connected" ? _cpdlcStation : "";
        if (station.Length == 0) return EfbDatalinkResult.Fail("Log on to a CPDLC station first.");
        var clean = EfbAcars.Clean(text);
        if (clean.Length == 0) return EfbDatalinkResult.Fail("Build a request first.");
        int min; lock (_lock) min = ++_min;
        var (ok, body) = await _client.ConnectAsync(Code, Callsign, station, "cpdlc", EfbAcars.FormatCpdlc(min, null, "Y", clean));
        var m = Outbound(station, "cpdlc", "REQUEST", clean, ok, body, min);
        return ok ? EfbDatalinkResult.Done(m) : EfbDatalinkResult.Fail(m.Error, m);
    }

    public async Task<EfbDatalinkResult> CpdlcRespondAsync(string? id, string? response)
    {
        var err = Guard(); if (err != null) return EfbDatalinkResult.Fail(err);
        EfbAcarsMessage? up; int min;
        var resp = (response ?? "").Trim().ToUpperInvariant();
        lock (_lock)
        {
            up = _msgs.FirstOrDefault(x => x.Id == id && x.Dir == "in" && x.Type == "cpdlc");
            if (up == null || up.Min == null) return EfbDatalinkResult.Fail("That message can't be answered.");
            if (!up.Responses.Contains(resp)) return EfbDatalinkResult.Fail($"{resp} isn't a valid response to that message.");
            min = ++_min;
        }
        var (ok, body) = await _client.ConnectAsync(Code, Callsign, up.Station, "cpdlc", EfbAcars.FormatCpdlc(min, up.Min, "N", resp));
        if (ok) lock (_lock) { up.Response = resp; up.Open = !EfbAcars.IsClosing(resp); up.Read = true; }
        var m = Outbound(up.Station, "cpdlc", "RESPONSE", resp, ok, body, min, up.Min);
        return ok ? EfbDatalinkResult.Done(m) : EfbDatalinkResult.Fail(m.Error, m);
    }

    public void MarkRead(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        bool any = false;
        lock (_lock)
            foreach (var m in _msgs)
                if (!m.Read && set.Contains(m.Id)) { m.Read = true; any = true; }
        if (any) Changed?.Invoke(this, EventArgs.Empty);
    }

    // ── state ────────────────────────────────────────────────

    public EfbAcarsState GetState()
    {
        var plan = _state.GetFlightPlan();
        var ops = _ops.MessageLog.Select(o => new EfbAcarsOps
        {
            Utc = o.SentAtUtc, Category = o.Category, Message = o.Message, Success = o.Success,
        }).ToList();
        lock (_lock)
        {
            return new EfbAcarsState
            {
                HasLogon = Code.Length > 0,
                AutoOpsEnabled = _settings.Settings.HoppiesEnabled,
                Callsign = Callsign,
                Departure = plan.DepartureIcao, Arrival = plan.ArrivalIcao, Alternate = plan.AlternateIcao,
                AircraftType = plan.AircraftType,
                CpdlcStation = _cpdlcStation, CpdlcState = _cpdlcState, NextStation = _nextStation,
                LastPollUtc = _lastPollUtc, LastPollOk = _lastPollOk, LastError = _lastError,
                Unread = _msgs.Count(m => m.Dir == "in" && !m.Read),
                Messages = _msgs.Select(m => m.Clone()).ToList(),
                Ops = ops,
            };
        }
    }

    private void OnOpsMessage(object? s, HoppiesLogEntry e) => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _disposed = true;
        _ops.MessageSent -= OnOpsMessage;
        _timer?.Dispose();
        _timer = null;
    }
}

public sealed record EfbDatalinkResult(bool Ok, string? Error, EfbAcarsMessage? Message)
{
    public static EfbDatalinkResult Done(EfbAcarsMessage m) => new(true, null, m);
    public static EfbDatalinkResult Fail(string? error, EfbAcarsMessage? m = null) => new(false, error ?? "Not sent.", m);
}
