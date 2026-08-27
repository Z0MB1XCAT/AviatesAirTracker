using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Serilog;

namespace AviatesAirTracker.Services.Voice;

// ============================================================
// VOICE AUDIO ENGINE
//
// Owns the native WASAPI capture/render devices for the voice pipeline: a
// WasapiCapture for the microphone (PTT-gated -- only actually recording
// while StartCapture()/StopCapture() bracket a press, per DispatchRadioService.
// PttPressed/PttReleased) and a WasapiOut for the speaker (opened once, kept
// running for the engine's lifetime, silence-padded between transmissions --
// see PlaybackQueueSampleProvider's own remarks on why).
//
// Device selection mirrors wwwroot/js/voice.js's setMicrophone(deviceId)/
// setSpeaker(deviceId): a null/empty id means "use the default device",
// matching voice.js's `deviceId || ''` fallback. Device ids are plain
// MMDevice.ID strings (via MMDeviceEnumerator.GetDevice), the same shape
// SettingsPage.xaml.cs already persists into SettingsService.Settings.
// PttMicrophoneDeviceId/PttSpeakerDeviceId -- though note those were captured
// via Windows.Devices.Enumeration.DeviceInformation, a different (WinRT) API
// surface than NAudio's MMDeviceEnumerator; whether their ID strings are
// interchangeable is NOT verified by this task (Task 5's job, when it wires
// SettingsPage's pickers to these methods) and is called out again in this
// task's final report.
//
// DISPOSE-HANG RISK (see the design spec's "Spike findings" and the
// implementation plan's "Before you start" section): a hands-on spike
// observed an apparent hang in Dispose() shortly after StopRecording() on one
// virtual audio device. This class addresses it with two deliberate design
// choices, not a single band-aid:
//
//   1. StopCapture() never disposes the WasapiCapture instance -- it only
//      calls StopRecording() and leaves the (idle) capture object alive for
//      the *next* StartCapture() to reuse. Since PTT press/release happens
//      far more often than a device change or app shutdown, this keeps the
//      flagged StopRecording()-then-Dispose() sequence OFF the hot path
//      entirely; Dispose() on a given WasapiCapture instance is only ever
//      called on the rare paths (SetMicrophone to a different device, or
//      engine shutdown).
//   2. Wherever a stop-then-dispose sequence IS required (device switch,
//      shutdown), it runs on a dedicated background thread bounded by
//      RunWithTimeout's timeout/watchdog rather than blocking the caller
//      (App.xaml.cs's shutdown path in particular cannot tolerate a hang).
//      If the bounded wait expires, the thread is abandoned (left to
//      complete or not on its own) and a warning is logged rather than
//      blocking indefinitely -- the underlying WASAPI COM object may leak
//      until process exit in that case, but the app itself keeps shutting
//      down.
//
// This task's environment could not reproduce the original hang (no live
// desktop audio session was available to drive a real StopRecording() call
// in the first place -- see the class-level "what was/wasn't verified" notes
// in this task's final report), so neither choice above is verified to
// actually prevent it; both are defensible, standard mitigations applied
// regardless.
// ============================================================
public sealed class VoiceAudioEngine : IDisposable
{
    /// <summary>Matches OpusCodec.SampleRateHz -- the pipeline's fixed internal sample rate.</summary>
    public const int TargetSampleRate = OpusCodec.SampleRateHz;

    /// <summary>Matches OpusCodec.Channels -- the pipeline is mono throughout.</summary>
    public const int TargetChannels = OpusCodec.Channels;

    /// <summary>Matches OpusCodec.FrameSamples -- 20ms @ 48000Hz mono.</summary>
    public const int TargetFrameSamples = OpusCodec.FrameSamples;

    // Bound for the stop/dispose watchdog. Generous enough that a healthy device's
    // normal stop/dispose (typically well under 100ms) never trips it, short enough
    // that App.xaml.cs's shutdown path notices a hang instead of waiting on it forever.
    private const int StopDisposeTimeoutMs = 1500;

    // WASAPI shared-mode output latency. Generous on purpose: the wire protocol this
    // pipeline replaces (see the design spec) buffers a whole transmission and plays
    // it as one clip on voice_end/timeout, not a real-time conversational stream, so
    // there is no latency-sensitivity reason to run this tight.
    private const int RenderLatencyMs = 200;

    private readonly object _captureLock = new();
    private readonly object _renderLock = new();

