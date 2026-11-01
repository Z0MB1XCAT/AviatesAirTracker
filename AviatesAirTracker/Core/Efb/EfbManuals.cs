using System.Text;
using System.Text.RegularExpressions;

namespace AviatesAirTracker.Core.Efb;

// ============================================================
// EFB MANUALS — the AviatesAir document library, as a real airline structures it:
//
//   CN     Company NOTAMs        — current operational notices; read & acknowledge
//   OM-A   Operations Manual A   — general / basic: policy, fuel, minima, SOPs, FDM
//   OM-B   Operations Manual B   — aircraft operating data per fleet
//   QRH    Quick Reference       — normal checklists (generated from EfbChecklistLibrary,
//                                  so they can never drift from the Checklists app) + memory items
//   GUIDE  Pilot Guides          — tracker, EFB, weather & performance
//
// Source of truth: the markdown files in /Manuals (embedded into the assembly and read by
// EfbManualsService; the PDF build reads the same files). Each starts with a front matter
// block:
//
//   ---
//   id: om-a-05              manual: OM-A          chapter: 5
//   title: …                 summary: …            tags: a, b, c
//   revision: 0              effective: 2026-09-24 order: 5
//   ack: true                (notices that must be read & acknowledged)
//   aircraft: a320, b737ng   (checklist set ids — marks a document as type-specific)
//   ---
//
// Markdown dialect (rendered by the tablet and the PDF build): ## / ### headings (anchored),
// paragraphs, - and 1. lists, | tables |, > **NOTE** / **CAUTION** / **WARNING** callouts,
// **bold**, *italic*, `code`, --- rules and links [text](doc:om-a-05#anchor).
// ============================================================

public sealed class EfbManualInfo
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
}

public sealed class EfbDocSection
{
    public string Anchor { get; set; } = "";
    public string Title { get; set; } = "";
    public int Level { get; set; }
}

public sealed class EfbDocument
{
    public string Id { get; init; } = "";
    public string Manual { get; init; } = "";
    public string Chapter { get; init; } = "";
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    /// <summary>Checklist-set ids (EfbChecklistLibrary) this document is specific to; empty = all.</summary>
    public IReadOnlyList<string> Aircraft { get; init; } = [];
    public int Revision { get; init; }
    public string Effective { get; init; } = "";
    public bool RequiresAck { get; init; }
    public int Order { get; init; }
    public string BodyMarkdown { get; init; } = "";
    public IReadOnlyList<EfbDocSection> Sections { get; init; } = [];
}

public sealed class EfbDocSearchHit
{
    public string DocId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Manual { get; set; } = "";
    public string Chapter { get; set; } = "";
    public string Section { get; set; } = "";
    public string Anchor { get; set; } = "";
    public string Snippet { get; set; } = "";
}

public static class EfbManualLibrary
{
    public static readonly IReadOnlyList<EfbManualInfo> Manuals =
    [
        new() { Code = "CN",    Name = "Company NOTAMs",
                Description = "Current operational notices. Read and acknowledge before you fly." },
        new() { Code = "OM-A",  Name = "Operations Manual Part A",
                Description = "General and basic: operating policy, flight planning, fuel, minima, SOPs, approach & landing, flight data monitoring." },
        new() { Code = "OM-B",  Name = "Operations Manual Part B",
                Description = "Aircraft operating matters: limitations, configuration policy and handling notes for each fleet." },
        new() { Code = "QRH",   Name = "Quick Reference Handbook",
                Description = "Normal checklists for every fleet type and the immediate-action items." },
        new() { Code = "GUIDE", Name = "Pilot Guides",
                Description = "Using the AviatesAir Tracker, ACARS and EFB, plus weather and performance know-how." },
    ];

    public static int ManualRank(string code)
    {
        for (int i = 0; i < Manuals.Count; i++) if (Manuals[i].Code == code) return i;
        return Manuals.Count;
    }

    // ── parsing ─────────────────────────────────────────────

    /// <summary>Parses one markdown document with its front matter; null when there is none or no id.</summary>
    public static EfbDocument? Parse(string text)
    {
        text = text.Replace("\r\n", "\n").TrimStart('﻿');
        if (!text.StartsWith("---\n")) return null;
        int end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return null;

        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text[4..end].Split('\n'))
        {
            int c = line.IndexOf(':');
            if (c > 0) meta[line[..c].Trim()] = line[(c + 1)..].Trim();
        }
        int bodyStart = text.IndexOf('\n', end + 1);
        var body = bodyStart < 0 ? "" : text[(bodyStart + 1)..].Trim('\n');

        string M(string k) => meta.TryGetValue(k, out var v) ? v : "";
        List<string> ListOf(string k) => M(k).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (M("id").Length == 0) return null;

