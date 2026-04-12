using AviatesAirTracker.Core.Analytics;
using AviatesAirTracker.Core.SimConnect;
using Serilog;
using System.Collections.Concurrent;

namespace AviatesAirTracker.Core.SimConnect;

// ============================================================
// TELEMETRY PROCESSOR
//
// Receives raw AircraftState from SimConnectManager and:
//   1. Computes derived values (wind components, etc.)
//   2. Maintains rolling telemetry history buffer
//   3. Publishes enriched snapshots
//   4. Calculates rates (vertical speed smoothing, etc.)
// ============================================================

public class TelemetryProcessor
{
    // =====================================================
    // PUBLIC EVENTS
    // =====================================================
    public event EventHandler<TelemetrySnapshot>? SnapshotReady;

    // =====================================================
    // HISTORY BUFFER
    // Circular buffer of last 30 minutes of data at 20Hz
    // = 30 * 60 * 20 = 36,000 samples max
    // =====================================================
    private const int MAX_BUFFER_SIZE = 36_000;
    private readonly ConcurrentQueue<TelemetrySnapshot> _historyBuffer = new();
    private int _bufferCount = 0;

    // =====================================================
    // SMOOTHING FILTERS
    // =====================================================
    private readonly ExponentialFilter _vsFilter = new(alpha: 0.15);  // Smooth VS
    private readonly ExponentialFilter _iasFilter = new(alpha: 0.3);
    private readonly ExponentialFilter _headingFilter = new(alpha: 0.5);

    // =====================================================
    // RATES
    // =====================================================
    private TelemetrySnapshot? _previousSnapshot;
    private DateTime _lastProcessTime;

    // =====================================================
    // INJECT DEPENDENCIES
    // =====================================================
    private readonly FlightPhaseDetector _phaseDetector;

    public TelemetryProcessor(FlightPhaseDetector phaseDetector)
    {
        _phaseDetector = phaseDetector;
        _lastProcessTime = DateTime.UtcNow;
    }

    // =====================================================
    // MAIN PROCESSING PIPELINE
    // Called at ~20Hz from SimConnectManager
    // =====================================================

    public void Process(TelemetrySnapshot raw)
    {
        try
        {
            // Step 1: Compute derived / enriched values
            EnrichSnapshot(raw);

            // Step 2: Apply smoothing filters
            ApplyFilters(raw);

            // Step 3: Detect flight phase
            raw.Phase = _phaseDetector.Detect(raw, _previousSnapshot);

            // Step 4: Buffer management
            AddToHistory(raw);

            // Step 5: Update previous reference
            _previousSnapshot = raw;
            _lastProcessTime = DateTime.UtcNow;

            // Step 6: Publish
            SnapshotReady?.Invoke(this, raw);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[TelemetryProcessor] Processing error");
        }
    }

    // =====================================================
    // SNAPSHOT ENRICHMENT
    // =====================================================

    private static void EnrichSnapshot(TelemetrySnapshot snap)
    {
        // Compute wind components
        (snap.HeadwindComponent, snap.CrosswindComponent) = ComputeWindComponents(
            snap.Raw.HeadingTrue,
            snap.Raw.WindDirection,
            snap.Raw.WindSpeed);
    }

    private void ApplyFilters(TelemetrySnapshot snap)
    {
        // MAJOR-11: Filter values were computed but result discarded (_). Now write smoothed VS back.
        double smoothedVS = _vsFilter.Filter(snap.Raw.VerticalSpeed);
        var raw = snap.Raw;
        raw.VerticalSpeed = smoothedVS;
        snap.Raw = raw;
    }

    // =====================================================
    // WIND COMPONENT CALCULATION
    // =====================================================

