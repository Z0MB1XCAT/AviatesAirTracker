using System.Net.Http;
using AviatesAirTracker.Models;
using Newtonsoft.Json;
using Serilog;

namespace AviatesAirTracker.Services;

// ============================================================
// FLEET SERVICE
// Fetches fleet data from the Aviates Air backend worker.
// Aircraft types and registrations are read directly from
// Supabase (aircraft_types + aircraft_registrations tables).
// Fleet stats are derived from the type list.
// All data is cached for 5 minutes; call ClearCache() to force.
// ============================================================
public class FleetService
{
    private readonly HttpClient _backend;
    private readonly HttpClient _supabase;

    private const string BackendBase  = "https://acars.flyaviatesair.uk";
    private const string SupabaseUrl  = "https://wgyoabligpwpksdpdqsl.supabase.co";
    private const string SupabaseAnon = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6IndneW9hYmxpZ3B3cGtzZHBkcXNsIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NzU2NjQzMDYsImV4cCI6MjA5MTI0MDMwNn0.xNi0IcRcGpkWxd63G_LEGH6peuiJ-4jCy7H3SGUnx9g";

    private FleetData?  _cached;
    private DateTime    _cacheExpiry = DateTime.MinValue;
    private readonly Dictionary<string, List<AircraftRegistration>> _regCache = new();

    public FleetService()
    {
        _backend = new HttpClient
        {
            BaseAddress = new Uri(BackendBase),
            Timeout     = TimeSpan.FromSeconds(15)
        };

        _supabase = new HttpClient
        {
            BaseAddress = new Uri(SupabaseUrl),
            Timeout     = TimeSpan.FromSeconds(15)
        };
        _supabase.DefaultRequestHeaders.Add("apikey",        SupabaseAnon);
        _supabase.DefaultRequestHeaders.Add("Authorization", $"Bearer {SupabaseAnon}");
    }

    // ─── Public API ──────────────────────────────────────────

    public async Task<FleetData> GetFleetDataAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _cached != null && DateTime.UtcNow < _cacheExpiry)
            return _cached;

        var types = await FetchTypesFromSupabaseAsync();
        var stats = BuildStats(types);

        _cached      = new FleetData { Types = types, Stats = stats };
        _cacheExpiry = DateTime.UtcNow.AddMinutes(5);
        return _cached;
    }

    /// <summary>
    /// Returns all registrations for the given Supabase type_code, ordered by registration mark.
    /// Results are cached for the session; pass forceRefresh to bypass.
    /// </summary>
    public async Task<List<AircraftRegistration>> GetRegistrationsAsync(
        string typeCode, bool forceRefresh = false)
    {
        if (!forceRefresh && _regCache.TryGetValue(typeCode, out var hit))
            return hit;

        try
        {
            var encoded = Uri.EscapeDataString(typeCode);
            var json = await _supabase.GetStringAsync(
                $"/rest/v1/aircraft_registrations?type_code=eq.{encoded}&select=*&order=registration.asc");
            var regs = JsonConvert.DeserializeObject<List<AircraftRegistration>>(json) ?? [];
            _regCache[typeCode] = regs;
            Log.Information("[FleetService] Loaded {Count} registrations for {TypeCode} from Supabase", regs.Count, typeCode);
            return regs;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[FleetService] Error fetching registrations from Supabase for {TypeCode}", typeCode);
            return [];
        }
    }

    public void ClearCache()
    {
        _cached      = null;
        _cacheExpiry = DateTime.MinValue;
        _regCache.Clear();
    }

    // ─── Private helpers ─────────────────────────────────────

    private async Task<List<AircraftType>> FetchTypesFromSupabaseAsync()
    {
        // Return in-memory cache if still warm
        if (_cached != null && DateTime.UtcNow < _cacheExpiry)
            return _cached.Types;

        try
        {
            // Fetch types and all registration summaries in parallel
            var typesTask = _supabase.GetStringAsync(
                "/rest/v1/aircraft_types?select=*&order=sort_order.asc");
            var regsTask  = _supabase.GetStringAsync(
                "/rest/v1/aircraft_registrations?select=type_code,status");

            await Task.WhenAll(typesTask, regsTask);

            var types = JsonConvert.DeserializeObject<List<AircraftType>>(typesTask.Result) ?? [];
            var regs  = JsonConvert.DeserializeObject<List<AircraftRegistration>>(regsTask.Result) ?? [];

            // Compute count fields from registration data (the Supabase table has no stored counts)
            var byType = regs.GroupBy(r => r.TypeCode)
                             .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var t in types)
            {
                var bucket = byType.GetValueOrDefault(t.TypeCode, []);
                t.TotalAircraft    = bucket.Count;
                t.ActiveCount      = bucket.Count(r => r.Status == "active");
                t.MaintenanceCount = bucket.Count(r => r.Status == "maintenance");
                t.OrderedCount     = bucket.Count(r => r.Status == "ordered");
            }

            Log.Information("[FleetService] Loaded {Count} aircraft types from Supabase", types.Count);
            return types;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[FleetService] Error fetching aircraft types from Supabase — falling back to backend");
            return await FetchTypesFromBackendAsync();
        }
    }

    private async Task<List<AircraftType>> FetchTypesFromBackendAsync()
    {
        try
        {
            var json  = await _backend.GetStringAsync("/api/fleet/types");
            var types = JsonConvert.DeserializeObject<List<AircraftType>>(json) ?? [];
            Log.Information("[FleetService] Loaded {Count} aircraft types from backend", types.Count);
            return types;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[FleetService] Error fetching fleet types from {Url}", BackendBase);
            return [];
        }
    }

    private static FleetStats BuildStats(List<AircraftType> types) => new()
    {
        TotalAircraft = types.Sum(t => t.TotalAircraft),
        InService     = types.Sum(t => t.ActiveCount),
        InMaintenance = types.Sum(t => t.MaintenanceCount),
        OnOrder       = types.Sum(t => t.OrderedCount),
    };
}
