using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace AviatesAirTracker.Services.Voice;

// ============================================================
// VOICE CHANNEL CLIENT
//
// The /voice WebSocket wire-protocol client — the native replacement for
// wwwroot/js/voice.js's own WebSocket handling (connect/reconnect, envelope
// encode/decode, JSON markers, per-sender receive buffering). This class is
// deliberately just the wire protocol: it does NOT know about PTT, NAudio,
// Opus, or WebM — Task 5's VoiceOrchestrator drives it (SendChunk fed muxed
// blobs from WebmMuxer, AudioReceived routed into WebmDemuxer/OpusCodec/
// RadioDegradationEffect/VoiceAudioEngine.PlayPcm). Not called from anywhere
// in the app yet as of this task.
//
// MIRRORS DispatchRadioService's OWN ClientWebSocket HANDLING (its /pilot
// socket) for connection-lifecycle care level: a reconnect loop bounded by a
// fixed delay, a CancellationTokenSource per connection lifetime, a bounded
// close handshake, defensive try/catch around send/receive. But this is an
// INDEPENDENT socket and reconnect loop, not shared with /pilot's — matching
// voice.js's own separate 5-second reconnect timer for /voice (RECONNECT_
// DELAY_MS in that file). DispatchRadioService's loop is driven by tune/
// untune (repeated start/stop over the app's lifetime, hence its generation-
// counter dance to handle a fast retune racing a still-closing old socket);
// this class's loop instead runs for exactly one ConnectAsync/DisconnectAsync
// lifetime at a time — a second ConnectAsync call while already connected is
// a no-op (matching voice.js's `if (shouldStayConnected) return;` guard), so
// there's never more than one live loop task to coordinate and no generation
// counter is needed. Field access (_socket, _cts) is still guarded by a lock
// so Connect/Disconnect calls from different threads can't race each other.
//
// WIRE PROTOCOL (confirmed by reading wwwroot/js/voice.js directly, not
// paraphrased from the design spec):
//   - Binary envelope (decodeVoiceFrame / the ondataavailable handler):
//       [0]        uint8  idLen
//       [1..idLen] bytes  senderId (UTF-8)
//       [1+idLen..] bytes  audio/webm;codecs=opus chunk
//   - JSON text-frame markers (ws.send(JSON.stringify(...)))):
//       {"type":"voice_start","from":<pilotId>}   sent once, before the first chunk
//       {"type":"voice_end","from":<pilotId>}     sent once, after the last chunk
//   - Receive-side session model (activeSessions Map, getOrCreateSession/
//     playSession): chunks for a sender-id are buffered until either a
//     voice_end marker arrives for that sender, OR SESSION_TIMEOUT_MS (2000,
//     read directly from voice.js's own constant — not a secondhand citation)
//     elapses with no voice_end. Either path plays/delivers the full
//     concatenation as one clip — this is buffered whole-clip delivery, not
//     streaming, matching voice.js exactly.
// ============================================================
public sealed class VoiceChannelClient : IDisposable
{
    /// <summary>Matches voice.js's RECONNECT_DELAY_MS (5000ms), independent of DispatchRadioService's own /pilot reconnect delay.</summary>
    private const int ReconnectDelaySeconds = 5;

    /// <summary>Matches voice.js's SESSION_TIMEOUT_MS exactly — confirmed by reading the constant directly.</summary>
    private const int SessionTimeoutMs = 2000;

    private readonly object _lock = new();
    private readonly object _sessionsLock = new();
    private readonly Dictionary<string, ReceiveSession> _sessions = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _connectionLoopTask;
    private volatile bool _shouldStayConnected;
    private string? _pilotId;
    private bool _disposed;

    /// <summary>
    /// Fires once per completed receive session — either on that sender's voice_end
    /// marker, or after the SESSION_TIMEOUT_MS lost-marker timeout. Carries the FULL
    /// concatenated WebM bytes for that sender's transmission (ready for a later
    /// task's WebmDemuxer -> OpusCodec.Decode -> RadioDegradationEffect -> playback
    /// chain). Fires on whichever thread completes the session — the WebSocket
    /// receive loop's background thread for a voice_end-triggered play, or a
    /// System.Threading.Timer callback thread for a timeout-triggered one. Never the
    /// UI thread — subscribers must marshal if they touch UI state.
    /// </summary>
    public event Action<string, byte[]>? AudioReceived;

