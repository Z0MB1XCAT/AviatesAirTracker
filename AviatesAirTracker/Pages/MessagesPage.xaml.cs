using AviatesAirTracker.Models;
using AviatesAirTracker.Services;
using AviatesAirTracker.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace AviatesAirTracker.Pages;

/// <summary>
/// Pre-formatted display row for one friend-list entry. Plain mutable class with
/// { get; set; } properties, not a record (CS8852 risk, same as every prior display-row class in
/// this migration). RowBackground is recomputed and the whole collection rebuilt (Clear + re-Add)
/// on every RebuildFriendList() call, so the active/inactive highlight always reflects the
/// ViewModel's current SelectedFriendId without needing a value converter.
/// </summary>
public sealed class FriendDisplayRow
{
    public string PilotId { get; set; } = "";
    public string Initials { get; set; } = "";
    public string Name { get; set; } = "";
    public string Rank { get; set; } = "";
    public Visibility ShowUnreadDot { get; set; } = Visibility.Collapsed;
    public Brush RowBackground { get; set; } = new SolidColorBrush(Color.FromArgb(0x00, 0x00, 0x00, 0x00));
}

public sealed partial class MessagesPage : Page
{
    // Dark-theme hex values read directly from wwwroot/css/app.css's html[data-theme="dark"]
    // block, the same values every prior native page in this migration hardcodes.
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x4D, 0x9B, 0xEC));
    private static readonly Brush AccentLightBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0x4D, 0x9B, 0xEC));
    private static readonly Brush Surface2Brush = new SolidColorBrush(Color.FromArgb(0xFF, 0x12, 0x1A, 0x2A));
    private static readonly Brush BorderBrushColor = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly Brush TextPrimaryBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9));
    private static readonly Brush TextMutedBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x64, 0x74, 0x8B));
    private static readonly Brush WhiteBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    private static readonly Brush WhiteMutedBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly Brush TransparentBrush = new SolidColorBrush(Color.FromArgb(0x00, 0x00, 0x00, 0x00));
    private static readonly Brush TabInactiveForeground = TextMutedBrush;

    private readonly MessagesViewModel _viewModel;
    private readonly SettingsService _settings;

    private readonly ObservableCollection<FriendDisplayRow> _friendRows = [];

    // Lives on the page, not the ViewModel -- matches Messages.razor's own _activeTab/
    // _dmSendError field placement (the design spec's "page owns its own UI-local state"
    // decision, same as every prior page's _activeTab/_statusMessage).
    private string _activeTab = "inbox";
    private string? _dmSendError;
    private bool _isSending;

    // Tracks which message ids have already been rendered (and animated) in the bubble list, so
    // a rebuild triggered by switching tabs and back, or by a refresh that doesn't add new
    // messages, does not re-animate bubbles that are already on screen. Cleared whenever the
    // selected conversation changes (a fresh conversation has nothing "already seen" yet).
    private readonly HashSet<Guid> _renderedBubbleIds = [];

    public MessagesPage()
    {
        InitializeComponent();

        _viewModel = App.GetService<MessagesViewModel>();
        _settings = App.GetService<SettingsService>();
        DataContext = _viewModel;

        FriendListItems.ItemsSource = _friendRows;

        UpdateTabVisuals();

        Loaded += async (_, _) =>
        {
            await _viewModel.RefreshAllAsync();
            RebuildUi();
        };
    }

    // ── Top-level rebuild ────────────────────────────────────────────────────────────────────

    private void RebuildUi()
    {
        if (!_viewModel.HasAcarsKey)
        {
            AcarsGatePanel.Visibility = Visibility.Visible;
            MainContentGrid.Visibility = Visibility.Collapsed;
            return;
        }

        AcarsGatePanel.Visibility = Visibility.Collapsed;
        MainContentGrid.Visibility = Visibility.Visible;

        UpdateTabBadges();
        ShowActiveTabPanel();
        RebuildFriendList();
        RebuildConversation();
    }

    // ── ACARS gate ───────────────────────────────────────────────────────────────────────────

    // TODO: wire to native SettingsPage once it exists. The native Settings page hasn't been
    // converted yet (it's the largest remaining legacy page, a separate future conversion per
    // the design spec's explicit scoping) -- this button stays visually present but is a
    // deliberate no-op for now, rather than silently dropped or backed by a placeholder page.
    private void OnGoToSettingsTapped(object sender, TappedRoutedEventArgs e)
    {
    }

    // ── Tab bar ──────────────────────────────────────────────────────────────────────────────

    private void OnTabTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Border { Tag: string tab } || tab == _activeTab) return;

        _activeTab = tab;
        // Matches Messages.razor's SwitchTab: clears page-local status/error text on every switch.
        _dmSendError = null;

        UpdateTabVisuals();
        ShowActiveTabPanel();
        UpdateDmSendError();
    }

    private void UpdateTabVisuals()
    {
        ApplyTabVisual(TabInboxBorder, TabInboxText, "inbox");
        ApplyTabVisual(TabBroadcastBorder, TabBroadcastText, "broadcast");
        ApplyTabVisual(TabFriendsBorder, TabFriendsText, "friends");
    }

    private void ApplyTabVisual(Border border, TextBlock text, string tab)
    {
        var isActive = _activeTab == tab;
        border.Background = isActive ? AccentBrush : TransparentBrush;
        text.Foreground = isActive ? WhiteBrush : TabInactiveForeground;
        text.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Medium;
    }

    private void UpdateTabBadges()
    {
        TabInboxBadgeBorder.Visibility = _viewModel.UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        TabInboxBadgeText.Text = _viewModel.UnreadCount.ToString();

        TabFriendsBadgeBorder.Visibility = _viewModel.Friends.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TabFriendsBadgeText.Text = _viewModel.Friends.Count.ToString();
    }

    private void ShowActiveTabPanel()
    {
        InboxPanel.Visibility = _activeTab == "inbox" ? Visibility.Visible : Visibility.Collapsed;
        BroadcastPanel.Visibility = _activeTab == "broadcast" ? Visibility.Visible : Visibility.Collapsed;
        FriendsPanel.Visibility = _activeTab == "friends" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Friend list ──────────────────────────────────────────────────────────────────────────

    private void RebuildFriendList()
    {
        _friendRows.Clear();

        if (_viewModel.Friends.Count == 0)
        {
            FriendListEmptyPanel.Visibility = Visibility.Visible;
            FriendListScroll.Visibility = Visibility.Collapsed;
            return;
        }

        FriendListEmptyPanel.Visibility = Visibility.Collapsed;
        FriendListScroll.Visibility = Visibility.Visible;

        var myId = _viewModel.MyPilotId;
        foreach (var friend in _viewModel.Friends)
        {
            var unreadFromFriend = _viewModel.InboxMessages.Count(m =>
                m.SenderId == friend.PilotId && m.RecipientId == myId && m.ReadAt == null);
            var isActive = _viewModel.SelectedFriendId == friend.PilotId;

            _friendRows.Add(new FriendDisplayRow
            {
                PilotId = friend.PilotId,
                Initials = GetInitials(friend.PilotName),
                Name = friend.PilotName,
                Rank = friend.Rank,
                ShowUnreadDot = unreadFromFriend > 0 ? Visibility.Visible : Visibility.Collapsed,
                RowBackground = isActive ? AccentLightBrush : TransparentBrush,
            });
        }
    }

    private async void OnFriendTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: FriendDisplayRow row }) return;

        await _viewModel.SelectFriendAsync(row.PilotId);
        _renderedBubbleIds.Clear(); // a freshly-opened conversation has nothing "already seen" yet
        RebuildFriendList();
        RebuildConversation();
    }

    // ── Conversation panel ───────────────────────────────────────────────────────────────────

    private void RebuildConversation()
    {
        if (_viewModel.SelectedFriendId == null)
        {
            ConversationEmptyPanel.Visibility = Visibility.Visible;
            ConversationContentGrid.Visibility = Visibility.Collapsed;
            return;
        }

        ConversationEmptyPanel.Visibility = Visibility.Collapsed;
        ConversationContentGrid.Visibility = Visibility.Visible;

        var friend = _viewModel.Friends.FirstOrDefault(f => f.PilotId == _viewModel.SelectedFriendId);
        ConvAvatarText.Text = GetInitials(friend?.PilotName ?? "?");
        ConvNameText.Text = friend?.PilotName ?? "Pilot";
        ConvRankText.Text = friend?.Rank ?? "";

        RebuildBubbleList(BubbleListPanel, _viewModel.Conversation, isBroadcast: false, "No messages yet — say hello!");
        UpdateDmSendError();
        UpdateComposeState();
    }

    private async void OnRemoveFriendTapped(object sender, TappedRoutedEventArgs e)
    {
        var pilotId = _viewModel.SelectedFriendId;
        if (pilotId == null) return;

        await _viewModel.RemoveFriendAsync(pilotId);
        // The ViewModel already clears SelectedFriendId/Conversation itself when the removed
        // friend was the one currently selected -- just rebuild and the empty state shows.
        RebuildFriendList();
        RebuildConversation();
    }

    // ── Compose / send ───────────────────────────────────────────────────────────────────────

    private void OnComposeTextChanged(object sender, TextChangedEventArgs e) => UpdateComposeState();

    private void OnComposeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;

        var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        var isShiftDown = (shiftState & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        if (isShiftDown) return; // matches OnComposeKeyDown's `if (e.Key == "Enter" && !e.ShiftKey)`

        e.Handled = true;
        _ = SendDirectAsync();
    }

    private void OnSendDirectTapped(object sender, TappedRoutedEventArgs e) => _ = SendDirectAsync();

    private async Task SendDirectAsync()
    {
        var text = ComposeTextBox.Text;
        if (string.IsNullOrWhiteSpace(text) || _viewModel.SelectedFriendId == null || _isSending) return;

        // Optimistic clear, matching SendDirect()'s exact sequencing: grab the text, clear the
        // box, show the busy/disabled state, then send.
        ComposeTextBox.Text = "";
        _isSending = true;
        _dmSendError = null;
        UpdateComposeState();
        UpdateDmSendError();

        var (ok, error) = await _viewModel.SendDirectAsync(_viewModel.SelectedFriendId, text);
        if (!ok)
        {
            ComposeTextBox.Text = text; // restore so the pilot can edit and retry
            _dmSendError = error;
        }

        _isSending = false;

        // SendDirectAsync already refreshed InboxMessages and rebuilt Conversation internally
        // (Task 1's fix) -- this just re-renders from the already-updated collections.
        RebuildFriendList();
        RebuildConversation();
    }

    private void UpdateComposeState()
    {
        var hasText = !string.IsNullOrWhiteSpace(ComposeTextBox.Text);
        var enabled = hasText && !_isSending;

        SendButtonBorder.IsHitTestVisible = enabled;
        SendButtonBorder.Opacity = enabled ? 1.0 : 0.45; // matches .msg-send-btn:disabled { opacity: 0.45 }

        SendIconViewbox.Visibility = _isSending ? Visibility.Collapsed : Visibility.Visible;
        SendProgressRing.Visibility = _isSending ? Visibility.Visible : Visibility.Collapsed;
        SendProgressRing.IsActive = _isSending;
    }

    private void UpdateDmSendError()
    {
        DmSendErrorBorder.Visibility = string.IsNullOrEmpty(_dmSendError) ? Visibility.Collapsed : Visibility.Visible;
        DmSendErrorText.Text = _dmSendError ?? "";
    }

    // ── Bubble list (shared helper -- Task 3's Broadcast tab reuses this rather than
    // duplicating it, passing isBroadcast: true so non-mine bubbles get a sender-name label). ──

    private void RebuildBubbleList(Panel host, IEnumerable<PilotMessage> messages, bool isBroadcast, string emptyText)
    {
        host.Children.Clear();

        var list = messages.ToList();
        if (list.Count == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = emptyText,
                FontSize = 13.5,
                Foreground = TextMutedBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 24, 0, 0),
            });
            return;
        }

        var myId = _viewModel.MyPilotId;
        foreach (var msg in list)
        {
            var isMine = msg.SenderId == myId;
            host.Children.Add(BuildBubble(msg, isMine, isBroadcast));
        }
    }

    private FrameworkElement BuildBubble(PilotMessage msg, bool isMine, bool isBroadcast)
    {
        var wrap = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = isMine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Spacing = 3,
        };

        if (isBroadcast && !isMine)
        {
            wrap.Children.Add(new TextBlock
            {
                Text = msg.SenderName,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextMutedBrush,
                Margin = new Thickness(8, 0, 8, 0),
            });
        }

        // Bubble-in animation transform: translateY(10px) scale(0.96) -> identity, matching the
        // CSS bubble-in keyframe's feel. Set up on every bubble (not just new ones) so the
        // RenderTransform/RenderTransformOrigin plumbing is uniform; already-seen bubbles just
        // start at the identity values below instead of running the Storyboard.
        var scale = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
        var translate = new TranslateTransform { Y = 0 };
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(scale);
        transformGroup.Children.Add(translate);

        var bubble = new Border
        {
            MaxWidth = 420,
            Padding = new Thickness(14, 10, 14, 10),
            CornerRadius = isMine ? new CornerRadius(20, 20, 6, 20) : new CornerRadius(20, 20, 20, 6),
            Background = isMine ? AccentBrush : Surface2Brush,
            BorderBrush = isMine ? null : BorderBrushColor,
            BorderThickness = isMine ? new Thickness(0) : new Thickness(1),
            RenderTransform = transformGroup,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };

        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(new TextBlock
        {
            Text = msg.Content,
            FontSize = 13.5,
            Foreground = isMine ? WhiteBrush : TextPrimaryBrush,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = FormatTime(msg.SentAt),
            FontSize = 10.5,
            Foreground = isMine ? WhiteMutedBrush : TextMutedBrush,
        });
        bubble.Child = content;

        wrap.Children.Add(bubble);

        var isNew = !_renderedBubbleIds.Contains(msg.Id);
        if (isNew)
        {
            _renderedBubbleIds.Add(msg.Id);
            AnimateBubbleIn(bubble, translate, scale);
        }
        else
        {
            bubble.Opacity = 1;
        }

        return wrap;
    }

    // Reproduces the CSS bubble-in keyframe (opacity 0->1, translateY(10px) scale(0.96) ->
    // identity, 0.32s spring easing -- app.css's `--ease-spring: cubic-bezier(0.34, 1.56, 0.64, 1)`,
    // a curve with the same "slight overshoot" character as BackEase) as a WinUI Storyboard.
    // Implementer's choice: BackEase (moderate Amplitude) on the transform animations for the
    // spring-like overshoot, CubicEase (EaseOut) on opacity since an overshooting opacity would
    // look wrong (values outside [0,1] are meaningless for Opacity).
    private static void AnimateBubbleIn(Border bubble, TranslateTransform translate, ScaleTransform scale)
    {
        bubble.Opacity = 0;
        translate.Y = 10;
        scale.ScaleX = 0.96;
        scale.ScaleY = 0.96;

        var duration = new Duration(TimeSpan.FromSeconds(0.32));
        var springEase = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut };

        var opacityAnim = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(opacityAnim, bubble);
        Storyboard.SetTargetProperty(opacityAnim, "Opacity");

        var translateAnim = new DoubleAnimation { From = 10, To = 0, Duration = duration, EasingFunction = springEase };
        Storyboard.SetTarget(translateAnim, translate);
        Storyboard.SetTargetProperty(translateAnim, "Y");

        var scaleXAnim = new DoubleAnimation { From = 0.96, To = 1, Duration = duration, EasingFunction = springEase };
        Storyboard.SetTarget(scaleXAnim, scale);
        Storyboard.SetTargetProperty(scaleXAnim, "ScaleX");

        var scaleYAnim = new DoubleAnimation { From = 0.96, To = 1, Duration = duration, EasingFunction = springEase };
        Storyboard.SetTarget(scaleYAnim, scale);
        Storyboard.SetTargetProperty(scaleYAnim, "ScaleY");

        var storyboard = new Storyboard();
        storyboard.Children.Add(opacityAnim);
        storyboard.Children.Add(translateAnim);
        storyboard.Children.Add(scaleXAnim);
        storyboard.Children.Add(scaleYAnim);
        storyboard.Begin();
    }

    // ── Formatting helpers (ported verbatim from Messages.razor's @code block) ──────────────────

    private static string GetInitials(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        return string.Concat(
                name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Take(2)
                    .Select(w => w[0]))
            .ToUpper();
    }

    private static string FormatTime(DateTime dt)
    {
        // Treat Unspecified kind as UTC (backend may omit timezone suffix).
        var utc = dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt;
        var diff = DateTime.UtcNow - utc;
        if (diff.TotalSeconds < 0) diff = TimeSpan.Zero; // guard against clock skew / future timestamps
        if (diff.TotalSeconds < 30) return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        return utc.ToLocalTime().ToString("MMM d, HH:mm");
    }
}
