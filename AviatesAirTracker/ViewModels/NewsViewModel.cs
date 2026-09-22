using AviatesAirTracker.Core.Backend;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace AviatesAirTracker.ViewModels;

/// <summary>
/// Replaces NewsPage.xaml.cs's own BuildNewsRows() (6 hardcoded cards, none real) with the same
/// news_articles data the AviatesAir website reads -- via AviatesBackendClient.FetchNewsAsync's
/// public GET /api/news.
/// </summary>
public partial class NewsViewModel : ObservableObject
{
    private readonly AviatesBackendClient _backend;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasArticles;

    public ObservableCollection<RemoteNewsArticle> Articles { get; } = [];

    public NewsViewModel(AviatesBackendClient backend)
    {
        _backend = backend;
    }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var articles = await _backend.FetchNewsAsync();
            Articles.Clear();
            foreach (var a in articles) Articles.Add(a);
            HasArticles = Articles.Count > 0;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
