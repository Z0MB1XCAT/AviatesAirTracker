using Concentus;
using Concentus.Enums;

namespace AviatesAirTracker.Services.Voice;

// ============================================================
// OPUS CODEC
//
// Thin wrapper over Concentus (pure managed, no P/Invoke) matching the exact
// framing Chromium's MediaRecorder(audio/webm;codecs=opus) uses internally:
// 48000Hz mono, 20ms frames (960 samples). This is the real Opus frame size —
// NOT to be confused with MediaRecorder's 100ms `ondataavailable` timeslice
// (visible in the legacy wwwroot/js/voice.js), which is a separate, larger-
// grained WebM cluster-flush cadence. Chromium packs multiple 20ms Opus
// frames into each 100ms WebM cluster.
//
// Uses OpusCodecFactory.CreateEncoder/CreateDecoder — the classic
// `new OpusEncoder(...)` constructor is obsolete in Concentus 2.2.2.
// ============================================================
public sealed class OpusCodec : IDisposable
{
    /// <summary>Sample rate Chromium's MediaRecorder always runs Opus at internally.</summary>
    public const int SampleRateHz = 48000;

    /// <summary>Mono — matches the single-channel capture used throughout the voice pipeline.</summary>
    public const int Channels = 1;

    /// <summary>The real Opus frame duration. Do not confuse with the WebM cluster-flush cadence.</summary>
    public const int FrameDurationMs = 20;

    /// <summary>20ms @ 48000Hz mono = 960 samples per frame.</summary>
    public const int FrameSamples = SampleRateHz * FrameDurationMs / 1000;

    // A single Opus packet for one 20ms mono frame is always well under this at any
    // sane bitrate — a generous encode scratch buffer, not a wire-protocol limit.
    private const int MaxEncodedPacketBytes = 4000;

    private readonly IOpusEncoder _encoder;
    private readonly IOpusDecoder _decoder;
    private bool _disposed;

    public OpusCodec()
    {
        _encoder = OpusCodecFactory.CreateEncoder(SampleRateHz, Channels, OpusApplication.OPUS_APPLICATION_VOIP);
        _decoder = OpusCodecFactory.CreateDecoder(SampleRateHz, Channels);
    }

    /// <summary>
    /// Encodes exactly one 20ms/960-sample mono PCM frame into an Opus packet.
    /// Throws on any other input size rather than silently mishandling it.
    /// </summary>
    public byte[] Encode(short[] pcmFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(pcmFrame);
        if (pcmFrame.Length != FrameSamples)
        {
            throw new ArgumentException(
                $"Opus frame must be exactly {FrameSamples} samples (20ms @ {SampleRateHz}Hz mono); got {pcmFrame.Length}.",
                nameof(pcmFrame));
        }

        Span<byte> scratch = stackalloc byte[MaxEncodedPacketBytes];
        int bytesWritten = _encoder.Encode(pcmFrame, FrameSamples, scratch, scratch.Length);
        return scratch[..bytesWritten].ToArray();
    }

    /// <summary>
    /// Decodes one Opus packet back into a 20ms/960-sample mono PCM frame. If the
    /// decoder produces fewer samples than a full frame (can happen for certain
    /// packet types), the returned array is sized to the actual sample count
    /// rather than padded with silence.
    /// </summary>
    public short[] Decode(byte[] opusPacket)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(opusPacket);

        var pcm = new short[FrameSamples];
        int samplesDecoded = _decoder.Decode(opusPacket, pcm, FrameSamples, false);
        if (samplesDecoded != FrameSamples)
        {
            Array.Resize(ref pcm, samplesDecoded);
        }
        return pcm;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _encoder.Dispose();
        _decoder.Dispose();
    }
}
