namespace AviatesAirTracker.Services.Voice;

// ============================================================
// RADIO DEGRADATION DSP
//
// Pure PCM-in/PCM-out port of wwwroot/js/voice.js's Web Audio degradation
// graph (playDegradedClip/scheduleDropouts/getStaticNoiseBuffer/
// computeDegradationFactor, lines ~81-151 of that file). No I/O, no NAudio,
// no WebSocket — takes a whole decoded transmission (one clip, matching
// voice.js's buffered whole-clip-on-voice_end playback model, not a
// streaming per-frame effect) plus a distance value, returns processed PCM.
//
// voice.js builds a fresh Web Audio graph per playSession() call — a new
// BiquadFilterNode, a new dropout GainNode with automation scheduled from
// t=0, and the shared/cached noise AudioBuffer restarted from its own
// sample 0 — so nothing here needs to persist state *across* transmissions.
// The only genuinely stateful pieces (the biquad's own y[n-1]/y[n-2] delay
// line, the dropout gain envelope) live and die within a single Apply()
// call, exactly mirroring the JS graph's per-session lifetime.
//
// The one deliberate difference from voice.js: this operates on normalized
// double samples internally (matching Web Audio's own [-1, 1] float PCM
// domain, which is what the JS noise-buffer constants — 0.15, 3.5,
// NOISE_GAIN_CEILING — were tuned against) rather than raw int16 range, then
// converts back to short[] (WASAPI's PCM format) at the end.
// ============================================================
public static class RadioDegradationEffect
{
    /// <summary>
    /// Distance (nm) at or below which the radio is fully clear — no degradation.
    /// Matches voice.js's CLEAR_NM exactly. Load-bearing: a later task's
    /// VoiceOrchestrator must reference this constant, not redefine its own copy,
    /// and DISPATCH-RADIO-WORKER/src/distance.js holds the same value server-side.
    /// </summary>
    public const double ClearNm = 200;

    /// <summary>
    /// Distance (nm) at or beyond which degradation is maximal (factor = 1).
    /// Matches voice.js's DEGRADED_NM exactly. Same load-bearing note as <see cref="ClearNm"/>.
    /// </summary>
    public const double DegradedNm = 400;

    /// <summary>Matches voice.js's NOISE_GAIN_CEILING — max noise mix gain at factor=1.</summary>
    private const double NoiseGainCeiling = 0.35;

    /// <summary>Bandpass center frequency, matches voice.js's BiquadFilterNode.frequency.value.</summary>
    private const double BandpassFrequencyHz = 1650.0;

    /// <summary>Bandpass Q, matches voice.js's BiquadFilterNode.Q.value.</summary>
    private const double BandpassQ = 0.7;

    // Static noise buffer generation constants — exact port of voice.js's
    // getStaticNoiseBuffer(): a one-pole-smoothed white noise recurrence,
    // 2 seconds long, looped by the caller (noiseSource.loop = true) if the
    // clip is longer. Seeded here (voice.js's Math.random() has no
    // equivalent need for determinism in a browser) purely so this table —
    // and therefore this class's tests — are reproducible from run to run;
    // the statistical shape (smoothed white noise scaled by 3.5) is identical.
    private const double NoiseSmoothingWhiteWeight = 0.15;
    private const double NoiseSmoothingDivisor = 1.15;
    private const double NoiseOutputScale = 3.5;
    private const int NoiseSeed = 20260827;

    // Dropout timing constants — exact port of voice.js's scheduleDropouts():
    // average interval ranges from 20s (factor=0) down to 3s (factor=1),
    // jittered by a uniform [0.5, 1.5) multiplier per gap; each dropout dips
    // gain 1 -> 0.15 over 50ms then ramps 0.15 -> 1 over the next 100ms.
    private const double DropoutIntervalMaxSec = 20.0;
    private const double DropoutIntervalRangeSec = 17.0;
    private const double DropoutDipDurationSec = 0.05;
    private const double DropoutRecoverDurationSec = 0.10;

