# AviatesAir Tracker — Feature Backlog

Ideas catalogued during codebase audit (2026-05-03). Work through these in future sessions.

---

## Critical Bugs (Fix First)

### BUG-1: Debug logging in production — `MainViewModel.cs:106, 118`
`AgentDebug.Log()` fires on every navigation call. Remove before any public release.

### BUG-2: WPF converter `ConvertBack` methods throw `NotImplementedException`
`ValueConverters.cs` and `ExtraConverters.cs` — 10+ converters throw instead of returning `Binding.DoNothing`.
Safe as long as all bindings are one-way, but a silent crash landmine for any future two-way binding.

### BUG-3: No departure alert audio files
`TakeoffPerformanceModal.razor:214` calls `aviatesAudio.playDepartureAlert` and `interop.js` defines the function,
but there are no audio files in `wwwroot/`. The error is swallowed silently — pilots hear nothing.
**Fix:** Add a short GPWS-style chime WAV/MP3 to `wwwroot/audio/` and update `interop.js`.

---

## High-Impact Features

### FEAT-A: Data Persistence (SQLite)
**Why it matters:** All 5 data repos are in-memory — every app restart wipes all flight history, landings, and messages.
**Approach:** Add `Microsoft.EntityFrameworkCore.Sqlite` NuGet. Create `AviatesDbContext`. Replace `InMemoryFlightRepository`,
`InMemoryLandingRepository`, `InMemoryMessageRepository`, `InMemoryPilotRepository`, `InMemoryFriendRepository`
with EF Core implementations. No backend required — local `.db` file in `%AppData%\AviatesAirTracker\`.

### FEAT-C: Build Out the 5 Stub Pages
Currently redirect instead of rendering their UI:
- `/live-flight` → redirects to `/acars` (should show an in-cockpit instrument panel view)
- `/replay` → redirects to `/flights` (flight playback engine exists in `ReplayViewModel.cs`; needs UI)
- `/statistics` → redirects to `/pilot-hub` (OxyPlot is in the project; wire charts to `StatisticsViewModel`)
- `/telemetry` → redirects to `/acars` (OxyPlot + 600-sample telemetry buffer ready; needs chart UI)
- `/landing-analysis` → redirects to `/pilot-hub` (full landing scoring exists; needs standalone page)

### FEAT-D: PIREP Submission to Backend
`AviatesBackendClient.SubmitPirepAsync()` is a TODO stub. This is the core of virtual airline tracking —
submitting completed flights to the Aviates Air backend so they count toward hours and rank.
**Depends on:** backend API endpoint being live, plus SQLite persistence (FEAT-A) to queue offline PIREPs.

### FEAT-E: SimBrief → Map Route Overlay Pipeline
All the pieces exist but aren't connected end-to-end:
- `SimBriefService` fully parses OFPs including `Waypoints` list
- `interop.js` has `aviatesMap.setPlannedRoute(points)` and `addAirportMarker()`
- `MapViewModel` exists but is a stub
Wire it up: on SimBrief load → push waypoints to map as dashed planned route → overlay against actual flown path.

### FEAT-F: Real-Time Departure Countdown / Alert
`TakeoffPerformanceModal` shows scheduled dep time but no countdown.
Add a 1-minute timer that fires an alert banner at T-15 min and T-5 min before scheduled departure.
Tie into `AlertService` which is already fully wired.

---

## Polish / UX

### UX-1: Sidebar search functionality
`MainLayout.razor` has a search `<input>` placeholder that does nothing.
Could search across flights (by ICAO, aircraft type, date), routes, and fleet — purely local.

### UX-2: Real News & Events content
`News.razor` has 4 hardcoded article cards. `Events.razor` has a create-event form with no save.
Wire both to the backend (or a static JSON feed) so content comes from the airline.

### UX-3: Support ticket system
`Support.razor` is a pure placeholder (3 feature cards, no functionality).
At minimum: link "Report a Bug" to the GitHub issues URL; wire "Live Chat" to the Discord invite link.

### UX-4: MSFS connect/disconnect desktop toast
When SimConnect status changes, show a Windows toast notification so the pilot knows the app connected
without having to look at the app. Use `Microsoft.Toolkit.Uwp.Notifications` or WPF `Notifyicon`.

---

## Backend Integration (When API Is Ready)

All `InMemory` repositories have `// TODO replace with HTTP` comments.
`AviatesBackendClient` has 8 TODO endpoint stubs:
- `SubmitPirepAsync` — POST `/api/v1/pireps`
- `GetPilotStatsAsync` — GET `/api/v1/pilots/{id}/stats`
- `ValidateRouteAsync` — GET `/api/v1/routes/validate`
- `SendMessageAsync` — POST `/api/v1/messages`
- `GetMessagesAsync` — GET `/api/v1/messages/inbox`
- `DeleteFriendAsync` — DELETE `/api/v1/friends/{pilotId}`
- `UpdateAcarsPositionAsync` — POST `/api/v1/acars/position`

Swap these in once backend endpoints go live. DI container means no page-level changes needed.
