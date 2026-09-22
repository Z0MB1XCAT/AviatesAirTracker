using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI;

namespace AviatesAirTracker.Dialogs;

// ============================================================
// FLIGHT REVIEW DIALOG
//
// Shown when FlightSessionManager.FlightHeldForReview fires -- a completed flight tripped one
// or more of the app's own compliance checks (hard landing, unstabilized approach, off-
// centerline/touchdown-zone, excessive climb/descent rate, dangerous cruise speed, or a late
// departure; see LandingComplianceChecker/FlightComplianceMonitor). Lists every reason so the
// pilot isn't left guessing, then either:
//   - "Submit for Review": requires a written explanation, logs it to Supabase via
//     AviatesBackendClient.SubmitFlightReviewAsync.
//   - "Open Discord Support": launches the community Discord (same invite SupportPage.xaml's
//     own Contact card uses) without closing the dialog, so a pilot who'd rather discuss it
//     there can still come back and submit afterward.
//   - "Dismiss" (or closing without submitting any other way): still logs a review row, via the
//     dialog's own Closed event -- explicitly requested so nothing is silently lost. The
//     pilot_reason records whether Discord was opened first or the dialog was just dismissed,
//     so a reviewer can tell "gone to explain it in Discord" apart from "ignored".
//
// Same dark-theme dialog chrome + native-button recoloring fix as ConfirmDialog.cs: the
// Primary/Close/Secondary Button instances default to stock Windows light-theme brushes since
// none of those are settable ContentDialog properties.
// ============================================================

public static class FlightReviewDialog
{
    private const string DiscordSupportUrl = "https://discord.gg/WjBubsD8E9";

    private static readonly SolidColorBrush TextPrimaryBrush = new(Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9));
    private static readonly SolidColorBrush TextSecondaryBrush = new(Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1));
    private static readonly SolidColorBrush TextMutedBrush = new(Color.FromArgb(0xFF, 0x64, 0x74, 0x8B));
    private static readonly SolidColorBrush AccentBrush = new(Color.FromArgb(0xFF, 0x4D, 0x9B, 0xEC));
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush BorderBrush_ = new(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush SurfaceBrush = new(Color.FromArgb(0xFF, 0x0C, 0x11, 0x1D));
    private static readonly SolidColorBrush ViolationBg = new(Color.FromArgb(0x14, 0xEF, 0x44, 0x44));

    public static async Task ShowAsync(
        XamlRoot xamlRoot, FlightRecord flight, AviatesBackendClient backend, string acarsKey)
    {
        var violations = flight.FlightComplianceViolations
            .Concat(flight.AllLandings.SelectMany(l => l.ComplianceViolations))
            .Distinct()
            .ToList();

        if (violations.Count == 0) return;

        var intro = new TextBlock
        {
            Text = "This flight didn't meet the app's automatic acceptance criteria. It's already been recorded — this just explains why, and lets you add context.",
            FontSize = 13, Foreground = TextSecondaryBrush, TextWrapping = TextWrapping.Wrap,
        };

        var violationsPanel = new StackPanel { Spacing = 6 };
        foreach (var v in violations)
        {
            violationsPanel.Children.Add(new Border
            {
                Background = ViolationBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8),
                Child = new TextBlock
                {
                    Text = v, FontSize = 12.5, Foreground = TextPrimaryBrush, TextWrapping = TextWrapping.Wrap,
                },
            });
        }

        var reasonLabel = new TextBlock
        {
            Text = "EXPLAIN WHAT HAPPENED", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = TextSecondaryBrush,
        };
        var reasonBox = new TextBox
        {
            PlaceholderText = "e.g. simulated an engine failure, weather forced a go-around, autopilot malfunction...",
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 84,
        };
        var errorText = new TextBlock
        {
            Text = "A reason is required to submit, or use Open Discord Support instead.",
            FontSize = 11.5, Foreground = ErrorBrush, Visibility = Visibility.Collapsed,
        };
        var helperText = new TextBlock
        {
            Text = "Prefer to talk it through instead? Open Discord Support below — you can still come back and submit a reason afterward.",
            FontSize = 11, Foreground = TextMutedBrush, TextWrapping = TextWrapping.Wrap,
        };

        var panel = new StackPanel
        {
            Spacing = 12, Width = 400,
            Children = { intro, violationsPanel, reasonLabel, reasonBox, errorText, helperText },
        };

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
                Text = "Flight Held for Review", FontSize = 17,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = TextPrimaryBrush,
            },
            Content = panel,
            PrimaryButtonText = "Submit for Review",
            SecondaryButtonText = "Open Discord Support",
            CloseButtonText = "Dismiss",
            DefaultButton = ContentDialogButton.Close,
        };

        dialog.Opened += (_, _) => ThemeDialogButtons(dialog);

        var isSubmitting = false;
        var alreadySubmitted = false;
        var discordOpened = false;

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (isSubmitting) { args.Cancel = true; return; }

            if (string.IsNullOrWhiteSpace(reasonBox.Text))
            {
                errorText.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            var deferral = args.GetDeferral();
            try
            {
                isSubmitting = true;
                await backend.SubmitFlightReviewAsync(
                    acarsKey,
                    flight.Id.ToString(),
                    flight.PrimaryLanding?.Id.ToString(),
                    violations,
                    reasonBox.Text.Trim());
                alreadySubmitted = true;
            }
            finally
            {
                isSubmitting = false;
                deferral.Complete();
            }
        };

        dialog.SecondaryButtonClick += async (_, args) =>
        {
            // Keep the dialog open -- opening Discord isn't "finishing" the interaction, the
            // pilot may still want to come back and submit a written reason too.
            args.Cancel = true;
            discordOpened = true;
            await Windows.System.Launcher.LaunchUriAsync(new System.Uri(DiscordSupportUrl));
        };

        // Fires no matter how the dialog closed (Dismiss, a stray Escape/light-dismiss, or the
        // window closing under it) -- if the pilot never actually submitted a written reason via
        // Primary above, still log the review rather than losing it: explicitly requested so a
        // dismissed flight is recorded as "gone to Discord" or "dismissed/ignored" instead of
        // vanishing with no trace.
        dialog.Closed += async (_, _) =>
        {
            if (alreadySubmitted) return;
            await backend.SubmitFlightReviewAsync(
                acarsKey,
                flight.Id.ToString(),
                flight.PrimaryLanding?.Id.ToString(),
                violations,
                discordOpened
                    ? "Pilot opened Discord Support to discuss; no written reason submitted."
                    : "Dialog dismissed without a reason.");
        };

        await dialog.ShowAsync();
    }

    private static void ThemeDialogButtons(ContentDialog dialog)
    {
        if (FindDescendant<Button>(dialog, "PrimaryButton") is { } primary)
        {
            primary.Background = AccentBrush;
            primary.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
            primary.BorderBrush = AccentBrush;
        }
        if (FindDescendant<Button>(dialog, "SecondaryButton") is { } secondary)
        {
            secondary.Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            secondary.Foreground = TextSecondaryBrush;
            secondary.BorderBrush = BorderBrush_;
        }
        if (FindDescendant<Button>(dialog, "CloseButton") is { } close)
        {
            close.Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            close.Foreground = TextSecondaryBrush;
            close.BorderBrush = BorderBrush_;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T typed && typed.Name == name) return typed;
            if (FindDescendant<T>(child, name) is { } found) return found;
        }
        return null;
    }
}
