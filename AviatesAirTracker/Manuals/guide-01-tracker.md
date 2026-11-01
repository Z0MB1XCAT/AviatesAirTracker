---
id: guide-01
manual: GUIDE
chapter: 1
title: The AviatesAir Tracker & ACARS
summary: Setting up the Tracker (ACARS key, SimBrief, Hoppies), what each page is for, and how a flight is recorded and reported from gate to gate.
tags: tracker, acars, acars key, simbrief, hoppies, setup, pirep, logbook, pilot hub, routes, map, discord
revision: 0
effective: 2026-09-24
order: 1
---
The **AviatesAir Tracker** is the desktop app that connects to Microsoft Flight Simulator, records your flights and files your PIREPs. It also hosts the EFB.

## First-time setup

1. **Sign in** with your **ACARS key**, the 28-character key you receive when you register at *flyaviatesair.uk*. It identifies you on every PIREP and message.
2. **Settings → SimBrief Integration:** enter your SimBrief username or ID so the Tracker can fetch your OFP.
3. **Settings → Hoppies ACARS:** enable company ACARS messages if you want loadsheets and timing messages in the simulator's datalink. Company messages use AviatesAir's own Hoppies connection, so no personal logon code is needed for them.
4. **Settings → EFB Companion:** enable it to use the tablet (see [Guide 2](doc:guide-02)).

## The pages

| Page | Use it for |
|---|---|
| **Dashboard** | Today at a glance: your position, booking, recent flights and stats. |
| **Flights** | Your logbook: every recorded flight with its landing grade. |
| **Routes** | The route network: find routes from your position by type, book, or reposition. |
| **Fleet** | The AviatesAir fleet and registrations. |
| **Pilot Hub** | Your booking, career statistics and landing history. |
| **ACARS** | The live flight: connection state, booking, SimBrief OFP, live flight data, and the Hoppies log. |
| **Map** | Live map of your flight and other AviatesAir pilots. |
| **Messages** | Direct messages with other pilots (friend codes) and company broadcasts. |
| **News / Events / Support** | Company news, events to join, and help. |

## Flying a recorded flight

1. **Book** on the Routes page. The booking gives you the flight number (**AV…**) and your ATC callsign.
2. Open **ACARS**, generate the plan in SimBrief (the Tracker can open it for you), then **fetch the OFP**.
3. Start the simulator at the gate. The Tracker connects automatically and shows **LIVE**.
4. Fly. The Tracker detects each phase (taxi, takeoff, climb, cruise, descent, approach, landing) and records the route, fuel and your landing.
5. After parking and shutting down, the flight **completes** and the **PIREP is submitted** automatically.

> **CAUTION** Keep the Tracker running from gate to gate. If something crashes, restart the Tracker and simulator. Flight recovery resumes the flight, and a PIREP that failed to send is retried at the next start.

## After the flight

- The **touchdown report** pops up after landing, and every landing stays available in the EFB **Landing** app.
- A PIREP flagged by the checks in [OM-A 6](doc:om-a-06#held-for-review) is **held for review**. Add a short explanation when you're asked.
- **Discord presence** shows your flight on your Discord profile while you fly (if enabled).
