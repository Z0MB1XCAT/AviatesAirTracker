using System.IO;
using System.Text.Json;
using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Models;
using Serilog;

namespace AviatesAirTracker.Services;

/// <summary>
/// Schedules and sends every automated Hoppies ACARS telex for the current flight:
/// loadsheet (via TakeoffPerformanceService's existing -15min timer), pushback
/// reminder (-5min, own timer), airborne/landing calls, post-flight summary, and
/// departure/arrival weather + hazard advisories. No-ops entirely when
/// AppSettings.HoppiesEnabled is false. Persists per-flight sent-state to disk so a
/// mid-flight app restart never duplicates a message.
/// </summary>
public class HoppiesAcarsOrchestrator : IDisposable
{
    private readonly SettingsService _settings;
    private readonly SimBriefService _simBrief;
    private readonly TakeoffPerformanceService _takeoffPerf;
    private readonly FlightPhaseDetector _phaseDetector;
    private readonly FlightSessionManager _session;
    private readonly BookingService _booking;
    private readonly LandingAnalyzer _landingAnalyzer;
    private readonly HoppiesAcarsClient _client;
    private readonly HazardZoneService _hazards;
    private readonly WeatherAdvisoryService _weather;

    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AviatesAirTracker", "hoppies_state.json");

    private HoppiesFlightState _state = new();
    private System.Timers.Timer? _pushbackTimer;
    private FlightPhase _lastPhase = FlightPhase.Unknown;

    /// <summary>Raised after every send attempt (success or failure) for the in-app log.</summary>
    public event EventHandler<HoppiesLogEntry>? MessageSent;

    public HoppiesAcarsOrchestrator(
        SettingsService settings,
        SimBriefService simBrief,
        TakeoffPerformanceService takeoffPerf,
        FlightPhaseDetector phaseDetector,
        FlightSessionManager session,
        BookingService booking,
        LandingAnalyzer landingAnalyzer,
        HoppiesAcarsClient client,
        HazardZoneService hazards,
        WeatherAdvisoryService weather)
    {
        _settings = settings;
        _simBrief = simBrief;
        _takeoffPerf = takeoffPerf;
        _phaseDetector = phaseDetector;
        _session = session;
        _booking = booking;
        _landingAnalyzer = landingAnalyzer;
        _client = client;
        _hazards = hazards;
        _weather = weather;

        LoadState();

        _simBrief.FlightPlanLoaded += OnFlightPlanLoaded;
        _takeoffPerf.BriefingTriggered += OnLoadsheetDue;
        _phaseDetector.PhaseChanged += OnPhaseChanged;
        _landingAnalyzer.LandingDetected += OnLandingDetected;
        _session.FlightCompleted += OnFlightCompleted;

        Serilog.Log.Information("[HoppiesAcars] Orchestrator initialized");
    }

    private bool Enabled => _settings.Settings.HoppiesEnabled;

    // ── Trigger: SimBrief plan loaded → reset state, arm pushback timer, check departure advisories ──

    private void OnFlightPlanLoaded(object? sender, SimBriefFlightPlan plan)
    {
        _state = new HoppiesFlightState();
        SaveState();
        _lastPhase = FlightPhase.Unknown;
        ResetPushbackTimer();

        if (plan.ScheduledDepartureUtc is { } dep)
            SchedulePushbackTimer(dep);

        _ = SendDepartureAdvisoriesAsync(plan);
    }

    private void SchedulePushbackTimer(DateTime scheduledDepartureUtc)
    {
        var alertAt = scheduledDepartureUtc.AddMinutes(-5);
        var delay = alertAt - DateTime.UtcNow;

        if (delay.TotalSeconds <= 0)
        {
            _ = SendPushbackReminderAsync(scheduledDepartureUtc);
            return;
        }

        _pushbackTimer = new System.Timers.Timer(delay.TotalMilliseconds) { AutoReset = false };
        _pushbackTimer.Elapsed += (_, _) => _ = SendPushbackReminderAsync(scheduledDepartureUtc);
        _pushbackTimer.Start();
    }

