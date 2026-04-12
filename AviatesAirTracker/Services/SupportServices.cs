using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Models;
using Serilog;
using System.IO;
using System.Text.Json;

namespace AviatesAirTracker.Services;

// ============================================================
// FUEL ANALYZER
// Tracks fuel burn throughout flight
// ============================================================

public class FuelAnalyzer
{
    private double _fuelAtStart;
    private DateTime _startTime;
    private bool _initialized;
    private double _maxAltSeen;
    private readonly List<(DateTime t, double fuel)> _fuelHistory = [];

    public double MaxAltitude => _maxAltSeen;
    public double TotalFuelBurnedLbs { get; private set; }
    public double CurrentBurnRatePPH { get; private set; }
    public double AverageBurnRatePPH { get; private set; }
    public double FuelEfficiencyLbsPerNm { get; private set; }

    public void Process(TelemetrySnapshot snap)
    {
        if (!_initialized)
        {
            _fuelAtStart = snap.Raw.FuelTotalLbs;
            _startTime = snap.Timestamp;
            _initialized = true;
        }

        if (snap.AltitudePressure > _maxAltSeen)
            _maxAltSeen = snap.AltitudePressure;

        // MAJOR-09: Guard against negative fuel burn when pilot refuels mid-session (MSFS reload).
        TotalFuelBurnedLbs = Math.Max(0, _fuelAtStart - snap.Raw.FuelTotalLbs);
        CurrentBurnRatePPH = snap.FuelBurnRatePPH;

        // Log fuel sample every 30 seconds
        if (_fuelHistory.Count == 0 || (snap.Timestamp - _fuelHistory.Last().t).TotalSeconds >= 30)
        {
            _fuelHistory.Add((snap.Timestamp, snap.Raw.FuelTotalLbs));
        }

        // Average burn rate
        double elapsed = (snap.Timestamp - _startTime).TotalHours;
        if (elapsed > 0)
            AverageBurnRatePPH = TotalFuelBurnedLbs / elapsed;
    }

    public List<(DateTime t, double fuel)> GetFuelHistory() => [.. _fuelHistory];

    public void Reset()
    {
        _initialized = false;
        _fuelAtStart = 0;
        TotalFuelBurnedLbs = 0;
        _maxAltSeen = 0;
        _fuelHistory.Clear();
    }
}

// ============================================================
// APPROACH MONITOR
// Real-time unstable approach detection and alerting
// ============================================================

public class ApproachMonitor
{
    public event EventHandler<ApproachAlert>? AlertTriggered;

    private readonly Dictionary<string, DateTime> _lastAlertTime = [];
    private const double ALERT_COOLDOWN_SECONDS = 10;

    public void CheckStability(TelemetrySnapshot snap)
    {
        if (snap.AltitudeAGL > 5000) return;

        CheckAndAlert("HIGH_VS", snap.VerticalSpeedFPM < -1500,
            $"HIGH DESCENT RATE {snap.VerticalSpeedFPM:F0}fpm", AlertLevel.Warning, snap);

        CheckAndAlert("GEAR_UP_LOW", snap.AltitudeAGL < 1000 && !snap.GearDown,
            "GEAR NOT DOWN BELOW 1000ft", AlertLevel.Critical, snap);

        CheckAndAlert("OVERSPEED_APP", snap.IASKts > 200,
            $"OVERSPEED {snap.IASKts:F0}kt", AlertLevel.Warning, snap);

        CheckAndAlert("BANK_HIGH", Math.Abs(snap.Raw.Bank) > 30,
            $"EXCESSIVE BANK {snap.Raw.Bank:F1}°", AlertLevel.Warning, snap);
    }

    private void CheckAndAlert(string key, bool condition, string message, AlertLevel level, TelemetrySnapshot snap)
    {
        if (!condition) return;
        if (_lastAlertTime.TryGetValue(key, out var last) &&
            (DateTime.UtcNow - last).TotalSeconds < ALERT_COOLDOWN_SECONDS) return;

        _lastAlertTime[key] = DateTime.UtcNow;
        AlertTriggered?.Invoke(this, new ApproachAlert
        {
            Message = message,
            Level = level,
            AltitudeAGL = snap.AltitudeAGL,
            Timestamp = snap.Timestamp
        });
    }
}

public class ApproachAlert
{
    public string Message { get; set; } = "";
    public AlertLevel Level { get; set; }
    public double AltitudeAGL { get; set; }
    public DateTime Timestamp { get; set; }
}

