// ============================================================
// AVIATES PERFORMANCE — MAIN ENTRY POINT
//
// Exposes AviatesPerf.calculate(input) → result
//
// Input:
//   aircraftIcao      string   ICAO type code (e.g. 'A20N', 'B738')
//   takeoffWeightKg   number   actual TOW in kg
//   runwayLengthM     number   available takeoff run (m); default 2500
//   runwayElevationFt number   threshold elevation AMSL (ft); default 0
//   oat               number   outside air temperature (°C); default 15
//   qnh               number   QNH (hPa); default 1013.25
//   windKts           number   headwind component (kts); positive=HW
//   condition         string   'DRY' | 'WET'; default 'DRY'
//
// Output:
//   v1                number   knots (rounded)
//   vr                number   knots (rounded)
//   v2                number   knots (rounded)
//   vs                number   stall speed (rounded)
//   flaps             string   flap label (e.g. 'CONF 2' / 'Flaps 5')
//   flapName          string   short name used in brief (e.g. '2' / '5')
//   flexTemp          number|null  Airbus: assumed temperature (°C); null = TOGA
//   derateLabel       string|null  Boeing: derate label (e.g. 'TO 1'); null = TOGA
//   thrustFraction    number   1.0 = full, <1.0 = reduced
//   rtodM             number   estimated required TODA (m)
//   densityAltFt      number   density altitude (ft)
//   isaDevC           number   ISA deviation (°C)
//   pressureAltFt     number   pressure altitude (ft)
//   performanceType   string   'flex' | 'derate'
//   aircraftFamily    string   'airbus' | 'boeing'
//   configUsed        string   ICAO code actually looked up (may be DEFAULT)
//   warnings          string[] any advisory messages
// ============================================================

