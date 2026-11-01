---
id: cn-003
manual: CN
chapter: CN-003
title: Minimum Landing Flap for the Boeing 737 and Airbus A320 Family
summary: 737 NG/MAX land with FLAPS 25 minimum (FLAPS 30 standard). A319/A320neo land with CONF 3 minimum (CONF FULL standard). Using the minimum is fully permitted and never counts against a PIREP.
tags: landing, flaps, b737, b738, a319, a320, a20n, conf 3, conf full, approach speed
revision: 0
effective: 2026-09-24
order: 3
ack: true
aircraft: b737ng, b737max, a320
---
> **WARNING** Status: **ACTIVE** until cancelled. Applies to the **B736, B738, B739ER, B38M, A319 and A320neo**.

## Restriction

| Fleet | Minimum landing flap | Standard landing flap | Also permitted |
|---|---|---|---|
| Boeing 737 NG / MAX | **FLAPS 25** | **FLAPS 30** | FLAPS 40 (short, wet or contaminated runways) |
| Airbus A319 / A320neo | **CONF 3** | **CONF FULL** | None |

Landing with **less** than the minimum (for example a 737 at FLAPS 15, or an A320 at CONF 2) is not permitted except when a non-normal procedure in the QRH/ECAM requires it.

> **NOTE** Using the **minimum** configuration (FLAPS 25 / CONF 3) is a normal, fully accepted choice. It **never** results in a deduction or any action against your PIREP. The Tracker's landing review treats both as the landing configuration.

## Why

Less landing flap means a **higher approach speed** (VREF / VLS). The consequences:

- more energy to dissipate: a **longer landing distance** and more braking,
- a **longer runway occupancy** before you can vacate, which disrupts traffic on busy online events, and
- a greater tendency towards a **firm touchdown**, which puts stress on the airframe and may call for an inspection.

The standard settings (FLAPS 30 / CONF FULL) give the lowest approach speed and the most margin. Choose the minimum only for a reason, such as expected windshear, a go-around performance limit or noise, and brief it.

## How the tools apply it

- **EFB Arrival Perf** offers only permitted settings and defaults to the standard one.
- The **landing review** in the Tracker judges "landing configuration by 1,000 ft" against the company minimum for your type (see [OM-A 6](doc:om-a-06#held-for-review)).
- Fly the approach speed for the flap you have selected. The stabilised-approach criteria are in [OM-A 5](doc:om-a-05#stabilised-approach-criteria).
