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
