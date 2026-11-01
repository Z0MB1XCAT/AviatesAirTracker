---
id: om-a-02
manual: OM-A
chapter: 2
title: Fuel Policy
summary: How AviatesAir plans fuel (taxi, trip, contingency, alternate, final reserve, additional, extra), in-flight fuel management and the MINIMUM FUEL / MAYDAY FUEL declarations.
tags: fuel, reserves, contingency, alternate, final reserve, minimum fuel, mayday fuel, tankering
revision: 0
effective: 2026-09-24
order: 2
---
AviatesAir follows an ICAO/EASA-style fuel scheme. SimBrief calculates it for you; this chapter explains what each figure is for, so you can judge whether the OFP makes sense.

## Planned fuel

| Component | AviatesAir policy |
|---|---|
| **Taxi** | Expected taxi-out burn, including APU use. Use the OFP figure; add more at congested hubs. |
| **Trip** | Takeoff to landing at destination, including the expected departure and arrival procedures. |
| **Contingency** | **5 % of trip fuel**, never less than **5 minutes** of holding at 1,500 ft above destination. |
| **Alternate** | Missed approach at destination, climb, cruise and approach to the **destination alternate**. |
| **Final reserve** | **30 minutes** holding at 1,500 ft above the alternate, for jets and turboprops alike. |
| **Additional** | Only when needed so that fuel never falls below final reserve (e.g. an isolated destination with no alternate). |
| **Extra** | Commander's discretion: weather, expected holding, ATC delays, online event congestion. |

**Minimum takeoff fuel** = trip + contingency + alternate + final reserve + additional. **Block fuel** = minimum takeoff fuel + taxi + extra.

> **NOTE** Busy online events regularly add 10–20 minutes of holding or vectoring. Carry extra fuel accordingly and note it in your briefing.

### Destination alternate

A destination alternate is required unless the flight is very short and the destination weather is well above minima. Choose one that has suitable runways and weather above the planning minima in [OM-A 3](doc:om-a-03#planning-minima). Our long-haul fleet (A350, A380, 777F, 747-8) should always carry one.

### Tankering

Carrying fuel for the next sector is permitted when the OFP shows a saving and the landing weight stays within limits. Never tanker if it would push the landing weight above MLW or erode landing performance on a limiting runway.

## In-flight fuel management

- Check **fuel on board against the OFP** at every waypoint or at least every 30 minutes. The EFB Dashboard shows the predicted arrival fuel against the planned figure.
- Investigate any trend of more than **5 %** or an **imbalance**: fuel leak, wrong cruise level, headwinds.
- Plan to land with **at least final reserve plus alternate fuel** at destination. If that is no longer possible, re-plan early: a lower level, a closer alternate, or a diversion.

## Fuel declarations

| Call | When |
|---|---|
| **"MINIMUM FUEL"** | You are committed to landing at a specific aerodrome, and any further delay may result in landing with **less than final reserve**. This is not an emergency. It tells ATC that further delay is not acceptable. |
| **"MAYDAY MAYDAY MAYDAY FUEL"** | The fuel you are calculated to have on landing at the nearest suitable aerodrome is **less than final reserve**. This is an emergency. |

> **CAUTION** Final reserve is there to be protected, not used. A landing below final reserve is a reportable occurrence. Mention it in your PIREP comment.
