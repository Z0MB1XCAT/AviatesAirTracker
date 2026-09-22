using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.ObjectModel;
using Windows.UI;

namespace AviatesAirTracker.Pages;

/// <summary>
/// Pre-formatted display row for one news card in the grid. Plain mutable class with
/// { get; set; } properties, not a record (CS8852 risk, same as every prior display-row class
/// in this migration). Exactly one of <see cref="ShowPhoto"/>/<see cref="ShowGradient"/> is
/// Visible per row -- articles with a real thumbnail URL show it, everything else falls back to
/// one of three gradient bands (cycled by index, since news_articles has no per-article color).
/// </summary>
public sealed class NewsDisplayRow
{
    // The full article, carried along so OnCardTapped can navigate NewsDetailPage straight to
    // it without re-fetching or re-resolving anything.
    public RemoteNewsArticle Source { get; set; } = null!;

    public ImageSource? PhotoSource { get; set; }
    public Visibility ShowPhoto { get; set; } = Visibility.Collapsed;
    public Visibility ShowGradient { get; set; } = Visibility.Collapsed;
    public Brush GradientBrush { get; set; } = new SolidColorBrush(Colors.Transparent);

    // TagDisplay is the CSS .news-banner-tag text-transform:uppercase equivalent (WinUI TextBlock
    // has no text-transform property).
    public string TagDisplay { get; set; } = "";
    public string Date { get; set; } = "";
    public string Title { get; set; } = "";
    public string Excerpt { get; set; } = "";
}

/// <summary>
/// Backed by NewsViewModel/the real news_articles table. The tab bar was removed (always purely
/// cosmetic) and every card now navigates to NewsDetailPage for the full article on tap.
/// </summary>
public sealed partial class NewsPage : Page
{
    // news_articles' thumbnail/hero_image columns hold a root-relative path (e.g.
    // "/images/ben1.png"), confirmed directly against the table -- the same path the
    // flyaviatesair.uk website itself resolves image src attributes against. Resolving them
    // as-is against a bare BitmapImage(new Uri(...)) throws (not a valid absolute URI), which is
    // why every card silently fell back to its gradient band before this fix.
    internal const string WebsiteBaseUrl = "https://flyaviatesair.uk";

    internal static string ResolveImageUrl(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out _)) return path;
        return WebsiteBaseUrl + (path.StartsWith('/') ? path : "/" + path);
    }

    // Dark-theme hex values read directly from wwwroot/css/app.css's html[data-theme="dark"]
    // block (.news-card-banner.banner-blue/-violet/-teal), same values every prior native page
    // in this migration hardcodes rather than re-deriving from Theme/Tokens.xaml.
    // internal (not private): NewsDetailPage.xaml.cs reuses this same 3-brush cycle for its own
    // hero fallback, so an article's card and its detail page never show a different gradient.
    private static readonly Brush BannerBlue = MakeBannerGradient(0x1D, 0x4E, 0xD8, 0x0B, 0x16, 0x28);
    private static readonly Brush BannerViolet = MakeBannerGradient(0x4C, 0x1D, 0x95, 0x1E, 0x1B, 0x4B);
    private static readonly Brush BannerTeal = MakeBannerGradient(0x0F, 0x4C, 0x75, 0x0B, 0x16, 0x28);
    internal static readonly Brush[] FallbackGradients = [BannerBlue, BannerViolet, BannerTeal];

    private readonly NewsViewModel _viewModel;

    public ObservableCollection<NewsDisplayRow> NewsRows { get; } = [];

    public NewsPage()
    {
        InitializeComponent();

        _viewModel = App.GetService<NewsViewModel>();
        DataContext = _viewModel;
        NewsCardsList.ItemsSource = NewsRows;

        Loaded += async (_, _) =>
        {
            UpdateNewsGridColumns();
            await _viewModel.RefreshAsync();
            RebuildNewsRows();
        };
    }

    private void RebuildNewsRows()
    {
        NewsRows.Clear();

        for (var i = 0; i < _viewModel.Articles.Count; i++)
        {
            var a = _viewModel.Articles[i];
            var hasPhoto = !string.IsNullOrWhiteSpace(a.Thumbnail);

            NewsRows.Add(new NewsDisplayRow
            {
                Source = a,
                PhotoSource = hasPhoto ? LoadRemoteImage(ResolveImageUrl(a.Thumbnail!)) : null,
                ShowPhoto = hasPhoto ? Visibility.Visible : Visibility.Collapsed,
                ShowGradient = hasPhoto ? Visibility.Collapsed : Visibility.Visible,
                GradientBrush = FallbackGradients[i % FallbackGradients.Length],
                TagDisplay = (a.Category ?? "News").ToUpperInvariant(),
                Date = a.Date,
                Title = a.Title,
                Excerpt = a.Summary,
            });
        }

        var isEmpty = NewsRows.Count == 0;
        EmptyNewsText.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        NewsCardsList.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    // Never throws; returns null (falls back to the gradient band) if the resolved URL is
    // malformed. url is expected to already be absolute -- see ResolveImageUrl above.
    private static ImageSource? LoadRemoteImage(string url)
    {
        try { return new BitmapImage(new Uri(url)); }
        catch { return null; }
    }

    private static Brush MakeBannerGradient(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
    {
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1) };
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xFF, r1, g1, b1), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xFF, r2, g2, b2), Offset = 1 });
        return brush;
    }

    // ── Card grid columns ────────────────────────────────────────────────────────────────────
    // Same "ItemsWrapGrid wraps at a fixed ItemWidth" fix RoutesPage.xaml.cs's own
    // UpdateRouteGridColumns applies: a hardcoded card width leaves a dead gap whenever the
    // available width isn't an exact multiple of it, so ItemWidth is recomputed on every resize
    // from the panel's own ActualWidth instead.
    private const double CardMinWidth = 300;
    private const double CardGutter = 16;

    private void NewsCardsList_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateNewsGridColumns();

    private void UpdateNewsGridColumns()
    {
        if (NewsCardsList.ItemsPanelRoot is not ItemsWrapGrid panel) return;

        var available = NewsCardsList.ActualWidth;
        if (available <= 0) return;

        var columns = Math.Max(1, (int)Math.Floor((available + CardGutter) / (CardMinWidth + CardGutter)));
        panel.ItemWidth = Math.Floor(available / columns);
    }

    // ── Card tap → full article ──────────────────────────────────────────────────────────────

    private void OnCardTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: NewsDisplayRow row }) return;
        if (this.Frame == null) return;

        this.Frame.Navigate(typeof(NewsDetailPage), row.Source);
    }
}