    /// <summary>
    /// Calculates headwind and crosswind components.
    /// </summary>
    /// <param name="runwayHeading">Runway/aircraft heading in degrees true</param>
    /// <param name="windFrom">Wind direction FROM (degrees true)</param>
    /// <param name="windSpeed">Wind speed (knots)</param>
    /// <returns>(headwind, crosswind) — headwind > 0 = headwind, crosswind > 0 = from right</returns>
    public static (double headwind, double crosswind) ComputeWindComponents(
        double runwayHeading, double windFrom, double windSpeed)
    {
        // Wind direction is where wind comes FROM; convert to where it blows TO
        double windToward = (windFrom + 180) % 360;
        // CRIT-04: Was DeltaAngle(windToward, runwayHeading) — sign was inverted.
        // DeltaAngle(a,b) = a-b. For runway=0, wind from east (blows west, toward=270):
        // old: DeltaAngle(270, 0)=-90, sin(-90°)=-1 → negative. Should be positive (from right).
        // Fix: negate the angle so right-side wind is positive.
        double angleDiff = DeltaAngle(runwayHeading, windToward);

        double headwind = windSpeed * Math.Cos(ToRad(angleDiff));
        double crosswind = windSpeed * Math.Sin(ToRad(angleDiff));

        return (headwind, crosswind);
    }

    // =====================================================
    // HISTORY BUFFER
    // =====================================================

    private void AddToHistory(TelemetrySnapshot snap)
    {
        _historyBuffer.Enqueue(snap);
        // MAJOR-03: _bufferCount++ is not atomic. Use Interlocked to avoid race with ClearHistory.
        Interlocked.Increment(ref _bufferCount);

        // Trim oldest entries when exceeding max
        while (_bufferCount > MAX_BUFFER_SIZE)
        {
            if (_historyBuffer.TryDequeue(out _))
                Interlocked.Decrement(ref _bufferCount);
        }
    }

    /// <summary>Returns a snapshot of the current telemetry history.</summary>
    public List<TelemetrySnapshot> GetHistory(int maxSeconds = 600)
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-maxSeconds);
        return _historyBuffer
            .Where(s => s.Timestamp >= cutoff)
            .ToList();
    }

    /// <summary>Returns the last N snapshots.</summary>
    public List<TelemetrySnapshot> GetLastN(int count)
    {
        return _historyBuffer.TakeLast(count).ToList();
    }

    /// <summary>Returns the latest snapshot or null.</summary>
    public TelemetrySnapshot? GetLatest() => _previousSnapshot;

    /// <summary>Clears the history buffer (call on flight start)</summary>
    public void ClearHistory()
    {
        while (_historyBuffer.TryDequeue(out _)) {}
        // MAJOR-03: Use Interlocked.Exchange to atomically zero the counter
        Interlocked.Exchange(ref _bufferCount, 0);
        _previousSnapshot = null;
        _vsFilter.Reset();
        _iasFilter.Reset();
        Log.Information("[TelemetryProcessor] History buffer cleared");
    }

    // =====================================================
    // MATH UTILITIES
    // =====================================================

    private static double DeltaAngle(double a, double b)
    {
        double diff = ((a - b) + 360) % 360;
        return diff > 180 ? diff - 360 : diff;
    }

    private static double ToRad(double deg) => deg * Math.PI / 180.0;
}

// ============================================================
// EXPONENTIAL MOVING AVERAGE FILTER
// Used to smooth noisy telemetry signals
// ============================================================

public class ExponentialFilter
{
    private readonly double _alpha;  // Smoothing factor (0=no smoothing, 1=no filter)
    private double _value;
    private bool _initialized;

    public ExponentialFilter(double alpha)
    {
        _alpha = alpha;
    }

    public double Filter(double input)
    {
        if (!_initialized)
        {
            _value = input;
            _initialized = true;
        }
        else
        {
            _value = _alpha * input + (1 - _alpha) * _value;
        }
        return _value;
    }

    public void Reset()
    {
        _initialized = false;
        _value = 0;
    }
}

// ============================================================
// DERIVATIVE CALCULATOR
// Used to compute acceleration, rate of VS change, etc.
// ============================================================

public class DerivativeCalculator
{
    private double _prevValue;
    private DateTime _prevTime;
    private bool _initialized;

    public double Calculate(double currentValue, DateTime currentTime)
    {
        if (!_initialized)
        {
            _prevValue = currentValue;
            _prevTime = currentTime;
            _initialized = true;
            return 0;
        }

        double dt = (currentTime - _prevTime).TotalSeconds;
        if (dt < 0.001) return 0;

        double rate = (currentValue - _prevValue) / dt;
        _prevValue = currentValue;
        _prevTime = currentTime;
        return rate;
    }

    public void Reset()
    {
        _initialized = false;
    }
}
