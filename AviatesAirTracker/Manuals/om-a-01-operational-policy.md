---
id: om-a-01
manual: OM-A
chapter: 1
title: Operational Policy & Flight Planning
summary: The AviatesAir operation: subsidiaries and callsigns, bookings, routes, registrations, repositioning, the OFP and the life of a flight from booking to PIREP.
tags: policy, booking, routes, callsign, subsidiary, pirep, simbrief, ofp, reposition, charter, events
revision: 0
effective: 2026-09-24
order: 1
---
## The operation

AviatesAir flies scheduled passenger and cargo services under three operating brands. Each has its own ICAO designator used for ATC callsigns and SimBrief dispatch:

| Operating brand | ICAO designator | Fleet |
|---|---|---|
| **AviatesAir Mainline** | **VAV** | A320neo · A350 · A380 · 737-800 · 737-900ER · 737 MAX 8 · 747-8 |
| **AviatesAir CityHopper** | **VAH** | A319 · 737-600 · ATR 72-600 · ATR 42-600 |
| **AviatesAir Skycargo** | **VAC** | 777F |

## Callsigns and flight numbers

- **Route flight numbers** always use the **AV** prefix (e.g. *AV150*). This is the number shown on the booking and in the logbook.
- The **ATC callsign** uses the operating brand's designator plus 1–4 characters: up to four digits, optionally ending in up to two letters (e.g. *VAV34NE*, *VAH64E*, *VAC023F*). The Tracker generates it when you book.
- File exactly the callsign shown on your booking in SimBrief and on your network flight plan. The radiotelephony callsign is spoken as designator plus suffix (for example "*VAV three four november echo*").

## Bookings

- You hold **one booking at a time**. Complete or cancel it before booking another.
- A booking defines the **route, flight number, aircraft type and scheduled departure time**.
- Once a booking is **activated** (the flight has started), it is locked and can no longer be cancelled.
- **Charter** bookings are available for flying outside the published schedule.
- **Event flights** earn the event's hours multiplier when you confirm attendance at the event reminder before departure.

### Punctuality

Plan to be off blocks at the scheduled time. A departure **more than 20 minutes after the scheduled departure time** is recorded and the PIREP is held for review. If you are running late, cancel and rebook rather than departing an hour late on an old booking.

## Routes and aircraft types

Only fly a type from an airport that has **AviatesAir routes for that type**; the Routes page lists them. Registrations are pooled fleet-wide; see [CN-001](doc:cn-001).

## Repositioning

Your position is the destination of your last completed flight. If you need to be somewhere else to fly the route you want, the **Reposition** action on the Routes page may be used **once every 7 days**. Otherwise, fly there.

## The life of a flight

1. **Book** the flight in the Tracker.
2. **Plan**: generate the SimBrief OFP with the booked flight number, callsign, type and your registration, then import it in the Tracker **ACARS** page. The OFP flows automatically into the EFB (Pilot Brief, Departure Perf, Dashboard).
3. **Brief**: read the Company NOTAMs and the OFP, check weather and NOTAMs for departure, destination and alternates, and compute takeoff performance.
4. **Fly**: keep the **Tracker running and connected to the simulator for the whole flight**, from the gate to the gate. Flight phases, the route, fuel and the landing are recorded automatically.
5. **Arrive**: park, set the parking brake and shut down. The Tracker completes the flight and submits the **PIREP** automatically.
6. **Review**: read your landing report in the EFB **Landing** app. If the PIREP is held for review, add your comment (see [OM-A 6](doc:om-a-06#held-for-review)).

> **NOTE** If the simulator or the Tracker crashes mid-flight, restart the Tracker: flight recovery resumes the recorded flight. A PIREP that fails to send is retried automatically at the next start.

## Pilot responsibilities

You shall:

- fly the **booked** route and type, with a realistic OFP,
- follow ATC and published procedures when flying online,
- operate within the aircraft's limitations and the company restrictions in the Company NOTAMs,
- avoid using simulation "cheats" (slew, time acceleration, unrealistic fuel or weight edits, pausing mid-approach) on a recorded flight.