    private static readonly double[] StaticNoiseBuffer = GenerateStaticNoiseBuffer();

    // Dropout gap jitter draws from a shared Random instance (matches
    // voice.js's Math.random() — genuinely random per call/session, not
    // seeded) so repeated Apply() calls at the same distance don't produce
    // identical dropout timing every time, same as repeated real transmissions.
    private static readonly Random DropoutJitterRandom = new();

    /// <summary>
    /// Exact port of voice.js's computeDegradationFactor(): 0 at/below ClearNm,
    /// 1 at/above DegradedNm, linear ramp in between.
    /// </summary>
    public static double ComputeDegradationFactor(double distanceNm)
    {
        if (distanceNm <= ClearNm) return 0;
        if (distanceNm >= DegradedNm) return 1;
        return (distanceNm - ClearNm) / (DegradedNm - ClearNm);
    }

    /// <summary>
    /// Applies the full radio degradation chain to one whole decoded transmission
    /// (a concatenated sequence of 48000Hz mono PCM samples — see
    /// <see cref="OpusCodec.SampleRateHz"/>/<see cref="OpusCodec.Channels"/>),
    /// scaled by <paramref name="distanceNm"/>. Mirrors voice.js's
    /// playDegradedClip() signal chain exactly:
    ///
    ///   source -> bandpass(1650Hz, Q=0.7) -> [merge with noise*noiseGain] -> dropout gain -> out
    ///
    /// The bandpass filter is always applied, matching voice.js. Noise mixing and
    /// dropouts are skipped entirely when the degradation factor is 0 (matching
    /// voice.js's `if (degradationFactor > 0)` guards), leaving only the filtered
    /// clean signal — same as a full-clear-range transmission today.
    /// </summary>
    public static short[] Apply(short[] pcm, double distanceNm)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (pcm.Length == 0) return pcm;

        double factor = ComputeDegradationFactor(distanceNm);

        double[] samples = ToNormalized(pcm);
        double[] filtered = ApplyBandpass(samples);

        if (factor <= 0)
        {
            return ToPcm16(filtered);
        }

        double[] noise = GetLoopedNoise(pcm.Length);
        double noiseGain = factor * NoiseGainCeiling;

        var merged = new double[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
        {
            merged[i] = filtered[i] + noise[i] * noiseGain;
        }

        double[] dropoutGain = BuildDropoutGainEnvelope(pcm.Length, factor);
        for (int i = 0; i < pcm.Length; i++)
        {
            merged[i] *= dropoutGain[i];
        }

        return ToPcm16(merged);
    }

    /// <summary>
    /// RBJ Audio EQ Cookbook / W3C Web Audio API bandpass biquad (the "constant
    /// 0dB peak gain" variant — this is exactly what a Web Audio
    /// BiquadFilterNode with type='bandpass' computes internally, per the Web
    /// Audio API spec's own normative filter formulas), applied as a Direct
    /// Form I difference equation over the whole clip with a fresh (zeroed)
    /// delay line — matching voice.js constructing a brand-new
    /// BiquadFilterNode per playDegradedClip() call.
    /// </summary>
    private static double[] ApplyBandpass(double[] samples)
    {
        double sampleRate = OpusCodec.SampleRateHz;
        double w0 = 2 * Math.PI * BandpassFrequencyHz / sampleRate;
        double alpha = Math.Sin(w0) / (2 * BandpassQ);
        double cosW0 = Math.Cos(w0);

        double a0 = 1 + alpha;
        double b0 = alpha / a0;
        double b1 = 0.0;
        double b2 = -alpha / a0;
        double a1 = (-2 * cosW0) / a0;
        double a2 = (1 - alpha) / a0;

        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        var output = new double[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            double x0 = samples[i];
            double y0 = b0 * x0 + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            output[i] = y0;
            x2 = x1;
            x1 = x0;
            y2 = y1;
            y1 = y0;
        }
        return output;
    }