(function () {
    'use strict';

    window.AviatesPerf = window.AviatesPerf || {};

    var configs  = window.AviatesPerf.configs;
    var dens     = window.AviatesPerf.density;
    var asp      = window.AviatesPerf.airspeed;
    var flapsLib = window.AviatesPerf.flaps;
    var flexLib  = window.AviatesPerf.flex;

    /**
     * Resolve aircraft config from ICAO code.
     * Tries direct match, then strips suffix chars (A20N → A20, etc.).
     */
    function resolveConfig(icao) {
        if (!icao) return { cfg: configs['DEFAULT'], key: 'DEFAULT' };
        var key = icao.toUpperCase().trim();
        if (configs[key]) return { cfg: configs[key], key: key };
        // Try stripping trailing letter (some sims add variant suffix)
        var shorter = key.slice(0, -1);
        if (configs[shorter]) return { cfg: configs[shorter], key: shorter };
        return { cfg: configs['DEFAULT'], key: 'DEFAULT' };
    }

    /**
     * Parse a basic METAR string and extract OAT, QNH, and wind component.
     * Returns { oat, qnh, windKts, windDir } or null if not parseable.
     *
     * Handles standard formats:
     *   Temperature/dewpoint: "12/09" or "M02/M05"
     *   QNH: "Q1018" (hPa) or "A2992" (inHg × 100)
     *   Wind: "22018KT" or "VRB05KT"
     */
    function parseMetar(metar) {
        if (!metar) return null;
        var result = {};
        try {
            // Temperature: dd/dd or Mdd/Mdd (M = minus)
            var tempMatch = metar.match(/\b(M?\d{2})\/(M?\d{2})\b/);
            if (tempMatch) {
                var raw = tempMatch[1].replace('M', '-');
                result.oat = parseInt(raw, 10);
            }

            // QNH hPa: Q followed by 4 digits
            var qnhMatch = metar.match(/\bQ(\d{4})\b/);
            if (qnhMatch) {
                result.qnh = parseInt(qnhMatch[1], 10);
            } else {
                // Altimeter inHg: A followed by 4 digits (US format)
                var altMatch = metar.match(/\bA(\d{4})\b/);
                if (altMatch) {
                    result.qnh = Math.round(parseInt(altMatch[1], 10) / 100 * 33.8639);
                }
            }

            // Wind: dddssKT where ddd=direction, ss=speed
            var windMatch = metar.match(/\b(\d{3}|VRB)(\d{2,3})(G\d{2,3})?KT\b/);
            if (windMatch && windMatch[1] !== 'VRB') {
                result.windDir = parseInt(windMatch[1], 10);
                result.windSpeedKts = parseInt(windMatch[2], 10);
                // Wind component along runway requires runway heading
                // Store raw values; caller must project onto runway heading
                result.windKts = result.windSpeedKts; // default: treat as headwind
            }
        } catch (e) {}

        return Object.keys(result).length > 0 ? result : null;
    }

    /**
     * Compute headwind component given wind speed, wind direction, and runway heading.
     * Positive = headwind, negative = tailwind.
     */
    function headwindComponent(windSpeedKts, windDirDeg, runwayHeadingDeg) {
        if (!windSpeedKts || windSpeedKts === 0) return 0;
        var angle = (windDirDeg - runwayHeadingDeg) * Math.PI / 180;
        return windSpeedKts * Math.cos(angle);
    }

    /**
     * Crosswind component (positive = from left).
     */
    function crosswindComponent(windSpeedKts, windDirDeg, runwayHeadingDeg) {
        if (!windSpeedKts || windSpeedKts === 0) return 0;
        var angle = (windDirDeg - runwayHeadingDeg) * Math.PI / 180;
        return windSpeedKts * Math.sin(angle);
    }

    /**
     * Main calculation entry point.
     *
     * @param {object} input - see module header for fields
     * @returns {object}     - see module header for output
     */
    function calculate(input) {
        // ── Validate & default inputs ────────────────────────
        input = input || {};
        var icao            = input.aircraftIcao || '';
        var weightKg        = parseFloat(input.takeoffWeightKg) || 65000;
        var runwayLengthM   = parseFloat(input.runwayLengthM)   || 2500;
        var elevationFt     = parseFloat(input.runwayElevationFt) || 0;
        var oat             = input.oat !== undefined ? parseFloat(input.oat) : 15;
        var qnh             = parseFloat(input.qnh)  || 1013.25;
        var windKts         = parseFloat(input.windKts) || 0;
        var condition       = (input.condition || 'DRY').toUpperCase();
        var warnings        = [];

        // ── Resolve aircraft config ──────────────────────────
        var resolved   = resolveConfig(icao);
        var cfg        = resolved.cfg;
        var configUsed = resolved.key;

        if (configUsed === 'DEFAULT') {
            warnings.push('Aircraft type "' + icao + '" not in performance database — using generic narrow-body values. Results are indicative only.');
        }

        // ── Sanity checks ────────────────────────────────────
        if (weightKg > cfg.mtow) {
            warnings.push('TOW ' + weightKg + ' kg exceeds MTOW ' + cfg.mtow + ' kg — performance may be unachievable.');
        }
        if (oat > 55) {
            warnings.push('OAT ' + oat + '°C is above certified envelope for most aircraft.');
        }
        if (condition === 'WET') {
            warnings.push('WET runway — performance degraded. Verify hydroplaning speed.');
        }
        if (windKts < -5) {
            warnings.push('Tailwind component ' + Math.abs(Math.round(windKts)) + ' KT — takeoff performance significantly reduced.');
        }

        // ── Atmospheric conditions ───────────────────────────
        var rho          = dens.airDensity(oat, qnh, elevationFt);
        var daFt         = dens.densityAltitudeFt(oat, qnh, elevationFt);
        var isaDevC      = dens.isaDeviation(oat, elevationFt);
        var pressAltFt   = dens.pressureAltitudeFt(qnh, elevationFt);

        if (daFt > 8000) {
            warnings.push('Density altitude ' + Math.round(daFt) + ' ft — expect significant performance degradation.');
        }

        // ── Flap selection ───────────────────────────────────
        var flapOpt = flapsLib.selectFlaps(cfg, weightKg, runwayLengthM, condition);

        // ── V-speeds ─────────────────────────────────────────
        var speeds = asp.calculateVSpeeds(
            cfg, weightKg, rho,
            flapOpt.clMax, runwayLengthM, windKts, condition
        );

        // ── Flex / derate ────────────────────────────────────
        var flexResult   = null;
        var derateResult = null;

        if (cfg.performanceType === 'flex') {
            flexResult = flexLib.calculateFlex(
                cfg, weightKg, speeds.v2, oat, qnh, elevationFt, runwayLengthM, condition
            );
        } else {
            derateResult = flexLib.calculateDerate(
                cfg, weightKg, speeds.v2, oat, qnh, elevationFt, runwayLengthM, condition
            );
        }

        // ── Build output ─────────────────────────────────────
        return {
            v1:             speeds.v1,
            vr:             speeds.vr,
            v2:             speeds.v2,
            vs:             speeds.vs,
            flaps:          flapOpt.label,
            flapName:       flapOpt.name,
            flexTemp:       flexResult  ? flexResult.flexTemp       : null,
            derateLabel:    derateResult ? derateResult.label        : null,
            thrustFraction: flexResult  ? flexResult.thrustFraction
                          : derateResult ? derateResult.thrustFraction : 1.0,
            rtodM:          flexResult  ? flexResult.rtod
                          : derateResult ? derateResult.rtod : null,
            densityAltFt:   Math.round(daFt),
            isaDevC:        Math.round(isaDevC * 10) / 10,
            pressureAltFt:  Math.round(pressAltFt),
            performanceType: cfg.performanceType,
            aircraftFamily:  cfg.family,
            configUsed:      configUsed,
            warnings:        warnings,
        };
    }

    // ── Export ───────────────────────────────────────────────
    window.AviatesPerf.calculate      = calculate;
    window.AviatesPerf.parseMetar     = parseMetar;
    window.AviatesPerf.headwindComponent  = headwindComponent;
    window.AviatesPerf.crosswindComponent = crosswindComponent;

}());
