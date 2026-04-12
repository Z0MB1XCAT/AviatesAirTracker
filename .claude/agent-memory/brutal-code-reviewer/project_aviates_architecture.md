---
name: aviates_architecture
description: Confirmed architectural decisions, threading model, and key design patterns in Aviates Air Tracker
type: project
---

Aviates Air Tracker is a WPF .NET 8 desktop app for MSFS. All services are singletons registered in App.xaml.cs.

**Threading model (confirmed):**
- SimConnect callbacks (OnRecvOpen, OnRecvSimobjectData, etc.) fire on the WndProc thread (Win32 message pump via HwndSource hook)
- The 20Hz DispatcherTimer (PollTelemetry) fires on the UI thread
- TelemetryReceived event is raised from the WndProc message pump — NOT the UI thread
- MainViewModel wires: SimConnectManager.TelemetryReceived → TelemetryProcessor.Process → FlightSessionManager.OnTelemetryReceived → SessionManager raises TelemetryUpdated → MainViewModel.OnTelemetryUpdated dispatches to UI thread
- ReplayViewModel uses System.Timers.Timer — its Elapsed fires on a ThreadPool thread, not the UI thread

**Key data flow:**
- AircraftState struct is 70 doubles packed sequentially (SequentialLayout, Pack=1)
- TelemetrySnapshot is a class (reference type) wrapping AircraftState struct
- FlightPhaseDetector is stateful and called synchronously in TelemetryProcessor.Process (WndProc thread)
- LandingAnalyzer is stateful and called from FlightSessionManager.OnTelemetryReceived (WndProc thread)

**Why:** These threading facts are critical for spotting race conditions. The SimConnect callback thread is the WndProc message pump, which is distinct from the DispatcherTimer thread.

**How to apply:** Flag any shared state touched from both timer callbacks and WndProc callbacks without locking. Note that all ViewModel updates in MainViewModel are correctly wrapped in Dispatcher.Invoke.
