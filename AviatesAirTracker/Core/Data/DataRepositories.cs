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
}

// ---- Pilot Profile ----
public interface IPilotRepository
{
    Task<PilotStatistics?> GetStatisticsAsync(string pilotId);
    Task SaveStatisticsAsync(PilotStatistics stats);
    Task<PilotProfile?> GetProfileAsync();
    Task SaveProfileAsync(PilotProfile profile);
}

// ---- Landings ----
public interface ILandingRepository
{
    Task<List<LandingResult>> GetAllAsync();
    Task<List<LandingResult>> GetForFlightAsync(Guid flightId);
    Task<LandingResult?> GetBestAsync();
    Task SaveAsync(LandingResult landing);
    Task<double> GetAverageScoreAsync();
}

// ---- Messages ----
public interface IMessageRepository
{
    Task<List<PilotMessage>> GetInboxAsync(string pilotId);
    Task<List<PilotMessage>> GetBroadcastsAsync(int count = 50);
    Task SaveAsync(PilotMessage message);
    Task MarkReadAsync(Guid messageId);
    Task<int> GetUnreadCountAsync(string pilotId);
}

// ---- Friends ----
public interface IFriendRepository
{
    Task<List<FriendEntry>> GetAllAsync();
    Task AddAsync(FriendEntry friend);
    Task RemoveAsync(string pilotId);
    Task<bool> ExistsAsync(string pilotId);
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
}

// ============================================================
// IN-MEMORY PILOT REPOSITORY
// TODO: Connect to Aviates Air pilot account API
// ============================================================

public class InMemoryPilotRepository : IPilotRepository
{
    // MINOR-19: Added lock — was the only repository with zero thread safety.
    private readonly object _lock = new();
    private PilotStatistics? _stats;
    private PilotProfile? _profile;

    public Task<PilotStatistics?> GetStatisticsAsync(string pilotId)
    {
        lock (_lock) return Task.FromResult(_stats);
    }

    public Task SaveStatisticsAsync(PilotStatistics stats)
    {
        lock (_lock) _stats = stats;
        return Task.CompletedTask;
    }

    public Task<PilotProfile?> GetProfileAsync()
    {
        lock (_lock) return Task.FromResult(_profile);
    }