    private void ResetPushbackTimer()
    {
        _pushbackTimer?.Stop();
        _pushbackTimer?.Dispose();
        _pushbackTimer = null;
    }

    // ── Trigger: TakeoffPerformanceService fires 15 min before scheduled departure ──

    private void OnLoadsheetDue(object? sender, TakeoffBriefingEventArgs e)
    {
        if (!Enabled || !_settings.Settings.HoppiesSendLoadsheet || _state.LoadsheetSent) return;
        _ = SendLoadsheetAsync(e.Plan);
    }

    private async Task SendLoadsheetAsync(SimBriefFlightPlan plan)
    {
        var booking = _booking.ActiveBooking;
        if (booking is null) return;

        var flightNumber = Subsidiary.StripIcaoPrefix(
            !string.IsNullOrEmpty(booking.RouteCallsign) ? booking.RouteCallsign : booking.Callsign);
        var message = HoppiesMessageComposer.Loadsheet(flightNumber, booking.Registration ?? "", booking.AircraftType, plan);

        var success = await _client.SendTelexAsync(booking.Callsign, message);
        Log(success, "Loadsheet", message);
        if (success)
        {
            _state.LoadsheetSent = true;
            SaveState();
        }
    }

    private async Task SendPushbackReminderAsync(DateTime scheduledDepartureUtc)
    {
        if (!Enabled || !_settings.Settings.HoppiesSendTimingReminders || _state.PushbackReminderSent) return;

        var booking = _booking.ActiveBooking;
        if (booking is null) return;

        var message = HoppiesMessageComposer.PushbackReminder(booking.Callsign, scheduledDepartureUtc);
        var success = await _client.SendTelexAsync(booking.Callsign, message);
        Log(success, "Pushback Reminder", message);
        if (success)
        {
            _state.PushbackReminderSent = true;
            SaveState();
        }
    }