    // Nullable: constructed defensively (see ctor) so a headless/no-audio-subsystem
    // environment degrades to "voice I/O disabled" rather than throwing out of the
    // constructor and taking down whatever registers this as a DI singleton.
    private readonly MMDeviceEnumerator? _deviceEnumerator;

    private string? _microphoneDeviceId;
    private string? _speakerDeviceId;

    private WasapiCapture? _capture;
    private CaptureConverter? _captureConverter;

    private WasapiOut? _renderOut;
    private PlaybackQueueSampleProvider? _playbackQueue;

    private bool _disposed;

    /// <summary>
    /// Raw 20ms/960-sample 48000Hz mono PCM frames as they're captured, ready for
    /// Task 4's OpusCodec.Encode. Fires on WasapiCapture's own capture callback
    /// thread (NAudio's default, non-UI) -- subscribers must not touch UI state
    /// directly from this event, same convention as DispatchRadioService's own
    /// background-thread-sourced events.
    /// </summary>
    public event Action<short[]>? PcmCaptured;

    public bool IsCapturing => _capture?.CaptureState == CaptureState.Capturing;

    public VoiceAudioEngine()
    {
        try
        {
            _deviceEnumerator = new MMDeviceEnumerator();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[VoiceAudioEngine] MMDeviceEnumerator unavailable -- voice audio I/O will be disabled");
            _deviceEnumerator = null;
        }
    }

    // ── Device selection ──

    /// <summary>Null/empty means "use the default capture device", matching voice.js's setMicrophone.</summary>
    public void SetMicrophone(string? deviceId)
    {
        string? normalized = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        if (normalized == _microphoneDeviceId) return;
        _microphoneDeviceId = normalized;

        lock (_captureLock)
        {
            // Tear down an existing (possibly idle, possibly recording) capture
            // instance so the next StartCapture() rebuilds against the newly
            // selected device. This is the rare, not-hot-path dispose call --
            // see the class remarks on the dispose-hang risk for why that split
            // matters.
            if (_capture != null)
                DisposeCaptureInstance();
        }
    }

    /// <summary>Null/empty means "use the default render device", matching voice.js's setSpeaker.</summary>
    public void SetSpeaker(string? deviceId)
    {
        string? normalized = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        if (normalized == _speakerDeviceId) return;
        _speakerDeviceId = normalized;

        lock (_renderLock)
        {
            if (_renderOut != null)
                DisposeRenderInstance();
        }
    }

    /// <summary>Enumerates active capture (microphone) devices. Never throws -- returns an empty list on failure.</summary>
    public IReadOnlyList<VoiceAudioDevice> GetCaptureDevices() => EnumerateDevices(DataFlow.Capture);

    /// <summary>Enumerates active render (speaker) devices. Never throws -- returns an empty list on failure.</summary>
    public IReadOnlyList<VoiceAudioDevice> GetRenderDevices() => EnumerateDevices(DataFlow.Render);

