# Changelog — Aviates Air Flight Tracker

All notable changes to this project are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

---

## [Unreleased]

### Planned
- Dark map tile layer (Stadia Dark / MapTiler Dark)
- Offline airport database with full runway data
- Weight and balance calculator overlay
- Weather radar overlay (Windy API)
- VATSIM/IVAO network position overlay
- Multi-flight comparison analytics
- PDF landing report generation
- Mobile companion app telemetry bridge
- Aviates Air backend live sync (when API ready)

---

## [2.1.0] — 2026-11-01

### Added
- **Landings page** — a full debrief of every landing: an overall rating (Critical, Needs work,
  Good, Excellent), every check against its limit with coaching (stabilised by 1,000/500 ft,
  configuration, speed, glidepath, centreline, touchdown point, flare, bounces, crab), the
  approach profile against a 3° path, speed/sink/attitude/configuration traces and a
  touchdown-zone diagram
- **Statistics page** — hours, flights, rank progress, hours by month, landing-score trend, a map
  of every recorded track, aircraft, most-flown routes, landing grades and records
- **Multiple aircraft per route** — pick which aircraft to fly when booking a route
- ACARS **"Before you fly"** checks: sim connected, booking activated, plan matches, at the
  departure airport, booked aircraft loaded
- **PIREP status on Flights** with Send now, and Fix details for PIREPs Aviates Air refuses
- In-app notices for approach alerts, and Windows notifications for messages, PIREP results,
  event reminders, landings and updates while the tracker is behind the sim
- **Tray mode**, Start with Windows and Start with Microsoft Flight Simulator; opening the app
  twice brings the running copy forward
- **Ctrl+K** search; full keyboard and screen-reader support
- Window size and position are remembered

### Changed
- Updates are **mandatory**: checked every 6 hours, installed when no flight is being tracked,
  and the installer is verified against GitHub's published SHA-256 before it runs
- Flights without an activated booking are no longer tracked

### Fixed
- Crash 10 minutes before every event you'd registered for
- Crash when leaving the Map or ACARS page
- Crash when two pop-ups opened at once
- Settings no longer freezes the window for over a second when it opens
- Tracking stopped for the rest of the session after an unbooked flight
- Bookings are now marked completed on the server
- Rail highlight and Back button follow in-page navigation; Back no longer returns to login
- Messages unread badge updates; the same message is no longer re-announced every minute
- Joystick PTT no longer floods the log; logs are capped at 20 MB a day
- Flight history can no longer be wiped by a crash mid-save
- The updater can launch the installer (2.0.0 couldn't, so 2.1.0 must be installed by hand once)

---

## [1.0.0] — 2026-03-16

### Added
- **SimConnect integration** — full 70-variable telemetry at 20Hz
  - Position, altitude (MSL + AGL), all speeds, heading, pitch, bank
  - Engine N1/N2/fuel flow × 4 engines, fuel levels
  - Autopilot modes and set values, ILS deviation
  - Weather, nav radios, transponder, weight & balance
- **Flight Phase Detector** — automatic state machine
  - Parked → Taxi → Takeoff → Climb → Cruise → Descent → Approach → Landing
- **Advanced Landing Analysis**
  - Flare detection, bounce detection with counter
  - Crosswind and headwind calculation at touchdown
  - 100-point landing score with 6-category breakdown
  - Rollout distance and deceleration profile
  - Runway identification (major airports database)
- **SimBrief Integration**
  - Fetch latest OFP by username via SimBrief API
  - Import XML and JSON OFP files
  - Planned vs actual comparison
- **World Map** — Mapsui/OpenStreetMap with live aircraft, path, planned route
- **Live Flight Dashboard** — all instruments, engine data, AP modes
- **Telemetry Charts** — 7 live OxyPlot charts
- **Flight Replay** — play back any completed flight with speed control
- **Pilot Statistics** — full career totals, rank progression
- **Export** — JSON flight records, CSV landing log, CSV path, PIREP text
- **Aviates Air Backend Client** — ready for ACARS key auth + PIREP submission
- **Aircraft Performance Database** — profiles for A320, B737, B777, B787, CRJ, ATR, C172
- **Custom WPF Controls** — compass rose, VSI, fuel gauge, aircraft symbol
- **Inno Setup installer** script
- **GitHub Actions** CI/CD pipeline

### Architecture
- C# .NET 8 WPF with MVVM (CommunityToolkit.Mvvm)
- Dependency injection (Microsoft.Extensions.DependencyInjection)
- Data abstraction layer with backend-ready interfaces
- 8,200+ lines across 40 source files
- Zero third-party UI framework dependency (pure WPF + OxyPlot + Mapsui)
