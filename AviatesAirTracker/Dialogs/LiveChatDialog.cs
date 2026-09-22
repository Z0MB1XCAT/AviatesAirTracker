using AviatesAirTracker.Core.Backend;
using AviatesAirTracker.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading.Tasks;
using Windows.System;

namespace AviatesAirTracker.Dialogs;

// ============================================================
// The desktop-app side of the same live chat the website's /support page has (backed by the
// worker's /api/support/chat* endpoints — see BACKEND-API/worker.js's "SUPPORT CHAT" section and
// AviatesAirWebsite's LiveChatWidget.tsx for the sibling implementation). Anonymous by design: no
// ACARS key is sent, only a visitorToken persisted in SettingsService.Settings so the pilot's
// conversation survives an app restart and (separately) shows up in the same admin Live Chat
// panel the website chat feeds. Follows BookingDialog's build-everything-in-C#-with-closures
// pattern rather than a XAML file, same reasoning: no <Style> element anywhere on this SDK.
// ============================================================

public static class LiveChatDialog
{
    private static readonly SolidColorBrush TextPrimaryBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9));
    private static readonly SolidColorBrush TextSecondaryBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1));
    private static readonly SolidColorBrush TextMutedBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x64, 0x74, 0x8B));
    private static readonly SolidColorBrush AccentBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x4D, 0x9B, 0xEC));
    private static readonly SolidColorBrush AccentLightBg = new(Windows.UI.Color.FromArgb(0x1F, 0x4D, 0x9B, 0xEC));
    private static readonly SolidColorBrush BorderBrush_ = new(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush SurfaceBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x0C, 0x11, 0x1D));
    private static readonly SolidColorBrush Surface2Brush = new(Windows.UI.Color.FromArgb(0xFF, 0x12, 0x1A, 0x2A));
    private static readonly SolidColorBrush GreenBrush = new(Windows.UI.Color.FromArgb(0xFF, 0x3E, 0xC7, 0xAC));
    private static readonly SolidColorBrush GreenLightBg = new(Windows.UI.Color.FromArgb(0x1A, 0x3E, 0xC7, 0xAC));
    private static readonly SolidColorBrush RedBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush RedLightBg = new(Windows.UI.Color.FromArgb(0x1A, 0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush RedBorder = new(Windows.UI.Color.FromArgb(0x33, 0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush WhiteBrush = new(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

    private const double PanelWidth = 380;
    private const double MessagesHeight = 340;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(4);

    public static async Task ShowAsync(XamlRoot xamlRoot, AviatesBackendClient backend, SettingsService settings)
    {
        var lastMessageId = 0;

        // ── Start panel: name + first message, shown until a chat exists ──────────────────────
        var nameBox = new TextBox
        {
            Text = settings.Settings.PilotName,
            PlaceholderText = "Your name",
            Width = PanelWidth,
        };
        var messageBox = new TextBox
        {
            PlaceholderText = "I'm having trouble with my ACARS key…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 90,
            Width = PanelWidth,
        };
        var startErrorText = new TextBlock { FontSize = 12.5, Foreground = RedBrush, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var startButtonText = new TextBlock { Text = "Start chat", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = WhiteBrush, HorizontalAlignment = HorizontalAlignment.Center };
        var startButton = new Border
        {
            Background = AccentBrush,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(0, 10, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = startButtonText,
        };

        var startPanel = new StackPanel
        {
            Spacing = 12,
            Width = PanelWidth,
            Children =
            {
                BuildLabel("Your name"), nameBox,
                BuildLabel("How can we help?"), messageBox,
                startErrorText,
                startButton,
            },
        };

        // ── Thread panel: message history + composer, shown once a chat exists ────────────────
        var statusDot = BuildStatusDot(GreenBrush);
        var statusText = new TextBlock { Text = "Live", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = GreenBrush };
        var statusPill = new Border
        {
            Background = GreenLightBg,
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(9, 4, 9, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { statusDot, statusText } },
        };

        var messagesPanel = new StackPanel { Spacing = 10, Padding = new Thickness(2, 8, 2, 8) };
        var messagesScroll = new ScrollViewer
        {
            Content = messagesPanel,
            Height = MessagesHeight,
            Width = PanelWidth,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var composerBox = new TextBox { PlaceholderText = "Type a message…", Width = PanelWidth - 76 };
        var sendButtonText = new TextBlock { Text = "Send", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = WhiteBrush };
        var sendButton = new Border
        {
            Background = AccentBrush,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = new StackPanel { Children = { sendButtonText }, VerticalAlignment = VerticalAlignment.Center },
        };
        var composerRow = new Grid { ColumnSpacing = 8, Children = { composerBox, sendButton } };
        composerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        composerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(composerBox, 0);
        Grid.SetColumn(sendButton, 1);

        var closedNote = new TextBlock
        {
            Text = "This conversation has been closed.",
            FontSize = 12.5,
            Foreground = TextMutedBrush,
            TextAlignment = TextAlignment.Center,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var newChatButtonText = new TextBlock { Text = "Start a new conversation", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = TextSecondaryBrush, HorizontalAlignment = HorizontalAlignment.Center };
        var newChatButton = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderBrush = BorderBrush_,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(0, 9, 0, 9),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
            Child = newChatButtonText,
        };

        var threadPanel = new StackPanel
        {
            Spacing = 10,
            Width = PanelWidth,
            Visibility = Visibility.Collapsed,
            Children = { statusPill, messagesScroll, composerRow, closedNote, newChatButton },
        };

        var root = new Grid { Children = { startPanel, threadPanel } };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Background = SurfaceBrush,
            Foreground = TextPrimaryBrush,
            BorderBrush = BorderBrush_,
            CornerRadius = new CornerRadius(16),
            Title = BuildHeader(),
            Content = root,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.None,
        };
        dialog.Opened += (_, _) => ThemeDialogButtons(dialog);

        void ShowThread()
        {
            startPanel.Visibility = Visibility.Collapsed;
            threadPanel.Visibility = Visibility.Visible;
        }

        void AppendMessage(SupportChatMessage m)
        {
            var mine = m.SenderType == "visitor";
            var bubble = new Border
            {
                Background = mine ? AccentLightBg : Surface2Brush,
                BorderBrush = mine ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x4D, 0x9B, 0xEC)) : BorderBrush_,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 9, 12, 9),
                MaxWidth = PanelWidth * 0.8,
                HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Child = new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock { Text = m.Content, FontSize = 13.5, Foreground = TextPrimaryBrush, TextWrapping = TextWrapping.Wrap },
                        new TextBlock
                        {
                            Text = $"{(mine ? "You" : "AviatesAir Support")} · {m.CreatedAt.ToLocalTime():t}",
                            FontSize = 10.5, Foreground = TextMutedBrush,
                        },
                    },
                },
            };
            messagesPanel.Children.Add(bubble);
            messagesScroll.UpdateLayout();
            messagesScroll.ChangeView(null, messagesScroll.ScrollableHeight, null);
        }

        void SetClosed(bool closed)
        {
            statusText.Text = closed ? "Closed" : "Live";
            statusText.Foreground = closed ? TextMutedBrush : GreenBrush;
            statusDot.Fill = closed ? TextMutedBrush : GreenBrush;
            statusPill.Background = closed ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)) : GreenLightBg;
            composerRow.Visibility = closed ? Visibility.Collapsed : Visibility.Visible;
            closedNote.Visibility = closed ? Visibility.Visible : Visibility.Collapsed;
            newChatButton.Visibility = closed ? Visibility.Visible : Visibility.Collapsed;
        }

        async Task RefreshAsync(bool initial)
        {
            if (settings.Settings.SupportChatId is not { } chatId || settings.Settings.SupportChatVisitorToken is not { } token) return;
            var poll = await backend.PollSupportChatMessagesAsync(chatId, token, initial ? null : lastMessageId);
            if (poll is null) return;

            SetClosed(poll.Status == "closed");
            if (poll.Messages.Count > 0)
            {
                foreach (var m in poll.Messages) AppendMessage(m);
                lastMessageId = poll.Messages[^1].Id;
            }
        }

        startButton.Tapped += async (_, _) =>
        {
            startErrorText.Visibility = Visibility.Collapsed;
            var name = nameBox.Text.Trim();
            var message = messageBox.Text.Trim();
            if (string.IsNullOrEmpty(name)) { ShowStartError(startErrorText, "Please tell us your name."); return; }
            if (string.IsNullOrEmpty(message)) { ShowStartError(startErrorText, "Write a quick message to get started."); return; }

            startButtonText.Text = "Starting…";
            startButton.IsHitTestVisible = false;
            var result = await backend.StartSupportChatAsync(name, message);
            startButtonText.Text = "Start chat";
            startButton.IsHitTestVisible = true;

            if (!result.Success)
            {
                ShowStartError(startErrorText, result.Error ?? "Could not start chat. Please try again.");
                return;
            }

            settings.Settings.SupportChatId = result.ChatId;
            settings.Settings.SupportChatVisitorToken = result.VisitorToken;
            settings.Save();

            ShowThread();
            SetClosed(false);
            // No optimistic bubble here — RefreshAsync below fetches the real message (with
            // its real id) from the server. Appending a local copy first meant the poll's
            // own copy showed up right next to it once it caught up.
            await RefreshAsync(initial: true);
        };

        sendButton.Tapped += async (_, _) => await SendAsync();
        composerBox.KeyDown += async (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) { e.Handled = true; await SendAsync(); }
        };

        async Task SendAsync()
        {
            if (settings.Settings.SupportChatId is not { } chatId || settings.Settings.SupportChatVisitorToken is not { } token) return;
            var content = composerBox.Text.Trim();
            if (string.IsNullOrEmpty(content)) return;

            composerBox.Text = "";

            var result = await backend.SendSupportChatMessageAsync(chatId, token, content);
            if (result.Success)
            {
                await RefreshAsync(initial: false);
            }
            else
            {
                composerBox.Text = content; // restore what they typed so it isn't lost
            }
        };

        newChatButton.Tapped += (_, _) =>
        {
            settings.Settings.SupportChatId = null;
            settings.Settings.SupportChatVisitorToken = null;
            settings.Save();
            lastMessageId = 0;
            messagesPanel.Children.Clear();
            nameBox.Text = settings.Settings.PilotName;
            messageBox.Text = "";
            startErrorText.Visibility = Visibility.Collapsed;
            threadPanel.Visibility = Visibility.Collapsed;
            startPanel.Visibility = Visibility.Visible;
        };

        // Resume an existing conversation immediately; otherwise the start form stays up front.
        if (settings.Settings.SupportChatId.HasValue)
        {
            ShowThread();
            await RefreshAsync(initial: true);
        }

        DispatcherTimer? pollTimer = null;
        dialog.Opened += (_, _) =>
        {
            pollTimer = new DispatcherTimer { Interval = PollInterval };
            pollTimer.Tick += async (_, _) => await RefreshAsync(initial: false);
            pollTimer.Start();
        };
        dialog.Closed += (_, _) => pollTimer?.Stop();

        await dialog.ShowAsync();
    }

    private static void ShowStartError(TextBlock errorText, string message)
    {
        errorText.Text = message;
        errorText.Visibility = Visibility.Visible;
    }

    private static TextBlock BuildLabel(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 11,
        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        CharacterSpacing = 40,
        Foreground = TextSecondaryBrush,
    };

    private static StackPanel BuildHeader() => new()
    {
        Spacing = 4,
        Children =
        {
            new TextBlock { Text = "AviatesAir Support", FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = TextPrimaryBrush },
            new TextBlock { Text = "We'll get right back to you", FontSize = 12.5, Foreground = TextMutedBrush },
        },
    };

    private static void ThemeDialogButtons(ContentDialog dialog)
    {
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

    private static Microsoft.UI.Xaml.Shapes.Ellipse BuildStatusDot(Brush fill) => new()
    {
        Width = 6, Height = 6, Fill = fill, VerticalAlignment = VerticalAlignment.Center,
    };
}