// ============================================================
// ALERT SERVICE
// In-app notification system
// ============================================================

public class AlertService
{
    public event EventHandler<AlertNotification>? AlertRaised;
    public event EventHandler<LandingResult>? LandingResultReady;

    private readonly List<AlertNotification> _activeAlerts = [];
    private readonly object _lock = new();

    public IReadOnlyList<AlertNotification> ActiveAlerts
    {
        get { lock (_lock) return _activeAlerts.AsReadOnly(); }
    }

    public void ShowAlert(string message, AlertLevel level, TimeSpan? duration = null)
    {
        // MAJOR-13: At 20Hz during approach, this spawned a new Task.Delay per call (up to 60+ concurrent
        // timers). Now dedup: if an identical message is already active, skip.
        lock (_lock)
        {
            if (_activeAlerts.Any(a => a.Message == message && a.Level == level))
                return;
        }

        var alert = new AlertNotification
        {
            Id = Guid.NewGuid(),
            Message = message,
            Level = level,
            Timestamp = DateTime.UtcNow,
            ExpiresAt = duration.HasValue ? DateTime.UtcNow + duration.Value : null
        };

        lock (_lock)
        {
            _activeAlerts.Add(alert);
        }

        AlertRaised?.Invoke(this, alert);
        Log.Information("[Alert] [{Level}] {Message}", level, message);

        if (duration.HasValue)
        {
            // MAJOR-13: Use a single continuation, no race of dozens of Task.Delay timers.
            _ = Task.Delay(duration.Value).ContinueWith(_ =>
            {
                lock (_lock) { _activeAlerts.Remove(alert); }
            }, TaskScheduler.Default);
        }
    }

    public void ShowLandingResult(LandingResult result)
    {
        LandingResultReady?.Invoke(this, result);
        ShowAlert($"LANDED — Score: {result.LandingScore}/100 | {result.VerticalSpeedFPM:F0}fpm",
            result.LandingScore >= 70 ? AlertLevel.Success : AlertLevel.Warning,
            TimeSpan.FromSeconds(10));
    }

    public void Dismiss(Guid id)
    {
        lock (_lock) { _activeAlerts.RemoveAll(a => a.Id == id); }
    }
}

public enum AlertLevel { Info, Success, Warning, Critical }

public class AlertNotification
{
    public Guid Id { get; set; }
    public string Message { get; set; } = "";
    public AlertLevel Level { get; set; }
    public DateTime Timestamp { get; set; }
    public DateTime? ExpiresAt { get; set; }

    public string Color => Level switch
    {
        AlertLevel.Success => "#22C55E",
        AlertLevel.Warning => "#EAB308",
        AlertLevel.Critical => "#EF4444",
        _ => "#3D7EEE"
    };
}

// ============================================================
// SETTINGS SERVICE
// Persistent application settings
// ============================================================

public class SettingsService
{
    private AppSettings _settings = new();
    // MINOR-07: Was a relative path — UnauthorizedAccessException when installed to Program Files.
    // Use AppData\Roaming which is always writable.
    private readonly string _settingsPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AviatesAirTracker", "aviates_settings.json");

    public AppSettings Settings => _settings;

    /// <summary>
    /// Fired after Save() or Reset() — lets Blazor components (e.g. MainLayout) refresh
    /// displayed pilot name / initials without a full page reload.
    /// </summary>
    public event EventHandler? SettingsChanged;

    public SettingsService()
    {
        Load();
    }

    public void Save()
    {
        try
        {
            // Ensure the directory exists (AppData\Roaming\AviatesAirTracker\)
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Settings] Failed to save");
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                Log.Information("[Settings] Loaded from {Path}", _settingsPath);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Settings] Failed to load, using defaults");
            _settings = new AppSettings();
        }
    }

    public void Reset() { _settings = new AppSettings(); Save(); /* SettingsChanged fired inside Save() */ }

    /// <summary>
    /// Derives a shareable 9-char friend code (XXXX-XXXX) from the pilot's ACARS key.
    /// Deterministic — same ACARS key always produces the same code.
    /// Uses an unambiguous character set (no 0, O, I, 1).
    /// </summary>
    public static string GenerateFriendCode(string acarsKey)
    {
        if (string.IsNullOrEmpty(acarsKey)) return "";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("AVIATES_FC_" + acarsKey));
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var sb = new System.Text.StringBuilder(9);
        for (int i = 0; i < 8; i++)
            sb.Append(chars[hash[i] % chars.Length]);
        sb.Insert(4, '-');
        return sb.ToString();
    }

    /// <summary>
    /// Regenerates and persists the friend code whenever the ACARS key changes.
    /// Call this after saving settings that modify AcarsKey.
    /// </summary>
    public void RefreshFriendCode()
    {
        _settings.FriendCode = GenerateFriendCode(_settings.AcarsKey);
        Save();
    }
}

