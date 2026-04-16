// ============================================================
// AVIATES PERFORMANCE — FLAP SELECTION MODULE
//
// Rule-based flap selection that accounts for:
//   - Runway length  (shorter → more lift → higher flap)
//   - Takeoff weight (heavier → more lift → higher flap)
//   - Runway condition (wet → prefer higher flap for lower speeds)
//   - Obstacle clearance margin (obstacle → higher flap for steeper climb)
//
// Returns the best flap option object from the aircraft config.
// ============================================================

(function () {
    'use strict';

    window.AviatesPerf = window.AviatesPerf || {};

    /**
     * Score a flap option based on current conditions.
     * Higher score = more appropriate for conditions.
     * We want the LOWEST flap setting that still gives safe margin.
     *
     * @param {object} flapOpt    - { label, name, clMax }
     * @param {number} flapIndex  - index in the config's flapOptions array
     * @param {number} totalFlaps - total number of options
     * @param {object} conditions - { runwayLengthM, weightFraction, condition }
     * @returns {number} score
     */
    function scoreFlap(flapOpt, flapIndex, totalFlaps, conditions) {
        var score = 0;
        var wf   = conditions.weightFraction;   // 0..1
        var rwy  = conditions.runwayLengthM;
        var wet  = conditions.condition === 'WET';

        // ── Runway length component ────────────────────────────
        // Short runways need more flap (higher CL, lower V2, shorter TODA needed)
        if (rwy < 1600) {
            score += (flapIndex / (totalFlaps - 1)) * 40;  // strongly favour max flap
        } else if (rwy < 2000) {
            score += (flapIndex / (totalFlaps - 1)) * 25;
        } else if (rwy < 2600) {
            score += (flapIndex / (totalFlaps - 1)) * 10;
        } else {
            // Long runway: prefer lower flap for better climb gradient & fuel
            score -= (flapIndex / (totalFlaps - 1)) * 8;
        }

        // ── Weight component ───────────────────────────────────
        // Heavy aircraft benefit more from extra flap to lower V2
        if (wf > 0.92) {
            score += (flapIndex / (totalFlaps - 1)) * 20;
        } else if (wf > 0.80) {
            score += (flapIndex / (totalFlaps - 1)) * 10;
        }

        // ── Wet runway component ──────────────────────────────
        // Wet favours higher flap (lower rotation speed, less tyre load)
        if (wet) {
            score += (flapIndex / (totalFlaps - 1)) * 12;
        }

        return score;
    }

    /**
     * Select the optimal flap setting for the given conditions.
     *
     * @param {object} cfg           - aircraft config
     * @param {number} weightKg      - takeoff weight (kg)
     * @param {number} runwayLengthM - available runway length (m)
     * @param {string} condition     - 'DRY' | 'WET'
     * @returns {object} best flap option { label, name, clMax }
     */
    function selectFlaps(cfg, weightKg, runwayLengthM, condition) {
        var options       = cfg.flapOptions;
        var totalFlaps    = options.length;
        var weightFraction = weightKg / cfg.mtow;

        var conditions = {
            runwayLengthM:  runwayLengthM,
            weightFraction: weightFraction,
            condition:      condition || 'DRY',
        };

        var bestOpt   = options[0];
        var bestScore = -Infinity;

        for (var i = 0; i < totalFlaps; i++) {
            var s = scoreFlap(options[i], i, totalFlaps, conditions);
            if (s > bestScore) {
                bestScore = s;
                bestOpt   = options[i];
            }
        }

        return bestOpt;
    }

    // ── Export ───────────────────────────────────────────────
    window.AviatesPerf.flaps = {
        selectFlaps: selectFlaps,
    };

}());
