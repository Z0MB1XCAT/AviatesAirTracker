using AviatesAirTracker.Services;
using Serilog;

namespace AviatesAirTracker.Services.Voice;

// ============================================================
// VOICE ORCHESTRATOR
//
// The direct native replacement for wwwroot/js/voice.js's role as bridged
// from Shared/MainLayout.razor -- the one and only listener for
// DispatchRadioService's VoiceChannelConnectRequested/
// VoiceChannelDisconnectRequested/DistanceUpdated/PttPressed/PttReleased
// events (all five previously had zero listeners anywhere in the native
// app). Drives VoiceChannelClient (the /voice wire protocol) and
// VoiceAudioEngine (WASAPI capture/render) using OpusCodec/WebmMuxer/
// WebmDemuxer/RadioDegradationEffect for the encode/mux and
// demux/decode/degrade steps in between.
//
// A pure background service -- unlike a Page, it has no UI to marshal onto,
// so unlike ViewModel property updates elsewhere in this app there is no
// DispatcherQueue/IUiDispatcher involvement here. DispatchRadioService's
// events, VoiceAudioEngine.PcmCaptured, and VoiceChannelClient.AudioReceived
// each fire on their own non-UI background threads (confirmed by each
// class's own doc comments) -- every handler below is wrapped in try/catch
// (log + swallow), matching the established convention from
// DispatchRadioService.OnPttStateChanged and MessagesPage.xaml.cs, so an
// exception anywhere in this pipeline can never crash a background
// event-raiser thread.
//
// PTT-PRESS ENCODE/SEND BATCHING DECISION -- whole transmission, not
// incremental ~100ms sends:
//
// WebmMuxer.Mux() is a batch API that returns one fully self-contained WebM
// document (its own EBML header + Segment/Info/Tracks) per call, not a
// streaming/incremental writer (see WebmMuxer.cs's own class remarks on why
// -- NEbml 1.1.0.5 rejects an unknown-size streaming Segment). Calling
// Mux() repeatedly during one PTT press (e.g. every ~100ms as frames
// accumulate) and sending each result as a separate SendChunk would mean
// the receive side's per-sender buffer (VoiceChannelClient's own
// ReceiveSession) ends up concatenating several independent, complete EBML
// documents back-to-back into one byte array before ever calling
// WebmDemuxer.Demux on the result at voice_end. WebmDemuxer was only
// verified against a single self-contained document (its own doc comment:
// "a deliberately narrow reader matching what this app ever produces or
// receives") -- multi-document concatenation is technically plausible
// (EBML permits sequential root-level elements) but was never spiked or
// tested, so it is an unnecessary, unverified risk to take on here.
//
// Buffering the WHOLE transmission's Opus frames in memory during the PTT
// press and calling Mux() exactly ONCE at PttReleased sidesteps that risk
// entirely -- there is only ever one EBML document per transmission, sent
// as one SendChunk. This also matches the design spec's explicit scope
// decision most directly: the wire protocol's receive side already buffers
// and plays back a whole clip on voice_end (or the 2000ms lost-marker
// timeout), never streaming/incremental, and the spec explicitly calls out
// "does not attempt lower-latency streaming playback ... not an
// improvement" as out of scope for this phase. A single PTT transmission is
// realistically a few seconds of 20ms Opus frames (well under a second of
// muxing/encoding work), so the memory/latency cost of buffering the whole
// thing is negligible against that already-buffered receive model.
// WebmMuxer's own internal Cluster grouping (FramesPerCluster = 5, ~100ms
// each) still applies within that single Mux() call, so the resulting WebM
// document's internal structure matches MediaRecorder's own flush cadence
// exactly -- only the number of times SendChunk is called differs, not the
// container's internal shape.
// ============================================================
public sealed class VoiceOrchestrator : IDisposable
{
    private readonly DispatchRadioService _dispatchRadio;
    private readonly VoiceAudioEngine _audioEngine;
    private readonly VoiceChannelClient _channelClient;

