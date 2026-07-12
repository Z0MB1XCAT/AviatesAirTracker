using AviatesAirTracker.Core.SimConnect;
using Serilog;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AviatesAirTracker.Services;

// ============================================================
// DISPATCH RADIO SERVICE
//
// Watches COM1 frequency on the existing 20Hz telemetry tick.
// When tuned to the dispatch VHF frequency, opens a WebSocket
// to the dispatch backend, reports position once/sec, and
// exposes the clear/degraded/acars_only state pushed back.
//
// Mirrors AcarsPositionService in structure: subscribes to
// FlightSessionManager.TelemetryUpdated and throttles outbound
// calls with an Interlocked tick counter.
// ============================================================

public enum DispatchCommsState { Untuned, Clear, Degraded, AcarsOnly }

public class DispatchRadioService : IDisposable
{
    private const double VhfFrequencyMhz = 123.575;
    private const double FrequencyEpsilon = 0.001;
    private const int PositionSendIntervalSeconds = 1;
    private const int ReconnectDelaySeconds = 5;

    private readonly FlightSessionManager _session;
    private readonly SettingsService _settings;

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _connectionLoopTask;
    private volatile bool _tuned;
    private long _lastSendTicks;

    public DispatchCommsState CommsState { get; private set; } = DispatchCommsState.Untuned;
    public double? DistanceNm { get; private set; }

    public event EventHandler<DispatchCommsState>? CommsStateChanged;

    public DispatchRadioService(FlightSessionManager session, SettingsService settings)
    {
        _session = session;
        _settings = settings;
        _session.TelemetryUpdated += OnTelemetryUpdated;
        Log.Information("[DispatchRadio] Service initialized, VHF={Freq}", VhfFrequencyMhz);
    }

    private void OnTelemetryUpdated(object? sender, TelemetrySnapshot snap)
    {
        bool isTuned = Math.Abs(snap.Raw.COM1Frequency - VhfFrequencyMhz) < FrequencyEpsilon;

        if (isTuned && !_tuned)
        {
            _tuned = true;
            _cts = new CancellationTokenSource();
            _connectionLoopTask = Task.Run(() => MaintainConnectionAsync(_cts.Token));
        }
        else if (!isTuned && _tuned)
        {
            _tuned = false;
            _ = DisconnectAsync();
        }

        if (!_tuned) return;

        var now = DateTime.UtcNow;
        if ((now.Ticks - Interlocked.Read(ref _lastSendTicks)) < TimeSpan.TicksPerSecond * PositionSendIntervalSeconds)
            return;
        Interlocked.Exchange(ref _lastSendTicks, now.Ticks);

        _ = SendPositionAsync(snap.Latitude, snap.Longitude);
    }

    private async Task MaintainConnectionAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _tuned)
        {
            try
            {
                var url = _settings.Settings.DispatchBackendUrl;
                if (string.IsNullOrWhiteSpace(url))
                {
                    await Task.Delay(TimeSpan.FromSeconds(ReconnectDelaySeconds), token);
                    continue;
                }

                _socket = new ClientWebSocket();
                await _socket.ConnectAsync(new Uri($"{url}/pilot"), token);
                Log.Information("[DispatchRadio] Connected to dispatch backend");
                await ReceiveLoopAsync(_socket, token);
            }
            catch (OperationCanceledException)
            {
                // Expected when untuning or disposing
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[DispatchRadio] Connection error, retrying in {Delay}s", ReconnectDelaySeconds);
            }

            if (!token.IsCancellationRequested && _tuned)
                await Task.Delay(TimeSpan.FromSeconds(ReconnectDelaySeconds), token);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[4096];
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) break;

            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            HandleServerMessage(json);
        }
    }

    private void HandleServerMessage(string json)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<DispatchStateMessage>(json);
            if (msg == null) return;

            CommsState = msg.State switch
            {
                "clear" => DispatchCommsState.Clear,
                "degraded" => DispatchCommsState.Degraded,
                "acars_only" => DispatchCommsState.AcarsOnly,
                _ => CommsState,
            };
            DistanceNm = msg.DistanceNm;
            CommsStateChanged?.Invoke(this, CommsState);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[DispatchRadio] Failed to parse server message: {Json}", json);
        }
    }

    private async Task SendPositionAsync(double lat, double lon)
    {
        if (_socket?.State != WebSocketState.Open) return;

        var callsign = string.IsNullOrEmpty(_session.CurrentFlight?.Callsign)
            ? _settings.Settings.PilotId
            : _session.CurrentFlight!.Callsign;

        var payload = new DispatchPositionMessage
        {
            PilotId = _settings.Settings.PilotId,
            Callsign = callsign,
            Lat = lat,
            Lon = lon,
            Freq = VhfFrequencyMhz.ToString("F3"),
        };

        try
        {
            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[DispatchRadio] Send failed (non-critical, will retry next tick)");
        }
    }

    private async Task DisconnectAsync()
    {
        CommsState = DispatchCommsState.Untuned;
        DistanceNm = null;
        CommsStateChanged?.Invoke(this, CommsState);

        _cts?.Cancel();

        if (_socket?.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "untuned", CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[DispatchRadio] Close error (non-critical)");
            }
        }

        _socket?.Dispose();
        _socket = null;
    }

    public void Dispose()
    {
        _session.TelemetryUpdated -= OnTelemetryUpdated;
        _cts?.Cancel();
        _socket?.Dispose();
        Log.Debug("[DispatchRadio] Disposed");
    }
}

internal class DispatchPositionMessage
{
    [JsonPropertyName("pilotId")]  public string PilotId  { get; set; } = "";
    [JsonPropertyName("callsign")] public string Callsign { get; set; } = "";
    [JsonPropertyName("lat")]      public double Lat      { get; set; }
    [JsonPropertyName("lon")]      public double Lon       { get; set; }
    [JsonPropertyName("freq")]     public string Freq      { get; set; } = "";
}

internal class DispatchStateMessage
{
    [JsonPropertyName("state")]      public string State      { get; set; } = "";
    [JsonPropertyName("distanceNm")] public double DistanceNm  { get; set; }
}
