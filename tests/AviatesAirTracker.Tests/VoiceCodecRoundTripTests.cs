using AviatesAirTracker.Services.Voice;
using Xunit;

namespace AviatesAirTracker.Tests;

/// <summary>
/// Round-trip verification for the native voice pipeline's pure library layer
/// (Opus codec wrapper + WebM muxer/demuxer). Mirrors the hands-on spike that
/// preceded this task: synthetic 440Hz-sine PCM -> OpusCodec.Encode ->
/// WebmMuxer.Mux -> WebmDemuxer.Demux -> OpusCodec.Decode, checked for correct
/// frame counts and non-degenerate (non-silent, non-garbage) decoded energy.
///
/// This is fully autonomous — no real audio hardware or MSFS/backend
/// connectivity involved, matching the plan's scoping of Task 1 as
/// "pure library layer, fully autonomous".
/// </summary>
public class VoiceCodecRoundTripTests
{
    private const int SampleRateHz = OpusCodec.SampleRateHz;
    private const int FrameSamples = OpusCodec.FrameSamples;

    /// <summary>Generates PCM frames for a pure sine wave, split into 20ms/960-sample frames.</summary>
    private static List<short[]> GenerateSineFrames(int frameCount, double frequencyHz = 440.0, short amplitude = 12000)
    {
        var frames = new List<short[]>(frameCount);
        double phase = 0;
        double phaseIncrement = 2 * Math.PI * frequencyHz / SampleRateHz;
        for (int f = 0; f < frameCount; f++)
        {
            var frame = new short[FrameSamples];
            for (int i = 0; i < FrameSamples; i++)
            {
                frame[i] = (short)(Math.Sin(phase) * amplitude);
                phase += phaseIncrement;
            }
            frames.Add(frame);
        }
        return frames;
    }

    private static double ComputeRms(IEnumerable<short> samples)
    {
        double sumSquares = 0;
        int count = 0;
        foreach (var s in samples)
        {
            sumSquares += (double)s * s;
            count++;
        }
        Assert.True(count > 0, "RMS computed over zero samples");
        return Math.Sqrt(sumSquares / count);
    }

    [Fact]
    public void OpusCodec_EncodeDecode_SingleFrame_RoundTripsWithSaneEnergy()
    {
        var frame = GenerateSineFrames(1)[0];
        using var codec = new OpusCodec();

        var packet = codec.Encode(frame);
        Assert.NotEmpty(packet);

        var decoded = codec.Decode(packet);
        Assert.Equal(FrameSamples, decoded.Length);

        double originalRms = ComputeRms(frame);
        double decodedRms = ComputeRms(decoded);
        Assert.True(decodedRms > originalRms * 0.5, $"Decoded RMS {decodedRms:F1} looks degenerate/silent vs original {originalRms:F1}");
    }

    [Fact]
    public void OpusCodec_Encode_WrongFrameSize_Throws()
    {
        using var codec = new OpusCodec();
        var wrongSize = new short[FrameSamples - 1];
        Assert.Throws<ArgumentException>(() => codec.Encode(wrongSize));
    }

    [Fact]
    public void WebmMuxDemux_SingleCluster_RoundTripsOpusPacketsByteExact()
    {
        // 3 frames — fewer than WebmMuxer.FramesPerCluster (5), so this is one partial Cluster.
        var frames = GenerateSineFrames(3);
        using var codec = new OpusCodec();
        var packets = frames.Select(codec.Encode).ToList();

        var webm = WebmMuxer.Mux(packets);
        Assert.NotEmpty(webm);

        var demuxed = WebmDemuxer.Demux(webm);
        Assert.Equal(packets.Count, demuxed.Count);
        for (int i = 0; i < packets.Count; i++)
        {
            Assert.Equal(packets[i], demuxed[i]);
        }
    }

    [Fact]
    public void WebmMuxDemux_MultipleClusters_NonMultipleOfClusterSize_RoundTripsAllFrames()
    {
        // 137 frames deliberately isn't a multiple of WebmMuxer.FramesPerCluster (5),
        // exercising the final partial-cluster path.
        const int frameCount = 137;
        var frames = GenerateSineFrames(frameCount);
        using var codec = new OpusCodec();
        var packets = frames.Select(codec.Encode).ToList();

        var webm = WebmMuxer.Mux(packets);
        var demuxed = WebmDemuxer.Demux(webm);

        Assert.Equal(frameCount, demuxed.Count);
        Assert.Equal(packets.Count, demuxed.Count);
        for (int i = 0; i < packets.Count; i++)
        {
            Assert.True(packets[i].AsSpan().SequenceEqual(demuxed[i]), $"Opus packet #{i} did not round-trip byte-exact through mux/demux");
        }
    }

    [Fact]
    public void FullPipeline_EncodeMuxDemuxDecode_ProducesCorrectSampleCountAndNonDegenerateSignal()
    {
        const int frameCount = 100; // 2 seconds @ 20ms/frame
        var originalFrames = GenerateSineFrames(frameCount);

        using var encoder = new OpusCodec();
        var opusPackets = originalFrames.Select(encoder.Encode).ToList();

        var webmBlob = WebmMuxer.Mux(opusPackets);
        var demuxedPackets = WebmDemuxer.Demux(webmBlob);
        Assert.Equal(frameCount, demuxedPackets.Count);

        using var decoder = new OpusCodec();
        var decodedFrames = demuxedPackets.Select(decoder.Decode).ToList();

        int totalDecodedSamples = decodedFrames.Sum(f => f.Length);
        Assert.Equal(frameCount * FrameSamples, totalDecodedSamples);

        double originalRms = ComputeRms(originalFrames.SelectMany(f => f));
        double decodedRms = ComputeRms(decodedFrames.SelectMany(f => f));

        // Opus is lossy but should preserve the overall signal energy closely for a
        // clean sine wave — the spike measured ~0.995. Assert a generous band so this
        // doesn't become flaky against minor codec/library version drift, while still
        // catching a truly degenerate (near-silent or garbage/clipped) decode.
        double ratio = decodedRms / originalRms;
        Assert.True(ratio is > 0.8 and < 1.2, $"Decoded/original RMS ratio {ratio:F4} is outside the sane band — signal looks degenerate");
    }

    [Fact]
    public void WebmDemuxer_EmptyFrameList_ProducesZeroPackets()
    {
        var webm = WebmMuxer.Mux(Array.Empty<byte[]>());
        var demuxed = WebmDemuxer.Demux(webm);
        Assert.Empty(demuxed);
    }
}
