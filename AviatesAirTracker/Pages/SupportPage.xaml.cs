using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AviatesAirTracker.Pages;

/// <summary>
/// Native reproduction of AviatesAirTracker.LegacyWeb\Pages\Support.razor. No ViewModel and no
/// service dependency -- the original's own @code block is just a nullable OpenFaq string and a
/// static Faqs dictionary, so this page's only state is the FAQ accordion's currently-open index.
/// </summary>
public sealed partial class SupportPage : Page
{
    // Matches Support.razor's `private string? OpenFaq;` -- a single nullable field naming which
    // FAQ is open, not a per-row bool set. Keyed by index here (0-5, matching XAML declaration
    // order) rather than by question text, since the six rows are fixed and named in XAML; the
    // toggle semantics are identical to ToggleFaq's own key-based logic.
    private int? _openFaqIndex;

    private RotateTransform[] _faqRotates = null!;
    private FrameworkElement[] _faqAnswers = null!;

    public SupportPage()
    {
        InitializeComponent();

        _faqRotates = [Faq0Rotate, Faq1Rotate, Faq2Rotate, Faq3Rotate, Faq4Rotate, Faq5Rotate];
        _faqAnswers = [Faq0Answer, Faq1Answer, Faq2Answer, Faq3Answer, Faq4Answer, Faq5Answer];
    }

    // Support.razor's three feature-card buttons (Open Chat / Browse Docs / Open GitHub) have no
    // @onclick handler at all in the original -- they are purely decorative today. This handler
    // is a documented no-op so the Border still reads as a real interactive control in the native
    // tree; it deliberately does not open a URL, a chat widget, or navigate anywhere, since none
    // of those destinations exist in the source being ported.
    private void OnFeatureButtonTapped(object sender, TappedRoutedEventArgs e)
    {
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
