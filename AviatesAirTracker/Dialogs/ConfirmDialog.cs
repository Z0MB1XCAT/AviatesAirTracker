using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Threading.Tasks;

namespace AviatesAirTracker.Dialogs;

// ============================================================
// Generic title/message/Confirm/Cancel ContentDialog -- the FIRST plain confirm-modal in this
// migration (checked: every existing Dialogs/*.cs file -- CreateEventDialog, BookingDialog,
// LandingResultDialog -- is a bespoke multi-field form builder, none is a simple yes/no confirm).
// Introduced for AcarsPage's End Flight confirmation and OFP-import wipe-warning (Task 4 of the
// ACARS native-page plan), kept intentionally minimal and reusable in shape so future pages needing
// a plain confirm dialog can call ShowAsync directly instead of hand-rolling another one.
//
// Same dark-theme dialog chrome + native-button recoloring fix as every other ContentDialog in this
// codebase (BookingDialog.cs/CreateEventDialog.cs): RequestedTheme/Background/Foreground only reach
// the dialog's own chrome, not the native Primary/Close Button instances, which default to stock
// Windows light-theme brushes since neither is a settable ContentDialog property -- recolored via a
// VisualTreeHelper walk once the template is applied (Opened).
// ============================================================

public static class ConfirmDialog
{
    private static readonly SolidColorBrush TextPrimaryBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9));
    private static readonly SolidColorBrush TextSecondaryBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1));
    private static readonly SolidColorBrush AccentBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x4D, 0x9B, 0xEC));
    private static readonly SolidColorBrush BorderBrush_ = new(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush SurfaceBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x0C, 0x11, 0x1D));

    /// <summary>Shows a simple title/message confirm dialog. Returns true only if the user tapped
    /// the primary (confirm) button -- false for Cancel/close/light-dismiss.</summary>
    public static async Task<bool> ShowAsync(
        XamlRoot xamlRoot,
        string title,
        string message,
        string primaryText = "Confirm",
        string cancelText = "Cancel")
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Background = SurfaceBrush,
            Foreground = TextPrimaryBrush,
            BorderBrush = BorderBrush_,
            CornerRadius = new CornerRadius(16),
            Title = new TextBlock
            {
                Text = title,
                FontSize = 17,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = TextPrimaryBrush,
            },
            Content = new TextBlock
            {
                Text = message,
                FontSize = 13,
                Foreground = TextSecondaryBrush,
                TextWrapping = TextWrapping.Wrap,
                Width = 340,
            },
            PrimaryButtonText = primaryText,
            CloseButtonText = cancelText,
            // Neither of this dialog's two current uses (End Flight, OFP-import trail wipe) should
            // fire on a stray Enter press -- both are consequential/hard-to-undo, matching
            // FlightsPage.xaml.cs's own ShowForceRemoveDialogAsync precedent for the same reasoning.
            DefaultButton = ContentDialogButton.Close,
        };

        dialog.Opened += (_, _) => ThemeDialogButtons(dialog);

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private static void ThemeDialogButtons(ContentDialog dialog)
    {
        if (FindDescendant<Button>(dialog, "PrimaryButton") is { } primary)
        {
            primary.Background = AccentBrush;
            primary.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
            primary.BorderBrush = AccentBrush;
        }
        if (FindDescendant<Button>(dialog, "CloseButton") is { } close)
        {
            close.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            close.Foreground = TextSecondaryBrush;
            close.BorderBrush = BorderBrush_;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && typed.Name == name) return typed;
            if (FindDescendant<T>(child, name) is { } found) return found;
        }
        return null;
    }
}
