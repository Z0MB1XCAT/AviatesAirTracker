using AviatesAirTracker.Models;
using Serilog;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AviatesAirTracker.Core.Data;

// ============================================================
// DATA REPOSITORY INTERFACES
// 
// ARCHITECTURE NOTE:
// These interfaces define the data contract between the client
// and data storage. Current implementations are in-memory.
//
// FUTURE BACKEND INTEGRATION:
// Replace InMemory* classes with SqlFlightRepository, etc.
// that POST/GET to the Aviates Air backend API.
// All ViewModels depend only on these interfaces.
// ============================================================

// ---- Flight Records ----
public interface IFlightRepository
{
    Task<FlightRecord?> GetByIdAsync(Guid id);
    Task<List<FlightRecord>> GetAllAsync();
    Task<List<FlightRecord>> GetRecentAsync(int count);
    Task SaveAsync(FlightRecord record);
    Task UpdateAsync(FlightRecord record);
    Task DeleteAsync(Guid id);
    FlightRecord? GetCurrentFlight();
    void SetCurrentFlight(FlightRecord? flight);
    Task<List<FlightRecord>> GetInProgressAsync();
    Task<int> PruneWithoutLandingAsync(Guid? exceptId);
    Task ClearAllAsync();
}

// ---- Landings ----
public interface ILandingRepository
{
    Task<List<LandingResult>> GetAllAsync();
    Task<List<LandingResult>> GetForFlightAsync(Guid flightId);
    Task<LandingResult?> GetBestAsync();
    Task SaveAsync(LandingResult landing);
    Task DeleteAsync(Guid landingId);
    Task<double> GetAverageScoreAsync();
    Task ClearAllAsync();
    /// <summary>The landing-report trace (kept apart from the landing record); empty when none was recorded.</summary>
    Task<List<LandingTracePoint>> GetTraceAsync(Guid landingId);
}

// ---- Messages ----
public interface IMessageRepository
{
    Task<List<PilotMessage>> GetInboxAsync(string pilotId);
    Task<List<PilotMessage>> GetBroadcastsAsync(int count = 50);
    Task SaveAsync(PilotMessage message);
    Task MarkReadAsync(Guid messageId);
    Task<int> GetUnreadCountAsync(string pilotId);
    Task ClearAllAsync();
}

// ---- Friends ----
public interface IFriendRepository
{
    Task<List<FriendEntry>> GetAllAsync();
    Task AddAsync(FriendEntry friend);
    Task RemoveAsync(string pilotId);
    Task<bool> ExistsAsync(string pilotId);
    Task ClearAllAsync();
}

// ---- Flight Deletion Requests ----
public interface IFlightDeletionRepository
{
    Task<List<FlightDeletionRequest>> GetAllAsync();
    Task<FlightDeletionRequest?> GetPendingForFlightAsync(Guid flightId);
    Task SaveAsync(FlightDeletionRequest request);
    Task ApproveAsync(Guid requestId);
    Task RejectAsync(Guid requestId);
}

// ============================================================
// IN-MEMORY FLIGHT REPOSITORY
// TODO: Replace with HTTP client calling Aviates Air backend
// ============================================================

public class InMemoryFlightRepository : IFlightRepository
{
    private readonly List<FlightRecord> _flights = [];
    private readonly object _lock = new();
    private FlightRecord? _currentFlight;

    public Task<FlightRecord?> GetByIdAsync(Guid id)
    {
        lock (_lock)
        {
            return Task.FromResult(_flights.FirstOrDefault(f => f.Id == id));
        }
    }

    public Task<List<FlightRecord>> GetAllAsync()
    {
        lock (_lock)
        {
            return Task.FromResult(_flights.OrderByDescending(f => f.CreatedAt).ToList());
        }
    }

    public Task<List<FlightRecord>> GetRecentAsync(int count)
    {
        lock (_lock)
        {
            return Task.FromResult(_flights
                .Where(f => f.Status == FlightStatus.Completed)
                .OrderByDescending(f => f.CreatedAt)
                .Take(count)
                .ToList());
        }
    }