    private List<VoiceAudioDevice> EnumerateDevices(DataFlow flow)
    {
        var result = new List<VoiceAudioDevice>();
        if (_deviceEnumerator == null) return result;

        try
        {
            var collection = _deviceEnumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
            foreach (var device in collection)
            {
                try
                {
                    // .FriendlyName access is exactly where the spike hit
                    // AUDCLNT_E_DEVICE_INVALIDATED on stale/orphaned virtual-audio-
                    // driver entries (DroidCam/VB-Cable/Iriun/Voicemod-style leftovers
                    // are common on real user machines) -- skip that one device,
                    // don't let it take down the whole enumeration.
                    result.Add(new VoiceAudioDevice(device.ID, device.FriendlyName));
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[VoiceAudioEngine] Skipping invalidated {Flow} device", flow);
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[VoiceAudioEngine] Failed to enumerate {Flow} devices", flow);
        }

        return result;
    }

    private MMDevice? ResolveDevice(string? deviceId, DataFlow flow)
    {
        if (_deviceEnumerator == null) return null;

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            try
            {
                return _deviceEnumerator.GetDevice(deviceId);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[VoiceAudioEngine] Configured {Flow} device {DeviceId} unavailable, falling back to default", flow, deviceId);
            }
        }

        try
        {
            // Role.Communications: this is comms/radio audio, not general media
            // playback/capture -- matching Windows' own "default communication
            // device" concept when no explicit device is configured.
            return _deviceEnumerator.GetDefaultAudioEndpoint(flow, Role.Communications);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[VoiceAudioEngine] No default {Flow} device available", flow);
            return null;
        }
    }

    // ── Capture (microphone / PTT) ──

    /// <summary>
    /// Begins microphone capture. Should only be called while PTT is actually
    /// pressed (Task 5 drives this from DispatchRadioService.PttPressed) -- this
    /// class itself doesn't know about PTT state, it just exposes Start/Stop for
    /// the caller to bracket around a press.
    /// </summary>
    public void StartCapture()
    {
        lock (_captureLock)
        {
            if (_disposed) return;

            if (_capture == null && !TryBuildCapture())
                return; // already logged inside TryBuildCapture

            if (_capture!.CaptureState == CaptureState.Capturing) return;

            try
            {
                _captureConverter?.Reset();
                _capture.StartRecording();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[VoiceAudioEngine] StartRecording failed");
            }
        }
    }

    /// <summary>
    /// Stops microphone capture (on PTT release). Deliberately does NOT dispose
    /// the underlying WasapiCapture -- see the class remarks on the dispose-hang
    /// risk for why keeping StopRecording() and Dispose() apart matters.
    /// </summary>
    public void StopCapture()
    {
        lock (_captureLock)
        {
            if (_capture == null) return;

            if (_capture.CaptureState != CaptureState.Capturing)
            {
                _captureConverter?.Reset();
                return;
            }

            try
            {
                var capture = _capture;
                RunWithTimeout(() => capture.StopRecording(), StopDisposeTimeoutMs, "Capture.StopRecording");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[VoiceAudioEngine] StopRecording threw");
            }
            finally
            {
                // Drop any partial (< one frame) leftover audio rather than carrying
                // it into the next transmission -- matches a real PTT release cutting
                // the clip off exactly here.
                _captureConverter?.Reset();
            }
        }
    }

    private bool TryBuildCapture()
    {
        try
        {
            var device = ResolveDevice(_microphoneDeviceId, DataFlow.Capture);
            if (device == null)
            {
                Log.Warning("[VoiceAudioEngine] No capture device available (id={DeviceId})", _microphoneDeviceId ?? "<default>");
                return false;
            }

            var capture = new WasapiCapture(device);
            // Whatever the device actually negotiated (commonly IEEE-float 44100/
            // 48000Hz stereo, but device/driver dependent) -- never assumed to
            // already be 48000Hz mono. CaptureConverter handles the gap.
            var sourceFormat = capture.WaveFormat;
            var converter = new CaptureConverter(sourceFormat, TargetSampleRate, TargetChannels);

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;

            _capture = capture;
            _captureConverter = converter;

            Log.Information(
                "[VoiceAudioEngine] Capture device opened: {Device} ({Rate}Hz, {Channels}ch, {Bits}bit)",
                SafeFriendlyName(device), sourceFormat.SampleRate, sourceFormat.Channels, sourceFormat.BitsPerSample);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[VoiceAudioEngine] Failed to open capture device");
            return false;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            CaptureConverter? converter;
            lock (_captureLock)
            {
                converter = _captureConverter;
            }
            if (converter == null || e.BytesRecorded == 0) return;

            converter.Write(e.Buffer, e.BytesRecorded);
            var frames = converter.DrainFrames(TargetFrameSamples);
            foreach (var frame in frames)
            {
                PcmCaptured?.Invoke(frame);
            }
        }
        catch (Exception ex)
        {
            // Never let an exception here (including one thrown by a downstream
            // PcmCaptured subscriber) escape onto NAudio's capture callback thread --
            // same defensive rationale as DispatchRadioService.OnPttStateChanged's
            // own try/catch around subscriber dispatch.
            Log.Warning(ex, "[VoiceAudioEngine] Error processing captured audio");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            Log.Warning(e.Exception, "[VoiceAudioEngine] Capture stopped due to an error");
    }

    // ── Render (speaker) ──

    /// <summary>
    /// Queues already-decoded/degraded 48000Hz mono PCM16 for playback (Task 4/5's
    /// WebmDemuxer -> OpusCodec.Decode -> RadioDegradationEffect.Apply output).
    /// Opens the render device lazily on first call and keeps it running for the
    /// rest of the engine's lifetime.
    /// </summary>
    public void PlayPcm(short[] pcm)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (pcm.Length == 0 || _disposed) return;

        lock (_renderLock)
        {
            if (_renderOut == null && !TryBuildRender())
                return; // already logged inside TryBuildRender

            _playbackQueue?.Enqueue(pcm);
        }
    }

    private bool TryBuildRender()
    {
        try
        {
            var device = ResolveDevice(_speakerDeviceId, DataFlow.Render);
            if (device == null)
            {
                Log.Warning("[VoiceAudioEngine] No render device available (id={DeviceId})", _speakerDeviceId ?? "<default>");
                return false;
            }

            var mixFormat = device.AudioClient.MixFormat;

            var queue = new PlaybackQueueSampleProvider(TargetSampleRate, TargetChannels);
            ISampleProvider chain = queue;

            if (mixFormat.SampleRate != TargetSampleRate)
                chain = new WdlResamplingSampleProvider(chain, mixFormat.SampleRate);

            if (mixFormat.Channels != TargetChannels)
                chain = new UpmixSampleProvider(chain, mixFormat.Channels);

            // ToWaveProvider() yields an IEEE-float wave provider at the chain's own
            // rate/channel count -- matching WASAPI shared mode's near-universal
            // IEEE-float mix format on modern Windows. If a device's shared-mode mix
            // format is ever something else, WasapiOut.Init below will surface that
            // as an exception, caught and logged here rather than propagating.
            var waveProvider = chain.ToWaveProvider();

            var wasapiOut = new WasapiOut(device, AudioClientShareMode.Shared, true, RenderLatencyMs);
            wasapiOut.Init(waveProvider);
            wasapiOut.Play();

            _renderOut = wasapiOut;
            _playbackQueue = queue;

            Log.Information(
                "[VoiceAudioEngine] Render device opened: {Device} ({Rate}Hz, {Channels}ch)",
                SafeFriendlyName(device), mixFormat.SampleRate, mixFormat.Channels);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[VoiceAudioEngine] Failed to open render device");
            _renderOut = null;
            _playbackQueue = null;
            return false;
        }
    }

    private static string SafeFriendlyName(MMDevice device)
    {
        try
        {
            return device.FriendlyName;
        }
        catch
        {
            return "<unavailable>";
        }
    }

    // ── Disposal ──

    private void DisposeCaptureInstance()
    {
        var capture = _capture;
        _capture = null;
        _captureConverter = null;
        if (capture == null) return;

        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;

        RunWithTimeout(() =>
        {
            if (capture.CaptureState == CaptureState.Capturing)
                capture.StopRecording();
            capture.Dispose();
        }, StopDisposeTimeoutMs, "Capture.StopAndDispose");
    }

    private void DisposeRenderInstance()
    {
        var render = _renderOut;
        _renderOut = null;
        _playbackQueue = null;
        if (render == null) return;

        RunWithTimeout(() =>
        {
            render.Stop();
            render.Dispose();
        }, StopDisposeTimeoutMs, "Render.StopAndDispose");
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a dedicated background thread bounded by
    /// <paramref name="timeoutMs"/>. Used exclusively for the flagged WASAPI stop/
    /// dispose hang risk (see class remarks) -- a plain Thread (not a thread-pool
    /// Task) is used deliberately, so that if the action genuinely never returns,
    /// it leaks one abandoned background thread rather than permanently occupying
    /// (and eventually starving) the shared thread pool for the rest of the
    /// process's lifetime. The thread is marked IsBackground=true so it can never
    /// keep the process alive on its own.
    /// </summary>
    private static void RunWithTimeout(Action action, int timeoutMs, string label)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        })
        {
            IsBackground = true,
            Name = $"VoiceAudioEngine-{label}",
        };
        thread.Start();

        if (!thread.Join(timeoutMs))
        {
            Log.Warning(
                "[VoiceAudioEngine] {Label} did not complete within {TimeoutMs}ms -- abandoning the " +
                "background thread rather than blocking the caller. This mirrors a Dispose()/" +
                "StopRecording() hang the design spec's spike observed on a virtual audio device; " +
                "the underlying WASAPI COM object may leak until process exit, but this call will " +
                "not hang the app.", label, timeoutMs);
            return;
        }

        if (captured != null)
        {
            Log.Warning(captured, "[VoiceAudioEngine] {Label} threw", label);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_captureLock) DisposeCaptureInstance();
        lock (_renderLock) DisposeRenderInstance();

        try
        {
            _deviceEnumerator?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[VoiceAudioEngine] MMDeviceEnumerator dispose error (non-critical)");
        }

        Log.Debug("[VoiceAudioEngine] Disposed");
    }
}

/// <summary>A capture or render device as returned by VoiceAudioEngine.GetCaptureDevices/GetRenderDevices.</summary>
public sealed record VoiceAudioDevice(string Id, string Name);