public class AppSettings
{
    public string PilotName { get; set; } = "";
    public string PilotId { get; set; } = "";
    public string AcarsKey { get; set; } = "";
    public string SimBriefUsername { get; set; } = "";
    public bool AutoConnectSimConnect { get; set; } = true;
    public bool AutoStartFlight { get; set; } = true;
    public bool ShowApproachAlerts { get; set; } = true;
    public bool ShowLandingResult { get; set; } = true;
    public bool MinimizeToTray { get; set; } = false;
    public bool StartMinimized { get; set; } = false;
    public double TelemetryHz { get; set; } = 20;
    public bool RecordTelemetryHistory { get; set; } = true;
    public string DefaultExportFormat { get; set; } = "JSON";
    public string Units { get; set; } = "Aviation";  // Aviation / Metric
    public string BackendApiUrl { get; set; } = "https://acars.flyaviatesair.uk";
    public bool SyncToBackend { get; set; } = false;
    public bool DiscordPresenceEnabled { get; set; } = true;
    public string DiscordClientId { get; set; } = "1486109299506942079";
    // Derived from AcarsKey via SHA-256 — never stored as a secret, safe to share with other pilots.
    // Format: XXXX-XXXX (e.g. "K4MR-B7WN")
    public string FriendCode { get; set; } = "";
    // IATA code of the airport the pilot is currently based at.
    // Updated automatically when a booked flight completes.
    // Empty = no restriction (show all routes).
    public string CurrentAirportIata { get; set; } = "";
    // UI theme preference. "system" follows OS dark/light mode automatically.
    public string Theme { get; set; } = "system"; // "system" | "light" | "dark"
}

// ============================================================
// PILOT STATS SERVICE
// Aggregates statistics across all flights
// ============================================================

public class PilotStatsService
{
    private readonly IFlightRepository _flightRepo;
    private readonly ILandingRepository _landingRepo;
    private readonly IPilotRepository _pilotRepo;
    private readonly SettingsService _settings;

    public PilotStatsService(IFlightRepository flightRepo, ILandingRepository landingRepo,
        IPilotRepository pilotRepo, SettingsService settings)
    {
        _flightRepo = flightRepo;
        _landingRepo = landingRepo;
        _pilotRepo = pilotRepo;
        _settings = settings;
    }

    public async Task<PilotStatistics> ComputeAsync()
    {
        var flights = await _flightRepo.GetAllAsync();
        var landings = await _landingRepo.GetAllAsync();

        var completed = flights.Where(f => f.Status == FlightStatus.Completed).ToList();

        var stats = new PilotStatistics
        {
            PilotId = _settings.Settings.PilotId,
            PilotName = _settings.Settings.PilotName,
            TotalFlights = completed.Count,
            TotalHoursBlock = completed.Sum(f => f.BlockTime.TotalHours),
            TotalHoursAir = completed.Sum(f => f.AirTime.TotalHours),
            TotalDistanceNm = completed.Sum(f => f.ActualDistanceNm),
            TotalFuelUsedLbs = completed.Sum(f => f.FuelUsedLbs),
            TotalLandings = landings.Count,
            AverageLandingScore = landings.Any() ? landings.Average(l => l.LandingScore) : 0,
            BestLandingScore = landings.Any() ? landings.Max(l => l.LandingScore) : 0,
            AverageLandingVSFPM = landings.Any() ? landings.Average(l => l.VerticalSpeedFPM) : 0,
            BestLandingVSFPM = landings.Any() ? landings.Max(l => l.VerticalSpeedFPM) : 0,
            LastFlight = completed.Any() ? completed.Max(f => f.CreatedAt) : DateTime.MinValue,
            LastUpdated = DateTime.UtcNow
        };

        // Rank logic
        stats.Rank = stats.TotalHoursBlock switch
        {
            < 25 => "Student Pilot",
            < 100 => "First Officer",
            < 500 => "Senior First Officer",
            < 1000 => "Captain",
            _ => "Senior Captain"
        };

        return stats;
    }
}
