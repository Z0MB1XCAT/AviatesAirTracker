---
name: recurring_bugs_patterns
description: Recurring bug patterns found across the Aviates Air Tracker codebase during the first exhaustive review (2026-03-21)
type: project
---

**Recurring patterns confirmed in exhaustive review:**

1. THREADING - ViewModel properties updated without Dispatcher in ReplayViewModel.AdvanceReplay (System.Timers.Timer callback is ThreadPool, not UI thread). This is a systemic gap: any code path reaching a VM property setter from a non-DispatcherTimer must use Dispatcher.

2. DISCONNECTED COMPONENT - RunwayDetector.UpdateForApproach detects a runway but discards the result (local variable, never passed to LandingAnalyzer.SetRunwayInfo). The runway identifier is always "RWY??" in landing results.

3. LANDING REPO DOUBLE-SAVE - LandingAnalysisViewModel.AddLanding calls _landingRepo.SaveAsync AND LandingAnalyzer/FlightSessionManager.OnLandingDetected also calls _landingRepo.SaveAsync. Every landing is saved twice.

4. WIND COMPONENT MATH ERROR - TelemetryProcessor.ComputeWindComponents has wrong sign convention. Swapping windFrom+180 and then using DeltaAngle(windToward, runwayHeading) gives the wrong sign for headwind/crosswind. Should be angle between wind direction and aircraft heading; the cos gives the headwind component correctly but the sin crosswind sign is reversed (positive = left crosswind, not right).

5. ASYNC VOID ANTI-PATTERN - DashboardViewModel.RefreshAsync and StatisticsViewModel.RefreshAsync are async void. Exceptions escape silently. No await callers can observe failures.

6. APPROACH STABILITY DOUBLE-EVALUATION - LandingAnalyzer.CheckApproachStability and StabilityChecker.Check both set snap.ApproachStable and snap.ApproachAlerts. StabilityChecker is registered in DI but never wired to the telemetry pipeline, so only LandingAnalyzer's weaker check runs. The two classes have different stability criteria.

7. FLIGHTPHASEDETECTOR GROUND RESET BUG - _hasLeftGround is set to false every frame while on the ground, meaning a go-around that lands and takes off again resets the flag correctly, but this field is never used anywhere in ClassifyPhase — it is set but never read.

8. SIMBRIEF FUEL CONVERSION BUG - SimBriefService converts plan_ramp/plan_landing from kg to lbs using * 2.20462. The SimBrief API returns these values already in lbs (or kg depending on OFP layout). This needs verification against actual API response.

**Why:** These patterns compound and produce obviously wrong data during testing. They should be checked in every future code review of these files.

**How to apply:** When reviewing any ViewModel or analytics code, check threading first, then look for the double-save / disconnected-component patterns.

---

**XAML-specific recurring patterns confirmed in DashboardView.xaml / MainWindow.xaml review (2026-03-22):**

9. STRING INDEXER BINDING ON STRING - `{Binding AirportICAO[0]}` is used in DashboardView to show the first letter of an ICAO string. WPF's binding engine supports `[n]` indexer syntax only on `IList` and `IDictionary`. On a plain `string` property, WPF silently fails and the TextBlock shows nothing. Must use a converter or a dedicated char property.

10. TIMESPAN STRINGFORMAT LOWERCASE hh - `StringFormat='{}{0:hh\\:mm}'` on a `TimeSpan` uses `hh`, which is "hours modulo 12" (12-hour clock component). Any flight over 12 hours displays the wrong hours value. Must use `HH` (24-hour) or `%h` for total hours.

11. STORYBOARD NO TARGET IN DATATEMPLATE - `PulseAnimation` storyboard is triggered by a `Loaded` event on an `Ellipse` inside a `DataTemplate`. The storyboard specifies no `Storyboard.TargetName`. WPF resolves the target as the element that initiated `BeginStoryboard`, which works — but only when `Visibility` collapses and shows the parent, as the `Loaded` event may not fire again after virtualization. Low severity but fragile.

12. LOADSIMBRIEF BUTTON WRONG STYLE BINDING - The "Load SimBrief" button in the sidebar binds `Style` to `IsDashboardActive`. It should not reflect active-page state. The button will appear in active-page style whenever Dashboard is selected, regardless of whether SimBrief loading is relevant.

**How to apply:** In any XAML review, immediately check: (a) indexer syntax on string bindings, (b) TimeSpan StringFormat hh vs HH, (c) storyboard target resolution in DataTemplates.
