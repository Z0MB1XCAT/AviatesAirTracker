using AviatesAirTracker.Core.SimConnect;
using AviatesAirTracker.Models;
using DiscordRPC;
using Serilog;

namespace AviatesAirTracker.Services;

public class DiscordPresenceService : IDisposable
{
    private readonly FlightSessionManager _session;
    private readonly SettingsService _settings;
    private readonly SimConnectManager _simConnect;

    private DiscordRpcClient? _client;
    private DateTime _lastPresenceUpdate = DateTime.MinValue;
    private const int UPDATE_INTERVAL_SECONDS = 10;

    public DiscordPresenceService(FlightSessionManager session, SettingsService settings, SimConnectManager simConnect)
    {
        _session = session;
        _settings = settings;
        _simConnect = simConnect;

        _session.SessionStateChanged += OnSessionStateChanged;
        _session.TelemetryUpdated += OnTelemetryUpdated;
        _simConnect.ConnectionStatusChanged += OnSimConnectionChanged;
    }

    public void Initialize()
    {
        if (!_settings.Settings.DiscordPresenceEnabled)
            return;

        var clientId = _settings.Settings.DiscordClientId;
        if (string.IsNullOrWhiteSpace(clientId) || clientId == "YOUR_DISCORD_CLIENT_ID")
        {
            Log.Warning("[Discord] DiscordClientId not configured - Rich Presence disabled");
            return;
        }

        try
        {
            _client = new DiscordRpcClient(clientId);
            _client.OnReady += (_, msg) => Log.Information("[Discord] Connected as {User}", msg.User.Username);
            _client.OnError += (_, msg) => Log.Warning("[Discord] RPC error {Code}: {Message}", msg.Code, msg.Message);
            _client.OnClose += (_, _) => Log.Information("[Discord] RPC connection closed");
            _client.Initialize();

            SetIdlePresence();
            Log.Information("[Discord] Rich Presence initialized");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Discord] Failed to initialize Rich Presence");
            _client = null;
        }
    }

    private void OnSessionStateChanged(object? sender, FlightSessionState state)
    {
        UpdatePresence(force: true);
    }

    private void OnTelemetryUpdated(object? sender, TelemetrySnapshot snap)
    {
        if ((DateTime.UtcNow - _lastPresenceUpdate).TotalSeconds < UPDATE_INTERVAL_SECONDS)
            return;

        UpdatePresence(force: false);
    }

    private void OnSimConnectionChanged(object? sender, SimConnectionStatus status)
    {
        UpdatePresence(force: true);
    }

    private void UpdatePresence(bool force)
    {
        if (_client == null || !_client.IsInitialized)
            return;

        if (!force && (DateTime.UtcNow - _lastPresenceUpdate).TotalSeconds < UPDATE_INTERVAL_SECONDS)
            return;

        _lastPresenceUpdate = DateTime.UtcNow;

        var state = _session.State;
        var flight = _session.CurrentFlight;
        var snap = _session.LatestTelemetry;

        if (state == FlightSessionState.Idle || flight == null)
        {
            SetIdlePresence();
            return;
        }

        _client.SetPresence(new RichPresence
        {
            Details = BuildDetailsLine(flight),
            State = BuildStateLine(state, snap, flight),
            Timestamps = BuildStartTimestamp(state, flight),
            Assets = new Assets
            {
                LargeImageKey = "aviates_logo",
                LargeImageText = "Aviates Air Tracker",
                SmallImageKey = PhaseImageKey(snap?.Phase),
                SmallImageText = BuildSmallImageText(snap),
            }
        });
    }

    private void SetIdlePresence()
    {
        var state = _simConnect.IsConnected ? "Sim Connected · Standing by" : "Awaiting MSFS...";

        _client?.SetPresence(new RichPresence
        {
            Details = "Aviates Air Tracker",
            State = state,
            Assets = new Assets
            {
                LargeImageKey = "aviates_logo",
                LargeImageText = "Aviates Air Tracker",
            }
        });
    }

    private static string BuildDetailsLine(FlightRecord flight)
    {
        var dep = string.IsNullOrWhiteSpace(flight.DepartureICAO) ? "????" : flight.DepartureICAO;
        var arr = string.IsNullOrWhiteSpace(flight.ArrivalICAO) ? "????" : flight.ArrivalICAO;
        var acType = string.IsNullOrWhiteSpace(flight.AircraftType) ? "" : $" · {flight.AircraftType}";
        return $"{dep} -> {arr}{acType}";
    }

    private static string BuildStateLine(FlightSessionState state, TelemetrySnapshot? snap, FlightRecord? flight)
    {
        return state switch
        {
            FlightSessionState.PreFlight => BuildPreFlightState(flight),
            FlightSessionState.Taxiing => BuildTaxiingState(flight),
            FlightSessionState.Airborne => BuildAirborneState(snap, flight),
            FlightSessionState.OnApproach => BuildApproachState(snap, flight),
            FlightSessionState.Landed => BuildLandedState(flight),
            FlightSessionState.Complete => "Flight complete",
            _ => "Awaiting flight...",
        };
    }

    private static string BuildPreFlightState(FlightRecord? flight)
    {
        var dep = flight?.DepartureICAO;
        return string.IsNullOrWhiteSpace(dep) ? "Engines running · Pre-flight" : $"Pre-flight at {dep}";
    }

    private static string BuildTaxiingState(FlightRecord? flight)
    {
        var dep = flight?.DepartureICAO;
        return string.IsNullOrWhiteSpace(dep) ? "Taxiing" : $"Taxiing at {dep}";
    }

    private static string BuildAirborneState(TelemetrySnapshot? snap, FlightRecord? flight)
    {
        if (snap == null)
            return "Airborne";

        var fl = (int)(snap.AltitudePressure / 100);
        var phase = snap.Phase switch
        {
            FlightPhase.Takeoff or FlightPhase.InitialClimb => "Departing",
            FlightPhase.Climb => "Climbing",
            FlightPhase.Cruise => "Cruising",
            FlightPhase.TopOfDescent or FlightPhase.Descent => "Descending",
            _ => "Airborne",
        };

        if (snap.Phase is FlightPhase.Takeoff or FlightPhase.InitialClimb)
        {
            var dep = flight?.DepartureICAO;
            if (!string.IsNullOrWhiteSpace(dep))
                return $"Departing {dep} · FL{fl:D3}";
        }

        if (snap.Phase == FlightPhase.Cruise)
        {
            var dep = flight?.DepartureICAO;
            var arr = flight?.ArrivalICAO;
            var route = (!string.IsNullOrWhiteSpace(dep) && !string.IsNullOrWhiteSpace(arr))
                ? $" · {dep}->{arr}"
                : "";
            var region = GetGeoRegion(snap.Latitude, snap.Longitude);
            var regionStr = string.IsNullOrWhiteSpace(region) ? "" : $" · {region}";
            return $"Cruising FL{fl:D3}{route}{regionStr}";
        }

        return $"{phase} · FL{fl:D3}";
    }

    private static string BuildApproachState(TelemetrySnapshot? snap, FlightRecord? flight)
    {
        if (snap == null)
            return "On Approach";

        var agl = (int)snap.AltitudeAGL;
        var arr = flight?.ArrivalICAO;
        var rwy = flight?.PlannedFlight?.ArrivalRunway;
        var dest = !string.IsNullOrWhiteSpace(arr) ? arr : null;
        var rwyStr = !string.IsNullOrWhiteSpace(rwy) ? $" Rwy {rwy}" : "";

        if (snap.Phase == FlightPhase.Landing)
        {
            return dest != null
                ? $"Landing {dest}{rwyStr} · {agl} ft AGL"
                : $"Landing · {agl} ft AGL";
        }

        return dest != null
            ? $"Approach {dest}{rwyStr} · {agl} ft AGL"
            : $"On Approach · {agl} ft AGL";
    }

    private static string BuildLandedState(FlightRecord? flight)
    {
        var arr = flight?.ArrivalICAO;
        var rwy = flight?.PrimaryLanding?.RunwayIdentifier ?? flight?.PlannedFlight?.ArrivalRunway;
        var rwyStr = !string.IsNullOrWhiteSpace(rwy) ? $" Rwy {rwy}" : "";
        return !string.IsNullOrWhiteSpace(arr)
            ? $"Landed at {arr}{rwyStr} · Taxiing in"
            : "Landed · Taxiing in";
    }

    private static string GetGeoRegion(double lat, double lon)
    {
        if (lat is > 15 and < 70 && lon is > -75 and < -10) return "North Atlantic";
        if (lat is > -60 and < 15 && lon is > -60 and < 20) return "South Atlantic";
        if (lat is > 15 and < 70 && (lon > 130 || lon < -100)) return "North Pacific";
        if (lat is > -60 and < 15 && (lon > 130 || lon < -100)) return "South Pacific";
        if (lat is > -60 and < 25 && lon is > 20 and < 110) return "Indian Ocean";
        if (lat is > 30 and < 47 && lon is > -6 and < 37) return "Mediterranean";
        if (lat is > 35 and < 72 && lon is > -12 and < 45) return "Europe";
        if (lat is > 15 and < 72 && lon is > -170 and < -50) return "North America";
        if (lat is > -60 and < 15 && lon is > -85 and < -35) return "South America";
        if (lat is > -35 and < 37 && lon is > -20 and < 55) return "Africa";
        if (lat is > 0 and < 72 && lon is > 45 and < 145) return "Asia";
        if (lat is > -50 and < 0 && lon is > 110 and < 180) return "Australia";
        if (lat > 72) return "Arctic";
        if (lat < -60) return "Antarctic";
        return "";
    }

    private static Timestamps? BuildStartTimestamp(FlightSessionState state, FlightRecord flight)
    {
        if (state == FlightSessionState.Idle || state == FlightSessionState.Complete)
            return null;

        var blockOut = flight.BlockOutTime;
        if (blockOut == default)
            return Timestamps.Now;

        return new Timestamps(blockOut.ToUniversalTime());
    }

    private static string BuildSmallImageText(TelemetrySnapshot? snap)
    {
        var label = PhaseLabel(snap?.Phase) ?? "Airborne";
        if (snap == null)
            return label;

        var latDir = snap.Latitude >= 0 ? "N" : "S";
        var lonDir = snap.Longitude >= 0 ? "E" : "W";
        return $"{label} · {latDir}{Math.Abs(snap.Latitude):F2}° {lonDir}{Math.Abs(snap.Longitude):F2}°";
    }

    private static string? PhaseImageKey(FlightPhase? phase) => phase switch
    {
        FlightPhase.Cruise => "phase_cruise",
        FlightPhase.Climb or FlightPhase.InitialClimb => "phase_climb",
        FlightPhase.Descent or FlightPhase.TopOfDescent => "phase_descent",
        FlightPhase.Approach or FlightPhase.FinalApproach => "phase_approach",
        FlightPhase.Landing or FlightPhase.Rollout => "phase_landing",
        FlightPhase.Takeoff => "phase_takeoff",
        _ => null,
    };

    private static string? PhaseLabel(FlightPhase? phase) => phase switch
    {
        FlightPhase.Cruise => "Cruise",
        FlightPhase.Climb or FlightPhase.InitialClimb => "Climb",
        FlightPhase.Descent or FlightPhase.TopOfDescent => "Descent",
        FlightPhase.Approach or FlightPhase.FinalApproach => "Approach",
        FlightPhase.Landing or FlightPhase.Rollout => "Landing",
        FlightPhase.Takeoff => "Takeoff",
        _ => null,
    };

    public void Dispose()
    {
        _session.SessionStateChanged -= OnSessionStateChanged;
        _session.TelemetryUpdated -= OnTelemetryUpdated;
        _simConnect.ConnectionStatusChanged -= OnSimConnectionChanged;

        if (_client != null)
        {
            _client.ClearPresence();
            _client.Dispose();
            _client = null;
        }
    }
}
