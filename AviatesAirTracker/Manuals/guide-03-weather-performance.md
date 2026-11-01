---
id: guide-03
manual: GUIDE
chapter: 3
title: Weather & Performance Know-How
summary: Reading METARs and TAFs, flight categories, runway condition codes, and what really drives takeoff and landing performance.
tags: weather, metar, taf, decode, vfr, ifr, flight category, performance, density altitude, flex, assumed temperature, landing distance, rwycc
revision: 0
effective: 2026-09-24
order: 3
---
## Reading a METAR

`EGLL 241150Z 24012G22KT 9999 FEW035 BKN080 12/06 Q1014 TEMPO 4000 SHRA`

| Group | Meaning |
|---|---|
| `EGLL 241150Z` | London Heathrow, 24th at 11:50 UTC |
| `24012G22KT` | Wind from 240° at 12 kt, gusting 22 kt |
| `9999` | Visibility 10 km or more |
| `FEW035 BKN080` | Few cloud at 3,500 ft, broken at 8,000 ft (the **ceiling** is the lowest BKN or OVC layer) |
| `12/06` | Temperature 12 °C, dew point 6 °C |
| `Q1014` | QNH 1014 hPa (`A2992` = 29.92 inHg) |
| `TEMPO 4000 SHRA` | Temporarily 4,000 m in rain showers |

## Flight categories

| Category | Ceiling | Visibility |
|---|---|---|
| **VFR** | above 3,000 ft | more than 5 SM (8 km) |
| **MVFR** | 1,000–3,000 ft | 3–5 SM |
| **IFR** | 500–999 ft | 1–3 SM |
| **LIFR** | below 500 ft | less than 1 SM |

The EFB colours METARs by these categories, and highlights gusts, low ceilings and significant weather.

## Reading a TAF

TAFs forecast the weather for 24–30 hours. `BECMG` means a gradual change, `TEMPO` temporary fluctuations (under an hour at a time), `PROB30/40` a 30 or 40 % probability. For planning, the **worst** condition in the arrival window counts (see [OM-A 3](doc:om-a-03#planning-minima)).

## What drives takeoff performance

| Factor | Effect |
|---|---|
| **Weight** | Heavier: higher V-speeds and a longer runway needed. |
| **Density altitude** (elevation + temperature) | Hot and high: less thrust and lift, a longer takeoff and a lower FLEX temperature. |
| **Wind** | Headwind shortens the takeoff, tailwind lengthens it considerably. |
| **Runway condition** | Wet or contaminated: longer accelerate-stop distance, lower V1. |
| **Flap** | More flap: lower speeds and a shorter run, but worse climb gradient. |
| **Slope** | Uphill lengthens the takeoff run. |

**FLEX / assumed temperature:** the aircraft uses less than full thrust, as if it were hotter than it is, when the runway allows. That saves engine wear. EFB Departure Perf finds the highest FLEX temperature that still meets the runway requirement.

## What drives landing performance

The landing distance grows with **weight**, **approach speed** (which is why less flap means a longer landing; see [CN-003](doc:cn-003)), **tailwind**, **density altitude**, and above all **runway condition**. Braking on a POOR (RWYCC 1) runway can more than double the stopping distance of a dry one. EFB Arrival Perf shows the landing distance for every autobrake setting and warns when the margin is thin. Pick a setting with a comfortable margin, not the one that just fits.
