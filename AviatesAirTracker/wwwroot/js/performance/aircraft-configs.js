// ============================================================
// AVIATES PERFORMANCE — AIRCRAFT DATABASE
// Physics-calibrated constants for each supported type.
//
// Each entry defines:
//   family          'airbus' | 'boeing'
//   mtow            max takeoff weight (kg)
//   mlw             max landing weight (kg)
//   wingArea        reference wing area (m²)
//   thrustSL        total sea-level static thrust, both engines (kN)
//   thrustDecayPerC fraction of thrust lost per °C above ISA at SL
//   clMaxFlap       CL_max at the recommended departure flap setting
//   refStallKts     reference stall speed at MTOW, ISA SL, departure flap (kts)
//                   Pre-computed for validation; the engine recomputes from physics.
//   flapOptions     array of {label, clMax, name} in ascending lift order
//   performanceType 'flex' (Airbus) | 'derate' (Boeing)
//   maxFlexDelta    max flex temperature rise above OAT (°C)
//   derateOptions   (Boeing only) array of {label, thrustFraction}
//   vmcgMin         minimum V1 / Vmcg (kts)
//   vref25          Vref at 25° flaps / MLW, ISA SL (kts) — cross-check
// ============================================================

(function () {
    'use strict';

    window.AviatesPerf = window.AviatesPerf || {};

    window.AviatesPerf.configs = {

        // ── Airbus A318 ─────────────────────────────────────────
        A318: {
            family: 'airbus', performanceType: 'flex',
            mtow: 68000, mlw: 57500, wingArea: 122.6,
            thrustSL: 196.0, thrustDecayPerC: 0.0065,
            clMaxFlap: 2.20, refStallKts: 118,
            flapOptions: [
                { label: 'CONF 1', name: '1',  clMax: 1.95 },
                { label: 'CONF 2', name: '2',  clMax: 2.20 },
                { label: 'CONF 3', name: '3',  clMax: 2.40 },
            ],
            maxFlexDelta: 30, vmcgMin: 100,
        },

        // ── Airbus A319ceo ───────────────────────────────────────
        A319: {
            family: 'airbus', performanceType: 'flex',
            mtow: 75500, mlw: 62500, wingArea: 122.6,
            thrustSL: 222.4, thrustDecayPerC: 0.0065,
            clMaxFlap: 2.20, refStallKts: 123,
            flapOptions: [
                { label: 'CONF 1', name: '1',  clMax: 1.95 },
                { label: 'CONF 2', name: '2',  clMax: 2.20 },
                { label: 'CONF 3', name: '3',  clMax: 2.40 },
            ],
            maxFlexDelta: 30, vmcgMin: 105,
        },

        // ── Airbus A320ceo ───────────────────────────────────────
        A320: {
            family: 'airbus', performanceType: 'flex',
            mtow: 77000, mlw: 64500, wingArea: 122.6,
            thrustSL: 240.6, thrustDecayPerC: 0.0065,
            clMaxFlap: 2.20, refStallKts: 128,
            flapOptions: [
                { label: 'CONF 1', name: '1',  clMax: 1.95 },
                { label: 'CONF 2', name: '2',  clMax: 2.20 },
                { label: 'CONF 3', name: '3',  clMax: 2.40 },
            ],
            maxFlexDelta: 30, vmcgMin: 107,
        },

        // ── Airbus A320neo ───────────────────────────────────────
        A20N: {
            family: 'airbus', performanceType: 'flex',
            mtow: 79000, mlw: 67400, wingArea: 122.6,
            thrustSL: 246.6, thrustDecayPerC: 0.0060,
            clMaxFlap: 2.20, refStallKts: 129,
            flapOptions: [
                { label: 'CONF 1', name: '1',  clMax: 1.95 },
                { label: 'CONF 2', name: '2',  clMax: 2.20 },
                { label: 'CONF 3', name: '3',  clMax: 2.40 },
            ],
            maxFlexDelta: 32, vmcgMin: 107,
        },

        // ── Airbus A321ceo ───────────────────────────────────────
        A321: {
            family: 'airbus', performanceType: 'flex',
            mtow: 93500, mlw: 77800, wingArea: 122.6,
            thrustSL: 270.2, thrustDecayPerC: 0.0065,
            clMaxFlap: 2.20, refStallKts: 140,
            flapOptions: [
                { label: 'CONF 1', name: '1',  clMax: 1.95 },
                { label: 'CONF 2', name: '2',  clMax: 2.20 },
                { label: 'CONF 3', name: '3',  clMax: 2.40 },
            ],
            maxFlexDelta: 28, vmcgMin: 112,
        },

        // ── Airbus A321neo ───────────────────────────────────────
        A21N: {
            family: 'airbus', performanceType: 'flex',
            mtow: 97000, mlw: 79200, wingArea: 122.6,
            thrustSL: 280.0, thrustDecayPerC: 0.0058,
            clMaxFlap: 2.20, refStallKts: 143,
            flapOptions: [
                { label: 'CONF 1', name: '1',  clMax: 1.95 },
                { label: 'CONF 2', name: '2',  clMax: 2.20 },
                { label: 'CONF 3', name: '3',  clMax: 2.40 },
            ],
            maxFlexDelta: 30, vmcgMin: 113,
        },

        // ── Airbus A330-200 ──────────────────────────────────────
        A332: {
            family: 'airbus', performanceType: 'flex',
            mtow: 242000, mlw: 182000, wingArea: 361.6,
            thrustSL: 622.0, thrustDecayPerC: 0.0055,
            clMaxFlap: 2.15, refStallKts: 140,
            flapOptions: [
                { label: 'CONF 1', name: '1',  clMax: 1.90 },
                { label: 'CONF 2', name: '2',  clMax: 2.15 },
                { label: 'CONF 3', name: '3',  clMax: 2.30 },
            ],
            maxFlexDelta: 28, vmcgMin: 120,
        },

        // ── Airbus A330-300 ──────────────────────────────────────
        A333: {
            family: 'airbus', performanceType: 'flex',
            mtow: 242000, mlw: 187000, wingArea: 361.6,
            thrustSL: 622.0, thrustDecayPerC: 0.0055,
            clMaxFlap: 2.15, refStallKts: 140,
            flapOptions: [
                { label: 'CONF 1', name: '1',  clMax: 1.90 },
                { label: 'CONF 2', name: '2',  clMax: 2.15 },
                { label: 'CONF 3', name: '3',  clMax: 2.30 },
            ],
            maxFlexDelta: 28, vmcgMin: 120,
        },

        // ── Airbus A350-900 ──────────────────────────────────────
        A359: {
            family: 'airbus', performanceType: 'flex',
            mtow: 280000, mlw: 205000, wingArea: 442.0,
            thrustSL: 756.4, thrustDecayPerC: 0.0050,
            clMaxFlap: 2.10, refStallKts: 145,
            flapOptions: [
                { label: 'CONF 1', name: '1',  clMax: 1.85 },
                { label: 'CONF 2', name: '2',  clMax: 2.10 },
                { label: 'CONF 3', name: '3',  clMax: 2.28 },
            ],
            maxFlexDelta: 30, vmcgMin: 126,
        },

        // ── Airbus A350-1000 ─────────────────────────────────────
        A35K: {
            family: 'airbus', performanceType: 'flex',
            mtow: 316000, mlw: 233000, wingArea: 442.0,
            thrustSL: 800.0, thrustDecayPerC: 0.0050,
            clMaxFlap: 2.10, refStallKts: 150,
            flapOptions: [
                { label: 'CONF 1', name: '1',  clMax: 1.85 },
                { label: 'CONF 2', name: '2',  clMax: 2.10 },
                { label: 'CONF 3', name: '3',  clMax: 2.28 },
            ],
            maxFlexDelta: 30, vmcgMin: 128,
        },

        // ── Boeing 737-700 ───────────────────────────────────────
        B737: {
            family: 'boeing', performanceType: 'derate',
            mtow: 70080, mlw: 58060, wingArea: 125.0,
            thrustSL: 214.6, thrustDecayPerC: 0.0068,
            clMaxFlap: 2.10, refStallKts: 121,
            flapOptions: [
                { label: 'Flaps 1',  name: '1',  clMax: 1.70 },
                { label: 'Flaps 5',  name: '5',  clMax: 1.85 },
                { label: 'Flaps 10', name: '10', clMax: 2.00 },
                { label: 'Flaps 15', name: '15', clMax: 2.10 },
            ],
            derateOptions: [
                { label: 'TO',    thrustFraction: 1.00 },
                { label: 'TO 1', thrustFraction: 0.90 },
                { label: 'TO 2', thrustFraction: 0.80 },
            ],
            maxFlexDelta: 0, vmcgMin: 100,
        },

        // ── Boeing 737-800 ───────────────────────────────────────
        B738: {
            family: 'boeing', performanceType: 'derate',
            mtow: 79016, mlw: 65317, wingArea: 125.0,
            thrustSL: 242.8, thrustDecayPerC: 0.0068,
            clMaxFlap: 2.10, refStallKts: 128,
            flapOptions: [
                { label: 'Flaps 1',  name: '1',  clMax: 1.72 },
                { label: 'Flaps 5',  name: '5',  clMax: 1.87 },
                { label: 'Flaps 10', name: '10', clMax: 2.00 },
                { label: 'Flaps 15', name: '15', clMax: 2.10 },
            ],
            derateOptions: [
                { label: 'TO',    thrustFraction: 1.00 },
                { label: 'TO 1', thrustFraction: 0.90 },
                { label: 'TO 2', thrustFraction: 0.80 },
            ],
            maxFlexDelta: 0, vmcgMin: 103,
        },

        // ── Boeing 737-900ER ─────────────────────────────────────
        B739: {
            family: 'boeing', performanceType: 'derate',
            mtow: 85139, mlw: 66361, wingArea: 125.0,
            thrustSL: 242.8, thrustDecayPerC: 0.0068,
            clMaxFlap: 2.10, refStallKts: 132,
            flapOptions: [
                { label: 'Flaps 1',  name: '1',  clMax: 1.72 },
                { label: 'Flaps 5',  name: '5',  clMax: 1.87 },
                { label: 'Flaps 10', name: '10', clMax: 2.00 },
                { label: 'Flaps 15', name: '15', clMax: 2.10 },
            ],
            derateOptions: [
                { label: 'TO',    thrustFraction: 1.00 },
                { label: 'TO 1', thrustFraction: 0.90 },
                { label: 'TO 2', thrustFraction: 0.80 },
            ],
            maxFlexDelta: 0, vmcgMin: 105,
        },

        // ── Boeing 737 MAX 8 ─────────────────────────────────────
        B38M: {
            family: 'boeing', performanceType: 'derate',
            mtow: 82191, mlw: 66361, wingArea: 125.0,
            thrustSL: 250.0, thrustDecayPerC: 0.0063,
            clMaxFlap: 2.10, refStallKts: 130,
            flapOptions: [
                { label: 'Flaps 1',  name: '1',  clMax: 1.72 },
                { label: 'Flaps 5',  name: '5',  clMax: 1.87 },
                { label: 'Flaps 10', name: '10', clMax: 2.00 },
                { label: 'Flaps 15', name: '15', clMax: 2.10 },
            ],
            derateOptions: [
                { label: 'TO',    thrustFraction: 1.00 },
                { label: 'TO 1', thrustFraction: 0.90 },
                { label: 'TO 2', thrustFraction: 0.80 },
            ],
            maxFlexDelta: 0, vmcgMin: 104,
        },

        // ── Boeing 777-300ER ─────────────────────────────────────
        B77W: {
            family: 'boeing', performanceType: 'derate',
            mtow: 352400, mlw: 251300, wingArea: 436.8,
            thrustSL: 924.0, thrustDecayPerC: 0.0055,
            clMaxFlap: 2.05, refStallKts: 155,
            flapOptions: [
                { label: 'Flaps 5',  name: '5',  clMax: 1.80 },
                { label: 'Flaps 15', name: '15', clMax: 2.05 },
                { label: 'Flaps 20', name: '20', clMax: 2.15 },
            ],
            derateOptions: [
                { label: 'TO',    thrustFraction: 1.00 },
                { label: 'TO 1', thrustFraction: 0.90 },
                { label: 'TO 2', thrustFraction: 0.80 },
            ],
            maxFlexDelta: 0, vmcgMin: 130,
        },

        // ── Boeing 787-8 ─────────────────────────────────────────
        B788: {
            family: 'boeing', performanceType: 'derate',
            mtow: 227930, mlw: 172365, wingArea: 325.0,
            thrustSL: 596.4, thrustDecayPerC: 0.0058,
            clMaxFlap: 2.10, refStallKts: 142,
            flapOptions: [
                { label: 'Flaps 5',  name: '5',  clMax: 1.85 },
                { label: 'Flaps 15', name: '15', clMax: 2.10 },
                { label: 'Flaps 20', name: '20', clMax: 2.20 },
            ],
            derateOptions: [
                { label: 'TO',    thrustFraction: 1.00 },
                { label: 'TO 1', thrustFraction: 0.90 },
                { label: 'TO 2', thrustFraction: 0.80 },
            ],
            maxFlexDelta: 0, vmcgMin: 122,
        },

        // ── Boeing 787-9 ─────────────────────────────────────────
        B789: {
            family: 'boeing', performanceType: 'derate',
            mtow: 254011, mlw: 192777, wingArea: 325.0,
            thrustSL: 620.0, thrustDecayPerC: 0.0058,
            clMaxFlap: 2.10, refStallKts: 148,
            flapOptions: [
                { label: 'Flaps 5',  name: '5',  clMax: 1.85 },
                { label: 'Flaps 15', name: '15', clMax: 2.10 },
                { label: 'Flaps 20', name: '20', clMax: 2.20 },
            ],
            derateOptions: [
                { label: 'TO',    thrustFraction: 1.00 },
                { label: 'TO 1', thrustFraction: 0.90 },
                { label: 'TO 2', thrustFraction: 0.80 },
            ],
            maxFlexDelta: 0, vmcgMin: 125,
        },

        // ── ATR 72-600 ───────────────────────────────────────────
        AT76: {
            family: 'airbus', performanceType: 'flex',
            mtow: 23000, mlw: 22350, wingArea: 61.0,
            thrustSL: 50.0, thrustDecayPerC: 0.0070,
            clMaxFlap: 2.30, refStallKts: 90,
            flapOptions: [
                { label: 'Flaps 15', name: '15', clMax: 2.05 },
                { label: 'Flaps 25', name: '25', clMax: 2.30 },
            ],
            maxFlexDelta: 20, vmcgMin: 80,
        },
    };

    // Fallback generic config for unrecognised types — uses median narrow-body values
    window.AviatesPerf.configs['DEFAULT'] = {
        family: 'airbus', performanceType: 'flex',
        mtow: 79000, mlw: 67000, wingArea: 125.0,
        thrustSL: 245.0, thrustDecayPerC: 0.0062,
        clMaxFlap: 2.15, refStallKts: 128,
        flapOptions: [
            { label: 'CONF 2', name: '2', clMax: 2.15 },
        ],
        maxFlexDelta: 28, vmcgMin: 107,
    };

}());
