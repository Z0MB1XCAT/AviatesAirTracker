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
/// Visible per row -- the two photo cards (news-cityhopper.webp, news-acars-update.webp) set
/// <see cref="PhotoSource"/> and hide the gradient+emoji layer; the other four set
/// <see cref="GradientBrush"/>/<see cref="EmojiGlyph"/> and hide the photo layer.
/// </summary>
public sealed class NewsDisplayRow
{
    public ImageSource? PhotoSource { get; set; }
    public Visibility ShowPhoto { get; set; } = Visibility.Collapsed;
    public Visibility ShowGradient { get; set; } = Visibility.Collapsed;
    public Brush GradientBrush { get; set; } = new SolidColorBrush(Colors.Transparent);
    public string EmojiGlyph { get; set; } = "";

    // TagDisplay is the CSS .news-banner-tag text-transform:uppercase equivalent (WinUI TextBlock
    // has no text-transform property) -- computed once from the verbatim News.razor tag string at
    // construction, never re-derived, so the source literal in NewsPage.xaml.cs stays exact.
    public string TagDisplay { get; set; } = "";
    public string Date { get; set; } = "";
    public string Title { get; set; } = "";
    public string Excerpt { get; set; } = "";
    public string AuthorInitials { get; set; } = "";
    public string AuthorName { get; set; } = "";
}

/// <summary>
/// Native reproduction of AviatesAirTracker.LegacyWeb\Pages\News.razor. No ViewModel and no
/// service dependency -- the original has no @code block at all, just a page header, a 5-tab
/// bar (only "All News" ever active, none of the 5 has an @onclick), and 6 hardcoded news
/// cards whose "Read" links all point to href="#" (dead). Everything here is static content;
/// the only code-behind logic is the responsive card-grid column recompute (ported from
/// RoutesPage.xaml.cs's UpdateRouteGridColumns) and a documented no-op Tapped handler for the
/// "Read" links, matching the no-op pattern SupportPage.xaml.cs already established for its own
/// dead feature-card buttons.
/// </summary>
public sealed partial class NewsPage : Page
{
    // Dark-theme hex values read directly from wwwroot/css/app.css's html[data-theme="dark"]
    // block (.news-card-banner.banner-blue/-violet/-teal), same values every prior native page
    // in this migration hardcodes rather than re-deriving from Theme/Tokens.xaml.
    private static readonly Brush BannerBlue = MakeBannerGradient(0x1D, 0x4E, 0xD8, 0x0B, 0x16, 0x28);
    private static readonly Brush BannerViolet = MakeBannerGradient(0x4C, 0x1D, 0x95, 0x1E, 0x1B, 0x4B);
    private static readonly Brush BannerTeal = MakeBannerGradient(0x0F, 0x4C, 0x75, 0x0B, 0x16, 0x28);

    public ObservableCollection<NewsDisplayRow> NewsRows { get; } = [];

    public NewsPage()
    {
        InitializeComponent();

        NewsCardsList.ItemsSource = NewsRows;
        BuildNewsRows();

        // The grid is visible with content from the first layout pass (no async load, no
        // loading/empty state to gate it, unlike RoutesPage), so NewsCardsList_SizeChanged fires
        // naturally on resize -- this Loaded call just seeds the initial column count before the
        // first SizeChanged, same belt-and-suspenders reasoning as RoutesPage's own explicit call.
        Loaded += (_, _) => UpdateNewsGridColumns();
    }

    // ── News card data (verbatim from News.razor) ──────────────────────────────────────────────