    public Task SaveProfileAsync(PilotProfile profile)
    {
        lock (_lock) _profile = profile;
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

    public Task<double> GetAverageScoreAsync()
    {
        lock (_lock)
        {
            if (!_landings.Any()) return Task.FromResult(0.0);
            return Task.FromResult(_landings.Average(l => l.LandingScore));
        }
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
}

// ============================================================
// JSON-PERSISTED FRIEND REPOSITORY
// Friends survive app restarts — saved to AppData\Roaming.
// ============================================================

public class JsonFriendRepository : IFriendRepository
{
    private readonly List<FriendEntry> _friends = [];
    private readonly object _lock = new();
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AviatesAirTracker", "friends.json");

    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public JsonFriendRepository() { Load(); }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<List<FriendEntry>>(File.ReadAllText(_path));
                if (loaded != null) _friends.AddRange(loaded);
                Log.Debug("[FriendRepo] Loaded {Count} friends from disk", _friends.Count);
            }
        }
        catch (Exception ex) { Log.Warning(ex, "[FriendRepo] Failed to load"); }
    }

    private void SaveSnapshot(List<FriendEntry> snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(snapshot, _opts));
        }
        catch (Exception ex) { Log.Warning(ex, "[FriendRepo] Failed to save"); }
    }

    public Task<List<FriendEntry>> GetAllAsync()
    {
        lock (_lock) return Task.FromResult(_friends.OrderBy(f => f.PilotName).ToList());
    }

    public Task AddAsync(FriendEntry friend)
    {
        List<FriendEntry>? snapshot = null;
        lock (_lock)
        {
            if (!_friends.Any(f => f.PilotId == friend.PilotId))
            {
                _friends.Add(friend);
                snapshot = _friends.ToList();
            }
        }
        if (snapshot != null) SaveSnapshot(snapshot);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string pilotId)
    {
        List<FriendEntry> snapshot;
        lock (_lock)
        {
            _friends.RemoveAll(f => f.PilotId == pilotId);
            snapshot = _friends.ToList();
        }
        SaveSnapshot(snapshot);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string pilotId)
    {
        lock (_lock) return Task.FromResult(_friends.Any(f => f.PilotId == pilotId));
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
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AviatesAirTracker", "direct_messages.json");

    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public JsonMessageRepository() { Load(); }

    // Two messages are the same if same sender/recipient/content and sent within 2 minutes of each other.
    // Handles the case where the backend assigns a different ID to a locally-originated message.
    private static bool IsDuplicate(PilotMessage a, PilotMessage b) =>
        a.SenderId    == b.SenderId &&
        a.RecipientId == b.RecipientId &&
        string.Equals(a.Content?.Trim(), b.Content?.Trim(), StringComparison.Ordinal) &&
        Math.Abs((NormalizeUtc(a.SentAt) - NormalizeUtc(b.SentAt)).TotalSeconds) < 120;

    private static DateTime NormalizeUtc(DateTime dt) =>
        dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime()
        : DateTime.SpecifyKind(dt, DateTimeKind.Utc);

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<List<PilotMessage>>(File.ReadAllText(_path));
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
                        SaveSnapshot(_direct);
                    }
                }
                Log.Debug("[MessageRepo] Loaded {Count} direct messages from disk", _direct.Count);
            }
        }
        catch (Exception ex) { Log.Warning(ex, "[MessageRepo] Failed to load"); }
    }

    private void SaveSnapshot(List<PilotMessage> snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(snapshot, _opts));
        }
        catch (Exception ex) { Log.Warning(ex, "[MessageRepo] Failed to save"); }
    }

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
        List<PilotMessage>? directSnapshot = null;
        lock (_lock)
        {
            if (message.Type == MessageType.Direct)
            {
                var existingById = _direct.FirstOrDefault(m => m.Id == message.Id);
                if (existingById != null)
                    _direct[_direct.IndexOf(existingById)] = message;
                else if (!_direct.Any(m => IsDuplicate(m, message)))
                    _direct.Add(message);
                directSnapshot = _direct.ToList();
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
        if (directSnapshot != null) SaveSnapshot(directSnapshot);
        return Task.CompletedTask;
    }

    public Task MarkReadAsync(Guid messageId)
    {
        List<PilotMessage>? snapshot = null;
        lock (_lock)
        {
            var msg = _direct.FirstOrDefault(m => m.Id == messageId);
            if (msg != null)
            {
                msg.ReadAt = DateTime.UtcNow;
                snapshot = _direct.ToList();
            }
        }
        if (snapshot != null) SaveSnapshot(snapshot);
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
// JSON-PERSISTED FLIGHT REPOSITORY
// Flight records survive app restarts — saved to AppData\Roaming.
// Replaces InMemoryFlightRepository.
// ============================================================

public class JsonFlightRepository : IFlightRepository
{
    private readonly List<FlightRecord> _flights = [];
    private readonly object _lock = new();
    private FlightRecord? _currentFlight;

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AviatesAirTracker", "flights.json");

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonFlightRepository() { Load(); }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<List<FlightRecord>>(File.ReadAllText(_path), _opts);
                if (loaded != null) _flights.AddRange(loaded);
                Log.Debug("[FlightRepo] Loaded {Count} flights from disk", _flights.Count);
            }
        }
        catch (Exception ex) { Log.Warning(ex, "[FlightRepo] Failed to load from disk"); }
    }

    private void SaveSnapshot(List<FlightRecord> snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(snapshot, _opts));
        }
        catch (Exception ex) { Log.Warning(ex, "[FlightRepo] Failed to save to disk"); }
    }

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
        List<FlightRecord> snapshot;
        lock (_lock)
        {
            _flights.Add(record);
            snapshot = _flights.ToList();
            Log.Debug("[FlightRepo] Flight saved: {Id}", record.Id);
        }
        SaveSnapshot(snapshot);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(FlightRecord record)
    {
        List<FlightRecord>? snapshot = null;
        lock (_lock)
        {
            var idx = _flights.FindIndex(f => f.Id == record.Id);
            if (idx >= 0)
            {
                _flights[idx] = record;
                snapshot = _flights.ToList();
            }
        }
        if (snapshot != null) SaveSnapshot(snapshot);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id)
    {
        List<FlightRecord> snapshot;
        lock (_lock)
        {
            _flights.RemoveAll(f => f.Id == id);
            snapshot = _flights.ToList();
        }
        SaveSnapshot(snapshot);
        return Task.CompletedTask;
    }

    public FlightRecord? GetCurrentFlight() { lock (_lock) return _currentFlight; }
    public void SetCurrentFlight(FlightRecord? flight) { lock (_lock) _currentFlight = flight; }
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

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AviatesAirTracker", "landings.json");

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonLandingRepository() { Load(); }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<List<LandingResult>>(File.ReadAllText(_path), _opts);
                if (loaded != null) _landings.AddRange(loaded);
                Log.Debug("[LandingRepo] Loaded {Count} landings from disk", _landings.Count);
            }
        }
        catch (Exception ex) { Log.Warning(ex, "[LandingRepo] Failed to load from disk"); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_landings, _opts));
        }
        catch (Exception ex) { Log.Warning(ex, "[LandingRepo] Failed to save to disk"); }
    }

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
            Save();
            Log.Debug("[LandingRepo] Landing saved: Score={Score}", landing.LandingScore);
        }
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
