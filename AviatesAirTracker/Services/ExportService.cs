using AviatesAirTracker.Core.Data;
using AviatesAirTracker.Models;
using Newtonsoft.Json;
using Serilog;
using System.IO;
using System.Text;

namespace AviatesAirTracker.Services;

// ============================================================
// EXPORT SERVICE
// Exports flight records, landing reports, and telemetry
// in multiple formats for pilot records and VA submission
// ============================================================

public class ExportService
{
    private readonly IFlightRepository _flightRepo;
    private readonly ILandingRepository _landingRepo;

    public ExportService(IFlightRepository flightRepo, ILandingRepository landingRepo)
    {
        _flightRepo = flightRepo;
        _landingRepo = landingRepo;
    }

    // =====================================================
    // FLIGHT REPORT — JSON
    // =====================================================

    public async Task<string> ExportFlightJsonAsync(Guid flightId)
    {
        var flight = await _flightRepo.GetByIdAsync(flightId);
        if (flight == null) throw new Exception($"Flight {flightId} not found");

        var json = JsonConvert.SerializeObject(flight, Formatting.Indented,
            new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        var path = GetExportPath($"flight_{flight.DepartureICAO}_{flight.ArrivalICAO}_{DateTime.Now:yyyyMMdd_HHmm}.json");
        await File.WriteAllTextAsync(path, json);
        Log.Information("[Export] JSON flight exported: {Path}", path);
        return path;
    }

    // =====================================================
    // LANDING LOG — CSV
    // =====================================================

    public async Task<string> ExportLandingsCsvAsync()
    {
        var landings = await _landingRepo.GetAllAsync();

        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,Airport,Runway,VS_FPM,IAS_KT,GS_KT,Pitch_Deg,Bank_Deg,Headwind_KT,Crosswind_KT,Score,Grade,Bounces,StableApproach,Flare");

        foreach (var l in landings)
        {
            sb.AppendLine(string.Join(",",
                l.Timestamp.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                l.AirportICAO,
                l.RunwayIdentifier,
                $"{l.VerticalSpeedFPM:F0}",
                $"{l.IASKts:F1}",
                $"{l.GroundSpeedKts:F1}",
                $"{l.TouchdownPitchDeg:F1}",
                $"{l.TouchdownBankDeg:F1}",
                $"{l.HeadwindComponent:F1}",
                $"{l.CrosswindComponent:F1}",
                l.LandingScore,
                l.LandingGrade,
                l.BounceCount,
                l.ApproachWasStable,
                l.FlareDetected
            ));
        }

        var path = GetExportPath($"landings_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        await File.WriteAllTextAsync(path, sb.ToString());
        Log.Information("[Export] CSV landings exported: {Path}", path);
        return path;
    }

    // =====================================================
    // TELEMETRY PATH — CSV
    // =====================================================

    public async Task<string> ExportFlightPathCsvAsync(Guid flightId)
    {
        var flight = await _flightRepo.GetByIdAsync(flightId);
        if (flight?.FlightPath == null) throw new Exception("No flight path data");

        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,Latitude,Longitude,AltitudeMSL_ft,GroundSpeed_kt,VerticalSpeed_fpm,Heading,Phase");

        foreach (var pt in flight.FlightPath)
        {
            sb.AppendLine(string.Join(",",
                pt.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                $"{pt.Latitude:F6}",
                $"{pt.Longitude:F6}",
                $"{pt.AltitudeMSL:F0}",
                $"{pt.GroundSpeed:F1}",
                $"{pt.VerticalSpeed:F0}",
                $"{pt.Heading:F1}",
                pt.Phase
            ));
        }

        var path = GetExportPath($"path_{flight.DepartureICAO}_{flight.ArrivalICAO}_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        await File.WriteAllTextAsync(path, sb.ToString());
        Log.Information("[Export] Path CSV exported: {Path}", path);
        return path;
    }

    // =====================================================
    // ACARS PIREP FORMAT
    // For submission to Aviates Air backend
    // =====================================================

    public string GeneratePirep(FlightRecord flight)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== AVIATES AIR PIREP ===");
        sb.AppendLine($"Pilot:      {flight.PilotName} ({flight.PilotId})");
        sb.AppendLine($"Flight:     {flight.FlightNumber}");
        sb.AppendLine($"Route:      {flight.DepartureICAO} → {flight.ArrivalICAO}");
        sb.AppendLine($"Aircraft:   {flight.AircraftType} ({flight.AircraftTitle})");
        sb.AppendLine($"Block Out:  {flight.BlockOutTime:HH:mm}Z");
        sb.AppendLine($"Block In:   {flight.BlockInTime:HH:mm}Z");
        sb.AppendLine($"Block Time: {flight.BlockTime:hh\\:mm}");
        sb.AppendLine($"Air Time:   {flight.AirTime:hh\\:mm}");
        sb.AppendLine($"Distance:   {flight.ActualDistanceNm:F0}nm");
        sb.AppendLine($"Max Alt:    {flight.MaxAltitudeFt:F0}ft");
        sb.AppendLine($"Fuel Used:  {flight.FuelUsedLbs:F0}lbs");
        if (flight.PrimaryLanding != null)
        {
            sb.AppendLine($"Landing VS: {flight.PrimaryLanding.VerticalSpeedFPM:F0}fpm");
            sb.AppendLine($"Land Score: {flight.PrimaryLanding.LandingScore}/100 ({flight.PrimaryLanding.LandingGrade})");
        }
        sb.AppendLine("========================");
        return sb.ToString();
    }

    // =====================================================
    // HELPERS
    // =====================================================

    private static string GetExportPath(string filename)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "AviatesAir", "Exports");

        Directory.CreateDirectory(dir);
        return Path.Combine(dir, filename);
    }
}
