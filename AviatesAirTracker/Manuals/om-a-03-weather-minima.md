---
id: om-a-03
manual: OM-A
chapter: 3
title: Weather, Minima & Runway Condition
summary: Planning minima for destinations and alternates, takeoff and landing minima, crosswind and tailwind policy by runway condition code (RWYCC), low-visibility operations and the Global Reporting Format.
tags: weather, minima, alternate, crosswind, tailwind, rwycc, grf, low visibility, lvp, contaminated
revision: 0
effective: 2026-09-24
order: 3
---
## Planning minima

An aerodrome may be planned as a **destination** or **destination alternate** only if the forecast (TAF, including TEMPO/PROB groups) for **one hour before to one hour after** the expected arrival time is at or above:

| Approach available | Destination | Destination alternate |
|---|---|---|
| Precision approach (ILS CAT I / GLS / LPV) | Published minima | DA/H **+ 200 ft** and RVR/VIS **+ 800 m** |
| Non-precision (RNP LNAV / LNAV/VNAV, VOR, NDB) | Published minima | MDA/H **+ 400 ft** and RVR/VIS **+ 1,500 m** |
| Circling | Published circling minima | Circling minima **+ 400 ft / + 1,500 m** |

A **takeoff alternate** within one hour's flying time (one engine inoperative, still air; two hours for the A350, 777F, 747-8 and A380) is required when departure weather is below the landing minima for the departure runway.

## Takeoff and approach minima

- **Takeoff:** minimum RVR/visibility **400 m** with centreline lighting; **550 m** without. Lower values need low-visibility procedures (LVP) in force at the airport, where the simulator scenery and network ATC support them.
- **Approach:** fly to the minima published on the chart. Set the DA/MDA on the minimums selector during the approach briefing.
- **Approach ban:** do not continue an approach below **1,000 ft AAL** if the reported RVR/visibility is below the required minimum.

## Crosswind and tailwind

Company crosswind guidance is based on the **runway condition code (RWYCC)** and matches the EFB Arrival Perf warnings. Never exceed the aircraft's demonstrated crosswind (your add-on's FCOM).

| RWYCC | Runway description | Max crosswind (incl. gusts) |
|---|---|---|
| **6** | Dry | **33 kt** |
| **5** | Wet (≤ 3 mm water), frost | **33 kt** |
| **4** | Compacted snow (≤ −15 °C OAT) | **25 kt** |
| **3** | Wet "slippery", dry snow or wet snow over compacted snow | **25 kt** |
| **2** | Standing water or slush (> 3 mm) | **15 kt** |
| **1** | Ice | **15 kt** |
| **0** | Wet ice, water on compacted snow | **Operations not permitted** |

- **Tailwind:** maximum **10 kt** for takeoff and landing unless the add-on's AFM allows more; never on a contaminated runway (RWYCC ≤ 4).
- Gust factor: add the full gust to the crosswind when comparing with the limit.

## Runway condition and the Global Reporting Format (GRF)

Runway state is reported as a **runway condition report (RCR)**: a RWYCC for each third of the runway (e.g. *RWYCC 5/5/3*), plus contaminant type, depth and coverage. Use the **lowest** code of the thirds you will use, and select it in EFB Arrival/Departure Perf. Pilots report braking action as **GOOD** (5), **GOOD TO MEDIUM** (4), **MEDIUM** (3), **MEDIUM TO POOR** (2), **POOR** (1) or **LESS THAN POOR** (0).

> **CAUTION** On contaminated runways, performance calculations must assume **no reverse thrust credit** for dispatch, and you should plan the **longest available runway**.

## Low-visibility operations

CAT II/III approaches are permitted only when:

- the aircraft and add-on support autoland to the required category,
- the airport publishes the approach and ATC declares LVP in force (online), and
- you brief the automatic callouts, minima (DH or alert height) and go-around.

## Thunderstorms, icing and windshear

- **Thunderstorms:** avoid active cells by at least **20 NM** laterally in cruise (10 NM below FL200). Never take off or land with a storm over the airport or on the approach path.
- **Icing:** use engine and wing anti-ice as the aircraft procedures require. Airframe de-icing is required before takeoff with frost, snow or ice on critical surfaces.
- **Windshear:** reported moderate or severe windshear, or a predictive windshear alert: **delay** the takeoff or approach. See the [QRH immediate actions](doc:qrh-00#windshear) if you encounter it.
