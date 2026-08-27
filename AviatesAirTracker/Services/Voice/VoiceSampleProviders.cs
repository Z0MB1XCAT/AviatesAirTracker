using NAudio.Wave;

namespace AviatesAirTracker.Services.Voice;

// ============================================================
// VOICE SAMPLE PROVIDERS
//
// Small, pure NAudio ISampleProvider adapters used by VoiceAudioEngine to bridge
// between whatever format a real WASAPI device negotiates and the pipeline's
// fixed internal format (48000Hz mono, see OpusCodec.SampleRateHz/Channels).
//
// Kept separate from VoiceAudioEngine.cs (which owns the actual WasapiCapture/
// WasapiOut lifecycle) because every class here is pure sample-in/sample-out
// logic with no device/COM/thread dependency -- exactly the kind of thing that
// CAN be unit-tested with synthetic ISampleProvider stand-ins even in an
// environment with zero real audio hardware, unlike VoiceAudioEngine itself.
//
// NAudio 2.2.1's own built-in StereoToMonoSampleProvider only handles exactly
// 2 channels, and there is no built-in inverse (mono -> N channels) at all --
// hence these two small hand-rolled adapters rather than reusing a stock type.
// ============================================================

/// <summary>
/// Downmixes an N-channel float source to mono by averaging all channels per
/// frame. Works for any channel count (stereo, 5.1, etc.), unlike NAudio's
/// built-in <see cref="NAudio.Wave.SampleProviders.StereoToMonoSampleProvider"/>
/// which only accepts exactly 2 channels.
/// </summary>
public sealed class DownmixToMonoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private float[] _sourceBuffer = Array.Empty<float>();

    public DownmixToMonoSampleProvider(ISampleProvider source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _sourceChannels = source.WaveFormat.Channels;
        if (_sourceChannels < 1)
            throw new ArgumentException("Source must have at least one channel.", nameof(source));
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int sourceSamplesNeeded = count * _sourceChannels;
        if (_sourceBuffer.Length < sourceSamplesNeeded)
            _sourceBuffer = new float[sourceSamplesNeeded];

        int sourceRead = _source.Read(_sourceBuffer, 0, sourceSamplesNeeded);
        int framesRead = sourceRead / _sourceChannels;

        for (int frame = 0; frame < framesRead; frame++)
        {
            float sum = 0f;
            int baseIdx = frame * _sourceChannels;
            for (int ch = 0; ch < _sourceChannels; ch++)
                sum += _sourceBuffer[baseIdx + ch];
            buffer[offset + frame] = sum / _sourceChannels;
        }

        return framesRead;
    }
}

/// <summary>
/// Upmixes a mono float source to N channels by duplicating the single sample
/// into every output channel per frame -- the inverse of
/// <see cref="DownmixToMonoSampleProvider"/>, used on the render path when a
/// speaker device's WASAPI mix format is not mono (the overwhelmingly common
/// case in practice).
/// </summary>
public sealed class UpmixSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _outputChannels;
    private float[] _monoBuffer = Array.Empty<float>();

    public UpmixSampleProvider(ISampleProvider source, int outputChannels)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.WaveFormat.Channels != 1)
            throw new ArgumentException("Source must be mono.", nameof(source));
        if (outputChannels < 1)
            throw new ArgumentException("Output channel count must be at least 1.", nameof(outputChannels));

        _source = source;
        _outputChannels = outputChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, outputChannels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int framesRequested = count / _outputChannels;
        if (_monoBuffer.Length < framesRequested)
            _monoBuffer = new float[framesRequested];

        int framesRead = _source.Read(_monoBuffer, 0, framesRequested);
        for (int frame = 0; frame < framesRead; frame++)
        {
            float sample = _monoBuffer[frame];
            int baseIdx = offset + frame * _outputChannels;
            for (int ch = 0; ch < _outputChannels; ch++)
                buffer[baseIdx + ch] = sample;
        }

        return framesRead * _outputChannels;
    }
}

/// <summary>
/// The render-path source: a thread-safe FIFO of already-decoded/degraded 48000Hz
/// mono PCM16 samples (see RadioDegradationEffect.Apply's output), exposed as a
/// mono float ISampleProvider for WasapiOut's pull-based graph. Enqueue() is
/// called from VoiceAudioEngine.PlayPcm (any thread); Read() is called from
/// WasapiOut's own render callback thread.
///
/// Always "fully" reads -- silence-pads with 0f when the queue is empty rather
/// than returning fewer samples than requested. WASAPI shared-mode playback
/// expects a continuously-fed stream; returning a short read (the usual
/// ISampleProvider convention for "no more data") would be misinterpreted as
/// end-of-stream and stop playback outright instead of just going quiet
/// between transmissions, which is the correct behavior here (this engine's
/// WasapiOut instance is opened once and stays running for the engine's
/// lifetime, not re-opened per transmission).
/// </summary>
public sealed class PlaybackQueueSampleProvider : ISampleProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<float> _queue = new();

    public PlaybackQueueSampleProvider(int sampleRate, int channels)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Number of samples currently buffered and not yet played -- exposed for tests/diagnostics.</summary>
    public int QueuedSampleCount => _queue.Count;

    public void Enqueue(short[] pcm)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        foreach (short sample in pcm)
            _queue.Enqueue(sample / 32768f);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            buffer[offset + i] = _queue.TryDequeue(out float sample) ? sample : 0f;
        }
        return count;
    }
}