    public Task SaveAsync(FlightRecord record)
    {
        lock (_lock)
        {
            _flights.Add(record);
            Log.Debug("[FlightRepo] Flight saved: {Id}", record.Id);
        }
        return Task.CompletedTask;
    }

    public Task UpdateAsync(FlightRecord record)
    {
        lock (_lock)
        {
            var idx = _flights.FindIndex(f => f.Id == record.Id);
            if (idx >= 0) _flights[idx] = record;
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id)
    {
        lock (_lock)
        {
            _flights.RemoveAll(f => f.Id == id);
        }
        return Task.CompletedTask;
    }

    // CRIT-08: GetCurrentFlight/SetCurrentFlight had no lock — data race between SimConnect and UI threads.
    public FlightRecord? GetCurrentFlight() { lock (_lock) return _currentFlight; }
    public void SetCurrentFlight(FlightRecord? flight) { lock (_lock) _currentFlight = flight; }

    public Task<List<FlightRecord>> GetInProgressAsync()
    {
        lock (_lock)
            return Task.FromResult(_flights.Where(f => f.Status == FlightStatus.InProgress)
                                           .OrderByDescending(f => f.CreatedAt).ToList());
    }

    public Task<int> PruneWithoutLandingAsync(Guid? exceptId)
    {
        lock (_lock)
        {
            var removed = _flights.RemoveAll(f => f.PrimaryLanding == null && f.Id != exceptId);
            return Task.FromResult(removed);
        }
    }

    public Task ClearAllAsync()
    {
        lock (_lock) { _flights.Clear(); _currentFlight = null; }
        return Task.CompletedTask;
    }
}

// ============================================================
// IN-MEMORY LANDING REPOSITORY
// TODO: Connect to Aviates Air backend landings table
// ============================================================

public class InMemoryLandingRepository : ILandingRepository
{
    private readonly List<LandingResult> _landings = [];
    private readonly object _lock = new();

    public Task<List<LandingResult>> GetAllAsync()
    {
        lock (_lock)
        {
            return Task.FromResult(_landings.OrderByDescending(l => l.Timestamp).ToList());
        }
    }

    public Task<List<LandingResult>> GetForFlightAsync(Guid flightId)
    {
        lock (_lock)
        {
            return Task.FromResult(_landings
                .Where(l => l.FlightId == flightId.ToString())
                .ToList());
        }
    }

    public Task<LandingResult?> GetBestAsync()
    {
        lock (_lock)
        {
            return Task.FromResult(_landings
                .OrderByDescending(l => l.LandingScore)
                .FirstOrDefault());
        }
    }

    public Task SaveAsync(LandingResult landing)
    {
        lock (_lock)
        {
            _landings.Add(landing);
            Log.Debug("[LandingRepo] Landing saved: Score={Score}", landing.LandingScore);
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid landingId)
    {
        lock (_lock) { _landings.RemoveAll(l => l.Id == landingId); }
        return Task.CompletedTask;
    }

    public Task<double> GetAverageScoreAsync()
    {
        lock (_lock)
        {
            if (!_landings.Any()) return Task.FromResult(0.0);
            return Task.FromResult(_landings.Average(l => l.LandingScore));
        }
    }

    public Task ClearAllAsync()
    {
        lock (_lock) { _landings.Clear(); }
        return Task.CompletedTask;
    }

    public Task<List<LandingTracePoint>> GetTraceAsync(Guid landingId)
    {
        lock (_lock) return Task.FromResult(_landings.FirstOrDefault(l => l.Id == landingId)?.Trace ?? []);
    }
}

// ============================================================
// IN-MEMORY MESSAGE REPOSITORY
// TODO: Replace with HTTP calls to /api/v1/messages
// ============================================================

public class InMemoryMessageRepository : IMessageRepository
{
    private readonly List<PilotMessage> _messages = [];
    private readonly object _lock = new();

    public Task<List<PilotMessage>> GetInboxAsync(string pilotId)
    {
        lock (_lock)
        {
            // Return messages where the pilot is sender OR recipient — matches JsonMessageRepository contract
            return Task.FromResult(_messages
                .Where(m => (m.RecipientId == pilotId || m.SenderId == pilotId) && !m.IsModerated && m.Type == MessageType.Direct)
                .OrderByDescending(m => m.SentAt)
                .ToList());
        }
    }

