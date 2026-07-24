using AviatesAirTracker.Core.Efb;
using System.Collections.Concurrent;

namespace AviatesAirTracker.Services.Efb;

// ============================================================
// EFB CHECKLIST SERVICE
//
// Holds live check state for the default checklists. State is shared
// across every paired device (a single-crew session), so ticking an
// item on the tablet reflects on a second monitor instantly via the
// WebSocket rebroadcast.
//
// State is in-memory and resets when a new flight session starts.
// Timestamps are captured the moment an item is checked.
// ============================================================

public sealed class EfbChecklistItemState
{
    public string Id { get; set; } = "";
    public string Challenge { get; set; } = "";
    public string Response { get; set; } = "";
    public bool IsChecked { get; set; }
    public DateTime? CheckedAtUtc { get; set; }
}

public sealed class EfbChecklistState
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Phase { get; set; } = "";
    public List<EfbChecklistItemState> Items { get; set; } = [];
    public int CheckedCount => Items.Count(i => i.IsChecked);
    public int TotalCount => Items.Count;
    public bool IsComplete => TotalCount > 0 && CheckedCount == TotalCount;
    public DateTime? CompletedAtUtc { get; set; }
}

public sealed class EfbChecklistService
{
    // checklistId -> (itemId -> checked?)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime?>> _state = new();

    /// <summary>Raised whenever any item changes so the server can rebroadcast to devices.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<EfbChecklistState> GetAll()
    {
        return EfbChecklistLibrary.DefaultChecklists.Select(def =>
        {
            var checks = _state.GetOrAdd(def.Id, _ => new());
            var cs = new EfbChecklistState
            {
                Id = def.Id,
                Name = def.Name,
                Phase = def.Phase,
                Items = def.Items.Select(it =>
                {
                    checks.TryGetValue(it.Id, out var ts);
                    return new EfbChecklistItemState
                    {
                        Id = it.Id,
                        Challenge = it.Challenge,
                        Response = it.Response,
                        IsChecked = ts.HasValue,
                        CheckedAtUtc = ts,
                    };
                }).ToList(),
            };
            if (cs.IsComplete)
                cs.CompletedAtUtc = cs.Items.Where(i => i.CheckedAtUtc.HasValue)
                                            .Max(i => i.CheckedAtUtc);
            return cs;
        }).ToList();
    }

    /// <returns>false if the checklist/item id is unknown.</returns>
    public bool SetItem(string checklistId, string itemId, bool isChecked)
    {
        var def = EfbChecklistLibrary.DefaultChecklists.FirstOrDefault(c => c.Id == checklistId);
        if (def == null || def.Items.All(i => i.Id != itemId)) return false;

        var checks = _state.GetOrAdd(checklistId, _ => new());
        if (isChecked) checks[itemId] = DateTime.UtcNow;
        else checks.TryRemove(itemId, out _);

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void ResetChecklist(string checklistId)
    {
        if (_state.TryGetValue(checklistId, out var checks)) checks.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ResetAll()
    {
        _state.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