        return new EfbDocument
        {
            Id = M("id"),
            Manual = M("manual").ToUpperInvariant(),
            Chapter = M("chapter"),
            Title = M("title"),
            Summary = M("summary"),
            Tags = ListOf("tags"),
            Aircraft = ListOf("aircraft").Select(a => a.ToLowerInvariant()).ToList(),
            Revision = int.TryParse(M("revision"), out var rev) ? rev : 0,
            Effective = M("effective"),
            RequiresAck = M("ack").Equals("true", StringComparison.OrdinalIgnoreCase),
            Order = int.TryParse(M("order"), out var o) ? o : 0,
            BodyMarkdown = body,
            Sections = SectionsOf(body),
        };
    }

    /// <summary>The anchor a heading gets (same rule the tablet uses for scroll targets).</summary>
    public static string Anchor(string heading)
    {
        var sb = new StringBuilder();
        foreach (var ch in heading.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        var a = Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
        return a.Length == 0 ? "section" : a;
    }

    private static string StripInline(string s) =>
        Regex.Replace(Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", "$1"), @"[*`]", "");

    public static List<EfbDocSection> SectionsOf(string body) =>
        body.Split('\n')
            .Where(l => l.StartsWith("## ") || l.StartsWith("### "))
            .Select(l =>
            {
                int level = l.StartsWith("### ") ? 3 : 2;
                var title = StripInline(l[(level + 1)..].Trim());
                return new EfbDocSection { Level = level, Title = title, Anchor = Anchor(title) };
            }).ToList();

    /// <summary>All doc: link targets in a body ("om-a-05" or "om-a-05#anchor").</summary>
    public static IEnumerable<string> LinkTargets(string body) =>
        Regex.Matches(body, @"\]\(doc:([^)\s]+)\)").Select(m => m.Groups[1].Value);

    public static bool AppliesTo(EfbDocument d, string? aircraftType) =>
        d.Aircraft.Count > 0 && !string.IsNullOrWhiteSpace(aircraftType)
        && d.Aircraft.Contains(EfbChecklistLibrary.Resolve(aircraftType).Id);

    public static IEnumerable<EfbDocument> Ordered(IEnumerable<EfbDocument> docs) =>
        docs.OrderBy(d => ManualRank(d.Manual)).ThenBy(d => d.Order).ThenBy(d => d.Id);

    // ── QRH normal checklists, generated from the checklist library ──

    public static IReadOnlyList<EfbDocument> ChecklistDocuments(string effective)
    {
        var docs = new List<EfbDocument>();
        int n = 0;
        foreach (var set in EfbChecklistLibrary.Sets.Where(s => s.Id != EfbChecklistLibrary.GenericSetId))
        {
            n++;
            var sb = new StringBuilder();
            sb.Append("> **NOTE** These are the same checklists as the EFB **Checklists** app, which follows your booked aircraft automatically. Complete the flows first, then read the checklist as challenge and response.\n\n");
            foreach (var cl in set.Checklists)
            {
                sb.Append($"## {cl.Name}\n\n| Challenge | Response |\n|---|---|\n");
                foreach (var it in cl.Items)
                {
                    if (it.LineBefore) sb.Append("| ─── | ─── |\n");
                    var resp = $"**{it.Response.Replace("|", "/")}**" + (it.Who != null ? $" *({it.Who})*" : "");
                    var chal = it.Challenge.Replace("|", "/") + (it.Note != null ? $" (*{it.Note.Replace("|", "/")}*)" : "");
                    sb.Append($"| {chal} | {resp} |\n");
                }
                sb.Append('\n');
            }
            sb.Append($"---\n\n*Source: {set.Source} For simulation use only.*\n");
            var body = sb.ToString();
            docs.Add(new EfbDocument
            {
                Id = "qrh-nc-" + set.Id,
                Manual = "QRH",
                Chapter = $"NC {n}",
                Title = $"Normal Checklist: {set.Name}",
                Summary = $"{set.Checklists.Count} normal checklists, {set.Checklists.Sum(c => c.Items.Count)} items: {string.Join(" · ", set.Checklists.Take(4).Select(c => c.Name))}…",
                Tags = ["checklist", "normal", .. set.Types.Where(t => t.Length == 4)],
                Aircraft = [set.Id],
                Revision = 0,
                Effective = effective,
                Order = 10 + n,
                BodyMarkdown = body,
                Sections = SectionsOf(body),
            });
        }
        return docs;
    }

    // ── search ──────────────────────────────────────────────

    /// <summary>
    /// Full-text search: title/tag matches first, then body hits with the section they sit in
    /// and a ~160-character snippet around the first occurrence (one hit per section).
    /// </summary>
    public static List<EfbDocSearchHit> Search(IEnumerable<EfbDocument> docs, string query, int max = 40)
    {
        var q = (query ?? "").Trim();
        var hits = new List<EfbDocSearchHit>();
        if (q.Length < 2) return hits;

        foreach (var d in Ordered(docs))
        {
            bool titleHit = d.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                         || d.Tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase))
                         || d.Chapter.Equals(q, StringComparison.OrdinalIgnoreCase);
            if (titleHit)
                hits.Add(new EfbDocSearchHit { DocId = d.Id, Title = d.Title, Manual = d.Manual, Chapter = d.Chapter, Snippet = d.Summary });

            string section = "", anchor = "";
            var seen = new HashSet<string>();
            foreach (var raw in d.BodyMarkdown.Split('\n'))
            {
                if (raw.StartsWith("## ") || raw.StartsWith("### "))
                {
                    section = StripInline(raw.TrimStart('#', ' '));
                    anchor = Anchor(section);
                }
                var line = StripInline(raw.TrimStart('#', '>', '-', ' ', '|')).Replace("|", " · ");
                int i = line.IndexOf(q, StringComparison.OrdinalIgnoreCase);
                if (i < 0 || !seen.Add(anchor)) continue;
                int from = Math.Max(0, i - 60), len = Math.Min(line.Length - from, 160);
                var snip = (from > 0 ? "…" : "") + line.Substring(from, len).Trim() + (from + len < line.Length ? "…" : "");
                hits.Add(new EfbDocSearchHit
                {
                    DocId = d.Id, Title = d.Title, Manual = d.Manual, Chapter = d.Chapter,
                    Section = section, Anchor = anchor, Snippet = snip,
                });
                if (hits.Count >= max) return hits;
            }
        }
        return hits;
    }
}