    public Task<List<PilotMessage>> GetBroadcastsAsync(int count = 50)
    {
        lock (_lock)
        {
            return Task.FromResult(_messages
                .Where(m => m.Type == MessageType.Broadcast && !m.IsModerated)
                .OrderByDescending(m => m.SentAt)
                .Take(count)
                .ToList());
        }
    }

    public Task SaveAsync(PilotMessage message)
    {
        lock (_lock) { _messages.Add(message); }
        return Task.CompletedTask;
    }

    public Task MarkReadAsync(Guid messageId)
    {
        lock (_lock)
        {
            var msg = _messages.FirstOrDefault(m => m.Id == messageId);
            if (msg != null) msg.ReadAt = DateTime.UtcNow;
        }
        return Task.CompletedTask;
    }

    public Task<int> GetUnreadCountAsync(string pilotId)
    {
        lock (_lock)
        {
            return Task.FromResult(_messages.Count(
                m => m.RecipientId == pilotId && m.ReadAt == null && !m.IsModerated));
        }
    }

    public Task ClearAllAsync()
    {
        lock (_lock) { _messages.Clear(); }
        return Task.CompletedTask;
    }
}

// ============================================================
// IN-MEMORY FRIEND REPOSITORY
// TODO: Replace with HTTP calls to /api/v1/friends
// ============================================================

public class InMemoryFriendRepository : IFriendRepository
{
    private readonly List<FriendEntry> _friends = [];
    private readonly object _lock = new();

    public Task<List<FriendEntry>> GetAllAsync()
    {
        lock (_lock) { return Task.FromResult(_friends.OrderBy(f => f.PilotName).ToList()); }
    }

    public Task AddAsync(FriendEntry friend)
    {
        lock (_lock)
        {
            if (!_friends.Any(f => f.PilotId == friend.PilotId))
                _friends.Add(friend);
        }
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string pilotId)
    {
        lock (_lock) { _friends.RemoveAll(f => f.PilotId == pilotId); }
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string pilotId)
    {
        lock (_lock) { return Task.FromResult(_friends.Any(f => f.PilotId == pilotId)); }
    }

    public Task ClearAllAsync()
    {
        lock (_lock) { _friends.Clear(); }
        return Task.CompletedTask;
    }
}

// ============================================================
// JSON-PERSISTED FRIEND REPOSITORY
// Friends survive app restarts — saved to AppData\Roaming.
// ============================================================

public class JsonFriendRepository : IFriendRepository
{
    private readonly List<FriendEntry> _friends = [];
    private readonly object _lock = new();
    private readonly JsonFileStore _store = JsonFileStore.InAppData("friends.json", "FriendRepo");

    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public JsonFriendRepository() { Load(); }

    private void Load()
    {
        var loaded = _store.Read<List<FriendEntry>>();
        if (loaded != null) _friends.AddRange(loaded);
        Log.Debug("[FriendRepo] Loaded {Count} friends from disk", _friends.Count);
    }

    private void Persist() => _store.Write(() => { lock (_lock) return _friends.ToList(); }, _opts);

    public Task<List<FriendEntry>> GetAllAsync()
    {
        lock (_lock) return Task.FromResult(_friends.OrderBy(f => f.PilotName).ToList());
    }

    public Task AddAsync(FriendEntry friend)
    {
        var added = false;
        lock (_lock)
        {
            if (!_friends.Any(f => f.PilotId == friend.PilotId))
            {
                _friends.Add(friend);
                added = true;
            }
        }
        if (added) Persist();
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string pilotId)
    {
        lock (_lock) { _friends.RemoveAll(f => f.PilotId == pilotId); }
        Persist();
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string pilotId)
    {
        lock (_lock) return Task.FromResult(_friends.Any(f => f.PilotId == pilotId));
    }

