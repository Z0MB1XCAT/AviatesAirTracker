using AviatesAirTracker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AviatesAirTracker.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _svc;
    private readonly SimBriefService _simBriefSvc;

    [ObservableProperty] private string _pilotName = "";
    [ObservableProperty] private string _pilotId = "";
    [ObservableProperty] private string _acarsKey = "";
    [ObservableProperty] private string _simBriefUsername = "";
    [ObservableProperty] private bool _autoConnect = true;
    [ObservableProperty] private bool _showAlerts = true;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isFetchingSimBrief;

    public SettingsViewModel(SettingsService svc, SimBriefService simBriefSvc)
    {
        _svc = svc;
        _simBriefSvc = simBriefSvc;
        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        var s = _svc.Settings;
        PilotName = s.PilotName;
        PilotId = s.PilotId;
        AcarsKey = s.AcarsKey;
        SimBriefUsername = s.SimBriefUsername;
        AutoConnect = s.AutoConnectSimConnect;
        ShowAlerts = s.ShowApproachAlerts;
        MinimizeToTray = s.MinimizeToTray;
    }

    [RelayCommand]
    public void Save()
    {
        var s = _svc.Settings;
        s.PilotName = PilotName;
        s.PilotId = PilotId;
        s.AcarsKey = AcarsKey;
        s.SimBriefUsername = SimBriefUsername;
        s.AutoConnectSimConnect = AutoConnect;
        s.ShowApproachAlerts = ShowAlerts;
        s.MinimizeToTray = MinimizeToTray;
        _svc.Save();
        StatusMessage = "Settings saved successfully.";
    }

    [RelayCommand]
    public async Task TestSimBriefAsync()
    {
        if (string.IsNullOrWhiteSpace(SimBriefUsername))
        {
            StatusMessage = "Enter your SimBrief username first.";
            return;
        }
        IsFetchingSimBrief = true;
        StatusMessage = "Fetching from SimBrief...";
        try
        {
            var plan = await _simBriefSvc.FetchLatestOFPAsync(SimBriefUsername);
            StatusMessage = plan != null
                ? $"OK — Latest OFP: {plan.DepartureICAO}→{plan.ArrivalICAO}"
                : "No OFP found.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsFetchingSimBrief = false;
        }
    }
}