    private void BuildNewsRows()
    {
        NewsRows.Add(new NewsDisplayRow
        {
            PhotoSource = LoadNewsPhoto("news-cityhopper.webp"),
            ShowPhoto = Visibility.Visible,
            ShowGradient = Visibility.Collapsed,
            TagDisplay = "Fleet Update".ToUpperInvariant(),
            Date = "Mar 20, 2026",
            Title = "Two New Airbus A321XLR Aircraft Join Our Regional Fleet",
            Excerpt = "AviatesAir Regional is pleased to confirm delivery of two A321XLR aircraft, extending our reach to ultra-long thin routes across Asia-Pacific and Europe.",
            AuthorInitials = "ZH",
            AuthorName = "Zakir Horizontal",
        });

        NewsRows.Add(new NewsDisplayRow
        {
            ShowPhoto = Visibility.Collapsed,
            ShowGradient = Visibility.Visible,
            GradientBrush = BannerViolet,
            EmojiGlyph = "🌍",
            TagDisplay = "Network Expansion".ToUpperInvariant(),
            Date = "Mar 18, 2026",
            Title = "AviatesAir Announces New African Route: FAOR–HECA",
            Excerpt = "Operations on the Johannesburg–Cairo route will commence Q2 2026, marking our first entry into the African continental market.",
            AuthorInitials = "PS",
            AuthorName = "Padhang Satrio",
        });

        NewsRows.Add(new NewsDisplayRow
        {
            ShowPhoto = Visibility.Collapsed,
            ShowGradient = Visibility.Visible,
            GradientBrush = BannerTeal,
            EmojiGlyph = "🏆",
            TagDisplay = "Community".ToUpperInvariant(),
            Date = "Mar 15, 2026",
            Title = "March Fly-In Event: Results and Top Performers",
            Excerpt = "Over 48 pilots participated in the March EGLL group fly-in. Congratulations to the top three ranked pilots and all those who joined the event!",
            AuthorInitials = "LS",
            AuthorName = "Leonardo Samsul",
        });

        NewsRows.Add(new NewsDisplayRow
        {
            PhotoSource = LoadNewsPhoto("news-acars-update.webp"),
            ShowPhoto = Visibility.Visible,
            ShowGradient = Visibility.Collapsed,
            TagDisplay = "Operations".ToUpperInvariant(),
            Date = "Mar 10, 2026",
            Title = "ACARS System Upgrade: New Features in v2.4",
            Excerpt = "Our ACARS platform has been upgraded with improved real-time telemetry reporting, enhanced fuel tracking, and new approach stability alerts.",
            AuthorInitials = "ZH",
            AuthorName = "Zakir Horizontal",
        });

        NewsRows.Add(new NewsDisplayRow
        {
            ShowPhoto = Visibility.Collapsed,
            ShowGradient = Visibility.Visible,
            GradientBrush = BannerBlue,
            EmojiGlyph = "📈",
            TagDisplay = "Career".ToUpperInvariant(),
            Date = "Mar 5, 2026",
            Title = "New Rank Requirements and Progression Path Published",
            Excerpt = "The updated pilot progression handbook is now available, including revised hour requirements for Captain and above, and new specialist tracks.",
            AuthorInitials = "PS",
            AuthorName = "Padhang Satrio",
        });

        NewsRows.Add(new NewsDisplayRow
        {
            ShowPhoto = Visibility.Collapsed,
            ShowGradient = Visibility.Visible,
            GradientBrush = BannerViolet,
            EmojiGlyph = "✨",
            TagDisplay = "Announcement".ToUpperInvariant(),
            Date = "Mar 1, 2026",
            Title = "AviatesAir Cargo Subsidiary Celebrates 1-Year Anniversary",
            Excerpt = "Our cargo division has successfully completed over 500 cargo flights in its first year. A milestone celebration event is planned for April.",
            AuthorInitials = "LS",
            AuthorName = "Leonardo Samsul",
        });
    }

    // wwwroot ships as loose Content next to the exe (BlazorHostServer.cs resolves it the same
    // way), not as a packaged ms-appx:// asset, so photos are loaded from an absolute file path --
    // same technique Dashboard's hero.webp, FleetPage's fleet-*.webp, and RoutesPage's flag
    // images already established. Never throws; returns null (falls back to no image) if the
    // file is missing.
    private static ImageSource? LoadNewsPhoto(string fileName)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "wwwroot", "images", fileName);
        return System.IO.File.Exists(path) ? new BitmapImage(new Uri(path)) : null;
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

    // ── "Read" link ──────────────────────────────────────────────────────────────────────────
    // News.razor's own "Read" link is `<a href="#" class="news-read-link">` on all 6 cards --
    // dead in the original, so this is a documented no-op, not a place to invent a destination.
    // Matches SupportPage.xaml.cs's OnFeatureButtonTapped precedent exactly.
    private void OnReadTapped(object sender, TappedRoutedEventArgs e)
    {
    }
}