    /// <summary>True once ConnectAsync has been called and DisconnectAsync/Dispose hasn't torn it down yet — does not imply the socket is currently open (the reconnect loop may be between attempts).</summary>
    public bool IsConnected => _shouldStayConnected;

    /// <summary>
    /// Begins the connect/reconnect lifecycle for {backendUrl}/voice?pilotId={pilotId}.
    /// Idempotent while already connected/connecting — a second call is a no-op,
    /// matching voice.js's own `connect()` guard. Does not await the actual socket
    /// handshake (mirrors DispatchRadioService's own fire-and-forget
    /// Task.Run(MaintainConnectionAsync) pattern) — the reconnect loop runs in the
    /// background and IsConnected reflects the intent, not the live socket state.
    /// </summary>
    public Task ConnectAsync(string pilotId, string backendUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pilotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(backendUrl);

        lock (_lock)
        {
            if (_disposed || _shouldStayConnected)
                return Task.CompletedTask;

            _pilotId = pilotId;
            _shouldStayConnected = true;

            var cts = new CancellationTokenSource();
            _cts = cts;
            _connectionLoopTask = Task.Run(() => MaintainConnectionAsync(pilotId, backendUrl, cts.Token));
        }

        Log.Information("[VoiceChannelClient] ConnectAsync requested, pilotId={PilotId}", pilotId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Ends the connect/reconnect lifecycle, closes the socket (bounded, same 2s
    /// pattern as DispatchRadioService.DisconnectAsync), and clears any in-flight
    /// receive sessions WITHOUT delivering them via AudioReceived — matches
    /// voice.js's own disconnect(), which clears activeSessions' timeouts without
    /// calling playSession().
    /// </summary>
    public async Task DisconnectAsync()
    {
        CancellationTokenSource? cts;
        ClientWebSocket? socket;

        lock (_lock)
        {
            _shouldStayConnected = false;
            cts = _cts;
            _cts = null;
            socket = _socket;
            _socket = null;
        }

        cts?.Cancel();

        if (socket?.State == WebSocketState.Open)
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "disconnect", closeCts.Token);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[VoiceChannelClient] Close error (non-critical)");
            }
        }

        socket?.Dispose();
        cts?.Dispose();

        ClearSessions();

        Log.Information("[VoiceChannelClient] Disconnected");
    }

    /// <summary>
    /// Sends one already-muxed WebM/Opus chunk (Task 5's WebmMuxer output) wrapped
    /// in the [idLen][senderId][chunk] envelope, senderId = this client's own
    /// pilotId. No-ops (logged at Debug, non-critical) if the socket isn't currently
    /// open — matches voice.js's ondataavailable handler silently dropping a chunk
    /// when ws isn't OPEN rather than throwing.
    /// </summary>
    public Task SendChunk(byte[] webmChunk)
    {
        ArgumentNullException.ThrowIfNull(webmChunk);

        var pilotId = _pilotId;
        if (string.IsNullOrEmpty(pilotId))
        {
            Log.Debug("[VoiceChannelClient] SendChunk dropped — not connected");
            return Task.CompletedTask;
        }

        return SendBinaryAsync(EncodeEnvelope(pilotId, webmChunk));
    }

    /// <summary>Sends {"type":"voice_start","from":pilotId} — call once, before the first SendChunk of a transmission.</summary>
    public Task SendVoiceStart() => SendMarkerAsync("voice_start");

    /// <summary>Sends {"type":"voice_end","from":pilotId} — call once, after the last SendChunk of a transmission.</summary>
    public Task SendVoiceEnd() => SendMarkerAsync("voice_end");

    private Task SendMarkerAsync(string type)
    {
        var pilotId = _pilotId;
        if (string.IsNullOrEmpty(pilotId))
        {
            Log.Debug("[VoiceChannelClient] {Type} dropped — not connected", type);
            return Task.CompletedTask;
        }

        return SendTextAsync(BuildMarkerJson(type, pilotId));
    }

    private async Task SendBinaryAsync(byte[] frame)
    {
        var socket = _socket;
        if (socket?.State != WebSocketState.Open) return;

        try
        {
            await socket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[VoiceChannelClient] Send (binary) failed (non-critical, will retry after reconnect)");
        }
    }

