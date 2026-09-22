using AviatesAirTracker.Core.Backend;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;

namespace AviatesAirTracker.Pages;

public sealed partial class NewsDetailPage : Page
{
    public NewsDetailPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is not RemoteNewsArticle article) return;

        CategoryText.Text = (article.Category ?? "News").ToUpperInvariant();
        DateText.Text = article.Date;
        TitleText.Text = article.Title;
        SummaryText.Text = article.Summary;
        SummaryText.Visibility = string.IsNullOrWhiteSpace(article.Summary) ? Visibility.Collapsed : Visibility.Visible;

        var heroPath = !string.IsNullOrWhiteSpace(article.HeroImage) ? article.HeroImage : article.Thumbnail;
        if (!string.IsNullOrWhiteSpace(heroPath))
        {
            try
            {
                HeroImage.Source = new BitmapImage(new Uri(NewsPage.ResolveImageUrl(heroPath)));
                HeroImage.Visibility = Visibility.Visible;
                HeroGradientBorder.Visibility = Visibility.Collapsed;
            }
            catch
            {
                ShowFallbackGradient(article.Id);
            }
        }
        else
        {
            ShowFallbackGradient(article.Id);
        }

        BuildContentBlocks(article.Content);
    }

    private void ShowFallbackGradient(string articleId)
    {
        HeroImage.Visibility = Visibility.Collapsed;
        HeroGradientBorder.Visibility = Visibility.Visible;
        var index = Math.Abs(articleId.GetHashCode()) % NewsPage.FallbackGradients.Length;
        HeroGradientBorder.Background = NewsPage.FallbackGradients[index];
    }

    // Small purpose-built splitter, not a real Markdown engine -- see the page's own XAML doc
    // comment for why. Blocks are separated by a blank line; a block starting with "#" (one or
    // more, Markdown-style) renders as a bold heading, everything else as a wrapped body
    // paragraph with the Frontend Design Rules' generous 1.7 line-height on body text.
    private void BuildContentBlocks(string content)
    {
        ContentPanel.Children.Clear();

        if (string.IsNullOrWhiteSpace(content)) return;

        var blocks = content.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        foreach (var raw in blocks)
        {
            var block = raw.Trim();
            if (block.Length == 0) continue;

            var headingLevel = 0;
            while (headingLevel < block.Length && block[headingLevel] == '#') headingLevel++;

            if (headingLevel is > 0 and <= 6 && headingLevel < block.Length && block[headingLevel] == ' ')
            {
                var text = block[(headingLevel + 1)..].Trim();
                ContentPanel.Children.Add(new TextBlock
                {
                    Text = text,
                    FontFamily = (FontFamily)Application.Current.Resources["DisplayFontBold"],
                    FontSize = headingLevel == 1 ? 22 : 18,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0),
                });
            }
            else
            {
                ContentPanel.Children.Add(new TextBlock
                {
                    Text = block,
                    FontSize = 14.5,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1)),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 24.5, // ~1.7x FontSize, matching the Frontend Design Rules' body line-height
                });
            }
        }
    }
}
