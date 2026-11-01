using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AviatesAirTracker.Pages;

/// <summary>
/// Native reproduction of AviatesAirTracker.LegacyWeb\Pages\Support.razor, since expanded with
/// real content from flyaviatesair.uk (Contact &amp; Community section, FAQ items 6-9, and the
/// GitHub Issues link). Still no ViewModel or service dependency -- the only state is the FAQ
/// accordion's currently-open index.
/// </summary>
public sealed partial class SupportPage : Page
{
    // Enter/Space on a focused Border-as-button runs its Tapped handler (see KeyboardActivation).
    private void OnActivateKey(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e) =>
        AviatesAirTracker.Platform.KeyboardActivation.Handle(this, sender, e);

    // Matches Support.razor's `private string? OpenFaq;` -- a single nullable field naming which
    // FAQ is open, not a per-row bool set. Keyed by index here (0-9, matching XAML declaration
    // order) rather than by question text, since the rows are fixed and named in XAML; the
    // toggle semantics are identical to ToggleFaq's own key-based logic.
    private int? _openFaqIndex;

    private RotateTransform[] _faqRotates = null!;
    private FrameworkElement[] _faqAnswers = null!;

    public SupportPage()
    {
        InitializeComponent();

        _faqRotates =
        [
            Faq0Rotate, Faq1Rotate, Faq2Rotate, Faq3Rotate, Faq4Rotate, Faq5Rotate,
            Faq6Rotate, Faq7Rotate, Faq8Rotate, Faq9Rotate,
        ];
        _faqAnswers =
        [
            Faq0Answer, Faq1Answer, Faq2Answer, Faq3Answer, Faq4Answer, Faq5Answer,
            Faq6Answer, Faq7Answer, Faq8Answer, Faq9Answer,
        ];
    }

    // Live Chat now opens the real chat dialog (same backend the website's /support live chat
    // widget uses). Knowledge Base still has no destination to link to, so it stays a no-op.
    private async void OnFeatureButtonTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || tag != "live-chat") return;

        var backend  = App.GetService<AviatesAirTracker.Core.Backend.AviatesBackendClient>();
        var settings = App.GetService<AviatesAirTracker.Services.SettingsService>();
        await AviatesAirTracker.Dialogs.LiveChatDialog.ShowAsync(XamlRoot, backend, settings);
    }

    private async void OnGitHubIssuesTapped(object sender, TappedRoutedEventArgs e)
    {
        await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/Z0MB1XCAT/AviatesAirTracker/issues"));
    }

    // Shared by both Contact & Community cards: Tag carries the destination URI directly (a
    // mailto: link for email, an https: invite link for Discord), same Launcher.LaunchUriAsync
    // pattern LoginPage.xaml.cs's OnRegisterTapped already uses.
    private async void OnContactTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string uri }) return;
        await Windows.System.Launcher.LaunchUriAsync(new Uri(uri));
    }

    // Matches ToggleFaq's exact logic: tapping the currently-open row closes it; tapping a
    // different row closes whichever one was open and opens the tapped one. Exactly one FAQ open
    // at a time.
    private void OnFaqTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tagText } || !int.TryParse(tagText, out var index))
        {
            return;
        }

        _openFaqIndex = _openFaqIndex == index ? null : index;

        for (var i = 0; i < _faqAnswers.Length; i++)
        {
            var isOpen = _openFaqIndex == i;
            _faqAnswers[i].Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            _faqRotates[i].Angle = isOpen ? 45 : 0;
        }
    }
}