    private async Task SendTextAsync(string json)
    {
        var socket = _socket;
        if (socket?.State != WebSocketState.Open) return;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[VoiceChannelClient] Send (text) failed (non-critical, will retry after reconnect)");
        }
    }

    // ── Connection loop ──

    private async Task MaintainConnectionAsync(string pilotId, string backendUrl, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            ClientWebSocket? socket = null;
            try
            {
                var url = $"{backendUrl}/voice?pilotId={Uri.EscapeDataString(pilotId)}";
                socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri(url), token);

                lock (_lock)
                {
                    // A DisconnectAsync racing this handshake already canceled `token` by
                    // the time we get here — don't resurrect _socket after a disconnect.
                    if (!token.IsCancellationRequested)
                        _socket = socket;
                }

                Log.Information("[VoiceChannelClient] Connected to /voice, pilotId={PilotId}", pilotId);
                await ReceiveLoopAsync(socket, token);
            }
            catch (OperationCanceledException)
            {
                // Expected on disconnect/dispose.
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[VoiceChannelClient] Connection error, retrying in {Delay}s", ReconnectDelaySeconds);
            }
            finally
            {
                lock (_lock)
                {
                    if (ReferenceEquals(_socket, socket))
                        _socket = null;
                }
                socket?.Dispose();
            }

            if (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(ReconnectDelaySeconds), token);
                }
                catch (OperationCanceledException)
                {
                    // Expected on disconnect/dispose while waiting to retry.
                }
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[8192];
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            bool closed = false;

            while (true)
            {
                result = await socket.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    closed = true;
                    break;
                }
                ms.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) break;
            }

            if (closed) break;

            if (result.MessageType == WebSocketMessageType.Binary)
                HandleIncomingBinary(ms.ToArray());
            else
                HandleIncomingMarker(Encoding.UTF8.GetString(ms.ToArray()));
        }
    }

    // ── Receive-side per-sender session buffering ──

    private void HandleIncomingBinary(byte[] frame)
    {
        string senderId;
        byte[] payload;
        try
        {
            (senderId, payload) = DecodeEnvelope(frame);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[VoiceChannelClient] Failed to decode incoming voice envelope ({Length} bytes)", frame.Length);
            return;
        }

        lock (_sessionsLock)
        {
            var session = GetOrCreateSessionLocked(senderId);
            session.Chunks.Add(payload);
        }
    }

    private void HandleIncomingMarker(string json)
    {
        if (!TryParseMarker(json, out var type, out var from) || string.IsNullOrEmpty(from))
            return;

        // voice_start needs no action here — GetOrCreateSessionLocked lazily starts a
        // fresh buffer for that sender on the first chunk that follows, matching
        // voice.js's own comment on why voice_start is a no-op on receipt.
        if (type == "voice_end")
            PlaySession(from);
    }

    private ReceiveSession GetOrCreateSessionLocked(string senderId)
    {
        if (!_sessions.TryGetValue(senderId, out var session))
        {
            session = new ReceiveSession();
            _sessions[senderId] = session;
        }

        // Restart the lost-marker timeout on every chunk, same as voice.js's
        // getOrCreateSession clearing/resetting the timeout on each call.
        session.TimeoutTimer?.Dispose();
        session.TimeoutTimer = new Timer(_ => PlaySession(senderId), null, SessionTimeoutMs, Timeout.Infinite);
        return session;
    }

    private void PlaySession(string senderId)
    {
        byte[][] chunks;
        lock (_sessionsLock)
        {
            if (!_sessions.TryGetValue(senderId, out var session)) return;
            _sessions.Remove(senderId);
            session.TimeoutTimer?.Dispose();
            if (session.Chunks.Count == 0) return;
            chunks = session.Chunks.ToArray();
        }

        int totalLength = 0;
        foreach (var chunk in chunks) totalLength += chunk.Length;

        var blob = new byte[totalLength];
        int offset = 0;
        foreach (var chunk in chunks)
        {
            Buffer.BlockCopy(chunk, 0, blob, offset, chunk.Length);
            offset += chunk.Length;
        }

        try
        {
            AudioReceived?.Invoke(senderId, blob);
        }
        catch (Exception ex)
        {
            // Never let a subscriber's exception escape onto the receive loop or a
            // Timer callback thread — same defensive rationale as DispatchRadioService.
            // OnPttStateChanged's own try/catch around subscriber dispatch.
            Log.Warning(ex, "[VoiceChannelClient] AudioReceived subscriber threw for sender {SenderId}", senderId);
        }
    }

    private void ClearSessions()
    {
        lock (_sessionsLock)
        {
            foreach (var session in _sessions.Values)
                session.TimeoutTimer?.Dispose();
            _sessions.Clear();
        }
    }

    private sealed class ReceiveSession
    {
        public List<byte[]> Chunks { get; } = new();
        public Timer? TimeoutTimer;
    }

    // ── Wire-format encode/decode (pure functions — unit tested directly) ──

    /// <summary>
    /// Builds one binary envelope: [1-byte idLen][senderId UTF-8][payload]. Confirmed
    /// byte-for-byte against voice.js's own encode logic (the ondataavailable handler:
    /// `header[0] = idBytes.length; header.set(idBytes, 1); ws.send(new Blob([header, event.data]))`).
    /// </summary>
    internal static byte[] EncodeEnvelope(string senderId, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(senderId);
        ArgumentNullException.ThrowIfNull(payload);

        var idBytes = Encoding.UTF8.GetBytes(senderId);
        if (idBytes.Length > byte.MaxValue)
        {
            throw new ArgumentException(
                $"senderId UTF-8 length {idBytes.Length} exceeds the 1-byte length prefix's 255-byte limit.",
                nameof(senderId));
        }

        var frame = new byte[1 + idBytes.Length + payload.Length];
        frame[0] = (byte)idBytes.Length;
        Buffer.BlockCopy(idBytes, 0, frame, 1, idBytes.Length);
        Buffer.BlockCopy(payload, 0, frame, 1 + idBytes.Length, payload.Length);
        return frame;
    }

    /// <summary>
    /// Parses one binary envelope back into (senderId, payload). Confirmed byte-for-byte
    /// against voice.js's own decodeVoiceFrame: `idLen = bytes[0]; senderId =
    /// decode(bytes.subarray(1, 1+idLen)); chunk = bytes.subarray(1+idLen)`.
    /// </summary>
    internal static (string SenderId, byte[] Payload) DecodeEnvelope(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Length < 1)
            throw new ArgumentException("Frame too short to contain an idLen byte.", nameof(frame));

        int idLen = frame[0];
        if (frame.Length < 1 + idLen)
            throw new ArgumentException("Frame too short to contain the declared senderId length.", nameof(frame));

        var senderId = Encoding.UTF8.GetString(frame, 1, idLen);
        var payload = new byte[frame.Length - 1 - idLen];
        Buffer.BlockCopy(frame, 1 + idLen, payload, 0, payload.Length);
        return (senderId, payload);
    }

    /// <summary>Builds {"type":type,"from":pilotId} — matches voice.js's `JSON.stringify({ type, from: myPilotId })`.</summary>
    internal static string BuildMarkerJson(string type, string from)
        => JsonSerializer.Serialize(new VoiceMarkerMessage { Type = type, From = from });

    /// <summary>Parses a marker JSON text frame. Returns false (rather than throwing) on malformed/non-marker JSON, matching voice.js's own try/catch-and-ignore handleIncomingMarker.</summary>
    internal static bool TryParseMarker(string json, out string? type, out string? from)
    {
        type = null;
        from = null;
        try
        {
            var msg = JsonSerializer.Deserialize<VoiceMarkerMessage>(json);
            if (msg == null || string.IsNullOrEmpty(msg.Type)) return false;
            type = msg.Type;
            from = msg.From;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ── Disposal ──

    /// <summary>
    /// Synchronous, best-effort teardown — matches DispatchRadioService.Dispose's own
    /// pattern (cancel + dispose without awaiting the close handshake). Callers that
    /// want a clean close handshake should call DisconnectAsync first; Dispose is the
    /// safety net for process/service shutdown where an async close can't be awaited.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _shouldStayConnected = false;
            _cts?.Cancel();
            _socket?.Dispose();
            _cts?.Dispose();
            _socket = null;
            _cts = null;
        }

        ClearSessions();
        Log.Debug("[VoiceChannelClient] Disposed");
    }

    internal sealed class VoiceMarkerMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("from")] public string From { get; set; } = "";
    }
}
