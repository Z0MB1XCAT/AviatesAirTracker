using AviatesAirTracker.Services.Voice;
using Xunit;

namespace AviatesAirTracker.Tests;

/// <summary>
/// Verification for the radio degradation DSP (Task 2 of the native voice
/// pipeline plan) — a pure PCM-in/PCM-out port of wwwroot/js/voice.js's Web
/// Audio degradation graph (bandpass filter, procedural static noise,
/// distance-scaled gain dropouts). Fully autonomous: synthetic sine-wave PCM
/// in, no real audio hardware or MSFS/backend connectivity involved, same
/// spirit as Task 1's Opus/WebM round-trip tests.
/// </summary>
public class RadioDegradationEffectTests
{
    private const int SampleRateHz = OpusCodec.SampleRateHz;

    private static short[] GenerateSine(double frequencyHz, double durationSec, short amplitude = 8000)
    {
        int length = (int)(SampleRateHz * durationSec);
        var pcm = new short[length];
        double phase = 0;
        double phaseIncrement = 2 * Math.PI * frequencyHz / SampleRateHz;
        for (int i = 0; i < length; i++)
        {
            pcm[i] = (short)(Math.Sin(phase) * amplitude);
            phase += phaseIncrement;
        }
        return pcm;
    }

    private static double ComputeRms(short[] samples)
    {
        double sumSquares = 0;
        foreach (var s in samples)
        {
            sumSquares += (double)s * s;
        }
        return Math.Sqrt(sumSquares / samples.Length);
    }

