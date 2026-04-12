using AviatesAirTracker.Models;
using AviatesAirTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;

namespace AviatesAirTracker.ViewModels;

public partial class FleetViewModel : ObservableObject
{
    private readonly FleetService _fleet;

    [ObservableProperty] private FleetData    _data           = new();
    [ObservableProperty] private AircraftType? _selectedType;
    [ObservableProperty] private List<AircraftRegistration> _selectedRegistrations = [];
    [ObservableProperty] private bool         _isLoading      = false;
    [ObservableProperty] private bool         _isLoadingRegs  = false;
    [ObservableProperty] private string?      _error;
    [ObservableProperty] private string       _subsidiaryFilter = "All";

    public FleetViewModel(FleetService fleet) => _fleet = fleet;

    // ─── Computed ────────────────────────────────────────────

    public IEnumerable<AircraftType> FilteredTypes =>
        SubsidiaryFilter == "All"
            ? Data.Types
            : Data.Types.Where(t => t.Subsidiary == SubsidiaryFilter);

    // ─── Commands ────────────────────────────────────────────

    public async Task LoadAsync()
    {
        IsLoading = true;
        Error     = null;
        try
        {
            Data = await _fleet.GetFleetDataAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[FleetViewModel] Load error");
            Error = "Unable to load fleet data. Check your connection and backend URL in Settings.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task SelectTypeAsync(AircraftType type)
    {
        // Toggle off if same type clicked again
        if (SelectedType?.TypeCode == type.TypeCode)
        {
            SelectedType          = null;
            SelectedRegistrations = [];
            return;
        }

        SelectedType          = type;
        SelectedRegistrations = [];
        IsLoadingRegs         = true;

        try
        {
            SelectedRegistrations = await _fleet.GetRegistrationsAsync(type.TypeCode);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[FleetViewModel] Registration fetch error");
        }
        finally
        {
            IsLoadingRegs = false;
        }
    }

    public void ClearSelection()
    {
        SelectedType          = null;
        SelectedRegistrations = [];
    }

    public async Task RefreshAsync()
    {
        _fleet.ClearCache();
        var previousTypeCode = SelectedType?.TypeCode;
        ClearSelection();
        await LoadAsync();

        if (previousTypeCode != null)
        {
            var type = Data.Types.FirstOrDefault(t => t.TypeCode == previousTypeCode);
            if (type != null) await SelectTypeAsync(type);
        }
    }

    public void SetFilter(string subsidiary)
    {
        SubsidiaryFilter = subsidiary;
        if (SelectedType != null && SelectedType.Subsidiary != subsidiary && subsidiary != "All")
            ClearSelection();
        OnPropertyChanged(nameof(FilteredTypes));
    }
}