    // ── Trigger: flight phase changes → airborne call + arrival advisories ──

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEvent e)
    {
        var enteringInitialClimb = e.Current == FlightPhase.InitialClimb && _lastPhase != FlightPhase.InitialClimb;
        var enteringDescent = (e.Current == FlightPhase.TopOfDescent || e.Current == FlightPhase.Descent)
            && _lastPhase is not (FlightPhase.TopOfDescent or FlightPhase.Descent);
        _lastPhase = e.Current;

        if (!Enabled) return;

        if (enteringInitialClimb && _settings.Settings.HoppiesSendAirborneLandingCalls && !_state.AirborneSent)
            _ = SendAirborneCallAsync();

        if (enteringDescent && (_settings.Settings.HoppiesSendWeatherAdvisories || _settings.Settings.HoppiesSendHazardAdvisories)
            && !_state.ArrivalAdvisoriesSent && _simBrief.CurrentPlan is { } plan)
            _ = SendArrivalAdvisoriesAsync(plan);
    }

    private async Task SendAirborneCallAsync()
    {
        var booking = _booking.ActiveBooking;
        var plan = _simBrief.CurrentPlan;
        if (booking is null || plan is null) return;

        var message = HoppiesMessageComposer.AirborneCall(booking.Callsign, plan.DepartureICAO, DateTime.UtcNow);
        var success = await _client.SendTelexAsync(booking.Callsign, message);
        Log(success, "Airborne Call", message);
        if (success)
        {
            _state.AirborneSent = true;
            SaveState();
        }
    }

    // ── Trigger: LandingAnalyzer detects a landing ──

    private void OnLandingDetected(object? sender, LandingResult result)
    {
        if (!Enabled || !_settings.Settings.HoppiesSendAirborneLandingCalls || _state.LandingSent) return;
        _ = SendLandingCallAsync(result);
    }

    private async Task SendLandingCallAsync(LandingResult result)
    {
        var booking = _booking.ActiveBooking;
        if (booking is null) return;

        var arrivalIcao = string.IsNullOrEmpty(result.AirportICAO) ? (_simBrief.CurrentPlan?.ArrivalICAO ?? "") : result.AirportICAO;
        var message = HoppiesMessageComposer.LandingCall(booking.Callsign, arrivalIcao, DateTime.UtcNow);
        var success = await _client.SendTelexAsync(booking.Callsign, message);
        Log(success, "Landing Call", message);
        if (success)
        {
            _state.LandingSent = true;
            SaveState();
        }
    }

    // ── Trigger: FlightSessionManager.FlightCompleted → post-flight summary ──

    private void OnFlightCompleted(object? sender, FlightRecord record)
    {
        if (!Enabled || !_settings.Settings.HoppiesSendPostFlightSummary || _state.SummarySent) return;
        _ = SendSummaryAsync(record);
    }

    private async Task SendSummaryAsync(FlightRecord record)
    {
        var message = HoppiesMessageComposer.PostFlightSummary(record);
        var success = await _client.SendTelexAsync(record.Callsign, message);
        Log(success, "Flight Summary", message);
        if (success)
        {
            _state.SummarySent = true;
            SaveState();
        }
    }

    // ── Weather / hazard advisories ──

    private async Task SendDepartureAdvisoriesAsync(SimBriefFlightPlan plan)
    {
        await SendAdvisoriesAsync(plan.DepartureICAO, "DEPARTURE");
        _state.DepartureAdvisoriesSent = true;
        SaveState();
    }

    private async Task SendArrivalAdvisoriesAsync(SimBriefFlightPlan plan)
    {
        await SendAdvisoriesAsync(plan.ArrivalICAO, "ARRIVAL");
        _state.ArrivalAdvisoriesSent = true;
        SaveState();
    }

    private async Task SendAdvisoriesAsync(string icao, string phaseLabel)
    {
        if (!Enabled) return;

        var booking = _booking.ActiveBooking;
        if (booking is null || string.IsNullOrEmpty(icao)) return;

        if (_settings.Settings.HoppiesSendHazardAdvisories)
        {
            foreach (var hazard in _hazards.GetHazards(icao))
            {
                var message = HoppiesMessageComposer.HazardAdvisory(icao, hazard.Message);
                var success = await _client.SendTelexAsync(booking.Callsign, message);
                Log(success, "Hazard Advisory", message);
            }
        }

        if (_settings.Settings.HoppiesSendWeatherAdvisories)
        {
            foreach (var condition in await _weather.GetAdvisoriesAsync(icao))
            {
                var message = HoppiesMessageComposer.WeatherAdvisory(icao, condition, phaseLabel);
                var success = await _client.SendTelexAsync(booking.Callsign, message);
                Log(success, "Weather Advisory", message);
            }
        }
    }

    // ── Persistence + logging ──

    private void LoadState()
    {
        try
        {
            if (File.Exists(_statePath))
                _state = JsonSerializer.Deserialize<HoppiesFlightState>(File.ReadAllText(_statePath)) ?? new HoppiesFlightState();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[HoppiesAcars] Failed to load state");
            _state = new HoppiesFlightState();
        }
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(_state));
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[HoppiesAcars] Failed to save state");
        }
    }

    private void Log(bool success, string category, string message)
    {
        MessageSent?.Invoke(this, new HoppiesLogEntry
        {
            SentAtUtc = DateTime.UtcNow,
            Category = category,
            Message = message,
            Success = success
        });
    }

    public void Dispose()
    {
        ResetPushbackTimer();
        _simBrief.FlightPlanLoaded -= OnFlightPlanLoaded;
        _takeoffPerf.BriefingTriggered -= OnLoadsheetDue;
        _phaseDetector.PhaseChanged -= OnPhaseChanged;
        _landingAnalyzer.LandingDetected -= OnLandingDetected;
        _session.FlightCompleted -= OnFlightCompleted;
    }
}
