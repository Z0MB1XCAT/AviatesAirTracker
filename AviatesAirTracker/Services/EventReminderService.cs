using System.Globalization;
using Serilog;

namespace AviatesAirTracker.Services;

// ============================================================
// EVENT REMINDER SERVICE
//
// Watches the pilot's registered events (EventsService.GetMyEventsAsync) and fires
// ReminderDue once, 10 minutes before an event's scheduled start, so ShellWindow can show a
// confirmation popup. Confirming opens SimBrief dispatch (a light prefill of route/aircraft/time
// parsed back out of the event -- CreateEventDialog stores Route as "ORIG → DEST" and
// AircraftRestriction as a real fleet type code specifically so this can do that) and arms
// PendingEventFlight so the NEXT flight FlightSessionManager starts picks up the event's hours
// multiplier (1.5x participating, 2x an official/is_featured event -- see
// FlightRecord.HoursMultiplier). Confirming never touches BookingService/CurrentAirportIata --
// an event flight is deliberately independent of the booking/position system.
// ============================================================

public class PendingEventFlight
{
    public int EventId { get; set; }
    public bool IsOfficial { get; set; }
    public double Multiplier { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

public class EventReminderService : IDisposable
{
    private const int ReminderMinutesBefore = 10;
    private static readonly TimeSpan EventsRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PendingFlightValidFor = TimeSpan.FromHours(3);

    private readonly EventsService _events;
    private readonly SettingsService _settings;
    private readonly System.Timers.Timer _timer;

    private List<AviatesEvent> _myEvents = [];
    private DateTime _lastEventsFetchUtc = DateTime.MinValue;
    private readonly HashSet<int> _prompted = [];

    public event EventHandler<AviatesEvent>? ReminderDue;

    public PendingEventFlight? PendingEventFlight { get; private set; }

    public EventReminderService(EventsService events, SettingsService settings)
    {
        _events = events;
        _settings = settings;

        _timer = new System.Timers.Timer(TimeSpan.FromSeconds(30));
        _timer.Elapsed += async (_, _) => await TickAsync();
        _timer.AutoReset = true;
        _timer.Start();
    }

    private async Task TickAsync()
    {
        try
        {
            var key = _settings.Settings.AcarsKey.Trim();
            if (string.IsNullOrEmpty(key)) return;

            if (DateTime.UtcNow - _lastEventsFetchUtc > EventsRefreshInterval)
            {
                _myEvents = await _events.GetMyEventsAsync(key);
                _lastEventsFetchUtc = DateTime.UtcNow;
            }

            foreach (var evt in _myEvents)
            {
                if (_prompted.Contains(evt.Id)) continue;

                var start = EventStartUtc(evt);
                if (start == null) continue;

                var minutesUntil = (start.Value - DateTime.UtcNow).TotalMinutes;
                if (minutesUntil > 0 && minutesUntil <= ReminderMinutesBefore)
                {
                    _prompted.Add(evt.Id);
                    ReminderDue?.Invoke(this, evt);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[EventReminderService] Tick failed");
        }
    }

    public static DateTime? EventStartUtc(AviatesEvent evt)
    {
        if (evt.ParsedDate is not { } date) return null;
        var timeOfDay = TimeSpan.TryParseExact(evt.TimeUtc, @"hh\:mm", CultureInfo.InvariantCulture, out var t)
            ? t : TimeSpan.Zero;
        return DateTime.SpecifyKind(date.Date + timeOfDay, DateTimeKind.Utc);
    }

    /// <summary>Arms the hours-multiplier flag for whichever flight the pilot starts next, and
    /// opens SimBrief dispatch pre-filled with as much of the event's route/aircraft/time as can
    /// be parsed back out of it.</summary>
    public void ConfirmAttendance(AviatesEvent evt)
    {
        var start = EventStartUtc(evt) ?? DateTime.UtcNow;

        PendingEventFlight = new PendingEventFlight
        {
            EventId = evt.Id,
            IsOfficial = evt.IsFeatured,
            Multiplier = evt.IsFeatured ? 2.0 : 1.5,
            ExpiresAtUtc = start + PendingFlightValidFor,
        };

        OpenSimBriefDispatch(evt, start);
    }

    /// <summary>Called once by FlightSessionManager when a new flight starts. Returns null (and
    /// clears the flag either way) if nothing was armed, or if it was armed too long ago to
    /// plausibly still be this flight.</summary>
    public PendingEventFlight? ConsumePendingEventFlight()
    {
        var pending = PendingEventFlight;
        PendingEventFlight = null;

        if (pending == null) return null;
        return DateTime.UtcNow <= pending.ExpiresAtUtc ? pending : null;
    }

    // Route is stored as "ORIG → DEST" by CreateEventDialog specifically so this can parse it
    // back out; AircraftRestriction is a real fleet type code from the same dialog's dropdown.
    // Also accepts a plain "-" separator (e.g. "EGFF-EGKK") since an admin-created event entered
    // directly in Supabase (bypassing CreateEventDialog entirely) isn't guaranteed to use the
    // arrow format -- confirmed against a real admin-created row. Falls back to the plain
    // dispatch page with nothing pre-filled if neither separator parses into exactly two ICAO-
    // shaped codes -- still "opens SimBrief in their browser to make the flight plan", just
    // without the shortcut.
    private static void OpenSimBriefDispatch(AviatesEvent evt, DateTime startUtc)
    {
        try
        {
            var parts = evt.Route.Split('→', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
                parts = evt.Route.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && (parts[0].Length is < 3 or > 4 || parts[1].Length is < 3 or > 4))
                parts = [];

            var url = "https://dispatch.simbrief.com/options/custom";
            var query = new List<string>();

            if (parts.Length == 2)
            {
                query.Add($"orig={Uri.EscapeDataString(parts[0])}");
                query.Add($"dest={Uri.EscapeDataString(parts[1])}");
            }
            if (!string.IsNullOrWhiteSpace(evt.AircraftRestriction))
                query.Add($"type={Uri.EscapeDataString(evt.AircraftRestriction)}");

            query.Add($"date={startUtc.ToString("ddMMMyy", CultureInfo.InvariantCulture).ToUpperInvariant()}");
            query.Add($"deph={startUtc.Hour:D2}");
            query.Add($"depm={startUtc.Minute:D2}");

            if (query.Count > 0) url += "?" + string.Join("&", query);

            Log.Information("[EventReminderService] Opening SimBrief dispatch for event {Id}: {Url}", evt.Id, url);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[EventReminderService] Failed to open SimBrief for event {Id}", evt.Id);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