    public Task ClearAllAsync()
    {
        lock (_lock) { _friends.Clear(); }
        Persist();
        return Task.CompletedTask;
    }
}

// ============================================================
// JSON-PERSISTED MESSAGE REPOSITORY
// Direct messages survive app restarts — saved to AppData\Roaming.
// Broadcasts are session-only (fetched from backend when live).
// ============================================================

public class JsonMessageRepository : IMessageRepository
{
    private readonly List<PilotMessage> _direct     = [];
    private readonly List<PilotMessage> _broadcasts = [];
    private readonly object _lock = new();
    private readonly JsonFileStore _store = JsonFileStore.InAppData("direct_messages.json", "MessageRepo");

    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public JsonMessageRepository() { Load(); }

    private static bool IsDuplicate(PilotMessage a, PilotMessage b) => a.IsSameMessageAs(b);

    private static DateTime NormalizeUtc(DateTime dt) =>
        dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime()
        : DateTime.SpecifyKind(dt, DateTimeKind.Utc);

    private void Load()
    {
        var loaded = _store.Read<List<PilotMessage>>();
        if (loaded != null)
        {
            // Deduplicate: keep only the first occurrence of semantically identical messages.
            // Older messages loaded from disk may contain multiple copies caused by the
            // retry-send bug where SyncedToBackend was never persisted.
            var deduped = new List<PilotMessage>();
            foreach (var msg in loaded.OrderBy(m => NormalizeUtc(m.SentAt)))
            {
                if (!deduped.Any(x => IsDuplicate(x, msg)))
                    deduped.Add(msg);
            }
            _direct.AddRange(deduped);
            if (deduped.Count != loaded.Count)
            {
                Log.Information("[MessageRepo] Removed {N} duplicate messages on load", loaded.Count - deduped.Count);
                Persist();
            }
        }
        Log.Debug("[MessageRepo] Loaded {Count} direct messages from disk", _direct.Count);
    }

    private void Persist() => _store.Write(() => { lock (_lock) return _direct.ToList(); }, _opts);

    public Task<List<PilotMessage>> GetInboxAsync(string pilotId)
    {
        lock (_lock)
        {
            // Return all direct messages where the user is sender OR recipient
            // so GetConversation() in Messages.razor can show the full thread.
            return Task.FromResult(_direct
                .Where(m => (m.RecipientId == pilotId || m.SenderId == pilotId) && !m.IsModerated)
                .OrderByDescending(m => m.SentAt)
                .ToList());
        }
    }

    public Task<List<PilotMessage>> GetBroadcastsAsync(int count = 50)
    {
        lock (_lock)
        {
            return Task.FromResult(_broadcasts
                .Where(m => !m.IsModerated)
                .OrderByDescending(m => m.SentAt)
                .Take(count)
                .ToList());
        }
    }

    public Task SaveAsync(PilotMessage message)
    {
        var directChanged = false;
        lock (_lock)
        {
            if (message.Type == MessageType.Direct)
            {
                var existingById = _direct.FirstOrDefault(m => m.Id == message.Id);
                var duplicateIdx = existingById == null ? _direct.FindIndex(m => IsDuplicate(m, message)) : -1;
                if (existingById != null)
                    _direct[_direct.IndexOf(existingById)] = message;
                else if (duplicateIdx < 0)
                    _direct.Add(message);
                else if (message.SyncedToBackend)
                {
                    // The server's copy of a message stored here under another id: adopt the
                    // server id (keeping local read state) so the next inbox poll recognises it
                    // by id instead of treating it as new again every minute.
                    message.ReadAt ??= _direct[duplicateIdx].ReadAt;
                    _direct[duplicateIdx] = message;
                }
                directChanged = true;
            }
            else
            {
                var existing = _broadcasts.FirstOrDefault(m => m.Id == message.Id);
                if (existing == null)
                    _broadcasts.Add(message);
                else
                    _broadcasts[_broadcasts.IndexOf(existing)] = message;
            }
        }
        if (directChanged) Persist();
        return Task.CompletedTask;
    }

    public Task MarkReadAsync(Guid messageId)
    {
        var changed = false;
        lock (_lock)
        {
            var msg = _direct.FirstOrDefault(m => m.Id == messageId);
            if (msg != null)
            {
                msg.ReadAt = DateTime.UtcNow;
                changed = true;
            }
        }
        if (changed) Persist();
        return Task.CompletedTask;
    }

