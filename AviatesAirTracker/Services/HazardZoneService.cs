using System.IO;
using System.Text.Json;
using AviatesAirTracker.Models;
using Serilog;

namespace AviatesAirTracker.Services;

/// <summary>
/// Loads a hand-maintained list of hazard zones (conflict areas, volcanic activity,
/// wildfires) from a local JSON file so Hoppies advisories can flag departure/arrival
/// airports without any live news/data feed. Call Reload() any time to pick up manual
/// edits to the file without restarting the app.
/// </summary>
public class HazardZoneService
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AviatesAirTracker", "hazard_zones.json");

    private List<HazardZoneEntry> _zones = [];

    public HazardZoneService()
    {
        Reload();
    }

    /// <summary>Re-reads hazard_zones.json from disk, seeding an example file if none exists yet.</summary>
    public void Reload()
    {
        try
        {
            if (!File.Exists(_path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var example = new List<HazardZoneEntry>
                {
                    new() { Icao = "OSDI", Type = "conflict", Message = "ACTIVE CONFLICT ZONE - EXERCISE EXTREME CAUTION" },
                    new() { Icao = "BIKF", Type = "volcano",  Message = "VOLCANIC ACTIVITY REPORTED IN VICINITY - MONITOR NOTAMS" }
                };
                File.WriteAllText(_path, JsonSerializer.Serialize(example, new JsonSerializerOptions { WriteIndented = true }));
                _zones = example;
                Log.Information("[HazardZones] Seeded example file at {Path}", _path);
                return;
            }

            var json = File.ReadAllText(_path);
            _zones = JsonSerializer.Deserialize<List<HazardZoneEntry>>(json) ?? [];
            Log.Information("[HazardZones] Loaded {Count} hazard zone entries", _zones.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[HazardZones] Failed to load hazard_zones.json");
            _zones = [];
        }
    }

    /// <summary>Returns any hazard entries matching the given ICAO exactly (case-insensitive).</summary>
    public List<HazardZoneEntry> GetHazards(string icao)
    {
        if (string.IsNullOrWhiteSpace(icao)) return [];
        return _zones.Where(z => string.Equals(z.Icao, icao, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
