using AviatesAirTracker.Core.Efb;
using System.Collections.Concurrent;

namespace AviatesAirTracker.Services.Efb;

// ============================================================
// EFB CHECKLIST SERVICE
//
// Holds live check state for the per-aircraft checklist sets in EfbChecklistLibrary.
// State is shared across every paired device (a single-crew session), so ticking an
// item on the tablet reflects on a second monitor instantly via the WebSocket rebroadcast.
//
// The active set follows the flight's aircraft type (booking → SimBrief → sim) unless the
// pilot picks one manually on the tablet; the override is shared across devices too.
//
// State is in-memory, keyed set/checklist/item, and resets when a new flight session
// starts. Timestamps are captured the moment an item is checked.
// ============================================================

public sealed class EfbChecklistItemState
{
    public string Id { get; set; } = "";
    public string Challenge { get; set; } = "";
    public string Response { get; set; } = "";
    public string? Who { get; set; }
    public string? Note { get; set; }
    public bool LineBefore { get; set; }
    public bool IsChecked { get; set; }
    public DateTime? CheckedAtUtc { get; set; }
}

public sealed class EfbChecklistState
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Phase { get; set; } = "";
    public string PhaseKey { get; set; } = "";
    public List<EfbChecklistItemState> Items { get; set; } = [];
    public int CheckedCount => Items.Count(i => i.IsChecked);
    public int TotalCount => Items.Count;
    public bool IsComplete => TotalCount > 0 && CheckedCount == TotalCount;
    public DateTime? CompletedAtUtc { get; set; }
}

public sealed class EfbChecklistSetInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Style { get; set; } = "";
    public List<string> Types { get; set; } = [];
}

/// <summary>What the tablet renders: the active set's checklists plus the set picker.</summary>
public sealed class EfbChecklistBundle
{
    public string SetId { get; set; } = "";
    public string SetName { get; set; } = "";
    public string Style { get; set; } = "";
    public string Source { get; set; } = "";
    /// <summary>The flight's aircraft type the auto selection was made from.</summary>
    public string AircraftType { get; set; } = "";
    /// <summary>The set the aircraft type resolves to (what AUTO would pick).</summary>
    public string AutoSetId { get; set; } = "";
    /// <summary>True when the pilot has overridden the automatic choice.</summary>
    public bool Manual { get; set; }
    public List<EfbChecklistSetInfo> Sets { get; set; } = [];
    public List<EfbChecklistState> Checklists { get; set; } = [];
}

public sealed class EfbChecklistService
{
    // "set/checklist" -> (itemId -> checked at)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>> _state = new();
    private volatile string? _manualSetId;

    /// <summary>Raised whenever any item or the set selection changes so the server can rebroadcast.</summary>
    public event EventHandler? Changed;

    public string? ManualSetId => _manualSetId;

    private static string Key(string setId, string checklistId) => $"{setId}/{checklistId}";

    /// <summary>The set in force for this aircraft type (manual override wins).</summary>
    public EfbChecklistSet ActiveSet(string? aircraftType) =>
        EfbChecklistLibrary.FindSet(_manualSetId) ?? EfbChecklistLibrary.Resolve(aircraftType);

    public EfbChecklistBundle GetBundle(string? aircraftType)
    {
        var auto = EfbChecklistLibrary.Resolve(aircraftType);
        var set = ActiveSet(aircraftType);
        return new EfbChecklistBundle
        {
            SetId = set.Id,
            SetName = set.Name,
            Style = set.Style,
            Source = set.Source,
            AircraftType = aircraftType ?? "",
            AutoSetId = auto.Id,
            Manual = _manualSetId != null,
            Sets = EfbChecklistLibrary.Sets.Select(s => new EfbChecklistSetInfo
            {
                Id = s.Id, Name = s.Name, Style = s.Style, Types = s.Types.Where(t => t.Length == 4).ToList(),
            }).ToList(),
            Checklists = GetChecklists(set),
        };
    }

    public List<EfbChecklistState> GetChecklists(EfbChecklistSet set) =>
        set.Checklists.Select(def =>
        {
            _state.TryGetValue(Key(set.Id, def.Id), out var checks);
            var cs = new EfbChecklistState
            {
                Id = def.Id,
                Name = def.Name,
                Phase = def.Phase,
                PhaseKey = def.PhaseKey,
                Items = def.Items.Select(it =>
                {
                    DateTime? ts = checks != null && checks.TryGetValue(it.Id, out var t) ? t : null;
                    return new EfbChecklistItemState
                    {
                        Id = it.Id,
                        Challenge = it.Challenge,
                        Response = it.Response,
                        Who = it.Who,
                        Note = it.Note,
                        LineBefore = it.LineBefore,
                        IsChecked = ts.HasValue,
                        CheckedAtUtc = ts,
                    };
                }).ToList(),
            };
            if (cs.IsComplete)
                cs.CompletedAtUtc = cs.Items.Max(i => i.CheckedAtUtc);
            return cs;
        }).ToList();

    /// <summary>Pin a set (null / "auto" returns to following the aircraft type).</summary>
    /// <returns>false if the set id is unknown.</returns>
    public bool SelectSet(string? setId)
    {
        if (string.IsNullOrWhiteSpace(setId) || setId.Equals("auto", StringComparison.OrdinalIgnoreCase))
            _manualSetId = null;
        else if (EfbChecklistLibrary.FindSet(setId) is { } s)
            _manualSetId = s.Id;
        else
            return false;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <returns>false if the set/checklist/item id is unknown.</returns>
    public bool SetItem(string setId, string checklistId, string itemId, bool isChecked)
    {
        var set = EfbChecklistLibrary.FindSet(setId);
        var def = set?.Checklists.FirstOrDefault(c => c.Id == checklistId);
        if (set == null || def == null || def.Items.All(i => i.Id != itemId)) return false;

        var checks = _state.GetOrAdd(Key(set.Id, checklistId), _ => new());
        if (isChecked) checks.TryAdd(itemId, DateTime.UtcNow);   // keep the first tick's time
        else checks.TryRemove(itemId, out _);

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Check (override) every remaining item of one checklist — Boeing "CHKL OVRD".</summary>
    public bool CompleteChecklist(string setId, string checklistId)
    {
        var set = EfbChecklistLibrary.FindSet(setId);
        var def = set?.Checklists.FirstOrDefault(c => c.Id == checklistId);
        if (set == null || def == null) return false;
        var checks = _state.GetOrAdd(Key(set.Id, checklistId), _ => new());
        var now = DateTime.UtcNow;
        foreach (var it in def.Items) checks.TryAdd(it.Id, now);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void ResetChecklist(string setId, string checklistId)
    {
        if (_state.TryGetValue(Key(setId, checklistId), out var checks)) checks.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reset one set, or everything when <paramref name="setId"/> is null.</summary>
    public void ResetAll(string? setId = null)
    {
        if (setId == null) _state.Clear();
        else foreach (var k in _state.Keys.Where(k => k.StartsWith(setId + "/", StringComparison.Ordinal)))
            _state.TryRemove(k, out _);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