    public Task<int> GetUnreadCountAsync(string pilotId)
    {
        lock (_lock)
        {
            return Task.FromResult(_direct.Count(
                m => m.RecipientId == pilotId && m.ReadAt == null && !m.IsModerated));
        }
    }

    public Task ClearAllAsync()
    {
        // Broadcasts are session-only (see class doc comment) so there's nothing on disk for
        // them to begin with; only _direct needs clearing + a snapshot write.
        lock (_lock) { _direct.Clear(); _broadcasts.Clear(); }
        Persist();
        return Task.CompletedTask;
    }
}

// ============================================================
// IN-MEMORY FLIGHT DELETION REPOSITORY
// Stores pilot-submitted deletion requests pending admin approval.
// Replace with HTTP calls to /api/v1/flights/deletion-requests when backend is ready.
// ============================================================

public class InMemoryFlightDeletionRepository : IFlightDeletionRepository
{
    private readonly List<FlightDeletionRequest> _requests = [];
    private readonly object _lock = new();

    public Task<List<FlightDeletionRequest>> GetAllAsync()
    {
        lock (_lock)
            return Task.FromResult(_requests.OrderByDescending(r => r.SubmittedAt).ToList());
    }

    public Task<FlightDeletionRequest?> GetPendingForFlightAsync(Guid flightId)
    {
        lock (_lock)
            return Task.FromResult(_requests.FirstOrDefault(r =>
                r.FlightId == flightId && r.Status == DeletionRequestStatus.Pending));
    }

    public Task SaveAsync(FlightDeletionRequest request)
    {
        lock (_lock)
        {
            _requests.Add(request);
            Log.Debug("[DeletionRepo] Request saved for flight {Id}: {Reason}", request.FlightId, request.Reason);
        }
        return Task.CompletedTask;
    }

    public Task ApproveAsync(Guid requestId)
    {
        lock (_lock)
        {
            var r = _requests.FirstOrDefault(x => x.Id == requestId);
            if (r != null) { r.Status = DeletionRequestStatus.Approved; r.ReviewedAt = DateTime.UtcNow; }
        }
        return Task.CompletedTask;
    }

    public Task RejectAsync(Guid requestId)
    {
        lock (_lock)
        {
            var r = _requests.FirstOrDefault(x => x.Id == requestId);
            if (r != null) { r.Status = DeletionRequestStatus.Rejected; r.ReviewedAt = DateTime.UtcNow; }
        }
        return Task.CompletedTask;
    }
}

// ============================================================
// JSON-PERSISTED FLIGHT DELETION REPOSITORY
// ============================================================

public class JsonFlightDeletionRepository : IFlightDeletionRepository
{
    private readonly List<FlightDeletionRequest> _requests = [];
    private readonly object _lock = new();
    private readonly JsonFileStore _store = JsonFileStore.InAppData("deletion_requests.json", "DeletionRepo");

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        Converters    = { new JsonStringEnumConverter() }
    };

    public JsonFlightDeletionRepository() { Load(); }

    private void Load()
    {
        var loaded = _store.Read<List<FlightDeletionRequest>>(_opts);
        if (loaded != null)
        {
            _requests.AddRange(loaded);
            Log.Debug("[DeletionRepo] Loaded {Count} requests from disk", _requests.Count);
        }
    }

    private void Persist() => _store.Write(() => { lock (_lock) return _requests.ToList(); }, _opts);

    public Task<List<FlightDeletionRequest>> GetAllAsync()
    {
        lock (_lock)
            return Task.FromResult(_requests.OrderByDescending(r => r.SubmittedAt).ToList());
    }

    public Task<FlightDeletionRequest?> GetPendingForFlightAsync(Guid flightId)
    {
        lock (_lock)
            return Task.FromResult(_requests.FirstOrDefault(r =>
                r.FlightId == flightId && r.Status == DeletionRequestStatus.Pending));
    }

