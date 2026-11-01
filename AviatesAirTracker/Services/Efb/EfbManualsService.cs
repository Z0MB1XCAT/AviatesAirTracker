using AviatesAirTracker.Core.Efb;
using Serilog;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace AviatesAirTracker.Services.Efb;

// ============================================================
// EFB MANUALS / DOCUMENT LIBRARY
//
// Serves the AviatesAir document library (see Core/Efb/EfbManuals.cs for the structure):
// the markdown manuals embedded from /Manuals plus the QRH normal checklists generated from
// EfbChecklistLibrary. Adds the airline document-management bits: bookmarks, a recent list
// and READ & ACKNOWLEDGE for Company NOTAMs — acknowledgements are kept per document
// revision, so revising a notice asks every pilot to acknowledge it again.
//
// Bookmarks and acknowledgements persist in %AppData%\AviatesAirTracker\efb_manuals.json
// (shared by every paired device, like the rest of the EFB state).
// ============================================================

public sealed class EfbDocumentSummary
{
    public string Id { get; set; } = "";
    public string Manual { get; set; } = "";
    public string Chapter { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public List<string> Aircraft { get; set; } = [];
    public int Revision { get; set; }
    public string Effective { get; set; } = "";
    public bool RequiresAck { get; set; }
    public DateTime? AcknowledgedUtc { get; set; }
    public bool Bookmarked { get; set; }
}

public sealed class EfbManualsService
{
    private const string ResourcePrefix = "AviatesAirTracker.Manuals.";
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AviatesAirTracker", "efb_manuals.json");

    internal sealed class Persisted
    {
        public List<string> Bookmarks { get; set; } = [];
        public Dictionary<string, AckRecord> Acks { get; set; } = [];
    }
    internal sealed record AckRecord(int Revision, DateTime AtUtc);

    private readonly ConcurrentDictionary<string, byte> _bookmarks = new();
    private readonly ConcurrentDictionary<string, AckRecord> _acks = new();
    private readonly LinkedList<string> _recent = new();
    private readonly object _gate = new();

    public IReadOnlyList<EfbDocument> Documents { get; }

    /// <summary>Raised when an acknowledgement changes (the tablet's NOTAM badge).</summary>
    public event EventHandler? Changed;

    public EfbManualsService()
    {
        var embedded = LoadEmbedded();
        var effective = embedded.Select(d => d.Effective).DefaultIfEmpty("").Max() ?? "";
        Documents = EfbManualLibrary.Ordered(embedded.Concat(EfbManualLibrary.ChecklistDocuments(effective))).ToList();
        Load();
    }

    private List<EfbDocument> LoadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        var docs = new List<EfbDocument>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix) && n.EndsWith(".md")))
        {
            try
            {
                using var s = asm.GetManifestResourceStream(name)!;
                using var r = new StreamReader(s);
                var doc = EfbManualLibrary.Parse(r.ReadToEnd());
                if (doc != null) docs.Add(doc);
                else Log.Warning("[EFB] Manual {Name} has no front matter — skipped", name);
            }
            catch (Exception ex) { Log.Warning(ex, "[EFB] Could not load manual {Name}", name); }
        }
        return docs;
    }

    public IReadOnlyList<EfbManualInfo> Manuals => EfbManualLibrary.Manuals;

    public IReadOnlyList<EfbDocumentSummary> List() => Documents.Select(ToSummary).ToList();

    public IReadOnlyList<EfbDocSearchHit> Search(string query) => EfbManualLibrary.Search(Documents, query);

    public EfbDocument? Get(string id)
    {
        var doc = Documents.FirstOrDefault(d => d.Id == id);
        if (doc != null) PushRecent(id);
        return doc;
    }

    public EfbDocumentSummary? Summary(string id) =>
        Documents.FirstOrDefault(d => d.Id == id) is { } d ? ToSummary(d) : null;

    public IReadOnlyList<string> Recent()
    {
        lock (_gate) return _recent.ToList();
    }

    /// <summary>Notices that require acknowledgement and haven't been acknowledged at their current revision.</summary>
    public int PendingAcknowledgements =>
        Documents.Count(d => d.RequiresAck && !IsAcknowledged(d));

    public bool ToggleBookmark(string id)
    {
        if (Documents.All(d => d.Id != id)) return false;
        if (!_bookmarks.TryRemove(id, out _)) _bookmarks[id] = 1;
        Save();
        return true;
    }

    /// <returns>false if the document is unknown or doesn't take acknowledgement.</returns>
    public bool Acknowledge(string id)
    {
        var doc = Documents.FirstOrDefault(d => d.Id == id);
        if (doc is not { RequiresAck: true }) return false;
        _acks[id] = new AckRecord(doc.Revision, DateTime.UtcNow);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // ── internals ───────────────────────────────────────────

    private bool IsAcknowledged(EfbDocument d) =>
        _acks.TryGetValue(d.Id, out var a) && a.Revision >= d.Revision;

    private EfbDocumentSummary ToSummary(EfbDocument d) => new()
    {
        Id = d.Id,
        Manual = d.Manual,
        Chapter = d.Chapter,
        Title = d.Title,
        Summary = d.Summary,
        Tags = d.Tags.ToList(),
        Aircraft = d.Aircraft.ToList(),
        Revision = d.Revision,
        Effective = d.Effective,
        RequiresAck = d.RequiresAck,
        AcknowledgedUtc = IsAcknowledged(d) ? _acks[d.Id].AtUtc : null,
        Bookmarked = _bookmarks.ContainsKey(d.Id),
    };

    private void PushRecent(string id)
    {
        lock (_gate)
        {
            _recent.Remove(id);
            _recent.AddFirst(id);
            while (_recent.Count > 8) _recent.RemoveLast();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var p = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_path));
            if (p == null) return;
            foreach (var b in p.Bookmarks) _bookmarks[b] = 1;
            foreach (var (k, v) in p.Acks) _acks[k] = v;
        }
        catch (Exception ex) { Log.Warning(ex, "[EFB] Could not read manuals state"); }
    }

    private void Save()
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(new Persisted
                {
                    Bookmarks = _bookmarks.Keys.ToList(),
                    Acks = _acks.ToDictionary(k => k.Key, k => k.Value),
                }, _json));
            }
        }
        catch (Exception ex) { Log.Warning(ex, "[EFB] Could not save manuals state"); }
    }
}
