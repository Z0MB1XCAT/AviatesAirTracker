// ============================================================
// AVIATES PERFORMANCE — V-SPEED CALCULATION MODULE
//
// Computes V1, VR, V2 from first principles using:
//   - Lift equation: L = ½ ρ V² S CL
//   - Stall speed scaled by weight and density
//   - Regulatory multipliers per CS-25 / FAR Part 25
//   - Runway and wind corrections
// ============================================================

(function () {
    'use strict';

    window.AviatesPerf = window.AviatesPerf || {};

    var density = window.AviatesPerf.density;
    var G       = density.G;   // 9.80665 m/s²
    var RHO0    = density.RHO0; // 1.225 kg/m³

    // Unit conversion
    var MPS_TO_KTS = 1.94384;

    /**
     * Stall speed (kts) for a given configuration.
     *
     * Uses the lift equation solved for V:
     *   Vs = sqrt(2 * W * g / (ρ * S * CL_max))
     *
     * @param {number} weightKg   - actual takeoff weight (kg)
     * @param {number} rho        - air density at runway (kg/m³)
     * @param {number} wingArea   - reference wing area (m²)
     * @param {number} clMax      - maximum lift coefficient at this flap setting
     * @returns {number} stall speed in knots (1g, unbanked)
     */
    function stallSpeed(weightKg, rho, wingArea, clMax) {
        var vs_mps = Math.sqrt((2 * weightKg * G) / (rho * wingArea * clMax));
        return vs_mps * MPS_TO_KTS;
    }

    /**
     * V2 minimum speed (kts).
     * CS-25.149: V2 ≥ 1.13 × Vs (2-engine)
     * In practice operators target 1.18–1.22 × Vs.
     * We use 1.20 × Vs as a representative value.
     */
    function v2Speed(vs) {
        return vs * 1.20;
    }

    /**
     * VR (rotation speed) in knots.
     * VR must be ≥ V1 and typically 4–6 kts below V2.
     * We model it as V2 − Δ, where Δ depends on aircraft weight fraction.
     *
     * @param {number} v2         - computed V2 (kts)
     * @param {number} weightKg   - actual TOW
     * @param {number} mtow       - max TOW from config
     * @returns {number}
     */
    function vrSpeed(v2, weightKg, mtow) {
        // Heavier aircraft: VR closer to V2 (higher inertia → longer rotation)
        var wFraction = weightKg / mtow;
        var delta     = 4 + (1 - wFraction) * 4; // 4–8 kts below V2
        return v2 - delta;
    }

    /**
     * V1 (takeoff decision speed) in knots.
     *
     * V1 must satisfy:
     *   - V1 ≤ VR
     *   - V1 ≥ Vmcg (minimum control speed on ground)
     *   - Reduced on short runways (less stopping distance available)
     *   - Increased with tailwind (higher groundspeed at V1)
     *
     * Runway factor: every 100 m below 2,500 m reduces V1 by ~0.4 kts,
     * capped at −10 kts reduction vs VR.
     *
     * Wind factor: headwind reduces V1 by 0.3 × hw component (capped at −5 kts);
     * tailwind increases V1 by 0.5 × |tw| (capped at +8 kts).
     *
     * @param {number} vr            - computed VR (kts)
     * @param {number} vmcgMin       - minimum V1 from config (kts)
     * @param {number} runwayLengthM - available takeoff distance (m)
     * @param {number} windKts       - wind component (positive = headwind)
     */
    function v1Speed(vr, vmcgMin, runwayLengthM, windKts) {
        // Base: V1 is typically 2–4 kts below VR on a long runway
        var v1 = vr - 3;

        // Runway correction — short runways need lower V1 to allow abort
        if (runwayLengthM < 2500) {
            var shortfall = (2500 - runwayLengthM) / 100; // in 100-m increments
            v1 -= Math.min(shortfall * 0.5, 10);
        }

        // Wind correction
        if (windKts > 0) {
            // Headwind: slight V1 reduction (accelerates to V1 faster → more runway used)
            v1 -= Math.min(windKts * 0.25, 5);
        } else if (windKts < 0) {
            // Tailwind: must raise V1 (higher groundspeed at same IAS)
            v1 += Math.min(Math.abs(windKts) * 0.45, 8);
        }

        // Regulatory floor
        return Math.max(v1, vmcgMin);
    }

    /**
     * Full V-speed computation.
     *
     * @param {object} cfg          - aircraft config from AviatesPerf.configs
     * @param {number} weightKg     - takeoff weight (kg)
     * @param {number} rho          - air density at runway (kg/m³)
     * @param {number} clMax        - CL_max for selected flap setting
     * @param {number} runwayLengthM
     * @param {number} windKts      - positive = headwind
     * @param {string} condition    - 'DRY' | 'WET'
     * @returns {{ v1, vr, v2, vs }}
     */
    function calculateVSpeeds(cfg, weightKg, rho, clMax, runwayLengthM, windKts, condition) {
        // Wet runway: CLmax is slightly degraded due to aquaplaning risk → ~5% reduction
        var clEff = condition === 'WET' ? clMax * 0.96 : clMax;

        var vs = stallSpeed(weightKg, rho, cfg.wingArea, clEff);
        var v2 = v2Speed(vs);
        var vr = vrSpeed(v2, weightKg, cfg.mtow);
        var v1 = v1Speed(vr, cfg.vmcgMin, runwayLengthM, windKts);

        // Sanity bounds:
        // V1 must not exceed VR
        v1 = Math.min(v1, vr);
        // V2 min hard floor: 1.10 × Vs (absolute minimum per regulations)
        v2 = Math.max(v2, vs * 1.10);
        // Upper bound: VR ≤ 1.05 × V2 (unusual but possible on very short runway)
        vr = Math.min(vr, v2 * 1.05);

        return {
            vs: Math.round(vs),
            v1: Math.round(v1),
            vr: Math.round(vr),
            v2: Math.round(v2),
        };
    }

    // ── Export ───────────────────────────────────────────────
    window.AviatesPerf.airspeed = {
        stallSpeed:      stallSpeed,
        v2Speed:         v2Speed,
        vrSpeed:         vrSpeed,
        v1Speed:         v1Speed,
        calculateVSpeeds: calculateVSpeeds,
    };

}());