    public Task SaveAsync(FlightDeletionRequest request)
    {
        lock (_lock) { _requests.Add(request); }
        Persist();
        return Task.CompletedTask;
    }

    public Task ApproveAsync(Guid requestId)
    {
        lock (_lock)
        {
            var r = _requests.FirstOrDefault(x => x.Id == requestId);
            if (r != null) { r.Status = DeletionRequestStatus.Approved; r.ReviewedAt = DateTime.UtcNow; }
        }
        Persist();
        return Task.CompletedTask;
    }

    public Task RejectAsync(Guid requestId)
    {
        lock (_lock)
        {
            var r = _requests.FirstOrDefault(x => x.Id == requestId);
            if (r != null) { r.Status = DeletionRequestStatus.Rejected; r.ReviewedAt = DateTime.UtcNow; }
        }
        Persist();
        return Task.CompletedTask;
    }
}

// ============================================================
// JSON-PERSISTED FLIGHT REPOSITORY
// Flight records survive app restarts — saved to AppData\Roaming.
// Replaces InMemoryFlightRepository.
// ============================================================

public class JsonFlightRepository : IFlightRepository
{
    private readonly List<FlightRecord> _flights = [];
    private readonly object _lock = new();
    private FlightRecord? _currentFlight;

    private readonly JsonFileStore _store = JsonFileStore.InAppData("flights.json", "FlightRepo");

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonFlightRepository() { Load(); }

    private void Load()
    {
        var loaded = _store.Read<List<FlightRecord>>(_opts);
        if (loaded != null) _flights.AddRange(loaded);
        Log.Debug("[FlightRepo] Loaded {Count} flights from disk", _flights.Count);
    }

    private void Persist() => _store.Write(() => { lock (_lock) return _flights.ToList(); }, _opts);

    public Task<FlightRecord?> GetByIdAsync(Guid id)
    {
        lock (_lock) return Task.FromResult(_flights.FirstOrDefault(f => f.Id == id));
    }

    public Task<List<FlightRecord>> GetAllAsync()
    {
        lock (_lock) return Task.FromResult(_flights.OrderByDescending(f => f.CreatedAt).ToList());
    }

    public Task<List<FlightRecord>> GetRecentAsync(int count)
    {
        lock (_lock)
        {
            return Task.FromResult(_flights
                .Where(f => f.Status == FlightStatus.Completed)
                .OrderByDescending(f => f.CreatedAt)
                .Take(count)
                .ToList());
        }
    }

    public Task SaveAsync(FlightRecord record)
    {
        lock (_lock)
        {
            _flights.Add(record);
            Log.Debug("[FlightRepo] Flight saved: {Id}", record.Id);
        }
        Persist();
        return Task.CompletedTask;
    }

    public Task UpdateAsync(FlightRecord record)
    {
        var found = false;
        lock (_lock)
        {
            var idx = _flights.FindIndex(f => f.Id == record.Id);
            if (idx >= 0)
            {
                _flights[idx] = record;
                found = true;
            }
        }
        if (found) Persist();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id)
    {
        lock (_lock) { _flights.RemoveAll(f => f.Id == id); }
        Persist();
        return Task.CompletedTask;
    }

    public FlightRecord? GetCurrentFlight() { lock (_lock) return _currentFlight; }
    public void SetCurrentFlight(FlightRecord? flight) { lock (_lock) _currentFlight = flight; }

    public Task<List<FlightRecord>> GetInProgressAsync()
    {
        lock (_lock)
            return Task.FromResult(_flights.Where(f => f.Status == FlightStatus.InProgress)
                                           .OrderByDescending(f => f.CreatedAt).ToList());
    }

    public Task<int> PruneWithoutLandingAsync(Guid? exceptId)
    {
        int removed;
        lock (_lock) { removed = _flights.RemoveAll(f => f.PrimaryLanding == null && f.Id != exceptId); }
        if (removed > 0) Persist();
        return Task.FromResult(removed);
    }