    /// <summary>
    /// Exact port of voice.js's getStaticNoiseBuffer(): a 2-second one-pole-
    /// smoothed white noise table, generated once and cached/looped — matching
    /// the JS version's per-AudioContext cache (`ctx._aviatesNoiseBuffer`),
    /// just process-lifetime instead of per-AudioContext-lifetime since there's
    /// exactly one native voice pipeline per process.
    /// </summary>
    private static double[] GenerateStaticNoiseBuffer()
    {
        int length = OpusCodec.SampleRateHz * 2;
        var buffer = new double[length];
        var rng = new Random(NoiseSeed);
        double last = 0;
        for (int i = 0; i < length; i++)
        {
            double white = rng.NextDouble() * 2 - 1;
            last = (last + NoiseSmoothingWhiteWeight * white) / NoiseSmoothingDivisor;
            buffer[i] = last * NoiseOutputScale;
        }
        return buffer;
    }

    /// <summary>
    /// Reads <paramref name="length"/> samples from the cached static noise
    /// buffer starting at index 0, wrapping around (looping) as needed —
    /// matching voice.js's `noiseSource.loop = true` on a source restarted
    /// from position 0 at the start of every playSession() call.
    /// </summary>
    private static double[] GetLoopedNoise(int length)
    {
        var noise = new double[length];
        for (int i = 0; i < length; i++)
        {
            noise[i] = StaticNoiseBuffer[i % StaticNoiseBuffer.Length];
        }
        return noise;
    }

    /// <summary>
    /// Exact port of voice.js's scheduleDropouts(): builds a per-sample gain
    /// envelope (1.0 everywhere except brief dips) covering the whole clip
    /// duration. avgIntervalSec = 20 - 17*factor (20s at factor=0 down to 3s
    /// at factor=1); each gap is avgIntervalSec * uniform[0.5, 1.5); each dip
    /// ramps 1 -> 0.15 over 50ms then 0.15 -> 1 over the next 100ms, exactly
    /// matching the JS gain.setValueAtTime/linearRampToValueAtTime calls.
    /// </summary>
    private static double[] BuildDropoutGainEnvelope(int lengthSamples, double degradationFactor)
    {
        var gain = new double[lengthSamples];
        for (int i = 0; i < lengthSamples; i++)
        {
            gain[i] = 1.0;
        }

        double sampleRate = OpusCodec.SampleRateHz;
        double durationSec = lengthSamples / sampleRate;

        double avgIntervalSec = DropoutIntervalMaxSec - DropoutIntervalRangeSec * degradationFactor;
        double t = 0;
        while (t < durationSec)
        {
            t += avgIntervalSec * (0.5 + DropoutJitterRandom.NextDouble());
            if (t >= durationSec) break;
            ApplyDropoutRamp(gain, sampleRate, t, lengthSamples);
        }

        return gain;
    }

    private static void ApplyDropoutRamp(double[] gain, double sampleRate, double dipStartSec, int totalSamples)
    {
        int startIdx = (int)(dipStartSec * sampleRate);
        int midIdx = (int)((dipStartSec + DropoutDipDurationSec) * sampleRate);
        int endIdx = (int)((dipStartSec + DropoutDipDurationSec + DropoutRecoverDurationSec) * sampleRate);

        for (int i = startIdx; i < midIdx && i < totalSamples; i++)
        {
            double frac = (i - startIdx) / (double)(midIdx - startIdx);
            gain[i] = Lerp(1.0, 0.15, frac);
        }
        for (int i = midIdx; i < endIdx && i < totalSamples; i++)
        {
            double frac = (i - midIdx) / (double)(endIdx - midIdx);
            gain[i] = Lerp(0.15, 1.0, frac);
        }
    }

    private static double Lerp(double from, double to, double frac) => from + (to - from) * frac;

    private static double[] ToNormalized(short[] pcm)
    {
        var normalized = new double[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
        {
            normalized[i] = pcm[i] / 32768.0;
        }
        return normalized;
    }

    private static short[] ToPcm16(double[] samples)
    {
        var pcm = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            double scaled = samples[i] * 32768.0;
            pcm[i] = (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
        }
        return pcm;
    }
}
