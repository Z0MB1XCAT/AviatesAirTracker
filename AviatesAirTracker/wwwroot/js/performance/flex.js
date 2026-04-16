// ============================================================
// AVIATES PERFORMANCE — FLEX / DERATED THRUST MODULE
//
// Flex temperature (Airbus):
//   Simulates an assumed temperature thrust reduction.  The engine
//   is "told" the outside air is hotter than it really is, so the
//   FADEC commands less thrust — saving engine cycles.
//
//   We iterate assumed temperature upward from OAT, computing
//   the required balanced field length at reduced thrust, stopping
//   when the required distance approaches the available runway.
//
// Derate (Boeing TO 1 / TO 2):
//   Fixed thrust reduction levels.  We evaluate each derate tier
//   and return the most aggressive one that fits the runway.
//
// Approach to required takeoff distance (RTOD):
//   The empirical model is derived from the energy method:
//     RTOD ∝ (V2² × TOW) / (2 × Fnet × η)
//   where Fnet = thrust_reduced − rolling_resistance − drag
//         η    = acceleration efficiency (~0.85 for nominal conditions)
//   RTOD scales linearly with 1/thrust_ratio and with 1/density_ratio.
// ============================================================

(function () {
    'use strict';

    window.AviatesPerf = window.AviatesPerf || {};

    var density  = window.AviatesPerf.density;
    var airspeed = window.AviatesPerf.airspeed;

    // Rolling resistance coefficient (dry hard surface)
    var MU_DRY  = 0.015;
    var MU_WET  = 0.025;

    // Acceleration efficiency (accounts for varying acceleration profile)
    var ETA     = 0.82;

    // Safety margin: we will not use more than this fraction of runway
    var SAFETY_MARGIN = 0.94;

    /**
     * Required balanced field length (metres) at given conditions.
     *
     * @param {object} cfg        - aircraft config
     * @param {number} weightKg   - TOW
     * @param {number} v2         - V2 in kts
     * @param {number} rho        - air density (kg/m³)
     * @param {number} thrustN    - total net thrust (N) at takeoff rating
     * @param {string} condition  - 'DRY' | 'WET'
     * @returns {number} estimated RTOD in metres
     */
    function requiredDistance(cfg, weightKg, v2, rho, thrustN, condition) {
        var v2_mps = v2 / 1.94384; // kts → m/s
        var mu     = condition === 'WET' ? MU_WET : MU_DRY;
        var G      = density.G;

        // Rolling resistance force (N)
        var F_roll = mu * weightKg * G;

        // Approximate drag during roll (simplified as constant fraction of thrust)
        // Lift-drag ratio during ground roll ~8–12 for transport jets
        var liftDragRatio = 10;
        var F_drag_roll   = (weightKg * G / liftDragRatio) * 0.4;

        // Net accelerating force
        var F_net = thrustN - F_roll - F_drag_roll;

        if (F_net <= 0) return Infinity; // can't accelerate

        // RTOD from energy: d = (m × v²) / (2 × F_net × η)
        var rtod = (weightKg * v2_mps * v2_mps) / (2 * F_net * ETA);

        // Density correction: higher DA → longer roll
        var sigma  = rho / density.RHO0;
        rtod /= Math.sqrt(sigma); // partially accounts for increased groundspeed

        // Wet runway: 15% penalty (lower friction, possible aquaplaning)
        if (condition === 'WET') rtod *= 1.15;

        return rtod;
    }

    /**
     * Compute flex temperature (Airbus).
     *
     * Steps from OAT upward, 1°C at a time, computing the required
     * distance at reduced thrust.  Returns the highest safe assumed temp.
     *
     * @param {object} cfg           - Airbus aircraft config
     * @param {number} weightKg      - TOW
     * @param {number} v2            - V2 at selected flap (kts)
     * @param {number} oat           - actual OAT (°C)
     * @param {number} qnh           - QNH (hPa)
     * @param {number} elevationFt   - runway elevation (ft)
     * @param {number} runwayLengthM - TODA (m)
     * @param {string} condition     - 'DRY' | 'WET'
     * @returns {{ flexTemp, thrustFraction, rtod }}
     */
    function calculateFlex(cfg, weightKg, v2, oat, qnh, elevationFt, runwayLengthM, condition) {
        var rho0        = density.airDensity(oat, qnh, elevationFt);
        var thrustMaxN  = cfg.thrustSL * 1000; // kN → N
        var maxDa       = oat + cfg.maxFlexDelta;

        // Minimum flex: must save at least 2% thrust
        var minFlex = oat + 2;
        var bestFlex = null;

        for (var t = minFlex; t <= maxDa; t++) {
            // ISA deviation at this assumed temperature
            var isaTemp  = density.isaTemperature(elevationFt);
            var delta    = t - isaTemp;

            // Thrust reduction: each °C above ISA at SL costs ~thrustDecayPerC of SL thrust
            var thrustFraction = 1 - cfg.thrustDecayPerC * Math.max(delta, 0);

            // Absolute lower limit: FADEC will not go below 0.75 of rated thrust
            if (thrustFraction < 0.75) break;

            // Air density at assumed temperature (warmer → less dense → less thrust)
            var rho_assumed = density.airDensity(t, qnh, elevationFt);
            var thrustN     = thrustMaxN * thrustFraction;

            // Density-correct the thrust (turbofans are sensitive to inlet density)
            thrustN *= (rho_assumed / density.RHO0);
            thrustN  = Math.max(thrustN, thrustMaxN * 0.75);

            var rtod = requiredDistance(cfg, weightKg, v2, rho_assumed, thrustN, condition);

            if (rtod <= runwayLengthM * SAFETY_MARGIN) {
                bestFlex = {
                    flexTemp:       Math.round(t),
                    thrustFraction: thrustFraction,
                    rtod:           Math.round(rtod),
                };
            } else {
                // Required distance now exceeds available → stop
                break;
            }
        }

        // If no flex is possible, return no-flex result
        if (!bestFlex) {
            var rtod0 = requiredDistance(cfg, weightKg, v2, rho0, thrustMaxN * (rho0 / density.RHO0), condition);
            return {
                flexTemp:       null,
                thrustFraction: 1.0,
                rtod:           Math.round(rtod0),
            };
        }

        return bestFlex;
    }

    /**
     * Compute Boeing derate recommendation.
     *
     * Evaluates TO, TO 1, TO 2 and returns the most aggressive derate
     * that keeps required distance within available runway.
     *
     * @param {object} cfg           - Boeing aircraft config
     * @param {number} weightKg      - TOW
     * @param {number} v2            - V2 (kts)
     * @param {number} oat           - OAT (°C)
     * @param {number} qnh           - QNH (hPa)
     * @param {number} elevationFt   - runway elevation (ft)
     * @param {number} runwayLengthM - TODA (m)
     * @param {string} condition     - 'DRY' | 'WET'
     * @returns {{ derateLabel, thrustFraction, rtod }}
     */
    function calculateDerate(cfg, weightKg, v2, oat, qnh, elevationFt, runwayLengthM, condition) {
        var thrustMaxN = cfg.thrustSL * 1000;
        var rho        = density.airDensity(oat, qnh, elevationFt);
        var bestDerate = cfg.derateOptions[0]; // default = full TO

        for (var i = cfg.derateOptions.length - 1; i >= 0; i--) {
            var d          = cfg.derateOptions[i];
            var thrustN    = thrustMaxN * d.thrustFraction * (rho / density.RHO0);
            var rtod       = requiredDistance(cfg, weightKg, v2, rho, thrustN, condition);

            if (rtod <= runwayLengthM * SAFETY_MARGIN) {
                bestDerate = { label: d.label, thrustFraction: d.thrustFraction, rtod: Math.round(rtod) };
                break;
            }
        }

        if (!bestDerate.rtod) {
            var t0 = cfg.derateOptions[0];
            var rN = thrustMaxN * t0.thrustFraction * (rho / density.RHO0);
            bestDerate = { label: t0.label, thrustFraction: t0.thrustFraction, rtod: Math.round(requiredDistance(cfg, weightKg, v2, rho, rN, condition)) };
        }

        return bestDerate;
    }

    // ── Export ───────────────────────────────────────────────
    window.AviatesPerf.flex = {
        requiredDistance: requiredDistance,
        calculateFlex:    calculateFlex,
        calculateDerate:  calculateDerate,
    };

}());