    public Task ClearAllAsync()
    {
        lock (_lock) { _flights.Clear(); _currentFlight = null; }
        Persist();
        return Task.CompletedTask;
    }
}

// ============================================================
// JSON-PERSISTED LANDING REPOSITORY
// Landing records survive app restarts — saved to AppData\Roaming.
// Replaces InMemoryLandingRepository.
// ============================================================

public class JsonLandingRepository : ILandingRepository
{
    private readonly List<LandingResult> _landings = [];
    private readonly object _lock = new();

    private readonly JsonFileStore _store = JsonFileStore.InAppData("landings.json", "LandingRepo");

    // LandingResult.Trace is [JsonIgnore] (it would otherwise bloat landings.json AND
    // flights.json, which embeds the same objects) -- each trace is its own compact file.
    private readonly string _traceDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AviatesAirTracker", "landing_traces");

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private string TracePath(Guid id) => Path.Combine(_traceDir, id.ToString("N") + ".json");

    private void SaveTrace(LandingResult landing)
    {
        if (landing.Trace.Count == 0) return;
        try
        {
            Directory.CreateDirectory(_traceDir);
            File.WriteAllText(TracePath(landing.Id), JsonSerializer.Serialize(landing.Trace));
        }
        catch (Exception ex) { Log.Warning(ex, "[LandingRepo] Failed to save landing trace"); }
    }

    public Task<List<LandingTracePoint>> GetTraceAsync(Guid landingId)
    {
        lock (_lock)
        {
            var live = _landings.FirstOrDefault(l => l.Id == landingId);
            if (live is { Trace.Count: > 0 }) return Task.FromResult(live.Trace);
        }
        try
        {
            var path = TracePath(landingId);
            if (File.Exists(path))
                return Task.FromResult(JsonSerializer.Deserialize<List<LandingTracePoint>>(File.ReadAllText(path)) ?? []);
        }
        catch (Exception ex) { Log.Warning(ex, "[LandingRepo] Failed to read landing trace"); }
        return Task.FromResult(new List<LandingTracePoint>());
    }

    public JsonLandingRepository() { Load(); }

    private void Load()
    {
        var loaded = _store.Read<List<LandingResult>>(_opts);
        if (loaded != null) _landings.AddRange(loaded);
        Log.Debug("[LandingRepo] Loaded {Count} landings from disk", _landings.Count);
    }

    private void Persist() => _store.Write(() => { lock (_lock) return _landings.ToList(); }, _opts);

    public Task<List<LandingResult>> GetAllAsync()
    {
        lock (_lock) return Task.FromResult(_landings.OrderByDescending(l => l.Timestamp).ToList());
    }

    public Task<List<LandingResult>> GetForFlightAsync(Guid flightId)
    {
        lock (_lock)
        {
            return Task.FromResult(_landings
                .Where(l => l.FlightId == flightId.ToString())
                .ToList());
        }
    }

    public Task<LandingResult?> GetBestAsync()
    {
        lock (_lock)
        {
            return Task.FromResult(_landings.OrderByDescending(l => l.LandingScore).FirstOrDefault());
        }
    }

    public Task SaveAsync(LandingResult landing)
    {
        lock (_lock)
        {
            _landings.Add(landing);
            Log.Debug("[LandingRepo] Landing saved: Score={Score}", landing.LandingScore);
        }
        Persist();
        SaveTrace(landing);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid landingId)
    {
        lock (_lock) { _landings.RemoveAll(l => l.Id == landingId); }
        Persist();
        try { File.Delete(TracePath(landingId)); } catch { /* none recorded */ }
        return Task.CompletedTask;
    }

    public Task<double> GetAverageScoreAsync()
    {
        lock (_lock)
        {
            if (!_landings.Any()) return Task.FromResult(0.0);
            return Task.FromResult(_landings.Average(l => l.LandingScore));
        }
    }

    public Task ClearAllAsync()
    {
        lock (_lock) { _landings.Clear(); }
        Persist();
        try { if (Directory.Exists(_traceDir)) Directory.Delete(_traceDir, recursive: true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }
}

// ============================================================
// PILOT PROFILE MODEL
// ============================================================

public class PilotProfile
{
    public string PilotId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string SimBriefUsername { get; set; } = "";
    public string Airline { get; set; } = "AviatesAir";
    public DateTime JoinDate { get; set; }
}
