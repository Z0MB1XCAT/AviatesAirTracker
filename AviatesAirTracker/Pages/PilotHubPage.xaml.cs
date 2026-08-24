using AviatesAirTracker.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace AviatesAirTracker.Pages;

public sealed partial class PilotHubPage : Page
{
    private readonly PilotHubViewModel _viewModel;

    public PilotHubPage()
    {
        InitializeComponent();

        _viewModel = App.GetService<PilotHubViewModel>();
        DataContext = _viewModel;

        Loaded += async (_, _) =>
        {
            await _viewModel.RefreshAsync();
        };
    }
}