    private static double ComputeDiffRms(short[] a, short[] b)
    {
        Assert.Equal(a.Length, b.Length);
        double sumSquares = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double diff = a[i] - b[i];
            sumSquares += diff * diff;
        }
        return Math.Sqrt(sumSquares / a.Length);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(150, 0)]
    [InlineData(200, 0)]
    [InlineData(250, 0.25)]
    [InlineData(300, 0.5)]
    [InlineData(400, 1)]
    [InlineData(500, 1)]
    public void ComputeDegradationFactor_MatchesVoiceJsShapeExactly(double distanceNm, double expectedFactor)
    {
        Assert.Equal(expectedFactor, RadioDegradationEffect.ComputeDegradationFactor(distanceNm), precision: 10);
    }

    [Fact]
    public void ClearAndDegradedNm_MatchDesignSpecConstants()
    {
        // Load-bearing per the design spec — DISPATCH-RADIO-WORKER/src/distance.js
        // holds these same two numbers server-side; a later task's
        // VoiceOrchestrator must reference these constants, not redefine them.
        Assert.Equal(200, RadioDegradationEffect.ClearNm);
        Assert.Equal(400, RadioDegradationEffect.DegradedNm);
    }

    [Fact]
    public void Apply_BandpassAlwaysApplied_PassesCenterFrequencyMoreThanOffBandFrequency()
    {
        // distance=0 -> degradation factor 0 -> only the always-on bandpass runs
        // (no noise, no dropouts), isolating the filter's own behavior.
        var centerTone = GenerateSine(1650.0, durationSec: 1.0);
        var lowTone = GenerateSine(100.0, durationSec: 1.0);
        var highTone = GenerateSine(8000.0, durationSec: 1.0);

        var centerOut = RadioDegradationEffect.Apply(centerTone, distanceNm: 0);
        var lowOut = RadioDegradationEffect.Apply(lowTone, distanceNm: 0);
        var highOut = RadioDegradationEffect.Apply(highTone, distanceNm: 0);

        double centerGain = ComputeRms(centerOut) / ComputeRms(centerTone);
        double lowGain = ComputeRms(lowOut) / ComputeRms(lowTone);
        double highGain = ComputeRms(highOut) / ComputeRms(highTone);

        // A 1650Hz/Q=0.7 bandpass should pass its center frequency near
        // unchanged while attenuating a tone two decades below and roughly an
        // octave-plus above it.
        Assert.True(centerGain > 0.9, $"Center-frequency gain {centerGain:F4} should be near 1.0 (unattenuated)");
        Assert.True(lowGain < centerGain, $"100Hz gain {lowGain:F4} should be well below center-frequency gain {centerGain:F4}");
        Assert.True(highGain < centerGain, $"8000Hz gain {highGain:F4} should be below center-frequency gain {centerGain:F4}");
    }

    [Fact]
    public void Apply_WithinClearRange_IsDeterministicAndIdenticalRegardlessOfExactDistance()
    {
        // 0nm and 150nm are both <= ClearNm=200, so both should hit the
        // factor<=0 fast path: bandpass only, no noise/dropout randomness —
        // byte-identical output for the same input.
        var tone = GenerateSine(1650.0, durationSec: 0.5);

        var outAt0 = RadioDegradationEffect.Apply(tone, distanceNm: 0);
        var outAt150 = RadioDegradationEffect.Apply(tone, distanceNm: 150);

        Assert.Equal(outAt0, outAt150);
    }

    [Fact]
    public void Apply_DegradationIncreasesMonotonicallyWithDistance_AcrossClearToBeyondDegradedTiers()
    {
        // Long enough (8s) that dropout timing's inherent randomness (avg
        // interval 3-20s, jittered) averages out and doesn't flip the
        // monotonic ordering driven by the deterministic noiseGain scaling
        // (0 -> 0 -> 0.175 -> 0.35 as factor goes 0 -> 0 -> 0.5 -> 1.0).
        const double durationSec = 8.0;
        var tone = GenerateSine(1650.0, durationSec);

        double[] distances = { 0, 150, 300, 500 };
        var diffFromReference = new double[distances.Length];

        // Reference: bandpass-only output (factor=0 at distanceNm=0).
        var reference = RadioDegradationEffect.Apply(tone, distanceNm: 0);

        for (int i = 0; i < distances.Length; i++)
        {
            var processed = RadioDegradationEffect.Apply(tone, distances[i]);
            diffFromReference[i] = ComputeDiffRms(processed, reference);
        }

        // 0nm and 150nm are both within the clear range -> both factor 0 ->
        // identical to the reference (diff should be exactly 0).
        Assert.Equal(0.0, diffFromReference[0], precision: 6);
        Assert.Equal(0.0, diffFromReference[1], precision: 6);

        // 300nm (factor=0.5) and 500nm (factor=1.0) must diverge from the
        // clean reference, and 500nm must diverge more than 300nm — more
        // noise gain and more frequent dropouts at greater distance.
        Assert.True(diffFromReference[2] > 0, $"300nm should differ from the clean reference (got diff RMS {diffFromReference[2]:F2})");
        Assert.True(diffFromReference[3] > diffFromReference[2],
            $"500nm diff RMS ({diffFromReference[3]:F2}) should exceed 300nm diff RMS ({diffFromReference[2]:F2})");
    }

    [Fact]
    public void Apply_AtFullDegradation_ProducesAtLeastOneAudibleGainDropout()
    {
        // avgIntervalSec at factor=1 is 3s (20 - 17*1), jittered by up to
        // 1.5x -> worst case ~4.5s between dips. A 10s clip should reliably
        // contain at least one full dip-and-recover cycle (150ms window),
        // detectable as a local RMS trough well below the clip's peak
        // windowed RMS.
        const double durationSec = 10.0;
        var tone = GenerateSine(1650.0, durationSec, amplitude: 12000);

        var processed = RadioDegradationEffect.Apply(tone, distanceNm: 500);

        const int windowSamples = SampleRateHz / 20; // 50ms windows
        double minWindowRms = double.MaxValue;
        double maxWindowRms = double.MinValue;
        for (int start = 0; start + windowSamples <= processed.Length; start += windowSamples)
        {
            double sumSquares = 0;
            for (int i = start; i < start + windowSamples; i++)
            {
                sumSquares += (double)processed[i] * processed[i];
            }
            double windowRms = Math.Sqrt(sumSquares / windowSamples);
            minWindowRms = Math.Min(minWindowRms, windowRms);
            maxWindowRms = Math.Max(maxWindowRms, windowRms);
        }

        // A dip drags gain down to 0.15x for part of a window; the trough
        // window's RMS should be well under half the loudest window's RMS.
        Assert.True(minWindowRms < maxWindowRms * 0.5,
            $"Expected an audible dropout dip: min window RMS {minWindowRms:F1} vs max {maxWindowRms:F1}");
    }

    [Fact]
    public void Apply_EmptyInput_ReturnsEmptyOutput()
    {
        var result = RadioDegradationEffect.Apply(Array.Empty<short>(), distanceNm: 300);
        Assert.Empty(result);
    }

    [Fact]
    public void Apply_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => RadioDegradationEffect.Apply(null!, distanceNm: 300));
    }
}
