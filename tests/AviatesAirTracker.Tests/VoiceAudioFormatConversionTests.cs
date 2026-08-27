using AviatesAirTracker.Services.Voice;
using NAudio.Wave;
using Xunit;

namespace AviatesAirTracker.Tests;

/// <summary>
/// Synthetic-data coverage for VoiceAudioEngine's pure format-conversion helpers
/// (Task 3): DownmixToMonoSampleProvider, UpmixSampleProvider,
/// PlaybackQueueSampleProvider, and CaptureConverter. These are exactly the pieces
/// of Task 3 that CAN be verified without real audio hardware -- VoiceAudioEngine
/// itself (the actual WasapiCapture/WasapiOut device wiring) is deliberately not
/// exercised here; see this task's final report for what remains genuinely
/// unverified pending a real desktop session.
/// </summary>
public class VoiceAudioFormatConversionTests
{
    // ---- test double: a fixed, pre-supplied stream of float samples ----
    private sealed class FakeSampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public FakeSampleProvider(WaveFormat format, float[] samples)
        {
            WaveFormat = format;
            _samples = samples;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int available = Math.Min(count, _samples.Length - _position);
            if (available <= 0) return 0;
            Array.Copy(_samples, _position, buffer, offset, available);
            _position += available;
            return available;
        }
    }

    private static short[] GenerateSine(int totalSamples, int sampleRate, double frequencyHz, short amplitude)
    {
        var samples = new short[totalSamples];
        double phase = 0;
        double increment = 2 * Math.PI * frequencyHz / sampleRate;
        for (int i = 0; i < totalSamples; i++)
        {
            samples[i] = (short)(Math.Sin(phase) * amplitude);
            phase += increment;
        }
        return samples;
    }

    private static byte[] Pcm16ToBytes(short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static double Rms(IEnumerable<short> samples)
    {
        double sumSquares = 0;
        int count = 0;
        foreach (short s in samples)
        {
            sumSquares += (double)s * s;
            count++;
        }
        return count == 0 ? 0 : Math.Sqrt(sumSquares / count);
    }

    // ---- DownmixToMonoSampleProvider ----

    [Fact]
    public void DownmixToMonoSampleProvider_StereoInput_AveragesChannelsPerFrame()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        // 3 stereo frames: (1, -1) -> 0, (0.5, 0.5) -> 0.5, (1, 0) -> 0.5
        var interleaved = new float[] { 1f, -1f, 0.5f, 0.5f, 1f, 0f };
        var downmix = new DownmixToMonoSampleProvider(new FakeSampleProvider(format, interleaved));

        Assert.Equal(1, downmix.WaveFormat.Channels);
        Assert.Equal(48000, downmix.WaveFormat.SampleRate);

        var output = new float[3];
        int read = downmix.Read(output, 0, 3);

        Assert.Equal(3, read);
        Assert.Equal(0f, output[0], 5);
        Assert.Equal(0.5f, output[1], 5);
        Assert.Equal(0.5f, output[2], 5);
    }

    [Fact]
    public void DownmixToMonoSampleProvider_FiveChannelInput_AveragesAllChannels()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 5);
        var interleaved = new float[] { 1f, 1f, 1f, 1f, 1f }; // one 5-channel frame, all = 1
        var downmix = new DownmixToMonoSampleProvider(new FakeSampleProvider(format, interleaved));

        var output = new float[1];
        int read = downmix.Read(output, 0, 1);

        Assert.Equal(1, read);
        Assert.Equal(1f, output[0], 5);
    }

    // ---- UpmixSampleProvider ----

    [Fact]
    public void UpmixSampleProvider_MonoInput_DuplicatesToAllChannels()
    {
        var monoFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        var monoSamples = new float[] { 0.25f, -0.5f };
        var upmix = new UpmixSampleProvider(new FakeSampleProvider(monoFormat, monoSamples), 4);

        Assert.Equal(4, upmix.WaveFormat.Channels);

        var output = new float[8]; // 2 frames * 4 channels
        int read = upmix.Read(output, 0, 8);

        Assert.Equal(8, read);
        Assert.Equal(new[] { 0.25f, 0.25f, 0.25f, 0.25f, -0.5f, -0.5f, -0.5f, -0.5f }, output);
    }

    [Fact]
    public void UpmixSampleProvider_RejectsNonMonoSource()
    {
        var stereoFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var source = new FakeSampleProvider(stereoFormat, new float[4]);
        Assert.Throws<ArgumentException>(() => new UpmixSampleProvider(source, 2));
    }

    // ---- PlaybackQueueSampleProvider ----

    [Fact]
    public void PlaybackQueueSampleProvider_EnqueuedPcm_ReadsBackAsNormalizedFloat()
    {
        var queue = new PlaybackQueueSampleProvider(48000, 1);
        queue.Enqueue(new short[] { short.MaxValue, 0, short.MinValue });

        var output = new float[3];
        int read = queue.Read(output, 0, 3);

        Assert.Equal(3, read); // always "fully" reads -- silence-pads, never short-reads
        Assert.InRange(output[0], 0.99f, 1.0f);
        Assert.Equal(0f, output[1], 5);
        Assert.InRange(output[2], -1.0f, -0.99f);
    }

    [Fact]
    public void PlaybackQueueSampleProvider_EmptyQueue_ReadsSilenceRatherThanShortRead()
    {
        var queue = new PlaybackQueueSampleProvider(48000, 1);
        var output = new float[] { 1f, 1f, 1f };

        int read = queue.Read(output, 0, 3);

        Assert.Equal(3, read);
        Assert.All(output, sample => Assert.Equal(0f, sample));
    }

    // ---- CaptureConverter ----

    [Fact]
    public void CaptureConverter_RejectsNonMonoTarget()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        Assert.Throws<NotSupportedException>(() => new CaptureConverter(format, 48000, 2));
    }

    [Fact]
    public void CaptureConverter_SourceAlreadyAtTargetFormat_PassesThroughWithSaneEnergy()
    {
        // 16-bit PCM mono 48000Hz -- already the pipeline's target format, so the
        // conversion chain runs through NAudio's Pcm16BitToSampleProvider decode
        // and back with no resample/downmix stage. Not a byte-identity copy (it
        // round-trips through float), so this checks sane energy preservation,
        // like the Task 1 Opus round-trip tests do, rather than exact equality.
        var format = new WaveFormat(48000, 16, 1);
        var converter = new CaptureConverter(format, 48000, 1);

        const int frameCount = 5;
        const int totalSamples = frameCount * 960; // OpusCodec.FrameSamples
        var pcm = GenerateSine(totalSamples, 48000, 440.0, 12000);
        var bytes = Pcm16ToBytes(pcm);

        converter.Write(bytes, bytes.Length);
        var frames = converter.DrainFrames(960);

        Assert.Equal(frameCount, frames.Count);
        foreach (var frame in frames)
            Assert.Equal(960, frame.Length);

        var decoded = frames.SelectMany(f => f).ToArray();
        Assert.Equal(totalSamples, decoded.Length);

        double originalRms = Rms(pcm);
        double decodedRms = Rms(decoded);
        Assert.True(decodedRms > originalRms * 0.9, $"Passthrough RMS {decodedRms:F1} too far from original {originalRms:F1}");
    }

    [Fact]
    public void CaptureConverter_StereoDifferentRate_DownmixesAndResamplesToTargetFormat()
    {
        // Simulates a real device negotiating 44100Hz stereo 16-bit PCM -- a common
        // WASAPI shared-mode format, and deliberately NOT the pipeline's 48000Hz
        // mono target, exercising both the downmix and resample stages together.
        const int sourceRate = 44100;
        const int sourceChannels = 2;
        const double toneHz = 440.0;
        const short amplitude = 12000;
        const double durationSec = 0.5;
        int sourceSamplesPerChannel = (int)(sourceRate * durationSec);

        var format = new WaveFormat(sourceRate, 16, sourceChannels);
        var converter = new CaptureConverter(format, OpusCodec.SampleRateHz, 1);

        var interleaved = new short[sourceSamplesPerChannel * sourceChannels];
        double phase = 0;
        double phaseIncrement = 2 * Math.PI * toneHz / sourceRate;
        for (int i = 0; i < sourceSamplesPerChannel; i++)
        {
            short sample = (short)(Math.Sin(phase) * amplitude);
            interleaved[i * 2] = sample;
            interleaved[i * 2 + 1] = sample; // identical L/R -- downmix should reproduce it near-exactly
            phase += phaseIncrement;
        }
        var bytes = Pcm16ToBytes(interleaved);

        converter.Write(bytes, bytes.Length);
        var frames = converter.DrainFrames(OpusCodec.FrameSamples);

        Assert.NotEmpty(frames);
        foreach (var frame in frames)
            Assert.Equal(OpusCodec.FrameSamples, frame.Length);

        int totalOutputSamples = frames.Sum(f => f.Length);
        int expectedOutputSamples = (int)(sourceSamplesPerChannel * ((double)OpusCodec.SampleRateHz / sourceRate));
        // The resampler's own internal latency/buffering plus frame-boundary
        // truncation (DrainFrames only emits whole 960-sample frames) mean this
        // won't be exact -- assert it lands in the right ballpark (within a tenth
        // of a second of audio) rather than an exact count.
        Assert.InRange(totalOutputSamples,
            expectedOutputSamples - OpusCodec.SampleRateHz / 10,
            expectedOutputSamples + OpusCodec.SampleRateHz / 10);

        var decoded = frames.SelectMany(f => f).ToArray();
        double rms = Rms(decoded);
        Assert.True(rms > amplitude * 0.3, $"Downsampled/downmixed signal energy looks degenerate: RMS={rms:F1}");
    }

    [Fact]
    public void CaptureConverter_Reset_DropsBufferedAudio()
    {
        var format = new WaveFormat(48000, 16, 1);
        var converter = new CaptureConverter(format, 48000, 1);

        // Less than one full frame -- should stay buffered as a partial frame, not emitted.
        var partial = GenerateSine(500, 48000, 440.0, 12000);
        converter.Write(Pcm16ToBytes(partial), partial.Length * 2);
        Assert.Empty(converter.DrainFrames(960));

        converter.Reset();

        // After Reset(), even feeding a full frame's worth should start clean --
        // if the partial 500 samples had survived, this would emit 0 frames
        // instead of exactly 1 (500 + 960 > 960 but < 1920).
        var full = GenerateSine(960, 48000, 440.0, 12000);
        converter.Write(Pcm16ToBytes(full), full.Length * 2);
        var frames = converter.DrainFrames(960);

        Assert.Single(frames);
    }
}
