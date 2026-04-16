// ============================================================
// AVIATES PERFORMANCE — DENSITY & ATMOSPHERE MODULE
//
// Implements ISA (International Standard Atmosphere) and
// derived quantities used by all other performance modules.
// ============================================================

(function () {
    'use strict';

    window.AviatesPerf = window.AviatesPerf || {};

    // Physical constants
    var G     = 9.80665;   // m/s²
    var R     = 287.058;   // J/(kg·K) — specific gas constant for dry air
    var P0    = 101325;    // Pa — ISA sea-level pressure
    var T0    = 288.15;    // K  — ISA sea-level temperature (15 °C)
    var L     = 0.0065;    // K/m — ISA lapse rate in troposphere
    var RHO0  = 1.22500;   // kg/m³ — ISA sea-level density

    // Pressure exponent for ISA: g / (R * L)
    var PEXP  = G / (R * L); // ≈ 5.2561

    /**
     * ISA temperature at a given geometric altitude (feet).
     * Returns °C.
     */
    function isaTemperature(elevationFt) {
        var h = elevationFt * 0.3048; // convert to metres
        return (T0 - L * h) - 273.15;
    }

    /**
     * ISA pressure (Pa) at a given geometric altitude (feet).
     * Uses hypsometric formula valid below the tropopause (36,090 ft).
     */
    function isaPressurePa(elevationFt) {
        var h = elevationFt * 0.3048;
        var T = T0 - L * h;
        return P0 * Math.pow(T / T0, PEXP);
    }

    /**
     * Actual air density (kg/m³) at the runway given:
     *   oat        - outside air temperature (°C)
     *   qnh        - altimeter setting (hPa)
     *   elevationFt - runway elevation above MSL (ft)
     */
    function airDensity(oat, qnh, elevationFt) {
        // Station pressure: QNH reduced to field elevation
        // Using the simplified barometric formula from ICAO Doc 8643
        var elev_m  = elevationFt * 0.3048;
        var T_k     = oat + 273.15;
        var T_avg_k = T_k + L * elev_m / 2; // average layer temperature

        // Pressure at field elevation from QNH
        var P_field = qnh * 100 * Math.pow(
            1 - (L * elev_m) / (T0 + L * elev_m + L * elev_m),
            PEXP
        );
        // Simpler but accurate enough approximation:
        P_field = qnh * 100 * Math.exp(-G * elev_m / (R * T_avg_k));

        return P_field / (R * T_k);
    }

    /**
     * Density altitude (feet) — the altitude in the ISA that has the
     * same density as the current conditions.
     */
    function densityAltitudeFt(oat, qnh, elevationFt) {
        var rho    = airDensity(oat, qnh, elevationFt);
        var sigma  = rho / RHO0;  // density ratio

        // Invert ISA density formula: h = T0/L * (1 - sigma^(1/(PEXP-1)))
        // Using simplified approximation (accurate to ±200 ft within envelope):
        var da_ft = 145442.16 * (1 - Math.pow(sigma, 0.235));
        return da_ft;
    }

    /**
     * Density ratio σ = ρ / ρ₀ at the runway.
     */
    function densityRatio(oat, qnh, elevationFt) {
        return airDensity(oat, qnh, elevationFt) / RHO0;
    }

    /**
     * Temperature deviation from ISA at the runway (°C).
     * Positive = hotter than ISA.
     */
    function isaDeviation(oat, elevationFt) {
        return oat - isaTemperature(elevationFt);
    }

    /**
     * Pressure altitude (feet) — altitude in ISA corresponding to the
     * given QNH setting.  Used for altimetry cross-check.
     */
    function pressureAltitudeFt(qnh, elevationFt) {
        // Each hPa difference from 1013.25 is approximately 27 ft at low altitude
        return elevationFt + (1013.25 - qnh) * 27;
    }

    // ── Export ───────────────────────────────────────────────
    window.AviatesPerf.density = {
        isaTemperature:    isaTemperature,
        isaPressurePa:     isaPressurePa,
        airDensity:        airDensity,
        densityAltitudeFt: densityAltitudeFt,
        densityRatio:      densityRatio,
        isaDeviation:      isaDeviation,
        pressureAltitudeFt: pressureAltitudeFt,
        // Expose constants for other modules
        G: G, R: R, RHO0: RHO0,
    };

}());