    // Two separate OpusCodec instances -- one for the capture/encode side, one for
    // the receive/decode side. OpusCodec's own constructor always builds both an
    // encoder and a decoder together (there's no encoder-only/decoder-only factory
    // surface), so this is a small amount of redundant decoder/encoder state on
    // each side, but it keeps the encode path (fed serially by
    // VoiceAudioEngine.PcmCaptured, always from NAudio's own single capture
    // callback thread) and the decode path (fed serially by
    // VoiceChannelClient.AudioReceived, from either its receive-loop thread or a
    // Timer callback thread) from ever touching shared Concentus encoder/decoder
    // state across those two independent threads.
    private readonly OpusCodec _encodeCodec = new();
    private readonly OpusCodec _decodeCodec = new();

    // Guards _transmitting/_pendingOpusFrames together as one composite piece of
    // state (a plain bool + List<byte[]> pairing can't be updated atomically with
    // Interlocked alone, unlike DispatchRadioService's own single-volatile-bool
    // _tuned flag) -- same lock-object-per-piece-of-state convention
    // VoiceAudioEngine (_captureLock/_renderLock) and VoiceChannelClient
    // (_lock/_sessionsLock) already established in this same Services/Voice
    // folder.
    private readonly object _txLock = new();
    private bool _transmitting;
    private List<byte[]>? _pendingOpusFrames;

    // Guards the single double field fed by DistanceUpdated (WebSocket receive-loop
    // thread) and read by OnAudioReceived (a different background thread) --
    // trivial state, but still guarded rather than left as a bare non-volatile
    // field, matching the same care level as every other piece of cross-thread
    // state in this class.
    private readonly object _distanceLock = new();
    private double _latestDistanceNm;

    private bool _disposed;

    public VoiceOrchestrator(DispatchRadioService dispatchRadio, VoiceAudioEngine audioEngine, VoiceChannelClient channelClient)
    {
        _dispatchRadio = dispatchRadio;
        _audioEngine = audioEngine;
        _channelClient = channelClient;

        _dispatchRadio.VoiceChannelConnectRequested += OnVoiceChannelConnectRequested;
        _dispatchRadio.VoiceChannelDisconnectRequested += OnVoiceChannelDisconnectRequested;
        _dispatchRadio.DistanceUpdated += OnDistanceUpdated;
        _dispatchRadio.PttPressed += OnPttPressed;
        _dispatchRadio.PttReleased += OnPttReleased;

        _audioEngine.PcmCaptured += OnPcmCaptured;
        _channelClient.AudioReceived += OnAudioReceived;

        Log.Information("[VoiceOrchestrator] Initialized -- listening to DispatchRadioService voice events");
    }

    // ── Connect / disconnect ──

    // VoiceChannelConnectRequested carries pilotId+backendUrl directly in its own
    // VoiceChannelInfo payload (DispatchRadioService.OnTelemetryUpdated constructs
    // it from _settings.Settings.PilotId/DispatchBackendUrl at the moment the radio
    // tunes in) -- this orchestrator reads that payload, it does NOT reach into
    // SettingsService itself. That keeps this class agnostic of where the pilotId/
    // backend URL actually come from, same as DispatchRadioService's own event
    // already decided that for us.
    private void OnVoiceChannelConnectRequested(object? sender, VoiceChannelInfo info)
    {
        try
        {
            _ = _channelClient.ConnectAsync(info.PilotId, info.BackendUrl);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[VoiceOrchestrator] Failed to handle VoiceChannelConnectRequested");
        }
    }

    private void OnVoiceChannelDisconnectRequested(object? sender, EventArgs e)
    {
        try
        {
            _ = _channelClient.DisconnectAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[VoiceOrchestrator] Failed to handle VoiceChannelDisconnectRequested");
        }
    }

    // ── Distance tracking (feeds the receive path's degradation effect) ──

