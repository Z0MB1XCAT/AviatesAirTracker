using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AviatesAirTracker.Services.Voice;

// ============================================================
// CAPTURE CONVERTER
//
// Converts whatever raw PCM format a real WASAPI capture device actually
// negotiates (commonly IEEE-float 44100/48000Hz stereo, but device- and
// driver-dependent -- never assume it's already 48000Hz mono) into a stream
// of fixed-size 48000Hz mono PCM16 frames matching OpusCodec's required
// framing (20ms / 960 samples, see OpusCodec.FrameSamples).
//
// Pure format-conversion logic, deliberately decoupled from WasapiCapture
// itself: it only needs a WaveFormat (to build the conversion chain) and raw
// captured byte buffers (via Write()) pushed in from VoiceAudioEngine's
// DataAvailable handler. This split is what makes the format-conversion math
// unit-testable with synthetic buffers even though real WasapiCapture.
// DataAvailable callbacks cannot be exercised in a headless/no-audio-hardware
// environment (see VoiceAudioEngine's own remarks).
//
// Conversion chain: BufferedWaveProvider(sourceFormat) -> ToSampleProvider()
// (NAudio auto-selects the right PCM8/16/24/32/IeeeFloat -> float decoder for
// whatever the source format is) -> DownmixToMonoSampleProvider (only if the
// source isn't already mono) -> WdlResamplingSampleProvider (only if the
// source rate isn't already 48000Hz) -> float samples, converted to PCM16 and
// sliced into exact 960-sample frames.
//
// ReadFully=false on the BufferedWaveProvider is deliberate: with the default
// (true), Read() silently pads missing input with zero-samples up to the
// requested count instead of returning fewer samples than asked -- which
// would inject fabricated silence into the resampler chain's input on every
// drain call where less than a full scratch buffer has arrived yet, rather
// than correctly waiting for more real audio. false makes short reads
// propagate honestly instead.
// ============================================================
public sealed class CaptureConverter
{
    private const int ScratchSamples = 4096;

    // Defensive bound on how many scratch-sized reads DrainFrames will pull in a
    // single call. Read() against a BufferedWaveProvider-backed chain is a
    // synchronous in-memory copy (never blocking I/O), so a true infinite loop
    // isn't expected here -- this exists purely so a future change to the chain
    // (or an unexpected NAudio behavior) can't turn "drain what's available" into
    // an unbounded loop on the capture callback thread.
    private const int MaxDrainIterations = 64;

    private readonly BufferedWaveProvider _buffered;
    private readonly ISampleProvider _chain;
    private readonly Queue<short> _pending = new();
    private readonly float[] _scratch = new float[ScratchSamples];

    public WaveFormat SourceFormat { get; }
    public int TargetSampleRate { get; }
    public int TargetChannels { get; }

    public CaptureConverter(WaveFormat sourceFormat, int targetSampleRate, int targetChannels)
    {
        ArgumentNullException.ThrowIfNull(sourceFormat);
        if (targetChannels != 1)
            throw new NotSupportedException("CaptureConverter only supports a mono target (targetChannels must be 1).");

        SourceFormat = sourceFormat;
        TargetSampleRate = targetSampleRate;
        TargetChannels = targetChannels;

        _buffered = new BufferedWaveProvider(sourceFormat)
        {
            ReadFully = false,
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(5),
        };

        ISampleProvider chain = _buffered.ToSampleProvider();

        if (chain.WaveFormat.Channels != targetChannels)
            chain = new DownmixToMonoSampleProvider(chain);

        if (chain.WaveFormat.SampleRate != targetSampleRate)
            chain = new WdlResamplingSampleProvider(chain, targetSampleRate);

        _chain = chain;
    }

    /// <summary>Pushes newly-captured raw bytes (in <see cref="SourceFormat"/>) into the conversion pipeline.</summary>
    public void Write(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded <= 0) return;
        _buffered.AddSamples(buffer, 0, bytesRecorded);
    }

    /// <summary>
    /// Drains whatever converted audio is currently available and returns any
    /// complete <paramref name="frameSamples"/>-sized frames it produces.
    /// Leftover samples smaller than a full frame stay buffered for the next call.
    /// </summary>
    public List<short[]> DrainFrames(int frameSamples)
    {
        if (frameSamples <= 0) throw new ArgumentOutOfRangeException(nameof(frameSamples));

        int read;
        int iterations = 0;
        while ((read = _chain.Read(_scratch, 0, _scratch.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
                _pending.Enqueue(FloatToPcm16(_scratch[i]));

            iterations++;
            if (read < _scratch.Length || iterations >= MaxDrainIterations)
                break;
        }

        var frames = new List<short[]>();
        while (_pending.Count >= frameSamples)
        {
            var frame = new short[frameSamples];
            for (int i = 0; i < frameSamples; i++)
                frame[i] = _pending.Dequeue();
            frames.Add(frame);
        }
        return frames;
    }

    /// <summary>Drops any buffered-but-not-yet-emitted audio (source bytes and partial-frame samples).</summary>
    public void Reset()
    {
        _pending.Clear();
        _buffered.ClearBuffer();
    }

    private static short FloatToPcm16(float sample)
    {
        float clamped = Math.Clamp(sample, -1f, 1f);
        return (short)(clamped * short.MaxValue);
    }
}
