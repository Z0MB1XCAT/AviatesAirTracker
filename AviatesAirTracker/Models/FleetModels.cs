using Newtonsoft.Json;

namespace AviatesAirTracker.Models;

public class AircraftType
{
    [JsonProperty("type_code")]        public string TypeCode         { get; set; } = "";
    [JsonProperty("name")]             public string Name             { get; set; } = "";
    [JsonProperty("manufacturer")]     public string Manufacturer     { get; set; } = "";
    [JsonProperty("category")]         public string Category         { get; set; } = "";
    [JsonProperty("capacity")]         public int?   Capacity         { get; set; }
    [JsonProperty("payload_tonnes")]   public double? PayloadTonnes   { get; set; }
    [JsonProperty("range_nm")]         public int    RangeNm          { get; set; }
    [JsonProperty("engines")]          public int    Engines          { get; set; }
    [JsonProperty("engine_type")]      public string EngineType       { get; set; } = "";
    [JsonProperty("cruise_speed_kts")] public int?   CruiseSpeedKts  { get; set; }
    [JsonProperty("max_altitude_ft")]  public int?   MaxAltitudeFt   { get; set; }
    [JsonProperty("subsidiary")]       public string Subsidiary       { get; set; } = "";
    [JsonProperty("emoji")]            public string Emoji            { get; set; } = "✈";
    [JsonProperty("total_aircraft")]   public int    TotalAircraft    { get; set; }
    [JsonProperty("active_count")]     public int    ActiveCount      { get; set; }
    [JsonProperty("maintenance_count")]public int    MaintenanceCount { get; set; }
    [JsonProperty("ordered_count")]    public int    OrderedCount     { get; set; }

    [JsonIgnore]
    public string PrimaryStatus =>
        ActiveCount > 0 ? "active" :
        MaintenanceCount > 0 ? "maintenance" : "ordered";

    [JsonIgnore]
    public string CapacityDisplay =>
        Capacity.HasValue ? Capacity.Value.ToString()
        : PayloadTonnes.HasValue ? $"{PayloadTonnes}t" : "—";

    [JsonIgnore]
    public string CapacityLabel => Category == "Freighter" ? "Payload" : "Capacity";
}

public class AircraftRegistration
{
    [JsonProperty("registration")]       public string Registration    { get; set; } = "";
    [JsonProperty("type_code")]          public string TypeCode        { get; set; } = "";
    [JsonProperty("status")]             public string Status          { get; set; } = "active";
    [JsonProperty("msn")]                public string? Msn            { get; set; }
    [JsonProperty("delivery_date")]      public string? DeliveryDate   { get; set; }
    [JsonProperty("expected_delivery")]  public string? ExpectedDelivery { get; set; }
    [JsonProperty("hub_icao")]           public string? HubIcao        { get; set; }
    [JsonProperty("total_flights")]      public int    TotalFlights    { get; set; }
    [JsonProperty("total_hours_tenths")] public int    TotalHoursTenths { get; set; }
    [JsonProperty("notes")]              public string? Notes          { get; set; }

    [JsonIgnore] public double TotalHours => TotalHoursTenths / 10.0;
    [JsonIgnore] public string DisplayDate =>
        Status == "ordered" ? (ExpectedDelivery ?? "TBA") : (DeliveryDate ?? "—");
    [JsonIgnore] public string DateLabel =>
        Status == "ordered" ? "Expected" : "Delivered";
}

public class FleetStats
{
    [JsonProperty("total_aircraft")]  public int TotalAircraft  { get; set; }
    [JsonProperty("in_service")]      public int InService      { get; set; }
    [JsonProperty("in_maintenance")]  public int InMaintenance  { get; set; }
    [JsonProperty("on_order")]        public int OnOrder        { get; set; }
}

public class FleetData
{
    public FleetStats  Stats { get; set; } = new();
    public List<AircraftType> Types { get; set; } = new();
}