    private void OnDistanceUpdated(object? sender, double distanceNm)
    {
        try
        {
            lock (_distanceLock)
            {
                _latestDistanceNm = distanceNm;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[VoiceOrchestrator] Failed to handle DistanceUpdated");
        }
    }

    // ── Transmit path (PTT press → capture+encode+buffer → mux+send on release) ──

    private void OnPttPressed(object? sender, EventArgs e)
    {
        try
        {
            lock (_txLock)
            {
                _pendingOpusFrames = new List<byte[]>();
                _transmitting = true;
            }

            _audioEngine.StartCapture();
            _ = _channelClient.SendVoiceStart();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[VoiceOrchestrator] Failed to handle PttPressed");
        }
    }

    // Fires on VoiceAudioEngine's own WASAPI capture callback thread -- one 20ms/
    // 960-sample frame at a time, already resampled/downmixed to 48000Hz mono by
    // VoiceAudioEngine's CaptureConverter, ready for OpusCodec.Encode as-is.
    private void OnPcmCaptured(short[] pcmFrame)
    {
        try
        {
            bool transmitting;
            lock (_txLock)
            {
                transmitting = _transmitting && _pendingOpusFrames != null;
            }
            if (!transmitting) return;

            var encoded = _encodeCodec.Encode(pcmFrame);

            lock (_txLock)
            {
                // Re-check inside the lock: PttReleased may have flipped _transmitting/
                // swapped _pendingOpusFrames to null between the check above and here.
                if (_transmitting && _pendingOpusFrames != null)
                    _pendingOpusFrames.Add(encoded);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[VoiceOrchestrator] Failed to encode a captured PCM frame");
        }
    }

    private void OnPttReleased(object? sender, EventArgs e)
    {
        try
        {
            _audioEngine.StopCapture();

            List<byte[]>? frames;
            lock (_txLock)
            {
                frames = _pendingOpusFrames;
                _pendingOpusFrames = null;
                _transmitting = false;
            }

            // Mux+send the whole transmission in one shot -- see this class's own
            // header comment for why (avoids concatenating multiple independent EBML
            // documents on the wire, an untested WebmDemuxer edge case).
            if (frames is { Count: > 0 })
            {
                var blob = WebmMuxer.Mux(frames);
                _ = _channelClient.SendChunk(blob);
            }

            _ = _channelClient.SendVoiceEnd();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[VoiceOrchestrator] Failed to handle PttReleased");
        }
    }

    // ── Receive path (WebM blob → Opus packets → PCM → degraded → speaker) ──

    // Fires on whichever thread VoiceChannelClient completes a receive session on
    // (its WebSocket receive-loop background task for a voice_end-triggered play,
    // or a System.Threading.Timer callback thread for a lost-marker-timeout one) --
    // never the UI thread, matching VoiceChannelClient's own doc comment on
    // AudioReceived.
    private void OnAudioReceived(string senderId, byte[] webmBlob)
    {
        try
        {
            var opusPackets = WebmDemuxer.Demux(webmBlob);
            if (opusPackets.Count == 0) return;

            var pcm = new List<short>(opusPackets.Count * OpusCodec.FrameSamples);
            foreach (var packet in opusPackets)
            {
                pcm.AddRange(_decodeCodec.Decode(packet));
            }

            double distanceNm;
            lock (_distanceLock)
            {
                distanceNm = _latestDistanceNm;
            }

            var degraded = RadioDegradationEffect.Apply(pcm.ToArray(), distanceNm);
            _audioEngine.PlayPcm(degraded);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[VoiceOrchestrator] Failed to process received audio from sender {SenderId}", senderId);
        }
    }

    // ── Disposal ──

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _dispatchRadio.VoiceChannelConnectRequested -= OnVoiceChannelConnectRequested;
        _dispatchRadio.VoiceChannelDisconnectRequested -= OnVoiceChannelDisconnectRequested;
        _dispatchRadio.DistanceUpdated -= OnDistanceUpdated;
        _dispatchRadio.PttPressed -= OnPttPressed;
        _dispatchRadio.PttReleased -= OnPttReleased;

        _audioEngine.PcmCaptured -= OnPcmCaptured;
        _channelClient.AudioReceived -= OnAudioReceived;

        _encodeCodec.Dispose();
        _decodeCodec.Dispose();

        Log.Debug("[VoiceOrchestrator] Disposed");
    }
}
